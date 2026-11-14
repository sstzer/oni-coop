namespace OniCoop
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>The cycle report (the daily report screen: oxygen made, calories eaten, stress, power...) is a sum of
    /// ReportValue calls made by the game's logic as it runs. A guest runs little of that logic, so its own report was
    /// mostly empty. Every call adds into the report (totals, positive and negative parts, and per-note sums), so the
    /// server sums the calls per (day, type, note, context, sign) for two seconds and sends the sums; a guest adds them
    /// through the game's own DailyReport.AddData*, and its own ReportValue calls are dropped. When a guest joins, it
    /// gets today's report as a whole (what happened between the save it loaded and now).</summary>
    internal static class ReportSync
    {
        public const byte Ch = 17;
        private const byte RSums = 1, RToday = 2;
        private const byte KText = 0, KObject = 1;

        private static bool HostActive => CommandBus.Active && CommandBus.IsHost && EntitySync.GuestsInGame > 0;

        private struct Key : IEquatable<Key>
        {
            public int day, type, obj;
            public byte kind;
            public bool positive, minion;
            public string note, context; // context: the text context, or the object's name (for a guest without it)

            public bool Equals(Key o) => day == o.day && type == o.type && obj == o.obj && kind == o.kind && positive == o.positive
                                         && note == o.note && context == o.context;
            public override bool Equals(object o) => o is Key k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = day * 397 ^ type * 31 ^ obj * 17 ^ kind ^ (positive ? 1 << 20 : 0);
                    if (note != null) h = h * 31 + note.GetHashCode();
                    if (context != null) h = h * 31 + context.GetHashCode();
                    return h;
                }
            }
        }

        // ================================================================== server

        private static readonly Dictionary<Key, float> sums = new Dictionary<Key, float>();
        private static float nextSend;
        public static long Calls, SentSums, SentBytes, FullsSent;

        private static void Record(ReportManager.ReportType type, float value, string note, byte kind, string context, KPrefabID obj)
        {
            if (value == 0f || ReportManager.Instance == null || ReportManager.Instance.TodaysReport == null) return;
            var k = new Key
            {
                day = ReportManager.Instance.TodaysReport.day, type = (int)type, kind = kind, positive = value > 0f, note = note,
                context = context, obj = obj != null ? obj.InstanceID : 0, minion = obj != null && obj.HasTag(GameTags.BaseMinion),
            };
            sums.TryGetValue(k, out float v);
            sums[k] = v + value;
            Calls++;
        }

        public static void HostTick(float now)
        {
            if (now < nextSend) return;
            nextSend = now + 2f;
            SendSums();
        }

        private static void SendSums()
        {
            if (sums.Count == 0) return;
            var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(z))
            {
                w.Write(RSums);
                w.Write(sums.Count);
                foreach (var kv in sums)
                {
                    Key k = kv.Key;
                    w.Write(k.day); w.Write(k.type); w.Write(k.kind); w.Write(kv.Value);
                    // names of things by reference: a guest files them under its own language's name (TextParts)
                    w.Write(k.note != null); if (k.note != null) w.Write(TextParts.EncodeName(k.note));
                    w.Write(k.context != null); if (k.context != null) w.Write(TextParts.EncodeName(k.context));
                    if (k.kind == KObject) { w.Write(k.obj); w.Write(k.minion); }
                }
            }
            SentSums += sums.Count;
            sums.Clear();
            SentBytes += ms.Length;
            CommandBus.Client.SendRelay(Proto.TargetAll, Ch, ms.ToArray());
        }

        /// <summary>A guest joined: what's summed so far goes out, then today's report as a whole (every guest takes
        /// it: it's the server's report as it is now).</summary>
        public static void OnGuestJoined()
        {
            if (!CommandBus.Active || !CommandBus.IsHost) return;
            SendSums();
            ReportManager rm = ReportManager.Instance;
            if (rm == null || rm.TodaysReport == null) return;
            var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(z))
            {
                w.Write(RToday);
                ReportManager.DailyReport d = rm.TodaysReport;
                w.Write(d.day);
                w.Write(d.reportEntries.Count);
                foreach (ReportManager.ReportEntry e in d.reportEntries)
                {
                    WriteEntry(w, e);
                    int children = e.contextEntries.Count;
                    w.Write(children);
                    for (int i = 0; i < children; i++) WriteEntry(w, e.contextEntries[i]);
                }
            }
            FullsSent++;
            SentBytes += ms.Length;
            CommandBus.Client.SendRelay(Proto.TargetAll, Ch, ms.ToArray());
        }

        private static void WriteEntry(BinaryWriter w, ReportManager.ReportEntry e)
        {
            w.Write((int)e.reportType);
            w.Write(e.context != null); if (e.context != null) w.Write(TextParts.EncodeName(e.context));
            w.Write(e.contextId);
            w.Write(e.accumulate); w.Write(e.accPositive); w.Write(e.accNegative);
            var notes = new List<ReportManager.ReportEntry.Note>();
            e.IterateNotes(n => notes.Add(n));
            w.Write(notes.Count);
            foreach (ReportManager.ReportEntry.Note n in notes) { w.Write(TextParts.EncodeName(n.note ?? "")); w.Write(n.value); }
        }

        public static void FlushNow()
        {
            if (HostActive) SendSums();
        }

        // ================================================================== guest

        private static bool applying;
        public static long Applied, Late, Dropped, Rebuilt;

        private sealed class Sum
        {
            public int day, type, obj;
            public byte kind;
            public bool minion;
            public float value;
            public string note, context;
        }

        private static readonly List<Sum> pending = new List<Sum>(); // for a day this guest hasn't reached yet

        private static readonly AccessTools.FieldRef<ReportManager, ReportManager.NoteStorage> Notes =
            AccessTools.FieldRefAccess<ReportManager, ReportManager.NoteStorage>("noteStorage");
        private static readonly AccessTools.FieldRef<ReportManager, ReportManager.DailyReport> Today =
            AccessTools.FieldRefAccess<ReportManager, ReportManager.DailyReport>("todaysReport");

        public static void OnRecords(byte[] data)
        {
            if (!Puppets.Mirroring || ReportManager.Instance == null) return;
            using (var r = Inflate.Open(data)) // (the shared reader: no garbage per message; using is harmless)
            {
                byte kind = r.ReadByte();
                if (kind == RToday) { ReadToday(r); return; }
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var s = new Sum { day = r.ReadInt32(), type = r.ReadInt32(), kind = r.ReadByte(), value = r.ReadSingle() };
                    if (r.ReadBoolean()) s.note = TextParts.DecodeName(Inflate.ReadId(r));
                    if (r.ReadBoolean()) s.context = TextParts.DecodeName(Inflate.ReadId(r));
                    if (s.kind == KObject) { s.obj = r.ReadInt32(); s.minion = r.ReadBoolean(); }
                    if (!Apply(s)) pending.Add(s);
                }
            }
        }

        /// <summary>Add one sum to the report of its day; false when this guest hasn't started that day yet.</summary>
        private static bool Apply(Sum s)
        {
            ReportManager rm = ReportManager.Instance;
            ReportManager.DailyReport d = rm.FindReport(s.day);
            if (d == null)
            {
                if (rm.TodaysReport != null && s.day > rm.TodaysReport.day) { Late++; return false; }
                Dropped++;
                return true;
            }
            var type = (ReportManager.ReportType)s.type;
            applying = true;
            try
            {
                if (s.kind == KObject && s.minion) AddForMinion(d, type, s);
                else d.AddData(type, s.value, s.note, s.context); // anything else is filed under its name (the server's)
            }
            finally { applying = false; }
            Applied++;
            return true;
        }

        private static readonly Action<ReportManager.ReportEntry, ReportManager.NoteStorage, float, string> AddActual =
            AccessTools.MethodDelegate<Action<ReportManager.ReportEntry, ReportManager.NoteStorage, float, string>>(
                AccessTools.Method(typeof(ReportManager.ReportEntry), "AddActualData"));

        /// <summary>A duplicant's share goes to its own line, found the way the game finds it (by id, else an unclaimed
        /// line with its name), whether or not this guest has that duplicant as one right now (e.g. stored in a rocket).</summary>
        private static void AddForMinion(ReportManager.DailyReport d, ReportManager.ReportType type, Sum s)
        {
            ReportManager.NoteStorage notes = Notes(ReportManager.Instance);
            ReportManager.ReportEntry entry = d.GetEntry(type);
            AddActual(entry, notes, s.value, s.note);
            ReportManager.ReportEntry child = null;
            for (int i = 0; i < entry.contextEntries.Count && child == null; i++)
                if (entry.contextEntries[i].contextId == s.obj) child = entry.contextEntries[i];
            for (int i = 0; i < entry.contextEntries.Count && child == null; i++)
            {
                ReportManager.ReportEntry c = entry.contextEntries[i];
                if (c.contextId == 0 && c.context == s.context) { c.contextId = s.obj; child = c; }
            }
            if (child == null)
            {
                child = new ReportManager.ReportEntry(type, notes.GetNewNoteId(), s.context, s.obj, is_child: true);
                entry.contextEntries.Add(child);
            }
            AddActual(child, notes, s.value, s.note);
        }

        private static float nextRetry;

        public static void GuestTick(float now)
        {
            if (pending.Count == 0 || now < nextRetry) return;
            nextRetry = now + 1f;
            for (int i = 0; i < pending.Count; i++)
                if (Apply(pending[i])) pending.RemoveAt(i--);
        }

        /// <summary>Today's report as the server has it: replaces this guest's report of that day.</summary>
        private static void ReadToday(BinaryReader r)
        {
            ReportManager rm = ReportManager.Instance;
            ReportManager.NoteStorage notes = Notes(rm);
            int day = r.ReadInt32();
            var report = new ReportManager.DailyReport(rm) { day = day };
            report.reportEntries.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                ReportManager.ReportEntry e = ReadEntry(r, notes, child: false);
                int children = r.ReadInt32();
                for (int j = 0; j < children; j++) e.contextEntries.Add(ReadEntry(r, notes, child: true));
                report.reportEntries.Add(e);
            }
            if (rm.TodaysReport != null && rm.TodaysReport.day == day) Today(rm) = report;
            else
            {
                int at = rm.reports.FindIndex(x => x.day == day);
                if (at >= 0) rm.reports[at] = report;
                else if (rm.TodaysReport == null || day > rm.TodaysReport.day) Today(rm) = report; // this guest is a bit behind
            }
            Rebuilt++;
            pending.RemoveAll(s => s.day < day);
        }

        private static ReportManager.ReportEntry ReadEntry(BinaryReader r, ReportManager.NoteStorage notes, bool child)
        {
            var type = (ReportManager.ReportType)r.ReadInt32();
            string context = r.ReadBoolean() ? TextParts.DecodeName(Inflate.ReadId(r)) : null;
            int contextId = r.ReadInt32();
            var e = new ReportManager.ReportEntry(type, notes.GetNewNoteId(), context, contextId, child)
            {
                accumulate = r.ReadSingle(), accPositive = r.ReadSingle(), accNegative = r.ReadSingle(),
            };
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string note = TextParts.DecodeName(Inflate.ReadId(r));
                float value = r.ReadSingle();
                notes.Add(e.noteStorageId, value, note);
            }
            return e;
        }

        public static void Reset()
        {
            sums.Clear();
            pending.Clear();
            nextSend = 0f;
        }

        public static string Report() =>
            $"report: server summed {Calls} calls, sent {SentSums} sums ({SentBytes / 1024} KB), today's report as a whole {FullsSent}x; " +
            $"guest added {Applied} sums ({Late} early for its day, {Dropped} for days it doesn't have, {pending.Count} waiting), rebuilt today's {Rebuilt}x";

        /// <summary>For the sync test: today's report and the last finished one, entry by entry.</summary>
        public static string Dump()
        {
            var sb = new System.Text.StringBuilder();
            ReportManager rm = ReportManager.Instance;
            if (rm == null) return "";
            void One(string label, ReportManager.DailyReport d)
            {
                if (d == null) return;
                foreach (ReportManager.ReportEntry e in d.reportEntries)
                {
                    sb.Append(label).Append(' ').Append(d.day).Append(' ').Append(e.reportType).Append(' ')
                      .Append(e.accPositive.ToString("0.###")).Append(' ').Append(e.accNegative.ToString("0.###")).Append(' ')
                      .Append(e.contextEntries.Count).Append('\n');
                }
            }
            One("T", rm.TodaysReport);
            One("Y", rm.YesterdaysReport);
            return sb.ToString();
        }

        // ================================================================== patches (main menu)

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                Type t = typeof(ReportManager);
                h.Patch(AccessTools.Method(t, nameof(ReportManager.ReportValue)), prefix: new HarmonyMethod(typeof(ReportSync), nameof(ValuePrefix)));
                h.Patch(AccessTools.Method(t, nameof(ReportManager.ReportValueWithPrefabInstanceContext)), prefix: new HarmonyMethod(typeof(ReportSync), nameof(ValueInstancePrefix)));
                h.Patch(AccessTools.Method(t, nameof(ReportManager.ReportValueWithGameObjectContext)), prefix: new HarmonyMethod(typeof(ReportSync), nameof(ValueObjectPrefix)));
            }
            catch (Exception e) { Log.Warn("report: patching failed: " + e.Message); }
        }

        /// <summary>Server: count it (and let it happen). Guest: the server's sums are the report, not this.</summary>
        private static bool Allow() => !Puppets.Mirroring || applying;

        private static bool ValuePrefix(ReportManager.ReportType __0, float __1, string __2, string __3)
        {
            if (HostActive) Record(__0, __1, __2, KText, __3, null);
            return Allow();
        }

        private static bool ValueInstancePrefix(ReportManager.ReportType __0, float __1, KPrefabID __2, string __3)
        {
            if (HostActive)
            {
                if (__2 != null) Record(__0, __1, __3, KObject, __2.gameObject.GetProperName(), __2);
                else Record(__0, __1, __3, KText, null, null);
            }
            return Allow();
        }

        private static bool ValueObjectPrefix(ReportManager.ReportType __0, float __1, GameObject __2, string __3)
        {
            if (HostActive)
            {
                KPrefabID k = __2 != null ? __2.GetComponent<KPrefabID>() : null;
                if (k != null) Record(__0, __1, __3, KObject, __2.GetProperName(), k);
                else Record(__0, __1, __3, KText, __2 != null ? __2.GetProperName() : null, null);
            }
            return Allow();
        }
    }
}
