namespace OniCoop
{
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Roadmap step 3a: duplicants and critters are decided on the host only; on a guest their AI and own
    /// movement are off and they replay the host's timeline. Entities are matched by KPrefabID.InstanceID, which the
    /// guest got from the host's save.
    ///
    /// Host: every frame it looks at each of them and writes a keyframe (time, position, animation, facing, nav type)
    /// when something visible changed: a new animation, a flip, a nav type (wall / ceiling critters), an instant step
    /// (the end of a jump or climb, where the game moves the transform at once after the animation already showed the
    /// motion), or, while walking, every 0.1 s. Keyframes go out 20 times a second, stamped with the host's clock.
    /// Guest: plays that timeline a little behind (one-way delay + jitter + 0.18 s, measured), interpolating walks and
    /// stepping where the host stepped, and switching animations at the moment the host did. While mirroring, nothing
    /// else on the guest may move these entities, play their animations or turn them around.</summary>
    internal static class Puppets
    {
        public const byte ChPuppets = 5;
        private const int TimelineMark = int.MinValue; // (no entity has this id) what follows: a section of other records
        private const byte SectionBuildings = 1, SectionFeedback = 2;
        private const float SampleInterval = 0.1f;   // a walking entity's position is sampled this often
        private const float SendInterval = 0.1f;     // pending keyframes go out this often (each message costs the guest
                                                     // a decompressor, ~7 KB of garbage: 20/s made it collect every 15 s)
        private const float RefreshAll = 2f;         // and everything once in a while
        private const float JumpDist = 0.75f;        // moved more than this in one frame: an instant step, not a walk
        private const float TeleportDist = 5f;

        private const byte FFlipX = 1, FFlipY = 2, FStep = 4, FRestart = 8, FXform = 16, FOverrides = 32, FLooks = 64;
        private const byte LookFlags = FFlipX | FFlipY;
        private const byte NoNav = 255;

        private static readonly AccessTools.FieldRef<KAnimControllerBase, float> PlaySpeed =
            AccessTools.FieldRefAccess<KAnimControllerBase, float>("playSpeed");

        /// <summary>Guest: the host drives duplicants and critters (set while a guest is in a game).</summary>
        public static bool Mirroring;

        /// <summary>The timeline's clock on both sides: wall time. (Unity's frame clock, Time.unscaledTime, was seen
        /// running up to 38 s behind on a guest for a while after it loaded the world.)</summary>
        public static float Clock => Time.realtimeSinceStartup;

        /// <summary>The mirror itself is changing a puppet (the blocking patches let it through).</summary>
        public static bool Applying;

        private struct Kf
        {
            public float time;
            public Vector3 pos;
            public int anim;
            public byte mode, flags, nav;
            public float speed, elapsed, rot;
            public Vector2 off;
            public List<KeyValuePair<string, float>> overrides;
            public PuppetLooks.Delta looks;  // tool, clothes, face, work target... (PuppetLooks)
            public int seq;

            public bool SameLook(Kf o) =>
                anim == o.anim && mode == o.mode && (flags & LookFlags) == (o.flags & LookFlags) && nav == o.nav
                && Mathf.Abs(speed - o.speed) < 0.01f && Mathf.Abs(rot - o.rot) < 0.01f && (off - o.off).sqrMagnitude < 1e-6f;
        }

        // ------------------------------------------------------------------ host

        private sealed class HostEnt
        {
            public int id;
            public KBatchedAnimController kbac;
            public Navigator nav;
            public bool init;
            public Kf last;                       // what the last keyframe said (without the one-off flags)
            public float lastKfTime, lastSeen;
            public Vector3 framePos;              // previous frame
            public float frameTime, frameElapsed;
            public int frameAnim, overridesHash;
            public PuppetLooks.HostLook look;
            public readonly List<Kf> pending = new List<Kf>(4);
        }

        private static readonly Dictionary<Brain, HostEnt> hostEnts = new Dictionary<Brain, HostEnt>();
        private static float nextFlush, nextFull;
        public static long HostKeyframes, HostBytes, HostMessages;

        /// <summary>Work animations ("working_loop" at a desk, digging, ...) live in extra anim files that the
        /// building or task adds to the duplicant: those have to be mirrored too, or the guest can't play them.</summary>
        private static int OverrideHash(KBatchedAnimController kbac)
        {
            // every frame for every puppet: no foreach (the list is an interface: a boxed enumerator) and no .name (a new
            // string from the engine each time)
            int h = 17;
            IReadOnlyList<KAnimControllerBase.OverrideAnimFileData> list = kbac.OverrideAnimFiles;
            for (int i = 0; i < list.Count; i++)
            {
                KAnimControllerBase.OverrideAnimFileData o = list[i];
                if (o.file != null) h = h * 31 + o.file.GetInstanceID() * 7 + (int)(o.priority * 100);
            }
            return h;
        }

        private static List<KeyValuePair<string, float>> Overrides(KBatchedAnimController kbac)
        {
            var list = new List<KeyValuePair<string, float>>();
            foreach (var o in kbac.OverrideAnimFiles)
                if (o.file != null) list.Add(new KeyValuePair<string, float>(o.file.name, o.priority));
            return list;
        }

        private static Kf Sample(KBatchedAnimController kbac, Navigator nav, Vector3 p, float now)
        {
            Vector3 o = kbac.Offset;
            var k = new Kf
            {
                time = now, pos = p, anim = kbac.currentAnim.HashValue, mode = (byte)kbac.PlayMode,
                speed = PlaySpeed(kbac), elapsed = kbac.GetElapsedTime(), rot = kbac.Rotation, off = new Vector2(o.x, o.y),
                nav = nav != null ? (byte)nav.CurrentNavType : NoNav,
            };
            if (kbac.FlipX) k.flags |= FFlipX;
            if (kbac.FlipY) k.flags |= FFlipY;
            return k;
        }

        /// <summary>Every frame on the host.</summary>
        public static void HostTick()
        {
            float now = Clock;
            ProbeHostFrame(now);
            NoteDeadTime(now);
            bool full = now >= nextFull;
            if (full) nextFull = now + RefreshAll;
            foreach (Brain b in Components.Brains.Items)
            {
                if (b == null) continue;
                if (!hostEnts.TryGetValue(b, out HostEnt e))
                {
                    var kpid = b.GetComponent<KPrefabID>();
                    var kbac = b.GetComponent<KBatchedAnimController>();
                    if (kpid == null || kbac == null) continue;
                    hostEnts[b] = e = new HostEnt { id = kpid.InstanceID, kbac = kbac, nav = b.GetComponent<Navigator>(), look = PuppetLooks.NewHostLook(b, kbac) };
                }
                if (e.kbac == null) continue;
                e.lastSeen = now;
                Vector3 p = b.transform.GetPosition();
                Kf s = Sample(e.kbac, e.nav, p, now);
                int oh = OverrideHash(e.kbac);
                bool first = !e.init;
                // an instant step is the game snapping the transform at the end of an animation-driven transition
                // (climbing onto a ledge, jumping...); a move that only looks big because the frame was long is a walk.
                // Besides those: a real teleport (more than 2 cells, faster than anything walks or rides)
                bool snapped = e.nav != null && snappedNavs.Remove(e.nav);
                float moved2 = ((Vector2)(p - e.framePos)).sqrMagnitude;
                bool teleport = moved2 > 4f && Mathf.Sqrt(moved2) / Mathf.Max(Time.deltaTime, 1e-3f) > 80f;
                bool jump = !first && (snapped || teleport);
                bool restart = !first && s.anim == e.frameAnim && s.elapsed + 0.02f < e.frameElapsed;
                bool look = first || !s.SameLook(e.last);
                bool ovr = first || oh != e.overridesHash;
                bool moved = !first && ((Vector2)(p - e.last.pos)).sqrMagnitude > 1e-4f;
                PuppetLooks.Delta looks = PuppetLooks.HostSample(e.look, full || first);
                bool looksChanged = PuppetLooks.HasChange(looks);
                bool change = look || jump || restart || ovr || looksChanged || (moved && now - e.lastKfTime >= SampleInterval);
                if (change || full)
                {
                    // "it stayed there (or kept walking) until the previous frame", so the guest neither starts
                    // sliding early nor slides across a step
                    if (change && !first && e.frameTime > e.lastKfTime + 0.001f && (jump || now - e.lastKfTime > SampleInterval * 1.5f))
                    {
                        Kf hold = e.last;
                        hold.time = e.frameTime;
                        hold.pos = e.framePos;
                        e.pending.Add(hold);
                    }
                    Kf k = s;
                    if (jump) ProbeStep(p, e.framePos, e.nav);
                    if (jump) k.flags |= FStep;
                    if (restart) k.flags |= FRestart;
                    if (ovr || full) { k.flags |= FOverrides; k.overrides = Overrides(e.kbac); }
                    if (looks != null) { k.flags |= FLooks; k.looks = looks; }
                    e.pending.Add(k);
                    e.last = s;
                    e.lastKfTime = now;
                    e.overridesHash = oh;
                }
                e.init = true;
                e.framePos = p;
                e.frameTime = now;
                e.frameAnim = s.anim;
                e.frameElapsed = s.elapsed;
            }
            snappedNavs.Clear(); // (navigators without a brain)
            if (full)
            {
                var gone = new List<Brain>();
                foreach (var kv in hostEnts) if (kv.Key == null || kv.Value.lastSeen < now - 5f) gone.Add(kv.Key);
                foreach (Brain b in gone) hostEnts.Remove(b);
            }
            if (now >= nextFlush)
            {
                nextFlush = now + SendInterval;
                Flush(now);
            }
        }

        // ---- the game's own instant steps (host)

        private static readonly HashSet<Navigator> snappedNavs = new HashSet<Navigator>();
        private static readonly AccessTools.FieldRef<TransitionDriver, Navigator> DriverNav =
            AccessTools.FieldRefAccess<TransitionDriver, Navigator>("navigator");
        private static readonly AccessTools.FieldRef<TransitionDriver, Navigator.ActiveTransition> DriverTransition =
            AccessTools.FieldRefAccess<TransitionDriver, Navigator.ActiveTransition>("transition");

        private struct Snap { public Navigator nav; public Vector3 pos; }

        private static void UpdateTransitionPrefix(TransitionDriver __instance, out Snap __state)
        {
            __state = default;
            if (!CommandBus.Active || !CommandBus.IsHost) return;
            Navigator.ActiveTransition tr = DriverTransition(__instance);
            if (tr == null || tr.isLooping) return; // a looping one (walking) moves the transform smoothly itself
            Navigator nav = DriverNav(__instance);
            if (nav == null) return;
            __state.nav = nav;
            __state.pos = nav.transform.GetPosition();
        }

        private static void UpdateTransitionPostfix(Snap __state)
        {
            if (__state.nav == null) return;
            if (((Vector2)(__state.nav.transform.GetPosition() - __state.pos)).sqrMagnitude > 1e-6f) snappedNavs.Add(__state.nav);
        }

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(TransitionDriver), nameof(TransitionDriver.UpdateTransition)),
                    prefix: new HarmonyMethod(typeof(Puppets), nameof(UpdateTransitionPrefix)),
                    postfix: new HarmonyMethod(typeof(Puppets), nameof(UpdateTransitionPostfix)));
            }
            catch (System.Exception e) { Log.Warn("puppets: can't patch TransitionDriver.UpdateTransition: " + e.Message); }
        }

        private static void Flush(float now)
        {
            var ms = new MemoryStream();
            int n = 0;
            using (var z = new DeflateStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(z))
            {
                w.Write(now);
                w.Write((byte)Mathf.Min(deadTimes.Count, 255));
                for (int i = 0; i < deadTimes.Count && i < 255; i++) { w.Write(now - deadTimes[i].x); w.Write(now - deadTimes[i].y); }
                deadTimes.Clear();
                foreach (HostEnt e in hostEnts.Values)
                {
                    for (int start = 0; start < e.pending.Count; start += 255)
                    {
                        int count = Mathf.Min(255, e.pending.Count - start);
                        w.Write(e.id);
                        w.Write((byte)count);
                        for (int i = start; i < start + count; i++) Write(w, e.pending[i], now);
                        n += count;
                    }
                    e.pending.Clear();
                }
                // what else happened on the same timeline since the last message: building animations (BuildingSync),
                // progress bars and floating texts (Feedback)
                if (BuildingSync.HasTimeline) { w.Write(TimelineMark); w.Write(SectionBuildings); BuildingSync.TakeTimeline(w); }
                if (Feedback.HasTimeline) { w.Write(TimelineMark); w.Write(SectionFeedback); Feedback.TakeTimeline(w); }
            }
            // sent even when empty: a guest knows the host's timeline reached "now" (nothing moved), so it can tell
            // a quiet moment from a stalled stream
            byte[] payload = ms.ToArray();
            HostKeyframes += n;
            HostBytes += payload.Length;
            HostMessages++;
            CommandBus.Client.SendRelay(Proto.TargetAll, ChPuppets, payload);
        }

        private static void Write(BinaryWriter w, Kf k, float now)
        {
            byte f = k.flags;
            bool xf = k.rot != 0f || k.off != Vector2.zero;
            if (xf) f |= FXform;
            w.Write(now - k.time);
            w.Write(k.pos.x); w.Write(k.pos.y); w.Write(k.pos.z);
            w.Write(k.anim); w.Write(k.mode); w.Write(f); w.Write(k.nav);
            w.Write(k.speed); w.Write(k.elapsed);
            if (xf) { w.Write(k.rot); w.Write(k.off.x); w.Write(k.off.y); }
            if ((f & FOverrides) != 0)
            {
                w.Write((byte)k.overrides.Count);
                foreach (var kv in k.overrides) { w.Write(kv.Key); w.Write(kv.Value); }
            }
            if ((f & FLooks) != 0) PuppetLooks.Write(w, k.looks);
        }

        private static Kf Read(BinaryReader r, float sentAt)
        {
            var k = new Kf { time = sentAt - r.ReadSingle() };
            k.pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            k.anim = r.ReadInt32(); k.mode = r.ReadByte(); k.flags = r.ReadByte(); k.nav = r.ReadByte();
            k.speed = r.ReadSingle(); k.elapsed = r.ReadSingle();
            if ((k.flags & FXform) != 0) { k.rot = r.ReadSingle(); k.off = new Vector2(r.ReadSingle(), r.ReadSingle()); }
            if ((k.flags & FOverrides) != 0)
            {
                int n = r.ReadByte();
                k.overrides = new List<KeyValuePair<string, float>>(n);
                for (int i = 0; i < n; i++) k.overrides.Add(new KeyValuePair<string, float>(Inflate.ReadId(r), r.ReadSingle()));
            }
            if ((k.flags & FLooks) != 0) k.looks = PuppetLooks.Read(r);
            return k;
        }

        // ------------------------------------------------------------------ guest

        private sealed class Track
        {
            public int id;
            public Brain brain;
            public KBatchedAnimController kbac;
            public Navigator nav;
            public Facing facing;
            public Transform tf;
            public readonly List<Kf> kfs = new List<Kf>(8);
            public int appliedSeq = -1;
            public bool hasLook;
            public Kf look;
            public List<KeyValuePair<string, float>> pendingOverrides;
            public Vector3 lastSet;
            public bool hasSet;
            public readonly PuppetLooks.GuestLook looks = new PuppetLooks.GuestLook();
            // probe
            public Kf dropped;      // the last keyframe already passed
            public bool hasDropped, starved, stepPassed;
        }

        private static readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        private static readonly Dictionary<int, Brain> brains = new Dictionary<int, Brain>();
        // what only the mirror may touch while mirroring
        private static readonly HashSet<KAnimControllerBase> puppetAnims = new HashSet<KAnimControllerBase>();
        private static readonly HashSet<Transform> puppetTransforms = new HashSet<Transform>();
        private static readonly HashSet<Facing> puppetFacings = new HashSet<Facing>();
        private static readonly HashSet<Navigator> puppetNavs = new HashSet<Navigator>();
        private static float nextIndex, nextReport;
        private static bool choresStopped;
        private static int seqCounter;

        public static long GuestKeyframes, GuestUnknown, LateKeyframes, AnimPlays, ExternalMoves, BlockedCalls, Stashed;
        public static int MissingAnimFiles;
        public static int Tracked => tracks.Count;

        // clock: guest time - host time of each message (one-way delay + clock offset), last 10 s
        private const int LagWindow = 600; // messages (10 a second): the last minute
        private static readonly float[] lagSamples = new float[LagWindow], lagSorted = new float[LagWindow];
        private static int lagCount, lagNext, lagObserved;
        private static bool lagInit;
        private static float lag, lagTarget, lastGuestTick, latestSent;
        private static readonly List<Vector2> guestDead = new List<Vector2>(); // host time spans the host didn't run
        private static readonly List<Vector2> deadHistory = new List<Vector2>(); // the same, kept a while for interpolation

        /// <summary>Host time between t0 and t1 that the host actually ran (dead spans taken out). A walk across a stall
        /// (a save) took place in the frame after it, so it is played over that live time, not mostly skipped.</summary>
        private static float Live(float t0, float t1)
        {
            float d = t1 - t0;
            for (int i = 0; i < deadHistory.Count; i++)
            {
                Vector2 x = deadHistory[i];
                float o = Mathf.Min(t1, x.y) - Mathf.Max(t0, x.x);
                if (o > 0f) d -= o;
            }
            return d > 0f ? d : 0f;
        }

        /// <summary>Dead host time still ahead of the render time (it will be jumped over).</summary>
        private static float DeadAhead()
        {
            float rt = Clock - lag, sum = 0f;
            foreach (Vector2 d in guestDead) if (d.y > rt) sum += d.y - Mathf.Max(d.x, rt);
            return sum;
        }
        public static float SkippedDead;

        /// <summary>A keyframe can be this much older than the message that carries it (made at most one sample
        /// interval plus a host frame before the flush), so the host's timeline is known up to the newest message
        /// time minus this.</summary>
        private const float KnownMargin = 0.15f;
        private const float MaxSpread = 0.8f;

        /// <summary>Guest: the server said it is saving; collect garbage the next time the timeline is waiting for
        /// data anyway (then nothing on screen moves, so the pause doesn't show).</summary>
        public static bool CollectWhenWaiting;
        public static float JitterP95, RenderLagMs;

        /// <summary>How far behind the fastest recent message the guest plays (jitter allowance + sampling).</summary>
        public static float BufferMs => lagCount > 0 ? (lag - lagSorted[0]) * 1000f : 0f;

        /// <summary>Guest: the host time the puppets currently show (-1 before the first update).</summary>
        public static float RenderTime = -1f;

        /// <summary>Guest: the host time of the newest puppet message received.</summary>
        public static float LatestSent => latestSent;

        public static bool TimelineReady => lagInit && RenderTime >= 0f;

        /// <summary>Test only: the timeline may not pass this host time (a sync point the guest hasn't captured yet,
        /// so a slow frame just before it can't carry the timeline past it). -1: no hold.</summary>
        public static float TestHoldAt = -1f;

        // keyframes of an entity this guest doesn't have yet (its spawn record comes on the timeline too, so it can be
        // a little behind): kept a few seconds and handed over when it appears
        private static readonly Dictionary<int, List<Kf>> stash = new Dictionary<int, List<Kf>>();
        private static float nextStashPrune;

        public static void Reset()
        {
            hostEnts.Clear();
            foreach (Track t in tracks.Values) PuppetLooks.DropHit(t.looks);
            tracks.Clear();
            PuppetLooks.Reset();
            brains.Clear();
            puppetAnims.Clear(); puppetTransforms.Clear(); puppetFacings.Clear(); puppetNavs.Clear();
            choresStopped = false;
            nextIndex = 0;
            lagCount = lagNext = lagObserved = 0;
            lagInit = false;
            latestSent = 0f;
            guestDead.Clear();
            deadHistory.Clear();
            stash.Clear();
            RenderTime = -1f;
            TestHoldAt = -1f;
            blockedBy.Clear();
        }

        private static void Index(float now)
        {
            if (now < nextIndex) return;
            nextIndex = now + 1f;
            // objects come and go now (EntitySync): forget the destroyed ones
            var dead = new List<int>();
            foreach (var kv in tracks) if (kv.Value.brain == null) dead.Add(kv.Key);
            foreach (int id in dead) { PuppetLooks.DropHit(tracks[id].looks); tracks.Remove(id); }
            brains.Clear();
            puppetAnims.Clear(); puppetTransforms.Clear(); puppetFacings.Clear(); puppetNavs.Clear();
            foreach (Brain b in Components.Brains.Items)
            {
                if (b == null) continue;
                var kpid = b.GetComponent<KPrefabID>();
                if (kpid == null) continue;
                brains[kpid.InstanceID] = b;
                var kbac = b.GetComponent<KBatchedAnimController>();
                if (kbac != null) puppetAnims.Add(kbac);
                puppetTransforms.Add(b.transform);
                var facing = b.GetComponent<Facing>();
                if (facing != null) puppetFacings.Add(facing);
                var nav = b.GetComponent<Navigator>();
                if (nav != null) puppetNavs.Add(nav);
            }
        }

        private static Track GetTrack(int id)
        {
            if (tracks.TryGetValue(id, out Track t) && t.brain != null) return t;
            if (!brains.TryGetValue(id, out Brain b) || b == null)
            {
                nextIndex = Mathf.Min(nextIndex, Clock + 0.25f); // maybe it just arrived: look again soon
                return null;
            }
            var kbac = b.GetComponent<KBatchedAnimController>();
            if (kbac == null) return null;
            tracks[id] = t = new Track
            {
                id = id, brain = b, kbac = kbac, tf = b.transform,
                nav = b.GetComponent<Navigator>(), facing = b.GetComponent<Facing>(),
            };
            if (stash.TryGetValue(id, out List<Kf> early)) { t.kfs.AddRange(early); stash.Remove(id); Stashed += early.Count; }
            return t;
        }

        private static void ObserveLag(float d)
        {
            lagSamples[lagNext] = d;
            lagNext = (lagNext + 1) % LagWindow;
            if (lagCount < LagWindow) lagCount++;
            System.Array.Copy(lagSamples, lagSorted, lagCount);
            System.Array.Sort(lagSorted, 0, lagCount);
            float p95 = lagSorted[Mathf.Min(lagCount - 1, (int)(lagCount * 0.99f))];
            JitterP95 = p95 - lagSorted[0];
            // a sample is how old the newest known moment was when the next message came; the timeline must stay
            // KnownMargin behind that, plus a little slack. Rare long stalls (a save, a join) are waited through
            // instead of padding the buffer for a minute (samples mix the two clocks: compare with the fastest one).
            lagTarget = lagSorted[0] + Mathf.Min(JitterP95, MaxSpread) + KnownMargin + 0.02f;
            // the first seconds after loading are bumpy: grow the delay at once instead of playing late
            // (far off: the host restarted, or a long stall; time the host said it didn't run will be skipped anyway)
            if (!lagInit || Mathf.Abs(lagTarget - (lag - DeadAhead())) > 2f || (lagObserved < 60 && lagTarget > lag)) { lag = lagTarget; lagInit = true; lastGuestTick = Clock; }
            lagObserved++;
            if (lagObserved == 1 || lagObserved == 20 || lagObserved == 100 || lagObserved == 400)
                Log.Info($"puppets clock #{lagObserved}: arrival-sent {d * 1000f:F0} ms, p95 {p95 * 1000f:F0}, min {lagSorted[0] * 1000f:F0}; unscaled-real gap {(Time.unscaledTime - Time.realtimeSinceStartup) * 1000f:F0} ms");
        }

        /// <param name="waitedMs">how long it sat in the inbox before the game polled it</param>
        public static void OnSnapshot(byte[] data, long waitedMs)
        {
            float now = Clock;
            float arrived = now - waitedMs / 1000f; // a busy main thread is not network delay
            Index(now);
            if (!choresStopped)
            {
                // whatever they were doing in the guest's copy of the world stops; from now on the host decides
                choresStopped = true;
                Applying = true;
                try
                {
                    foreach (Brain b in brains.Values)
                    {
                        var driver = b.GetComponent<ChoreDriver>();
                        if (driver != null) driver.StopChore();
                    }
                }
                finally { Applying = false; }
                Log.Info($"puppets: {brains.Count} duplicants/critters now follow the host");
            }
            using (var r = Inflate.Open(data)) // (the shared reader: no garbage per message; using is harmless)
            {
                float sentAt = r.ReadSingle();
                int dead = r.ReadByte();
                float deadSum = 0f;
                for (int i = 0; i < dead; i++)
                {
                    float from = sentAt - r.ReadSingle(), to = sentAt - r.ReadSingle();
                    if (to > from) { guestDead.Add(new Vector2(from, to)); deadHistory.Add(new Vector2(from, to)); deadSum += to - from; }
                }
                // what the buffer must cover: until this message came, the newest known moment was the previous
                // message's (network jitter and the server's short hitches alike; time it said it didn't run at all
                // is skipped, so it doesn't count)
                ObserveLag(arrived - (latestSent > 0f && latestSent < sentAt ? latestSent : sentAt) - deadSum);
                if (sentAt > latestSent || sentAt < latestSent - 5f) latestSent = sentAt; // (the host's clock started over: a new server)
                float renderAtArrival = arrived - lag; // late = the network was later than the buffer allows
                while (true)
                {
                    int id;
                    if (!Inflate.More(r)) break;
                    id = r.ReadInt32();
                    if (id == TimelineMark)
                    {
                        byte section = r.ReadByte();
                        if (section == SectionBuildings) BuildingSync.ReadTimeline(r);
                        else if (section == SectionFeedback) Feedback.ReadTimeline(r);
                        else throw new InvalidDataException("puppets: unknown timeline section " + section);
                        continue;
                    }
                    int n = r.ReadByte();
                    Track t = GetTrack(id);
                    if (t == null) GuestUnknown++;
                    for (int i = 0; i < n; i++)
                    {
                        Kf k = Read(r, sentAt);
                        GuestKeyframes++;
                        k.seq = ++seqCounter;
                        if (t == null)
                        {
                            if (!stash.TryGetValue(id, out List<Kf> st)) stash[id] = st = new List<Kf>(8);
                            if (st.Count < 256) st.Add(k);
                            continue;
                        }
                        if (k.time < renderAtArrival && t.kfs.Count > 0) LateKeyframes++; // arrived after its moment
                        if (t.kfs.Count > 0 && k.time <= t.kfs[t.kfs.Count - 1].time - 1f) t.kfs.Clear(); // host clock went back
                        t.kfs.Add(k);
                    }
                    if (t != null && t.kfs.Count > 128) t.kfs.RemoveRange(0, t.kfs.Count - 128);
                }
            }
        }

        public static void GuestTick()
        {
            float now = Clock;
            if (!lagInit) return;
            float dt = Mathf.Clamp(now - lastGuestTick, 0f, 0.5f); // wall time: Unity's frame clock is unreliable here
            lastGuestTick = now;
            // follow the measured delay smoothly: slow down up to 25 % when it grows, speed up 5 % when it shrinks
            // (up to 25 % while far behind)
            float catchUp = lag - lagTarget > 0.5f ? 0.25f : 0.05f;
            float lagBefore = lag;
            lag = lagTarget > lag ? Mathf.Min(lagTarget, lag + 0.25f * dt) : Mathf.Max(lagTarget, lag - catchUp * dt);
            // time the host didn't run (it said so): jump over it, nothing happened there
            for (int i = 0; i < guestDead.Count; i++)
            {
                float rtNow = now - lag;
                Vector2 d = guestDead[i];
                if (d.y <= rtNow) { guestDead.RemoveAt(i--); continue; }
                if (d.x <= rtNow)
                {
                    lag -= d.y - rtNow; SkippedDead += d.y - rtNow; PSkipped += d.y - rtNow;
                    if (d.y - rtNow > 0.3f) Log.Info($"puppets: skipped {(d.y - rtNow) * 1000f:F0} ms the server didn't run (of {(d.y - d.x) * 1000f:F0} ms)");
                    guestDead.RemoveAt(i--);
                }
            }
            // the stream stalled (the server hitched, the line hiccuped): the whole timeline waits at the last moment
            // it knows, instead of each moving entity stopping at its last keyframe and snapping forward later
            bool clamped = false;
            if (latestSent > 0f && now - lag > latestSent - KnownMargin) { lag = now - (latestSent - KnownMargin); clamped = true; }
            if (TestHoldAt >= 0f && now - lag > TestHoldAt) { lag = now - TestHoldAt; clamped = true; }
            if (clamped && CollectWhenWaiting)
            {
                CollectWhenWaiting = false;
                if (GcPolicy.Active) GcPolicy.Collect("the server is saving, the timeline waits");
                else
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    System.GC.Collect();
                    Log.Info($"puppets: collected garbage while waiting for the server ({sw.ElapsedMilliseconds} ms)");
                }
                lastGuestTick = Clock; // that time isn't playback time
            }
            ProbeGuestFrame(dt, lag - lagBefore, clamped);
            float renderTime = now - lag;
            RenderTime = renderTime;
            if (stash.Count > 0 && now >= nextStashPrune)
            {
                // an entity that just appeared is found by the index; ones that never come are dropped
                nextStashPrune = now + 0.5f;
                Index(now);
                var ids = new List<int>(stash.Keys);
                foreach (int id in ids)
                {
                    List<Kf> st = stash[id];
                    if (GetTrack(id) != null) continue; // (handed over)
                    if (st.Count == 0 || st[st.Count - 1].time < renderTime - 5f) stash.Remove(id);
                }
            }
            while (deadHistory.Count > 0 && (deadHistory[0].y < renderTime - 30f || deadHistory[0].y > renderTime + 600f)) deadHistory.RemoveAt(0);
            RenderLagMs = lag * 1000f;
            Applying = true;
            try
            {
                foreach (Track t in tracks.Values) Render(t, renderTime);
            }
            finally { Applying = false; }
            BuildingSync.ApplyTimeline(renderTime);
            Feedback.ApplyTimeline(renderTime);
            if (now >= nextReport)
            {
                if (nextReport > 0) Log.Info("puppets: " + GuestReport());
                nextReport = now + 15f;
            }
        }

        // ------------------------------------------------------------------ smoothness probe: what a player sees as stutter

        public static long PFrames, PHitch50, PHitch100, PHitch250, PMovingFrames, PStarvedFrames, PStarveEpisodes, PSnaps, PSteps;
        public static float PMaxFrame, PSlowTime, PFastTime, PTime, PWaitTime, PSkipped;
        private static readonly long[] stepBySpeed = new long[5]; // cells per game second: <5, 5-10, 10-20, 20-40, >40
        private static readonly Dictionary<string, long> stepByNav = new Dictionary<string, long>();
        private static float probeLast;

        public static void ProbeReset()
        {
            PFrames = PHitch50 = PHitch100 = PHitch250 = PMovingFrames = PStarvedFrames = PStarveEpisodes = PSnaps = PSteps = 0;
            PMaxFrame = PSlowTime = PFastTime = PTime = PWaitTime = PSkipped = 0f;
            System.Array.Clear(stepBySpeed, 0, stepBySpeed.Length);
            stepByNav.Clear();
            probeLast = 0f;
        }

        private static void Hitch(float dtMs)
        {
            PFrames++;
            PTime += dtMs / 1000f;
            if (dtMs > PMaxFrame) PMaxFrame = dtMs;
            if (dtMs > 50f) PHitch50++;
            if (dtMs > 100f) PHitch100++;
            if (dtMs > 250f) PHitch250++;
        }

        private static void ProbeHostFrame(float now)
        {
            if (probeLast > 0f) Hitch((now - probeLast) * 1000f);
            probeLast = now;
        }

        // ---- dead time (host): a frame that took long (a save, a collection) is time the game didn't run. The game
        // moves at most maximumDeltaTime in it; the rest of that wall time nothing happened, and a guest can skip it
        // instead of playing everything late (and then fast) afterwards.
        private static float lastHostFrame;
        private static readonly List<Vector2> deadTimes = new List<Vector2>();
        private static int deadLogged;

        private static void NoteDeadTime(float now)
        {
            float dt = now - lastHostFrame;
            if (lastHostFrame > 0f && dt > Time.maximumDeltaTime + 0.15f)
            {
                deadTimes.Add(new Vector2(lastHostFrame, now - Time.maximumDeltaTime));
                if (dt > 0.5f && deadLogged++ < 20) Log.Info($"puppets: a {dt * 1000f:F0} ms frame here (the game moved at most {Time.maximumDeltaTime * 1000f:F0} ms of it): guests skip the rest");
            }
            lastHostFrame = now;
        }

        private static void ProbeStep(Vector3 p, Vector3 prev, Navigator nav)
        {
            PSteps++;
            float gdt = Mathf.Max(Time.deltaTime, 1e-4f);
            float v = ((Vector2)(p - prev)).magnitude / gdt;
            stepBySpeed[v < 5f ? 0 : v < 10f ? 1 : v < 20f ? 2 : v < 40f ? 3 : 4]++;
            string n = nav != null ? nav.CurrentNavType.ToString() : "-";
            stepByNav.TryGetValue(n, out long c);
            stepByNav[n] = c + 1;
        }

        private static void ProbeGuestFrame(float dt, float lagChange, bool clamped)
        {
            Hitch(dt * 1000f);
            if (clamped) PWaitTime += dt;
            else if (lagChange > 1e-5f) PSlowTime += dt;
            else if (lagChange < -1e-5f) PFastTime += dt;
            probePaused = SpeedControlScreen.Instance != null && SpeedControlScreen.Instance.IsPaused;
        }

        private static bool probePaused;

        /// <summary>Per track per frame: moving now; or out of data while it was moving (it stands still until the next
        /// keyframe, then snaps forward).</summary>
        private static void ProbeTrack(Track t, Kf a, Vector3 pos, float rt)
        {
            bool beyond = t.kfs.Count == 1 && rt > a.time + 0.05f;
            bool wasMoving = t.hasDropped && a.anim == t.dropped.anim && (a.flags & FStep) == 0
                             && ((Vector2)(a.pos - t.dropped.pos)).sqrMagnitude > 1e-4f && a.time - t.dropped.time < 0.5f;
            bool starved = beyond && wasMoving && !probePaused;
            if (t.kfs.Count > 1 && ((Vector2)(t.kfs[1].pos - a.pos)).sqrMagnitude > 1e-4f) PMovingFrames++;
            if (starved) { PStarvedFrames++; if (!t.starved) PStarveEpisodes++; }
            if (t.hasSet && !t.stepPassed && t.starved && !starved && ((Vector2)(pos - t.lastSet)).sqrMagnitude > 0.25f * 0.25f) PSnaps++;
            t.starved = starved;
            t.stepPassed = false;
        }

        public static string ProbeReport(bool host)
        {
            string frames = $"{PFrames} frames in {PTime:F0} s ({(PTime > 0 ? PFrames / PTime : 0):F0} fps), hitches >50 ms {PHitch50}, >100 ms {PHitch100}, >250 ms {PHitch250}, worst {PMaxFrame:F0} ms";
            if (host)
            {
                var nav = new List<string>();
                foreach (var kv in stepByNav) nav.Add(kv.Key + " " + kv.Value);
                return $"{frames}; instant steps {PSteps} (by cells per game second <5/5-10/10-20/20-40/>40: {string.Join("/", System.Array.ConvertAll(stepBySpeed, x => x.ToString()))}; by nav: {string.Join(", ", nav.ToArray())})";
            }
            return $"{frames}; moving {PMovingFrames} entity-frames, out of data while moving {PStarvedFrames} entity-frames ({PStarveEpisodes} times, " +
                   $"{(PMovingFrames > 0 ? 100f * PStarvedFrames / (PMovingFrames + PStarvedFrames) : 0):F2} %), snaps after that {PSnaps}; " +
                   $"waiting for data {PWaitTime:F1} s, skipped server dead time {PSkipped:F1} s, playing slower {PSlowTime:F1} s, faster {PFastTime:F1} s; buffer {BufferMs:F0} ms, jitter p98 {JitterP95 * 1000f:F0} ms";
        }

        public static string GuestReport() =>
            $"{tracks.Count} tracked, {GuestKeyframes} keyframes ({LateKeyframes} late, {GuestUnknown} unknown, {Stashed} kept for entities that came later), {AnimPlays} anim plays, " +
            $"jitter p98 {JitterP95 * 1000f:F0} ms, playing {BufferMs:F0} ms behind the fastest message (unscaled-real clock gap {(Time.unscaledTime - Time.realtimeSinceStartup) * 1000f:F0} ms); " +
            $"external moves {ExternalMoves}, blocked {BlockedCalls}{BlockedSummary()}";

        public static string HostReport() =>
            $"{hostEnts.Count} entities, {HostKeyframes} keyframes in {HostMessages} messages, {HostBytes / 1024} KB";

        private static void Render(Track t, float rt)
        {
            if (t.brain == null || t.kbac == null || t.kfs.Count == 0) return;
            // keep the newest keyframe at or before the render time, and what comes after it
            int drop = 0;
            while (drop + 1 < t.kfs.Count && t.kfs[drop + 1].time <= rt) drop++;
            if (drop > 0)
            {
                for (int i = 0; i < drop; i++)
                {
                    if (t.kfs[i].overrides != null) t.pendingOverrides = t.kfs[i].overrides;
                    ConsumeLooks(t, t.kfs[i]); // every change, in order (the ones skipped over too)
                }
                for (int i = 1; i <= drop; i++) if ((t.kfs[i].flags & FStep) != 0) t.stepPassed = true;
                t.dropped = t.kfs[drop - 1];
                t.hasDropped = true;
                t.kfs.RemoveRange(0, drop);
            }
            Kf a = t.kfs[0];
            if (a.seq != t.appliedSeq)
            {
                if (a.overrides != null) t.pendingOverrides = a.overrides;
                ConsumeLooks(t, a); // before the animation starts: its events (the beam turning on) read the work context
                ApplyLook(t, a);
                t.appliedSeq = a.seq;
            }
            Vector3 pos = a.pos;
            if (t.kfs.Count > 1 && rt > a.time)
            {
                Kf b = t.kfs[1];
                if ((b.flags & FStep) == 0 && ((Vector2)(b.pos - a.pos)).sqrMagnitude <= TeleportDist * TeleportDist)
                {
                    float f;
                    if (deadHistory.Count == 0) f = Mathf.Clamp01((rt - a.time) / Mathf.Max(1e-4f, b.time - a.time));
                    else { float live = Live(a.time, b.time); f = live > 1e-4f ? Mathf.Clamp01(Live(a.time, rt) / live) : (rt >= b.time ? 1f : 0f); }
                    pos = new Vector3(Mathf.Lerp(a.pos.x, b.pos.x, f), Mathf.Lerp(a.pos.y, b.pos.y, f), a.pos.z);
                }
            }
            ProbeTrack(t, a, pos, rt);
            Vector3 cur = t.tf.GetPosition();
            if (t.hasSet && (cur - t.lastSet).sqrMagnitude > 1e-6f) ExternalMoves++;
            if ((cur - pos).sqrMagnitude > 1e-8f) t.tf.SetPosition(pos);
            t.lastSet = pos;
            t.hasSet = true;
            // facing is cheap to check every frame
            bool fx = (t.look.flags & FFlipX) != 0;
            if (t.hasLook && t.kbac.FlipX != fx) t.kbac.FlipX = fx;
        }

        private static void ConsumeLooks(Track t, Kf k)
        {
            if (k.looks == null || k.seq <= t.looks.seq) return;
            t.looks.seq = k.seq;
            PuppetLooks.GuestApply(t.id, t.looks, k.looks, t.kbac);
        }

        /// <summary>Guest: an animation controller of a duplicant / critter the server drives.</summary>
        public static bool IsPuppet(KAnimControllerBase c) => c != null && puppetAnims.Contains(c);

        /// <summary>Server: a guest wants the looks of some puppets in full.</summary>
        public static void OnLooksAsk(byte[] data)
        {
            var list = new List<KeyValuePair<int, PuppetLooks.HostLook>>(hostEnts.Count);
            foreach (HostEnt e in hostEnts.Values) list.Add(new KeyValuePair<int, PuppetLooks.HostLook>(e.id, e.look));
            PuppetLooks.OnAsk(data, list);
        }

        /// <summary>Server: someone (re)entered the game: everyone's looks go out in full again.</summary>
        public static void OnGuestJoined()
        {
            foreach (HostEnt e in hostEnts.Values) if (e.look != null) e.look.needFull = true;
        }

        private static void ApplyLook(Track t, Kf k)
        {
            KBatchedAnimController kbac = t.kbac;
            bool replay = (k.flags & FRestart) != 0 || !t.hasLook || k.anim != t.look.anim || k.mode != t.look.mode
                          || Mathf.Abs(k.speed - t.look.speed) > 0.01f;
            if (t.pendingOverrides != null)
            {
                if (SyncOverrides(kbac, t.pendingOverrides)) replay = true; // replay with the new files
                t.pendingOverrides = null;
            }
            if (t.nav != null && k.nav != NoNav && t.nav.CurrentNavType != (NavType)k.nav) t.nav.SetCurrentNavType((NavType)k.nav);
            bool fx = (k.flags & FFlipX) != 0, fy = (k.flags & FFlipY) != 0;
            if (t.facing != null && t.facing.facingLeft != fx) t.facing.SetFacing(fx);
            if (kbac.FlipX != fx) kbac.FlipX = fx;
            if (kbac.FlipY != fy) kbac.FlipY = fy;
            if (Mathf.Abs(kbac.Rotation - k.rot) > 0.01f) kbac.Rotation = k.rot;
            Vector3 off = kbac.Offset;
            if (Mathf.Abs(off.x - k.off.x) > 1e-4f || Mathf.Abs(off.y - k.off.y) > 1e-4f) kbac.Offset = new Vector3(k.off.x, k.off.y, off.z);
            if (replay && k.anim != 0)
            {
                kbac.PlaySpeedMultiplier = 1f; // the host's speed already includes its multiplier
                kbac.Play(new HashedString(k.anim), (KAnim.PlayMode)k.mode, k.speed > 0f ? k.speed : 1f, k.elapsed);
                kbac.SetElapsedTime(k.elapsed); // no random loop offset
                AnimPlays++;
            }
            t.look = k;
            t.hasLook = true;
        }

        /// <summary>Make the guest's override anim files match the host's. Returns true if anything changed.</summary>
        private static bool SyncOverrides(KBatchedAnimController kbac, List<KeyValuePair<string, float>> want)
        {
            bool changed = false;
            var have = new List<KAnimControllerBase.OverrideAnimFileData>(kbac.OverrideAnimFiles);
            foreach (var o in have)
            {
                if (o.file == null) continue;
                bool keep = false;
                foreach (var kv in want) if (kv.Key == o.file.name && Mathf.Approximately(kv.Value, o.priority)) keep = true;
                if (!keep) { kbac.RemoveAnimOverrides(o.file); changed = true; }
            }
            foreach (var kv in want)
            {
                bool present = false;
                foreach (var o in kbac.OverrideAnimFiles) if (o.file != null && o.file.name == kv.Key) present = true;
                if (present) continue;
                KAnimFile file = Assets.GetAnim(kv.Key);
                if (file == null) { MissingAnimFiles++; continue; }
                kbac.AddAnimOverrides(file, kv.Value);
                changed = true;
            }
            return changed;
        }

        // ------------------------------------------------------------------ guarding the puppets on a guest

        public static bool AllowAnim(KAnimControllerBase c, string what)
        {
            if (!Mirroring || Applying || BuildingSync.Applying) return true;
            if (BuildingSync.IsMirrored(c)) { BuildingSync.Blocked++; return false; } // a building the host drives
            if (!puppetAnims.Contains(c)) return true;
            Blocked(what);
            return false;
        }

        public static bool AllowMove(Transform tf)
        {
            if (!Mirroring || Applying || !puppetTransforms.Contains(tf)) return true;
            Blocked("move");
            return false;
        }

        public static bool AllowFacing(Facing f)
        {
            if (!Mirroring || Applying || !puppetFacings.Contains(f)) return true;
            Blocked("facing");
            return false;
        }

        public static bool AllowNavType(Navigator n)
        {
            if (!Mirroring || Applying || !puppetNavs.Contains(n)) return true;
            Blocked("navtype");
            return false;
        }

        private static readonly Dictionary<string, int> blockedBy = new Dictionary<string, int>();

        /// <summary>Count it, and (sampled: stack traces are slow) remember who tried.</summary>
        private static void Blocked(string what)
        {
            BlockedCalls++;
            if (BlockedCalls > 300 && BlockedCalls % 100 != 0) return;
            string key = what + " <- " + Caller();
            blockedBy.TryGetValue(key, out int c);
            blockedBy[key] = c + 1;
        }

        private static string Caller()
        {
            var st = new System.Diagnostics.StackTrace(2, false);
            var sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < st.FrameCount && n < 3; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                System.Type dt = m?.DeclaringType;
                if (dt == null || dt.Namespace == "OniCoop" || dt.Namespace == "HarmonyLib") continue;
                if (n++ > 0) sb.Append(" < ");
                sb.Append(dt.Name).Append('.').Append(m.Name);
            }
            return sb.ToString();
        }

        private static string BlockedSummary()
        {
            if (blockedBy.Count == 0) return "";
            var list = new List<KeyValuePair<string, int>>(blockedBy);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            var sb = new StringBuilder(" (sampled: ");
            for (int i = 0; i < list.Count && i < 6; i++) sb.Append(i > 0 ? "; " : "").Append(list[i].Key).Append(" x").Append(list[i].Value);
            return sb.Append(')').ToString();
        }

        /// <summary>For the sync test: every puppet as this side shows it right now.</summary>
        public static string Dump()
        {
            var sb = new StringBuilder();
            foreach (Brain b in Components.Brains.Items)
            {
                if (b == null) continue;
                var kpid = b.GetComponent<KPrefabID>();
                var kbac = b.GetComponent<KBatchedAnimController>();
                var nav = b.GetComponent<Navigator>();
                if (kpid == null || kbac == null) continue;
                Vector3 p = b.transform.GetPosition();
                sb.Append(kpid.InstanceID).Append(' ').Append(kpid.PrefabTag.Name).Append(' ')
                  .Append(p.x.ToString("F2")).Append(' ').Append(p.y.ToString("F2")).Append(' ')
                  .Append(kbac.CurrentAnim != null ? kbac.CurrentAnim.name : "-").Append(' ')
                  .Append(kbac.FlipX ? 1 : 0).Append(' ').Append(kbac.FlipY ? 1 : 0).Append(' ')
                  .Append(nav != null ? nav.CurrentNavType.ToString() : "-").Append(' ')
                  .Append(kbac.Rotation.ToString("F0")).Append('\n');
            }
            return sb.ToString();
        }
    }

    /// <summary>On a guest the host decides: no local brain updates (no chores chosen) ...</summary>
    [HarmonyPatch(typeof(Brain), nameof(Brain.UpdateBrain))]
    internal static class Brain_UpdateBrain_Patch
    {
        private static bool Prefix() => !Puppets.Mirroring;
    }

    /// <summary>... and no movement of their own (positions come from the host).</summary>
    [HarmonyPatch(typeof(TransitionDriver), nameof(TransitionDriver.UpdateTransition))]
    internal static class TransitionDriver_UpdateTransition_Patch
    {
        private static bool Prefix() => !Puppets.Mirroring;
    }

    // Nothing else on the guest (falling, reactions, monitors, ...) may move a puppet, play its animations or turn it.

    [HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.Play), new[] { typeof(HashedString), typeof(KAnim.PlayMode), typeof(float), typeof(float) })]
    internal static class KAnim_Play_Patch
    {
        private static bool Prefix(KAnimControllerBase __instance) => Puppets.AllowAnim(__instance, "Play");
    }

    [HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.Play), new[] { typeof(HashedString[]), typeof(KAnim.PlayMode) })]
    internal static class KAnim_PlayMany_Patch
    {
        private static bool Prefix(KAnimControllerBase __instance) => Puppets.AllowAnim(__instance, "Play[]");
    }

    [HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.Queue))]
    internal static class KAnim_Queue_Patch
    {
        private static bool Prefix(KAnimControllerBase __instance) => Puppets.AllowAnim(__instance, "Queue");
    }

    [HarmonyPatch(typeof(TransformExtensions), nameof(TransformExtensions.SetPosition))]
    internal static class Transform_SetPosition_Patch
    {
        private static bool Prefix(Transform __0, Vector3 __1, ref Vector3 __result)
        {
            if (Puppets.AllowMove(__0)) return true;
            __result = __1; // it returns the position it was given
            return false;
        }
    }

    [HarmonyPatch(typeof(Facing), nameof(Facing.SetFacing))]
    internal static class Facing_SetFacing_Patch
    {
        private static bool Prefix(Facing __instance) => Puppets.AllowFacing(__instance);
    }

    [HarmonyPatch(typeof(Navigator), nameof(Navigator.SetCurrentNavType))]
    internal static class Navigator_SetCurrentNavType_Patch
    {
        private static bool Prefix(Navigator __instance) => Puppets.AllowNavType(__instance);
    }
}
