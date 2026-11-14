using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace DivergenceProbe
{
    /// <summary>
    /// One run: wait for the save to finish loading, settle paused, apply the variant, unpause at a fixed
    /// speed, record ticksPerRun real sim ticks, pause, write results, then move on to the next run
    /// (restarting the game for a fresh process if configured).
    /// </summary>
    public sealed class ProbeController : MonoBehaviour
    {
        private Recorder rec;
        private string phase = "loading";
        private int unexpectedPauses;
        private GUIStyle style;

        private void Start() => StartCoroutine(Run());

        private void OnDestroy()
        {
            if (Recorder.Active == rec) Recorder.Active = null;
            Time.captureDeltaTime = 0f;
        }

        private IEnumerator Run()
        {
            ProbeConfig cfg = ProbeMod.Config;
            ProbeState st = ProbeMod.State;
            int run = ProbeMod.CurrentRun;
            string variant = ProbeMod.CurrentVariant;

            while (Game.Instance == null || Game.Instance.IsLoading() || SpeedControlScreen.Instance == null)
                yield return null;

            phase = "settling";
            if (!SpeedControlScreen.Instance.IsPaused)
            {
                Log.Warn("game was not paused after load; pausing");
                SpeedControlScreen.Instance.Pause(playSound: false);
            }
            for (int i = 0; i < cfg.settleFrames; i++)
                yield return null;

            try
            {
                rec = new Recorder(Path.Combine(ProbeMod.SessionDir, $"run{run}_{variant}"), run, variant, cfg);
                rec.Begin(); // records the pre-tick state (tick -1) and a snapshot of it
                if (variant != "natural")
                    UnityEngine.Random.InitState(cfg.seed);
                if (variant == "fixeddt")
                    Time.captureDeltaTime = 1f / 60f;
                Recorder.Active = rec;
                SpeedControlScreen.Instance.SetSpeed(cfg.gameSpeed);
                SpeedControlScreen.Instance.Unpause(playSound: false);
                Log.Info($"run {run} ({variant}) recording {cfg.ticksPerRun} ticks");
            }
            catch (Exception e)
            {
                ProbeController.Fail($"run {run} could not start: {e}");
                yield break;
            }

            phase = "recording";
            float start = Time.realtimeSinceStartup;
            while (rec.Ticks < cfg.ticksPerRun && rec.Error == null)
            {
                if (Time.realtimeSinceStartup - start > 20f * 60f)
                {
                    rec.Error = "timeout after 20 minutes";
                    break;
                }
                if (SpeedControlScreen.Instance.IsPaused)
                {
                    unexpectedPauses++;
                    SpeedControlScreen.Instance.Unpause(playSound: false);
                }
                yield return null;
            }

            phase = "writing";
            Recorder.Active = null;
            SpeedControlScreen.Instance.Pause(playSound: false);
            Time.captureDeltaTime = 0f;
            var finish = rec.Finish(unexpectedPauses);
            while (!finish.IsCompleted)
                yield return null;

            if (rec.Error != null)
            {
                Fail($"run {run} ({variant}) failed: {rec.Error}");
                yield break;
            }
            Log.Info($"run {run} ({variant}) done: {rec.Ticks} ticks, results in '{rec.Dir}'");
            st.nextRun++;
            st.attempts = 0;
            st.Save();
            phase = "next";
            yield return new WaitForSecondsRealtime(1f);
            LoadNextRunOrFinish(fromMenu: false);
        }

        private void OnGUI()
        {
            if (style == null)
                style = new GUIStyle(GUI.skin.box) { fontSize = 22, alignment = TextAnchor.MiddleLeft };
            ProbeConfig cfg = ProbeMod.Config;
            ProbeState st = ProbeMod.State;
            int total = cfg.RunList().Count;
            string text;
            if (st.status == "done")
                text = " Divergence probe DONE - all runs finished. You can quit the game and disable the mod.";
            else if (st.status == "failed")
                text = " Divergence probe FAILED: " + st.message;
            else
                text = $" Divergence probe  run {ProbeMod.CurrentRun + 1}/{total} ({ProbeMod.CurrentVariant})  {phase}" +
                       (rec != null ? $"  tick {rec.Ticks}/{cfg.ticksPerRun}" : "") + "  - please don't touch";
            GUI.Box(new Rect(20, 20, 1100, 44), text, style);
        }

        // ---------------------------------------------------------------- sequencing (static)

        public static void LoadNextRunOrFinish(bool fromMenu)
        {
            ProbeConfig cfg = ProbeMod.Config;
            ProbeState st = ProbeMod.State;
            var runs = cfg.RunList();

            if (st.nextRun >= runs.Count)
            {
                st.status = "done";
                st.message = "all runs completed";
                st.Save();
                Log.Info($"SESSION DONE: {runs.Count} runs in '{ProbeMod.SessionDir}'. Delete state.json to run again.");
                return;
            }
            if (string.IsNullOrEmpty(cfg.savePath) || !File.Exists(cfg.savePath))
            {
                Fail($"savePath '{cfg.savePath}' does not exist; set it in config.json");
                return;
            }
            if (!fromMenu && cfg.freshProcessPerRun)
            {
                Log.Info($"restarting the game for run {st.nextRun} (fresh process)");
                App.instance.Restart();
                return;
            }

            st.attempts++;
            if (st.attempts > 2)
            {
                Fail($"run {st.nextRun} was started twice without finishing (crash?)");
                return;
            }
            st.Save();

            ProbeMod.CurrentRun = st.nextRun;
            ProbeMod.CurrentVariant = runs[st.nextRun];
            try
            {
                Directory.CreateDirectory(ProbeMod.SessionDir);
                string cfgCopy = Path.Combine(ProbeMod.SessionDir, "config.json");
                if (!File.Exists(cfgCopy))
                    File.WriteAllText(cfgCopy, JsonUtility.ToJson(cfg, true));
            }
            catch (Exception e)
            {
                Log.Warn("could not create session dir: " + e.Message);
            }
            Log.Info($"starting run {st.nextRun}/{runs.Count} ({ProbeMod.CurrentVariant}), attempt {st.attempts}, loading '{cfg.savePath}'");
            LoadScreen.DoLoad(cfg.savePath);
        }

        public static void Fail(string message)
        {
            ProbeMod.State.status = "failed";
            ProbeMod.State.message = message;
            ProbeMod.State.Save();
            Recorder.Active = null;
            Time.captureDeltaTime = 0f;
            Log.Warn("SESSION FAILED: " + message);
        }
    }
}
