namespace OniCoop
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Roadmap step 3c: the guest has the same objects as the host — buildings, construction sites, orders
    /// (dig / mop markers), items, eggs, critters, duplicants: everything the game would put in a save file
    /// (has a SaveLoadRoot).
    ///
    /// Host: when such an object spawns or is destroyed, it tells the guests, in order, on one stream. A new object is
    /// sent as the game's own save data for that one object (SaveLoadRoot.Save), which carries its InstanceID, so the
    /// guest loads it with SaveLoadRoot.Load and gets the very same object under the same ID. Items (loose or stored)
    /// also stream where they are, which storage holds them, mass and temperature. Every 5 s a digest of the whole set
    /// (per-bucket hashes of id + prefab) goes out; a guest that disagrees asks for the bucket lists and fixes itself
    /// (missing objects: asks for them; objects the host doesn't have, twice in a row: removes them).
    ///
    /// IDs: orders replayed on a guest create objects in the same order as on the host, and the host sends the IDs it
    /// got, so the replay reuses them ("ID script"). Anything else a guest's own game creates gets an ID from a
    /// separate range (LocalIdBase and up) and, being local only, is removed after a few seconds.</summary>
    internal static class EntitySync
    {
        public const byte ChEntities = 8; // host -> guests: ordered records
        public const byte ChAsk = 9;      // guest -> host: bucket lists / objects it lacks
        public const int LocalIdBase = 1 << 30;

        private const byte RSpawn = 1, RDestroy = 2, RItem = 3, RDigest = 4, RBucket = 5,
                           RAssign = 6, REquip = 7, RAssignState = 8, REquipState = 9, RReplace = 10, RStorageList = 11;
        private const byte AskBuckets = 1, AskObjects = 2;
        private const int Buckets = 256;
        private const float DigestInterval = 5f, CleanInterval = 2f, LocalGrace = 3f;
        private const int RollingSlices = 120; // every item is re-sent at least every 120 * 0.25 s = 30 s

        private static readonly AccessTools.FieldRef<KPrefabIDTracker, Dictionary<int, KPrefabID>> TrackerMap =
            AccessTools.FieldRefAccess<KPrefabIDTracker, Dictionary<int, KPrefabID>>("prefabIdMap");
        private static readonly System.Reflection.MethodInfo ApplyStoredModifiers =
            AccessTools.Method(typeof(Storage), "ApplyStoredItemModifiers");

        /// <summary>Guest: the mirror is creating / destroying / moving objects right now.</summary>
        public static bool Applying;

        /// <summary>Host: guests currently in the game (set by the runtime). Nothing is recorded without one — e.g.
        /// not the 16 000 objects spawning while the host loads its save.</summary>
        public static int GuestsInGame;
        private static bool HostActive => CommandBus.Active && CommandBus.IsHost && GuestsInGame > 0;
        private static bool GuestActive => Puppets.Mirroring;

        private static bool Synced(KPrefabID k) =>
            k != null && k.InstanceID >= 0 && k.InstanceID < LocalIdBase && Saveable(k);

        // GetComponent for ~40 000 tracked objects every 5 s was a 20 ms hitch; whether an object is saveable never changes
        private static readonly Dictionary<KPrefabID, bool> saveable = new Dictionary<KPrefabID, bool>();

        private static bool Saveable(KPrefabID k)
        {
            if (saveable.TryGetValue(k, out bool b)) return b;
            b = k.GetComponent<SaveLoadRoot>() != null;
            saveable[k] = b;
            return b;
        }

        private static void PruneSaveable()
        {
            if (saveable.Count < 60000) return;
            var dead = new List<KPrefabID>();
            foreach (var kv in saveable) if (kv.Key == null) dead.Add(kv.Key);
            foreach (KPrefabID k in dead) saveable.Remove(k);
        }

        private static KPrefabID Find(int id) => KPrefabIDTracker.Get().GetInstance(id);

        public static void Reset()
        {
            events.Clear();
            itemsSent.Clear();
            itemRefs.Clear();
            storageRefs.Clear();
            itemBuf.SetLength(0);
            itemRecords = 0;
            conveyorCells.Clear();
            guestRail.Clear();
            asked.Clear();
            extraSince.Clear();
            localSeen.Clear();
            pendingAsk.Clear();
            saveable.Clear();
            recording = null;
            script = null;
            round = quietUntil = bucketOffset = 0;
        }

        // ================================================================== ID script

        private static List<int> recording, script;
        private static int scriptNext;
        public static int ScriptMismatches;

        private static int localMadeCount;

        private static void LocalMade(KPrefabID k)
        {
            if (localMadeCount++ > 200 && localMadeCount % 50 != 0) return; // stack traces are slow
            Sample(localCreatedBy, k.PrefabTag.Name + " <- " + Caller(4));
        }

        /// <summary>Guest: something of its own game destroys an object the host has (sampled, with the caller).</summary>
        public static void OnDeleteObject(GameObject go)
        {
            if (!GuestActive || Applying || go == null) return;
            var k = go.GetComponent<KPrefabID>();
            if (!Synced(k)) return;
            if (localDeleteCount++ > 200 && localDeleteCount % 50 != 0) return;
            Sample(localDestroyedBy, k.PrefabTag.Name + " <- " + Caller(4));
        }

        private static int localDeleteCount;

        private static string Caller(int frames)
        {
            var st = new System.Diagnostics.StackTrace(2, false);
            var sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < st.FrameCount && n < frames; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                System.Type dt = m?.DeclaringType;
                if (dt == null || dt.Namespace == "OniCoop" || dt.Namespace == "HarmonyLib" || dt == typeof(Util) || dt == typeof(GameUtil)
                    || dt.Name == "TracesExtesions" || m.Name.Contains("_Patch")) continue;
                if (n++ > 0) sb.Append(" < ");
                sb.Append(dt.Name).Append('.').Append(m.Name);
            }
            return sb.ToString();
        }

        /// <summary>Host: collect the IDs of the saveable objects an order creates.</summary>
        public static void BeginRecord(List<int> into) { into.Clear(); recording = into; }
        public static void EndRecord() => recording = null;
        public static List<int> CurrentRecord => recording;

        /// <summary>Guest: hand those IDs out again, in the same order, while replaying that order.</summary>
        public static void BeginScript(List<int> ids) { script = ids; scriptNext = 0; }
        public static void EndScript()
        {
            if (script != null && scriptNext != script.Count) ScriptMismatches++;
            script = null;
        }

        /// <summary>Util.KInstantiate postfix (objects that get an ID).</summary>
        public static void OnInstantiated(GameObject go)
        {
            if (recording == null && script == null && !GuestActive) return;
            var k = go.GetComponent<KPrefabID>();
            if (k == null || go.GetComponent<SaveLoadRoot>() == null) return;
            if (recording != null) { recording.Add(k.InstanceID); return; }
            if (script == null) { if (GuestActive && !Applying) LocalMade(k); return; }
            if (scriptNext >= script.Count) return; // the replay made more than the host: local, cleaned up later
            int id = script[scriptNext++];
            KPrefabIDTracker tracker = KPrefabIDTracker.Get();
            if (tracker.GetInstance(id) != null) return; // already here (sent by the host): keep this one local
            tracker.Unregister(k);
            k.InstanceID = id;
            tracker.Register(k);
        }

        // ================================================================== host

        private struct Ev { public byte kind; public int id, id2; public bool flag; }

        private sealed class ItemState
        {
            public int container; public byte storageIdx; public float x, y, mass, temp;
        }

        private static readonly List<Ev> events = new List<Ev>();
        private static readonly Dictionary<int, ItemState> itemsSent = new Dictionary<int, ItemState>();
        private static float nextDigest;
        private static int rollingSlice;
        public static long HostSpawns, HostDestroys, HostItems, HostBytes, HostObjectsAsked, HostAssigns;
        public static int LastDigestCount;
        public static float LastDigestMs;

        public static void OnSpawned(KPrefabID k)
        {
            if (HostActive && Synced(k)) { events.Add(new Ev { kind = RSpawn, id = k.InstanceID }); StatusSync.Spawned(k); }
        }

        public static void OnCleanedUp(KPrefabID k)
        {
            if (k == null || k.InstanceID < 0) return;
            if (HostActive)
            {
                if (k.InstanceID < LocalIdBase && Saveable(k)) events.Add(new Ev { kind = RDestroy, id = k.InstanceID });
            }
            else if (GuestActive && Synced(k))
            {
                if (k.GetComponent<MinionIdentity>() != null && minionCleanupsLogged++ < 3)
                    Log.Info($"entities: duplicant #{k.InstanceID} {k.name} cleaned up here; by: {new System.Diagnostics.StackTrace(2, false).ToString().Replace("\n", " <- ").Substring(0, 600)}");
                if (!Applying) LocalDestroys++;
                if (watched.Remove(k.InstanceID) && watchLogs++ < 12)
                    Log.Info($"entities: fetched {k.PrefabTag.Name} #{k.InstanceID} destroyed again{(Applying ? " (by the mirror)" : "")}: {Caller(6)}");
            }
        }

        /// <summary>A guest (re)loaded: it has the host's save, so every item state goes out once more.</summary>
        public static void OnGuestJoined() => itemsSent.Clear();

        /// <summary>End of every host frame, after the orders went out (so a guest replays an order before it hears
        /// about the objects that order made).</summary>
        public static void HostFlush(float now)
        {
            if (!HostActive) { events.Clear(); return; }
            bool digest = now >= nextDigest;
            WriteItems(itemW, all: false); // a slice every frame, into a buffer...
            bool items = itemRecords > 0 && now >= nextItemSend;
            if (events.Count == 0 && !items && !digest) return;
            using (var msg = new Msg())
            {
                if (items) { nextItemSend = now + 0.25f; TakeItems(msg.w); } // ...sent 4 times a second, before newer events
                msg.Send(Proto.TargetAll);
            }
            SendEvents(Proto.TargetAll);
            using (var msg = new Msg())
            {
                if (digest) { nextDigest = now + DigestInterval; WriteDigest(msg.w); WriteAssignState(msg.w); }
                msg.Send(Proto.TargetAll);
            }
        }

        private const int MessageBudget = 256 * 1024; // uncompressed bytes per message (frames are capped at 1 MB)
        private static int budget;

        /// <summary>All pending spawn / destroy records, in order, split into messages that stay small.</summary>
        private static void SendEvents(ushort target)
        {
            if (events.Count == 0) return;
            var dying = new HashSet<int>();
            foreach (Ev e in events) if (e.kind == RDestroy) dying.Add(e.id);
            int i = 0;
            while (i < events.Count)
            {
                using (var msg = new Msg())
                {
                    budget = MessageBudget;
                    for (; i < events.Count && budget > 0; i++) WriteEvent(msg.w, events[i], dying);
                    msg.Send(target);
                }
            }
            events.Clear();
        }

        /// <summary>Before the autotest's sync signal: everything up to now is on its way.</summary>
        public static void FlushNow()
        {
            if (!HostActive) return;
            SendEvents(Proto.TargetAll);
            using (var msg = new Msg()) { WriteItems(itemW, all: true); TakeItems(msg.w); msg.Send(Proto.TargetAll); }
        }

        /// <summary>One deflated message of records; sent only if something was written.</summary>
        private sealed class Msg : System.IDisposable
        {
            private readonly MemoryStream ms = new MemoryStream();
            public readonly BinaryWriter w;
            private bool closed;
            public Msg() { w = new BinaryWriter(new DeflateStream(ms, CompressionMode.Compress, true)); written = 0; }
            public void Send(ushort target)
            {
                w.Dispose(); // flushes the deflater; the memory stream stays open
                closed = true;
                if (written == 0) return;
                byte[] payload = ms.ToArray();
                HostBytes += payload.Length;
                CommandBus.Client.SendRelay(target, ChEntities, payload);
            }
            public void Dispose() { if (!closed) w.Dispose(); }
        }

        private static int written;

        private static void WriteEvent(BinaryWriter w, Ev e, HashSet<int> dying)
        {
            if (e.kind == RAssign)
            {
                var a = Find(e.id)?.GetComponent<Assignable>();
                if (a == null) return;
                w.Write(RAssign); w.Write(e.id); WriteIdentity(w, a.assignee); // the state now (what it ended as)
                HostAssigns++;
            }
            else if (e.kind == REquip)
            {
                w.Write(REquip); w.Write(e.id); w.Write(e.id2); w.Write(e.flag);
                HostAssigns++;
            }
            else if (e.kind == RStorageList)
            {
                var ms = Find(e.id)?.GetComponent<MinionStorage>();
                if (ms == null) return;
                List<MinionStorage.Info> list = ms.GetStoredMinionInfo();
                w.Write(RStorageList); w.Write(e.id); w.Write(list.Count);
                foreach (MinionStorage.Info info in list)
                {
                    w.Write(info.id.ToByteArray());
                    w.Write(info.name ?? "");
                    w.Write(info.serializedMinion != null ? info.serializedMinion.GetId() : -1);
                }
                budget -= 9 + 32 * list.Count;
            }
            else if (e.kind == RReplace)
            {
                // the object with this id is now another one: the old one goes, the new one comes with the same id
                w.Write(RReplace); w.Write(e.id);
                budget -= 5;
                KPrefabID k = Find(e.id);
                if (k != null && !k.pendingDestruction && !dying.Contains(e.id)) { WriteSpawn(w, k); HostSpawns++; }
                HostReplaced++;
            }
            else if (e.kind == RSpawn)
            {
                if (dying.Contains(e.id)) return; // spawned and died before we told anyone: only "gone" is sent
                KPrefabID k = Find(e.id);
                if (k == null || k.pendingDestruction) return;
                WriteSpawn(w, k);
                HostSpawns++;
            }
            else
            {
                w.Write(RDestroy); w.Write(e.id);
                itemsSent.Remove(e.id);
                HostDestroys++;
                budget -= 5;
            }
            written++;
        }

        private static void Container(GameObject go, out int container, out byte storageIdx)
        {
            container = -1; storageIdx = 0;
            var p = go.GetComponent<Pickupable>();
            Storage s = p != null ? p.storage : null;
            if (s == null) return;
            var owner = s.GetComponent<KPrefabID>();
            if (owner == null) return;
            container = owner.InstanceID;
            Storage[] all = s.GetComponents<Storage>();
            for (int i = 0; i < all.Length; i++) if (all[i] == s) storageIdx = (byte)i;
        }

        private static void WriteSpawn(BinaryWriter w, KPrefabID k)
        {
            var root = k.GetComponent<SaveLoadRoot>();
            var blob = new MemoryStream();
            using (var bw = new BinaryWriter(blob, Encoding.UTF8, true)) root.Save(bw);
            Container(k.gameObject, out int container, out byte idx);
            var pe = k.GetComponent<PrimaryElement>();
            w.Write(RSpawn);
            w.Write(k.InstanceID);
            w.Write(k.GetSaveLoadTag().Name);
            w.Write(container); w.Write(idx);
            w.Write(pe != null && pe.KeepZeroMassObject); // not in the save data; empty chunks a building keeps
            w.Write((int)blob.Length);
            w.Write(blob.GetBuffer(), 0, (int)blob.Length);
            budget -= (int)blob.Length + 40;
        }

        /// <summary>Item "container" for an item riding a conveyor rail: it isn't in a storage, the rail holds it; x is
        /// the rail cell.</summary>
        private const int OnConveyor = -2;

        private static bool ItemDiffers(ItemState a, ItemState b) =>
            a.container != b.container || a.storageIdx != b.storageIdx
            || (a.container == OnConveyor && (int)a.x != (int)b.x)
            || (a.container == -1 && (Mathf.Abs(a.x - b.x) > 0.05f || Mathf.Abs(a.y - b.y) > 0.05f))
            || Mathf.Abs(a.mass - b.mass) > Mathf.Max(0.005f * Mathf.Max(a.mass, b.mass), 0.001f)
            || Mathf.Abs(a.temp - b.temp) > 1f;

        private sealed class ItemRef
        {
            public KPrefabID k; public PrimaryElement pe; public Equippable eq; public bool puppet;
        }

        // GetComponent on thousands of items every 0.25 s cost the host a 15-25 ms hitch each time: cache, and walk the
        // list a slice per frame instead (a full pass about every 16 frames)
        private static readonly Dictionary<Pickupable, ItemRef> itemRefs = new Dictionary<Pickupable, ItemRef>();
        private static readonly Dictionary<Storage, long> storageRefs = new Dictionary<Storage, long>();
        private const int ItemSlices = 16;
        private static int itemCursor;

        private static readonly Dictionary<Pickupable, int> conveyorCells = new Dictionary<Pickupable, int>();

        /// <summary>Host: every item on a conveyor rail and its cell (a few hundred rails: cheap, once per item pass).</summary>
        private static void MapConveyors()
        {
            conveyorCells.Clear();
            SolidConduitFlow flow = Game.Instance != null ? Game.Instance.solidConduitFlow : null;
            if (flow == null) return;
            SolidConduitFlow.SOAInfo soa = flow.GetSOAInfo();
            for (int i = 0; i < soa.NumEntries; i++)
            {
                int cell = soa.GetCell(i);
                Pickupable p = flow.GetPickupable(flow.GetContents(cell).pickupableHandle);
                if (p != null) conveyorCells[p] = cell;
            }
        }

        private static void ContainerFast(Pickupable p, out int container, out byte idx)
        {
            container = -1; idx = 0;
            Storage s = p.storage;
            if (s == null) return;
            if (!storageRefs.TryGetValue(s, out long packed))
            {
                Container(p.gameObject, out container, out idx);
                storageRefs[s] = packed = ((long)container << 8) | idx;
            }
            container = (int)(packed >> 8);
            idx = (byte)(packed & 0xff);
        }

        /// <param name="all">the whole list now (test sync points), else the next slice</param>
        private static readonly MemoryStream itemBuf = new MemoryStream();
        private static readonly BinaryWriter itemW = new BinaryWriter(itemBuf);
        private static int itemRecords;
        private static float nextItemSend;

        private static void TakeItems(BinaryWriter w)
        {
            w.Write(itemBuf.GetBuffer(), 0, (int)itemBuf.Length);
            written += itemRecords;
            itemRecords = 0;
            itemBuf.SetLength(0);
        }

        private static void WriteItems(BinaryWriter w, bool all)
        {
            List<Pickupable> list = Components.Pickupables.Items;
            int n = list.Count;
            if (n == 0) return;
            int count = all ? n : (n + ItemSlices - 1) / ItemSlices;
            for (int step = 0; step < count; step++)
            {
                if (itemCursor >= n || conveyorCells.Count == 0 && itemCursor == 0) MapConveyors();
                if (itemCursor >= n)
                {
                    itemCursor = 0;
                    rollingSlice = (rollingSlice + 1) % RollingSlices; // a full pass done
                    if (itemRefs.Count > n * 2) PruneItemRefs();
                }
                Pickupable p = list[itemCursor++];
                if (p == null) continue;
                if (!itemRefs.TryGetValue(p, out ItemRef r))
                {
                    itemRefs[p] = r = new ItemRef
                    {
                        k = p.GetComponent<KPrefabID>(), pe = p.GetComponent<PrimaryElement>(), eq = p.GetComponent<Equippable>(),
                        puppet = p.GetComponent<Brain>() != null, // critters / duplicants follow Puppets
                    };
                }
                KPrefabID k = r.k;
                if (r.puppet || k == null || r.pe == null || k.InstanceID < 0 || k.InstanceID >= LocalIdBase || !k.isSpawned) continue;
                if (r.eq != null && r.eq.isEquipped)
                {
                    itemsSent.Remove(k.InstanceID); // worn (equipment records): when it comes off, send where it went
                    continue;
                }
                ContainerFast(p, out int container, out byte idx);
                Vector3 pos = p.transform.GetPosition();
                if (container == -1 && conveyorCells.TryGetValue(p, out int railCell)) { container = OnConveyor; pos = new Vector3(railCell, 0f, 0f); }
                var st = new ItemState { container = container, storageIdx = idx, x = pos.x, y = pos.y, mass = r.pe.Mass, temp = r.pe.Temperature };
                bool rolling = (k.InstanceID & 0x7fffffff) % RollingSlices == rollingSlice;
                if (itemsSent.TryGetValue(k.InstanceID, out ItemState old) && !rolling && !ItemDiffers(old, st)) continue;
                itemsSent[k.InstanceID] = st;
                w.Write(RItem); w.Write(k.InstanceID); w.Write(container); w.Write(idx);
                w.Write(st.x); w.Write(st.y); w.Write(st.mass); w.Write(st.temp);
                HostItems++;
                itemRecords++;
            }
        }

        private static void PruneItemRefs()
        {
            var dead = new List<Pickupable>();
            foreach (var kv in itemRefs) if (kv.Key == null) dead.Add(kv.Key);
            foreach (Pickupable p in dead) itemRefs.Remove(p);
            var deadS = new List<Storage>();
            foreach (var kv in storageRefs) if (kv.Key == null) deadS.Add(kv.Key);
            foreach (Storage st in deadS) storageRefs.Remove(st);
        }

        /// <summary>Per bucket (id mod 256): an order-independent hash of (id, prefab) over the synced objects.</summary>
        private static uint[] ComputeDigest(bool host, out int count)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            PruneSaveable();
            var d = new uint[Buckets];
            count = 0;
            foreach (var kv in TrackerMap(KPrefabIDTracker.Get()))
            {
                KPrefabID k = kv.Value;
                if (k == null || k.pendingDestruction || kv.Key >= LocalIdBase || kv.Key < 0) continue;
                if (host && !k.isSpawned) continue; // not announced yet
                if (!Saveable(k)) continue;
                d[kv.Key & (Buckets - 1)] += Mix(kv.Key, k.GetSaveLoadTag().GetHash());
                count++;
            }
            LastDigestMs = (float)sw.Elapsed.TotalMilliseconds;
            LastDigestCount = count;
            return d;
        }

        private static uint Mix(int id, int tag) => unchecked((uint)id * 0x9E3779B1u ^ ((uint)tag * 0x85EBCA6Bu + 0x27d4eb2fu));

        private static void WriteDigest(BinaryWriter w)
        {
            uint[] d = ComputeDigest(true, out int count);
            w.Write(RDigest); w.Write(count);
            foreach (uint h in d) w.Write(h);
            written++;
        }

        /// <summary>Host: a guest asks for bucket lists or for objects it lacks; answered to that guest only, after
        /// everything already queued for all.</summary>
        public static void OnAsk(ushort from, byte[] data)
        {
            if (!HostActive) return;
            SendEvents(Proto.TargetAll); // keep the order: earlier records first
            var r = new BinaryReader(new MemoryStream(data));
            byte kind = r.ReadByte();
            int n = r.ReadInt32();
            using (var msg = new Msg())
            {
                if (kind == AskBuckets)
                {
                    var wanted = new HashSet<int>();
                    for (int i = 0; i < n; i++) wanted.Add(r.ReadByte());
                    var lists = new Dictionary<int, List<KeyValuePair<int, int>>>();
                    foreach (int b in wanted) lists[b] = new List<KeyValuePair<int, int>>();
                    foreach (var kv in TrackerMap(KPrefabIDTracker.Get()))
                    {
                        KPrefabID k = kv.Value;
                        if (k == null || k.pendingDestruction || kv.Key >= LocalIdBase || kv.Key < 0 || !k.isSpawned) continue;
                        if (!lists.TryGetValue(kv.Key & (Buckets - 1), out var list)) continue;
                        if (!Saveable(k)) continue;
                        list.Add(new KeyValuePair<int, int>(kv.Key, k.GetSaveLoadTag().GetHash()));
                    }
                    foreach (var kv in lists)
                    {
                        msg.w.Write(RBucket); msg.w.Write((byte)kv.Key); msg.w.Write(kv.Value.Count);
                        foreach (var e in kv.Value) { msg.w.Write(e.Key); msg.w.Write(e.Value); }
                        written++;
                    }
                }
                else if (kind == AskObjects)
                {
                    budget = int.MaxValue; // asks come in batches of at most 200
                    for (int i = 0; i < n; i++)
                    {
                        int id = r.ReadInt32();
                        KPrefabID k = Find(id);
                        if (k == null || k.pendingDestruction || !k.isSpawned || !Synced(k)) { msg.w.Write(RDestroy); msg.w.Write(id); }
                        else { WriteSpawn(msg.w, k); HostObjectsAsked++; }
                        written++;
                    }
                }
                msg.Send(from);
            }
        }

        // ================================================================== guest

        public static long GuestSpawned, GuestSpawnExisting, GuestSpawnFailed, GuestDestroyed, GuestItems, GuestItemMissing,
            LocalDestroys, LocalCleaned, ReconcileFixed, ReconcileRemoved;
        public static int MissingPrefabs, IdConflicts, NotStored, watchLogs;
        private static readonly HashSet<int> watched = new HashSet<int>();
        private static readonly Dictionary<string, int> notStoredBy = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> conflictBy = new Dictionary<string, int>(), localCreatedBy = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> localDestroyedBy = new Dictionary<string, int>(), localCleanedBy = new Dictionary<string, int>(),
            failedBy = new Dictionary<string, int>();

        private static void Sample(Dictionary<string, int> d, string key)
        {
            d.TryGetValue(key, out int c);
            if (c > 0 || d.Count < 60) d[key] = c + 1;
        }

        private static string Top(Dictionary<string, int> d)
        {
            if (d.Count == 0) return "-";
            var l = new List<KeyValuePair<string, int>>(d);
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            for (int i = 0; i < l.Count && i < 10; i++) sb.Append(i > 0 ? ", " : "").Append(l[i].Key).Append(" x").Append(l[i].Value);
            return sb.ToString();
        }

        public static string GuestReport() =>
            $"spawned {GuestSpawned} (already there {GuestSpawnExisting}, failed {GuestSpawnFailed}: {Top(failedBy)}, missing prefabs {MissingPrefabs}), destroyed {GuestDestroyed}, " +
            $"item updates {GuestItems} (unknown {GuestItemMissing}); reconcile round {round}: fetched {ReconcileFixed}, removed {ReconcileRemoved}; " +
            $"local: destroyed by the guest's game {LocalDestroys} ({Top(localDestroyedBy)}), local objects cleaned {LocalCleaned} ({Top(localCleanedBy)}); " +
            $"assignments applied {GuestAssigns}, equipment changes {GuestEquips}; id script mismatches {ScriptMismatches}, id conflicts {IdConflicts} ({Top(conflictBy)}); objects {LastDigestCount} (digest {LastDigestMs:F1} ms); " +
            $"made locally by: {Top(localCreatedBy)}; arrived but not stored {NotStored} ({Top(notStoredBy)})";

        public static string HostReport() =>
            $"spawns {HostSpawns}, destroys {HostDestroys}, assignment/equipment changes {HostAssigns}, item updates {HostItems}, objects asked {HostObjectsAsked}, {HostBytes / 1024} KB; objects {LastDigestCount} (digest {LastDigestMs:F1} ms)";

        /// <summary>Guest entering a game: its own new objects get IDs the host will never use.</summary>
        public static void UseLocalIds()
        {
            if (KPrefabID.NextUniqueID < LocalIdBase) KPrefabID.NextUniqueID = LocalIdBase;
        }

        public static void OnRecords(byte[] data)
        {
            if (!GuestActive) return;
            Applying = true;
            try
            {
                using (var r = Inflate.Open(data)) // (the shared reader: no garbage per message; using is harmless)
                {
                    var buckets = new List<KeyValuePair<int, List<KeyValuePair<int, int>>>>();
                    while (true)
                    {
                        int kind = r.BaseStream.ReadByte();
                        if (kind < 0) break;
                        switch ((byte)kind)
                        {
                            case RSpawn: ReadSpawn(r); break;
                            case RDestroy: Destroy(r.ReadInt32()); break;
                            case RReplace: Retire(r.ReadInt32()); break;
                            case RStorageList: OnStorageList(r); break;
                            case RItem: ReadItem(r); break;
                            case RAssign: ApplyAssign(r.ReadInt32(), ReadIdentity(r, out bool known)); break;
                            case REquip: ApplyEquip(r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()); break;
                            case RAssignState: OnAssignState(r); break;
                            case REquipState: OnEquipState(r); break;
                            case RDigest: OnDigest(r); break;
                            case RBucket:
                            {
                                int b = r.ReadByte(), n = r.ReadInt32();
                                var list = new List<KeyValuePair<int, int>>(n);
                                for (int i = 0; i < n; i++) list.Add(new KeyValuePair<int, int>(r.ReadInt32(), r.ReadInt32()));
                                buckets.Add(new KeyValuePair<int, List<KeyValuePair<int, int>>>(b, list));
                                break;
                            }
                            default: Log.Warn($"entities: unknown record {kind}"); return;
                        }
                    }
                    if (buckets.Count > 0) OnBuckets(buckets);
                }
            }
            finally { Applying = false; }
        }

        private static void ReadSpawn(BinaryReader r)
        {
            int id = r.ReadInt32();
            string tag = Inflate.ReadId(r);
            int container = r.ReadInt32();
            byte idx = r.ReadByte();
            bool keepZero = r.ReadBoolean();
            byte[] blob = r.ReadBytes(r.ReadInt32());
            extraSince.Remove(id);
            KPrefabID existing = Find(id);
            if (existing != null)
            {
                if (existing.GetSaveLoadTag().Name == tag) { GuestSpawnExisting++; return; }
                IdConflicts++; // a different object under this ID: the host's wins
                Sample(conflictBy, existing.PrefabTag.Name + "->" + tag);
                if (Saveable(existing)) Util.KDestroyGameObject(existing.gameObject);
                KPrefabIDTracker.Get().Unregister(existing);
            }
            GameObject prefab = SaveLoader.Instance.saveManager.GetPrefab(new Tag(tag));
            if (prefab == null) { MissingPrefabs++; return; }
            SaveLoadRoot root = null;
            try { root = SaveLoadRoot.Load(prefab, new FastReader(blob)); }
            catch (System.Exception e) { Log.Warn($"entities: loading {tag} #{id} failed: {e.Message}"); }
            KPrefabID k = Find(id);
            if (root == null || k == null)
            {
                GuestSpawnFailed++;
                Sample(failedBy, tag);
                return;
            }
            GuestSpawned++;
            var kpe = k.GetComponent<PrimaryElement>();
            if (kpe != null) kpe.KeepZeroMassObject = keepZero;
            if (container >= 0)
            {
                MoveTo(k.gameObject, container, idx, Vector3.zero);
                var pk = k.GetComponent<Pickupable>();
                if (pk == null || pk.storage == null) { NotStored++; Sample(notStoredBy, tag + " in " + (Find(container)?.PrefabTag.Name ?? "missing #" + container)); }
            }
            if (asked.ContainsKey(id)) watched.Add(id);
            var proxy = k.GetComponent<MinionAssignablesProxy>();
            if (proxy != null) AdoptProxy(proxy);
        }

        public static int ProxiesAdopted;

        /// <summary>A new duplicant reaches a guest before its assignables proxy (the host makes the proxy while the
        /// duplicant spawns, a frame later), so the guest's copy makes a proxy of its own — local, and cleaned up a few
        /// seconds later, leaving the duplicant pointing at nothing. When the host's proxy arrives, the duplicant takes
        /// that one instead.</summary>
        private static void AdoptProxy(MinionAssignablesProxy proxy)
        {
            KPrefabID t = Find(proxy.TargetInstanceID);
            if (t == null) return;
            var mi = t.GetComponent<MinionIdentity>();
            var stored = t.GetComponent<StoredMinionIdentity>();
            Ref<MinionAssignablesProxy> r = mi != null ? mi.assignableProxy : stored != null ? stored.assignableProxy : null;
            if (r == null) return;
            MinionAssignablesProxy old = r.Get();
            if (old == proxy) return;
            r.Set(proxy);
            proxy.SetTarget(mi != null ? (IAssignableIdentity)mi : stored, t.gameObject);
            var oldId = old != null ? old.GetComponent<KPrefabID>() : null;
            if (oldId != null && oldId.InstanceID >= LocalIdBase) Util.KDestroyGameObject(old.gameObject);
            ProxiesAdopted++;
            Log.Info($"entities: {t.name} now uses the host's assignables proxy #{proxy.GetComponent<KPrefabID>().InstanceID}");
        }

        public static long HostReplaced, GuestReplaced;
        private static int minionCleanupsLogged;

        /// <summary>Server: a MinionStorage (a rocket's crew storage) took in or let out a duplicant.</summary>
        public static void StorageChanged(MinionStorage ms)
        {
            var k = ms != null ? ms.GetComponent<KPrefabID>() : null;
            if (HostActive && k != null && Synced(k)) events.Add(new Ev { kind = RStorageList, id = k.InstanceID });
        }

        private static readonly System.Reflection.FieldInfo RefId = AccessTools.Field(typeof(Ref<KPrefabID>), "id");

        /// <summary>Guest: the storage's list of stored duplicants is the server's (what its crew panel shows).</summary>
        private static void OnStorageList(BinaryReader r)
        {
            int id = r.ReadInt32(), n = r.ReadInt32();
            var list = new List<MinionStorage.Info>(n);
            for (int i = 0; i < n; i++)
            {
                var guid = new System.Guid(r.ReadBytes(16));
                string name = Inflate.ReadId(r);
                int target = r.ReadInt32();
                KPrefabID t = Find(target);
                var reference = t != null ? new Ref<KPrefabID>(t) : new Ref<KPrefabID>();
                if (t == null) RefId.SetValue(reference, target);
                var info = new MinionStorage.Info(name, reference) { id = guid };
                list.Add(info);
            }
            var ms = Find(id)?.GetComponent<MinionStorage>();
            if (ms != null) ms.SetStoredMinionInfo(list);
        }

        /// <summary>Server: the game moved an id to another object (MinionStorage.RedirectInstanceTracker: a duplicant
        /// going into a rocket's storage becomes a "stored duplicant" object with its id, and back). The old object is
        /// destroyed with id -1, so on its own nothing would tell guests; and the tracker still maps the id to the old
        /// object. Point the id at the new object, and tell guests to replace theirs.</summary>
        public static void Redirected(GameObject src, GameObject dest, int destOldId)
        {
            var k = dest != null ? dest.GetComponent<KPrefabID>() : null;
            if (k == null || k.InstanceID < 0) return;
            Dictionary<int, KPrefabID> map = TrackerMapRef(KPrefabIDTracker.Get());
            map[k.InstanceID] = k;
            if (destOldId >= 0 && destOldId != k.InstanceID && map.TryGetValue(destOldId, out KPrefabID was) && was == k) map.Remove(destOldId);
            if (HostActive) events.Add(new Ev { kind = RReplace, id = k.InstanceID });
            Log.Info($"entities: #{k.InstanceID} is now {k.PrefabTag.Name} (was {(src != null ? src.name : "?")})");
        }

        private static readonly AccessTools.FieldRef<KPrefabIDTracker, Dictionary<int, KPrefabID>> TrackerMapRef =
            AccessTools.FieldRefAccess<KPrefabIDTracker, Dictionary<int, KPrefabID>>("prefabIdMap");

        /// <summary>Guest: the server's object with this id was replaced; the next record spawns the new one. Do what
        /// the game did there: the old one gives up the id, then goes.</summary>
        private static void Retire(int id)
        {
            extraSince.Remove(id);
            Dictionary<int, KPrefabID> map = TrackerMapRef(KPrefabIDTracker.Get());
            if (!map.TryGetValue(id, out KPrefabID k)) { Log.Info($"entities: replacing #{id}: nothing here under that id"); return; }
            map.Remove(id); // also when that object is already destroyed: the new one registers under this id
            if (k == null) { Log.Info($"entities: replacing #{id}: the object here was already destroyed"); return; }
            Log.Info($"entities: replacing #{id} {k.PrefabTag.Name}");
            k.InstanceID = -1;
            var p = k.GetComponent<Pickupable>();
            if (p != null && p.storage != null) p.storage.Remove(k.gameObject, false);
            Util.KDestroyGameObject(k.gameObject);
            GuestReplaced++;
        }

        private static void Destroy(int id)
        {
            extraSince.Remove(id);
            KPrefabID k = Find(id);
            if (k == null || k.pendingDestruction) return;
            var p = k.GetComponent<Pickupable>();
            if (p != null) OffConveyor(p);
            if (p != null && p.storage != null) p.storage.Remove(k.gameObject, false);
            Util.KDestroyGameObject(k.gameObject);
            GuestDestroyed++;
        }

        private static Storage StorageOf(int container, byte idx)
        {
            KPrefabID owner = Find(container);
            if (owner == null) return null;
            Storage[] all = owner.GetComponents<Storage>();
            return idx < all.Length ? all[idx] : null;
        }

        // ---- conveyor rails on a guest: they don't move anything themselves (SolidConduitFlow.Sim200ms is off while
        // mirroring); each item sits in the rail cell the host has it in

        public static long ConveyorMoves;

        /// <summary>The rail cell holding this item on this guest, or -1.</summary>
        private static int RailCellOf(SolidConduitFlow flow, Pickupable p)
        {
            SolidConduitFlow.SOAInfo soa = flow.GetSOAInfo();
            for (int i = 0; i < soa.NumEntries; i++)
            {
                int cell = soa.GetCell(i);
                if (flow.GetPickupable(flow.GetContents(cell).pickupableHandle) == p) return cell;
            }
            return -1;
        }

        private static readonly Dictionary<Pickupable, int> guestRail = new Dictionary<Pickupable, int>();

        private static int TrackedRailCell(SolidConduitFlow flow, Pickupable p)
        {
            if (guestRail.TryGetValue(p, out int cell) && flow.GetPickupable(flow.GetContents(cell).pickupableHandle) == p) return cell;
            cell = RailCellOf(flow, p);
            if (cell >= 0) guestRail[p] = cell; else guestRail.Remove(p);
            return cell;
        }

        private static void OffConveyor(Pickupable p)
        {
            SolidConduitFlow flow = Game.Instance != null ? Game.Instance.solidConduitFlow : null;
            if (flow == null) return;
            int cell = TrackedRailCell(flow, p);
            if (cell >= 0) flow.RemovePickupable(cell);
            guestRail.Remove(p);
        }

        private static void OnRail(Pickupable p, int cell)
        {
            SolidConduitFlow flow = Game.Instance != null ? Game.Instance.solidConduitFlow : null;
            if (flow == null || !flow.HasConduit(cell)) return;
            Pickupable there = flow.GetPickupable(flow.GetContents(cell).pickupableHandle);
            if (there == p) { guestRail[p] = cell; return; }
            int from = TrackedRailCell(flow, p);
            if (from >= 0) flow.RemovePickupable(from);
            if (there != null) { flow.RemovePickupable(cell); guestRail.Remove(there); } // it gets its own record
            if (p.storage != null) p.storage.Drop(p.gameObject, false);
            flow.AddPickupable(cell, p);
            p.transform.SetPosition(Grid.CellToPosCCC(cell, Grid.SceneLayer.SolidConduitContents));
            guestRail[p] = cell;
            ConveyorMoves++;
        }

        /// <summary>Put an item where the host has it: in a storage, on a conveyor rail, or loose at a position.</summary>
        private static void MoveTo(GameObject go, int container, byte idx, Vector3 pos)
        {
            var p = go.GetComponent<Pickupable>();
            if (p != null && container == OnConveyor) { OnRail(p, (int)pos.x); return; }
            if (p != null && guestRail.Count > 0 && guestRail.ContainsKey(p)) OffConveyor(p);
            Storage cur = p != null ? p.storage : null;
            Storage want = container >= 0 ? StorageOf(container, idx) : null;
            if (container >= 0 && want == null) return; // its container isn't here (yet)
            if (cur != want)
            {
                if (cur != null)
                {
                    if (want == null) cur.Drop(go, false);
                    else cur.Remove(go, false);
                }
                if (want != null)
                {
                    // no merging into what is already there (the host didn't merge it either), then hide it like a load does
                    want.Store(go, hide_popups: true, block_events: false, do_disease_transfer: false, is_deserializing: true);
                    ApplyStoredModifiers?.Invoke(want, new object[] { go, true, false });
                }
            }
            if (want == null && container < 0)
            {
                Vector3 old = go.transform.GetPosition();
                if (Mathf.Abs(old.x - pos.x) > 0.05f || Mathf.Abs(old.y - pos.y) > 0.05f) go.transform.SetPosition(new Vector3(pos.x, pos.y, old.z));
            }
        }

        private static void ReadItem(BinaryReader r)
        {
            int id = r.ReadInt32(), container = r.ReadInt32();
            byte idx = r.ReadByte();
            float x = r.ReadSingle(), y = r.ReadSingle(), mass = r.ReadSingle(), temp = r.ReadSingle();
            KPrefabID k = Find(id);
            if (k == null) { GuestItemMissing++; return; }
            GuestItems++;
            // a missile in flight is placed by the timeline (Meteors); 4 updates a second would make it jump
            if (!(container < 0 && Meteors.IsMover(id))) MoveTo(k.gameObject, container, idx, new Vector3(x, y, 0f));
            var pe = k.GetComponent<PrimaryElement>();
            if (pe == null) return;
            if (Mathf.Abs(pe.Mass - mass) > Mathf.Max(0.005f * mass, 0.001f)) pe.Mass = mass;
            if (Mathf.Abs(pe.Temperature - temp) > 1f) pe.Temperature = temp;
        }

        // ------------------------------------------------------------------ reconcile

        private static uint[] hostDigest;
        private static int round, quietUntil, bucketOffset;
        private static readonly Dictionary<int, int> extraSince = new Dictionary<int, int>(); // id -> round it was first extra
        private static readonly Dictionary<int, int> asked = new Dictionary<int, int>();      // id -> times asked
        private static readonly List<int> pendingAsk = new List<int>();

        private static void OnDigest(BinaryReader r)
        {
            int hostCount = r.ReadInt32();
            hostDigest = new uint[Buckets];
            for (int i = 0; i < Buckets; i++) hostDigest[i] = r.ReadUInt32();
            round++;
            if (round < quietUntil) return;
            uint[] mine = ComputeDigest(false, out int myCount);
            var differ = new List<byte>();
            for (int j = 0; j < Buckets; j++)
            {
                int i = (j + bucketOffset) % Buckets; // a different part of the list each round
                if (mine[i] != hostDigest[i]) differ.Add((byte)i);
            }
            if (differ.Count == 0) return;
            if (differ.Count > 64) differ.RemoveRange(64, differ.Count - 64);
            bucketOffset = (differ[differ.Count - 1] + 1) % Buckets;
            Log.Info($"entities: digest round {round}: host {hostCount} objects, here {myCount}, {differ.Count} buckets differ");
            Ask(AskBuckets, w => { foreach (byte b in differ) w.Write(b); }, differ.Count);
        }

        private static void OnBuckets(List<KeyValuePair<int, List<KeyValuePair<int, int>>>> buckets)
        {
            var want = new Dictionary<int, Dictionary<int, int>>();
            foreach (var kv in buckets)
            {
                var d = new Dictionary<int, int>(kv.Value.Count);
                foreach (var e in kv.Value) d[e.Key] = e.Value;
                want[kv.Key] = d;
            }
            var mine = new Dictionary<int, Dictionary<int, KPrefabID>>();
            foreach (int b in want.Keys) mine[b] = new Dictionary<int, KPrefabID>();
            foreach (var kv in TrackerMap(KPrefabIDTracker.Get()))
            {
                KPrefabID k = kv.Value;
                if (k == null || k.pendingDestruction || kv.Key >= LocalIdBase || kv.Key < 0) continue;
                if (!mine.TryGetValue(kv.Key & (Buckets - 1), out var d) || !Saveable(k)) continue;
                d[kv.Key] = k;
            }
            int missing = 0, removed = 0;
            foreach (var kv in want)
            {
                Dictionary<int, KPrefabID> have = mine[kv.Key];
                foreach (var e in kv.Value)
                {
                    if (have.TryGetValue(e.Key, out KPrefabID k) && k.GetSaveLoadTag().GetHash() == e.Value) continue;
                    if (k != null) { Util.KDestroyGameObject(k.gameObject); removed++; } // same id, other object
                    asked.TryGetValue(e.Key, out int times);
                    if (times >= 3) continue; // it keeps failing here; don't hammer the host
                    asked[e.Key] = times + 1;
                    pendingAsk.Add(e.Key);
                    missing++;
                }
                foreach (var e in have)
                {
                    if (kv.Value.ContainsKey(e.Key)) { extraSince.Remove(e.Key); continue; }
                    // the host doesn't have it: remove it if that is still so in the next round
                    if (!extraSince.TryGetValue(e.Key, out int since)) { extraSince[e.Key] = round; continue; }
                    if (since >= round) continue;
                    extraSince.Remove(e.Key);
                    var p = e.Value.GetComponent<Pickupable>();
                    if (p != null && p.storage != null) p.storage.Remove(e.Value.gameObject, false);
                    Util.KDestroyGameObject(e.Value.gameObject);
                    removed++;
                }
            }
            ReconcileFixed += missing;
            ReconcileRemoved += removed;
            // nothing it can do (only objects it gave up on): look less often
            bool strikes = false;
            foreach (int r in extraSince.Values) if (r == round) strikes = true;
            if (missing == 0 && removed == 0 && !strikes) quietUntil = round + 6;
            if (missing > 0 || removed > 0) Log.Info($"entities: reconcile round {round}: asking for {missing} missing objects, removed {removed}");
            if (pendingAsk.Count > 0)
            {
                var ids = new List<int>(pendingAsk);
                pendingAsk.Clear();
                for (int start = 0; start < ids.Count; start += 200)
                {
                    int n = Mathf.Min(200, ids.Count - start);
                    int s0 = start;
                    Ask(AskObjects, w => { for (int i = s0; i < s0 + n; i++) w.Write(ids[i]); }, n);
                }
            }
        }

        private static void Ask(byte kind, System.Action<BinaryWriter> body, int n)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) { w.Write(kind); w.Write(n); body(w); }
            CommandBus.Client.SendRelay(Proto.TargetHost, ChAsk, ms.ToArray());
        }

        // ------------------------------------------------------------------ local objects

        private static readonly Dictionary<int, float> localSeen = new Dictionary<int, float>();
        private static float nextClean;

        /// <summary>Guest, every frame: objects its own game made (local IDs) have no counterpart on the host; the
        /// host's version of the same event arrives on its own. Remove them after a grace period.</summary>
        public static void GuestTick(float now)
        {
            if (!GuestActive || now < nextClean) return;
            nextClean = now + CleanInterval;
            List<KPrefabID> doomed = null;
            var seenNow = new HashSet<int>();
            foreach (var kv in TrackerMap(KPrefabIDTracker.Get()))
            {
                if (kv.Key < LocalIdBase) continue;
                KPrefabID k = kv.Value;
                if (k == null || k.pendingDestruction || !Saveable(k)) continue;
                seenNow.Add(kv.Key);
                if (!localSeen.TryGetValue(kv.Key, out float since)) { localSeen[kv.Key] = now; continue; }
                if (now - since < LocalGrace) continue;
                (doomed ?? (doomed = new List<KPrefabID>())).Add(k);
            }
            var gone = new List<int>();
            foreach (int id in localSeen.Keys) if (!seenNow.Contains(id)) gone.Add(id);
            foreach (int id in gone) localSeen.Remove(id);
            if (doomed == null) return;
            Applying = true;
            try
            {
                foreach (KPrefabID k in doomed)
                {
                    localSeen.Remove(k.InstanceID);
                    Sample(localCleanedBy, k.PrefabTag.Name);
                    var p = k.GetComponent<Pickupable>();
                    if (p != null && p.storage != null) p.storage.Remove(k.gameObject, false);
                    Util.KDestroyGameObject(k.gameObject);
                    LocalCleaned++;
                }
            }
            finally { Applying = false; }
        }

        // ------------------------------------------------------------------ assignments and equipment

        // Who owns a bed / toilet / suit (Assignable.Assign) and who wears what (Equipment.Equip) are not storages, so
        // the object stream above doesn't carry them. The host sends each change in order with the other records, and
        // with every digest the full lists, so a guest also fixes what it missed.

        public static void OnAssigned(Assignable a)
        {
            var k = a != null ? a.GetComponent<KPrefabID>() : null;
            if (HostActive && Synced(k)) events.Add(new Ev { kind = RAssign, id = k.InstanceID });
        }

        public static void OnEquipChanged(Equipment eq, Equippable e, bool equip)
        {
            if (!HostActive || eq == null || e == null) return;
            var owner = eq.GetComponent<KPrefabID>();
            var item = e.GetComponent<KPrefabID>();
            if (Synced(owner) && Synced(item)) events.Add(new Ev { kind = REquip, id = owner.InstanceID, id2 = item.InstanceID, flag = equip });
        }

        private const byte IdNone = 0, IdMinion = 1, IdProxy = 2, IdStored = 3, IdGroup = 4, IdOther = 5;

        internal static void WriteIdentity(BinaryWriter w, IAssignableIdentity a)
        {
            if (a == null) { w.Write(IdNone); return; }
            if (a is AssignmentGroup g) { w.Write(IdGroup); w.Write(g.id ?? ""); return; }
            byte kind = a is MinionIdentity ? IdMinion : a is MinionAssignablesProxy ? IdProxy : a is StoredMinionIdentity ? IdStored : IdOther;
            var k = kind != IdOther ? ((KMonoBehaviour)a).GetComponent<KPrefabID>() : null;
            if (k == null) { w.Write(IdOther); return; }
            w.Write(kind); w.Write(k.InstanceID);
        }

        /// <param name="known">false: something this side can't name (a room, ...): leave the assignment alone</param>
        internal static IAssignableIdentity ReadIdentity(BinaryReader r, out bool known)
        {
            known = true;
            byte kind = r.ReadByte();
            switch (kind)
            {
                case IdNone: return null;
                case IdGroup:
                {
                    string id = Inflate.ReadId(r);
                    var groups = Game.Instance != null ? Game.Instance.assignmentManager?.assignment_groups : null;
                    if (groups != null && groups.TryGetValue(id, out AssignmentGroup g)) return g;
                    known = false; return null;
                }
                case IdMinion: case IdProxy: case IdStored:
                {
                    KPrefabID k = Find(r.ReadInt32());
                    IAssignableIdentity a = k == null ? null
                        : kind == IdMinion ? k.GetComponent<MinionIdentity>()
                        : kind == IdProxy ? (IAssignableIdentity)k.GetComponent<MinionAssignablesProxy>()
                        : k.GetComponent<StoredMinionIdentity>();
                    if (a == null) known = false;
                    return a;
                }
                default: known = false; return null;
            }
        }

        public static long GuestAssigns, GuestEquips;

        private static void ApplyAssign(int id, IAssignableIdentity who)
        {
            var a = Find(id)?.GetComponent<Assignable>();
            if (a == null) return;
            if (who == null) { if (a.assignee != null) { a.Unassign(); GuestAssigns++; } return; }
            if (!SameIdentity(a.assignee, who)) { a.Assign(who); GuestAssigns++; }
        }

        private static bool SameIdentity(IAssignableIdentity a, IAssignableIdentity b)
        {
            if (a == null || b == null) return a == b;
            if (a == b) return true;
            // a minion and its proxy are the same person
            return a.GetSoleOwner() == b.GetSoleOwner() && !(a is AssignmentGroup) && !(b is AssignmentGroup);
        }

        private static Equipment EquipmentOf(Equippable e) =>
            e.isEquipped && e.assignee != null && e.assignee.GetSoleOwner() != null ? e.assignee.GetSoleOwner().GetComponent<Equipment>() : null;

        private static void ApplyEquip(int ownerId, int itemId, bool equip)
        {
            var eq = Find(ownerId)?.GetComponent<Equipment>();
            var e = Find(itemId)?.GetComponent<Equippable>();
            if (eq == null || e == null) return;
            Equipment now = EquipmentOf(e);
            if (!equip)
            {
                if (e.isEquipped && now != null) { now.Unequip(e); GuestEquips++; }
                return;
            }
            if (e.isEquipped && now == eq) return;
            if (e.isEquipped && now != null) now.Unequip(e);
            var proxy = eq.GetComponent<MinionAssignablesProxy>();
            if (proxy != null && !SameIdentity(e.assignee, proxy)) e.Assign(proxy);
            eq.Equip(e);
            GuestEquips++;
        }

        private static void WriteAssignState(BinaryWriter w)
        {
            var assigned = new List<Assignable>();
            foreach (Assignable a in Components.AssignableItems.Items)
                if (a != null && a.assignee != null && Synced(a.GetComponent<KPrefabID>())) assigned.Add(a);
            w.Write(RAssignState); w.Write(assigned.Count);
            foreach (Assignable a in assigned) { w.Write(a.GetComponent<KPrefabID>().InstanceID); WriteIdentity(w, a.assignee); }
            var worn = new List<KeyValuePair<int, int>>();
            foreach (Assignable a in assigned)
            {
                var e = a as Equippable;
                Equipment eq = e != null ? EquipmentOf(e) : null;
                var owner = eq != null ? eq.GetComponent<KPrefabID>() : null;
                if (owner != null) worn.Add(new KeyValuePair<int, int>(a.GetComponent<KPrefabID>().InstanceID, owner.InstanceID));
            }
            w.Write(REquipState); w.Write(worn.Count);
            foreach (var kv in worn) { w.Write(kv.Key); w.Write(kv.Value); }
            written += 2;
        }

        private static void OnAssignState(BinaryReader r)
        {
            int n = r.ReadInt32();
            var listed = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                IAssignableIdentity who = ReadIdentity(r, out bool known);
                listed.Add(id);
                if (known) ApplyAssign(id, who);
            }
            // assigned here, not on the host
            var stale = new List<Assignable>();
            foreach (Assignable a in Components.AssignableItems.Items)
            {
                if (a == null || a.assignee == null) continue;
                var k = a.GetComponent<KPrefabID>();
                if (Synced(k) && !listed.Contains(k.InstanceID)) stale.Add(a);
            }
            foreach (Assignable a in stale) { a.Unassign(); GuestAssigns++; }
        }

        private static void OnEquipState(BinaryReader r)
        {
            int n = r.ReadInt32();
            var worn = new Dictionary<int, int>();
            for (int i = 0; i < n; i++) worn[r.ReadInt32()] = r.ReadInt32();
            foreach (var kv in worn) ApplyEquip(kv.Value, kv.Key, true);
            var off = new List<Equippable>();
            foreach (Assignable a in Components.AssignableItems.Items)
            {
                var e = a as Equippable;
                if (e == null || !e.isEquipped) continue;
                var k = e.GetComponent<KPrefabID>();
                if (Synced(k) && !worn.ContainsKey(k.InstanceID)) off.Add(e);
            }
            foreach (Equippable e in off) { EquipmentOf(e)?.Unequip(e); GuestEquips++; }
        }

        // ------------------------------------------------------------------ guest: the host does this work

        /// <summary>Building logic that moves material in and out of storages. On a guest it only repeats what the
        /// host does (and the results arrive anyway), while making and destroying stored chunks of its own — so it is
        /// off while mirroring.</summary>
        private static readonly string[][] HostOnly =
        {
            new[] { nameof(ElementConverter), "ConvertMass" },          // electrolyzers, scrubbers, ... consuming and producing
            new[] { nameof(ElementConsumer), "AddMassInternal" },       // pumps / vents pulling gas or liquid into storage
            new[] { nameof(ConduitConsumer), "ConduitUpdate" },         // pipe inputs
            new[] { nameof(ConduitDispenser), "ConduitUpdate" },        // pipe outputs
            new[] { nameof(SolidConduitConsumer), "ConduitUpdate" },    // conveyor inputs
            new[] { nameof(SolidConduitDispenser), "ConduitUpdate" },   // conveyor outputs
            new[] { nameof(EntombedItemManager), "Sim33ms" },           // items in solid cells are folded away
            new[] { nameof(Sublimates), "Sim200ms" },                   // items turning into gas over time
            new[] { nameof(Exhaust), "Sim200ms" },                      // vents emitting what they hold
            new[] { nameof(ColdBreather), "OnSimEmitted" },             // wheezeworts using up the gas they took in
            new[] { nameof(PlantElementAbsorbers), "Sim200ms" },        // plants drinking / taking fertilizer from farm tiles
            new[] { nameof(EnergyGenerator), "Emit" },                  // generator exhaust (CO2, polluted water)
            new[] { nameof(SolidConduitFlow), "Sim200ms" },             // conveyor rails moving items (cells come from the host)
            new[] { nameof(StoredMinionIdentity), "CleanupLimboMinions" }, // "not in any storage list -> bring it back out": the server decides
            new[] { nameof(SolidTransferArm), "BatchUpdate" },          // auto-sweepers picking items up
        };

        private static void RedirectPrefix(GameObject __1, out int __state)
        {
            var k = __1 != null ? __1.GetComponent<KPrefabID>() : null;
            __state = k != null ? k.InstanceID : -1;
        }

        private static void StoragePostfix(MinionStorage __instance) => StorageChanged(__instance);

        private static void RedirectPostfix(GameObject __0, GameObject __1, int __state)
        {
            if (CommandBus.Active && CommandBus.IsHost) Redirected(__0, __1, __state);
        }

        public static void ApplyPatches(Harmony h)
        {
            var prefix = new HarmonyMethod(typeof(EntitySync), nameof(HostOnlyPrefix));
            int n = 0;
            foreach (string[] t in HostOnly)
            {
                try
                {
                    var m = AccessTools.Method(AccessTools.TypeByName(t[0]), t[1]);
                    if (m == null) { Log.Warn($"entities: {t[0]}.{t[1]} not found"); continue; }
                    h.Patch(m, prefix: prefix);
                    n++;
                }
                catch (System.Exception e) { Log.Warn($"entities: can't patch {t[0]}.{t[1]}: {e.Message}"); } // never fail the mod load
            }
            PatchGenerators(h);
            try
            {
                var storagePost = new HarmonyMethod(typeof(EntitySync), nameof(StoragePostfix));
                h.Patch(AccessTools.Method(typeof(MinionStorage), nameof(MinionStorage.SerializeMinion)), postfix: storagePost);
                h.Patch(AccessTools.Method(typeof(MinionStorage), nameof(MinionStorage.DeserializeMinion), new[] { typeof(System.Guid), typeof(Vector3) }), postfix: storagePost);
                h.Patch(AccessTools.Method(typeof(MinionStorage), nameof(MinionStorage.DeleteStoredMinion)), postfix: storagePost);
                h.Patch(AccessTools.Method(typeof(MinionStorage), nameof(MinionStorage.RedirectInstanceTracker)),
                    prefix: new HarmonyMethod(typeof(EntitySync), nameof(RedirectPrefix)), postfix: new HarmonyMethod(typeof(EntitySync), nameof(RedirectPostfix)));
            }
            catch (System.Exception e) { Log.Warn("entities: can't patch MinionStorage.RedirectInstanceTracker: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(KSerialization.Manager), nameof(KSerialization.Manager.GetDeserializationTemplate), new[] { typeof(Type) }),
                    prefix: new HarmonyMethod(typeof(EntitySync), nameof(MissingTemplatePrefix)));
            }
            catch (System.Exception e) { Log.Warn("entities: can't patch the template lookup: " + e.Message); }
            Log.Info($"entities: {n} host-only building updates are off on guests");
        }

        private static bool HostOnlyPrefix() => !Puppets.Mirroring;

        // ---- deserializing a kind of object the guest's save didn't have

        // A save carries the field layout of every class it contains, and loading takes those as the templates to read
        // with. An object of a class that wasn't in the guest's save (a duplicant stored in a rocket, say) has none, and
        // fails to load. Host and guest run the same build, so the guest's own layout of that class is the host's: make
        // the missing template from it (as reading a save's directory would).
        private static readonly System.Reflection.FieldInfo TemplatesByTypeField = AccessTools.Field(typeof(KSerialization.Manager), "deserializationTemplatesByType");
        private static readonly System.Reflection.FieldInfo TemplatesByNameField = AccessTools.Field(typeof(KSerialization.Manager), "deserializationTemplatesByTypeName");
        private static Dictionary<Type, KSerialization.DeserializationTemplate> TemplatesByType() => (Dictionary<Type, KSerialization.DeserializationTemplate>)TemplatesByTypeField.GetValue(null);
        private static Dictionary<string, KSerialization.DeserializationTemplate> TemplatesByName() => (Dictionary<string, KSerialization.DeserializationTemplate>)TemplatesByNameField.GetValue(null);
        public static int TemplatesMade;

        /// <summary>Test: the load template of a class this guest's save doesn't have can be made and used.</summary>
        public static string TemplateSelfTest(Type type)
        {
            bool had = TemplatesByType().ContainsKey(type);
            Applying = true;
            try
            {
                KSerialization.Manager.GetDeserializationMapping(type);
                return $"{type.Name}: {(had ? "was in the save" : "made")}, mapping ok";
            }
            catch (Exception e) { return $"{type.Name}: FAILED {e.Message}"; }
            finally { Applying = false; }
        }

        private static void MissingTemplatePrefix(Type type)
        {
            // while mirroring, not only while a record is applied: a state machine restores its saved data later, in
            // the object's OnSpawn (e.g. a sticker's StatesInstance), and any data a guest reads then is the server's
            if (!(Applying || Puppets.Mirroring) || type == null || TemplatesByType().ContainsKey(type)) return;
            KSerialization.SerializationTemplate st = KSerialization.Manager.GetSerializationTemplate(type);
            if (st == null) return;
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) st.SerializeTemplate(w);
            string name = KSerialization.Helper.GetKTypeString(type);
            var dt = new KSerialization.DeserializationTemplate(name, new FastReader(ms.ToArray()));
            TemplatesByType()[type] = dt;
            TemplatesByName()[name] = dt;
            TemplatesMade++;
            Log.Info($"entities: made the load template for {name} (not in the save this guest loaded)");
        }

        // Generators still make power on a guest (or every building there would show "no power"), but the fuel they
        // burn is the host's to burn: inside EnergySim200ms, taking from storage does nothing.
        private static bool generatorRunning;
        private static void GeneratorPrefix() => generatorRunning = Puppets.Mirroring;
        private static void GeneratorPostfix() => generatorRunning = false;
        private static bool ConsumePrefix() => !generatorRunning;

        private static void PatchGenerators(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(EnergyGenerator), nameof(EnergyGenerator.EnergySim200ms)),
                    prefix: new HarmonyMethod(typeof(EntitySync), nameof(GeneratorPrefix)),
                    postfix: new HarmonyMethod(typeof(EntitySync), nameof(GeneratorPostfix)));
                h.Patch(AccessTools.Method(typeof(Storage), nameof(Storage.ConsumeIgnoringDisease), new[] { typeof(Tag), typeof(float) }),
                    prefix: new HarmonyMethod(typeof(EntitySync), nameof(ConsumePrefix)));
            }
            catch (System.Exception e) { Log.Warn("entities: can't patch generators: " + e.Message); }
        }

        /// <summary>For the sync test: every synced object as this side has it.</summary>
        public static string Dump()
        {
            var sb = new StringBuilder();
            int local = 0;
            foreach (var kv in TrackerMap(KPrefabIDTracker.Get()))
            {
                KPrefabID k = kv.Value;
                if (k == null || k.pendingDestruction || !Saveable(k)) continue;
                if (kv.Key >= LocalIdBase) { local++; continue; }
                Vector3 p = k.transform.GetPosition();
                Container(k.gameObject, out int container, out byte idx);
                var pe = k.GetComponent<PrimaryElement>();
                sb.Append(kv.Key).Append(' ').Append(k.GetSaveLoadTag().Name).Append(' ')
                  .Append(p.x.ToString("F2")).Append(' ').Append(p.y.ToString("F2")).Append(' ')
                  .Append(pe != null ? pe.Mass.ToString("G6") : "-").Append(' ')
                  .Append(container).Append(' ').Append(idx).Append(' ')
                  .Append(k.GetComponent<Brain>() != null ? 1 : 0).Append('\n');
            }
            sb.Append("# local ").Append(local).Append('\n');
            return sb.ToString();
        }
    }

    // --------------------------------------------------------------------- hooks

    /// <summary>A guest's own objects get IDs from their own range from the very start of loading (the load itself
    /// makes thousands); the host's IDs stay the host's.</summary>
    [HarmonyPatch(typeof(Game), "OnPrefabInit")]
    internal static class Game_OnPrefabInit_LocalIds_Patch
    {
        private static void Postfix()
        {
            CoopSettings s = CoopMod.Settings;
            if (s != null && s.Enabled && !s.IsHost) EntitySync.UseLocalIds();
        }
    }

    /// <summary>On a guest, items don't merge by themselves: the host merges them and says so.</summary>
    [HarmonyPatch(typeof(Pickupable), nameof(Pickupable.TryAbsorb))]
    internal static class Pickupable_TryAbsorb_Patch
    {
        private static bool Prefix(ref bool __result)
        {
            if (!Puppets.Mirroring || EntitySync.Applying) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>Loading the host's objects: an empty chunk the host keeps (KeepZeroMassObject, not saved) must not
    /// destroy itself on arrival.</summary>
    [HarmonyPatch(typeof(PrimaryElement), "OnDeserialized")]
    internal static class PrimaryElement_OnDeserialized_Patch
    {
        private static void Prefix(PrimaryElement __instance)
        {
            if (EntitySync.Applying) __instance.KeepZeroMassObject = true;
        }
    }

    [HarmonyPatch(typeof(TracesExtesions), nameof(TracesExtesions.DeleteObject), new[] { typeof(GameObject) })]
    internal static class DeleteObject_Patch
    {
        private static void Prefix(GameObject __0) => EntitySync.OnDeleteObject(__0);
    }

    [HarmonyPatch(typeof(Assignable), nameof(Assignable.Assign), new[] { typeof(IAssignableIdentity), typeof(AssignableSlotInstance) })]
    internal static class Assignable_Assign_Patch
    {
        private static void Postfix(Assignable __instance) => EntitySync.OnAssigned(__instance);
    }

    [HarmonyPatch(typeof(Assignable), nameof(Assignable.Unassign))]
    internal static class Assignable_Unassign_Patch
    {
        private static void Postfix(Assignable __instance) => EntitySync.OnAssigned(__instance);
    }

    [HarmonyPatch(typeof(Equipment), nameof(Equipment.Equip))]
    internal static class Equipment_Equip_Patch
    {
        private static void Postfix(Equipment __instance, Equippable __0) => EntitySync.OnEquipChanged(__instance, __0, true);
    }

    [HarmonyPatch(typeof(Equipment), nameof(Equipment.Unequip))]
    internal static class Equipment_Unequip_Patch
    {
        private static void Postfix(Equipment __instance, Equippable __0) => EntitySync.OnEquipChanged(__instance, __0, false);
    }

    [HarmonyPatch(typeof(KPrefabID), "OnSpawn")]
    internal static class KPrefabID_OnSpawn_Patch
    {
        private static void Postfix(KPrefabID __instance) => EntitySync.OnSpawned(__instance);
    }

    [HarmonyPatch(typeof(KPrefabID), "OnCleanUp")]
    internal static class KPrefabID_OnCleanUp_Patch
    {
        private static void Prefix(KPrefabID __instance) => EntitySync.OnCleanedUp(__instance);
    }

    [HarmonyPatch(typeof(Util), nameof(Util.KInstantiate), new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(string), typeof(bool), typeof(int) })]
    internal static class Util_KInstantiate_Patch
    {
        private static void Postfix(GameObject __result, bool __5)
        {
            if (__5 && __result != null) EntitySync.OnInstantiated(__result);
        }
    }
}
