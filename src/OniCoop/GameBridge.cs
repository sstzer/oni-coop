namespace OniCoop
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Everything that touches the game itself.</summary>
    internal static class GameBridge
    {
        public static string CoopDir => Path.Combine(Util.RootFolder(), "coop");

        public static Manifest BuildManifest()
        {
            var m = new Manifest { build = KleiVersion.ChangeList, coopVersion = CoopMod.Version, dlcs = DlcManager.GetActiveDLCIds() };
            foreach (KMod.Mod mod in Global.Instance.modManager.mods)
            {
                if (!mod.IsEnabledForActiveDlc() || mod.staticID == CoopMod.StaticId) continue;
                // Steam mods: version = workshop update time (comparable across PCs). Local/dev mods: a hash of
                // the folder name, meaningless between PCs, so not compared.
                long version = mod.label.distribution_platform == KMod.Label.DistributionPlatform.Steam ? mod.label.version : 0;
                m.mods.Add(new ModEntry { staticId = mod.staticID, title = mod.title, version = version });
            }
            m.mods.Sort((a, b) => string.CompareOrdinal(a.staticId, b.staticId));
            return m;
        }

        /// <summary>Lets `coop-relay bot --manifest` impersonate this installation.</summary>
        public static void DumpManifest(Manifest m)
        {
            try { File.WriteAllText(Path.Combine(CoopMod.ModPath, "manifest.b64"), Convert.ToBase64String(Wire.Build(m.Write))); }
            catch (Exception e) { Log.Warn("could not write manifest.b64: " + e.Message); }
        }

        public static List<string> GameDlcs() =>
            SaveLoader.Instance != null ? new List<string>(SaveLoader.Instance.GameInfo.dlcIds ?? new List<string>()) : new List<string>();

        public static string ColonyName() => SaveGame.Instance != null ? SaveGame.Instance.BaseName : "";

        /// <summary>Host: write the running world to a private file (the player's own save and "current save"
        /// pointer are untouched) and return its bytes.</summary>
        public static byte[] SaveWorld(out string name)
        {
            string dir = Path.Combine(CoopDir, "outgoing");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "host_world.sav");
            if (File.Exists(file)) File.Delete(file);
            SaveLoader.Instance.Save(file, isAutoSave: false, updateSavePointer: false);
            if (!File.Exists(file)) throw new IOException("the game did not write the save (disk full / no access?)");
            name = ColonyName() + ".sav";
            return File.ReadAllBytes(file);
        }

        /// <summary>The part of a save that must run on the main thread: a consistent snapshot of the world,
        /// serialized but not compressed.</summary>
        public sealed class WorldSnapshot
        {
            public byte[] head;      // file header + type directory, written as-is
            public byte[] body;      // uncompressed world (MemoryStream buffer)
            public int bodyLength;
            public string name;
        }

        private static readonly System.Reflection.MethodInfo SaveToWriter =
            HarmonyLib.AccessTools.Method(typeof(SaveLoader), "Save", new[] { typeof(BinaryWriter) });

        /// <summary>Main thread. Same steps and file layout as SaveLoader.Save(string), minus what a world transfer
        /// doesn't need: no file, no colony preview screenshot, no colony summary / metrics, no forced GC.
        /// Compression and upload happen later on a worker thread (<see cref="EncodeWorld"/>).</summary>
        public static WorldSnapshot CaptureWorld()
        {
            if (SaveToWriter == null) throw new MissingMethodException("SaveLoader.Save(BinaryWriter) not found");
            KSerialization.Manager.Clear();
            var body = new MemoryStream(8 << 20);
            var w = new BinaryWriter(body);
            try { SaveToWriter.Invoke(SaveLoader.Instance, new object[] { w }); }
            catch (System.Reflection.TargetInvocationException e) { throw e.InnerException ?? e; }
            w.Flush();

            byte[] info = SaveGame.Instance.GetSaveHeader(false, true, out SaveGame.Header header);
            var head = new MemoryStream();
            var hw = new BinaryWriter(head);
            hw.Write(header.buildVersion);
            hw.Write(header.headerSize);
            hw.Write(header.headerVersion);
            hw.Write(header.compression);
            hw.Write(info);
            KSerialization.Manager.SerializeDirectory(hw);
            hw.Flush();
            return new WorldSnapshot { head = head.ToArray(), body = body.GetBuffer(), bodyLength = (int)body.Length, name = ColonyName() + ".sav" };
        }

        /// <summary>Any thread: compress like the game does (zlib, best speed) into a loadable .sav.</summary>
        public static byte[] EncodeWorld(WorldSnapshot s)
        {
            var file = new MemoryStream(s.head.Length + s.bodyLength / 4);
            file.Write(s.head, 0, s.head.Length);
            using (var z = new Ionic.Zlib.ZlibStream(file, Ionic.Zlib.CompressionMode.Compress, Ionic.Zlib.CompressionLevel.BestSpeed, true))
                z.Write(s.body, 0, s.bodyLength);
            return file.ToArray();
        }

        public static string WriteReceivedWorld(string room, byte[] data)
        {
            string dir = Path.Combine(CoopDir, "received");
            Directory.CreateDirectory(dir);
            var safe = new StringBuilder();
            foreach (char c in room) safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string file = Path.Combine(dir, safe + ".sav");
            File.WriteAllBytes(file, data);
            return file;
        }

        public static void LoadWorld(string file)
        {
            Log.Info("loading " + file);
            LoadScreen.DoLoad(file);
        }

        public static int ActiveWorld() => ClusterManager.Instance != null ? ClusterManager.Instance.activeWorldId : 0;

        public static bool Capture(PresenceData p, bool fakeCursor, float t)
        {
            Camera cam = Camera.main;
            if (cam == null) return false;
            float z = cam.transform.position.z;
            Vector3 lo = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
            Vector3 hi = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
            p.viewMinX = lo.x; p.viewMinY = lo.y; p.viewMaxX = hi.x; p.viewMaxY = hi.y;
            p.world = ActiveWorld();
            p.flags = PresenceData.FlagInGame;
            if (fakeCursor)
            {
                float cx = (lo.x + hi.x) * 0.5f, cy = (lo.y + hi.y) * 0.5f;
                p.cursorX = cx + (hi.x - lo.x) * 0.25f * Mathf.Cos(t * 1.3f);
                p.cursorY = cy + (hi.y - lo.y) * 0.25f * Mathf.Sin(t * 2.1f);
                p.flags |= PresenceData.FlagCursor;
            }
            else
            {
                Vector3 mouse = KInputManager.GetMousePos();
                if (Application.isFocused && mouse.x >= 0 && mouse.y >= 0 && mouse.x <= Screen.width && mouse.y <= Screen.height)
                {
                    Vector3 w = PlayerController.GetCursorPos(mouse);
                    p.cursorX = w.x; p.cursorY = w.y;
                    p.flags |= PresenceData.FlagCursor;
                }
            }
            return true;
        }

        /// <summary>Test helper: drift the camera slowly around where it started.</summary>
        public static void Wander(Vector3 origin, float t)
        {
            if (CameraController.Instance == null) return;
            var pos = new Vector3(origin.x + 18f * Mathf.Sin(t * 0.21f), origin.y + 10f * Mathf.Sin(t * 0.33f), origin.z);
            CameraController.Instance.SnapTo(pos);
        }
    }

    /// <summary>Windowed test layout. Win32 only (no-op elsewhere).</summary>
    internal static class WindowUtil
    {
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

        private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        public static bool Move(int x, int y)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer) return false;
            uint me = GetCurrentProcessId();
            IntPtr found = IntPtr.Zero;
            var sb = new StringBuilder(64);
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                sb.Length = 0;
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() != "UnityWndClass") return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found != IntPtr.Zero && SetWindowPos(found, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }
}
