using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MultiViewExperiment
{
    internal struct Region
    {
        public string kind;
        public float x, y, ortho, aspect;

        public float HalfW => ortho * aspect;
        public float HalfH => ortho;
        public Rect WorldRect => new Rect(x - HalfW, y - HalfH, 2f * HalfW, 2f * HalfH);

        public RectInt Cells
        {
            get
            {
                int x0 = Mathf.FloorToInt(x - HalfW), y0 = Mathf.FloorToInt(y - HalfH);
                int x1 = Mathf.CeilToInt(x + HalfW), y1 = Mathf.CeilToInt(y + HalfH);
                return new RectInt(x0, y0, x1 - x0, y1 - y0);
            }
        }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture,
                "{0}@({1:F1},{2:F1}) ortho={3:F2} cells=[{4:F0}..{5:F0}]x[{6:F0}..{7:F0}]",
                kind, x, y, ortho, x - HalfW, x + HalfW, y - HalfH, y + HalfH);
    }

    internal sealed class Shot
    {
        public string name;
        public Region region;
        public byte[] rgba; // RGBA32, bottom row first (Unity order)
    }

    /// <summary>
    /// Iteration 2. A second, independent camera looks at a region outside the main view (default: the
    /// Printing Pod). That region is registered with every culling system (MultiView.cs) for as long as
    /// the experiment runs. Each set saves:
    ///   screen        what the player sees
    ///   ctrl_a/b      extra camera at the main camera's own position (control)
    ///   off_a/b       extra camera on the extra region, union patches on (the thing under test)
    ///   off_2pass     same, liquids drawn in a second pass over the extra camera's own MRT copy
    ///   off_nounion   same region with the union patches off for two frames (iteration-1 behaviour)
    ///   off_L_*       layer-group breakdown (first sets only)
    ///   truth         main camera snapped onto the extra region for one frame, screenshot (full pipeline)
    /// </summary>
    public sealed class ExperimentController : MonoBehaviour
    {
        // Read/written by the Harmony patches.
        public static bool ForceFullWorld;
        public static int GroundRenderAllCalls;
        public static long LastWorldLateUpdateTicks;
        public static long LastBlockTileLateUpdateTicks;

        private static readonly Color ClearColor = new Color(1f, 0f, 1f, 1f); // magenta = "nothing drawn here"

        private ExperimentConfig cfg;
        private Camera mrtCamera;
        private Camera cam;
        private RenderTexture rt, mrtA, mrtB, copyA, copyB;
        private Texture2D readTex;
        private int width, height;
        private int baseMask, waterBit, defaultBit, worldBit;
        private string sessionDir;
        private bool ready;
        private bool freezeOff;   // keep the registered region fixed during a capture set
        private Region off;
        private Task pending = Task.CompletedTask;
        private readonly ConcurrentQueue<string> logQueue = new ConcurrentQueue<string>();
        private readonly object fileLock = new object();

        private float Aspect => width / (float)height;

        private void Start()
        {
            cfg = MultiViewMod.Config;
            MultiViewRegistry.Enabled = cfg.union;
            MultiViewRegistry.Suspended = false;
            MultiViewRegistry.Regions.Clear();
            StartCoroutine(Run());
        }

        private void Update()
        {
            while (logQueue.TryDequeue(out string line))
                Log.Info(line);

            if (!ready || freezeOff || CameraController.Instance == null)
                return;
            try
            {
                off = PickOffRegion(MainRegion());
                MultiViewRegistry.Regions.Clear();
                MultiViewRegistry.Regions.Add(off.Cells);
            }
            catch (Exception e)
            {
                Log.Error("region update failed, disabling union: " + e);
                MultiViewRegistry.Regions.Clear();
                ready = false;
            }
        }

        private void OnDestroy()
        {
            ForceFullWorld = false;
            MultiViewRegistry.Regions.Clear();
            MultiViewRegistry.Suspended = false;
            MultiViewRegistry.GridAreaOverride = null;
            if (cam != null) Destroy(cam.gameObject);
            foreach (var t in new[] { rt, mrtA, mrtB, copyA, copyB })
                if (t != null) { t.Release(); Destroy(t); }
            if (readTex != null) Destroy(readTex);
            while (logQueue.TryDequeue(out string line))
                Log.Info(line);
        }

        private IEnumerator Run()
        {
            float waitStart = Time.realtimeSinceStartup;
            while (!IsReady())
            {
                if (Time.realtimeSinceStartup - waitStart > 120f)
                {
                    Log.Warn("gave up waiting for the game / MRT camera; experiment not started");
                    yield break;
                }
                yield return null;
            }
            if (!TrySetup())
                yield break;
            ready = true; // from here on Update() keeps the extra region registered

            yield return new WaitForSecondsRealtime(cfg.startDelaySeconds);
            Perf.ReadAndReset();

            for (int i = 0; i < cfg.maxSets && ready; i++)
            {
                freezeOff = true;
                yield return CaptureSet(i);
                freezeOff = false;
                yield return new WaitForSecondsRealtime(cfg.intervalSeconds);
            }
            Log.Info($"finished {cfg.maxSets} sets; output in '{sessionDir}'. Extra region stays registered until you leave the save.");
        }

        private bool IsReady()
        {
            if (Game.Instance == null || Game.Instance.IsLoading() || CameraController.Instance == null)
                return false;
            if (Singleton<KBatchedAnimUpdater>.Instance == null || ClusterManager.Instance == null || PropertyTextures.instance == null)
                return false;
            string mrtName = CameraController.Instance.baseCamera.name + " MRT";
            foreach (var c in Camera.allCameras)
            {
                if (c.name == mrtName) { mrtCamera = c; return true; }
            }
            return false;
        }

        private bool TrySetup()
        {
            try
            {
                width = Mathf.Max(64, cfg.captureWidth);
                height = Mathf.Max(64, Mathf.RoundToInt(width * Screen.height / (float)Screen.width));

                string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                sessionDir = Path.Combine(Path.Combine(MultiViewMod.ModPath, "captures"), "session_" + stamp);
                Directory.CreateDirectory(sessionDir);

                string dump = CameraDump.Build(mrtCamera);
                File.WriteAllText(Path.Combine(sessionDir, "cameras.txt"), dump);
                File.WriteAllText(Path.Combine(sessionDir, "config.json"), JsonUtility.ToJson(cfg, true));
                File.WriteAllText(Path.Combine(sessionDir, "shots.csv"),
                    "set,shot,kind,x,y,ortho,meanLuma,blackFrac,magentaFrac,dominantFrac,magentaLeft,magentaRight,file\n");
                File.WriteAllText(Path.Combine(sessionDir, "pairs.csv"), "set,pair,a,b,diffFrac,meanAbsDiff\n");

                waterBit = 1 << LayerMask.NameToLayer("Water");
                defaultBit = 1 << LayerMask.NameToLayer("Default");
                worldBit = 1 << LayerMask.NameToLayer("World");
                baseMask = mrtCamera.cullingMask | waterBit;

                var go = new GameObject("MultiView_ExtraCamera");
                cam = go.AddComponent<Camera>();
                cam.enabled = false; // rendered manually at end of frame, like Timelapser does
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = ClearColor;
                cam.cullingMask = baseMask;
                cam.nearClipPlane = mrtCamera.nearClipPlane;
                cam.farClipPlane = mrtCamera.farClipPlane;
                cam.renderingPath = mrtCamera.renderingPath;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.useOcclusionCulling = false;
                cam.transparencySortMode = TransparencySortMode.Orthographic;
                go.transform.rotation = Quaternion.identity;

                rt = NewRT("MultiView_RT", 24);
                mrtA = NewRT("MultiView_MRT0", 24);
                mrtB = NewRT("MultiView_MRT1", 0);
                copyA = NewRT("MultiView_MRT0_Copy", 0);
                copyB = NewRT("MultiView_MRT1_Copy", 0);
                cam.targetTexture = rt;
                readTex = new Texture2D(width, height, TextureFormat.RGBA32, false);

                Log.Info($"extra camera ready: {width}x{height}, union={cfg.union}, target={cfg.target}, " +
                         $"mask=0x{baseMask:X8}, session='{sessionDir}'");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("setup failed: " + e);
                return false;
            }
        }

        private RenderTexture NewRT(string name, int depth)
        {
            var t = new RenderTexture(width, height, depth, RenderTextureFormat.ARGB32)
            {
                name = name,
                filterMode = FilterMode.Point,
                antiAliasing = 1,
            };
            t.Create();
            return t;
        }

        private IEnumerator CaptureSet(int set)
        {
            var shots = new List<Shot>();
            var meta = new StringBuilder();
            Region main = default;
            Region o = off;

            // ---- A: main frame
            yield return new WaitForEndOfFrame();
            try
            {
                main = MainRegion();
                RectInt gridVis = GridVisRect(), animVis = MainAnimVisRect();
                bool overlap = Overlaps(o.WorldRect, animVis);
                meta.AppendLine($"set {set}: frame={Time.frameCount} cycle={GameClock.Instance.GetCycle()} t={GameClock.Instance.GetTime():F0} " +
                                $"timeScale={Time.timeScale} world={ClusterManager.Instance.activeWorldId} union={MultiViewRegistry.Active}");
                meta.AppendLine($"  main  {main}");
                meta.AppendLine($"  off   {o}  overlapGridVisible={Overlaps(o.WorldRect, gridVis)} overlapMainAnimChunks={overlap}" +
                                (overlap ? "   <-- OVERLAPS the main view: pan the main camera away from the target!" : ""));
                meta.AppendLine($"  main GridVisibleArea=[{gridVis.xMin}..{gridVis.xMax})x[{gridVis.yMin}..{gridVis.yMax})  " +
                                $"main KAnim chunks(cells)=[{animVis.xMin}..{animVis.xMax})x[{animVis.yMin}..{animVis.yMax})");
                meta.AppendLine($"  perf (avg/frame since last set, union {(MultiViewRegistry.Active ? "ON" : "off")}): {Perf.ReadAndReset()}");

                shots.Add(new Shot { name = "screen", region = main, rgba = CaptureScreen() });
                shots.Add(RenderRegion("ctrl_a", main, baseMask));
                shots.Add(RenderRegion("off_a", o, baseMask));
                if (cfg.twoPassLiquid)
                    shots.Add(RenderTwoPass("off_2pass", o));
                if (set < cfg.layerBreakdownSets)
                {
                    shots.Add(RenderRegion("off_L_default", o, defaultBit));
                    shots.Add(RenderRegion("off_L_water", o, waterBit));
                    shots.Add(RenderRegion("off_L_world", o, worldBit));
                    shots.Add(RenderRegion("off_L_other", o, baseMask & ~(defaultBit | waterBit | worldBit)));
                }
            }
            catch (Exception e)
            {
                Log.Error($"set {set} phase A failed: {e}");
                yield break;
            }

            // ---- B: animation check
            yield return new WaitForSecondsRealtime(cfg.burstDelaySeconds);
            yield return new WaitForEndOfFrame();
            try
            {
                Region now = MainRegion();
                meta.AppendLine($"  burst: main moved {Vector2.Distance(new Vector2(main.x, main.y), new Vector2(now.x, now.y)):F2} cells, zoom {main.ortho:F2}->{now.ortho:F2}");
                shots.Add(RenderRegion("ctrl_b", main, baseMask));
                shots.Add(RenderRegion("off_b", o, baseMask));
            }
            catch (Exception e)
            {
                Log.Error($"set {set} phase B failed: {e}");
                yield break;
            }

            // ---- C: union patches off for two frames (what iteration 1 saw)
            if (cfg.union && cfg.nounionCheck)
            {
                Perf.ReadAndReset();
                MultiViewRegistry.Suspended = true;
                yield return null;
                yield return null;
                yield return new WaitForEndOfFrame();
                try
                {
                    shots.Add(RenderRegion("off_nounion", o, baseMask));
                }
                catch (Exception e)
                {
                    Log.Error($"set {set} nounion failed: {e}");
                }
                finally
                {
                    MultiViewRegistry.Suspended = false;
                }
                meta.AppendLine($"  perf (union OFF, ~3 frames): {Perf.ReadAndReset()}");
            }

            // ---- D: optional official whole-world branch
            if (cfg.forcedPass)
            {
                GroundRenderAllCalls = 0;
                ForceFullWorld = true;
                yield return null;
                yield return new WaitForEndOfFrame();
                try
                {
                    shots.Add(RenderRegion("off_forced", o, baseMask));
                }
                finally
                {
                    ForceFullWorld = false;
                }
                meta.AppendLine($"  forced pass: RenderAll calls={GroundRenderAllCalls}, World.LateUpdate {Ms(LastWorldLateUpdateTicks):F2}ms");
            }

            // ---- E: ground truth through the real pipeline (main camera on the region for one frame)
            if (cfg.truthCapture)
            {
                var cc = CameraController.Instance;
                Vector3 saved = cc.transform.position;
                cc.SnapTo(new Vector3(o.x, o.y, saved.z));
                yield return null;
                yield return new WaitForEndOfFrame();
                try
                {
                    Vector3 p = cc.transform.position;
                    meta.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  truth: main camera actually at ({0:F1},{1:F1}) ortho={2:F2} (wanted ({3:F1},{4:F1}) ortho={5:F2})",
                        p.x, p.y, cc.OrthographicSize, o.x, o.y, o.ortho));
                    shots.Add(new Shot { name = "truth", region = o, rgba = CaptureScreen() });
                }
                catch (Exception e)
                {
                    Log.Error($"set {set} truth failed: {e}");
                }
                finally
                {
                    cc.SnapTo(saved);
                }
            }

            string metaText = meta.ToString();
            int w = width, h = height;
            string dir = sessionDir;
            pending = pending.ContinueWith(_ => ProcessSet(set, shots, metaText, dir, w, h));
        }

        // ---------------------------------------------------------------- regions

        private Region MainRegion()
        {
            var cc = CameraController.Instance;
            Vector3 p = cc.transform.position;
            return new Region { kind = "main", x = p.x, y = p.y, ortho = cc.OrthographicSize, aspect = Aspect };
        }

        private Region PickOffRegion(Region main)
        {
            float ho = cfg.offOrthoSize > 0f ? cfg.offOrthoSize : main.ortho;
            float hw = ho * Aspect, m = cfg.separationMargin;
            CameraController.Instance.GetWorldCamera(out Vector2I wo, out Vector2I ws);
            RectInt anim = MainAnimVisRect();
            var cands = new List<Region>();

            Region Make(string kind, float x, float y) => new Region { kind = kind, x = x, y = y, ortho = ho, aspect = Aspect };

            if (cfg.target == "fixed")
                cands.Add(Make("fixed", cfg.fixedX, cfg.fixedY));
            if (cfg.target == "auto" || cfg.target == "telepad")
            {
                GameObject tp = GameUtil.GetTelepad(ClusterManager.Instance.activeWorldId);
                if (tp != null)
                {
                    Vector3 p = tp.transform.position;
                    cands.Add(Make("telepad", p.x, p.y + 2f));
                }
            }
            if (cfg.target == "auto" || cands.Count == 0)
            {
                cands.Add(Make("right", anim.xMax + m + hw, main.y));
                cands.Add(Make("left", anim.xMin - m - hw, main.y));
                cands.Add(Make("below", main.x, anim.yMin - m - ho));
                cands.Add(Make("above", main.x, anim.yMax + m + ho));
            }

            for (int i = 0; i < cands.Count; i++)
            {
                Region c = ClampToWorld(cands[i], wo, ws);
                if (cfg.target != "auto" || !Overlaps(c.WorldRect, anim))
                    return c;
            }
            return ClampToWorld(cands[0], wo, ws);
        }

        private static Region ClampToWorld(Region r, Vector2I wo, Vector2I ws)
        {
            float minX = wo.x + r.HalfW, maxX = wo.x + ws.x - r.HalfW;
            float minY = wo.y + r.HalfH, maxY = wo.y + ws.y - r.HalfH;
            r.x = minX <= maxX ? Mathf.Clamp(r.x, minX, maxX) : wo.x + ws.x * 0.5f;
            r.y = minY <= maxY ? Mathf.Clamp(r.y, minY, maxY) : wo.y + ws.y * 0.5f;
            return r;
        }

        private static RectInt GridVisRect()
        {
            GridArea a = GridVisibleArea.GetVisibleArea();
            return new RectInt(a.Min.x, a.Min.y, a.Max.x - a.Min.x, a.Max.y - a.Min.y);
        }

        /// <summary>The main camera's own KAnim chunk rect (not the widened one the patch hands out).</summary>
        private static RectInt MainAnimVisRect()
        {
            var u = Singleton<KBatchedAnimUpdater>.Instance;
            Vector2I mn = KBatchedAnimUpdater_UpdateVisibility_Patch.VisMin(u);
            Vector2I mx = KBatchedAnimUpdater_UpdateVisibility_Patch.VisMax(u);
            const int C = KAnimBatchManager.CHUNK_SIZE;
            return new RectInt(mn.x * C, mn.y * C, (mx.x - mn.x + 1) * C, (mx.y - mn.y + 1) * C);
        }

        private static bool Overlaps(Rect r, RectInt cells) =>
            r.xMin < cells.xMax && r.xMax > cells.xMin && r.yMin < cells.yMax && r.yMax > cells.yMin;

        // ---------------------------------------------------------------- rendering

        private void PositionCam(Region r)
        {
            float z = CameraController.Instance.transform.position.z;
            cam.transform.position = new Vector3(r.x, r.y, z);
            cam.orthographicSize = r.ortho;
            cam.aspect = Aspect;
        }

        private Shot RenderRegion(string name, Region r, int mask)
        {
            PositionCam(r);
            cam.cullingMask = mask;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = ClearColor;
            cam.targetTexture = null; // drop any SetTargetBuffers state
            cam.targetTexture = rt;
            cam.Render();
            return new Shot { name = name, region = r, rgba = ReadBack(rt) };
        }

        /// <summary>
        /// Mimics the main pipeline: world (minus Water) into two colour targets like "baseCamera MRT",
        /// copy them, point the liquid shader's MRT*_Copy / _MRT* globals at OUR copies, then draw the
        /// Water layer on top. In the single-pass render those globals still point at the main view.
        /// </summary>
        private Shot RenderTwoPass(string name, Region r)
        {
            PositionCam(r);
            var bufs = new[] { mrtA.colorBuffer, mrtB.colorBuffer };
            cam.backgroundColor = Color.black;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.cullingMask = baseMask & ~waterBit;
            cam.SetTargetBuffers(bufs, mrtA.depthBuffer);
            cam.Render();

            Graphics.Blit(mrtA, copyA);
            Graphics.Blit(mrtB, copyB);
            string[] names = { "MRT0_Copy", "MRT1_Copy", "_MRT0", "_MRT1" };
            Texture[] mine = { copyA, copyB, copyA, copyB };
            var saved = new Texture[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                saved[i] = Shader.GetGlobalTexture(names[i]);
                Shader.SetGlobalTexture(names[i], mine[i]);
            }
            try
            {
                cam.cullingMask = waterBit;
                cam.clearFlags = CameraClearFlags.Nothing;
                cam.SetTargetBuffers(bufs, mrtA.depthBuffer);
                cam.Render();
            }
            finally
            {
                for (int i = 0; i < names.Length; i++)
                    Shader.SetGlobalTexture(names[i], saved[i]);
                cam.targetTexture = null;
                cam.targetTexture = rt;
                cam.cullingMask = baseMask;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = ClearColor;
            }
            return new Shot { name = name, region = r, rgba = ReadBack(mrtA) };
        }

        private byte[] CaptureScreen()
        {
            Texture2D full = ScreenCapture.CaptureScreenshotAsTexture();
            RenderTexture tmp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(full, tmp);
                return ReadBack(tmp);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
                Destroy(full);
            }
        }

        private byte[] ReadBack(RenderTexture src)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = src;
            readTex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            RenderTexture.active = prev;
            return readTex.GetRawTextureData(); // CPU copy filled by ReadPixels
        }

        // ---------------------------------------------------------------- background: encode + stats

        private void ProcessSet(int set, List<Shot> shots, string meta, string dir, int w, int h)
        {
            try
            {
                var lines = new StringBuilder(meta);
                var csv = new StringBuilder();
                var byName = new Dictionary<string, Shot>();
                foreach (Shot s in shots)
                {
                    byName[s.name] = s;
                    string file = $"set{set:D3}_{s.name}.png";
                    byte[] png = ImageConversion.EncodeArrayToPNG(s.rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h);
                    File.WriteAllBytes(Path.Combine(dir, file), png);

                    ImageStats st = ImageStats.Compute(s.rgba, w, h);
                    lines.AppendLine($"  shot {s.name,-14} {st}");
                    csv.AppendLine(string.Join(",", set, s.name, s.region.kind,
                        F(s.region.x), F(s.region.y), F(s.region.ortho),
                        F(st.meanLuma), F(st.blackFrac), F(st.magentaFrac), F(st.dominantFrac),
                        F(st.magentaLeft), F(st.magentaRight), file));
                }

                var pairCsv = new StringBuilder();
                void Pair(string label, string a, string b)
                {
                    if (!byName.TryGetValue(a, out Shot sa) || !byName.TryGetValue(b, out Shot sb)) return;
                    ImageStats.Diff(sa.rgba, sb.rgba, out float frac, out float mad);
                    lines.AppendLine($"  pair {label,-16} {a} vs {b}: changedPixels={frac:P2} meanAbsDiff={mad:F2}");
                    pairCsv.AppendLine(string.Join(",", set, label, a, b, F(frac), F(mad)));
                }
                Pair("ctrl_anim", "ctrl_a", "ctrl_b");        // motion inside the main view
                Pair("off_anim", "off_a", "off_b");           // motion in the extra view
                Pair("union_gain", "off_nounion", "off_a");   // what the union patches add
                Pair("twopass_change", "off_a", "off_2pass"); // what the liquid second pass changes
                Pair("truth_vs_off", "truth", "off_a");
                Pair("truth_vs_2pass", "truth", "off_2pass");
                Pair("screen_vs_ctrl", "screen", "ctrl_a");

                lock (fileLock)
                {
                    File.AppendAllText(Path.Combine(dir, "shots.csv"), csv.ToString());
                    File.AppendAllText(Path.Combine(dir, "pairs.csv"), pairCsv.ToString());
                    File.AppendAllText(Path.Combine(dir, "session.log"), lines.ToString());
                }
                logQueue.Enqueue(lines.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                logQueue.Enqueue($"ERROR processing set {set}: {e}");
            }
        }

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
