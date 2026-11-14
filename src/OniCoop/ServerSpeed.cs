namespace OniCoop
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using HarmonyLib;
    using UnityEngine;
    using Object = UnityEngine.Object;

    /// <summary>The dedicated server (headless) doesn't do work nobody sees, and keeps its saves shorter. Every guest
    /// waits whenever the server's frame is long, so this is everyone's smoothness.
    ///
    /// Saves: the game serializes the world (main thread, must be), then compresses and writes it (0.6-0.8 s on the
    /// laptop) while everybody waits. On the server the compressing and writing happen on a thread of their own after
    /// the save returns; whoever reads the file next (the backup to the relay, the next save) waits for that thread.
    ///
    /// Rendering: no picture is ever made on the server. The world textures (gas, liquid, temperature...) and the
    /// drawing of the ground, the animation batches and falling water are skipped; the parts of the same functions the
    /// game's logic relies on (which animation batches are active, falling water's particles, revealed cells) still run.</summary>
    internal static class ServerSpeed
    {
        public static bool On => CoopMod.Settings != null && CoopMod.Settings.IsServer && Application.isBatchMode;

        // ================================================================== saves

        private static bool inSave;
        private static string saveFile;
        private static byte[] pending;
        private static Thread writer;
        public static long Deferred;
        public static float LastWriteMs;

        public static void BeforeSave(string file)
        {
            if (!On) return;
            WaitWriter();
            inSave = true;
            saveFile = file;
            pending = null;
        }

        /// <summary>The game is about to compress the serialized world into the open save file: keep a copy instead.</summary>
        private static bool CompressPrefix(byte[] uncompressed, int length)
        {
            if (!On || !inSave || string.IsNullOrEmpty(saveFile)) return true;
            pending = new byte[length];
            Buffer.BlockCopy(uncompressed, 0, pending, 0, length);
            return false; // the header and directory are written; the file is closed when Save returns
        }

        /// <summary>The save returned: compress and append the world on a thread of its own.</summary>
        public static void AfterSave()
        {
            if (!On || !inSave) return;
            inSave = false;
            if (pending == null) return;
            byte[] data = pending;
            string file = saveFile;
            pending = null;
            Deferred++;
            // a foreground thread: the process doesn't end with half a save on disk
            writer = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write))
                    using (var z = new Ionic.Zlib.ZlibStream(fs, Ionic.Zlib.CompressionMode.Compress, Ionic.Zlib.CompressionLevel.BestSpeed))
                    {
                        z.Write(data, 0, data.Length);
                        z.Flush();
                    }
                    LastWriteMs = sw.ElapsedMilliseconds;
                    Log.Info($"save: {Path.GetFileName(file)} compressed and written in the background in {LastWriteMs:F0} ms ({data.Length / 1048576.0:F1} MB)");
                }
                catch (Exception e) { Log.Warn($"save: writing {file} in the background failed: {e.Message}"); }
            }) { IsBackground = false, Name = "coop-save-write" };
            writer.Start();
        }

        /// <summary>Before anything reads the last save file (or writes the next one).</summary>
        public static void WaitWriter()
        {
            Thread w = writer;
            if (w != null && w.IsAlive) w.Join();
        }

        // ================================================================== rendering

        private static readonly AccessTools.FieldRef<World, System.Collections.Generic.List<int>> RevealedCells =
            AccessTools.FieldRefAccess<World, System.Collections.Generic.List<int>>("revealedCells");
        public static long SkippedFrames;

        private static bool SkipOnServer() => !On;

        private static float nextCanvasSweep;
        public static int CanvasesOff;

        /// <summary>Every frame on the server: canvases (the whole user interface) don't lay out or draw. New ones (screens
        /// open, progress bars...) are switched off every few seconds.</summary>
        public static void Tick(float now)
        {
            if (!On || now < nextCanvasSweep) return;
            nextCanvasSweep = now + 5f;
            foreach (Canvas c in Object.FindObjectsOfType<Canvas>())
                if (c != null && c.enabled) { c.enabled = false; CanvasesOff++; }
        }

        private static bool PropertyTexturesPrefix() => !On;

        /// <summary>World.LateUpdate without the drawing.</summary>
        private static bool WorldLateUpdatePrefix(World __instance)
        {
            if (!On) return true;
            if (Game.IsQuitting()) return false;
            SkippedFrames++;
            Singleton<KBatchedAnimUpdater>.Instance.GetVisibleArea(out Vector2I min, out Vector2I max);
            KAnimBatchManager.Instance().UpdateActiveArea(min, max);
            KAnimBatchManager.Instance().UpdateDirty(Time.frameCount);
            FallingWater.instance.UpdateParticles(Time.deltaTime);
            System.Collections.Generic.List<int> revealed = RevealedCells(__instance);
            if (revealed != null && revealed.Count > 0)
            {
                GameScenePartitioner.Instance.TriggerEvent(revealed, GameScenePartitioner.Instance.fogOfWarChangedLayer, null);
                revealed.Clear();
            }
            return false;
        }

        public static void ApplyPatches(Harmony h)
        {
            if (!(CoopMod.Settings != null && CoopMod.Settings.IsServer)) return; // (nothing to change elsewhere)
            void P(System.Reflection.MethodBase m, string prefix)
            {
                try
                {
                    if (m == null) throw new Exception("not found");
                    h.Patch(m, prefix: new HarmonyMethod(typeof(ServerSpeed), prefix));
                }
                catch (Exception e) { Log.Warn($"server speed: can't patch {m?.DeclaringType?.Name}.{m?.Name}: {e.Message}"); }
            }
            P(AccessTools.Method(typeof(SaveLoader), "CompressContents"), nameof(CompressPrefix));
            // the user interface: nobody looks at the server's (hover checks, toggles, scroll views, graph highlights)
            P(AccessTools.Method(typeof(InterfaceTool), "LateUpdate"), nameof(SkipOnServer));
            P(AccessTools.Method(typeof(MultiToggle), "Update"), nameof(SkipOnServer));
            P(AccessTools.Method(typeof(KScrollRect), "LateUpdate"), nameof(SkipOnServer));
            P(AccessTools.Method(typeof(UnityEngine.UI.ScrollRect), "LateUpdate"), nameof(SkipOnServer));
            P(AccessTools.Method(typeof(LineLayer), "Update"), nameof(SkipOnServer));
            P(AccessTools.Method(typeof(AlternateSiblingColor), "Update"), nameof(SkipOnServer));
            P(AccessTools.Method(typeof(PropertyTextures), "LateUpdate"), nameof(PropertyTexturesPrefix));
            P(AccessTools.Method(typeof(World), "LateUpdate"), nameof(WorldLateUpdatePrefix));
        }
    }
}
