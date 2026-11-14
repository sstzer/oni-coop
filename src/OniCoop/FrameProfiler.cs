namespace OniCoop
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using System.Text;
    using HarmonyLib;

    /// <summary>-coop-profile N: where a frame's time goes, logged every N seconds. Times the game's big per-frame
    /// pieces (and ours) with Harmony prefix/postfix stopwatches. Nested pieces are also counted in their parent
    /// (e.g. the sim step is inside Game.Update).</summary>
    internal static class FrameProfiler
    {
        private static readonly string[][] Points =
        {
            new[] { "Game", "Update", "Game.Update (all below + conduits/circuits)" },
            new[] { "Game", "UnsafeSim200ms", "  sim step: gas/liquid/heat (native) + cell changes" },
            new[] { "StateMachineUpdater", "AdvanceOneSimSubTick", "  sim ticks: buildings, critters, plants, monitors" },
            new[] { "CircuitManager", "RenderEveryTick", "  power" },
            new[] { "Pathfinding", "RenderEveryTick", "  pathfinding" },
            new[] { "Game", "LateUpdate", "Game.LateUpdate (state machine render, status icons)" },
            new[] { "KBatchedAnimUpdater", "LateUpdate", "animations" },
            new[] { "PropertyTextures", "LateUpdate", "world textures (gas, liquid, light, temperature)" },
            new[] { "World", "LateUpdate", "world" },
            new[] { "LightBuffer", "LateUpdate", "lights" },
            new[] { "NameDisplayScreen", "LateUpdate", "name labels and bars over duplicants" },
            new[] { "StatusItemRenderer", "RenderEveryTick", "  status icons over objects" },
            new[] { "LoopingSoundManager", "RenderEveryTick", "  looping sounds" },
            new[] { "KScreenManager", "Update", "screens" },
            new[] { "ToolTipScreen", "Update", "tooltips" },
            new[] { "OverlayScreen", "LateUpdate", "overlays" },
            new[] { "SimDebugView", "Update", "overlay view" },
            new[] { "CameraController", "Update", "camera" },
            new[] { "Timelapser", "Update", "timelapse" },
            new[] { "OniCoop.CoopRuntime", "Update", "coop: runtime (network, puppets, cells, objects)" },
            new[] { "OniCoop.Puppets", "OnSnapshot", "  coop: puppet keyframes in" },
            new[] { "OniCoop.Puppets", "GuestTick", "  coop: puppet playback" },
            new[] { "OniCoop.WorldSync", "OnCells", "  coop: cells in" },
            new[] { "OniCoop.WorldSync", "Apply", "    coop: cells written (game)" },
            new[] { "OniCoop.WorldSync", "GuestTick", "  coop: cell pinning" },
            new[] { "OniCoop.EntitySync", "OnRecords", "  coop: object records in" },
            new[] { "OniCoop.EntitySync", "GuestTick", "  coop: local object cleanup" },
            new[] { "OniCoop.CommandBus", "OnRelay", "  coop: orders in" },
            new[] { "OniCoop.BuildingSync", "ApplyTimeline", "  coop: building anims played" },
            new[] { "OniCoop.BuildingSync", "OnPipes", "  coop: pipe contents in" },
            new[] { "OniCoop.BuildingSync", "GuestTick", "  coop: pipe pinning" },
            new[] { "OniCoop.GlobalSync", "OnRecords", "  coop: global values in" },
            new[] { "OniCoop.StatusSync", "OnRecords", "  coop: status lists in" },
            new[] { "OniCoop.StatusSync", "ReadEntry", "    coop: status entries read" },
            new[] { "OniCoop.StatusSync", "ApplyStatusTo", "    coop: status lists applied (game)" },
            new[] { "OniCoop.StatusSync", "ApplyEffectsTo", "    coop: effects applied (game)" },
            new[] { "OniCoop.StatusSync", "GuestTick", "  coop: status/effect checks" },
            new[] { "OniCoop.ReportSync", "OnRecords", "  coop: report sums in" },
            new[] { "OniCoop.Calls", "OnRelay", "  coop: UI calls in" },
            new[] { "OniCoop.OperationSync", "OnRecords", "  coop: building states / power / automation in" },
            new[] { "OniCoop.OperationSync", "GuestTick", "  coop: circuit / network matching" },
            new[] { "OniCoop.PuppetLooks", "GuestTick", "  coop: looks asks" },
            new[] { "OniCoop.Puppets", "HostTick", "  coop (server): puppet keyframes out" },
            new[] { "OniCoop.PuppetLooks", "HostSample", "    coop (server): looks check (per puppet)" },
            new[] { "OniCoop.Puppets", "Flush", "    coop (server): keyframe messages" },
            new[] { "OniCoop.EntitySync", "HostFlush", "  coop (server): objects out" },
            new[] { "OniCoop.WorldSync", "HostTick", "  coop (server): cells out" },
            new[] { "OniCoop.StatusSync", "HostTick", "  coop (server): status lists out" },
            new[] { "OniCoop.BuildingSync", "HostTick", "  coop (server): building anims out" },
            new[] { "OniCoop.OperationSync", "HostTick", "  coop (server): building states / power / automation out" },
            new[] { "OniCoop.CoopRuntime", "Handle", "  coop: network events" },
            new[] { "OniCoop.CoopRuntime", "TestHelpers", "  coop: test helpers / headless dialogs" },
            new[] { "OniCoop.CoopRuntime", "SyncState", "  coop: member state" },
            new[] { "OniCoop.CoopRuntime", "LateUpdate", "coop: send orders/objects" },
            new[] { "OniCoop.CoopRuntime", "OnGUI", "coop: overlay panel" },
        };

        private static readonly Dictionary<MethodBase, int> index = new Dictionary<MethodBase, int>();
        private static long[] ticks, maxTicks, allocated;
        private static long frameAllocStart, frameAllocated;
        private static int[] calls;
        private static string[] labels;
        private static readonly List<float> frameMs = new List<float>(4096);
        private static long lastFrame;
        private static float nextReport;
        public static float Every;

        public static void Apply(Harmony h, float every)
        {
            Every = every;
            DetectCounter();
            labels = new string[Points.Length];
            ticks = new long[Points.Length];
            maxTicks = new long[Points.Length];
            calls = new int[Points.Length];
            allocated = new long[Points.Length];
            var pre = new HarmonyMethod(typeof(FrameProfiler), nameof(Prefix));
            var post = new HarmonyMethod(typeof(FrameProfiler), nameof(Postfix));
            for (int i = 0; i < Points.Length; i++)
            {
                labels[i] = Points[i][2];
                try
                {
                    MethodInfo m = AccessTools.Method(AccessTools.TypeByName(Points[i][0]), Points[i][1]);
                    if (m == null) { Log.Warn($"profile: {Points[i][0]}.{Points[i][1]} not found"); continue; }
                    index[m] = i;
                    h.Patch(m, prefix: pre, postfix: post);
                }
                catch (System.Exception e) { Log.Warn($"profile: can't patch {Points[i][0]}.{Points[i][1]}: {e.Message}"); }
            }
            Log.Info($"profile: timing {index.Count} pieces, report every {every} s");
        }

        private struct Mark { public long t, mem; }

        /// <summary>Bytes this thread allocated, when the runtime counts them (exact, unlike the heap size, which also
        /// grows with the network threads' allocations).</summary>
        private static System.Func<long> threadAllocated;
        public static bool PerThread => threadAllocated != null;

        private static long Allocated() => threadAllocated != null ? threadAllocated() : System.GC.GetTotalMemory(false);

        private static void DetectCounter()
        {
            try
            {
                MethodInfo m = typeof(System.GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static);
                if (m == null) { Log.Info("profile: no per-thread allocation counter; using the heap size"); return; }
                var f = (System.Func<long>)System.Delegate.CreateDelegate(typeof(System.Func<long>), m);
                long a = f();
                var junk = new byte[64 * 1024];
                long b = f();
                if (b - a >= junk.Length) { threadAllocated = f; Log.Info($"profile: per-thread allocation counter works ({b - a} bytes for a 64 KB array)"); }
                else Log.Info($"profile: per-thread allocation counter doesn't count ({b - a} bytes for a 64 KB array); using the heap size");
            }
            catch (System.Exception e) { Log.Info("profile: per-thread allocation counter unavailable: " + e.Message); }
        }

        private static void Prefix(out Mark __state) => __state = new Mark { t = Stopwatch.GetTimestamp(), mem = Allocated() };

        private static void Postfix(Mark __state, MethodBase __originalMethod)
        {
            if (!index.TryGetValue(__originalMethod, out int i)) return;
            long mem = Allocated() - __state.mem;
            if (mem > 0) allocated[i] += mem; // (a collection in between makes it negative: skipped)
            long d = Stopwatch.GetTimestamp() - __state.t;
            ticks[i] += d;
            calls[i]++;
            if (d > maxTicks[i]) maxTicks[i] = d;
        }

        /// <summary>Once per frame (from the runtime's Update).</summary>
        public static void Frame(float now)
        {
            if (Every <= 0) return;
            long t = Stopwatch.GetTimestamp();
            if (lastFrame != 0) frameMs.Add((float)((t - lastFrame) * 1000.0 / Stopwatch.Frequency));
            lastFrame = t;
            long m = System.GC.GetTotalMemory(false); // (whole process: all threads)
            if (frameAllocStart != 0 && m > frameAllocStart) frameAllocated += m - frameAllocStart;
            frameAllocStart = m;
            if (nextReport == 0) { nextReport = now + Every; Clear(); return; }
            if (now < nextReport) return;
            nextReport = now + Every;
            Report();
            Clear();
        }

        private static int gcAtStart;

        // ---- deep: every Update / LateUpdate / FixedUpdate of the game's scripts (-coop-profile-deep), to find what the
        // named pieces above don't cover

        private sealed class DeepStat { public string name; public long ticks, calls, max; }
        private static readonly Dictionary<MethodBase, DeepStat> deep = new Dictionary<MethodBase, DeepStat>();

        public static void ApplyDeep(Harmony h)
        {
            var sw = Stopwatch.StartNew();
            var pre = new HarmonyMethod(typeof(FrameProfiler), nameof(DeepPre));
            var post = new HarmonyMethod(typeof(FrameProfiler), nameof(DeepPost));
            int patched = 0, failed = 0;
            foreach (System.Reflection.Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name;
                if (an != "Assembly-CSharp" && an != "Assembly-CSharp-firstpass" && !an.StartsWith("FMOD", System.StringComparison.Ordinal) && an != "UnityEngine.UI" && an != "Unity.TextMeshPro") continue;
                System.Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (System.Type t in types)
                {
                    if (t == null || t.IsAbstract || t.ContainsGenericParameters || !typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(t)) continue;
                    foreach (string mn in new[] { "Update", "LateUpdate", "FixedUpdate" })
                    {
                        MethodInfo m = t.GetMethod(mn, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, System.Type.EmptyTypes, null);
                        if (m == null || m.IsAbstract || m.GetMethodBody() == null || deep.ContainsKey(m)) continue;
                        try { h.Patch(m, prefix: pre, postfix: post); deep[m] = new DeepStat { name = t.Name + "." + mn }; patched++; }
                        catch (System.Exception) { failed++; }
                    }
                }
            }
            Log.Info($"profile: deep: {patched} per-frame methods timed ({failed} couldn't be), {sw.ElapsedMilliseconds} ms to patch");
        }

        private static void DeepPre(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void DeepPost(long __state, MethodBase __originalMethod)
        {
            if (!deep.TryGetValue(__originalMethod, out DeepStat d)) return;
            long t = Stopwatch.GetTimestamp() - __state;
            d.ticks += t; d.calls++;
            if (t > d.max) d.max = t;
        }

        private static void ReportDeep(StringBuilder sb, int frames)
        {
            if (deep.Count == 0) return;
            var list = new List<DeepStat>(deep.Values);
            list.Sort((a, b) => b.ticks.CompareTo(a.ticks));
            double perMs = 1000.0 / Stopwatch.Frequency;
            double sum = 0;
            foreach (DeepStat d in list) sum += d.ticks * perMs / frames;
            sb.Append($"\n    deep: all game scripts' Update/LateUpdate/FixedUpdate together {sum:F2} ms/frame; the biggest:");
            for (int i = 0; i < list.Count && i < 15; i++)
            {
                DeepStat d = list[i];
                if (d.calls == 0) break;
                sb.Append($"\n      {d.ticks * perMs / frames,6:F2} ms/frame (max {d.max * perMs,5:F1} ms, {d.calls / (float)frames,5:F1}/frame)  {d.name}");
            }
            foreach (DeepStat d in list) { d.ticks = 0; d.calls = 0; d.max = 0; }
        }

        private static void Clear()
        {
            gcAtStart = System.GC.CollectionCount(0);
            frameMs.Clear();
            for (int i = 0; i < ticks.Length; i++) { ticks[i] = 0; maxTicks[i] = 0; calls[i] = 0; allocated[i] = 0; }
            frameAllocated = 0;
        }

        private static void Report()
        {
            int n = frameMs.Count;
            if (n == 0) return;
            var sorted = new List<float>(frameMs);
            sorted.Sort();
            float total = 0;
            foreach (float f in frameMs) total += f;
            double perMs = 1000.0 / Stopwatch.Frequency;
            var speed = SpeedControlScreen.Instance;
            var sb = new StringBuilder();
            int gcs = System.GC.CollectionCount(0) - gcAtStart;
            sb.Append($"profile: {gcs} GC in {total / 1000f:F0} s, {n / (total / 1000f):F1} fps, frame {total / n:F1} ms avg / {sorted[(int)(n * 0.5f)]:F1} median / {sorted[System.Math.Min(n - 1, (int)(n * 0.95f))]:F1} p95 / {sorted[n - 1]:F0} max, allocating {frameAllocated / 1024.0 / (total / 1000f):F0} KB/s, " +
                      $"{(speed == null ? "?" : speed.IsPaused ? "paused" : "speed " + (speed.GetSpeed() + 1))}, mirroring {Puppets.Mirroring}");
            double accounted = 0;
            for (int i = 0; i < ticks.Length; i++)
            {
                if (calls[i] == 0) continue;
                double ms = ticks[i] * perMs / n;
                if (!labels[i].StartsWith(" ")) accounted += ms;
                sb.Append($"\n    {ms,6:F2} ms/frame  (max {maxTicks[i] * perMs,5:F1} ms, {calls[i] / (float)n,4:F1}/frame, {allocated[i] / 1024.0 / (total / 1000f),6:F0} KB/s)  {labels[i]}");
            }
            sb.Append($"\n    {total / n - accounted,6:F2} ms/frame  rest: rendering / GPU wait, UI, other scripts");
            ReportDeep(sb, n);
            Log.Info(sb.ToString());
        }
    }
}
