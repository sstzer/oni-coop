namespace OniCoop
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Reflection;
    using System.Runtime.Serialization;
    using HarmonyLib;
    using Klei.AI;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>What each object "has on it", as the server's game decided:
    /// <list type="bullet">
    /// <item>status items: the icons over buildings, duplicants and critters, the lines in their info panel, and the
    /// notifications some of them raise ("operating", "pipe blocked", "idle", "can't reach food"...);</item>
    /// <item>effects: "happy", "good eats", "slept in a bed", "wild"..., with their time left. They change attributes
    /// (what the info panel shows as rates and modifiers), and each shown one is also a status item.</item>
    /// </list>
    /// A guest runs little of the logic behind these, so its own were wrong both ways. The server sends an object's full
    /// list of each when it changes. A guest makes its object match, and from then on its own code may only add what the
    /// server's list has and remove what it doesn't. That also lets the guest's code supply a status item's data when the
    /// server couldn't send it (data is sent when it can be named on the other side: a number, text, enum, tag, Db
    /// resource, a component / state machine / effect of a synced object).</summary>
    internal static class StatusSync
    {
        public const byte Ch = 16;

        // guest: what StatusSync itself is changing right now (that is allowed; side effects of it are not)
        private static StatusItemGroup applyingGroup;
        private static Effects applyingFx;
        private static int applyingEffect;
        private static string applyingEffectItem;

        private const byte KStatus = 1, KEffects = 2, KStatusDelta = 3;

        private static bool HostActive => CommandBus.Active && CommandBus.IsHost && EntitySync.GuestsInGame > 0;

        private static bool Synced(GameObject go)
        {
            if (go == null) return false;
            var k = go.GetComponent<KPrefabID>();
            return k != null && k.InstanceID >= 0 && k.InstanceID < EntitySync.LocalIdBase && go.GetComponent<SaveLoadRoot>() != null;
        }

        private static readonly AccessTools.FieldRef<KPrefabIDTracker, Dictionary<int, KPrefabID>> TrackerMap =
            AccessTools.FieldRefAccess<KPrefabIDTracker, Dictionary<int, KPrefabID>>("prefabIdMap");

        private static readonly AccessTools.FieldRef<Effects, List<EffectInstance>> EffectList =
            AccessTools.FieldRefAccess<Effects, List<EffectInstance>>("effects");

        // ================================================================== server

        private static readonly HashSet<GameObject> dirtyStatus = new HashSet<GameObject>();
        private static readonly HashSet<GameObject> dirtyEffects = new HashSet<GameObject>();
        private static readonly Dictionary<long, int> lastSent = new Dictionary<long, int>(); // kind<<32|id -> hash of the list sent
        private static readonly Dictionary<long, float> lastSentAt = new Dictionary<long, float>();
        private static readonly Dictionary<int, List<int>> sentSigs = new Dictionary<int, List<int>>(); // status entries as the guests have them
        public static long Deltas, Fulls;
        private static readonly HashSet<GameObject> carry = new HashSet<GameObject>();
        private static float nextSend;
        public static long Sent, SentEntries, SentEffects, Unnameable, SentBytes, Unchanged, StatusBytes;
        private static readonly Dictionary<string, long> bytesBy = new Dictionary<string, long>(), listsBy = new Dictionary<string, long>();
        private static readonly Dictionary<string, int> unnameableBy = new Dictionary<string, int>();

        public static void Changed(StatusItemGroup g)
        {
            if (g != null && g.gameObject != null && HostActive) dirtyStatus.Add(g.gameObject);
        }

        public static void EffectsChanged(Effects e)
        {
            if (e != null && HostActive) dirtyEffects.Add(e.gameObject);
        }

        /// <summary>Server: a synced object spawned. Send its lists even if they never change, so the guest's code on it
        /// is held to them from the start.</summary>
        public static void Spawned(KPrefabID k)
        {
            if (!HostActive || k == null) return;
            if (k.GetComponent<KSelectable>() != null) dirtyStatus.Add(k.gameObject);
            if (k.GetComponent<Effects>() != null) dirtyEffects.Add(k.gameObject);
        }

        /// <summary>A guest (re)loaded: every synced object's lists again.</summary>
        public static void OnGuestJoined()
        {
            lastSent.Clear();
            lastSentAt.Clear();
            sentSigs.Clear();
            foreach (var kv in TrackerMap(KPrefabIDTracker.Get()))
            {
                if (kv.Value == null || kv.Key >= EntitySync.LocalIdBase) continue;
                if (kv.Value.GetComponent<KSelectable>() != null) dirtyStatus.Add(kv.Value.gameObject);
                if (kv.Value.GetComponent<Effects>() != null) dirtyEffects.Add(kv.Value.gameObject);
            }
        }

        public static void HostTick(float now)
        {
            if (dirtyStatus.Count == 0 && dirtyEffects.Count == 0 || now < nextSend) return;
            nextSend = now + 0.25f;
            var ms = new MemoryStream();
            var one = new MemoryStream();
            var ow = new BinaryWriter(one);
            var data = new MemoryStream();
            var dw = new BinaryWriter(data);
            int n = 0;
            using (var z = new DeflateStream(ms, CompressionMode.Compress, true))
            {
                // effects first: the status items of effects refer to them
                foreach (GameObject go in dirtyEffects)
                {
                    if (go == null || !Synced(go)) continue;
                    var fx = go.GetComponent<Effects>();
                    if (fx == null) continue;
                    int id = go.GetComponent<KPrefabID>().InstanceID;
                    List<EffectInstance> list = EffectList(fx);
                    one.SetLength(0);
                    ow.Write(KEffects);
                    ow.Write(id);
                    ow.Write((ushort)list.Count);
                    // what counts as a change: the effects and when each runs out (5 s steps), not the countdown itself
                    float clock = GameClock.Instance != null ? GameClock.Instance.GetTime() : 0f;
                    int sig = id;
                    foreach (EffectInstance ei in list)
                    {
                        ow.Write(ei.effect.Id);
                        ow.Write(ei.shouldSave);
                        ow.Write(ei.timeRemaining);
                        int ends = ei.effect.duration > 0f ? Mathf.RoundToInt((clock + ei.timeRemaining) / 5f) : 0;
                        unchecked { sig = (sig * 31 + ei.effect.IdHash.HashValue) * 31 + ends + (ei.shouldSave ? 1 : 0); }
                    }
                    if (Emit(z, one, ow, KEffects, id, sig)) { n++; SentEffects += list.Count; }
                }
                foreach (GameObject go in dirtyStatus)
                {
                    if (go == null || !Synced(go)) continue;
                    var sel = go.GetComponent<KSelectable>();
                    StatusItemGroup g = sel != null ? sel.GetStatusItemGroup() : null;
                    if (g == null) continue;
                    int id = go.GetComponent<KPrefabID>().InstanceID;
                    long key = (long)KStatus << 32 | (uint)id;
                    // an object changing faster than once a second is sent once a second
                    if (!flushAll && lastSentAt.TryGetValue(key, out float at) && now - at < 1f) { carry.Add(go); continue; }
                    var entries = new List<StatusItemGroup.Entry>();
                    foreach (StatusItemGroup.Entry e in g) if (e.item != null) entries.Add(e);
                    // what counts as a change: which items, their categories and data. The text that comes along for
                    // stand-ins doesn't (it's often a live number), so it isn't even made unless the list is sent.
                    var datas = new byte[entries.Count][];
                    var named = new bool[entries.Count];
                    var sigs = new int[entries.Count];
                    for (int i = 0; i < entries.Count; i++)
                    {
                        data.SetLength(0);
                        named[i] = WriteData(dw, entries[i].data, entries[i].item.Id);
                        dw.Flush();
                        datas[i] = data.ToArray();
                        unchecked
                        {
                            int sig = entries[i].item.Id.GetHashCode();
                            sig = sig * 31 + (entries[i].category != null ? entries[i].category.Id.GetHashCode() : 0);
                            sigs[i] = sig * 31 + Hash(datas[i], datas[i].Length);
                        }
                    }
                    // what changed since the last list sent: which of those entries went, which are new
                    one.SetLength(0);
                    var added = new List<int>();
                    if (sentSigs.TryGetValue(id, out List<int> prev))
                    {
                        var used = new bool[prev.Count];
                        for (int i = 0; i < sigs.Length; i++)
                        {
                            int m = -1;
                            for (int j = 0; j < prev.Count && m < 0; j++) if (!used[j] && prev[j] == sigs[i]) m = j;
                            if (m >= 0) used[m] = true; else added.Add(i);
                        }
                        var removed = new List<int>();
                        for (int j = 0; j < prev.Count; j++) if (!used[j]) removed.Add(j);
                        if (added.Count == 0 && removed.Count == 0) { Unchanged++; continue; }
                        ow.Write(KStatusDelta);
                        ow.Write(id);
                        ow.Write((ushort)removed.Count);
                        foreach (int j in removed) ow.Write((ushort)j);
                        for (int j = removed.Count - 1; j >= 0; j--) prev.RemoveAt(removed[j]);
                        Deltas++;
                    }
                    else
                    {
                        prev = sentSigs[id] = new List<int>();
                        for (int i = 0; i < sigs.Length; i++) added.Add(i);
                        ow.Write(KStatus);
                        ow.Write(id);
                        Fulls++;
                    }
                    ow.Write((ushort)added.Count);
                    foreach (int i in added)
                    {
                        long before = one.Position;
                        WriteEntry(ow, entries[i], datas[i], named[i]);
                        prev.Add(sigs[i]);
                        ow.Flush();
                        string ik = entries[i].item.Id;
                        bytesBy.TryGetValue(ik, out long c);
                        if (c > 0 || bytesBy.Count < 400) bytesBy[ik] = c + one.Position - before;
                    }
                    lastSentAt[key] = now;
                    string pk = go.GetComponent<KPrefabID>().PrefabTag.Name;
                    listsBy.TryGetValue(pk, out long lc);
                    if (lc > 0 || listsBy.Count < 400) listsBy[pk] = lc + 1;
                    ow.Flush();
                    z.Write(one.GetBuffer(), 0, (int)one.Length);
                    n++;
                    SentEntries += entries.Count;
                    StatusBytes += one.Length;
                }
            }
            dirtyStatus.Clear();
            foreach (GameObject go in carry) dirtyStatus.Add(go);
            carry.Clear();
            dirtyEffects.Clear();
            if (n == 0) return;
            Sent += n;
            SentBytes += ms.Length;
            CommandBus.Client.SendRelay(Proto.TargetAll, Ch, ms.ToArray());
        }

        /// <summary>Write one object's record unless it's the same as what was sent last.</summary>
        private static bool Emit(Stream z, MemoryStream one, BinaryWriter ow, byte kind, int id, int hash)
        {
            ow.Flush();
            byte[] b = one.GetBuffer();
            int len = (int)one.Length;
            long key = (long)kind << 32 | (uint)id;
            if (lastSent.TryGetValue(key, out int old) && old == hash) { Unchanged++; return false; }
            lastSent[key] = hash;
            z.Write(b, 0, len);
            return true;
        }

        private static int Hash(byte[] b, int len)
        {
            unchecked
            {
                int h = (int)2166136261;
                for (int i = 0; i < len; i++) h = (h ^ b[i]) * 16777619;
                return h;
            }
        }

        private const byte FText = 1, FLooks = 2, FParts = 4;

        private static readonly Func<StatusItem, string, object, string> ResolveName =
            AccessTools.MethodDelegate<Func<StatusItem, string, object, string>>(AccessTools.Method(typeof(StatusItem), "ResolveString"));
        private static readonly Func<StatusItem, string, object, string> ResolveTip =
            AccessTools.MethodDelegate<Func<StatusItem, string, object, string>>(AccessTools.Method(typeof(StatusItem), "ResolveTooltip"));

        /// <summary>One status entry: item id, category, data; plus the text it shows here when the guest may not be
        /// able to make that text itself (its data can't be named, or the item isn't one a guest can look up by id),
        /// plus how it looks when the item can't be looked up.</summary>
        private static void WriteEntry(BinaryWriter w, StatusItemGroup.Entry e, byte[] data, bool named)
        {
            StatusItem item = e.item;
            w.Write(item.Id);
            w.Write(e.category != null ? e.category.Id : "");
            // text: when the data can't be sent, or a guest said it couldn't use the data of this item (a reference to
            // an object it doesn't have...). Looks: when a guest said it can't look this item up.
            bool looks = looksFor.Contains(item.Id);
            byte flags = (byte)((!named || looks || textFor.Contains(item.Id) ? FText | FParts : 0) | (looks ? FLooks : 0));
            w.Write(flags);
            w.Write(data);
            if ((flags & FText) != 0)
            {
                string name = Safe(() => item.GetName(e.data), item.Name), tip = Safe(() => item.GetTooltip(e.data), item.tooltipText);
                w.Write(name);
                w.Write(tip);
                // and token by token, so a guest can show it in its own language (TextParts)
                object d = e.data;
                TextParts.WriteParts(w, TextParts.Decompose(item.Name, name, tok => ResolveName(item, tok, d)));
                TextParts.WriteParts(w, TextParts.Decompose(item.tooltipText, tip, tok => ResolveTip(item, tok, d)));
            }
            if ((flags & FLooks) != 0)
            {
                w.Write(item.iconName ?? "");
                w.Write((int)item.iconType);
                w.Write((int)item.notificationType);
                w.Write(item.allowMultiples);
                w.Write(item.render_overlay.HashValue);
                w.Write(item.status_overlays);
                w.Write(ShowWorldIcon(item));
                w.Write(item.shouldNotify);
                w.Write(item.notificationText ?? "");
                w.Write(item.notificationTooltipText ?? "");
                w.Write(item.soundPath ?? "");
            }
        }

        private static readonly HashSet<string> textFor = new HashSet<string>(), looksFor = new HashSet<string>();
        private const byte NeedText = 1, NeedLooks = 2, NeedFull = 3;
        public static long FullsAsked;

        /// <summary>Server: a guest couldn't show some entries as sent. From now on send what it needs for those items,
        /// and send those objects' lists again.</summary>
        public static void OnReport(byte[] data)
        {
            using (var r = new BinaryReader(new MemoryStream(data)))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    byte kind = r.ReadByte();
                    string item = Inflate.ReadId(r);
                    int obj = r.ReadInt32();
                    if (kind != NeedFull)
                    {
                        HashSet<string> set = kind == NeedLooks ? looksFor : textFor;
                        if (set.Add(item)) Log.Info($"status: a guest needs the {(kind == NeedLooks ? "looks and text" : "text")} of '{item}' (first on {obj})");
                    }
                    else FullsAsked++;
                    long key = (long)KStatus << 32 | (uint)obj;
                    sentSigs.Remove(obj);
                    lastSentAt.Remove(key);
                    KPrefabID k = KPrefabIDTracker.Get().GetInstance(obj);
                    if (k != null) dirtyStatus.Add(k.gameObject);
                }
            }
        }

        private static readonly AccessTools.FieldRef<StatusItem, bool> ShowWorldIcon = AccessTools.FieldRefAccess<StatusItem, bool>("showShowWorldIcon");

        private static string Safe(Func<string> f, string fallback)
        {
            try { return f() ?? fallback ?? ""; }
            catch { return fallback ?? ""; }
        }

        private const byte DNull = 0, DComponent = 1, DGameObject = 2, DInt = 3, DFloat = 4, DBool = 5, DString = 6, DEnum = 7,
                           DTag = 8, DResource = 9, DSmi = 10, DEffect = 11, DUnnameable = 255;

        private static bool WriteData(BinaryWriter w, object v, string item)
        {
            switch (v)
            {
                case null: w.Write(DNull); return true;
                case Enum en: w.Write(DEnum); w.Write(en.GetType().FullName); w.Write(Convert.ToInt64(en)); return true;
                case int i: w.Write(DInt); w.Write(i); return true;
                case float f: w.Write(DFloat); w.Write(f); return true;
                case bool b: w.Write(DBool); w.Write(b); return true;
                case string s: w.Write(DString); w.Write(s); return true;
                case Tag t: w.Write(DTag); w.Write(t.Name ?? ""); return true;
                case Resource r: w.Write(DResource); w.Write(r.GetType().FullName); w.Write(r.Id); return true;
                case EffectInstance ei:
                {
                    var k = ei.gameObject != null ? ei.gameObject.GetComponent<KPrefabID>() : null;
                    if (k != null && ei.effect != null) { w.Write(DEffect); w.Write(k.InstanceID); w.Write(ei.effect.Id); return true; }
                    break;
                }
                case GameObject go:
                {
                    var k = go != null ? go.GetComponent<KPrefabID>() : null;
                    if (k != null) { w.Write(DGameObject); w.Write(k.InstanceID); return true; }
                    break;
                }
                case StateMachine.Instance smi:
                {
                    GameObject owner = null;
                    try { owner = smi.gameObject; } catch { }
                    var k = owner != null ? owner.GetComponent<KPrefabID>() : null;
                    if (k != null) { w.Write(DSmi); w.Write(k.InstanceID); w.Write(smi.GetType().FullName); return true; }
                    break;
                }
                case Component c:
                {
                    var ms = new MemoryStream();
                    var tmp = new BinaryWriter(ms);
                    if (Calls.WriteInstance(tmp, c)) { tmp.Flush(); w.Write(DComponent); w.Write((int)ms.Length); w.Write(ms.ToArray()); return true; }
                    break;
                }
            }
            w.Write(DUnnameable);
            Unnameable++;
            string key = item + ":" + (v?.GetType().Name ?? "?");
            unnameableBy.TryGetValue(key, out int count);
            if (count > 0 || unnameableBy.Count < 40) unnameableBy[key] = count + 1;
            return false;
        }

        private static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        /// <summary>A type by name, looked up once (AccessTools.TypeByName searches every assembly, with garbage).</summary>
        private static Type TypeOf(string name)
        {
            if (!typeCache.TryGetValue(name, out Type t)) typeCache[name] = t = AccessTools.TypeByName(name);
            return t;
        }

        private static bool ReadData(BinaryReader r, out object v)
        {
            v = null;
            byte kind = r.ReadByte();
            switch (kind)
            {
                case DNull: return true;
                case DEnum:
                {
                    Type t = TypeOf(Inflate.ReadId(r)); long x = r.ReadInt64();
                    if (t == null) return false;
                    v = Enum.ToObject(t, x); return true;
                }
                case DInt: v = r.ReadInt32(); return true;
                case DFloat: v = r.ReadSingle(); return true;
                case DBool: v = r.ReadBoolean(); return true;
                case DString: v = Inflate.ReadId(r); return true;
                case DTag: v = new Tag(Inflate.ReadId(r)); return true;
                case DResource:
                {
                    Type t = TypeOf(Inflate.ReadId(r)); string id = Inflate.ReadId(r);
                    v = t != null ? Calls.FindResource(t, id) : null;
                    return v != null;
                }
                case DEffect:
                {
                    KPrefabID k = KPrefabIDTracker.Get().GetInstance(r.ReadInt32());
                    string effect = Inflate.ReadId(r);
                    var fx = k != null ? k.GetComponent<Effects>() : null;
                    v = fx != null ? fx.Get(effect) : null;
                    return v != null;
                }
                case DGameObject: v = KPrefabIDTracker.Get().GetInstance(r.ReadInt32())?.gameObject; return v != null;
                case DSmi:
                {
                    KPrefabID k = KPrefabIDTracker.Get().GetInstance(r.ReadInt32());
                    Type t = TypeOf(Inflate.ReadId(r));
                    var ctl = k != null ? k.GetComponent<StateMachineController>() : null;
                    v = ctl != null && t != null ? ctl.GetSMI(t) : null;
                    return v != null;
                }
                case DComponent:
                {
                    byte[] b = r.ReadBytes(r.ReadInt32());
                    v = Calls.ReadInstance(new BinaryReader(new MemoryStream(b)), typeof(Component));
                    return v != null;
                }
                default: return false;
            }
        }

        // ================================================================== guest

        public static long Applied, AppliedEffects, Unknown, UnknownEffects, SkippedData, Blocked, BlockedEffects, Waited;

        /// <summary>Guest: the ids in the server's latest lists for each object. Local code may add only these and
        /// remove only others; an object without a list yet is left to local code.</summary>
        private static readonly Dictionary<int, HashSet<string>> serverHas = new Dictionary<int, HashSet<string>>();
        private static readonly Dictionary<int, HashSet<int>> serverEffects = new Dictionary<int, HashSet<int>>(); // effect IdHash values

        private struct Want
        {
            public string id;
            public StatusItem item;          // the real item, when this guest has it (null: only the stand-in)
            public StatusItemCategory cat;
            public object data;              // KeepLocal: use the guest's own entry of this item, if it has one
            public FrozenStatusItem frozen;  // the stand-in showing the server's text, if sent
            public FrozenText text;
        }

        private struct WantEffect
        {
            public string id;
            public bool save;
            public float time;
        }

        private sealed class ServerEffects
        {
            public List<WantEffect> list;
            public float clock; // game time when it arrived
        }

        private static readonly Dictionary<int, ServerEffects> serverEffectLists = new Dictionary<int, ServerEffects>();
        private static readonly List<int> checkIds = new List<int>();
        private static int checkCursor;
        public static long EffectDrift;
        private static int driftLogged;

        private sealed class Waiting
        {
            public List<Want> status;
            public List<WantEffect> effects;
            public float since;
        }

        /// <summary>Lists that arrived before their object did (objects and these lists travel separately).</summary>
        private static readonly Dictionary<int, Waiting> waiting = new Dictionary<int, Waiting>();
        private static int destroying;
        private static readonly Dictionary<string, int> unknownBy = new Dictionary<string, int>();

        // ---- names -> objects

        private static Dictionary<string, StatusItem> items;
        private static bool scannedStatics;

        /// <summary>Status items by id: every Db set of them, static ones (found by a one-time scan the first time an
        /// id is missing), plus whatever this guest's own code shows us.</summary>
        private static StatusItem FindItem(string id)
        {
            if (items == null)
            {
                items = new Dictionary<string, StatusItem>();
                Db db = Db.Get();
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (System.Reflection.MemberInfo m in typeof(Db).GetMembers(F))
                {
                    object set = m is FieldInfo f ? f.GetValue(db) : m is PropertyInfo p && p.GetIndexParameters().Length == 0 ? SafeGet(p, db) : null;
                    if (set is ResourceSet<StatusItem> rs)
                        foreach (StatusItem s in rs.resources) if (s != null && !items.ContainsKey(s.Id)) items[s.Id] = s;
                }
            }
            if (items.TryGetValue(id, out StatusItem item)) return item;
            if (!scannedStatics)
            {
                scannedStatics = true;
                ScanStatics();
                if (items.TryGetValue(id, out item)) return item;
            }
            return null;
        }

        private static void ScanStatics()
        {
            int found = 0;
            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(Game).Assembly))
            {
                if (t.ContainsGenericParameters) continue;
                FieldInfo[] fields;
                try { fields = t.GetFields(S); } catch { continue; }
                foreach (FieldInfo f in fields)
                {
                    if (f.FieldType != typeof(StatusItem)) continue;
                    try
                    {
                        if (f.GetValue(null) is StatusItem s && !items.ContainsKey(s.Id)) { items[s.Id] = s; found++; }
                    }
                    catch { }
                }
            }
            Log.Info($"status: found {found} more status items in static fields");
        }

        private static object SafeGet(PropertyInfo p, object o) { try { return p.GetValue(o, null); } catch { return null; } }

        private static void Learn(StatusItem s)
        {
            if (s == null || s.Id == null || s is FrozenStatusItem) return;
            if (items == null) FindItem(s.Id);
            if (!items.ContainsKey(s.Id)) items[s.Id] = s;
        }

        private static readonly Dictionary<string, Effect> effectsSeen = new Dictionary<string, Effect>();
        private static float nextEffectScan;

        /// <summary>Effects by id: the Db's, ones this guest's objects have or its code tried to add (e.g. a critter
        /// species' "wild" effect, which isn't in the Db).</summary>
        private static Effect FindEffect(string id)
        {
            Effect e = Db.Get().effects.TryGet(id);
            if (e != null) return e;
            if (effectsSeen.TryGetValue(id, out e)) return e;
            float now = Time.realtimeSinceStartup;
            if (now < nextEffectScan) return null;
            nextEffectScan = now + 10f;
            foreach (var kv in TrackerMap(KPrefabIDTracker.Get()))
            {
                var fx = kv.Value != null ? kv.Value.GetComponent<Effects>() : null;
                if (fx == null) continue;
                foreach (EffectInstance ei in EffectList(fx)) if (ei.effect != null) effectsSeen[ei.effect.Id] = ei.effect;
            }
            return effectsSeen.TryGetValue(id, out e) ? e : null;
        }

        private static readonly Dictionary<Type, List<Func<object, Effect>>> effectGetters = new Dictionary<Type, List<Func<object, Effect>>>();

        /// <summary>Effects the object's state machines hold (a monitor's definition or its per-object instance), e.g.
        /// a critter's "uncomfortable temperature" or "cramped": they aren't in the Db.</summary>
        private static Effect EffectOn(GameObject go, string id)
        {
            var ctl = go != null ? go.GetComponent<StateMachineController>() : null;
            if (ctl == null) return null;
            foreach (StateMachine.Instance smi in ctl)
            {
                if (smi == null) continue;
                Effect e = EffectIn(smi, id);
                if (e == null) { try { e = EffectIn(smi.GetStateMachine(), id); } catch { } }
                if (e != null) { effectsSeen[id] = e; return e; }
            }
            return null;
        }

        private static Effect EffectIn(object o, string id)
        {
            if (o == null) return null;
            foreach (Func<object, Effect> get in EffectGetters(o.GetType()))
            {
                Effect e = null;
                try { e = get(o); } catch { }
                if (e != null && e.Id == id) return e;
            }
            return null;
        }

        /// <summary>Fields holding an Effect, directly or as the Effect member of a field (one level).</summary>
        private static List<Func<object, Effect>> EffectGetters(Type t)
        {
            if (effectGetters.TryGetValue(t, out List<Func<object, Effect>> list)) return list;
            list = new List<Func<object, Effect>>();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type c = t; c != null && c != typeof(object); c = c.BaseType)
            {
                foreach (FieldInfo f in c.GetFields(F | BindingFlags.DeclaredOnly))
                {
                    if (typeof(Effect).IsAssignableFrom(f.FieldType)) { FieldInfo ff = f; list.Add(o => ff.GetValue(o) as Effect); continue; }
                    if (f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsArray) continue;
                    PropertyInfo inner = f.FieldType.GetProperty("Effect", F);
                    FieldInfo innerField = inner == null ? f.FieldType.GetField("effect", F) : null;
                    if (inner != null && typeof(Effect).IsAssignableFrom(inner.PropertyType) && inner.GetIndexParameters().Length == 0)
                    {
                        FieldInfo ff = f; PropertyInfo ip = inner;
                        list.Add(o => { object v = ff.GetValue(o); return v != null ? ip.GetValue(v, null) as Effect : null; });
                    }
                    else if (innerField != null && typeof(Effect).IsAssignableFrom(innerField.FieldType))
                    {
                        FieldInfo ff = f, inf = innerField;
                        list.Add(o => { object v = ff.GetValue(o); return v != null ? inf.GetValue(v) as Effect : null; });
                    }
                }
            }
            effectGetters[t] = list;
            return list;
        }

        // ---- receiving

        public static void OnRecords(byte[] data)
        {
            if (!Puppets.Mirroring) return;
            if (firstRecordAt < 0f) firstRecordAt = Time.realtimeSinceStartup;
            {
                using (var r = Inflate.Open(data)) // (the shared reader: no garbage per message; using is harmless)
                {
                    while (true)
                    {
                        byte kind;
                        if (!Inflate.More(r)) break;
                        kind = r.ReadByte();
                        int id = r.ReadInt32();
                        int n = r.ReadUInt16();
                        if (kind == KEffects)
                        {
                            var list = new List<WantEffect>(n);
                            // (the sets are reused: one per object, refilled)
                            if (!serverEffects.TryGetValue(id, out HashSet<int> hashes)) serverEffects[id] = hashes = new HashSet<int>();
                            else hashes.Clear();
                            for (int i = 0; i < n; i++)
                            {
                                var w = new WantEffect { id = Inflate.ReadId(r), save = r.ReadBoolean(), time = r.ReadSingle() };
                                list.Add(w);
                                hashes.Add(new HashedString(w.id).HashValue);
                            }
                            serverEffectLists[id] = new ServerEffects { list = list, clock = GameClock.Instance != null ? GameClock.Instance.GetTime() : 0f };
                            if (!ApplyEffectsTo(id, list)) Wait(id).effects = list;
                            else if (waiting.TryGetValue(id, out Waiting old)) old.effects = null;
                            continue;
                        }
                        List<Want> want;
                        if (kind == KStatusDelta)
                        {
                            if (removedBuf.Length < n) removedBuf = new int[System.Math.Max(n, removedBuf.Length * 2)];
                            int[] removed = removedBuf;
                            for (int i = 0; i < n; i++) removed[i] = r.ReadUInt16();
                            int added = r.ReadUInt16();
                            var news = new List<Want>(added);
                            for (int i = 0; i < added; i++) news.Add(ReadEntry(r, id));
                            bool fits = serverLists.TryGetValue(id, out want);
                            for (int i = 0; fits && i < n; i++) fits = removed[i] < want.Count && (i == 0 || removed[i] > removed[i - 1]);
                            if (!fits)
                            {
                                // this guest doesn't have the list this change is to: ask for the whole list
                                serverLists.Remove(id);
                                Need(NeedFull, "", id, again: true);
                                continue;
                            }
                            for (int i = n - 1; i >= 0; i--) want.RemoveAt(removed[i]);
                            want.AddRange(news);
                        }
                        else
                        {
                            want = new List<Want>(n);
                            for (int i = 0; i < n; i++) want.Add(ReadEntry(r, id));
                            serverLists[id] = want;
                        }
                        if (!serverHas.TryGetValue(id, out HashSet<string> has)) serverHas[id] = has = new HashSet<string>();
                        else has.Clear();
                        foreach (Want x in want) has.Add(x.id);
                        if (!ApplyStatusTo(id, want)) Wait(id).status = want;
                        else if (waiting.TryGetValue(id, out Waiting old)) old.status = null;
                    }
                }
            }
        }

        private static int[] removedBuf = new int[64];

        /// <summary>The server's status lists as last received, per object (entries this guest can't show stay in it as
        /// placeholders, so the server's changes by position still fit).</summary>
        private static readonly Dictionary<int, List<Want>> serverLists = new Dictionary<int, List<Want>>();

        private static Want ReadEntry(BinaryReader r, int obj)
        {
            string itemId = Inflate.ReadId(r), catId = Inflate.ReadId(r);
            byte flags = r.ReadByte();
            bool ok = ReadData(r, out object d);
            string sName = null, sTip = null;
            List<KeyValuePair<string, string>> nameParts = null, tipParts = null;
            if ((flags & FText) != 0) { sName = r.ReadString(); sTip = r.ReadString(); } // (texts: plain)
            if ((flags & FParts) != 0) { nameParts = TextParts.ReadParts(r); tipParts = TextParts.ReadParts(r); }
            FrozenStatusItem looks = (flags & FLooks) != 0 ? ReadLooks(r, itemId) : null;
            // an effect's status item is that effect instance's own
            StatusItem item = d is EffectInstance ei && ei.statusItem != null ? ei.statusItem : FindItem(itemId);
            // the server's text in this guest's language: its own template with the server's values
            FrozenText text = (flags & FText) == 0 ? null : new FrozenText(
                (item != null ? TextParts.Compose(item.Name, nameParts) : null) ?? sName,
                (item != null ? TextParts.Compose(item.tooltipText, tipParts) : null) ?? sTip);
            if (text != null) TextParts.Check(itemId, sName, text.name, sTip, text.tooltip);
            FrozenStatusItem frozen = text == null ? null : looks ?? (item != null ? FrozenOf(item) : null);
            if (item == null && frozen == null)
            {
                Need(NeedLooks, itemId, obj);
                Unknown++;
                unknownBy.TryGetValue(itemId, out int c);
                if (c > 0 || unknownBy.Count < 40) unknownBy[itemId] = c + 1;
            }
            StatusItemCategory cat = catId.Length > 0 ? Db.Get().StatusItemCategories.TryGet(catId) : null;
            return new Want { id = itemId, item = item, cat = cat, data = ok ? d : KeepLocal, frozen = frozen, text = text };
        }

        private static Waiting Wait(int id)
        {
            if (!waiting.TryGetValue(id, out Waiting w)) { waiting[id] = w = new Waiting(); Waited++; }
            w.since = Time.realtimeSinceStartup;
            return w;
        }

        private static KPrefabID Here(int id)
        {
            KPrefabID k = KPrefabIDTracker.Get().GetInstance(id);
            return k != null && k.isSpawned ? k : null;
        }

        private static bool ApplyStatusTo(int id, List<Want> want)
        {
            KPrefabID k = Here(id);
            if (k == null) return false;
            var sel = k.GetComponent<KSelectable>();
            StatusItemGroup g = sel != null ? sel.GetStatusItemGroup() : null;
            if (g == null) return true; // nothing to show it on
            ApplyStatus(g, want, id);
            Applied++;
            return true;
        }

        private static bool ApplyEffectsTo(int id, List<WantEffect> want)
        {
            KPrefabID k = Here(id);
            if (k == null) return false;
            var fx = k.GetComponent<Effects>();
            if (fx == null) return true;
            List<EffectInstance> have = EffectList(fx);
            for (int i = have.Count - 1; i >= 0; i--)
            {
                if (i >= have.Count) continue;
                Effect e = have[i].effect;
                if (e != null && !want.Exists(x => x.id == e.Id)) WithEffect(fx, e, () => fx.Remove(e));
            }
            foreach (WantEffect w in want)
            {
                EffectInstance ei = fx.Get(w.id);
                if (ei == null)
                {
                    Effect e = FindEffect(w.id) ?? EffectOn(k.gameObject, w.id);
                    if (e == null)
                    {
                        UnknownEffects++;
                        unknownBy.TryGetValue("effect " + w.id, out int c);
                        if (c > 0 || unknownBy.Count < 40) unknownBy["effect " + w.id] = c + 1;
                        continue;
                    }
                    ei = ServerAdd(fx, e, w.save);
                }
                if (ei != null) ei.timeRemaining = w.time;
            }
            AppliedEffects++;
            return true;
        }

        /// <summary>Rolling check: an object's effects are exactly the server's (only which ones; the time left is the
        /// guest's own countdown from the server's value). A difference is fixed and logged.</summary>
        private static void CheckEffects(int count)
        {
            if (serverEffectLists.Count == 0) return;
            if (checkCursor >= checkIds.Count) { checkIds.Clear(); checkIds.AddRange(serverEffectLists.Keys); checkCursor = 0; }
            float clock = GameClock.Instance != null ? GameClock.Instance.GetTime() : 0f;
            for (int n = 0; n < count && checkCursor < checkIds.Count; n++)
            {
                int id = checkIds[checkCursor++];
                if (!serverEffectLists.TryGetValue(id, out ServerEffects want)) continue;
                KPrefabID k = Here(id);
                var fx = k != null ? k.GetComponent<Effects>() : null;
                if (fx == null) { if (k == null && KPrefabIDTracker.Get().GetInstance(id) == null) serverEffectLists.Remove(id); continue; }
                List<EffectInstance> have = EffectList(fx);
                List<string> extra = null, missing = null;
                var seen = new HashSet<string>();
                foreach (EffectInstance ei in have)
                    if (ei.effect != null && (!want.list.Exists(x => x.id == ei.effect.Id) || !seen.Add(ei.effect.Id))) (extra = extra ?? new List<string>()).Add(ei.effect.Id);
                foreach (WantEffect w in want.list)
                    if (fx.Get(w.id) == null && (w.time <= 0f || w.time - (clock - want.clock) > 0f)) (missing = missing ?? new List<string>()).Add(w.id);
                if (extra == null && missing == null) continue;
                EffectDrift++;
                if (driftLogged++ < 8)
                    Log.Info($"status: effects on {id} {k.PrefabTag.Name} drifted from the server's: extra [{Join(extra)}], missing [{Join(missing)}] (list {clock - want.clock:0} s old)");
                if (extra != null) foreach (string e in extra) { Effect ef = fx.Get(e)?.effect; if (ef != null) WithEffect(fx, ef, () => fx.Remove(ef)); }
                if (missing != null)
                    foreach (WantEffect w in want.list)
                    {
                        if (!missing.Contains(w.id)) continue;
                        Effect ef = FindEffect(w.id) ?? EffectOn(k.gameObject, w.id);
                        if (ef == null) continue;
                        EffectInstance ei = ServerAdd(fx, ef, w.save);
                        if (ei != null && w.time > 0f) ei.timeRemaining = w.time - (clock - want.clock);
                    }
            }
        }

        /// <summary>For the sync test: the server's effect list as this guest last got it.</summary>
        public static string ServerEffectsOf(int id)
        {
            if (!serverEffectLists.TryGetValue(id, out ServerEffects e)) return "no list";
            var parts = new List<string>();
            foreach (WantEffect w in e.list) parts.Add(w.id);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(",", parts.ToArray());
        }

        private static string Join(List<string> l) => l == null ? "" : string.Join(",", l.ToArray());

        private static readonly AccessTools.FieldRef<Effects, List<Effects.EffectImmunity>> Immunities =
            AccessTools.FieldRefAccess<Effects, List<Effects.EffectImmunity>>("effectImmunites");

        /// <summary>Add an effect the server's object has. An immunity this guest's object has (a suit, riding a tube)
        /// doesn't stop it: the server's object got the effect before its immunity, which only stops new ones.</summary>
        private static EffectInstance ServerAdd(Effects fx, Effect e, bool save)
        {
            List<Effects.EffectImmunity> imm = Immunities(fx);
            List<Effects.EffectImmunity> taken = null;
            for (int i = imm.Count - 1; i >= 0; i--)
                if (imm[i].effect == e) { (taken = taken ?? new List<Effects.EffectImmunity>()).Add(imm[i]); imm.RemoveAt(i); }
            EffectInstance ei = null;
            try { WithEffect(fx, e, () => ei = fx.Add(e, save)); }
            finally { if (taken != null) imm.AddRange(taken); }
            return ei;
        }

        /// <summary>Let exactly this effect (and its own status item) change on this object.</summary>
        private static void WithEffect(Effects fx, Effect e, Action a)
        {
            Effects wasFx = applyingFx;
            int wasEffect = applyingEffect;
            string wasItem = applyingEffectItem;
            applyingFx = fx; applyingEffect = e.IdHash.HashValue; applyingEffectItem = e.Id;
            try { a(); }
            finally { applyingFx = wasFx; applyingEffect = wasEffect; applyingEffectItem = wasItem; }
        }

        private static float nextRetry;
        private static readonly List<int> retryIds = new List<int>();

        /// <summary>Guest, every frame: apply lists whose object has arrived since (give up after 30 s), and check a few
        /// objects' effects against the server's last list.</summary>
        public static void GuestTick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextRetry) return;
            nextRetry = now + 0.5f;
            CheckEffects(16);
            SendNeeds();
            retryIds.Clear();
            retryIds.AddRange(waiting.Keys);
            foreach (int id in retryIds)
            {
                Waiting w = waiting[id];
                if (w.effects != null && ApplyEffectsTo(id, w.effects)) w.effects = null;
                if (w.status != null && ApplyStatusTo(id, w.status)) w.status = null;
                if (w.effects == null && w.status == null || now - w.since > 30f) waiting.Remove(id);
            }
        }

        /// <summary>Stands for "the server's data for this item couldn't be sent": keep the guest's own entry.</summary>
        private static readonly object KeepLocal = new object();

        /// <summary>Make the group hold exactly the wanted entries: entries already there with the same item, category
        /// and data stay (no flicker, no repeated notification); the rest go, then the missing ones are added.</summary>
        private static void ApplyStatus(StatusItemGroup g, List<Want> want, int id)
        {
            var have = new List<StatusItemGroup.Entry>();
            foreach (StatusItemGroup.Entry e in g) { have.Add(e); Learn(e.item); }
            var keep = new bool[have.Count];
            var missing = new List<Want>();
            foreach (Want x in want)
            {
                if (x.item == null && x.frozen == null) continue; // can't be shown here (asked the server for its looks)
                int match = -1;
                // the real item (with the same data, or any data when the server's couldn't be sent)
                for (int i = 0; i < have.Count && match < 0 && x.item != null; i++)
                {
                    if (keep[i] || have[i].item != x.item || have[i].category != x.cat) continue;
                    if (x.data == KeepLocal || Equals(have[i].data, x.data)) match = i;
                }
                // or the stand-in already showing the same text
                for (int i = 0; i < have.Count && match < 0 && x.frozen != null; i++)
                    if (!keep[i] && have[i].item == x.frozen && have[i].category == x.cat && Equals(have[i].data, x.text)) match = i;
                if (match >= 0) keep[match] = true;
                else if (x.item != null && x.data != KeepLocal) missing.Add(x);
                else if (x.frozen != null) { missing.Add(new Want { id = x.id, item = x.frozen, cat = x.cat, data = x.text }); StandIns++; }
                else { SkippedData++; Need(NeedText, x.item.Id, id); } // the guest doesn't have it and can't make its data
            }
            StatusItemGroup was = applyingGroup;
            applyingGroup = g;
            try
            {
                for (int i = 0; i < have.Count; i++) if (!keep[i]) g.RemoveStatusItem(have[i].id, immediate: true);
                foreach (Want x in missing) g.AddStatusItem(x.item, x.data, x.cat);
            }
            finally { applyingGroup = was; }
        }

        // ---- stand-ins: an item showing the server's text

        internal sealed class FrozenText
        {
            public readonly string name, tooltip;
            public FrozenText(string name, string tooltip) { this.name = name; this.tooltip = tooltip; }
            public override bool Equals(object o) => o is FrozenText t && t.name == name && t.tooltip == tooltip;
            public override int GetHashCode() => (name ?? "").GetHashCode() * 31 + (tooltip ?? "").GetHashCode();
        }

        internal sealed class FrozenStatusItem : StatusItem
        {
            public FrozenStatusItem(string id, string icon, IconType iconType, NotificationType notification, bool multiples,
                HashedString overlay, int overlays, bool worldIcon)
                : base(id, "", "", icon, iconType, notification, multiples, overlay, overlays, worldIcon) { }

            public override string GetName(object data) => data is FrozenText t ? t.name : base.GetName(data);
            public override string GetTooltip(object data) => data is FrozenText t ? t.tooltip : base.GetTooltip(data);
        }

        public static long StandIns;
        private static readonly Dictionary<StatusItem, FrozenStatusItem> frozenOf = new Dictionary<StatusItem, FrozenStatusItem>();
        private static readonly Dictionary<string, FrozenStatusItem> frozenLooks = new Dictionary<string, FrozenStatusItem>();

        /// <summary>A stand-in that looks like this guest's own item.</summary>
        private static FrozenStatusItem FrozenOf(StatusItem real)
        {
            if (frozenOf.TryGetValue(real, out FrozenStatusItem f)) return f;
            f = new FrozenStatusItem(real.Id, real.iconName, real.iconType, real.notificationType, real.allowMultiples,
                real.render_overlay, real.status_overlays, ShowWorldIcon(real));
            if (real.shouldNotify) f.AddNotification(real.soundPath, real.notificationText ?? "", real.notificationTooltipText ?? "");
            frozenOf[real] = f;
            return f;
        }

        /// <summary>A stand-in that looks the way the server described (an item this guest can't look up).</summary>
        private static FrozenStatusItem ReadLooks(BinaryReader r, string id)
        {
            string icon = Inflate.ReadId(r);
            var iconType = (StatusItem.IconType)r.ReadInt32();
            var notification = (NotificationType)r.ReadInt32();
            bool multiples = r.ReadBoolean();
            int overlay = r.ReadInt32(), overlays = r.ReadInt32();
            bool worldIcon = r.ReadBoolean(), notify = r.ReadBoolean();
            string nText = Inflate.ReadId(r), nTip = Inflate.ReadId(r), sound = Inflate.ReadId(r);
            string key = string.Join("\u0001", new[] { id, icon, ((int)iconType).ToString(), ((int)notification).ToString(), multiples.ToString(),
                overlay.ToString(), overlays.ToString(), worldIcon.ToString(), notify.ToString(), nText, nTip, sound });
            if (frozenLooks.TryGetValue(key, out FrozenStatusItem f)) return f;
            f = new FrozenStatusItem(id, icon, iconType, notification, multiples, new HashedString(overlay), overlays, worldIcon);
            if (notify) f.AddNotification(sound.Length > 0 ? sound : null, nText, nTip);
            frozenLooks[key] = f;
            return f;
        }

        /// <summary>The guest's own code is adding the real item: the stand-in for it goes.</summary>
        private static void DropStandIn(StatusItemGroup g, string id)
        {
            Guid standIn = Guid.Empty;
            foreach (StatusItemGroup.Entry e in g) if (e.item is FrozenStatusItem && e.item.Id == id) standIn = e.id;
            if (standIn == Guid.Empty) return;
            StatusItemGroup was = applyingGroup;
            applyingGroup = g;
            try { g.RemoveStatusItem(standIn, immediate: true); }
            finally { applyingGroup = was; }
        }

        private static readonly HashSet<long> needed = new HashSet<long>();
        private static readonly MemoryStream needBuf = new MemoryStream();
        private static int needCount;
        private static float needSendAt;

        private static void Need(byte kind, string item, int obj, bool again = false)
        {
            long key = (long)(item.GetHashCode() ^ kind * 0x5bd1e995) << 32 | (uint)obj;
            if (!needed.Add(key) && !again) return;
            if (needCount == 0) needSendAt = Time.realtimeSinceStartup + 0.5f;
            var w = new BinaryWriter(needBuf);
            w.Write(kind);
            w.Write(item);
            w.Write(obj);
            w.Flush();
            needCount++;
        }

        private static void SendNeeds()
        {
            if (needCount == 0 || Time.realtimeSinceStartup < needSendAt) return;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(needCount);
            w.Write(needBuf.GetBuffer(), 0, (int)needBuf.Length);
            w.Flush();
            CommandBus.Client.SendRelay(Proto.TargetHost, Ch, ms.ToArray());
            NeedsSent += needCount;
            needBuf.SetLength(0);
            needCount = 0;
        }

        public static long NeedsSent, AllowedNoList;
        private static float firstRecordAt = -1f;

        // ---- the guest's own code

        private static int IdOf(GameObject go)
        {
            var k = go != null ? go.GetComponent<KPrefabID>() : null;
            return k != null ? k.InstanceID : -1;
        }

        /// <summary>Guest: may local code add (or remove) this status item on this object? Only to agree with the
        /// server's list.</summary>
        public static bool AllowLocal(StatusItemGroup g, StatusItem item, bool adding)
        {
            if (!Puppets.Mirroring || destroying > 0 || g == null || item == null || g == applyingGroup) return true;
            GameObject go = g.gameObject;
            if (applyingFx != null && item.Id == applyingEffectItem && go == applyingFx.gameObject) return true;
            if (!serverHas.TryGetValue(IdOf(go), out HashSet<string> has) || !Synced(go)) return true;
            Learn(item);
            if (has.Contains(item.Id) == adding)
            {
                if (adding) DropStandIn(g, item.Id);
                return true;
            }
            Blocked++;
            return false;
        }

        /// <summary>Guest: the same for effects.</summary>
        public static bool AllowEffect(Effects fx, int effectHash, bool adding)
        {
            if (!Puppets.Mirroring || fx == null || fx == applyingFx && effectHash == applyingEffect) return true;
            if (!serverEffects.TryGetValue(IdOf(fx.gameObject), out HashSet<int> has) || !Synced(fx.gameObject))
            {
                // diagnostics: long after the lists started coming, an object that never got one
                if (adding && firstRecordAt >= 0f && Time.realtimeSinceStartup - firstRecordAt > 30f && IdOf(fx.gameObject) > 0) AllowedNoList++;
                return true;
            }
            if (has.Contains(effectHash) == adding) return true;
            BlockedEffects++;
            return false;
        }

        private static StatusItem ItemOf(StatusItemGroup g, Guid guid)
        {
            if (guid == Guid.Empty) return null;
            foreach (StatusItemGroup.Entry e in g) if (e.id == guid) return e.item;
            return null;
        }

        /// <summary>What a refused local Effects.Add returns: callers adjust the result's time left
        /// (<c>Add(...).timeRemaining *= x</c>), so it has to be an instance; this one is attached to nothing.</summary>
        private static EffectInstance Detached(Effect e)
        {
            var ei = (EffectInstance)FormatterServices.GetUninitializedObject(typeof(EffectInstance));
            ei.effect = e;
            return ei;
        }

        public static string Report() =>
            $"status: server sent {Sent} lists ({SentEntries} status items, {SentEffects} effects, {Unnameable} items with data it couldn't name: {Top(unnameableBy)}), " +
            $"{SentBytes / 1024} KB ({StatusBytes / 1024} KB of status lists before compression; {Fulls} whole lists, {Deltas} changes, {FullsAsked} whole lists asked again), {Unchanged} unchanged not sent; " +
            $"guest applied {Applied} status + {AppliedEffects} effect lists (waited for the object {Waited}, still waiting {waiting.Count}), " +
            $"unknown {Unknown} items / {UnknownEffects} effects ({Top(unknownBy)}), " +
            $"effects found drifted and fixed {EffectDrift}, added with no list {AllowedNoList}, asked the server for text/looks {NeedsSent}, stand-ins with the server's text {StandIns}, server data not sendable and not here {SkippedData}, blocked local {Blocked} items / {BlockedEffects} effects";

        public static string ByteReport()
        {
            string Top2(Dictionary<string, long> d, string unit)
            {
                var l = new List<KeyValuePair<string, long>>(d);
                l.Sort((a, b) => b.Value.CompareTo(a.Value));
                var parts = new List<string>();
                for (int i = 0; i < l.Count && i < 12; i++) parts.Add(l[i].Key + " " + (unit == "KB" ? (l[i].Value / 1024) + " KB" : l[i].Value.ToString()));
                return string.Join(", ", parts.ToArray());
            }
            string r = $"status bytes by item (raw): {Top2(bytesBy, "KB")} | lists by prefab: {Top2(listsBy, "")}";
            bytesBy.Clear(); listsBy.Clear();
            return r;
        }

        private static string Top(Dictionary<string, int> d)
        {
            var l = new List<KeyValuePair<string, int>>(d);
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < l.Count && i < 8; i++) parts.Add(l[i].Key + " x" + l[i].Value);
            return parts.Count == 0 ? "-" : string.Join(", ", parts.ToArray());
        }

        public static void Reset()
        {
            dirtyStatus.Clear();
            dirtyEffects.Clear();
            lastSent.Clear();
            lastSentAt.Clear();
            sentSigs.Clear();
            carry.Clear();
            serverHas.Clear();
            serverEffects.Clear();
            serverEffectLists.Clear();
            serverLists.Clear();
            needed.Clear();
            needBuf.SetLength(0);
            needCount = 0;
            firstRecordAt = -1f;
            checkIds.Clear();
            checkCursor = 0;
            waiting.Clear();
            destroying = 0;
            nextSend = 0f;
        }

        /// <summary>For the sync test: send what's pending now.</summary>
        public static void FlushNow()
        {
            nextSend = 0f;
            flushAll = true;
            try { HostTick(Time.realtimeSinceStartup); }
            finally { flushAll = false; }
        }

        private static bool flushAll;

        // ================================================================== patches (main menu)

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                var post = new HarmonyMethod(typeof(StatusSync), nameof(ChangedPostfix));
                Type g = typeof(StatusItemGroup);
                // SetStatusItem is made of RemoveStatusItem + AddStatusItem: guarding those two covers it
                h.Patch(AccessTools.Method(g, nameof(StatusItemGroup.AddStatusItem)), prefix: new HarmonyMethod(typeof(StatusSync), nameof(GuardAddPrefix)), postfix: post);
                h.Patch(AccessTools.Method(g, nameof(StatusItemGroup.RemoveStatusItem), new[] { typeof(StatusItem), typeof(bool) }), prefix: new HarmonyMethod(typeof(StatusSync), nameof(GuardItemPrefix)), postfix: post);
                h.Patch(AccessTools.Method(g, nameof(StatusItemGroup.RemoveStatusItem), new[] { typeof(Guid), typeof(bool) }), prefix: new HarmonyMethod(typeof(StatusSync), nameof(GuardGuidPrefix)), postfix: post);
                // the object going away removes everything: always let that through
                h.Patch(AccessTools.Method(g, nameof(StatusItemGroup.Destroy)),
                    prefix: new HarmonyMethod(typeof(StatusSync), nameof(DestroyPrefix)), finalizer: new HarmonyMethod(typeof(StatusSync), nameof(DestroyFinalizer)));

                Type fx = typeof(Effects);
                var fxPost = new HarmonyMethod(typeof(StatusSync), nameof(EffectsChangedPostfix));
                h.Patch(AccessTools.Method(fx, nameof(Effects.Add), new[] { typeof(Effect), typeof(bool), typeof(Func<string, object, string>) }),
                    prefix: new HarmonyMethod(typeof(StatusSync), nameof(EffectAddPrefix)), postfix: fxPost);
                h.Patch(AccessTools.Method(fx, nameof(Effects.Remove), new[] { typeof(HashedString) }),
                    prefix: new HarmonyMethod(typeof(StatusSync), nameof(EffectRemoveHashPrefix)), postfix: fxPost);
                h.Patch(AccessTools.Method(fx, nameof(Effects.Remove), new[] { typeof(string) }),
                    prefix: new HarmonyMethod(typeof(StatusSync), nameof(EffectRemoveIdPrefix)), postfix: fxPost);
            }
            catch (Exception e) { Log.Warn("status: patching failed: " + e.Message); }
        }

        private static void ChangedPostfix(StatusItemGroup __instance) => Changed(__instance);
        private static bool GuardAddPrefix(StatusItemGroup __instance, StatusItem __0, ref Guid __result) { if (AllowLocal(__instance, __0, true)) return true; __result = Guid.Empty; return false; }
        private static bool GuardItemPrefix(StatusItemGroup __instance, StatusItem __0, ref Guid __result) { if (AllowLocal(__instance, __0, false)) return true; __result = Guid.Empty; return false; }
        private static bool GuardGuidPrefix(StatusItemGroup __instance, Guid __0, ref Guid __result)
        {
            if (!Puppets.Mirroring || __instance == applyingGroup) return true;
            if (AllowLocal(__instance, ItemOf(__instance, __0), false)) return true;
            __result = Guid.Empty;
            return false;
        }
        private static void DestroyPrefix() => destroying++;
        private static Exception DestroyFinalizer(Exception __exception) { destroying--; return __exception; }

        private static void EffectsChangedPostfix(Effects __instance) => EffectsChanged(__instance);
        private static bool EffectAddPrefix(Effects __instance, Effect __0, ref EffectInstance __result)
        {
            if (__0 == null) return true;
            if (Puppets.Mirroring && !effectsSeen.ContainsKey(__0.Id)) effectsSeen[__0.Id] = __0;
            if (AllowEffect(__instance, __0.IdHash.HashValue, true))
            {
                // the server's list says "one X": a guest whose state machine switched between two variants of X (a
                // critter's wild / tame "miserable" are different objects with one id) keeps the one it has
                if (!Puppets.Mirroring || __instance.Get(__0) != null) return true;
                EffectInstance same = __instance.Get(__0.IdHash);
                if (same == null) return true;
                __result = same;
                return false;
            }
            __result = __instance.Get(__0) ?? Detached(__0);
            return false;
        }
        private static bool EffectRemoveHashPrefix(Effects __instance, HashedString __0) => AllowEffect(__instance, __0.HashValue, false);
        private static bool EffectRemoveIdPrefix(Effects __instance, string __0) => AllowEffect(__instance, new HashedString(__0).HashValue, false);
    }
}
