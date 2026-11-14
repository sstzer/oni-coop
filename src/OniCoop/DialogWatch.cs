namespace OniCoop
{
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using HarmonyLib;
    using UnityEngine;

    /// <summary>Test aid: pop-ups only exist on screen, so whoever reads the log can't see them. Write every dialog
    /// (type + all its text) to the log, and in test mode (screenshots on) also save a picture of it.</summary>
    internal static class DialogWatch
    {
        private static int shots;

        public static void Shown(KScreen screen)
        {
            if (CoopMod.Settings == null || !CoopMod.Settings.Enabled) return;
            if (CoopRuntime.Instance != null) CoopRuntime.Instance.StartCoroutine(Report(screen));
            else Log.Warn("dialog: " + screen.GetType().Name);
        }

        /// <summary>Crash/error dialog of the game's crash reporter (also errors deferred during loading).</summary>
        public static void CrashDialog(string error, string stack)
        {
            Log.Warn($"dialog: crash reporter: {error}\n{stack}");
            if (CoopRuntime.Instance != null) CoopRuntime.Instance.StartCoroutine(Shot("crash"));
        }

        private static IEnumerator Report(KScreen screen)
        {
            // Dialogs are shown first and filled in right after (PopupConfirmDialog sets the text after activation).
            yield return null;
            yield return null;
            if (screen == null) yield break;
            string type = screen.GetType().Name;
            bool isDialog = screen is ConfirmDialogScreen || screen is InfoDialogScreen || type.Contains("Dialog");
            if (!isDialog)
            {
                Log.Info("screen opened: " + type); // e.g. ResearchScreen opened by the player: name only
                yield break;
            }
            Log.Warn($"dialog: {type}: {Texts(screen.gameObject)}");
            yield return Shot(type);
        }

        private static IEnumerator Shot(string what)
        {
            CoopSettings s = CoopMod.Settings;
            if (s == null || s.screenshotEvery <= 0 || shots >= 20 || Application.isBatchMode) yield break;
            yield return null; // let the dialog render
            string dir = Path.Combine(GameBridge.CoopDir, "shots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{s.name}_dialog{shots++:00}_{what}.png");
            ScreenCapture.CaptureScreenshot(file);
            Log.Info("dialog screenshot " + file);
        }

        private static string Texts(GameObject go)
        {
            var parts = new List<string>();
            foreach (TMPro.TMP_Text t in go.GetComponentsInChildren<TMPro.TMP_Text>(false))
            {
                string x = t.text;
                if (string.IsNullOrEmpty(x)) continue;
                x = x.Replace("\r", "").Replace('\n', ' ').Trim();
                if (x.Length > 0 && !parts.Contains(x)) parts.Add(x);
            }
            string all = string.Join(" | ", parts.ToArray());
            return all.Length > 800 ? all.Substring(0, 800) + "…" : all;
        }
    }

    [HarmonyPatch(typeof(KModalScreen), "OnShow")]
    internal static class KModalScreen_OnShow_Patch
    {
        // KModalScreen.OnShow(true) runs once per opening (ConfirmDialogScreen, InfoDialogScreen and the rest call it).
        private static void Postfix(KModalScreen __instance, bool show)
        {
            if (show && Application.isBatchMode) CoopRuntime.OpenedModals.Add(__instance); // the headless server answers it
            if (show) DialogWatch.Shown(__instance);
        }
    }

    [HarmonyPatch(typeof(KModalScreen), "OnCmpEnable")]
    internal static class KModalScreen_OnCmpEnable_Patch
    {
        private static void Postfix(KModalScreen __instance)
        {
            if (Application.isBatchMode) CoopRuntime.OpenedModals.Add(__instance);
        }
    }

    [HarmonyPatch(typeof(KCrashReporter), nameof(KCrashReporter.ShowDialog))]
    internal static class KCrashReporter_ShowDialog_Patch
    {
        private static void Prefix(string error, string stack_trace) => DialogWatch.CrashDialog(error, stack_trace);
    }
}
