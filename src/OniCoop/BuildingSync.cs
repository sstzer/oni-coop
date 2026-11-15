namespace OniCoop
{
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>What buildings look like on a guest follows the host.
    ///
    /// Animations: a guest doesn't run most building logic (or runs it on its own, without duplicants), so a machine
    /// that works on the host could stand idle there, a door the host opened stays shut, a battery or a storage bin
    /// shows its own fill level. The host records every animation a finished building (or one of its meters / child
    /// parts) starts, the moment it starts (KAnimControllerBase.StartQueuedAnim), and for paused meters their position
    /// (a slice of the buildings each frame). Each record carries the host's time and travels with the duplicants'
    /// keyframes, so a guest plays it at the same moment of the timeline as the duplicants: the airlock opens as the
    /// duplicant reaches it, not half a second earlier, and short in-between animations (opening, closing) aren't
    /// skipped. A guest plays exactly that and keeps its own logic from changing them.
    ///
    /// Pipes: the gas / liquid inside the pipes a guest is looking at (like WorldSync does for cells).</summary>
    internal static class BuildingSync
    {
        public const byte ChPipes = 11;  // host -> one guest

        /// <summary>Guest: the mirror is changing an animation right now.</summary>
        public static bool Applying;

        private static readonly AccessTools.FieldRef<KAnimControllerBase, float> PlaySpeed =
            AccessTools.FieldRefAccess<KAnimControllerBase, float>("playSpeed");

        // ================================================================== animations: host

        private struct Last { public bool known; public int anim; public byte mode; public float speed, elapsed, at, rot; }

        /// <summary>Host: building parts that turn (a missile launcher's cannon): looked at every frame.</summary>
        private static readonly HashSet<long> spinning = new HashSet<long>();
        private static readonly List<KeyValuePair<BRef, int>> spinList = new List<KeyValuePair<BRef, int>>();

        /// <summary>Host: which building (and which of its controllers) an animation controller belongs to.</summary>
        private struct Slot { public BRef r; public int i; }
        private static readonly Dictionary<KAnimControllerBase, Slot> slotOf = new Dictionary<KAnimControllerBase, Slot>();
        private static bool hostLive;

        private sealed class BRef
        {
            public BuildingComplete b;
            public KPrefabID k;
            public readonly List<KBatchedAnimController> kbacs = new List<KBatchedAnimController>(2);
            public readonly List<int> names = new List<int>(2);
            public readonly List<byte> ranks = new List<byte>(2); // which one of the children with this name
            public Last[] last = new Last[0];
            public int passesUntilRefresh;
        }

        private static readonly Dictionary<BuildingComplete, BRef> refs = new Dictionary<BuildingComplete, BRef>();
        private static readonly List<KBatchedAnimController> scratch = new List<KBatchedAnimController>(8);
        private static readonly MemoryStream buf = new MemoryStream();
        private static readonly BinaryWriter bufW = new BinaryWriter(buf);
        private static int records, cursor;
        private const int Slices = 16;
        public static long HostRecords, HostBytes, EventRecords, ShortAnims;
        private static readonly Dictionary<string, int> shortBy = new Dictionary<string, int>();

        public static void Reset()
        {
            refs.Clear();
            slotOf.Clear();
            spinning.Clear();
            spinList.Clear();
            hostLive = false;
            buf.SetLength(0);
            records = 0;
            cursor = 0;
            guestRefs.Clear();
            timeline.Clear();
            mirrored.Clear();
            pipeGuests.Clear();
            hostPipes = null;
        }

        /// <summary>A guest (re)loaded: send every building's look again.</summary>
        public static void OnGuestJoined()
        {
            foreach (BRef r in refs.Values) r.last = new Last[r.kbacs.Count];
        }

        private static void Refresh(BuildingComplete b, BRef r)
        {
            scratch.Clear();
            b.GetComponentsInChildren(true, scratch); // into a reused list: no garbage
            bool same = scratch.Count == r.kbacs.Count;
            for (int i = 0; same && i < scratch.Count; i++) same = scratch[i] == r.kbacs[i];
            if (same) return;
            foreach (KBatchedAnimController old in r.kbacs) if (!ReferenceEquals(old, null)) slotOf.Remove(old);
            r.kbacs.Clear(); r.names.Clear(); r.ranks.Clear();
            foreach (KBatchedAnimController c in scratch)
            {
                int h = c.gameObject.name.GetHashCode();
                byte rank = 0;
                foreach (int prev in r.names) if (prev == h) rank++;
                slotOf[c] = new Slot { r = r, i = r.kbacs.Count };
                r.kbacs.Add(c); r.names.Add(h); r.ranks.Add(rank);
            }
            r.last = new Last[r.kbacs.Count];
        }

        /// <summary>Every host frame while a guest is in the game: a slice of the buildings (a full pass every ~16 frames),
        /// for buildings new to the list and for paused meters; animation starts are recorded as they happen.</summary>
        public static void HostTick(float now)
        {
            hostLive = true;
            float stamp = stampOverride >= 0f ? stampOverride : Puppets.Clock;
            for (int s = spinList.Count - 1; s >= 0; s--)
            {
                var kv = spinList[s];
                if (kv.Key.b == null || kv.Value >= kv.Key.kbacs.Count) { spinning.Remove(((long)(kv.Key.k != null ? kv.Key.k.InstanceID : 0) << 8) | (uint)kv.Value); spinList.RemoveAt(s); continue; }
                Sample(kv.Key, kv.Value, stamp, false);
            }
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
                    if (!refs.TryGetValue(b, out BRef r))
                    {
                        refs[b] = r = new BRef { b = b, k = b.GetComponent<KPrefabID>() };
                        Refresh(b, r);
                    }
                    else if (--r.passesUntilRefresh <= 0) { Refresh(b, r); r.passesUntilRefresh = 30; }
                    if (r.k == null || r.k.InstanceID < 0 || r.k.InstanceID >= EntitySync.LocalIdBase) continue;
                    for (int i = 0; i < r.kbacs.Count; i++) Sample(r, i, stamp, false);
                }
            }
        }

        /// <summary>Host: a building animation starts (KAnimControllerBase.StartQueuedAnim, the one place every Play and
        /// every queued follow-up goes through).</summary>
        private static void StartQueuedAnimPostfix(KAnimControllerBase __instance)
        {
            if (!hostLive || slotOf.Count == 0 || !slotOf.TryGetValue(__instance, out Slot s) || s.r.k == null) return;
            if (s.r.k.InstanceID < 0 || s.r.k.InstanceID >= EntitySync.LocalIdBase || s.i >= s.r.kbacs.Count || s.r.kbacs[s.i] != __instance) return;
            Sample(s.r, s.i, Puppets.Clock, true);
        }

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(KAnimControllerBase), "StartQueuedAnim"),
                    postfix: new HarmonyMethod(typeof(BuildingSync), nameof(StartQueuedAnimPostfix)));
            }
            catch (System.Exception e) { Log.Warn("building anims: can't patch StartQueuedAnim: " + e.Message); }
        }

        /// <summary>Host, in each puppet message: the records since the last one (they share the timeline).</summary>
        public static void TakeTimeline(BinaryWriter w)
        {
            w.Write(records);
            if (records > 0) w.Write(buf.GetBuffer(), 0, (int)buf.Length);
            HostBytes += buf.Length;
            HostRecords += records;
            buf.SetLength(0);
            records = 0;
        }

        public static bool HasTimeline => records > 0;

        /// <param name="started">an animation just started (always a record, it may be a restart)</param>
        private static void Sample(BRef r, int i, float stamp, bool started)
        {
            KBatchedAnimController c = r.kbacs[i];
            if (c == null || c.CurrentAnim == null) return;
            int anim = c.currentAnim.HashValue;
            byte mode = (byte)c.PlayMode;
            float speed = PlaySpeed(c), elapsed = c.GetElapsedTime();
            if (i >= r.last.Length) return;
            ref Last l = ref r.last[i];
            bool same = l.known && l.anim == anim && l.mode == mode && Mathf.Abs(l.speed - speed) <= 0.01f;
            if (started && same && stamp - l.at < 0.05f) return; // the same start twice in a moment (Play, then its queue)
            float rot = c.Rotation;
            bool turned = !l.known || Mathf.Abs(Mathf.DeltaAngle(l.rot, rot)) > 0.3f;
            if (l.known && turned && spinning.Add(((long)r.k.InstanceID << 8) | (uint)i)) spinList.Add(new KeyValuePair<BRef, int>(r, i));
            bool changed = started || !same;
            if (!changed && mode == (byte)KAnim.PlayMode.Paused)
                changed = Mathf.Abs(l.elapsed - elapsed) > Mathf.Max(0.02f * c.CurrentAnim.totalTime, 0.001f); // a meter moved
            if (!changed && mode == (byte)KAnim.PlayMode.Once && elapsed + 0.05f < l.elapsed) changed = true; // played again
            if (mode != (byte)KAnim.PlayMode.Paused) l.elapsed = elapsed; // keeps "played again" detection current
            bool rotOnly = !changed && turned;
            if (!changed && !turned) return;
            // an animation that ran shorter than a full slice pass: the old way (find changes by sampling) would likely
            // have missed it (a door's "opening" / "closing")
            if (l.known && started && stamp - l.at < Slices * Mathf.Max(Time.unscaledDeltaTime, 0.016f))
            {
                ShortAnims++;
                string n = r.k.PrefabTag.Name;
                shortBy.TryGetValue(n, out int sc);
                if (sc > 0 || shortBy.Count < 40) shortBy[n] = sc + 1;
            }
            if (started) EventRecords++;
            l = new Last { known = true, anim = anim, mode = mode, speed = speed, elapsed = rotOnly ? l.elapsed : elapsed, at = rotOnly ? l.at : stamp, rot = rot };
            bufW.Write(stamp); bufW.Write(r.k.InstanceID); bufW.Write(r.ranks[i]); bufW.Write(r.names[i]);
            bufW.Write(anim); bufW.Write(mode); bufW.Write(speed); bufW.Write(elapsed);
            bufW.Write(rot); bufW.Write(rotOnly);
            records++;
        }

        private static void Prune()
        {
            var dead = new List<BuildingComplete>();
            foreach (var kv in refs) if (kv.Key == null) dead.Add(kv.Key);
            foreach (BuildingComplete b in dead) refs.Remove(b);
            var gone = new List<KAnimControllerBase>();
            foreach (var kv in slotOf) if (kv.Key == null || !refs.ContainsKey(kv.Value.r.b)) gone.Add(kv.Key);
            foreach (KAnimControllerBase c in gone) slotOf.Remove(c);
        }

        // ================================================================== animations: guest

        private sealed class GRef { public readonly List<KBatchedAnimController> kbacs = new List<KBatchedAnimController>(2); }
        private static readonly Dictionary<int, GRef> guestRefs = new Dictionary<int, GRef>();
        private static readonly HashSet<KAnimControllerBase> mirrored = new HashSet<KAnimControllerBase>();
        public static long GuestApplied, GuestUnknown, Blocked;

        /// <summary>Guest: an animation the host drives (local logic may not change it).</summary>
        public static bool IsMirrored(KAnimControllerBase c) => mirrored.Count > 0 && mirrored.Contains(c);

        private struct Pending { public float at, speed, elapsed, rot; public int id, rank, name, anim; public byte mode; public bool rotOnly; }
        private static readonly Queue<Pending> timeline = new Queue<Pending>();
        public static long GuestLate;
        public static float GuestAheadSum, GuestLateMax;
        private static long guestAheadCount;

        /// <summary>Guest, inside a puppet message: building animation starts, each at its moment of the host's timeline.</summary>
        public static void ReadTimeline(BinaryReader r)
        {
            int n = r.ReadInt32();
            float rt = Puppets.RenderTime;
            for (int i = 0; i < n; i++)
            {
                var p = new Pending
                {
                    at = r.ReadSingle(), id = r.ReadInt32(), rank = r.ReadByte(), name = r.ReadInt32(), anim = r.ReadInt32(),
                    mode = r.ReadByte(), speed = r.ReadSingle(), elapsed = r.ReadSingle(), rot = r.ReadSingle(), rotOnly = r.ReadBoolean(),
                };
                if (rt >= 0f)
                {
                    // how much earlier than the duplicants it would have shown if played on arrival (the old way);
                    // negative: it came after its moment had already been shown
                    float ahead = p.at - rt;
                    GuestAheadSum += ahead; guestAheadCount++;
                    if (ahead < 0f) { GuestLate++; GuestLateMax = Mathf.Max(GuestLateMax, -ahead); }
                }
                timeline.Enqueue(p);
            }
            if (rt < 0f) ApplyTimeline(float.PositiveInfinity); // no timeline yet: as they come
        }

        public static float GuestAheadMs => guestAheadCount > 0 ? GuestAheadSum / guestAheadCount * 1000f : 0f;

        /// <summary>Guest, every frame once the puppets are placed: play what the timeline reached.</summary>
        public static void ApplyTimeline(float renderTime)
        {
            if (timeline.Count == 0) return;
            Applying = true;
            try
            {
                while (timeline.Count > 0)
                {
                    Pending p = timeline.Peek();
                    // (far ahead of the timeline: the host's clock started over, a new server: play it now)
                    if (p.at > renderTime && p.at < renderTime + 10f) break;
                    timeline.Dequeue();
                    KBatchedAnimController c = Resolve(p.id, p.rank, p.name);
                    if (c == null) { GuestUnknown++; continue; }
                    mirrored.Add(c);
                    if (Mathf.Abs(Mathf.DeltaAngle(c.Rotation, p.rot)) > 0.01f) c.Rotation = p.rot;
                    if (!p.rotOnly) Apply(c, p.anim, p.mode, p.speed, p.elapsed);
                    GuestApplied++;
                }
            }
            finally { Applying = false; }
        }

        /// <summary>The building's child controller with this name, the rank-th of those (children are made in the same
        /// order on both sides, but other children may sit between them).</summary>
        private static KBatchedAnimController Resolve(int id, int rank, int name)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt == 1 || !guestRefs.TryGetValue(id, out GRef g))
                {
                    KPrefabID k = KPrefabIDTracker.Get().GetInstance(id);
                    if (k == null) { guestRefs.Remove(id); return null; }
                    guestRefs[id] = g = new GRef();
                    k.GetComponentsInChildren(true, g.kbacs);
                }
                int seen = 0;
                foreach (KBatchedAnimController c in g.kbacs)
                {
                    if (c == null) break; // a child went away: list it again
                    if (c.gameObject.name.GetHashCode() != name) continue;
                    if (seen++ == rank) return c;
                }
            }
            return null;
        }

        private static void Apply(KBatchedAnimController c, int anim, byte mode, float speed, float elapsed)
        {
            mirrored.Add(c);
            var m = (KAnim.PlayMode)mode;
            bool sameAnim = c.CurrentAnim != null && c.currentAnim.HashValue == anim && c.PlayMode == m;
            if (!sameAnim || m != KAnim.PlayMode.Paused)
            {
                c.PlaySpeedMultiplier = 1f; // the host's speed already includes its multiplier
                c.Play(new HashedString(anim), m, speed > 0f ? speed : 1f, elapsed);
            }
            if (c.CurrentAnim != null)
            {
                if (m == KAnim.PlayMode.Paused && c.CurrentAnim.numFrames > 0)
                    c.SetPositionPercent(elapsed * c.CurrentAnim.frameRate / c.CurrentAnim.numFrames);
                else c.SetElapsedTime(elapsed);
            }
        }

        // ================================================================== pipes

        private const float PipeInterval = 0.5f;

        private struct PipeCell { public int el; public float mass, temp; }

        private sealed class PipeGuest
        {
            public readonly Dictionary<int, PipeCell> gas = new Dictionary<int, PipeCell>(), liquid = new Dictionary<int, PipeCell>();
            public float next;
        }

        private static readonly Dictionary<ushort, PipeGuest> pipeGuests = new Dictionary<ushort, PipeGuest>();
        public static long HostPipeCells, GuestPipeCells, GuestPipePinned;

        public static void ResetGuest(ushort id) => pipeGuests.Remove(id);

        private static bool PipeDiffers(PipeCell a, PipeCell b) =>
            a.el != b.el || Mathf.Abs(a.mass - b.mass) > Mathf.Max(0.02f * Mathf.Max(a.mass, b.mass), 0.0005f) || Mathf.Abs(a.temp - b.temp) > 1f;

        /// <summary>Host, per guest: the gas and liquid in the pipes that guest can see.</summary>
        public static void HostPipes(float now, ushort guestId, PresenceData view)
        {
            if (view == null || (view.flags & PresenceData.FlagInGame) == 0 || Game.Instance == null) return;
            if (!pipeGuests.TryGetValue(guestId, out PipeGuest g)) pipeGuests[guestId] = g = new PipeGuest();
            if (now < g.next) return;
            g.next = now + PipeInterval;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(view.viewMinX) - 2), y0 = Mathf.Max(0, Mathf.FloorToInt(view.viewMinY) - 2);
            int x1 = Mathf.Min(Grid.WidthInCells - 1, Mathf.CeilToInt(view.viewMaxX) + 2), y1 = Mathf.Min(Grid.HeightInCells - 1, Mathf.CeilToInt(view.viewMaxY) + 2);
            var ms = new MemoryStream();
            int count = 0;
            using (var z = new DeflateStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(z))
            {
                count += WriteFlow(w, Game.Instance.gasConduitFlow, 0, g.gas, x0, y0, x1, y1);
                count += WriteFlow(w, Game.Instance.liquidConduitFlow, 1, g.liquid, x0, y0, x1, y1);
            }
            if (count == 0) return;
            HostPipeCells += count;
            CommandBus.Client.SendRelay(guestId, ChPipes, ms.ToArray());
        }

        private static int WriteFlow(BinaryWriter w, ConduitFlow flow, byte which, Dictionary<int, PipeCell> sent, int x0, int y0, int x1, int y1)
        {
            int count = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int c = Grid.XYToCell(x, y);
                    if (!flow.HasConduit(c)) continue;
                    ConduitFlow.ConduitContents cc = flow.GetContents(c);
                    var p = new PipeCell { el = (int)cc.element, mass = cc.mass, temp = cc.temperature };
                    if (sent.TryGetValue(c, out PipeCell old) && !PipeDiffers(old, p)) continue;
                    sent[c] = p;
                    w.Write(which); w.Write(c); w.Write(p.el); w.Write(p.mass); w.Write(p.temp); w.Write(cc.diseaseIdx); w.Write(cc.diseaseCount);
                    count++;
                }
            return count;
        }

        private static Dictionary<int, ConduitFlow.ConduitContents>[] hostPipes;
        private static float nextPin;

        public static void OnPipes(byte[] data)
        {
            if (!Puppets.Mirroring || Game.Instance == null) return;
            if (hostPipes == null) hostPipes = new[] { new Dictionary<int, ConduitFlow.ConduitContents>(), new Dictionary<int, ConduitFlow.ConduitContents>() };
            using (var r = Inflate.Open(data)) // (the shared reader: no garbage per message; using is harmless)
            {
                while (true)
                {
                    int which;
                    if (!Inflate.More(r)) break;
                    which = r.ReadByte();
                    int c = r.ReadInt32(), el = r.ReadInt32();
                    float mass = r.ReadSingle(), temp = r.ReadSingle();
                    byte d = r.ReadByte(); int dc = r.ReadInt32();
                    var cc = new ConduitFlow.ConduitContents((SimHashes)el, mass, temp, d, dc);
                    hostPipes[which & 1][c] = cc;
                    ConduitFlow flow = which == 0 ? Game.Instance.gasConduitFlow : Game.Instance.liquidConduitFlow;
                    if (flow.HasConduit(c)) flow.SetContents(c, cc);
                    GuestPipeCells++;
                }
            }
        }

        /// <summary>Guest: its own pipe flow moves the contents around between updates; put the visible ones back.</summary>
        public static void GuestTick(float now)
        {
            if (hostPipes == null || now < nextPin || Game.Instance == null || Camera.main == null) return;
            nextPin = now + PipeInterval;
            Camera cam = Camera.main;
            float z = cam.transform.position.z;
            Vector3 lo = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z)), hi = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
            int x0 = Mathf.Max(0, Mathf.FloorToInt(lo.x)), y0 = Mathf.Max(0, Mathf.FloorToInt(lo.y));
            int x1 = Mathf.Min(Grid.WidthInCells - 1, Mathf.CeilToInt(hi.x)), y1 = Mathf.Min(Grid.HeightInCells - 1, Mathf.CeilToInt(hi.y));
            for (int which = 0; which < 2; which++)
            {
                ConduitFlow flow = which == 0 ? Game.Instance.gasConduitFlow : Game.Instance.liquidConduitFlow;
                foreach (var kv in hostPipes[which])
                {
                    Grid.CellToXY(kv.Key, out int x, out int y);
                    if (x < x0 || x > x1 || y < y0 || y > y1 || !flow.HasConduit(kv.Key)) continue;
                    ConduitFlow.ConduitContents now2 = flow.GetContents(kv.Key);
                    if (now2.element == kv.Value.element && Mathf.Abs(now2.mass - kv.Value.mass) <= Mathf.Max(0.02f * kv.Value.mass, 0.0005f)) continue;
                    flow.SetContents(kv.Key, kv.Value);
                    GuestPipePinned++;
                }
            }
        }

        /// <summary>Test sync points: sample every building now and send at once (not the next slice in 0.25 s).</summary>
        /// <param name="at">the moment of the test signal: the records count as that moment (a guest compares when its
        /// timeline shows it)</param>
        public static void FlushNow(float at)
        {
            if (Components.BuildingCompletes.Count == 0) return;
            stampOverride = at;
            try { for (int i = 0; i < Slices + 1; i++) HostTick(at); }
            finally { stampOverride = -1f; }
        }

        private static float stampOverride = -1f;

        /// <summary>For the sync test: every building's animations (and meters) (anims: taken when the guest's timeline
        /// shows the test moment), or the pipes in view and who wears what (taken at the signal).</summary>
        public static string Dump(bool anims)
        {
            var sb = new System.Text.StringBuilder();
            var list = new List<KBatchedAnimController>();
            if (anims) foreach (BuildingComplete b in Components.BuildingCompletes.Items)
            {
                if (b == null) continue;
                var k = b.GetComponent<KPrefabID>();
                if (k == null) continue;
                list.Clear();
                b.GetComponentsInChildren(true, list);
                var seenNames = new Dictionary<string, int>();
                for (int i = 0; i < list.Count; i++)
                {
                    KBatchedAnimController c = list[i];
                    if (c == null) continue;
                    seenNames.TryGetValue(c.gameObject.name, out int rank);
                    seenNames[c.gameObject.name] = rank + 1;
                    if (c.CurrentAnim == null) continue;
                    float pct = c.PlayMode == KAnim.PlayMode.Paused && c.CurrentAnim.totalTime > 0 ? c.GetElapsedTime() / c.CurrentAnim.totalTime : -1f;
                    sb.Append("A ").Append(k.InstanceID).Append(' ').Append(k.PrefabTag.Name).Append(' ').Append(c.gameObject.name.Replace(' ', '_')).Append('#').Append(rank).Append(' ')
                      .Append(c.CurrentAnim.name).Append(' ').Append(c.PlayMode).Append(' ').Append(pct.ToString("F3")).Append('\n');
                }
            }
            if (anims) return sb.ToString();
            Camera cam = Camera.main;
            if (cam != null && Game.Instance != null)
            {
                float z = cam.transform.position.z;
                Vector3 lo = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z)), hi = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
                for (int which = 0; which < 2; which++)
                {
                    ConduitFlow flow = which == 0 ? Game.Instance.gasConduitFlow : Game.Instance.liquidConduitFlow;
                    for (int y = Mathf.Max(0, (int)lo.y); y <= Mathf.Min(Grid.HeightInCells - 1, (int)hi.y); y++)
                        for (int x = Mathf.Max(0, (int)lo.x); x <= Mathf.Min(Grid.WidthInCells - 1, (int)hi.x); x++)
                        {
                            int c = Grid.XYToCell(x, y);
                            if (!flow.HasConduit(c)) continue;
                            ConduitFlow.ConduitContents cc = flow.GetContents(c);
                            sb.Append("P ").Append(which).Append(' ').Append(c).Append(' ').Append(cc.element).Append(' ').Append(cc.mass.ToString("G5")).Append('\n');
                        }
                }
            }
            SolidConduitFlow rails = Game.Instance != null ? Game.Instance.solidConduitFlow : null;
            if (rails != null)
            {
                SolidConduitFlow.SOAInfo soa = rails.GetSOAInfo();
                for (int i = 0; i < soa.NumEntries; i++)
                {
                    int cell = soa.GetCell(i);
                    Pickupable item = rails.GetPickupable(rails.GetContents(cell).pickupableHandle);
                    var ik = item != null ? item.GetComponent<KPrefabID>() : null;
                    sb.Append("R ").Append(cell).Append(' ').Append(ik != null ? ik.InstanceID : -1).Append('\n');
                }
            }
            foreach (MinionIdentity m in Components.LiveMinionIdentities.Items)
            {
                if (m == null) continue;
                Equipment eq = m.GetEquipment();
                if (eq == null) continue;
                foreach (AssignableSlotInstance slot in eq.Slots)
                    if (slot.IsAssigned() && slot.assignable != null)
                        sb.Append("E ").Append(m.GetComponent<KPrefabID>().InstanceID).Append(' ').Append(slot.assignable.GetComponent<KPrefabID>().InstanceID)
                          .Append(' ').Append(slot.assignable.PrefabID().Name).Append('\n');
            }
            return sb.ToString();
        }

        private static string ShortSummary()
        {
            if (shortBy.Count == 0) return "";
            var l = new List<KeyValuePair<string, int>>(shortBy);
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder(": ");
            for (int i = 0; i < l.Count && i < 5; i++) sb.Append(i > 0 ? ", " : "").Append(l[i].Key).Append(' ').Append(l[i].Value);
            return sb.ToString();
        }

        public static string Report() =>
            $"building anims: host sent {HostRecords} ({HostBytes / 1024} KB raw; {EventRecords} at the moment they started, {ShortAnims} shorter than a sampling pass{ShortSummary()}), " +
            $"guest applied {GuestApplied} (unknown {GuestUnknown}, blocked local {Blocked}), arrived on average {GuestAheadMs:F0} ms before their moment, {GuestLate} after it (at most {GuestLateMax * 1000f:F0} ms); " +
            $"pipes: host sent {HostPipeCells} cells, guest got {GuestPipeCells}, pinned {GuestPipePinned}";
    }

    /// <summary>Guest: a building meter the host drives isn't moved by local logic.</summary>
    [HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.SetPositionPercent))]
    internal static class KAnim_SetPositionPercent_Patch
    {
        private static bool Prefix(KAnimControllerBase __instance)
        {
            if (!Puppets.Mirroring || BuildingSync.Applying || !BuildingSync.IsMirrored(__instance)) return true;
            BuildingSync.Blocked++;
            return false;
        }
    }
}

namespace OniCoop
{
    using HarmonyLib;

    /// <summary>Guest: a building part the host turns (a missile launcher's cannon) isn't turned by local logic.</summary>
    [HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.Rotation), MethodType.Setter)]
    internal static class KAnim_SetRotation_Patch
    {
        private static bool Prefix(KAnimControllerBase __instance)
        {
            if (!Puppets.Mirroring || BuildingSync.Applying || Puppets.Applying || Meteors.Applying || !BuildingSync.IsMirrored(__instance)) return true;
            BuildingSync.Blocked++;
            return false;
        }
    }
}
