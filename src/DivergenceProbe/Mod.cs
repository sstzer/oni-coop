using System.Collections;
using System.IO;
using HarmonyLib;
using KMod;
using UnityEngine;

namespace DivergenceProbe
{
    public sealed class ProbeMod : UserMod2
    {
        public static string ModPath { get; private set; }
        public static ProbeConfig Config { get; private set; }
        public static ProbeState State { get; private set; }

        /// <summary>The run being executed in this process, or null.</summary>
        public static string CurrentVariant;
        public static int CurrentRun = -1;

        /// <summary>True while a session is in progress (the menu auto-starts the next run).</summary>
        public static bool SessionActive => Config != null && Config.autoRun && State != null && State.status == "running";

        /// <summary>True only inside a game the probe itself loaded: saving is blocked there and nowhere else.</summary>
        public static bool InProbeRun => SessionActive && CurrentVariant != null;

        public override void OnLoad(Harmony harmony)
        {
            ModPath = path;
            Config = ProbeConfig.LoadOrCreate(Path.Combine(path, "config.json"));
            if (Config.autoRun)
                State = ProbeState.LoadOrNew(Path.Combine(path, "state.json"));
            Log.Info($"loaded v{assembly.GetName().Version}; autoRun={Config.autoRun} " +
                     (State != null ? $"session={State.sessionId} status={State.status} nextRun={State.nextRun}/{Config.RunList().Count}" : ""));
            base.OnLoad(harmony);
        }

        public static string SessionDir =>
            Path.Combine(Path.Combine(ModPath, "results"), "session_" + State.sessionId);
    }

    internal static class Log
    {
        private const string Prefix = "[Divergence] ";
        public static void Info(string msg) => global::Debug.Log(Prefix + msg);
        public static void Warn(string msg) => global::Debug.LogWarning(Prefix + msg);
    }

    // ------------------------------------------------------------------ main menu: start the next run

    [HarmonyPatch(typeof(MainMenu), "OnSpawn")]
    internal static class MainMenu_OnSpawn_Patch
    {
        private static void Postfix(MainMenu __instance)
        {
            if (!ProbeMod.SessionActive)
                return;
            __instance.StartCoroutine(StartNextRun());
        }

        private static IEnumerator StartNextRun()
        {
            yield return new WaitForSecondsRealtime(3f); // let the menu (and any popups) settle
            ProbeController.LoadNextRunOrFinish(fromMenu: true);
        }
    }

    // ------------------------------------------------------------------ in game: attach the controller

    [HarmonyPatch(typeof(Game), "OnSpawn")]
    internal static class Game_OnSpawn_Patch
    {
        private static void Postfix(Game __instance)
        {
            if (ProbeMod.SessionActive && ProbeMod.CurrentVariant != null)
                __instance.gameObject.AddComponent<ProbeController>();
        }
    }

    // ------------------------------------------------------------------ seeding at load start

    [HarmonyPatch(typeof(SaveLoader), nameof(SaveLoader.Load), typeof(string))]
    internal static class SaveLoader_Load_Patch
    {
        private static void Prefix()
        {
            if (ProbeMod.SessionActive && ProbeMod.CurrentVariant != null && ProbeMod.CurrentVariant != "natural")
            {
                UnityEngine.Random.InitState(ProbeMod.Config.seed);
                Log.Info($"seeded UnityEngine.Random ({ProbeMod.Config.seed}) at SaveLoader.Load");
            }
        }
    }

    // ------------------------------------------------------------------ the tick hook

    /// <summary>Game.SimEveryTick calls UnsafeSim200ms(0.2) once per 12 sub-ticks (a real 200 ms tick)
    /// and UnsafeSim200ms(0) on frames shorter than 1/60 s. Record after every real tick.</summary>
    [HarmonyPatch(typeof(Game), "UnsafeSim200ms")]
    internal static class Game_UnsafeSim200ms_Patch
    {
        private static void Postfix(float dt)
        {
            Recorder r = Recorder.Active;
            if (r == null) return;
            if (dt > 0f) r.OnTick();
            else r.ZeroDtCalls++;
        }
    }

    // ------------------------------------------------------------------ never write saves during a session

    [HarmonyPatch(typeof(GameClock), "DoAutoSave")]
    internal static class GameClock_DoAutoSave_Patch
    {
        private static bool Prefix()
        {
            if (!ProbeMod.InProbeRun) return true;
            Log.Info("autosave skipped (probe run)");
            return false;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.StartDelayedSave))]
    internal static class Game_StartDelayedSave_Patch
    {
        private static bool Prefix(string filename)
        {
            if (!ProbeMod.InProbeRun) return true;
            Log.Info($"save to '{filename}' skipped (probe run)");
            return false;
        }
    }
}
