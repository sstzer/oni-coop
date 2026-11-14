namespace OniCoop
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Roadmap step 2, the rest of the UI: building side screens, the buttons of a selected object, and the
    /// management screens (research, skills, schedules, consumables, job priorities) all end in a small set of game
    /// methods. Those are registered here and replicated as calls: "this method, on this object, with these
    /// arguments".
    ///
    /// Only calls the player made count: one that happens while Unity's UI is handling input (a click, a slider drag,
    /// typing in a field), and the outermost one (what it calls in turn replays by itself). A guest sends it to the
    /// host instead of running it; for plain setters (a slider, a capacity, a filter) it also applies it at once, so the
    /// UI doesn't jump back. The host runs it and sends it to everybody, who run the same method. Objects an action
    /// creates get the host's IDs on replay (EntitySync ID script).</summary>
    internal static class Calls
    {
        public const byte ChRequest = 12; // guest -> host
        public const byte ChApplied = 13; // host -> everybody

        public static bool Replaying;
        public static int Requests, Executed, Replayed, Unsupported;

        // ================================================================== what the player is doing

        /// <summary>True while Unity's UI handles input (EventSystem.Update, input fields), or while a coop tool that
        /// isn't a drag order (copy settings) applies.</summary>
        public static bool UiDispatch;
        private static int depth;

        private static bool Active => CommandBus.Active;

        // ================================================================== registry

        private sealed class Entry
        {
            public int index;
            public MethodBase method;
            public ParameterInfo[] parameters;
            public bool optimistic; // an idempotent setter: a guest applies it at once too
        }

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly Dictionary<MethodBase, Entry> byMethod = new Dictionary<MethodBase, Entry>();
        private static readonly HashSet<string> unsupportedSeen = new HashSet<string>();

        private static void Register(Harmony h, MethodBase m, bool optimistic)
        {
            if (m == null || byMethod.ContainsKey(m) || m.IsAbstract) return;
            var e = new Entry { index = entries.Count, method = m, parameters = m.GetParameters(), optimistic = optimistic };
            try
            {
                h.Patch(m, prefix: new HarmonyMethod(typeof(Calls), nameof(Prefix)), postfix: new HarmonyMethod(typeof(Calls), nameof(Postfix)));
            }
            catch (Exception ex) { Log.Warn($"calls: can't patch {m.DeclaringType?.Name}.{m.Name}: {ex.Message}"); return; }
            entries.Add(e);
            byMethod[m] = e;
        }

        private static void Method(Harmony h, Type t, string name, bool optimistic, params Type[] args)
        {
            MethodInfo m;
            try { m = args.Length > 0 ? AccessTools.Method(t, name, args) : AccessTools.Method(t, name); }
            catch (Exception ex) { Log.Warn($"calls: {t.Name}.{name}: {ex.Message}"); return; }
            if (m == null) { Log.Warn($"calls: {t.Name}.{name} not found"); return; }
            Register(h, m, optimistic);
            // a virtual one: also its overrides, so the outermost call (the override) is the one that is shared
            if (!m.IsVirtual || m.IsFinal) return;
            Type[] ps = m.GetParameters().Select(p => p.ParameterType).ToArray();
            foreach (Type sub in GameTypes())
            {
                if (sub == t || !t.IsAssignableFrom(sub) || sub.IsAbstract) continue;
                MethodInfo o = sub.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, ps, null);
                if (o != null && o.GetBaseDefinition() == m.GetBaseDefinition()) Register(h, o, optimistic);
            }
        }

        private static Type[] gameTypes;

        private static Type[] GameTypes()
        {
            if (gameTypes != null) return gameTypes;
            try { gameTypes = typeof(Game).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { gameTypes = ex.Types.Where(t => t != null).ToArray(); }
            return gameTypes;
        }

        private static void Setter(Harmony h, Type t, string property)
        {
            MethodInfo m = AccessTools.PropertySetter(t, property);
            if (m == null) { Log.Warn($"calls: {t.Name}.{property} setter not found"); return; }
            Register(h, m, optimistic: true);
        }

        /// <summary>Every class of the game implementing an interface: its implementation of these members.</summary>
        private static void Interface(Harmony h, Type iface, bool optimistic, params string[] members)
        {
            int n = 0;
            foreach (Type t in GameTypes())
            {
                if (t.IsInterface || t.ContainsGenericParameters || !iface.IsAssignableFrom(t)) continue;
                InterfaceMapping map;
                try { map = t.GetInterfaceMap(iface); } catch { continue; }
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    string name = map.InterfaceMethods[i].Name;
                    MethodInfo target = map.TargetMethods[i];
                    if (target.DeclaringType != t) continue; // inherited: patched where it is declared (that type is listed too)
                    foreach (string member in members)
                        if (name == member || name == "set_" + member) { Register(h, target, optimistic); n++; }
                }
            }
            if (n == 0) Log.Warn($"calls: nothing implements {iface.Name}");
        }

        public static void Apply(Harmony h)
        {
            // what counts as the player acting
            try
            {
                Type es = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
                h.Patch(AccessTools.Method(es, "Update"), prefix: new HarmonyMethod(typeof(Calls), nameof(UiBegin)), postfix: new HarmonyMethod(typeof(Calls), nameof(UiEnd)));
                Type tmp = AccessTools.TypeByName("TMPro.TMP_InputField");
                MethodInfo late = tmp != null ? AccessTools.Method(tmp, "LateUpdate") : null;
                if (late != null) h.Patch(late, prefix: new HarmonyMethod(typeof(Calls), nameof(UiBegin)), postfix: new HarmonyMethod(typeof(Calls), nameof(UiEnd)));
                h.Patch(AccessTools.Method(typeof(CopySettingsTool), "OnDragTool"), prefix: new HarmonyMethod(typeof(Calls), nameof(UiBegin)), postfix: new HarmonyMethod(typeof(Calls), nameof(UiEnd)));
                h.Patch(AccessTools.Method(typeof(UserMenu), nameof(UserMenu.AddButton)), prefix: new HarmonyMethod(typeof(Calls), nameof(WrapButton)));
            }
            catch (Exception ex) { Log.Warn("calls: UI hooks failed: " + ex.Message); }

            // side screens, through the interfaces they talk to
            Interface(h, typeof(ISliderControl), true, "SetSliderValue");
            Interface(h, typeof(IThresholdSwitch), true, "Threshold", "ActivateAboveThreshold");
            Interface(h, typeof(IActivationRangeTarget), true, "ActivateValue", "DeactivateValue");
            Interface(h, typeof(IUserControlledCapacity), true, "UserMaxCapacity");
            Interface(h, typeof(ICheckboxControl), true, "SetCheckboxValue");
            Interface(h, typeof(INToggleSideScreenControl), false, "QueueSelectedOption");
            Interface(h, typeof(IPlayerControlledToggle), false, "ToggleRequested", "ToggledByPlayer");
            Interface(h, typeof(ISidescreenButtonControl), false, "OnSidescreenButtonPressed");
            Interface(h, typeof(ILogicRibbonBitSelector), true, "SetBitSelection");
            // side screens, directly
            Method(h, typeof(Door), nameof(Door.QueueStateChange), false); // asking twice takes it back: not idempotent
            Method(h, typeof(TreeFilterable), nameof(TreeFilterable.AddTagToFilter), true);
            Method(h, typeof(TreeFilterable), nameof(TreeFilterable.RemoveTagFromFilter), true);
            Method(h, typeof(TreeFilterable), nameof(TreeFilterable.UpdateFilters), true);
            Setter(h, typeof(Filterable), nameof(Filterable.SelectedTag));
            Method(h, typeof(ComplexFabricator), nameof(ComplexFabricator.SetRecipeQueueCount), true);
            Method(h, typeof(ComplexFabricator), nameof(ComplexFabricator.IncrementRecipeQueueCount), false);
            Method(h, typeof(ComplexFabricator), nameof(ComplexFabricator.DecrementRecipeQueueCount), false);
            Method(h, typeof(SingleEntityReceptacle), nameof(SingleEntityReceptacle.CreateOrder), false);
            Method(h, typeof(SingleEntityReceptacle), nameof(SingleEntityReceptacle.OrderRemoveOccupant), false);
            Method(h, typeof(SingleEntityReceptacle), nameof(SingleEntityReceptacle.CancelActiveRequest), false);
            Method(h, typeof(Assignable), nameof(Assignable.Assign), false, typeof(IAssignableIdentity));
            Method(h, typeof(Assignable), nameof(Assignable.Unassign), false);
            Method(h, typeof(Automatable), nameof(Automatable.SetAutomationOnly), true);
            Method(h, typeof(Prioritizable), nameof(Prioritizable.SetMasterPriority), true);
            Method(h, typeof(AccessControl), nameof(AccessControl.SetDefaultPermission), true);
            Method(h, typeof(AccessControl), nameof(AccessControl.SetPermission), true, typeof(MinionAssignablesProxy), typeof(AccessControl.Permission));
            Method(h, typeof(AccessControl), nameof(AccessControl.SetPermission), true, typeof(Tag), typeof(AccessControl.Permission));
            Method(h, typeof(AccessControl), nameof(AccessControl.ClearPermission), true, typeof(MinionAssignablesProxy));
            Method(h, typeof(CopyBuildingSettings), nameof(CopyBuildingSettings.ApplyCopy), false);
            // management screens
            Method(h, typeof(Research), nameof(Research.SetActiveResearch), false);
            Method(h, typeof(Research), nameof(Research.CancelResearch), false);
            Method(h, typeof(MinionResume), nameof(MinionResume.MasterSkill), false);
            Method(h, typeof(MinionResume), nameof(MinionResume.SetHats), true);
            Method(h, typeof(ConsumableConsumer), nameof(ConsumableConsumer.SetPermitted), true);
            Method(h, typeof(ChoreConsumer), nameof(ChoreConsumer.SetPersonalPriority), true);
            Method(h, typeof(Immigration), nameof(Immigration.SetPersonalPriority), true);
            Method(h, typeof(Schedule), nameof(Schedule.SetBlockGroup), true);
            Method(h, typeof(Schedule), nameof(Schedule.Assign), false);
            Method(h, typeof(Schedule), nameof(Schedule.Unassign), false);
            Method(h, typeof(Schedule), nameof(Schedule.ShiftTimetable), false);
            Method(h, typeof(Schedule), nameof(Schedule.RotateBlocks), false);
            Method(h, typeof(Schedule), nameof(Schedule.RemoveTimetable), false);
            Method(h, typeof(ScheduleManager), nameof(ScheduleManager.AddDefaultSchedule), false);
            Method(h, typeof(ScheduleManager), nameof(ScheduleManager.DuplicateSchedule), false);
            Method(h, typeof(ScheduleManager), nameof(ScheduleManager.DeleteSchedule), false);
            // panels that write their target's fields directly (no method to share): the fields that changed go
            WatchFields(h, typeof(TimerSideScreen), "targetTimedSwitch", "ChangeSetting", "ToggleMode", "UpdateDurationValueFromTextInput");
            WatchFields(h, typeof(TimeRangeSideScreen), "targetTimedSwitch", "ChangeSetting");
            Method(h, typeof(LogicTimerSensor), nameof(LogicTimerSensor.ResetTimer), false);
            // what a duplicant's errand finally applies on the host
            HostMirror(h, typeof(BuildingEnabledButton), "set_IsEnabled");
            HostMirror(h, typeof(Door), "ApplyRequestedControlState");
            HostMirror(h, typeof(Switch), "SetState", o => o is CircuitSwitch || o is LogicSwitch); // manual switches, not sensors
            Log.Info($"calls: {entries.Count} UI methods are shared");
        }

        // ================================================================== interception

        private static void UiBegin() { UiDispatch = true; depth = 0; }
        private static void UiEnd() { UiDispatch = false; depth = 0; }

        private struct Pending { public bool counted, outer; public byte[] encoded; public List<int> ids; }

        /// <summary>Registered method, any instance, any call.</summary>
        private static bool Prefix(object __instance, object[] __args, MethodBase __originalMethod, out Pending __state)
        {
            __state = default;
            if (!Active || Replaying || !UiDispatch) return true;
            __state.counted = true;
            if (depth++ > 0) return true; // nested inside a call that is already being shared
            if (!byMethod.TryGetValue(__originalMethod, out Entry e)) return true;
            byte[] call = Encode(e, __instance, __args);
            if (call == null) return true; // can't name it: stays local (logged once)
            if (CommandBus.IsHost)
            {
                __state.outer = true; __state.encoded = call; __state.ids = new List<int>();
                EntitySync.BeginRecord(__state.ids);
                return true;
            }
            CommandBus.Client.SendRelay(Proto.TargetHost, ChRequest, call);
            Requests++;
            Log.Info($"call requested: {Describe(e)}");
            return e.optimistic; // a setter: show it now (the host's answer sets the same); else wait for the host
        }

        // (Harmony runs postfixes also when the prefix skipped the original)
        private static void Postfix(Pending __state)
        {
            if (!__state.counted) return;
            depth--;
            if (!__state.outer) return;
            EntitySync.EndRecord();
            Broadcast(__state.encoded, __state.ids);
        }

        // ---- panels that set fields of their target (a timer sensor's durations...): what the handler changed

        private static readonly Dictionary<MethodBase, FieldInfo> watchTarget = new Dictionary<MethodBase, FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo[]> savedFields = new Dictionary<Type, FieldInfo[]>();
        private static int fieldDepth;
        public static long FieldSets;

        private static void WatchFields(Harmony h, Type screen, string targetField, params string[] methods)
        {
            FieldInfo tf = AccessTools.Field(screen, targetField);
            if (tf == null) { Log.Warn($"calls: {screen.Name}.{targetField} not found"); return; }
            foreach (string name in methods)
            {
                MethodInfo m = AccessTools.Method(screen, name);
                if (m == null) { Log.Warn($"calls: {screen.Name}.{name} not found"); continue; }
                try { h.Patch(m, prefix: new HarmonyMethod(typeof(Calls), nameof(FieldsPrefix)), postfix: new HarmonyMethod(typeof(Calls), nameof(FieldsPostfix))); }
                catch (Exception ex) { Log.Warn($"calls: can't patch {screen.Name}.{name}: {ex.Message}"); continue; }
                watchTarget[m] = tf;
            }
        }

        /// <summary>The saved settings of a component: its [Serialize] fields of a kind a call can carry.</summary>
        private static FieldInfo[] SavedFields(Type t)
        {
            if (savedFields.TryGetValue(t, out FieldInfo[] fs)) return fs;
            var list = new List<FieldInfo>();
            for (Type c = t; c != null && c != typeof(KMonoBehaviour); c = c.BaseType)
                foreach (FieldInfo f in c.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (f.IsStatic || f.GetCustomAttributes(typeof(KSerialization.Serialize), true).Length == 0) continue;
                    Type ft = f.FieldType;
                    if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string)) list.Add(f);
                }
            return savedFields[t] = list.ToArray();
        }

        private struct FieldSnap { public bool counted; public Component target; public FieldInfo[] fields; public object[] values; }

        private static void FieldsPrefix(object __instance, MethodBase __originalMethod, out FieldSnap __state)
        {
            __state = default;
            if (!Active || Replaying || !UiDispatch) return;
            __state.counted = true;
            if (fieldDepth++ > 0) return; // inside a watched handler already: the outer one sends
            if (!watchTarget.TryGetValue(__originalMethod, out FieldInfo tf) || !(tf.GetValue(__instance) is Component target) || target == null) return;
            __state.target = target;
            __state.fields = SavedFields(target.GetType());
            __state.values = new object[__state.fields.Length];
            for (int i = 0; i < __state.fields.Length; i++) __state.values[i] = __state.fields[i].GetValue(target);
        }

        private static void FieldsPostfix(FieldSnap __state)
        {
            if (!__state.counted) return;
            fieldDepth--;
            if (__state.target == null) return;
            var changed = new List<int>();
            for (int i = 0; i < __state.fields.Length; i++)
                if (!Equals(__state.fields[i].GetValue(__state.target), __state.values[i])) changed.Add(i);
            if (changed.Count == 0) return;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Type t = __state.target.GetType();
            w.Write((byte)3); w.Write(t.FullName);
            if (!WriteInstance(w, __state.target)) { NotSupported("fields of " + t.Name); return; }
            w.Write(changed.Count);
            foreach (int i in changed)
            {
                FieldInfo f = __state.fields[i];
                w.Write(f.Name);
                if (!WriteValue(w, f.GetValue(__state.target), f.FieldType)) { NotSupported($"field {t.Name}.{f.Name}"); return; }
            }
            w.Flush();
            byte[] call = ms.ToArray();
            FieldSets++;
            if (CommandBus.IsHost) { Broadcast(call, new List<int>()); return; }
            CommandBus.Client.SendRelay(Proto.TargetHost, ChRequest, call); // already set here: the server's answer sets the same
            Requests++;
            Log.Info($"call requested: set {string.Join(", ", changed.Select(i => t.Name + "." + __state.fields[i].Name).ToArray())}");
        }

        // ---- results that a duplicant's errand applies on the host (a toggle, a door setting): every call there
        // is sent, whoever made it

        private static readonly Dictionary<MethodBase, Func<object, bool>> mirrorFilter = new Dictionary<MethodBase, Func<object, bool>>();

        private static void HostMirror(Harmony h, Type t, string name, Func<object, bool> filter = null)
        {
            MethodInfo m = AccessTools.Method(t, name);
            if (m == null) { Log.Warn($"calls: {t.Name}.{name} not found"); return; }
            if (byMethod.ContainsKey(m)) return;
            var e = new Entry { index = entries.Count, method = m, parameters = m.GetParameters(), optimistic = true };
            try { h.Patch(m, postfix: new HarmonyMethod(typeof(Calls), nameof(MirrorPostfix))); }
            catch (Exception ex) { Log.Warn($"calls: can't patch {t.Name}.{name}: {ex.Message}"); return; }
            entries.Add(e);
            byMethod[m] = e;
            mirrorFilter[m] = filter;
        }

        private static void MirrorPostfix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (!Active || !CommandBus.IsHost || EntitySync.GuestsInGame == 0 || !byMethod.TryGetValue(__originalMethod, out Entry e)) return;
            if (mirrorFilter.TryGetValue(__originalMethod, out var f) && f != null && !f(__instance)) return;
            if (__instance is KMonoBehaviour kmb && !kmb.isSpawned) return; // setting itself up: the guest's copy does that too
            byte[] call = Encode(e, __instance, __args);
            if (call == null) return;
            Broadcast(call, new List<int>());
            mirrorLogged.TryGetValue(__originalMethod, out int n);
            if (n < 5)
            {
                mirrorLogged[__originalMethod] = n + 1;
                var k = (__instance as Component)?.GetComponent<KPrefabID>();
                Log.Info($"call mirrored: {Describe(e)} on #{(k != null ? k.InstanceID : -1)} {(__instance as Component)?.name}, from {new System.Diagnostics.StackTrace(2, false).ToString().Split('\n').Take(4).Aggregate((a, b) => a + " <" + b.Trim())}");
            }
        }

        private static readonly Dictionary<MethodBase, int> mirrorLogged = new Dictionary<MethodBase, int>();

        private static void Broadcast(byte[] call, List<int> ids)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(ids.Count);
                foreach (int id in ids) w.Write(id);
                w.Write(call);
                w.Flush();
                CommandBus.Client.SendRelay(Proto.TargetAll, ChApplied, ms.ToArray());
            }
        }

        // ---- the buttons of a selected object (UserMenu): a callback on a component of that object

        private static void WrapButton(GameObject go, KIconButtonMenu.ButtonInfo button)
        {
            System.Action original = button.onClick;
            if (original == null) return;
            button.onClick = () => MenuClick(original);
        }

        internal static void MenuClick(System.Action original)
        {
            if (!Active || Replaying) { original(); return; }
            byte[] call = EncodeDelegate(original);
            if (call == null) { original(); return; }
            if (CommandBus.IsHost)
            {
                var ids = new List<int>();
                EntitySync.BeginRecord(ids);
                try { Replaying = true; original(); }
                finally { Replaying = false; EntitySync.EndRecord(); }
                Broadcast(call, ids);
                return;
            }
            CommandBus.Client.SendRelay(Proto.TargetHost, ChRequest, call);
            Requests++;
            Log.Info($"call requested: button {original.Method.DeclaringType?.Name}.{original.Method.Name}");
        }

        // ================================================================== receive

        public static void OnRelay(ushort from, byte channel, byte[] data)
        {
            if (channel == ChRequest && CommandBus.IsHost)
            {
                var ids = new List<int>();
                string what = null;
                EntitySync.BeginRecord(ids);
                try { what = Execute(data); }
                finally { EntitySync.EndRecord(); }
                Executed++;
                Log.Info($"call from #{from}: {what}");
                if (what != null && !what.StartsWith("failed")) Broadcast(data, ids);
            }
            else if (channel == ChApplied && !CommandBus.IsHost && CommandBus.InGame)
            {
                var r = new BinaryReader(new MemoryStream(data));
                int n = r.ReadInt32();
                var ids = new List<int>(n);
                for (int i = 0; i < n; i++) ids.Add(r.ReadInt32());
                byte[] call = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position));
                EntitySync.BeginScript(ids);
                string what;
                try { what = Execute(call); }
                finally { EntitySync.EndScript(); }
                Replayed++;
                Log.Info($"call applied: {what}");
            }
        }

        private static string Execute(byte[] call)
        {
            var r = new BinaryReader(new MemoryStream(call));
            byte kind = r.ReadByte();
            Replaying = true;
            try
            {
                if (kind == 1)
                {
                    int index = r.ReadInt32();
                    string name = r.ReadString();
                    if (index < 0 || index >= entries.Count || entries[index].method.Name != name) return $"failed: unknown method {name}";
                    Entry e = entries[index];
                    object target = ReadInstance(r, e.method.DeclaringType);
                    if (!e.method.IsStatic && target == null) return $"failed: {Describe(e)} - its object isn't here ({InstanceText(call)})";
                    var args = new object[e.parameters.Length];
                    for (int i = 0; i < args.Length; i++) args[i] = ReadValue(r, e.parameters[i].ParameterType);
                    e.method.Invoke(target, args);
                    return $"{Describe(e)}({string.Join(", ", args.Select(a => a?.ToString() ?? "null").ToArray())})";
                }
                if (kind == 2)
                {
                    string typeName = r.ReadString(), methodName = r.ReadString();
                    Type t = AccessTools.TypeByName(typeName);
                    object target = t != null ? ReadInstance(r, t) : null;
                    MethodInfo m = t != null ? AccessTools.Method(t, methodName, Type.EmptyTypes) : null;
                    if (target == null || m == null) return $"failed: button {typeName}.{methodName} - not here";
                    m.Invoke(target, null);
                    return $"button {typeName}.{methodName}";
                }
                if (kind == 3)
                {
                    string typeName = r.ReadString();
                    Type t = AccessTools.TypeByName(typeName);
                    object target = t != null ? ReadInstance(r, t) : null;
                    if (target == null) return $"failed: fields of {typeName} - not here";
                    int n = r.ReadInt32();
                    var set = new List<string>();
                    for (int i = 0; i < n; i++)
                    {
                        string name = r.ReadString();
                        FieldInfo f = AccessTools.Field(t, name);
                        if (f == null) return $"failed: {typeName}.{name} not found";
                        object v = ReadValue(r, f.FieldType);
                        f.SetValue(target, v);
                        set.Add($"{t.Name}.{name}={v}");
                    }
                    return "set " + string.Join(", ", set.ToArray());
                }
                return "failed: unknown call kind " + kind;
            }
            catch (Exception ex) { return "failed: " + (ex.InnerException ?? ex).Message; }
            finally { Replaying = false; }
        }

        private static string Describe(Entry e) => $"{e.method.DeclaringType?.Name}.{e.method.Name}";

        private static string InstanceText(byte[] call)
        {
            try
            {
                var r = new BinaryReader(new MemoryStream(call));
                r.ReadByte(); r.ReadInt32(); r.ReadString();
                byte kind = r.ReadByte();
                return kind == InstComponent ? $"#{r.ReadInt32()} {r.ReadString()}" : "kind " + kind;
            }
            catch { return "?"; }
        }

        // ================================================================== encoding

        private static byte[] Encode(Entry e, object instance, object[] args)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((byte)1); w.Write(e.index); w.Write(e.method.Name);
            if (!e.method.IsStatic && !WriteInstance(w, instance)) return NotSupported(Describe(e) + " on " + instance?.GetType().Name);
            for (int i = 0; i < e.parameters.Length; i++)
                if (!WriteValue(w, i < args.Length ? args[i] : null, e.parameters[i].ParameterType))
                    return NotSupported($"{Describe(e)} argument {e.parameters[i].ParameterType.Name}");
            w.Flush();
            return ms.ToArray();
        }

        private static byte[] EncodeDelegate(System.Action d)
        {
            MethodInfo m = d.Method;
            if (d.Target == null || m.GetParameters().Length != 0 || !(d.Target is Component))
                return NotSupported($"button {m.DeclaringType?.Name}.{m.Name}");
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((byte)2); w.Write(m.DeclaringType.FullName); w.Write(m.Name);
            if (!WriteInstance(w, d.Target)) return NotSupported($"button {m.DeclaringType?.Name}.{m.Name} on {d.Target.GetType().Name}");
            w.Flush();
            return ms.ToArray();
        }

        private static byte[] NotSupported(string what)
        {
            Unsupported++;
            if (unsupportedSeen.Add(what)) Log.Warn("calls: stays local (can't name it for the host): " + what);
            return null;
        }

        private const byte InstComponent = 1, InstSingleton = 2, InstSchedule = 3;

        /// <summary>A component of a saved object (by its InstanceID and type), a singleton, or a schedule.</summary>
        internal static bool WriteInstance(BinaryWriter w, object o)
        {
            if (o is Schedule s)
            {
                int i = ScheduleManager.Instance != null ? ScheduleManager.Instance.GetSchedules().IndexOf(s) : -1;
                if (i < 0) return false;
                w.Write(InstSchedule); w.Write(i);
                return true;
            }
            if (o is Component c && c != null)
            {
                var k = c.GetComponent<KPrefabID>();
                if (k != null && k.InstanceID >= 0 && k.InstanceID < EntitySync.LocalIdBase)
                {
                    Component[] same = c.GetComponents(c.GetType());
                    w.Write(InstComponent); w.Write(k.InstanceID); w.Write(c.GetType().FullName); w.Write((byte)Array.IndexOf(same, c));
                    return true;
                }
                if (SingletonOf(c.GetType()) == (object)c) { w.Write(InstSingleton); w.Write(c.GetType().FullName); return true; }
            }
            return false;
        }

        internal static object ReadInstance(BinaryReader r, Type declared)
        {
            byte kind = r.ReadByte();
            switch (kind)
            {
                case InstSchedule:
                {
                    int i = r.ReadInt32();
                    List<Schedule> all = ScheduleManager.Instance?.GetSchedules();
                    return all != null && i < all.Count ? all[i] : null;
                }
                case InstComponent:
                {
                    int id = r.ReadInt32();
                    Type t = AccessTools.TypeByName(r.ReadString()) ?? declared;
                    int idx = r.ReadByte();
                    KPrefabID k = KPrefabIDTracker.Get().GetInstance(id);
                    if (k == null || t == null) return null;
                    Component[] same = k.GetComponents(t);
                    return idx < same.Length ? same[idx] : null;
                }
                case InstSingleton:
                {
                    Type t = AccessTools.TypeByName(r.ReadString());
                    return t != null ? SingletonOf(t) : null;
                }
                default: return null;
            }
        }

        private static object SingletonOf(Type t)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (string name in new[] { "Instance", "instance" })
            {
                PropertyInfo p = t.GetProperty(name, F);
                if (p != null && t.IsAssignableFrom(p.PropertyType)) return p.GetValue(null, null);
                FieldInfo f = t.GetField(name, F);
                if (f != null && t.IsAssignableFrom(f.FieldType)) return f.GetValue(null);
            }
            return null;
        }

        private static readonly Dictionary<Type, MethodInfo> resourceGetters = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, FieldInfo> resourceSets = new Dictionary<Type, FieldInfo>();

        /// <summary>A Db resource (Tech, ChoreGroup, ScheduleGroup, Skill, ...) by its id: find the Db set of that type.</summary>
        internal static Resource FindResource(Type t, string id)
        {
            Db db = Db.Get();
            if (!resourceSets.TryGetValue(t, out FieldInfo set))
            {
                // the set of exactly this type (a ResourceSet<Resource> would match anything, and find nothing)
                set = null;
                FieldInfo loose = null;
                foreach (FieldInfo f in typeof(Db).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    for (Type b = f.FieldType; b != null; b = b.BaseType)
                    {
                        if (!b.IsGenericType || b.GetGenericTypeDefinition() != typeof(ResourceSet<>)) continue;
                        Type arg = b.GetGenericArguments()[0];
                        if (arg == t && set == null) set = f;
                        else if (arg != typeof(Resource) && arg.IsAssignableFrom(t) && loose == null) loose = f;
                        break;
                    }
                resourceSets[t] = set = set ?? loose;
            }
            if (set == null) return null;
            object s = set.GetValue(db);
            if (!resourceGetters.TryGetValue(t, out MethodInfo get))
                resourceGetters[t] = get = AccessTools.Method(s.GetType(), "TryGet", new[] { typeof(string) }) ?? AccessTools.Method(s.GetType(), "Get", new[] { typeof(string) });
            return get?.Invoke(s, new object[] { id }) as Resource;
        }

        private const byte VNull = 0, VBool = 1, VInt = 2, VFloat = 3, VString = 4, VTag = 5, VHash = 6, VPriority = 7, VComponent = 8,
                           VGameObject = 9, VResource = 10, VRecipe = 11, VIdentity = 12, VTags = 13, VEnum = 14, VDouble = 15, VLong = 16, VSchedule = 17;

        private static bool WriteValue(BinaryWriter w, object v, Type declared)
        {
            switch (v)
            {
                case null: w.Write(VNull); return true;
                case bool b: w.Write(VBool); w.Write(b); return true;
                case Enum en: w.Write(VEnum); w.Write(Convert.ToInt64(en)); return true;
                case int i: w.Write(VInt); w.Write(i); return true;
                case byte by: w.Write(VInt); w.Write((int)by); return true;
                case float f: w.Write(VFloat); w.Write(f); return true;
                case double d: w.Write(VDouble); w.Write(d); return true;
                case long l: w.Write(VLong); w.Write(l); return true;
                case string s: w.Write(VString); w.Write(s); return true;
                case Tag t: w.Write(VTag); w.Write(t.Name ?? ""); return true;
                case HashedString h: w.Write(VHash); w.Write(h.HashValue); return true;
                case PrioritySetting p: w.Write(VPriority); w.Write((int)p.priority_class); w.Write(p.priority_value); return true;
                case ComplexRecipe cr: w.Write(VRecipe); w.Write(cr.id); return true;
                case Resource res: w.Write(VResource); w.Write(res.Id); return true;
                case Schedule sc:
                {
                    int i = ScheduleManager.Instance != null ? ScheduleManager.Instance.GetSchedules().IndexOf(sc) : -1;
                    if (i < 0) return false;
                    w.Write(VSchedule); w.Write(i); return true;
                }
                case IEnumerable<Tag> tags:
                {
                    var list = tags.ToList();
                    w.Write(VTags); w.Write(list.Count);
                    foreach (Tag t in list) w.Write(t.Name ?? "");
                    return true;
                }
                case GameObject go:
                {
                    var k = go.GetComponent<KPrefabID>();
                    if (k == null) return false;
                    w.Write(VGameObject); w.Write(k.InstanceID); return true;
                }
            }
            if (v is IAssignableIdentity ident && (declared == typeof(IAssignableIdentity) || !(v is Component)))
            {
                w.Write(VIdentity); EntitySync.WriteIdentity(w, ident); return true;
            }
            if (v is Component c)
            {
                var ms = new MemoryStream();
                var tmp = new BinaryWriter(ms);
                if (!WriteInstance(tmp, c)) return false;
                tmp.Flush();
                w.Write(VComponent); w.Write(ms.ToArray().Length); w.Write(ms.ToArray());
                return true;
            }
            return false;
        }

        private static object ReadValue(BinaryReader r, Type declared)
        {
            byte kind = r.ReadByte();
            switch (kind)
            {
                case VNull: return null;
                case VBool: return r.ReadBoolean();
                case VEnum: return Enum.ToObject(declared, r.ReadInt64());
                case VInt: { int i = r.ReadInt32(); return declared == typeof(byte) ? (object)(byte)i : i; }
                case VFloat: return r.ReadSingle();
                case VDouble: return r.ReadDouble();
                case VLong: return r.ReadInt64();
                case VString: return r.ReadString();
                case VTag: return new Tag(r.ReadString());
                case VHash: return new HashedString(r.ReadInt32());
                case VPriority: return new PrioritySetting((PriorityScreen.PriorityClass)r.ReadInt32(), r.ReadInt32());
                case VRecipe: return ComplexRecipeManager.Get().GetRecipe(r.ReadString());
                case VResource:
                {
                    string id = r.ReadString();
                    return FindResource(declared, id) ?? throw new InvalidDataException($"no {declared.Name} '{id}' here");
                }
                case VSchedule:
                {
                    int i = r.ReadInt32();
                    List<Schedule> all = ScheduleManager.Instance?.GetSchedules();
                    return all != null && i < all.Count ? all[i] : null;
                }
                case VTags:
                {
                    int n = r.ReadInt32();
                    var set = new HashSet<Tag>();
                    var list = new List<Tag>();
                    for (int i = 0; i < n; i++) { var t = new Tag(r.ReadString()); set.Add(t); list.Add(t); }
                    return declared.IsAssignableFrom(typeof(HashSet<Tag>)) ? set : (object)list;
                }
                case VGameObject: return KPrefabIDTracker.Get().GetInstance(r.ReadInt32())?.gameObject;
                case VIdentity: return EntitySync.ReadIdentity(r, out _);
                case VComponent:
                {
                    byte[] b = r.ReadBytes(r.ReadInt32());
                    return ReadInstance(new BinaryReader(new MemoryStream(b)), declared);
                }
                default: throw new InvalidDataException("bad value kind " + kind);
            }
        }

        public static string Report() => $"calls: requested {Requests}, executed for guests {Executed}, replayed {Replayed}, stayed local {Unsupported}";
    }
}
