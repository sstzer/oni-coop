namespace OniCoop
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using HarmonyLib;

    /// <summary>-coop-profile: how long each part of a save takes (the server freezes for all of it).</summary>
    internal static class SaveTiming
    {
        public static void Apply(Harmony h)
        {
            Time(h, AccessTools.Method(typeof(SaveLoader), "Save", new[] { typeof(BinaryWriter) }), "serialize the world");
            Time(h, AccessTools.Method(typeof(SaveLoader), "CompressContents"), "compress + write");
            Time(h, AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.GetSaveFiles), new[] { typeof(string), typeof(bool), typeof(SearchOption) }), "list old autosaves");
            Time(h, AccessTools.Method(typeof(RetireColonyUtility), "SaveColonySummaryData"), "colony summary");
            Time(h, AccessTools.Method(typeof(SaveLoader), "ReportSaveMetrics"), "save metrics");
            Time(h, AccessTools.Method(typeof(Timelapser), nameof(Timelapser.SaveColonyPreview)), "colony preview picture");
            Time(h, AccessTools.Method(typeof(SaveLoader), "PrepSaveFile"), "  save file root");
            Time(h, AccessTools.Method(typeof(Game), nameof(Game.SaveSettings)), "  game settings");
            Time(h, AccessTools.Method(typeof(Sim), nameof(Sim.Save)), "  simulation grid (native)");
            Time(h, AccessTools.Method(typeof(SaveManager), nameof(SaveManager.Save)), "  all objects");
            Time(h, AccessTools.Method(typeof(Game), nameof(Game.Save), new[] { typeof(BinaryWriter) }), "  game state");
            // inside "all objects": how much is the per-field reflection (KSerialization) and how much the rest
            try
            {
                h.Patch(AccessTools.Method(typeof(KSerialization.SerializationTemplate), nameof(KSerialization.SerializationTemplate.SerializeData)),
                    prefix: new HarmonyMethod(typeof(SaveTiming), nameof(DataPre)), postfix: new HarmonyMethod(typeof(SaveTiming), nameof(DataPost)));
                h.Patch(AccessTools.Method(typeof(SaveLoadRoot), nameof(SaveLoadRoot.SaveWithoutTransform)),
                    prefix: new HarmonyMethod(typeof(SaveTiming), nameof(ObjPre)), postfix: new HarmonyMethod(typeof(SaveTiming), nameof(ObjPost)));
            }
            catch (Exception e) { Log.Warn("save timing: " + e.Message); }
            Time(h, AccessTools.Method(typeof(SaveLoader), nameof(SaveLoader.Save), new[] { typeof(string), typeof(bool), typeof(bool) }), "whole save");
        }

        private static void Time(Harmony h, System.Reflection.MethodBase m, string what)
        {
            if (m == null) { Log.Warn("save timing: not found: " + what); return; }
            names[m] = what;
            try { h.Patch(m, prefix: new HarmonyMethod(typeof(SaveTiming), nameof(Pre)), postfix: new HarmonyMethod(typeof(SaveTiming), nameof(Post))); }
            catch (Exception e) { Log.Warn($"save timing: {what}: {e.Message}"); }
        }

        private static readonly System.Collections.Generic.Dictionary<System.Reflection.MethodBase, string> names =
            new System.Collections.Generic.Dictionary<System.Reflection.MethodBase, string>();

        private static long dataTicks, dataCalls, objTicks, objCalls;
        private static int depth;

        private static void DataPre(out long __state) { __state = Stopwatch.GetTimestamp(); depth++; }
        private static void DataPost(long __state) { depth--; if (depth == 0) dataTicks += Stopwatch.GetTimestamp() - __state; dataCalls++; }
        private static void ObjPre(out long __state) => __state = Stopwatch.GetTimestamp();
        private static void ObjPost(long __state) { objTicks += Stopwatch.GetTimestamp() - __state; objCalls++; }

        private static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Post(long __state, System.Reflection.MethodBase __originalMethod)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            if (ms > 20 && names.TryGetValue(__originalMethod, out string what)) Log.Info($"save timing: {what} {ms:F0} ms");
            if (names.TryGetValue(__originalMethod, out string w2) && w2 == "  all objects")
            {
                double f = 1000.0 / Stopwatch.Frequency;
                Log.Info($"save timing:    {objCalls} objects in {objTicks * f:F0} ms, of which {dataCalls} component/field templates written in {dataTicks * f:F0} ms (reflection: field values, callbacks)");
                dataTicks = dataCalls = objTicks = objCalls = 0;
            }
        }
    }
}
