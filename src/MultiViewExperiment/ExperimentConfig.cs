using System;
using System.IO;
using UnityEngine;

namespace MultiViewExperiment
{
    /// <summary>config.json next to the DLL. Written with defaults on first load.</summary>
    [Serializable]
    public sealed class ExperimentConfig
    {
        /// <summary>Bump when defaults change meaning; an older config.json is then replaced with defaults.</summary>
        public int configVersion = CurrentVersion;
        public const int CurrentVersion = 2;

        public bool enabled = true;

        /// <summary>Seconds after the save finished loading before the first capture.</summary>
        public float startDelaySeconds = 8f;

        /// <summary>Seconds between capture sets.</summary>
        public float intervalSeconds = 5f;

        /// <summary>Delay between the "_a" and "_b" frames of a set (animation check).</summary>
        public float burstDelaySeconds = 0.5f;

        /// <summary>Stop after this many sets.</summary>
        public int maxSets = 10;

        /// <summary>Width of every PNG; height follows the screen aspect.</summary>
        public int captureWidth = 960;

        /// <summary>"telepad" = Printing Pod (known content: pod, dupes). "auto" = Printing Pod if it is
        /// outside the main view, else a region beside it. "fixed" = fixedX/fixedY (cells).</summary>
        public string target = "telepad";
        public float fixedX = 0f;
        public float fixedY = 0f;

        /// <summary>Orthographic half-height of the extra view, in cells. 0 = same zoom as the main camera
        /// (so the "truth" screenshot lines up with it).</summary>
        public float offOrthoSize = 0f;

        /// <summary>Extra cells between the main view and an "auto" region.</summary>
        public float separationMargin = 8f;

        /// <summary>Register the extra view with every culling system (ground, building tiles, KAnim
        /// visibility + batches, property textures). This is the iteration-2 fix under test.</summary>
        public bool union = true;

        /// <summary>Also shoot off_nounion: the same region with the union patches off for two frames.</summary>
        public bool nounionCheck = true;

        /// <summary>Render liquids in a second pass over the extra camera's own MRT copy.</summary>
        public bool twoPassLiquid = true;

        /// <summary>For the first N sets, also render the extra view one layer group at a time.</summary>
        public int layerBreakdownSets = 2;

        /// <summary>Ground truth: snap the MAIN camera onto the extra region for one frame and screenshot
        /// it (full pipeline). Causes a one-frame flash every set.</summary>
        public bool truthCapture = true;

        /// <summary>Iteration-1 check: one frame through the official timelapse "whole world" branch.</summary>
        public bool forcedPass = false;

        public static ExperimentConfig LoadOrCreate(string file)
        {
            var cfg = new ExperimentConfig();
            try
            {
                if (File.Exists(file))
                {
                    var loaded = new ExperimentConfig { configVersion = 0 };
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(file), loaded);
                    if (loaded.configVersion == CurrentVersion)
                        cfg = loaded;
                    else
                        Log.Info($"config.json is version {loaded.configVersion}, replacing with v{CurrentVersion} defaults");
                }
                File.WriteAllText(file, JsonUtility.ToJson(cfg, true));
            }
            catch (Exception e)
            {
                Log.Warn($"config '{file}' unreadable, using defaults: {e.Message}");
            }
            return cfg;
        }
    }
}
