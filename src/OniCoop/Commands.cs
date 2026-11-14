namespace OniCoop
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Roadmap step 2: player orders go through the host.
    ///
    /// Every order (a drag of the dig tool, placing a building, a pipe path, ...) is captured right where the game's
    /// tool would execute it. On a guest the local execution is skipped and the order goes to the host; the host
    /// executes it (with the guest's priority and tool filters) and broadcasts it; every guest then replays it, so
    /// everybody's world gets the same orders. The host's own orders are executed locally and broadcast the same way.
    /// Replays call the same game code the tool would have called.</summary>
    internal static class CommandBus
    {
        // relay channels
        public const byte ChCmdRequest = 1;  // guest → host
        public const byte ChCmdApplied = 2;  // host → everybody
        public const byte ChSpeedState = 3;  // host → everybody
        public const byte ChSpeedRequest = 4; // guest → host

        public enum Kind : byte { DragCells = 1, DragBox = 2, Build = 3, Utility = 4 }

        /// <summary>True while executing a replayed order: the interception patches let the game code run.</summary>
        public static bool Replaying;
        public static PrioritySetting? PriorityOverride;

        public static CoopClient Client;
        public static bool IsHost, InGame;
        public static bool Active => Client != null && Client.Welcomed && InGame;
        public static bool IsGuest => Active && !IsHost;

        public static int Sent, Executed, Replayed;

        // ------------------------------------------------------------------ command model

        public sealed class Toggle { public string field, name; public byte state; public bool inclusive; }

        public sealed class Command
        {
            public Kind kind;
            public ushort issuer;
            public string tool = "";      // DragCells / DragBox / Utility: tool type name
            public string def = "";       // Build / Utility: BuildingDef.PrefabID
            public bool hasPriority;
            public byte prioClass;
            public int prioValue;
            public List<Toggle> toggles = new List<Toggle>();
            public List<int> cells = new List<int>();
            public List<int> extra = new List<int>(); // DragCells: distFromOrigin; Utility: valid (0/1)
            public Vector3 down, up;      // DragBox
            public int orientation;
            public List<string> elements = new List<string>();
            public string facade = "";
            /// <summary>IDs of the saveable objects the host's execution created, in order (EntitySync ID script).</summary>
            public List<int> ids = new List<int>();

            public string Describe() =>
                kind == Kind.Build ? $"build {def} at {cells[0]} ({string.Join(",", elements.ToArray())})"
                : kind == Kind.Utility ? $"{tool} {def} path of {cells.Count}"
                : kind == Kind.DragBox ? $"{tool} box ({down.x:F0},{down.y:F0})-({up.x:F0},{up.y:F0})"
                : $"{tool} x{cells.Count}";

            public byte[] Encode() => Wire.Build(w =>
            {
                w.Write((byte)kind); w.Write(issuer); w.Write(tool); w.Write(def);
                w.Write(hasPriority); w.Write(prioClass); w.Write(prioValue);
                w.Write((byte)toggles.Count);
                foreach (Toggle t in toggles) { w.Write(t.field); w.Write(t.name); w.Write(t.state); w.Write(t.inclusive); }
                w.Write(cells.Count);
                for (int i = 0; i < cells.Count; i++) { w.Write(cells[i]); w.Write(i < extra.Count ? extra[i] : 0); }
                w.Write(down.x); w.Write(down.y); w.Write(down.z); w.Write(up.x); w.Write(up.y); w.Write(up.z);
                w.Write(orientation); w.WriteStrings(elements); w.Write(facade ?? "");
                w.Write(ids.Count);
                foreach (int id in ids) w.Write(id);
            });

            public static Command Decode(byte[] p)
            {
                var r = Wire.Reader(p);
                var c = new Command { kind = (Kind)r.ReadByte(), issuer = r.ReadUInt16(), tool = r.ReadString(), def = r.ReadString() };
                c.hasPriority = r.ReadBoolean(); c.prioClass = r.ReadByte(); c.prioValue = r.ReadInt32();
                int nt = r.ReadByte();
                for (int i = 0; i < nt; i++) c.toggles.Add(new Toggle { field = r.ReadString(), name = r.ReadString(), state = r.ReadByte(), inclusive = r.ReadBoolean() });
                int n = r.ReadInt32();
                if (n < 0 || n > 100000) throw new InvalidDataException("bad cell count " + n);
                for (int i = 0; i < n; i++) { c.cells.Add(r.ReadInt32()); c.extra.Add(r.ReadInt32()); }
                c.down = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                c.up = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                c.orientation = r.ReadInt32(); c.elements = r.ReadStrings(); c.facade = r.ReadString();
                int ni = r.ReadInt32();
                if (ni < 0 || ni > 100000) throw new InvalidDataException("bad id count " + ni);
                for (int i = 0; i < ni; i++) c.ids.Add(r.ReadInt32());
                return c;
            }

            public void SetPriority(PrioritySetting p) { hasPriority = true; prioClass = (byte)p.priority_class; prioValue = p.priority_value; }
            public PrioritySetting Priority => new PrioritySetting((PriorityScreen.PriorityClass)prioClass, prioValue);
        }

        // ------------------------------------------------------------------ capture (issuer side)

        private static readonly Dictionary<string, Command> pendingDrags = new Dictionary<string, Command>();
        private static readonly string[] ToggleFields = { "currentFilters", "options" };

        private static void CaptureToggles(object tool, Command c)
        {
            foreach (string f in ToggleFields)
            {
                FieldInfo fi = AccessTools.Field(tool.GetType(), f);
                if (fi == null || fi.FieldType != typeof(ToolParameterMenu.ToggleData[])) continue;
                var arr = (ToolParameterMenu.ToggleData[])fi.GetValue(tool);
                if (arr == null) continue;
                foreach (var t in arr) c.toggles.Add(new Toggle { field = f, name = t.name, state = (byte)t.state, inclusive = t.isToggleInclusive });
            }
        }

        private static PrioritySetting ToolPriority() =>
            ToolMenu.Instance != null && ToolMenu.Instance.PriorityScreen != null
                ? ToolMenu.Instance.PriorityScreen.GetLastSelectedPriority()
                : new PrioritySetting(PriorityScreen.PriorityClass.basic, 5);

        private static PrioritySetting BuildPriority()
        {
            // the plan screen's priority widget only exists while a building is selected in it
            try { if (PlanScreen.Instance != null) return PlanScreen.Instance.GetBuildingPriority(); }
            catch (NullReferenceException) { }
            return ToolPriority();
        }

        /// <summary>One cell of a drag; batched per tool and sent at the end of the frame.</summary>
        public static Command CaptureDragCell(DragTool tool, int cell, int dist)
        {
            string name = tool.GetType().Name;
            if (!pendingDrags.TryGetValue(name, out Command c))
            {
                c = new Command { kind = Kind.DragCells, tool = name };
                c.SetPriority(ToolPriority());
                CaptureToggles(tool, c);
                pendingDrags[name] = c;
            }
            c.cells.Add(cell);
            c.extra.Add(dist);
            return c;
        }

        public static Command CaptureDragBox(DragTool tool, Vector3 down, Vector3 up)
        {
            var c = new Command { kind = Kind.DragBox, tool = tool.GetType().Name, down = down, up = up };
            c.SetPriority(ToolPriority());
            CaptureToggles(tool, c);
            return c;
        }

        public static Command CaptureBuild(BuildingDef def, int cell, Orientation orientation, IList<Tag> elements, string facade)
        {
            var c = new Command { kind = Kind.Build, def = def.PrefabID, orientation = (int)orientation, facade = facade ?? "" };
            c.cells.Add(cell);
            foreach (Tag t in elements) c.elements.Add(t.Name);
            c.SetPriority(BuildPriority());
            return c;
        }

        /// <summary>End of frame: send the batched drag cells.</summary>
        public static void Flush()
        {
            if (pendingDrags.Count == 0) return;
            foreach (Command c in pendingDrags.Values) Issue(c);
            pendingDrags.Clear();
        }

        /// <summary>Guest: ask the host. Host: it already ran locally, tell everybody.</summary>
        public static void Issue(Command c)
        {
            if (!Active) return;
            Sent++;
            if (IsHost)
            {
                c.issuer = 0;
                Client.SendRelay(Proto.TargetAll, ChCmdApplied, c.Encode());
            }
            else Client.SendRelay(Proto.TargetHost, ChCmdRequest, c.Encode());
            Log.Info($"cmd issued: {c.Describe()}");
        }

        // ------------------------------------------------------------------ receive

        public static void OnRelay(ushort from, byte channel, byte[] data)
        {
            switch (channel)
            {
                case ChCmdRequest when IsHost:
                {
                    Command c = Command.Decode(data);
                    c.issuer = from;
                    string result;
                    var ids = new List<int>();
                    EntitySync.BeginRecord(ids);
                    try { result = Execute(c); }
                    finally { EntitySync.EndRecord(); }
                    c.ids = ids;
                    Executed++;
                    AutoTest.Observe(c);
                    Log.Info($"cmd from #{from}: {c.Describe()} -> {result}");
                    Client.SendRelay(Proto.TargetAll, ChCmdApplied, c.Encode());
                    break;
                }
                case ChCmdApplied when !IsHost:
                {
                    Command c = Command.Decode(data);
                    string result;
                    EntitySync.BeginScript(c.ids); // the objects it makes get the host's IDs
                    try { result = Execute(c); }
                    finally { EntitySync.EndScript(); }
                    Replayed++;
                    AutoTest.Observe(c);
                    Log.Info($"cmd applied (by #{c.issuer}): {c.Describe()} -> {result}, {c.ids.Count} object ids");
                    break;
                }
                case ChSpeedState when !IsHost:
                    SpeedSync.Apply(data);
                    break;
                case ChSpeedRequest when IsHost:
                    SpeedSync.OnRequest(from, data);
                    break;
            }
        }

        // ------------------------------------------------------------------ execute (host, and replay on guests)

        private static InterfaceTool FindTool(string name)
        {
            if (PlayerController.Instance == null) return null;
            foreach (InterfaceTool t in PlayerController.Instance.tools)
                if (t != null && t.GetType().Name == name) return t;
            return null;
        }

        public static string Execute(Command c)
        {
            if (!InGame) return "not in game";
            Replaying = true;
            PriorityOverride = c.hasPriority ? c.Priority : (PrioritySetting?)null;
            try
            {
                switch (c.kind)
                {
                    case Kind.DragCells: return ExecuteDrag(c, box: false);
                    case Kind.DragBox: return ExecuteDrag(c, box: true);
                    case Kind.Build: return ExecuteBuild(c);
                    case Kind.Utility: return ExecuteUtility(c);
                    default: return "unknown kind " + c.kind;
                }
            }
            catch (Exception e)
            {
                Log.Warn($"executing {c.Describe()} failed: {e}");
                return "error: " + e.Message;
            }
            finally
            {
                Replaying = false;
                PriorityOverride = null;
            }
        }

        private static string ExecuteDrag(Command c, bool box)
        {
            InterfaceTool tool = FindTool(c.tool);
            if (tool == null) return "no tool " + c.tool;
            var saved = new List<KeyValuePair<FieldInfo, object>>();
            foreach (string f in ToggleFields)
            {
                FieldInfo fi = AccessTools.Field(tool.GetType(), f);
                if (fi == null || fi.FieldType != typeof(ToolParameterMenu.ToggleData[])) continue;
                var list = new List<ToolParameterMenu.ToggleData>();
                foreach (Toggle t in c.toggles)
                    if (t.field == f)
                        list.Add(new ToolParameterMenu.ToggleData { name = t.name, state = (ToolParameterMenu.ToggleState)t.state, isToggleInclusive = t.inclusive });
                if (list.Count == 0) continue;
                saved.Add(new KeyValuePair<FieldInfo, object>(fi, fi.GetValue(tool)));
                fi.SetValue(tool, list.ToArray());
            }
            try
            {
                if (box)
                {
                    MethodInfo m = AccessTools.Method(tool.GetType(), "OnDragComplete", new[] { typeof(Vector3), typeof(Vector3) });
                    m.Invoke(tool, new object[] { c.down, c.up });
                    return "ok";
                }
                MethodInfo drag = AccessTools.Method(tool.GetType(), "OnDragTool", new[] { typeof(int), typeof(int) });
                int done = 0;
                for (int i = 0; i < c.cells.Count; i++)
                {
                    if (!Grid.IsValidCell(c.cells[i])) continue;
                    drag.Invoke(tool, new object[] { c.cells[i], c.extra[i] });
                    done++;
                }
                return $"{done} cells";
            }
            finally
            {
                foreach (var kv in saved) kv.Key.SetValue(tool, kv.Value);
            }
        }

        private static List<Tag> Tags(List<string> names)
        {
            var list = new List<Tag>();
            foreach (string n in names) list.Add(new Tag(n));
            return list;
        }

        /// <summary>BuildTool.TryBuild without the tool: place the construction order (or a replacement tile).</summary>
        private static string ExecuteBuild(Command c)
        {
            BuildingDef def = Assets.GetBuildingDef(c.def);
            if (def == null) return "no def " + c.def;
            int cell = c.cells[0];
            var orientation = (Orientation)c.orientation;
            List<Tag> elements = Tags(c.elements);
            string facade = c.facade.Length > 0 ? c.facade : null;
            GameObject src = def.BuildingPreview;
            Vector3 pos = Grid.CellToPosCBC(cell, Grid.SceneLayer.Building);
            GameObject go = def.TryPlace(src, pos, orientation, elements, facade, restrictToActiveWorld: false);
            if (go == null && def.ReplacementLayer != ObjectLayer.NumLayers)
            {
                GameObject candidate = def.GetReplacementCandidate(cell);
                bool occupied = false;
                def.RunOnArea(cell, orientation, oc => { if (def.IsReplacementLayerOccupied(oc)) occupied = true; });
                BuildingComplete bc = candidate != null ? candidate.GetComponent<BuildingComplete>() : null;
                if (bc != null && !occupied && bc.Def.Replaceable && def.CanReplace(candidate)
                    && (bc.Def != def || elements[0] != candidate.GetComponent<PrimaryElement>().Element.tag))
                {
                    go = def.TryReplaceTile(src, pos, orientation, elements, facade);
                    if (go != null) Grid.Objects[cell, (int)def.ReplacementLayer] = go;
                }
            }
            if (go == null) return "not placed";
            Prioritizable prio = go.GetComponent<Prioritizable>();
            if (prio != null && c.hasPriority) prio.SetMasterPriority(c.Priority);
            return "placed";
        }

        // ---- pipes / wires: replay BaseUtilityBuildTool.BuildPath with the issuer's path

        private static readonly Type PathNodeType = AccessTools.Inner(typeof(BaseUtilityBuildTool), "PathNode");

        public static Command CaptureUtility(BaseUtilityBuildTool tool)
        {
            var t = Traverse.Create(tool);
            var def = t.Field("def").GetValue<BuildingDef>();
            var c = new Command { kind = Kind.Utility, tool = tool.GetType().Name, def = def != null ? def.PrefabID : "", facade = t.Field("facadeID").GetValue<string>() ?? "" };
            var elements = t.Field("selectedElements").GetValue<IList<Tag>>();
            if (elements != null) foreach (Tag e in elements) c.elements.Add(e.Name);
            var path = (IList)t.Field("path").GetValue();
            foreach (object node in path)
            {
                var n = Traverse.Create(node);
                c.cells.Add(n.Field("cell").GetValue<int>());
                c.extra.Add(n.Field("valid").GetValue<bool>() ? 1 : 0);
            }
            c.SetPriority(BuildPriority());
            return c;
        }

        private static string ExecuteUtility(Command c)
        {
            var tool = FindTool(c.tool) as BaseUtilityBuildTool;
            BuildingDef def = Assets.GetBuildingDef(c.def);
            if (tool == null || def == null) return $"no tool/def {c.tool}/{c.def}";
            var t = Traverse.Create(tool);
            object oldDef = t.Field("def").GetValue(), oldEl = t.Field("selectedElements").GetValue(), oldFacade = t.Field("facadeID").GetValue(),
                   oldPath = t.Field("path").GetValue(), oldMgr = t.Field("conduitMgr").GetValue();
            try
            {
                var path = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(PathNodeType));
                for (int i = 0; i < c.cells.Count; i++)
                {
                    object node = Activator.CreateInstance(PathNodeType);
                    var nt = Traverse.Create(node);
                    nt.Field("cell").SetValue(c.cells[i]);
                    nt.Field("valid").SetValue(c.extra[i] != 0);
                    path.Add(node);
                }
                t.Field("def").SetValue(def);
                t.Field("selectedElements").SetValue(Tags(c.elements));
                t.Field("facadeID").SetValue(c.facade.Length > 0 ? c.facade : null);
                t.Field("conduitMgr").SetValue(def.BuildingComplete.GetComponent<IHaveUtilityNetworkMgr>().GetNetworkManager());
                t.Field("path").SetValue(path);
                AccessTools.Method(typeof(BaseUtilityBuildTool), "BuildPath").Invoke(tool, null);
                return $"path of {c.cells.Count}";
            }
            finally
            {
                t.Field("def").SetValue(oldDef);
                t.Field("selectedElements").SetValue(oldEl);
                t.Field("facadeID").SetValue(oldFacade);
                t.Field("path").SetValue(oldPath);
                t.Field("conduitMgr").SetValue(oldMgr);
            }
        }
    }

    // ===================================================================== interception patches

    internal static class CommandPatches
    {
        /// <summary>Drag tools whose per-cell / box actions are orders for the shared world.</summary>
        private static readonly Type[] CellTools =
        {
            typeof(DigTool), typeof(CancelTool), typeof(DeconstructTool), typeof(PrioritizeTool), typeof(MopTool),
            typeof(ClearTool), typeof(DisinfectTool), typeof(HarvestTool), typeof(EmptyPipeTool),
        };
        private static readonly Type[] BoxTools = { typeof(CancelTool), typeof(AttackTool), typeof(CaptureTool) };

        public static void Apply(Harmony h)
        {
            foreach (Type t in CellTools)
            {
                MethodInfo m = AccessTools.DeclaredMethod(t, "OnDragTool", new[] { typeof(int), typeof(int) });
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(CommandPatches), nameof(DragCellPrefix)),
                                         postfix: new HarmonyMethod(typeof(CommandPatches), nameof(DragCellPostfix)));
                else Log.Warn($"{t.Name}.OnDragTool not found");
            }
            foreach (Type t in BoxTools)
            {
                MethodInfo m = AccessTools.DeclaredMethod(t, "OnDragComplete", new[] { typeof(Vector3), typeof(Vector3) });
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(CommandPatches), nameof(DragBoxPrefix)));
                else Log.Warn($"{t.Name}.OnDragComplete not found");
            }
            h.Patch(AccessTools.Method(typeof(BuildTool), "TryBuild"),
                prefix: new HarmonyMethod(typeof(CommandPatches), nameof(TryBuildPrefix)),
                postfix: new HarmonyMethod(typeof(CommandPatches), nameof(TryBuildPostfix)));
            h.Patch(AccessTools.Method(typeof(BaseUtilityBuildTool), "BuildPath"),
                prefix: new HarmonyMethod(typeof(CommandPatches), nameof(BuildPathPrefix)),
                postfix: new HarmonyMethod(typeof(CommandPatches), nameof(BuildPathPostfix)));
            var prio = new HarmonyMethod(typeof(CommandPatches), nameof(PriorityPostfix));
            h.Patch(AccessTools.Method(typeof(PriorityScreen), nameof(PriorityScreen.GetLastSelectedPriority)), postfix: prio);
            h.Patch(AccessTools.Method(typeof(PlanScreen), nameof(PlanScreen.GetBuildingPriority)), postfix: prio);
            h.Patch(AccessTools.Method(typeof(BuildMenu), nameof(BuildMenu.GetBuildingPriority)), postfix: prio);
        }

        private static bool DragCellPrefix(DragTool __instance, int __0, int __1)
        {
            if (!CommandBus.Active || CommandBus.Replaying) return true;
            CommandBus.Command c = CommandBus.CaptureDragCell(__instance, __0, __1);
            if (!CommandBus.IsHost) return false; // a guest waits for the host to execute it
            recordingCell = c.ids;
            EntitySync.BeginRecord(new List<int>()); // this cell's objects, appended in the postfix
            return true;
        }

        private static List<int> recordingCell;

        private static void DragCellPostfix()
        {
            if (recordingCell == null) return;
            List<int> got = EntitySync.CurrentRecord;
            EntitySync.EndRecord();
            if (got != null) recordingCell.AddRange(got);
            recordingCell = null;
        }

        private static bool DragBoxPrefix(DragTool __instance, Vector3 __0, Vector3 __1)
        {
            Vector3 cursorDown = __0, cursorUp = __1;
            if (!CommandBus.Active || CommandBus.Replaying) return true;
            CommandBus.Command c = CommandBus.CaptureDragBox(__instance, cursorDown, cursorUp);
            if (CommandBus.IsHost)
            {
                // execute first, then broadcast (postfix would need state; the box is enough to replay)
                CommandBus.Replaying = true;
                EntitySync.BeginRecord(c.ids);
                try { AccessTools.Method(__instance.GetType(), "OnDragComplete", new[] { typeof(Vector3), typeof(Vector3) }).Invoke(__instance, new object[] { cursorDown, cursorUp }); }
                finally { CommandBus.Replaying = false; EntitySync.EndRecord(); }
            }
            CommandBus.Issue(c);
            return false;
        }

        // ---- BuildTool.TryBuild(int cell)

        private static CommandBus.Command pendingBuild;

        private static bool TryBuildPrefix(BuildTool __instance, int __0)
        {
            int cell = __0;
            pendingBuild = null;
            if (!CommandBus.Active || CommandBus.Replaying) return true;
            var t = Traverse.Create(__instance);
            var visualizer = t.Field("visualizer").GetValue<GameObject>();
            var def = t.Field("def").GetValue<BuildingDef>();
            var orientation = t.Field("buildingOrientation").GetValue<Orientation>();
            // same early-outs as TryBuild
            if (visualizer == null || def == null) return true;
            if (cell == t.Field("lastDragCell").GetValue<int>() && orientation == t.Field("lastDragOrientation").GetValue<Orientation>()) return true;
            if (Grid.PosToCell(visualizer) != cell && (def.BuildingComplete.GetComponent<LogicPorts>() != null || def.BuildingComplete.GetComponent<LogicGateBase>() != null)) return true;

            CommandBus.Command c = CommandBus.CaptureBuild(def, cell, orientation, t.Field("selectedElements").GetValue<IList<Tag>>(), t.Field("facadeID").GetValue<string>());
            if (CommandBus.IsHost)
            {
                pendingBuild = c; // sent after the original placed it
                EntitySync.BeginRecord(c.ids);
                return true;
            }
            t.Field("lastDragCell").SetValue(cell);
            t.Field("lastDragOrientation").SetValue(orientation);
            CommandBus.Issue(c);
            return false;
        }

        private static void TryBuildPostfix()
        {
            if (pendingBuild == null) return;
            EntitySync.EndRecord();
            CommandBus.Issue(pendingBuild);
            pendingBuild = null;
        }

        // ---- BaseUtilityBuildTool.BuildPath()

        private static CommandBus.Command pendingPath;

        private static bool BuildPathPrefix(BaseUtilityBuildTool __instance)
        {
            pendingPath = null;
            if (!CommandBus.Active || CommandBus.Replaying) return true;
            CommandBus.Command c = CommandBus.CaptureUtility(__instance);
            if (c.cells.Count == 0) return true;
            if (CommandBus.IsHost)
            {
                pendingPath = c;
                EntitySync.BeginRecord(c.ids);
                return true;
            }
            CommandBus.Issue(c);
            return false;
        }

        private static void BuildPathPostfix()
        {
            if (pendingPath == null) return;
            EntitySync.EndRecord();
            CommandBus.Issue(pendingPath);
            pendingPath = null;
        }

        private static void PriorityPostfix(ref PrioritySetting __result)
        {
            if (CommandBus.Replaying && CommandBus.PriorityOverride.HasValue) __result = CommandBus.PriorityOverride.Value;
        }
    }

    // ===================================================================== pause / speed

    /// <summary>The host's pause and speed are everybody's. A guest pressing pause or a speed button asks the host.
    /// Pop-up windows no longer pause the shared game.</summary>
    internal static class SpeedSync
    {
        private static bool applying;
        private static bool lastPaused;
        private static int lastSpeed = -1;
        private static float nextBroadcast;

        /// <summary>Host, every frame: broadcast changes (and every 2 s for late joiners).</summary>
        public static void HostTick(float now)
        {
            var s = SpeedControlScreen.Instance;
            if (s == null || !CommandBus.Active || !CommandBus.IsHost) return;
            bool paused = s.IsPaused;
            int speed = s.GetSpeed();
            if (paused == lastPaused && speed == lastSpeed && now < nextBroadcast) return;
            if (paused != lastPaused || speed != lastSpeed) Log.Info($"speed state: {(paused ? "paused" : "running")}, speed {speed + 1}");
            lastPaused = paused;
            lastSpeed = speed;
            nextBroadcast = now + 2f;
            CommandBus.Client.SendRelay(Proto.TargetAll, CommandBus.ChSpeedState, new[] { (byte)(paused ? 1 : 0), (byte)speed });
        }

        public static void Apply(byte[] data)
        {
            var s = SpeedControlScreen.Instance;
            if (s == null || data.Length < 2) return;
            bool paused = data[0] != 0;
            int speed = data[1];
            applying = true;
            try
            {
                if (s.GetSpeed() != speed) s.SetSpeed(speed);
                if (paused && !s.IsPaused) s.Pause(false);
                // a guest's pause count can be >1 (e.g. paused on load and by a pop-up opened before coop): clear it
                for (int i = 0; i < 10 && !paused && s.IsPaused; i++) s.Unpause(false);
            }
            finally { applying = false; }
            if (paused != lastPaused || speed != lastSpeed) Log.Info($"speed from host: {(paused ? "paused" : "running")}, speed {speed + 1}");
            lastPaused = paused;
            lastSpeed = speed;
        }

        /// <summary>Host: a guest pressed pause (data[0] = 1: toggle) or a speed button (data[0] = 2, data[1] = speed).</summary>
        public static void OnRequest(ushort from, byte[] data)
        {
            var s = SpeedControlScreen.Instance;
            if (s == null || data.Length < 2) return;
            Log.Info($"speed request from #{from}: {(data[0] == 1 ? "toggle pause" : "speed " + (data[1] + 1))}");
            applying = true;
            try
            {
                if (data[0] == 1) s.TogglePause();
                else s.SetSpeed(data[1]);
            }
            finally { applying = false; }
        }

        /// <summary>Set while the player presses a speed button or key. The game also calls SetSpeed itself
        /// (loading, unpausing), and those calls are not requests.</summary>
        public static bool UserAction;

        public static bool Intercept(byte kind, int speed)
        {
            if (kind == 2 && !UserAction) return true;
            UserAction = false;
            if (applying || !CommandBus.IsGuest) return true;
            CommandBus.Client.SendRelay(Proto.TargetHost, CommandBus.ChSpeedRequest, new[] { kind, (byte)speed });
            return false;
        }
    }

    [HarmonyPatch(typeof(SpeedControlScreen), nameof(SpeedControlScreen.TogglePause))]
    internal static class SpeedControlScreen_TogglePause_Patch
    {
        private static bool Prefix() => SpeedSync.Intercept(1, 0);
    }

    [HarmonyPatch(typeof(SpeedControlScreen), nameof(SpeedControlScreen.OnKeyDown))]
    internal static class SpeedControlScreen_OnKeyDown_Patch
    {
        private static void Prefix() => SpeedSync.UserAction = true;
        private static void Postfix() => SpeedSync.UserAction = false;
    }

    /// <summary>The speed buttons play this sound right before calling SetSpeed.</summary>
    [HarmonyPatch(typeof(SpeedControlScreen), "PlaySpeedChangeSound")]
    internal static class SpeedControlScreen_PlaySpeedChangeSound_Patch
    {
        private static void Prefix() => SpeedSync.UserAction = true;
    }

    [HarmonyPatch(typeof(SpeedControlScreen), nameof(SpeedControlScreen.SetSpeed))]
    internal static class SpeedControlScreen_SetSpeed_Patch
    {
        private static bool Prefix(int Speed) => SpeedSync.Intercept(2, Speed);
    }

    /// <summary>In a shared game a pop-up on one screen must not pause everybody (or freeze one player's world).</summary>
    [HarmonyPatch(typeof(KModalScreen), "OnShow")]
    internal static class KModalScreen_OnShow_NoPause_Patch
    {
        private static void Prefix(KModalScreen __instance, bool show)
        {
            if (show && CommandBus.Active) __instance.pause = false;
        }
    }
}
