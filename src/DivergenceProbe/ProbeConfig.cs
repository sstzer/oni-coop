using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DivergenceProbe
{
    /// <summary>config.json next to the DLL (user-editable).</summary>
    [Serializable]
    public sealed class ProbeConfig
    {
        /// <summary>Master switch. When false the mod does nothing at all.</summary>
        public bool autoRun = true;

        /// <summary>Absolute path of the .sav to test (forward slashes are fine). Never written to.</summary>
        public string savePath = "";

        /// <summary>Comma-separated run list. Consecutive runs of the same variant are compared.
        /// natural = untouched; seeded = UnityEngine.Random seeded at load start and at unpause;
        /// fixeddt = seeded + Time.captureDeltaTime = 1/60 (one sim sub-tick per rendered frame).</summary>
        public string runs = "natural,natural,seeded,seeded,fixeddt,fixeddt";

        public int seed = 12345;

        /// <summary>Real 200 ms sim ticks to record per run (300 = 60 s of game time at speed 1).</summary>
        public int ticksPerRun = 300;

        /// <summary>Frames to wait (paused) after the save finished loading, before unpausing.</summary>
        public int settleFrames = 120;

        /// <summary>Write a full grid snapshot every N ticks (plus tick 0 and the last tick).</summary>
        public int snapshotEvery = 50;

        /// <summary>SpeedControlScreen speed index: 0 = normal, 1 = fast, 2 = ultra.</summary>
        public int gameSpeed = 0;

        /// <summary>Restart the game between runs (each run = fresh process, like a different machine).
        /// false = reload the save inside the same process.</summary>
        public bool freshProcessPerRun = true;

        public List<string> RunList()
        {
            var list = new List<string>();
            foreach (string s in runs.Split(','))
                if (s.Trim().Length > 0) list.Add(s.Trim().ToLowerInvariant());
            return list;
        }

        public static ProbeConfig LoadOrCreate(string file)
        {
            var cfg = new ProbeConfig();
            try
            {
                if (File.Exists(file))
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(file), cfg);
                File.WriteAllText(file, JsonUtility.ToJson(cfg, true));
            }
            catch (Exception e)
            {
                Log.Warn($"config '{file}' unreadable, using defaults: {e.Message}");
            }
            return cfg;
        }
    }

    /// <summary>state.json: progress through the run list, survives game restarts.
    /// Delete it to start a new session.</summary>
    [Serializable]
    public sealed class ProbeState
    {
        public string sessionId = "";
        public string status = "running"; // running | done | failed
        public int nextRun;
        public int attempts;              // attempts of nextRun; >2 means it keeps crashing
        public string message = "";

        [NonSerialized] public string file;

        public static ProbeState LoadOrNew(string file)
        {
            ProbeState s = null;
            try
            {
                if (File.Exists(file))
                {
                    s = new ProbeState();
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(file), s);
                }
            }
            catch (Exception e)
            {
                Log.Warn($"state '{file}' unreadable, starting a new session: {e.Message}");
                s = null;
            }
            if (s == null || string.IsNullOrEmpty(s.sessionId))
            {
                s = new ProbeState
                {
                    sessionId = System.DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                    status = "running",
                };
            }
            s.file = file;
            return s;
        }

        public void Save()
        {
            try { File.WriteAllText(file, JsonUtility.ToJson(this, true)); }
            catch (Exception e) { Log.Warn("could not write state.json: " + e.Message); }
        }
    }
}
