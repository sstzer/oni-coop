namespace OniCoop
{
    using System.Diagnostics;
    using UnityEngine;
    using UnityEngine.Scripting;

    /// <summary>When the garbage collector runs, in a game.
    ///
    /// This Unity has no incremental collector: every collection stops the whole game while it scans the managed heap,
    /// and the heap holds the whole loaded world (~1.5 GB), so a collection takes 0.35-0.7 s on this laptop whether
    /// or not the game simulates (the server's take as long as a guest's). Its length can't be helped; its moment can.
    ///
    /// In a game the collector runs in manual mode (GarbageCollector.Mode.Manual: no automatic collections) and the
    /// mod collects:
    ///  - when the server saves (everybody waits for it anyway: the guests while their timeline waits for the server,
    ///    the server right after the save);
    ///  - at a quiet moment once garbage has piled up: the game window in the background, or the game paused with the
    ///    camera still for a second (a guest), or the game paused / no player in it (the server);
    ///  - at once when far too much has piled up (a safety cap; the heap only grows in between).
    /// Outside a game (menus, loading) the collector runs as normal.</summary>
    internal static class GcPolicy
    {
        public static bool Active;
        private static long baseline;           // heap in use right after our last collection
        private static long soft, hard;         // bytes of growth: collect at a quiet moment / at once
        private static float nextCheck, stillSince;
        private static Vector3 lastCam;
        private static float lastZoom;
        public static long Collections;
        public static float LastPauseMs, MaxPauseMs;
        private const long MB = 1024 * 1024;

        /// <summary>In a game: manual collections from now on (false: not available, the caller falls back).</summary>
        public static bool Enter(bool server)
        {
            if (!CoopMod.Settings.gcManual) return false;
            try { GarbageCollector.GCMode = GarbageCollector.Mode.Manual; }
            catch (System.Exception e) { Log.Warn("gc: manual mode not available: " + e.Message); return false; }
            if (GarbageCollector.GCMode != GarbageCollector.Mode.Manual) { Log.Warn("gc: manual mode didn't take"); return false; }
            Active = true;
            soft = (server ? 1024 : 384) * MB;
            hard = (server ? 3072 : 1536) * MB;
            baseline = System.GC.GetTotalMemory(false); // (the heap reserve right before ended with a collection)
            seenCount = System.GC.CollectionCount(0);
            Log.Info($"gc: manual mode: collections at server saves, at quiet moments after {soft / MB} MB, at once after {hard / MB} MB");
            return true;
        }

        public static void Leave()
        {
            if (!Active) return;
            Active = false;
            try { GarbageCollector.GCMode = GarbageCollector.Mode.Enabled; }
            catch (System.Exception e) { Log.Warn("gc: can't switch back to automatic: " + e.Message); }
            Log.Info($"gc: automatic again ({Collections} collections in the game, longest {MaxPauseMs:F0} ms)");
        }

        /// <summary>Someone else just collected (the game's save does): start counting from here.</summary>
        public static void NoteCollected(string why)
        {
            baseline = System.GC.GetTotalMemory(false);
            seenCount = System.GC.CollectionCount(0);
            Log.Info($"gc: {why}; in use {baseline / MB} MB");
        }

        private static int seenCount;
        public static long Unexpected;

        public static void Collect(string why)
        {
            long before = System.GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            inOwnCollect = true;
            try { System.GC.Collect(); }
            finally { inOwnCollect = false; }
            LastPauseMs = sw.ElapsedMilliseconds;
            if (LastPauseMs > MaxPauseMs) MaxPauseMs = LastPauseMs;
            baseline = System.GC.GetTotalMemory(false);
            seenCount = System.GC.CollectionCount(0);
            Collections++;
            Log.Info($"gc: collected ({why}) in {LastPauseMs:F0} ms, in use {before / MB} -> {baseline / MB} MB");
        }

        /// <summary>Garbage piled up since the last collection.</summary>
        public static long Grown => Active ? System.GC.GetTotalMemory(false) - baseline : 0;

        /// <summary>Every frame in a game.</summary>
        /// <param name="quietServer">server: paused, or no player in the game</param>
        public static void Tick(float now, bool server, bool quietServer)
        {
            if (!Active) return;
            // a collection nobody here asked for (manual mode should have none): note when and how big the heap was
            int count = System.GC.CollectionCount(0);
            if (count != seenCount)
            {
                Unexpected++;
                if (Unexpected <= 20) Log.Info($"gc: a collection the mod didn't make ({count - seenCount}), in use now {System.GC.GetTotalMemory(false) / MB} MB, {(System.GC.GetTotalMemory(false) - baseline) / MB} MB over the last baseline; this machine has {SystemInfo.systemMemorySize} MB of memory");
                seenCount = count;
                baseline = System.GC.GetTotalMemory(false);
            }
            // a guest: is the picture still? (paused, camera not moving)
            if (!server)
            {
                CameraController cam = CameraController.Instance;
                Vector3 p = cam != null ? cam.transform.position : Vector3.zero;
                float z = cam != null ? cam.OrthographicSize : 0f;
                bool paused = SpeedControlScreen.Instance != null && SpeedControlScreen.Instance.IsPaused;
                if (!paused || (p - lastCam).sqrMagnitude > 1e-4f || Mathf.Abs(z - lastZoom) > 1e-3f) stillSince = now;
                lastCam = p;
                lastZoom = z;
            }
            if (now < nextCheck) return;
            nextCheck = now + 0.25f;
            long grown = Grown;
            if (grown > hard) { Collect($"safety: {grown / MB} MB since the last one"); return; }
            if (grown < soft) return;
            bool quiet = server ? quietServer : (!Application.isFocused || now - stillSince > 1f);
            if (quiet) Collect($"quiet moment, {grown / MB} MB since the last one");
        }

        // ---- who else collects (diagnostics: a collection the mod didn't make stops everyone as long as any)

        private static int foreignLogged;

        private static void ForeignPrefix(System.Reflection.MethodBase __originalMethod)
        {
            if (!Active || inOwnCollect || foreignLogged >= 12) return;
            foreignLogged++;
            Log.Info($"gc: {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} called from:\n{new System.Diagnostics.StackTrace(1, false)}");
        }

        private static bool inOwnCollect;

        public static void ApplyPatches(HarmonyLib.Harmony h)
        {
            foreach (var m in typeof(System.GC).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != "Collect") continue;
                try { h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(GcPolicy), nameof(ForeignPrefix))); }
                catch (System.Exception e) { Log.Warn("gc: can't watch GC.Collect: " + e.Message); }
            }
            try { h.Patch(HarmonyLib.AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets)), prefix: new HarmonyLib.HarmonyMethod(typeof(GcPolicy), nameof(ForeignPrefix))); }
            catch (System.Exception e) { Log.Warn("gc: can't watch UnloadUnusedAssets: " + e.Message); }
        }

        public static string Report() => Active
            ? $"gc: manual, {Collections} collections (last {LastPauseMs:F0} ms, longest {MaxPauseMs:F0} ms), {Unexpected} not made by the mod, {Grown / MB} MB since the last"
            : "gc: automatic";
    }
}
