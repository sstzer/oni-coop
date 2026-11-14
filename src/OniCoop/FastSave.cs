namespace OniCoop
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Reflection.Emit;
    using HarmonyLib;
    using KSerialization;
    using TypeInfo = KSerialization.TypeInfo;
    using UnityEngine;

    /// <summary>The world serializes faster, byte for byte the same as the game writes it.
    ///
    /// A save took 1.3-2.3 s of everyone's time on the server for ~16 600 objects (and a player joining waits for the
    /// same serialization). The size has little to do with it; the per-object work does: for every object the game
    /// gets its components (a new array), asks each component's type by reflection whether it is skipped (twice), turns
    /// the type into a string; and for every one of ~540 000 templates (components and what they contain) reads each
    /// field by reflection (FieldInfo.GetValue, boxing) and calls the [OnSerializing]-style methods by reflection.
    ///
    /// Here the per-type answers are kept, field values are read by small compiled methods (DynamicMethod), callbacks
    /// are called through compiled delegates. What is written and in which order is unchanged; a self-check (autotest)
    /// serializes the world both ways in the same frame and compares the bytes.</summary>
    internal static class FastSave
    {
        public static bool Enabled => CoopMod.Settings == null || CoopMod.Settings.fastSave;
        public static bool ForceOriginal; // self-check: the game's own code this time

        // ================================================================== objects (SaveLoadRoot.SaveWithoutTransform)

        private sealed class CompInfo { public bool skip; public string name; }
        private static readonly Dictionary<Type, CompInfo> compInfo = new Dictionary<Type, CompInfo>();
        // one list per nesting level: saving an object can save others inside it (a storage writes what it stores)
        private static readonly List<List<KMonoBehaviour>> compLists = new List<List<KMonoBehaviour>>();
        private static int depth;
        private static readonly FieldInfo ManagersField = AccessTools.Field(typeof(SaveLoadRoot), "serializableComponentManagers");

        private static CompInfo Info(Type t)
        {
            if (!compInfo.TryGetValue(t, out CompInfo c))
                compInfo[t] = c = new CompInfo { skip = t.IsDefined(typeof(SkipSaveFileSerialization), false), name = t.ToString() };
            return c;
        }

        private static bool SaveWithoutTransformPrefix(SaveLoadRoot __instance, BinaryWriter writer)
        {
            if (!Enabled || ForceOriginal) return true;
            GameObject go = __instance.gameObject;
            if (compLists.Count <= depth) compLists.Add(new List<KMonoBehaviour>(32));
            List<KMonoBehaviour> comps = compLists[depth];
            depth++;
            try
            {
            comps.Clear();
            go.GetComponents(comps);
            var managers = ManagersField?.GetValue(null) as Dictionary<string, ISerializableComponentManager>;
            int num = 0;
            for (int i = 0; i < comps.Count; i++) if ((object)comps[i] != null && !Info(comps[i].GetType()).skip) num++;
            if (managers != null) foreach (var kv in managers) if (kv.Value.Has(go)) num++;
            writer.Write(num);
            Stream bs = writer.BaseStream;
            for (int i = 0; i < comps.Count; i++)
            {
                KMonoBehaviour c = comps[i];
                if ((object)c == null) continue;
                CompInfo info = Info(c.GetType());
                if (info.skip) continue;
                writer.WriteKleiString(info.name);
                long lengthAt = bs.Position;
                writer.Write(0);
                long start = bs.Position;
                Serializer.SerializeTypeless(c, writer);
                if (c is ISaveLoadableDetails details) details.Serialize(writer);
                long end = bs.Position;
                bs.Position = lengthAt;
                writer.Write((int)(end - start));
                bs.Position = end;
            }
            if (managers != null)
                foreach (var kv in managers)
                    if (kv.Value.Has(go)) { writer.WriteKleiString(kv.Key); kv.Value.Serialize(go, writer); }
            comps.Clear();
            Objects++;
            return false;
            }
            finally { depth--; }
        }

        // ================================================================== templates (SerializationTemplate.SerializeData)

        private sealed class Fast
        {
            public Func<object, object>[] fields, props;
            public TypeInfo[] fieldTypes, propTypes;
            public string[] fieldNames, propNames;
            public Action<object> onSerializing, onSerialized;
            public Action<object, BinaryWriter> custom;
        }

        private static readonly Dictionary<SerializationTemplate, Fast> fast = new Dictionary<SerializationTemplate, Fast>();
        public static long Objects, Templates, Fallbacks;

        private static Fast Build(SerializationTemplate t)
        {
            var f = new Fast();
            int n = t.serializableFields.Count;
            f.fields = new Func<object, object>[n]; f.fieldTypes = new TypeInfo[n]; f.fieldNames = new string[n];
            for (int i = 0; i < n; i++)
            {
                FieldInfo fi = t.serializableFields[i].field;
                f.fields[i] = FieldGetter(fi);
                f.fieldTypes[i] = t.serializableFields[i].typeInfo;
                f.fieldNames[i] = fi.Name;
            }
            n = t.serializableProperties.Count;
            f.props = new Func<object, object>[n]; f.propTypes = new TypeInfo[n]; f.propNames = new string[n];
            for (int i = 0; i < n; i++)
            {
                PropertyInfo pi = t.serializableProperties[i].property;
                f.props[i] = PropertyGetter(pi);
                f.propTypes[i] = t.serializableProperties[i].typeInfo;
                f.propNames[i] = pi.Name;
            }
            f.onSerializing = Caller(t.onSerializing);
            f.onSerialized = Caller(t.onSerialized);
            if (t.customSerialize != null)
            {
                MethodInfo m = t.customSerialize;
                f.custom = (o, w) => m.Invoke(o, new object[] { w }); // (rare: a handful of types)
            }
            return f;
        }

        private static Func<object, object> FieldGetter(FieldInfo fi)
        {
            Type owner = fi.DeclaringType;
            try
            {
                var dm = new DynamicMethod("coop_get_" + fi.Name, typeof(object), new[] { typeof(object) }, owner.Module, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                if (owner.IsValueType) il.Emit(OpCodes.Unbox, owner); else il.Emit(OpCodes.Castclass, owner);
                il.Emit(OpCodes.Ldfld, fi);
                if (fi.FieldType.IsValueType) il.Emit(OpCodes.Box, fi.FieldType);
                il.Emit(OpCodes.Ret);
                return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
            }
            catch (Exception) { Fallbacks++; return fi.GetValue; }
        }

        private static Func<object, object> PropertyGetter(PropertyInfo pi)
        {
            MethodInfo get = pi.GetGetMethod(true);
            Type owner = pi.DeclaringType;
            if (get == null || owner.IsValueType) { Fallbacks++; return o => pi.GetValue(o, null); }
            try
            {
                var dm = new DynamicMethod("coop_getp_" + pi.Name, typeof(object), new[] { typeof(object) }, owner.Module, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, owner);
                il.Emit(get.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, get);
                if (pi.PropertyType.IsValueType) il.Emit(OpCodes.Box, pi.PropertyType);
                il.Emit(OpCodes.Ret);
                return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
            }
            catch (Exception) { Fallbacks++; return o => pi.GetValue(o, null); }
        }

        private static Action<object> Caller(MethodInfo m)
        {
            if (m == null) return null;
            Type owner = m.DeclaringType;
            if (owner.IsValueType || m.GetParameters().Length != 0) { Fallbacks++; return o => m.Invoke(o, null); }
            try
            {
                var dm = new DynamicMethod("coop_call_" + m.Name, typeof(void), new[] { typeof(object) }, owner.Module, true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, owner);
                il.Emit(m.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, m);
                if (m.ReturnType != typeof(void)) il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ret);
                return (Action<object>)dm.CreateDelegate(typeof(Action<object>));
            }
            catch (Exception) { Fallbacks++; return o => m.Invoke(o, null); }
        }

        private static bool SerializeDataPrefix(SerializationTemplate __instance, object obj, BinaryWriter writer)
        {
            if (!Enabled || ForceOriginal) return true;
            if (!fast.TryGetValue(__instance, out Fast f)) fast[__instance] = f = Build(__instance);
            Templates++;
            f.onSerializing?.Invoke(obj);
            for (int i = 0; i < f.fields.Length; i++)
            {
                try { writer.WriteValue(f.fieldTypes[i], f.fields[i](obj)); }
                catch (Exception e)
                {
                    string text = $"Error occurred while serializing field {f.fieldNames[i]} on template {__instance.serializableType.Name}";
                    Debug.LogError(text);
                    throw new ArgumentException(text, e);
                }
            }
            for (int i = 0; i < f.props.Length; i++)
            {
                try { writer.WriteValue(f.propTypes[i], f.props[i](obj)); }
                catch (Exception e)
                {
                    string text = $"Error occurred while serializing property {f.propNames[i]} on template {__instance.serializableType.Name}";
                    Debug.LogError(text);
                    throw new ArgumentException(text, e);
                }
            }
            f.custom?.Invoke(obj, writer);
            f.onSerialized?.Invoke(obj);
            return false;
        }

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(SaveLoadRoot), nameof(SaveLoadRoot.SaveWithoutTransform)), prefix: new HarmonyMethod(typeof(FastSave), nameof(SaveWithoutTransformPrefix)) { priority = Priority.Low });
                h.Patch(AccessTools.Method(typeof(SerializationTemplate), nameof(SerializationTemplate.SerializeData)), prefix: new HarmonyMethod(typeof(FastSave), nameof(SerializeDataPrefix)) { priority = Priority.Low });
            }
            catch (Exception e) { Log.Warn("fast save: can't patch: " + e.Message); }
        }

        /// <summary>Test: serialize the world the game's way and this way in the same frame; the bytes must be the same.</summary>
        public static void SelfCheck()
        {
            MethodInfo save = AccessTools.Method(typeof(SaveLoader), "Save", new[] { typeof(BinaryWriter) });
            if (save == null || SaveLoader.Instance == null) return;
            byte[] Run(bool original, out long ms)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ForceOriginal = original;
                try
                {
                    var stream = new MemoryStream(64 << 20);
                    using (var w = new BinaryWriter(stream)) { save.Invoke(SaveLoader.Instance, new object[] { w }); w.Flush(); ms = sw.ElapsedMilliseconds; return stream.ToArray(); }
                }
                finally { ForceOriginal = false; }
            }
            byte[] a = Run(true, out long msA);
            byte[] a2 = Run(true, out long msA2);
            byte[] b = Run(false, out long msB);
            int Diff(byte[] x, byte[] y)
            {
                int n = Math.Min(x.Length, y.Length);
                for (int i = 0; i < n; i++) if (x[i] != y[i]) return i;
                return x.Length != y.Length ? n : -1;
            }
            int same = Diff(a, a2), diff = Diff(a2, b);
            Log.Info($"fast save: the game's way twice: {(same < 0 ? "identical" : $"differ at {same} ({a.Length} vs {a2.Length} bytes)")}; " +
                     (diff < 0 ? $"self-check OK, {b.Length / 1048576.0:F1} MB identical to the game's" : $"self-check FAILED: {a2.Length} vs {b.Length} bytes, first difference at {diff}") +
                     $"; the game's way {msA}/{msA2} ms, this way {msB} ms");
        }

        public static string Report() => $"fast save: {Objects} objects, {Templates} templates, {Fallbacks} members by plain reflection";
    }
}
