namespace OniCoop
{
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Whether each building works, as the server decides, and the numbers that follow from it.
    ///
    /// A building's Operational component holds its flags (powered, enabled, pipes connected, automation allows it...)
    /// and whether it is active right now. Much of what a player sees comes from there: a light shines while its
    /// lamp is operational (Light2D listens), a consumer draws its watts while active, a generator counts as producing
    /// while active. A guest runs little building logic and its own power simulation, so its lights, its circuit
    /// numbers and its "is it powered" could all differ from the server's.
    ///
    /// The server sends every building's flags and active state when they change; batteries' charge; per circuit the
    /// watts used and the lowest battery charge (the rest of the circuit panel follows from the buildings: watts
    /// generated, joules available, potential use); and per automation network its signal. A guest applies them,
    /// keeps its own code from changing them, and doesn't run its own power simulation (no batteries charging or
    /// draining, no overloads there).</summary>
    internal static class OperationSync
    {
        public const byte Ch = 21; // server -> guests
        private const byte ROp = 1, RBattery = 2, RCircuits = 3, RLogic = 4;
        private const int Slices = 16;

        /// <summary>Guest: the mirror itself is changing a building right now.</summary>
        public static bool Applying;

        private static readonly AccessTools.FieldRef<Battery, float> BatteryJoules = AccessTools.FieldRefAccess<Battery, float>("joulesAvailable");
        private static readonly AccessTools.FieldRef<LogicCircuitNetwork, int> LogicOut = AccessTools.FieldRefAccess<LogicCircuitNetwork, int>("outputValue");
        private static readonly AccessTools.FieldRef<LogicCircuitNetwork, List<LogicWire>[]> LogicWires =
            AccessTools.FieldRefAccess<LogicCircuitNetwork, List<LogicWire>[]>("wireGroups");

        public static long HostOps, HostBatteries, HostCircuits, HostLogic, HostBytes;
        public static long GuestOps, GuestBatteries, GuestUnknown, GuestCircuitMsgs, GuestLogicMsgs, BlockedFlags, BlockedActive, BlockedEnergy, NewFlags;

        public static void Reset()
        {
            refs.Clear();
            buf.SetLength(0);
            records = 0;
            cursor = 0;
            circuitsSent.Clear();
            logicSent.Clear();
            mirroredOps.Clear();
            opById.Clear();
            circuitByCell.Clear();
            circuitById.Clear();
            logicByCell.Clear();
            logicByNet.Clear();
        }

        // ================================================================== server

        private sealed class HRef
        {
            public KPrefabID k;
            public Operational op;
            public Battery bat;
            public int sig = int.MinValue;
            public float joules = -1f, joulesAt;
        }

        private static readonly Dictionary<BuildingComplete, HRef> refs = new Dictionary<BuildingComplete, HRef>();
        private static readonly Dictionary<Operational.Flag, int> flagHash = new Dictionary<Operational.Flag, int>();
        private static readonly MemoryStream buf = new MemoryStream();
        private static readonly BinaryWriter bufW = new BinaryWriter(buf);
        private static int records, cursor;
        private static float nextSend, nextCircuits, nextCircuitsFull, nextLogic, nextLogicFull;
        private static readonly Dictionary<int, Vector2> circuitsSent = new Dictionary<int, Vector2>();
        private static readonly Dictionary<int, int> logicSent = new Dictionary<int, int>();

        /// <summary>A guest (re)entered the game: send everything again.</summary>
        public static void OnGuestJoined()
        {
            foreach (HRef r in refs.Values) { r.sig = int.MinValue; r.joules = -1f; }
            nextCircuitsFull = nextLogicFull = 0f;
        }

        private static int FlagHash(Operational.Flag f)
        {
            if (!flagHash.TryGetValue(f, out int h)) flagHash[f] = h = (f.Name ?? "").GetHashCode() * 3 + (int)f.FlagType;
            return h;
        }

        private static int Sig(Operational op)
        {
            int h = op.IsActive ? 7 : 11;
            unchecked { foreach (var kv in op.Flags) h = h * 31 + FlagHash(kv.Key) * (kv.Value ? 3 : 5); }
            return h;
        }

        /// <summary>Every server frame while a guest is in the game: a slice of the buildings (all every ~16 frames),
        /// plus circuits every second and automation five times a second.</summary>
        public static void HostTick(float now)
        {
            List<BuildingComplete> list = Components.BuildingCompletes.Items;
            int n = list.Count;
            if (n > 0)
            {
                int count = (n + Slices - 1) / Slices;
                for (int step = 0; step < count; step++)
                {
                    if (cursor >= n) { cursor = 0; if (refs.Count > n * 2) Prune(); }
                    BuildingComplete b = list[cursor++];
                    if (b == null) continue;
                    if (!refs.TryGetValue(b, out HRef r))
                        refs[b] = r = new HRef { k = b.GetComponent<KPrefabID>(), op = b.GetComponent<Operational>(), bat = b.GetComponent<Battery>() };
                    if (r.k == null || r.k.InstanceID < 0 || r.k.InstanceID >= EntitySync.LocalIdBase) continue;
                    if (r.op != null)
                    {
                        int sig = Sig(r.op);
                        if (sig != r.sig)
                        {
                            r.sig = sig;
                            WriteOp(r.k.InstanceID, r.op);
                        }
                    }
                    if (r.bat != null)
                    {
                        float j = r.bat.JoulesAvailable;
                        float d = Mathf.Abs(j - r.joules);
                        if (r.joules < 0f || d > Mathf.Max(1f, 0.002f * r.bat.Capacity) || (d > 0f && now - r.joulesAt > 2f))
                        {
                            r.joules = j;
                            r.joulesAt = now;
                            bufW.Write(RBattery); bufW.Write(r.k.InstanceID); bufW.Write(j);
                            records++;
                            HostBatteries++;
                        }
                    }
                }
            }
            if (now >= nextCircuits) { nextCircuits = now + 1f; WriteCircuits(now); }
            if (now >= nextLogic) { nextLogic = now + 0.2f; WriteLogic(now); }
            if (records > 0 && now >= nextSend)
            {
                nextSend = now + 0.25f;
                var ms = new MemoryStream();
                using (var z = new DeflateStream(ms, CompressionMode.Compress, true)) z.Write(buf.GetBuffer(), 0, (int)buf.Length);
                byte[] payload = ms.ToArray();
                HostBytes += payload.Length;
                CommandBus.Client.SendRelay(Proto.TargetAll, Ch, payload);
                buf.SetLength(0);
                records = 0;
            }
        }

        /// <summary>Test sync points: every building now, circuits and automation too, sent at once.</summary>
        public static void FlushNow()
        {
            if (Components.BuildingCompletes.Count == 0) return;
            for (int i = 0; i < Slices + 1; i++) HostTick(float.NegativeInfinity);
            nextCircuits = nextLogic = nextSend = 0f;
            HostTick(Time.realtimeSinceStartup);
        }

        private static void WriteOp(int id, Operational op)
        {
            bufW.Write(ROp); bufW.Write(id); bufW.Write(op.IsActive);
            bufW.Write((byte)Mathf.Min(op.Flags.Count, 255));
            int i = 0;
            foreach (var kv in op.Flags)
            {
                if (i++ >= 255) break;
                bufW.Write(kv.Key.Name ?? ""); bufW.Write((byte)kv.Key.FlagType); bufW.Write(kv.Value);
            }
            records++;
            HostOps++;
        }

        private static readonly Dictionary<ushort, int> circuitCell = new Dictionary<ushort, int>();

        /// <summary>Per circuit: a cell where it can be found on the other side (the power port of one of its
        /// buildings), the watts it used and its lowest battery charge.</summary>
        private static void WriteCircuits(float now)
        {
            CircuitManager cm = Game.Instance?.circuitManager;
            if (cm == null) return;
            circuitCell.Clear();
            foreach (EnergyConsumer c in Components.EnergyConsumers.Items) if (c != null) Note(cm, c.PowerCell);
            foreach (Generator g in Components.Generators.Items) if (g != null) Note(cm, g.PowerCell);
            foreach (Battery b in Components.Batteries.Items) if (b != null) Note(cm, b.PowerCell);
            bool full = now >= nextCircuitsFull;
            if (full) { nextCircuitsFull = now + 10f; circuitsSent.Clear(); }
            var changed = new List<KeyValuePair<int, Vector2>>();
            foreach (var kv in circuitCell)
            {
                var v = new Vector2(cm.GetWattsUsedByCircuit(kv.Key), cm.GetMinBatteryPercentFullOnCircuit(kv.Key));
                if (circuitsSent.TryGetValue(kv.Value, out Vector2 old) && Mathf.Abs(old.x - v.x) < 0.5f && Mathf.Abs(old.y - v.y) < 0.001f) continue;
                circuitsSent[kv.Value] = v;
                changed.Add(new KeyValuePair<int, Vector2>(kv.Value, v));
            }
            if (changed.Count == 0 && !full) return;
            bufW.Write(RCircuits); bufW.Write(full); bufW.Write((ushort)changed.Count);
            foreach (var kv in changed) { bufW.Write(kv.Key); bufW.Write(kv.Value.x); bufW.Write(kv.Value.y); }
            records++;
            HostCircuits += changed.Count;
        }

        private static void Note(CircuitManager cm, int cell)
        {
            if (!Grid.IsValidCell(cell)) return;
            ushort id = cm.GetCircuitID(cell);
            if (id != ushort.MaxValue && !circuitCell.ContainsKey(id)) circuitCell[id] = cell;
        }

        /// <summary>Per automation network that changed: one of its wire cells and its signal.</summary>
        private static void WriteLogic(float now)
        {
            var sys = Game.Instance?.logicCircuitSystem;
            if (sys == null) return;
            bool full = now >= nextLogicFull;
            if (full) { nextLogicFull = now + 5f; logicSent.Clear(); }
            var changed = new List<KeyValuePair<int, int>>();
            foreach (UtilityNetwork un in sys.GetNetworks())
            {
                if (!(un is LogicCircuitNetwork net)) continue;
                int cell = KeyCell(net);
                if (cell < 0) continue;
                int v = net.OutputValue;
                if (logicSent.TryGetValue(cell, out int old) && old == v) continue;
                logicSent[cell] = v;
                changed.Add(new KeyValuePair<int, int>(cell, v));
            }
            if (changed.Count == 0 && !full) return;
            bufW.Write(RLogic); bufW.Write(full); bufW.Write((ushort)changed.Count);
            foreach (var kv in changed) { bufW.Write(kv.Key); bufW.Write(kv.Value); }
            records++;
            HostLogic += changed.Count;
        }

        private static int KeyCell(LogicCircuitNetwork net)
        {
            List<LogicWire>[] groups = LogicWires(net);
            if (groups == null) return -1;
            foreach (List<LogicWire> g in groups)
                if (g != null) foreach (LogicWire w in g) if (w != null) return Grid.PosToCell(w);
            return -1;
        }

        private static void Prune()
        {
            var dead = new List<BuildingComplete>();
            foreach (var kv in refs) if (kv.Key == null) dead.Add(kv.Key);
            foreach (BuildingComplete b in dead) refs.Remove(b);
        }

        // ================================================================== guest

        private static readonly HashSet<Operational> mirroredOps = new HashSet<Operational>();
        private static readonly Dictionary<int, Operational> opById = new Dictionary<int, Operational>();
        private static readonly Dictionary<string, Operational.Flag> flagByName = new Dictionary<string, Operational.Flag>();
        private static readonly Dictionary<int, Vector2> circuitByCell = new Dictionary<int, Vector2>();
        private static readonly Dictionary<ushort, Vector2> circuitById = new Dictionary<ushort, Vector2>();
        private static readonly Dictionary<int, int> logicByCell = new Dictionary<int, int>();
        private static readonly Dictionary<LogicCircuitNetwork, int> logicByNet = new Dictionary<LogicCircuitNetwork, int>();
        private static float nextResolve;

        public static bool IsMirrored(Operational op) => mirroredOps.Count > 0 && mirroredOps.Contains(op);

        public static void OnRecords(byte[] data)
        {
            if (!Puppets.Mirroring) return;
            Applying = true;
            try
            {
                using (var r = Inflate.Open(data)) // (the shared reader: no garbage per message; using is harmless)
                {
                    while (true)
                    {
                        int kind;
                        if (!Inflate.More(r)) break;
                        kind = r.ReadByte();
                        switch (kind)
                        {
                            case ROp: ReadOp(r); break;
                            case RBattery:
                            {
                                int id = r.ReadInt32();
                                float j = r.ReadSingle();
                                KPrefabID k = KPrefabIDTracker.Get().GetInstance(id);
                                Battery b = k != null ? k.GetComponent<Battery>() : null;
                                if (b == null) { GuestUnknown++; break; }
                                BatteryJoules(b) = Mathf.Clamp(j, 0f, b.Capacity);
                                GuestBatteries++;
                                break;
                            }
                            case RCircuits:
                            {
                                bool full = r.ReadBoolean();
                                int n = r.ReadUInt16();
                                if (full) circuitByCell.Clear();
                                for (int i = 0; i < n; i++) circuitByCell[r.ReadInt32()] = new Vector2(r.ReadSingle(), r.ReadSingle());
                                GuestCircuitMsgs++;
                                nextResolve = 0f;
                                break;
                            }
                            case RLogic:
                            {
                                bool full = r.ReadBoolean();
                                int n = r.ReadUInt16();
                                if (full) logicByCell.Clear();
                                for (int i = 0; i < n; i++) logicByCell[r.ReadInt32()] = r.ReadInt32();
                                GuestLogicMsgs++;
                                nextResolve = 0f;
                                break;
                            }
                            default:
                                Log.Warn($"operation sync: unknown record {kind}");
                                return;
                        }
                    }
                }
            }
            finally { Applying = false; }
            Resolve(Time.realtimeSinceStartup);
        }

        private static readonly List<Operational.Flag> seen = new List<Operational.Flag>(8);
        private static readonly List<Operational.Flag> localOnly = new List<Operational.Flag>(4);

        private static void ReadOp(BinaryReader r)
        {
            int id = r.ReadInt32();
            bool active = r.ReadBoolean();
            int n = r.ReadByte();
            if (!opById.TryGetValue(id, out Operational op) || op == null)
            {
                KPrefabID k = KPrefabIDTracker.Get().GetInstance(id);
                op = k != null ? k.GetComponent<Operational>() : null;
                if (op != null) opById[id] = op; else opById.Remove(id);
            }
            seen.Clear();
            for (int i = 0; i < n; i++)
            {
                string name = r.ReadString();
                var type = (Operational.Flag.Type)r.ReadByte();
                bool value = r.ReadBoolean();
                if (op == null) continue;
                Operational.Flag f = FindFlag(op, name, type);
                seen.Add(f);
                op.SetFlag(f, value);
            }
            if (op == null) { GuestUnknown++; return; }
            mirroredOps.Add(op);
            // a flag only this guest's code set: the server has no such condition, so it may not hold the building back
            localOnly.Clear();
            foreach (var kv in op.Flags) if (!kv.Value && !seen.Contains(kv.Key)) localOnly.Add(kv.Key);
            foreach (Operational.Flag f in localOnly) op.SetFlag(f, true);
            op.SetActive(active);
            GuestOps++;
        }

        /// <summary>The guest's flag object for the server's flag: the one this building already has with that name,
        /// else any the game made with that name (the flags are static fields of the classes using them).</summary>
        private static Operational.Flag FindFlag(Operational op, string name, Operational.Flag.Type type)
        {
            foreach (Operational.Flag f in op.Flags.Keys) if (f.Name == name && f.FlagType == type) return f;
            string key = name + "\u0001" + (int)type;
            if (!flagByName.TryGetValue(key, out Operational.Flag g))
            {
                flagByName[key] = g = new Operational.Flag(name, type);
                NewFlags++;
            }
            return g;
        }

        private static void FlagCtorPostfix(Operational.Flag __instance)
        {
            if (__instance?.Name == null) return;
            string key = __instance.Name + "\u0001" + (int)__instance.FlagType;
            if (!flagByName.ContainsKey(key)) flagByName[key] = __instance;
        }

        /// <summary>Which of this guest's circuits / automation networks the server's values belong to (their ids
        /// and objects change whenever wires are built or removed).</summary>
        private static void Resolve(float now)
        {
            nextResolve = now + 0.5f;
            if (Game.Instance == null) return;
            CircuitManager cm = Game.Instance.circuitManager;
            circuitById.Clear();
            if (cm != null)
                foreach (var kv in circuitByCell)
                {
                    ushort id = cm.GetCircuitID(kv.Key);
                    if (id != ushort.MaxValue) circuitById[id] = kv.Value;
                }
            logicByNet.Clear();
            var sys = Game.Instance.logicCircuitSystem;
            if (sys != null)
                foreach (var kv in logicByCell)
                    if (sys.GetNetworkForCell(kv.Key) is LogicCircuitNetwork net) logicByNet[net] = kv.Value;
        }

        public static void GuestTick(float now)
        {
            if (now >= nextResolve) Resolve(now);
        }

        // ---- the guest's own code keeps its hands off

        private static bool SetFlagPrefix(Operational __instance)
        {
            if (!Puppets.Mirroring || Applying || !IsMirrored(__instance)) return true;
            BlockedFlags++;
            return false;
        }

        private static bool SetActivePrefix(Operational __instance)
        {
            if (!Puppets.Mirroring || Applying || !IsMirrored(__instance)) return true;
            BlockedActive++;
            return false;
        }

        /// <summary>No power simulation on a guest: what is powered and how full batteries are comes from the server.</summary>
        private static bool CircuitSimPrefix() => !Puppets.Mirroring;

        private static bool BatteryEnergyPrefix()
        {
            if (!Puppets.Mirroring || Applying) return true;
            BlockedEnergy++;
            return false;
        }

        private static void WattsUsedPostfix(ushort circuitID, ref float __result)
        {
            if (Puppets.Mirroring && circuitById.TryGetValue(circuitID, out Vector2 v)) __result = v.x;
        }

        private static void MinBatteryPostfix(ushort circuitID, ref float __result)
        {
            if (Puppets.Mirroring && circuitById.TryGetValue(circuitID, out Vector2 v)) __result = v.y;
        }

        /// <summary>An automation network's signal on a guest is the server's (receivers, wire colours, port lights
        /// follow it).</summary>
        private static void LogicValuePostfix(LogicCircuitNetwork __instance)
        {
            if (Puppets.Mirroring && logicByNet.Count > 0 && logicByNet.TryGetValue(__instance, out int v)) LogicOut(__instance) = v;
        }

        public static void ApplyPatches(Harmony h)
        {
            void P(System.Reflection.MethodBase m, string prefix = null, string postfix = null)
            {
                try
                {
                    if (m == null) throw new System.Exception("not found");
                    h.Patch(m, prefix: prefix != null ? new HarmonyMethod(typeof(OperationSync), prefix) : null,
                        postfix: postfix != null ? new HarmonyMethod(typeof(OperationSync), postfix) : null);
                }
                catch (System.Exception e) { Log.Warn($"operation sync: can't patch {m?.DeclaringType?.Name}.{m?.Name}: {e.Message}"); }
            }
            P(AccessTools.Constructor(typeof(Operational.Flag), new[] { typeof(string), typeof(Operational.Flag.Type) }), postfix: nameof(FlagCtorPostfix));
            P(AccessTools.Method(typeof(Operational), nameof(Operational.SetFlag)), prefix: nameof(SetFlagPrefix));
            P(AccessTools.Method(typeof(Operational), nameof(Operational.SetActive)), prefix: nameof(SetActivePrefix));
            P(AccessTools.Method(typeof(CircuitManager), nameof(CircuitManager.Sim200msLast)), prefix: nameof(CircuitSimPrefix));
            P(AccessTools.Method(typeof(Battery), nameof(Battery.ConsumeEnergy), new[] { typeof(float), typeof(bool) }), prefix: nameof(BatteryEnergyPrefix));
            P(AccessTools.Method(typeof(Battery), nameof(Battery.AddEnergy)), prefix: nameof(BatteryEnergyPrefix));
            P(AccessTools.Method(typeof(CircuitManager), nameof(CircuitManager.GetWattsUsedByCircuit)), postfix: nameof(WattsUsedPostfix));
            P(AccessTools.Method(typeof(CircuitManager), nameof(CircuitManager.GetMinBatteryPercentFullOnCircuit)), postfix: nameof(MinBatteryPostfix));
            P(AccessTools.Method(typeof(LogicCircuitNetwork), nameof(LogicCircuitNetwork.UpdateLogicValue)), postfix: nameof(LogicValuePostfix));
            // the flags that already exist (static fields of classes initialized before this patch)
            foreach (Operational op in Object.FindObjectsOfType<Operational>()) foreach (Operational.Flag f in op.Flags.Keys) FlagCtorPostfix(f);
        }

        public static string Report(bool host) => host
            ? $"operation: {HostOps} building states, {HostBatteries} battery charges, {HostCircuits} circuit values, {HostLogic} automation signals, {HostBytes / 1024} KB"
            : $"operation: {GuestOps} building states ({mirroredOps.Count} buildings), {GuestBatteries} battery charges, {GuestCircuitMsgs} circuit / {GuestLogicMsgs} automation updates " +
              $"({circuitById.Count} circuits, {logicByNet.Count} networks matched), unknown {GuestUnknown}, flags made here {NewFlags}; blocked local flags {BlockedFlags}, active {BlockedActive}, battery {BlockedEnergy}";

        /// <summary>For the sync test: every building's state, batteries, circuits, lights and automation as this side
        /// shows them.</summary>
        public static string Dump()
        {
            var sb = new StringBuilder();
            var names = new List<string>();
            foreach (BuildingComplete b in Components.BuildingCompletes.Items)
            {
                if (b == null) continue;
                var k = b.GetComponent<KPrefabID>();
                if (k == null || k.InstanceID >= EntitySync.LocalIdBase) continue;
                var op = b.GetComponent<Operational>();
                if (op != null)
                {
                    names.Clear();
                    foreach (var kv in op.Flags) names.Add(kv.Key.Name + "=" + (kv.Value ? 1 : 0));
                    names.Sort(System.StringComparer.Ordinal);
                    sb.Append("OP ").Append(k.InstanceID).Append(' ').Append(k.PrefabTag.Name).Append(' ')
                      .Append(op.IsOperational ? 1 : 0).Append(op.IsActive ? 1 : 0).Append(' ').Append(string.Join(",", names.ToArray())).Append('\n');
                }
                var bat = b.GetComponent<Battery>();
                if (bat != null) sb.Append("BAT ").Append(k.InstanceID).Append(' ').Append(bat.JoulesAvailable.ToString("F1")).Append(' ').Append(bat.Capacity.ToString("F0")).Append('\n');
                var light = b.GetComponent<Light2D>();
                if (light != null) sb.Append("LIGHT ").Append(k.InstanceID).Append(' ').Append(light.enabled ? 1 : 0).Append(' ').Append(light.Lux).Append(' ').Append(light.Range.ToString("F1")).Append('\n');
            }
            CircuitManager cm = Game.Instance?.circuitManager;
            if (cm != null)
            {
                circuitCell.Clear();
                foreach (EnergyConsumer c in Components.EnergyConsumers.Items) if (c != null) Note(cm, c.PowerCell);
                foreach (Generator g in Components.Generators.Items) if (g != null) Note(cm, g.PowerCell);
                foreach (Battery bt in Components.Batteries.Items) if (bt != null) Note(cm, bt.PowerCell);
                // a circuit by the smallest port cell on it, so both sides name it the same
                var byMin = new Dictionary<ushort, int>();
                foreach (EnergyConsumer c in Components.EnergyConsumers.Items) if (c != null) Min(cm, byMin, c.PowerCell);
                foreach (Generator g in Components.Generators.Items) if (g != null) Min(cm, byMin, g.PowerCell);
                foreach (Battery bt in Components.Batteries.Items) if (bt != null) Min(cm, byMin, bt.PowerCell);
                foreach (Generator g in Components.Generators.Items)
                {
                    if (g == null) continue;
                    var gk = g.GetComponent<KPrefabID>();
                    ushort gc = Grid.IsValidCell(g.PowerCell) ? cm.GetCircuitID(g.PowerCell) : ushort.MaxValue;
                    sb.Append("GEN ").Append(gk != null ? gk.InstanceID : 0).Append(' ').Append(g.IsProducingPower() ? 1 : 0).Append(' ')
                      .Append(g.WattageRating.ToString("F0")).Append(' ').Append(gc != ushort.MaxValue && byMin.TryGetValue(gc, out int gm) ? gm : -1).Append(' ')
                      .Append(cm.GetGeneratorsOnCircuit(gc)?.Contains(g) == true ? 1 : 0).Append('\n');
                }
                foreach (var kv in byMin)
                    sb.Append("CIRCUIT ").Append(kv.Value).Append(' ').Append(cm.GetWattsUsedByCircuit(kv.Key).ToString("F1")).Append(' ')
                      .Append(cm.GetWattsNeededWhenActive(kv.Key).ToString("F1")).Append(' ').Append(cm.GetWattsGeneratedByCircuit(kv.Key).ToString("F1")).Append(' ')
                      .Append(cm.GetJoulesAvailableOnCircuit(kv.Key).ToString("F0")).Append(' ').Append(cm.GetMinBatteryPercentFullOnCircuit(kv.Key).ToString("F3")).Append('\n');
            }
            var sys = Game.Instance?.logicCircuitSystem;
            if (sys != null)
                foreach (UtilityNetwork un in sys.GetNetworks())
                {
                    if (!(un is LogicCircuitNetwork net)) continue;
                    int min = int.MaxValue;
                    List<LogicWire>[] groups = LogicWires(net);
                    if (groups != null) foreach (var g in groups) if (g != null) foreach (LogicWire w in g) if (w != null) min = Mathf.Min(min, Grid.PosToCell(w));
                    if (min != int.MaxValue) sb.Append("LOGIC ").Append(min).Append(' ').Append(net.OutputValue).Append('\n');
                }
            return sb.ToString();
        }

        private static void Min(CircuitManager cm, Dictionary<ushort, int> byMin, int cell)
        {
            if (!Grid.IsValidCell(cell)) return;
            ushort id = cm.GetCircuitID(cell);
            if (id == ushort.MaxValue) return;
            if (!byMin.TryGetValue(id, out int m) || cell < m) byMin[id] = cell;
        }
    }
}
