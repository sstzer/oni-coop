using System.IO;
using HarmonyLib;
using KMod;

namespace MultiViewExperiment
{
    public sealed class MultiViewMod : UserMod2
    {
        public static string ModPath { get; private set; }
        public static ExperimentConfig Config { get; private set; }

        public override void OnLoad(Harmony harmony)
        {
            ModPath = path;
            Config = ExperimentConfig.LoadOrCreate(Path.Combine(path, "config.json"));
            Log.Info($"loaded v{assembly.GetName().Version} from '{path}', enabled={Config.enabled}");
            base.OnLoad(harmony); // PatchAll on this assembly
        }
    }

    internal static class Log
    {
        private const string Prefix = "[MultiView] ";
        public static void Info(string msg) => global::Debug.Log(Prefix + msg);
        public static void Warn(string msg) => global::Debug.LogWarning(Prefix + msg);
        public static void Error(string msg) => global::Debug.LogWarning(Prefix + "ERROR " + msg);
    }

    /// <summary>Attach the experiment once a colony has spawned.</summary>
    [HarmonyPatch(typeof(Game), "OnSpawn")]
    internal static class Game_OnSpawn_Patch
    {
        private static void Postfix(Game __instance)
        {
            if (MultiViewMod.Config == null || !MultiViewMod.Config.enabled)
                return;
            if (__instance.gameObject.GetComponent<ExperimentController>() == null)
                __instance.gameObject.AddComponent<ExperimentController>();
        }
    }

    /// <summary>
    /// While ForceFullWorld is set, pretend a timelapse screenshot is being taken. That makes
    /// World.LateUpdate / BlockTileRenderer / PropertyTextures take their official "whole world" branch.
    /// </summary>
    [HarmonyPatch(typeof(GameUtil), nameof(GameUtil.IsCapturingTimeLapse))]
    internal static class GameUtil_IsCapturingTimeLapse_Patch
    {
        private static void Postfix(ref bool __result)
        {
            if (ExperimentController.ForceFullWorld)
                __result = true;
        }
    }

    /// <summary>Counts RenderAll calls so the log proves the forced branch actually ran.</summary>
    [HarmonyPatch(typeof(GroundRenderer), nameof(GroundRenderer.RenderAll))]
    internal static class GroundRenderer_RenderAll_Patch
    {
        private static void Postfix() => ExperimentController.GroundRenderAllCalls++;
    }

}
