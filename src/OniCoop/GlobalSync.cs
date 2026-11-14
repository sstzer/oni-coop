namespace OniCoop
{
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using HarmonyLib;
    using Klei.AI;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Roadmap step 3, the "what is true" part: everything that is computed on the host (whoever runs the
    /// simulation: a player's game now, the server's later) and that a guest must only show.
    ///
    /// Amounts: duplicants' calories, breath, stress, bladder, stamina, hit points, body temperature; critters' age,
    /// fertility, wildness, incubation; plants' maturity... all live in AmountInstance and change in one batch update.
    /// A guest doesn't run that update (its duplicants can't starve or suffocate on their own) and takes the host's
    /// values, sent when they move. Deaths come from the host as well.
    /// Colony: research (progress, finished techs), every duplicant's experience (skill points), the clock (cycle and
    /// time of day: schedules follow it), the printing pod's countdown.</summary>
    internal static class GlobalSync
    {
        public const byte ChGlobal = 14; // host -> guests

        private const byte RAmount = 1, RDeath = 2, RResearch = 3, RXp = 4, RClock = 5, RImmigration = 6;

        public static bool Applying;
        private static bool HostActive => CommandBus.Active && CommandBus.IsHost && EntitySync.GuestsInGame > 0;

        private static readonly AccessTools.FieldRef<MinionResume, float> Xp = AccessTools.FieldRefAccess<MinionResume, float>("totalExperienceGained");
        private static readonly AccessTools.FieldRef<GameClock, float> ClockTime = AccessTools.FieldRefAccess<GameClock, float>("time");
        private static readonly AccessTools.FieldRef<GameClock, float> ClockCycleTime = AccessTools.FieldRefAccess<GameClock, float>("timeSinceStartOfCycle");
        private static readonly AccessTools.FieldRef<Immigration, bool> ImmigrantAvailable = AccessTools.FieldRefAccess<Immigration, bool>("bImmigrantAvailable");
        private static readonly System.Reflection.MethodInfo AddTime = AccessTools.Method(typeof(GameClock), "AddTime");

        // ================================================================== host

        private static readonly List<AmountInstance> amounts = new List<AmountInstance>();
        private static readonly Dictionary<AmountInstance, float> sent = new Dictionary<AmountInstance, float>();
        private static readonly MemoryStream buf = new MemoryStream();
        private static readonly BinaryWriter bufW = new BinaryWriter(buf);
        private static int records, cursor;
        private static float nextSend, nextColony;
        private const int Slices = 12; // all amounts looked at about 5 times a second (the bars follow the duplicants closely)
        public static long HostRecords, HostBytes;

        /// <summary>AmountInstance constructor: every amount of every creature / plant, in one list.</summary>
        public static void Register(AmountInstance a) => amounts.Add(a);

        public static void Reset()
        {
            sent.Clear();
            buf.SetLength(0);
            records = 0;
            cursor = 0;
            amountsApplied = 0;
        }

        /// <summary>A guest (re)loaded: every value goes out again.</summary>
        public static void OnGuestJoined() { sent.Clear(); nextColony = 0; }

        public static void HostTick(float now)
        {
            int n = amounts.Count;
            if (n > 0)
            {
                int count = (n + Slices - 1) / Slices;
                for (int step = 0; step < count; step++)
                {
                    if (cursor >= amounts.Count) { cursor = 0; Prune(); if (amounts.Count == 0) break; }
                    AmountInstance a = amounts[cursor++];
                    if (a == null || a.gameObject == null) continue;
                    var k = a.gameObject.GetComponent<KPrefabID>();
                    if (k == null || k.InstanceID < 0 || k.InstanceID >= EntitySync.LocalIdBase || !k.isSpawned) continue;
                    float v = a.value;
                    float band = Mathf.Max(0.002f * Mathf.Abs(a.GetMax() - a.GetMin()), 0.001f);
                    if (sent.TryGetValue(a, out float last) && Mathf.Abs(last - v) <= band) continue;
                    sent[a] = v;
                    bufW.Write(RAmount); bufW.Write(k.InstanceID); bufW.Write(a.amount.Id); bufW.Write(v);
                    records++;
                }
            }
            if (now >= nextColony)
            {
                nextColony = now + 2f;
                WriteColony(bufW);
            }
            if (records > 0 && now >= nextSend) Send(now);
        }

        /// <summary>Test sync points: every value now, sent at once.</summary>
        public static void FlushNow()
        {
            if (!HostActive) return;
            for (int i = 0; i <= Slices; i++) HostTick(float.NegativeInfinity);
            nextColony = 0; nextSend = 0;
            HostTick(Time.realtimeSinceStartup); // (not +infinity: that would have been the next send time for good)
        }

        private static void Send(float now)
        {
            nextSend = now + 0.25f; // (the bars move in steps this long at most)
            var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionMode.Compress, true)) z.Write(buf.GetBuffer(), 0, (int)buf.Length);
            byte[] payload = ms.ToArray();
            HostBytes += payload.Length;
            HostRecords += records;
            CommandBus.Client.SendRelay(Proto.TargetAll, ChGlobal, payload);
            buf.SetLength(0);
            records = 0;
        }

        private static void Prune()
        {
            amounts.RemoveAll(a => a == null || a.gameObject == null);
            if (sent.Count > amounts.Count * 2)
            {
                var dead = new List<AmountInstance>();
                foreach (AmountInstance a in sent.Keys) if (a.gameObject == null) dead.Add(a);
                foreach (AmountInstance a in dead) sent.Remove(a);
            }
        }

        public static void OnKilled(GameObject go, Death death)
        {
            if (!HostActive || go == null || death == null) return;
            var k = go.GetComponent<KPrefabID>();
            if (k == null || k.InstanceID >= EntitySync.LocalIdBase) return;
            bufW.Write(RDeath); bufW.Write(k.InstanceID); bufW.Write(death.Id);
            records++;
            nextSend = 0; // promptly
        }

        private static void WriteColony(BinaryWriter w)
        {
            // research: what is finished, and the progress of what isn't
            if (Research.Instance != null)
            {
                var done = new List<string>();
                var progress = new List<KeyValuePair<string, Dictionary<string, float>>>();
                foreach (Tech t in Db.Get().Techs.resources)
                {
                    TechInstance ti = Research.Instance.Get(t);
                    if (ti == null) continue;
                    if (ti.IsComplete()) done.Add(t.Id);
                    else
                    {
                        bool any = false;
                        foreach (float p in ti.progressInventory.PointsByTypeID.Values) if (p > 0f) any = true;
                        if (any) progress.Add(new KeyValuePair<string, Dictionary<string, float>>(t.Id, ti.progressInventory.PointsByTypeID));
                    }
                }
                w.Write(RResearch);
                w.Write(done.Count); foreach (string id in done) w.Write(id);
                w.Write(progress.Count);
                foreach (var kv in progress)
                {
                    w.Write(kv.Key); w.Write(kv.Value.Count);
                    foreach (var p in kv.Value) { w.Write(p.Key); w.Write(p.Value); }
                }
                records++;
            }
            // experience (skill points)
            var dupes = Components.MinionResumes.Items;
            w.Write(RXp); w.Write(dupes.Count);
            foreach (MinionResume r in dupes)
            {
                var k = r != null ? r.GetComponent<KPrefabID>() : null;
                w.Write(k != null ? k.InstanceID : -1); w.Write(r != null ? r.TotalExperienceGained : 0f);
            }
            records++;
            if (GameClock.Instance != null)
            {
                w.Write(RClock); w.Write(GameClock.Instance.GetTime());
                records++;
            }
            if (Immigration.Instance != null)
            {
                w.Write(RImmigration); w.Write(Immigration.Instance.timeBeforeSpawn); w.Write(Immigration.Instance.ImmigrantsAvailable);
                records++;
            }
        }

        // ================================================================== guest

        public static long amountsApplied, GuestDeaths, TechsCompleted;
        public static float ClockCorrection;

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
                        int kind = r.BaseStream.ReadByte();
                        if (kind < 0) break;
                        switch ((byte)kind)
                        {
                            case RAmount: ApplyAmount(r.ReadInt32(), Inflate.ReadId(r), r.ReadSingle()); break;
                            case RDeath: ApplyDeath(r.ReadInt32(), Inflate.ReadId(r)); break;
                            case RResearch: ApplyResearch(r); break;
                            case RXp: ApplyXp(r); break;
                            case RClock: ApplyClock(r.ReadSingle()); break;
                            case RImmigration:
                            {
                                float t = r.ReadSingle(); bool available = r.ReadBoolean();
                                if (Immigration.Instance != null) { Immigration.Instance.timeBeforeSpawn = t; ImmigrantAvailable(Immigration.Instance) = available; }
                                break;
                            }
                            default: Log.Warn($"global: unknown record {kind}"); return;
                        }
                    }
                }
            }
            finally { Applying = false; }
        }

        private static void ApplyAmount(int id, string amountId, float value)
        {
            KPrefabID k = KPrefabIDTracker.Get().GetInstance(id);
            if (k == null) return;
            AmountInstance a = k.gameObject.GetAmounts()?.Get(amountId);
            if (a == null) return;
            a.SetValue(value);
            amountsApplied++;
        }

        private static void ApplyDeath(int id, string deathId)
        {
            KPrefabID k = KPrefabIDTracker.Get().GetInstance(id);
            Death death = Db.Get().Deaths.TryGet(deathId);
            var smi = k != null ? k.gameObject.GetSMI<DeathMonitor.Instance>() : null;
            if (smi == null || death == null || smi.IsDead()) return;
            smi.Kill(death);
            GuestDeaths++;
        }

        private static void ApplyResearch(BinaryReader r)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                TechInstance ti = Research.Instance?.Get(Db.Get().Techs.TryGet(Inflate.ReadId(r)));
                if (ti != null && !ti.IsComplete()) { ti.Purchased(); TechsCompleted++; }
            }
            int m = r.ReadInt32();
            for (int i = 0; i < m; i++)
            {
                TechInstance ti = Research.Instance?.Get(Db.Get().Techs.TryGet(Inflate.ReadId(r)));
                int c = r.ReadInt32();
                for (int j = 0; j < c; j++)
                {
                    string type = Inflate.ReadId(r); float p = r.ReadSingle();
                    if (ti != null) ti.progressInventory.PointsByTypeID[type] = p;
                }
            }
        }

        private static void ApplyXp(BinaryReader r)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32(); float xp = r.ReadSingle();
                var resume = KPrefabIDTracker.Get().GetInstance(id)?.GetComponent<MinionResume>();
                if (resume != null) Xp(resume) = xp;
            }
        }

        private static void ApplyClock(float hostTime)
        {
            GameClock c = GameClock.Instance;
            if (c == null) return;
            float d = hostTime - c.GetTime();
            // (the guest's clock shows the timeline's moment; that is a little behind and its delay moves a bit)
            if (Mathf.Abs(d) < 2f) return;
            if (d > 0f) AddTime?.Invoke(c, new object[] { d }); // moves cycles / day and night on like the game does
            else { ClockTime(c) += d; ClockCycleTime(c) = Mathf.Max(0f, ClockCycleTime(c) + d); }
            ClockCorrection += d;
        }

        public static string Report() =>
            $"global: host sent {HostRecords} records ({HostBytes / 1024} KB), guest set {amountsApplied} values, deaths {GuestDeaths}, techs completed {TechsCompleted}, clock corrected {ClockCorrection:F1} s";

        /// <summary>For the sync test: values that should match.</summary>
        public static string Dump()
        {
            var sb = new System.Text.StringBuilder();
            foreach (MinionIdentity m in Components.LiveMinionIdentities.Items)
            {
                if (m == null) continue;
                int id = m.GetComponent<KPrefabID>().InstanceID;
                foreach (string a in new[] { "Calories", "Breath", "Stress", "Bladder", "Stamina", "HitPoints" })
                {
                    AmountInstance ai = m.gameObject.GetAmounts()?.Get(a);
                    if (ai != null) sb.Append($"AMT {id} {a} {ai.value:F1} {ai.GetMax():F1}\n");
                }
                var resume = m.GetComponent<MinionResume>();
                if (resume != null) sb.Append($"XP {id} {resume.TotalExperienceGained:F0} {resume.AvailableSkillpoints}\n");
            }
            if (Research.Instance != null)
            {
                int done = 0;
                foreach (Tech t in Db.Get().Techs.resources) if (Research.Instance.Get(t)?.IsComplete() == true) done++;
                TechInstance active = Research.Instance.GetActiveResearch();
                sb.Append($"TECHS {done}\n");
                if (active != null) foreach (var p in active.progressInventory.PointsByTypeID) sb.Append($"PROG {active.tech.Id} {p.Key} {p.Value:F1}\n");
            }
            if (GameClock.Instance != null) sb.Append($"CLOCK {GameClock.Instance.GetCycle()} {GameClock.Instance.GetTimeSinceStartOfCycle():F0}\n");
            if (Immigration.Instance != null) sb.Append($"PRINT {Immigration.Instance.timeBeforeSpawn:F0}\n");
            return sb.ToString();
        }
    }

    /// <summary>Patched at the main menu (CoopMod.LatePatches), like everything that touches many game classes.</summary>
    internal static class GlobalSyncPatches
    {
        public static void Apply(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Constructor(typeof(AmountInstance), new[] { typeof(Amount), typeof(GameObject) }),
                    postfix: new HarmonyMethod(typeof(GlobalSyncPatches), nameof(Registered)));
                // guest: amounts don't run on their own (no starving, suffocating, aging...); the host's values come in
                h.Patch(AccessTools.Method(typeof(AmountInstance), nameof(AmountInstance.BatchUpdate)),
                    prefix: new HarmonyMethod(typeof(GlobalSyncPatches), nameof(HostOnly)));
                // guest: no experience of its own (its duplicants don't work); the host's comes in
                h.Patch(AccessTools.Method(typeof(MinionResume), nameof(MinionResume.AddExperience)),
                    prefix: new HarmonyMethod(typeof(GlobalSyncPatches), nameof(HostOnly)));
                h.Patch(AccessTools.Method(typeof(DeathMonitor.Instance), nameof(DeathMonitor.Instance.Kill)),
                    postfix: new HarmonyMethod(typeof(GlobalSyncPatches), nameof(Killed)));
            }
            catch (System.Exception e) { Log.Warn("global: patching failed: " + e.Message); }
        }

        private static void Registered(AmountInstance __instance) => GlobalSync.Register(__instance);
        private static bool HostOnly() => !Puppets.Mirroring || GlobalSync.Applying;
        private static void Killed(DeathMonitor.Instance __instance, Death __0) => GlobalSync.OnKilled(__instance.gameObject, __0);
    }
}
