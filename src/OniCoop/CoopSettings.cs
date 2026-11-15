namespace OniCoop
{
    using System;
    using System.IO;
    using UnityEngine;

    /// <summary>config.json next to the DLL; any field can be overridden for one launch with -coop-&lt;field&gt; (see ApplyArgs).</summary>
    [Serializable]
    public sealed class CoopSettings
    {
        /// <summary>off | host | guest. "off" = the mod does nothing.</summary>
        public string role = "off";
        /// <summary>Relay address, host[:port].</summary>
        public string server = "127.0.0.1:47810";
        public string token = "";
        public string room = "default";
        /// <summary>Shown to other players. Empty = Windows user name.</summary>
        public string name = "";

        /// <summary>Guest: duplicants, critters and the visible cells follow the host (step 3). false = old behaviour
        /// (the guest's copy simulates by itself; only orders are shared).</summary>
        public bool mirror = true;
        /// <summary>Guest: reload the host's world every N minutes of play (0 = only with the button).</summary>
        public float resyncEvery = 0;

        // ---- mainly for testing
        /// <summary>Host only: save to load automatically from the main menu (absolute path).</summary>
        public string autoLoad = "";
        /// <summary>Artificial network conditions, e.g. "delay=150,jitter=20,loss=0.05".</summary>
        public string netsim = "";
        /// <summary>"x,y,w,h": windowed at this size and position (not saved to the game's graphics settings).</summary>
        public string window = "";
        /// <summary>Seconds between screenshots while in game (0 = off), saved under coop/shots.</summary>
        public float screenshotEvery = 0;
        public int maxScreenshots = 12;
        /// <summary>Pan the camera around by itself and fake a moving cursor (to see presence without a mouse).</summary>
        public bool wander = false;
        /// <summary>Seconds between status lines in the log while in game (cycle, speed, fps, memory); 0 = off.</summary>
        public float statsEvery = 0;
        /// <summary>Unpause by itself 5 s after entering a game (a server has nobody to press play).</summary>
        public bool unpause = false;
        /// <summary>Host test: 20 s after entering a game, time the game's own save against the coop snapshot.</summary>
        public bool saveBench = false;
        /// <summary>Scripted test: "cmds" = drive the real tools and check both worlds got the orders.</summary>
        public string autotest = "";
        /// <summary>Guest: grow the managed heap by this many MB once after loading (see CoopRuntime.ReserveHeap); 0 = off.</summary>
        public int heapReserveMb = 512; // the server reserves twice this (only without gcManual)

        /// <summary>Guest and server: in a game, the garbage collector runs only when the mod says (GcPolicy).</summary>
        public bool gcManual = true;
        /// <summary>Seconds between frame-time breakdowns in the log (FrameProfiler); 0 = off.</summary>
        public float profile = 0;
        public bool profileDeep;

        /// <summary>Serialize the world with cached type facts and compiled field readers (FastSave): same bytes, faster.</summary>
        public bool fastSave = true;

        /// <summary>Test: the server starts meteor showers during the cmds autotest.</summary>
        public bool testMeteors;
        /// <summary>Quit the game this many seconds after entering a game (0 = never).</summary>
        public float quitAfter = 0;

        public bool Enabled => role == "host" || role == "guest" || role == "server";
        /// <summary>Runs the simulation (the authority): "server" (the dedicated one, nobody at it) or "host" (a player's
        /// game, for development).</summary>
        public bool IsHost => role == "host" || role == "server";
        /// <summary>The dedicated authority: no player, no view, pauses while nobody is in the game.</summary>
        public bool IsServer => role == "server";

        [NonSerialized] public string source = "defaults";

        public static CoopSettings Load(string file)
        {
            var s = new CoopSettings();
            try
            {
                if (File.Exists(file))
                {
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(file), s);
                    s.source = "config.json";
                }
                else File.WriteAllText(file, JsonUtility.ToJson(s, true)); // a template to edit
            }
            catch (Exception e)
            {
                Log.Warn($"config '{file}' unreadable, using defaults: {e.Message}");
            }
            s.ApplyArgs(Environment.GetCommandLineArgs());
            s.role = (s.role ?? "off").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s.name)) s.name = Environment.UserName;
            return s;
        }

        void ApplyArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("-coop-")) continue;
                string key = a.Substring(6);
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("-coop-");
                string v = hasValue ? args[i + 1] : "true";
                switch (key)
                {
                    case "role": role = v; break;
                    case "server": server = v; break;
                    case "token": token = v; break;
                    case "room": room = v; break;
                    case "name": name = v; break;
                    case "load": autoLoad = v; break;
                    case "sim": netsim = v; break;
                    case "window": window = v; break;
                    case "shots": screenshotEvery = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "wander": wander = v != "false"; break;
                    case "stats": statsEvery = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "unpause": unpause = v != "false"; break;
                    case "autotest": autotest = v; break;
                    case "mirror": mirror = v != "false"; break;
                    case "resync-every": resyncEvery = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "savebench": saveBench = v != "false"; break;
                    case "heap-reserve": heapReserveMb = int.Parse(v); break;
                    case "gc-manual": gcManual = v != "false" && v != "0"; break;
                    case "profile": profile = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "profile-deep": profileDeep = v != "false" && v != "0"; break;
                    case "fastsave": fastSave = v != "false" && v != "0"; break;
                    case "test-meteors": testMeteors = v != "false" && v != "0"; break;
                    case "quit-after": quitAfter = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); break;
                    default: Log.Warn($"unknown launch option {a}"); continue;
                }
                source += ", -coop-" + key;
                if (hasValue) i++;
            }
        }

        public bool TryGetWindow(out int x, out int y, out int w, out int h)
        {
            x = y = w = h = 0;
            if (string.IsNullOrEmpty(window)) return false;
            string[] p = window.Split(',');
            return p.Length == 4 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out w) && int.TryParse(p[3], out h) && w > 100 && h > 100;
        }

        public string Describe() =>
            $"role={role} server={server} room={room} name={name} token={(token.Length > 0 ? "set" : "none")}" +
            (autoLoad.Length > 0 ? $" autoLoad={autoLoad}" : "") + (netsim.Length > 0 ? $" netsim={netsim}" : "") +
            (window.Length > 0 ? $" window={window}" : "") + (wander ? " wander" : "") + (screenshotEvery > 0 ? $" shots={screenshotEvery}s" : "") +
            (quitAfter > 0 ? $" quitAfter={quitAfter}s" : "") + $" (from {source})";
    }
}
