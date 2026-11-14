namespace OniCoop
{
    using System.IO;
    using HarmonyLib;
    using KMod;
    using UnityEngine;

    public sealed class CoopMod : UserMod2
    {
        public const string StaticId = "mzhang.OniCoop";
        public static string ModPath { get; private set; }
        public static string Version { get; private set; }
        public static CoopSettings Settings { get; private set; }
        public static Harmony Harmony { get; private set; }
        private static bool latePatched;

        /// <summary>Patches that touch many game classes wait until the game has loaded its assets: patching a class
        /// can run its static initializer, and some of those read assets (Door's work animation came out null when
        /// patched at mod load, and the first duplicant sent to change a door threw).</summary>
        private static void BeforeSave(string __0) { ServerSpeed.BeforeSave(__0); CoopRuntime.Instance?.OnHostSaving(); }
        private static void AfterSave(string __result, string __0) { ServerSpeed.AfterSave(); CoopRuntime.Instance?.OnHostSaved(__result ?? __0); }

        public static void LatePatches()
        {
            if (latePatched || Harmony == null) return;
            latePatched = true;
            EntitySync.ApplyPatches(Harmony);
            Calls.Apply(Harmony);
            GlobalSyncPatches.Apply(Harmony);
            StatusSync.ApplyPatches(Harmony);
            Puppets.ApplyPatches(Harmony);
            PuppetLooks.ApplyPatches(Harmony);
            OperationSync.ApplyPatches(Harmony);
            BuildingSync.ApplyPatches(Harmony);
            GcPolicy.ApplyPatches(Harmony);
            Feedback.ApplyPatches(Harmony);
            ServerSpeed.ApplyPatches(Harmony);
            FastSave.ApplyPatches(Harmony);
            ReportSync.ApplyPatches(Harmony);
            Printing.ApplyPatches(Harmony);
            try
            {
                Harmony.Patch(AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.Save), new[] { typeof(string), typeof(bool), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(CoopMod), nameof(BeforeSave)), postfix: new HarmonyMethod(typeof(CoopMod), nameof(AfterSave)));
            }
            catch (System.Exception e) { Log.Warn("backup patch failed: " + e.Message); }
            if (Settings.profile > 0) { FrameProfiler.Apply(Harmony, Settings.profile); SaveTiming.Apply(Harmony); if (Settings.profileDeep) FrameProfiler.ApplyDeep(Harmony); }
        }

        public override void OnLoad(Harmony harmony)
        {
            ModPath = path;
            Version = assembly.GetName().Version.ToString(3);
            Settings = CoopSettings.Load(Path.Combine(path, "config.json"));
            Log.Info($"loaded v{Version}: {Settings.Describe()}");
            base.OnLoad(harmony);
            if (!Settings.Enabled) return;
            CommandPatches.Apply(harmony);
            Harmony = harmony; // the rest is patched at the main menu (LatePatches)

            Application.runInBackground = true; // keep simulating and talking to the relay when unfocused
            var go = new GameObject("OniCoop");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<CoopRuntime>();
        }
    }

    internal static class Log
    {
        private const string Prefix = "[Coop] ";
        public static void Info(string msg) => global::Debug.Log(Prefix + msg);
        public static void Warn(string msg) => global::Debug.LogWarning(Prefix + msg);
    }

    [HarmonyPatch(typeof(MainMenu), "OnSpawn")]
    internal static class MainMenu_OnSpawn_Patch
    {
        private static void Postfix()
        {
            CoopMod.LatePatches();
            CoopRuntime.Instance?.OnMainMenu();
        }
    }

    [HarmonyPatch(typeof(Game), "OnSpawn")]
    internal static class Game_OnSpawn_Patch
    {
        private static void Postfix() => CoopRuntime.Instance?.OnGameSpawned();
    }

    /// <summary>A guest plays inside the host's world: never write autosaves of it.</summary>
    [HarmonyPatch(typeof(GameClock), "DoAutoSave")]
    internal static class GameClock_DoAutoSave_Patch
    {
        private static bool Prefix()
        {
            if (CoopMod.Settings == null || !CoopMod.Settings.Enabled || CoopMod.Settings.IsHost) return true;
            Log.Info("autosave skipped (guest)");
            return false;
        }
    }

    /// <summary>The game saves every window resize as the player's graphics setting. While a test window
    /// size is forced, or without a window at all (-batchmode), don't let that overwrite the real settings.</summary>
    [HarmonyPatch]
    internal static class GraphicsOptionsScreen_OnResize_Patch
    {
        // GraphicsOptionsScreen is internal to Assembly-CSharp.
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("GraphicsOptionsScreen"), "OnResize");

        // Also in -batchmode: there is no real window and the game would save a made-up size (seen: 1898x1061 windowed).
        private static bool Prefix() => !Application.isBatchMode &&
            (CoopMod.Settings == null || !CoopMod.Settings.Enabled || !CoopMod.Settings.TryGetWindow(out _, out _, out _, out _));
    }
}

namespace OniCoop
{
    /// <summary>Diagnostics: the game quits through App.QuitCode; log who asked (headless runs quit by themselves).</summary>
    [HarmonyLib.HarmonyPatch(typeof(App), nameof(App.QuitCode))]
    internal static class App_QuitCode_Patch
    {
        private static void Prefix(int exitCode) => Log.Info($"App.QuitCode({exitCode}) called:\n{System.Environment.StackTrace}");
    }
}

namespace OniCoop
{
    /// <summary>Headless (-batchmode -nographics) server: Unity logs graphics errors on the null device (e.g. "This custom
    /// render path shader needs to have at least 1 passes." from the main-menu video). The crash reporter treats any
    /// error as a crash: it pauses the game and opens an (invisible) dialog that quits the game when it is destroyed.
    /// A server should log and keep running instead.</summary>
    [HarmonyLib.HarmonyPatch(typeof(KCrashReporter), "HandleLog")]
    internal static class KCrashReporter_HandleLog_Patch
    {
        private static int ignored;

        private static bool Prefix(string msg, UnityEngine.LogType type)
        {
            if (!UnityEngine.Application.isBatchMode || (type != UnityEngine.LogType.Error && type != UnityEngine.LogType.Exception)) return true;
            if (++ignored <= 50) Log.Warn($"headless: crash reporter skipped for {type}: {msg}");
            return false;
        }
    }
}
