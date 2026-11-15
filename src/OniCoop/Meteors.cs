namespace OniCoop
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Meteor showers and the missiles shot at them, decided by the server and shown on the duplicants'
    /// timeline.
    ///
    /// A guest's game would run its own shower (the event's state comes with the save, so it bombards at the same time
    /// but with meteors of its own), move the server's meteors itself and let them hit its own copy of the ground, and
    /// fire its own missiles. Now:
    ///  - the guest makes no meteors and fires no missiles of its own;
    ///  - the server samples every meteor and every missile in flight (position, the animation's offset and rotation)
    ///    ten times a second into the timeline; a guest moves them only from that (no hits, no damage of its own);
    ///  - a missile's launch and its explosion are events on the timeline: the guest's own copy of that missile then
    ///    takes off (smoke trail, sound) and explodes (the game's own effects), without making the loot (the server's
    ///    loot arrives as objects);
    ///  - a meteor's impact sound is an event too; its explosion effect comes through the general effects
    ///    (Feedback: Game.SpawnFX).</summary>
    internal static class Meteors
    {
        private const byte RMove = 1, RLaunch = 2, RMissileBoom = 3, RImpact = 4, RSky = 5;
        private const float SampleInterval = 0.1f;

        public static bool Applying;
        public static long HostMoves, HostLaunches, HostBooms, HostImpacts, GuestMoves, GuestLaunches, GuestBooms, GuestImpacts,
            BlockedShowerMeteors, BlockedLaunches, BlockedLoot;

        private static readonly MemoryStream buf = new MemoryStream();
        private static readonly BinaryWriter bufW = new BinaryWriter(buf);
        private static int records;

        public static void Reset()
        {
            buf.SetLength(0);
            records = 0;
            hostMovers.Clear();
            flying.Clear();
            tracks.Clear();
            events.Clear();
            retry.Clear();
            foreach (GameObject bg in sky.Values) if (bg != null) UnityEngine.Object.Destroy(bg);
            sky.Clear();
            skyNow.Clear(); skySent.Clear();
            nextSky = skyResend = 0f;
        }

        private static int IdOf(GameObject go)
        {
            if (go == null) return 0;
            KPrefabID k = go.GetComponent<KPrefabID>();
            return k != null && k.InstanceID > 0 && k.InstanceID < EntitySync.LocalIdBase ? k.InstanceID : 0;
        }

        // ================================================================== server

        private sealed class HostMover { public GameObject go; public KBatchedAnimController kbac; public float last; public Vector3 pos, off; public float rot; }
        private static readonly Dictionary<int, HostMover> hostMovers = new Dictionary<int, HostMover>();
        private static readonly HashSet<GameObject> flying = new HashSet<GameObject>(); // missiles launched and not yet gone
        private static readonly List<int> gone = new List<int>();

        private static bool Host => CommandBus.Active && CommandBus.IsHost;
        private static bool HostLive => Host && EntitySync.GuestsInGame > 0;

        // the showers' sky (MeteorShowerEvent's background particles) per world, as the server has it
        private static readonly List<int> skyNow = new List<int>(), skySent = new List<int>();
        private static List<GameplayEventInstance> eventList = new List<GameplayEventInstance>();
        private static float nextSky, skyResend;

        /// <summary>Every server frame.</summary>
        public static void HostTick()
        {
            if (!HostLive || ClusterManager.Instance == null) return;
            float now = Puppets.Clock;
            if (now >= nextSky)
            {
                nextSky = now + 1f;
                SkyWorlds(skyNow, false);
                bool same = skyNow.Count == skySent.Count;
                for (int i = 0; same && i < skyNow.Count; i++) same = skyNow[i] == skySent[i];
                if (!same || now >= skyResend)
                {
                    skyResend = now + 5f;
                    skySent.Clear(); skySent.AddRange(skyNow);
                    bufW.Write(RSky); bufW.Write(now); bufW.Write((byte)skyNow.Count);
                    foreach (int wid in skyNow) bufW.Write(wid);
                    records++;
                }
            }
            foreach (WorldContainer w in ClusterManager.Instance.WorldContainers)
            {
                if (w == null) continue;
                List<Comet> list = Components.Meteors.GetItems(w.id);
                if (list != null) foreach (Comet c in list) if (c != null) Sample(c.gameObject, now);
            }
            if (flying.Count > 0)
            {
                foreach (GameObject m in flying) if (m != null) Sample(m, now);
                flying.RemoveWhere(m => m == null);
            }
            if (hostMovers.Count > 0)
            {
                gone.Clear();
                foreach (var kv in hostMovers) if (kv.Value.go == null) gone.Add(kv.Key);
                foreach (int id in gone) hostMovers.Remove(id);
            }
        }

        /// <summary>The worlds whose shower shows its sky now (sorted); stop: (guest) put out this game's own.</summary>
        private static void SkyWorlds(List<int> into, bool stop)
        {
            into.Clear();
            if (GameplayEventManager.Instance == null) return;
            eventList.Clear();
            GameplayEventManager.Instance.GetActiveEventsOfType<Klei.AI.MeteorShowerEvent>(ref eventList);
            foreach (GameplayEventInstance e in eventList)
            {
                if (!(e?.smi is Klei.AI.MeteorShowerEvent.StatesInstance smi) || smi.activeMeteorBackground == null) continue;
                if (stop) { smi.StopBackgroundEffects(); OwnSkyStopped++; continue; }
                if (!into.Contains(e.worldId)) into.Add(e.worldId);
            }
            into.Sort();
        }

        /// <summary>Test sync points: every meteor's and missile's position now.</summary>
        public static void FlushNow(float at)
        {
            if (!HostLive || ClusterManager.Instance == null) return;
            foreach (WorldContainer w in ClusterManager.Instance.WorldContainers)
            {
                List<Comet> list = w != null ? Components.Meteors.GetItems(w.id) : null;
                if (list != null) foreach (Comet c in list) if (c != null) Sample(c.gameObject, at, true);
            }
            foreach (GameObject m in flying) if (m != null) Sample(m, at, true);
        }

        private static void Sample(GameObject go, float now, bool force = false)
        {
            int id = IdOf(go);
            if (id == 0) return;
            if (!hostMovers.TryGetValue(id, out HostMover m)) hostMovers[id] = m = new HostMover { go = go, kbac = go.GetComponent<KBatchedAnimController>(), last = -1f };
            if (!force && now - m.last < SampleInterval) return;
            Vector3 p = go.transform.GetPosition();
            Vector3 off = m.kbac != null ? m.kbac.Offset : Vector3.zero;
            float rot = m.kbac != null ? m.kbac.Rotation : 0f;
            if (!force && m.last >= 0f && (p - m.pos).sqrMagnitude < 1e-6f && (off - m.off).sqrMagnitude < 1e-6f && Mathf.Abs(rot - m.rot) < 0.1f) return;
            m.last = now; m.pos = p; m.off = off; m.rot = rot;
            bufW.Write(RMove); bufW.Write(now); bufW.Write(id);
            bufW.Write(p.x); bufW.Write(p.y); bufW.Write(p.z); bufW.Write(off.x); bufW.Write(off.y); bufW.Write(rot);
            records++;
            HostMoves++;
        }

        /// <summary>Server: a missile is about to fly at a meteor.</summary>
        private static void PrepareLaunchPostfix(MissileProjectile.StatesInstance __instance, Comet meteor_target, float speed, Vector3 launchPos, float launchAngle)
        {
            if (!HostLive || __instance == null) return;
            GameObject go = __instance.gameObject;
            int id = IdOf(go), target = meteor_target != null ? IdOf(meteor_target.gameObject) : 0;
            if (id == 0) return;
            flying.Add(go);
            bufW.Write(RLaunch); bufW.Write(Puppets.Clock); bufW.Write(id); bufW.Write(target); bufW.Write(speed);
            bufW.Write(launchPos.x); bufW.Write(launchPos.y); bufW.Write(launchPos.z); bufW.Write(launchAngle);
            records++;
            HostLaunches++;
            Sample(go, Puppets.Clock, true); // its first position at once
        }

        /// <summary>Server: the missile reached its meteor.</summary>
        private static void TriggerExplosionPrefix(MissileProjectile.StatesInstance __instance)
        {
            if (!HostLive || __instance == null) return;
            int id = IdOf(__instance.gameObject);
            if (id == 0) return;
            bufW.Write(RMissileBoom); bufW.Write(Puppets.Clock); bufW.Write(id);
            records++;
            HostBooms++;
        }

        /// <summary>Server: a meteor hits the ground (its explosion effect goes through Game.SpawnFX).</summary>
        private static void CometExplodePrefix(Comet __instance, Vector3 pos)
        {
            if (!HostLive || __instance == null) return;
            bufW.Write(RImpact); bufW.Write(Puppets.Clock); bufW.Write(IdOf(__instance.gameObject));
            bufW.Write(pos.x); bufW.Write(pos.y); bufW.Write(__instance.impactSound ?? "");
            records++;
            HostImpacts++;
        }

        public static bool HasTimeline => records > 0;

        public static void TakeTimeline(BinaryWriter w)
        {
            w.Write(records);
            if (records > 0) w.Write(buf.GetBuffer(), 0, (int)buf.Length);
            buf.SetLength(0);
            records = 0;
        }

        // ================================================================== guest

        private struct Key { public float t; public Vector3 pos; public Vector2 off; public float rot; }

        private sealed class Track
        {
            public readonly List<Key> keys = new List<Key>(8);
            public GameObject go;
            public KBatchedAnimController kbac;
            public KSelectable selectable; // (a meteor: clickable once it is inside the world, as Comet.Sim33ms does)
            public float lastKeyAt;
        }

        private struct Event
        {
            public byte kind;
            public float t, speed, angle, x, y;
            public int id, target;
            public Vector3 launch;
            public string sound;
            public int[] worlds;
        }

        private static readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        private static readonly Queue<Event> events = new Queue<Event>();
        // events whose object isn't here yet (a missile split off its stack: its spawn record is a frame behind)
        private static readonly List<Event> retry = new List<Event>();
        public static long Retried, GaveUp;

        /// <summary>Guest: objects whose position comes from here (item updates leave them alone).</summary>
        public static bool IsMover(int id) => tracks.Count > 0 && tracks.ContainsKey(id);

        public static void ReadTimeline(BinaryReader r)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                byte kind = r.ReadByte();
                float t = r.ReadSingle();
                switch (kind)
                {
                    case RMove:
                    {
                        int id = r.ReadInt32();
                        var k = new Key { t = t, pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), off = new Vector2(r.ReadSingle(), r.ReadSingle()), rot = r.ReadSingle() };
                        if (!tracks.TryGetValue(id, out Track tr)) tracks[id] = tr = new Track();
                        if (tr.keys.Count > 0 && k.t < tr.keys[tr.keys.Count - 1].t - 1f) tr.keys.Clear(); // the server's clock started over
                        tr.keys.Add(k);
                        tr.lastKeyAt = k.t;
                        GuestMoves++;
                        break;
                    }
                    case RLaunch:
                        events.Enqueue(new Event { kind = kind, t = t, id = r.ReadInt32(), target = r.ReadInt32(), speed = r.ReadSingle(),
                            launch = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), angle = r.ReadSingle() });
                        break;
                    case RMissileBoom:
                        events.Enqueue(new Event { kind = kind, t = t, id = r.ReadInt32() });
                        break;
                    case RImpact:
                        events.Enqueue(new Event { kind = kind, t = t, id = r.ReadInt32(), x = r.ReadSingle(), y = r.ReadSingle(), sound = Inflate.ReadId(r) });
                        break;
                    case RSky:
                    {
                        var ws = new int[r.ReadByte()];
                        for (int k = 0; k < ws.Length; k++) ws[k] = r.ReadInt32();
                        events.Enqueue(new Event { kind = kind, t = t, worlds = ws });
                        break;
                    }
                    default: throw new InvalidDataException("meteors: unknown record " + kind);
                }
            }
        }

        /// <summary>Guest, every frame after the puppets are placed: move the meteors and missiles to the timeline's
        /// moment, and play what happened up to it.</summary>
        public static void ApplyTimeline(float rt)
        {
            Applying = true;
            try
            {
                SkyTick();
                for (int i = 0; i < retry.Count; i++)
                {
                    Event e = retry[i];
                    bool done;
                    try { done = Play(e); }
                    catch (Exception ex) { Log.Warn($"meteors: event {e.kind} failed: {ex.Message}"); done = true; }
                    if (done) { retry.RemoveAt(i--); Retried++; }
                    else if (e.t < rt - 2f) { retry.RemoveAt(i--); GaveUp++; }
                }
                while (events.Count > 0)
                {
                    Event e = events.Peek();
                    if (e.t > rt && e.t < rt + 10f) break;
                    events.Dequeue();
                    bool done;
                    try { done = Play(e); }
                    catch (Exception ex) { Log.Warn($"meteors: event {e.kind} failed: {ex.Message}"); done = true; }
                    if (!done) retry.Add(e);
                }
                if (tracks.Count == 0) return;
                gone.Clear();
                foreach (var kv in tracks)
                {
                    Track tr = kv.Value;
                    if (tr.go == null)
                    {
                        KPrefabID k = KPrefabIDTracker.Get().GetInstance(kv.Key);
                        if (k != null)
                        {
                            tr.go = k.gameObject;
                            tr.kbac = k.GetComponent<KBatchedAnimController>();
                            tr.selectable = k.GetComponent<Comet>() != null ? k.GetComponent<KSelectable>() : null;
                        }
                        else
                        {
                            // not here (yet, or any more): keep the keys a few seconds, then forget it
                            if (tr.lastKeyAt < rt - 5f) gone.Add(kv.Key);
                            continue;
                        }
                    }
                    Place(tr, rt);
                }
                foreach (int id in gone) tracks.Remove(id);
            }
            finally { Applying = false; }
        }

        private static void Place(Track tr, float rt)
        {
            List<Key> ks = tr.keys;
            if (ks.Count == 0) return;
            int drop = 0;
            while (drop + 1 < ks.Count && ks[drop + 1].t <= rt) drop++;
            if (drop > 0) ks.RemoveRange(0, drop);
            Key a = ks[0];
            Vector3 pos = a.pos;
            Vector2 off = a.off;
            float rot = a.rot;
            if (ks.Count > 1 && rt > a.t)
            {
                Key b = ks[1];
                float f = Mathf.Clamp01((rt - a.t) / Mathf.Max(1e-4f, b.t - a.t));
                pos = Vector3.Lerp(a.pos, b.pos, f);
                off = Vector2.Lerp(a.off, b.off, f);
                rot = Mathf.LerpAngle(a.rot, b.rot, f);
            }
            tr.go.transform.SetPosition(pos);
            if (tr.kbac != null)
            {
                Vector3 o = tr.kbac.Offset;
                if (Mathf.Abs(o.x - off.x) > 1e-4f || Mathf.Abs(o.y - off.y) > 1e-4f) tr.kbac.Offset = new Vector3(off.x, off.y, o.z);
                if (Mathf.Abs(tr.kbac.Rotation - rot) > 0.01f) tr.kbac.Rotation = rot;
            }
            if (tr.selectable != null && !tr.selectable.enabled && off.x == 0f && off.y == 0f) tr.selectable.enabled = true;
        }

        /// <returns>false: its object isn't here yet (try again)</returns>
        private static bool Play(Event e)
        {
            switch (e.kind)
            {
                case RLaunch:
                {
                    GameObject m = KPrefabIDTracker.Get().GetInstance(e.id)?.gameObject;
                    Comet target = KPrefabIDTracker.Get().GetInstance(e.target)?.GetComponent<Comet>();
                    var smi = m != null ? m.GetSMI<MissileProjectile.StatesInstance>() : null;
                    if (smi == null || target == null) return false;
                    smi.PrepareLaunch(target, e.speed, e.launch, e.angle); // the game's own take-off: smoke trail, sound
                    GuestLaunches++;
                    return true;
                }
                case RMissileBoom:
                {
                    GameObject m = KPrefabIDTracker.Get().GetInstance(e.id)?.gameObject;
                    var smi = m != null ? m.GetSMI<MissileProjectile.StatesInstance>() : null;
                    if (smi == null) return false;
                    if (!smi.IsInsideState(smi.sm.launch)) { Explode(smi); return true; } // (never took off here: just the blast)
                    smi.sm.triggerexplode.Set(true, smi); // the game's own explosion (its loot is the server's)
                    GuestBooms++;
                    return true;
                }
                case RSky:
                    SetSky(e.worlds);
                    return true;
                case RImpact:
                {
                    string sound = GlobalAssets.GetSound(string.IsNullOrEmpty(e.sound) ? "Meteor_Large_Impact" : e.sound);
                    int cell = Grid.PosToCell(new Vector3(e.x, e.y, 0f));
                    if (sound != null && Grid.IsValidCell(cell) && Grid.WorldIdx[cell] == ClusterManager.Instance.activeWorldId)
                    {
                        KFMOD.PlayOneShot(sound, new Vector3(e.x, e.y, 0f));
                    }
                    GuestImpacts++;
                    return true;
                }
            }
            return true;
        }

        // ---- the showers' sky on a guest: as the server's, not its own game's

        private static readonly Dictionary<int, GameObject> sky = new Dictionary<int, GameObject>();
        private static readonly List<int> skyScratch = new List<int>();
        private static int skyActiveWorld = -2;
        private static float nextOwnSkyCheck;
        public static long OwnSkyStopped, SkyShown;

        private static void SetSky(int[] worlds)
        {
            skyScratch.Clear();
            foreach (int wid in sky.Keys) if (Array.IndexOf(worlds, wid) < 0) skyScratch.Add(wid);
            foreach (int wid in skyScratch) { StopSky(sky[wid]); sky.Remove(wid); }
            foreach (int wid in worlds)
            {
                if (sky.ContainsKey(wid)) continue;
                WorldContainer w = ClusterManager.Instance?.GetWorld(wid);
                if (w == null || EffectPrefabs.Instance == null) continue;
                // as MeteorShowerEvent.StartBackgroundEffects
                GameObject bg = Util.KInstantiate(EffectPrefabs.Instance.MeteorBackground);
                bg.transform.SetPosition(new Vector3((w.maximumBounds.x + w.minimumBounds.x) / 2f, w.maximumBounds.y, 25f));
                bg.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                sky[wid] = bg;
                SkyShown++;
            }
            skyActiveWorld = -2; // (renderers set below)
        }

        private static void StopSky(GameObject bg)
        {
            if (bg == null) return;
            ParticleSystem ps = bg.GetComponent<ParticleSystem>();
            if (ps == null) { UnityEngine.Object.Destroy(bg); return; }
            ParticleSystem.MainModule main = ps.main;
            main.stopAction = ParticleSystemStopAction.Destroy;
            ps.Stop();
            if (!ps.IsAlive()) UnityEngine.Object.Destroy(bg);
        }

        private static void SkyTick()
        {
            float now = Time.unscaledTime;
            if (now >= nextOwnSkyCheck)
            {
                nextOwnSkyCheck = now + 1f;
                SkyWorlds(skyScratch, true); // (one started before this guest mirrored: a save loaded during a shower)
            }
            int active = ClusterManager.Instance != null ? ClusterManager.Instance.activeWorldId : -1;
            if (active == skyActiveWorld || sky.Count == 0) return;
            skyActiveWorld = active;
            foreach (var kv in sky)
            {
                var rend = kv.Value != null ? kv.Value.GetComponent<ParticleSystemRenderer>() : null;
                if (rend != null) rend.enabled = kv.Key == active;
            }
        }

        private static bool StartBackgroundPrefix()
        {
            if (!Puppets.Mirroring) return true;
            return false;
        }

        private static readonly System.Reflection.MethodInfo ExplodeFx = AccessTools.Method(typeof(MissileProjectile.StatesInstance), "Explode");

        /// <summary>Guest: a missile's blast only (the meteor goes by the server's record, at the same moment of the
        /// timeline).</summary>
        private static void Explode(MissileProjectile.StatesInstance smi)
        {
            ExplodeFx?.Invoke(smi, null);
            GuestBooms++;
        }

        /// <summary>Guest: TriggerExplosion (the explode state): the blast only; no loot, the meteor isn't destroyed here.</summary>
        private static bool GuestTriggerExplosionPrefix(MissileProjectile.StatesInstance __instance)
        {
            if (!Puppets.Mirroring || __instance == null) return true;
            ExplodeFx?.Invoke(__instance, null);
            return false;
        }

        // ---- what a guest's own game may not do

        private static bool Mirroring => Puppets.Mirroring && !Applying;

        private static bool SpawnBombardPrefix(ref GameObject __result)
        {
            if (!Mirroring) return true;
            __result = null;
            BlockedShowerMeteors++;
            return false;
        }

        private static bool LaunchPrefix()
        {
            if (!Mirroring) return true;
            BlockedLaunches++;
            return false;
        }

        /// <summary>Guest: meteors move only from the timeline; no hits, no damage, no exhaust.</summary>
        private static bool CometSimPrefix() => !Puppets.Mirroring;

        /// <summary>Guest: missiles fly only from the timeline (their take-off and explosion are the game's own).</summary>
        private static bool UpdateLaunchPrefix() => !Puppets.Mirroring;

        /// <summary>Guest: a meteor keeps the mass and temperature the server gave it (its spawn record).</summary>
        private static bool RandomizeMassPrefix() => !Puppets.Mirroring;

        private static bool LootPrefix()
        {
            if (!Puppets.Mirroring) return true;
            BlockedLoot++;
            return false;
        }

        public static void ApplyPatches(Harmony h)
        {
            void P(System.Reflection.MethodBase m, string prefix = null, string postfix = null)
            {
                try
                {
                    if (m == null) throw new Exception("not found");
                    h.Patch(m, prefix: prefix != null ? new HarmonyMethod(typeof(Meteors), prefix) : null,
                        postfix: postfix != null ? new HarmonyMethod(typeof(Meteors), postfix) : null);
                }
                catch (Exception e) { Log.Warn($"meteors: can't patch {m?.DeclaringType?.Name}.{m?.Name}: {e.Message}"); }
            }
            P(AccessTools.Method(typeof(Klei.AI.MeteorShowerEvent.StatesInstance), "SpawnBombard"), prefix: nameof(SpawnBombardPrefix));
            P(AccessTools.Method(typeof(Klei.AI.MeteorShowerEvent.StatesInstance), nameof(Klei.AI.MeteorShowerEvent.StatesInstance.StartBackgroundEffects)), prefix: nameof(StartBackgroundPrefix));
            P(AccessTools.Method(typeof(MissileLauncher.Instance), nameof(MissileLauncher.Instance.LaunchMissile)), prefix: nameof(LaunchPrefix));
            P(AccessTools.Method(typeof(MissileLauncher.Instance), nameof(MissileLauncher.Instance.LaunchLongRangeMissile)), prefix: nameof(LaunchPrefix));
            P(AccessTools.Method(typeof(Comet), nameof(Comet.Sim33ms)), prefix: nameof(CometSimPrefix));
            P(AccessTools.Method(typeof(Comet), nameof(Comet.RandomizeMassAndTemperature)), prefix: nameof(RandomizeMassPrefix));
            P(AccessTools.Method(typeof(Comet), "Explode", new[] { typeof(Vector3), typeof(int), typeof(int), typeof(Element) }), prefix: nameof(CometExplodePrefix));
            P(AccessTools.Method(typeof(MissileProjectile.StatesInstance), nameof(MissileProjectile.StatesInstance.PrepareLaunch)), postfix: nameof(PrepareLaunchPostfix));
            P(AccessTools.Method(typeof(MissileProjectile.StatesInstance), nameof(MissileProjectile.StatesInstance.UpdateLaunch)), prefix: nameof(UpdateLaunchPrefix));
            P(AccessTools.Method(typeof(MissileProjectile.StatesInstance), nameof(MissileProjectile.StatesInstance.TriggerExplosion)), prefix: nameof(TriggerExplosionPrefix));
            P(AccessTools.Method(typeof(MissileProjectile.StatesInstance), nameof(MissileProjectile.StatesInstance.TriggerExplosion)), prefix: nameof(GuestTriggerExplosionPrefix));
            P(AccessTools.Method(typeof(MissileProjectile.StatesInstance), "SpawnMeteorResources"), prefix: nameof(LootPrefix));
        }

        public static string Report(bool host) => host
            ? $"meteors: {HostMoves} positions, {HostLaunches} missile launches, {HostBooms} missile hits, {HostImpacts} meteor impacts"
            : $"meteors: {GuestMoves} positions ({tracks.Count} tracked now), {GuestLaunches} launches, {GuestBooms} missile hits, {GuestImpacts} impacts, sky shown {SkyShown} (now {sky.Count}, own put out {OwnSkyStopped}); " +
              $"waited for their object {Retried} (gave up {GaveUp}); kept from the guest's own game: {BlockedShowerMeteors} meteors, {BlockedLaunches} launches, {BlockedLoot} loot drops";

        /// <summary>For the sync test: meteors and missiles in flight as this side shows them.</summary>
        public static string Dump()
        {
            var sb = new System.Text.StringBuilder();
            if (ClusterManager.Instance == null) return "";
            foreach (WorldContainer w in ClusterManager.Instance.WorldContainers)
            {
                List<Comet> list = w != null ? Components.Meteors.GetItems(w.id) : null;
                if (list == null) continue;
                foreach (Comet c in list)
                {
                    if (c == null) continue;
                    int id = IdOf(c.gameObject);
                    Vector3 p = c.transform.GetPosition();
                    var kbac = c.GetComponent<KBatchedAnimController>();
                    Vector3 o = kbac != null ? kbac.Offset : Vector3.zero;
                    sb.Append("M ").Append(id).Append(' ').Append(c.PrefabID().Name).Append(' ').Append((p.x + o.x).ToString("F2")).Append(' ').Append((p.y + o.y).ToString("F2")).Append('\n');
                }
            }
            return sb.ToString();
        }
    }
}
