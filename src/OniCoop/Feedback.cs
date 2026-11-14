namespace OniCoop
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;
    using UnityEngine.UI;

    /// <summary>The little signs of work a guest's game wouldn't make by itself: the progress bars over work (digging,
    /// building, research, a fabricator's order...) and the floating texts ("+5 kg", "built"...).
    ///
    /// The server's game makes them from duplicants' work, which only the server runs. It records every progress bar the
    /// game creates (ProgressBar.CreateProgressBar: whatever made it) and samples its fill; and every floating text the
    /// game asks for (PopFXManager.SpawnFX, before the server's own camera would cull it). Both go out on the
    /// duplicants' timeline, so a guest shows them at the same moment as the work animation. A guest's own code making
    /// these is kept from showing them (they would be its guesses, and double).</summary>
    internal static class Feedback
    {
        private const byte RBarNew = 1, RBar = 2, RBarGone = 3, RPop = 4;

        /// <summary>Guest: the mirror is making these right now.</summary>
        public static bool Applying;

        private static readonly AccessTools.FieldRef<ProgressBar, Func<float>> BarFunc = AccessTools.FieldRefAccess<ProgressBar, Func<float>>("updatePercentFull");
        private static readonly AccessTools.FieldRef<ProgressBar, bool> BarVisible = AccessTools.FieldRefAccess<ProgressBar, bool>("lastVisibilityValue");

        public static long HostBars, HostBarUpdates, HostPops, GuestBars, GuestPops, BlockedLocalBars, BlockedLocalPops, MissingSprites;

        private static readonly MemoryStream buf = new MemoryStream();
        private static readonly BinaryWriter bufW = new BinaryWriter(buf);
        private static int records;

        public static void Reset()
        {
            hostBars.Clear();
            buf.SetLength(0);
            records = 0;
            foreach (GuestBar g in guestBars.Values) if (g.bar != null) g.bar.gameObject.DeleteObject();
            guestBars.Clear();
            timeline.Clear();
            localBars.Clear();
        }

        // ================================================================== server

        private sealed class HostBar
        {
            public int id, entity;
            public ProgressBar bar;
            public Func<float> func;
            public float pct = -1f;
            public bool visible, sent;
        }

        /// <summary>Server: someone (re)entered the game: every bar is announced again.</summary>
        public static void OnGuestJoined()
        {
            foreach (HostBar h in hostBars) h.sent = false;
        }

        private static readonly List<HostBar> hostBars = new List<HostBar>();
        private static int nextBarId = 1;

        private static bool HostLive => CommandBus.Active && CommandBus.IsHost && EntitySync.GuestsInGame > 0;

        private static int IdOf(GameObject go)
        {
            if (go == null) return 0;
            KPrefabID k = go.GetComponent<KPrefabID>();
            return k != null && k.InstanceID > 0 && k.InstanceID < EntitySync.LocalIdBase ? k.InstanceID : 0;
        }

        private static void CreateBarPostfix(ProgressBar __result, GameObject entity, Vector3 offset)
        {
            if (__result == null) return;
            if (CommandBus.Active && CommandBus.IsHost)
            {
                int id = IdOf(entity);
                if (id == 0) return;
                var h = new HostBar { id = nextBarId++, entity = id, bar = __result, func = BarFunc(__result) };
                hostBars.Add(h);
                // the record of its creation goes out with its first sample (HostTick), when its colour is settled
                pendingOffset[h.id] = offset;
                return;
            }
            if (Puppets.Mirroring && !Applying)
            {
                // the guest's own code made one: it stays hidden (the server's bars are the ones shown)
                localBars.Add(__result);
                __result.SetVisibility(false);
                BlockedLocalBars++;
            }
        }

        private static readonly Dictionary<int, Vector3> pendingOffset = new Dictionary<int, Vector3>();

        /// <summary>Every server frame (bars that went away are forgotten even with nobody in the game).</summary>
        public static void HostTick()
        {
            float stamp = Puppets.Clock;
            for (int i = hostBars.Count - 1; i >= 0; i--)
            {
                HostBar h = hostBars[i];
                if (h.bar == null)
                {
                    if (h.sent) { bufW.Write(RBarGone); bufW.Write(stamp); bufW.Write(h.id); records++; }
                    hostBars.RemoveAt(i);
                    pendingOffset.Remove(h.id);
                    continue;
                }
                float pct = 0f;
                try { pct = h.func != null ? Mathf.Clamp01(h.func()) : h.bar.PercentFull; }
                catch (Exception) { }
                bool visible = BarVisible(h.bar);
                if (!h.sent)
                {
                    pendingOffset.TryGetValue(h.id, out Vector3 off);
                    pendingOffset.Remove(h.id);
                    Color c = h.bar.bar != null ? h.bar.bar.color : Color.white;
                    bufW.Write(RBarNew); bufW.Write(stamp); bufW.Write(h.id); bufW.Write(h.entity);
                    bufW.Write(off.x); bufW.Write(off.y); bufW.Write(c.r); bufW.Write(c.g); bufW.Write(c.b); bufW.Write(c.a);
                    bufW.Write(pct); bufW.Write(visible);
                    records++;
                    HostBars++;
                    h.sent = true; h.pct = pct; h.visible = visible;
                    continue;
                }
                if (visible == h.visible && Mathf.Abs(pct - h.pct) < 0.004f) continue;
                h.pct = pct; h.visible = visible;
                bufW.Write(RBar); bufW.Write(stamp); bufW.Write(h.id); bufW.Write(pct); bufW.Write(visible);
                records++;
                HostBarUpdates++;
            }
        }

        /// <summary>Server: a floating text is asked for (before the game's own camera culling: a guest culls with its own).</summary>
        private static bool SpawnFxPrefix(Sprite mainIcon, Sprite secondaryIcon, string text, Transform target_transform, Vector3 offset,
            float lifetime, bool selfAdjustPositionIfInGroup, bool track_target, bool force_spawn, ref PopFX __result)
        {
            if (CommandBus.Active && CommandBus.IsHost)
            {
                if (!HostLive) return true;
                int id = target_transform != null ? IdOf(target_transform.gameObject) : 0;
                Vector3 pos = offset + (target_transform != null ? target_transform.GetPosition() : Vector3.zero);
                bufW.Write(RPop); bufW.Write(Puppets.Clock);
                bufW.Write(SpriteDesc(mainIcon)); bufW.Write(SpriteDesc(secondaryIcon));
                bufW.Write(text ?? ""); bufW.Write(id);
                bufW.Write(id != 0 ? offset.x : pos.x); bufW.Write(id != 0 ? offset.y : pos.y);
                bufW.Write(lifetime);
                byte f = (byte)((selfAdjustPositionIfInGroup ? 1 : 0) | (track_target ? 2 : 0) | (force_spawn ? 4 : 0));
                bufW.Write(f);
                records++;
                HostPops++;
                return true;
            }
            if (Puppets.Mirroring && !Applying)
            {
                __result = null;
                BlockedLocalPops++;
                return false;
            }
            return true;
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

        private sealed class GuestBar { public ProgressBar bar; public float pct; }
        private static readonly Dictionary<int, GuestBar> guestBars = new Dictionary<int, GuestBar>();
        private static readonly HashSet<ProgressBar> localBars = new HashSet<ProgressBar>();

        private struct Pending
        {
            public byte kind;
            public float at, pct, x, y, lifetime;
            public int id, entity;
            public Color color;
            public bool visible;
            public byte flags;
            public string main, second, text;
        }

        private static readonly Queue<Pending> timeline = new Queue<Pending>();

        public static void ReadTimeline(BinaryReader r)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var p = new Pending { kind = r.ReadByte(), at = r.ReadSingle() };
                switch (p.kind)
                {
                    case RBarNew:
                        p.id = r.ReadInt32(); p.entity = r.ReadInt32(); p.x = r.ReadSingle(); p.y = r.ReadSingle();
                        p.color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        p.pct = r.ReadSingle(); p.visible = r.ReadBoolean();
                        break;
                    case RBar: p.id = r.ReadInt32(); p.pct = r.ReadSingle(); p.visible = r.ReadBoolean(); break;
                    case RBarGone: p.id = r.ReadInt32(); break;
                    case RPop:
                        p.main = Inflate.ReadId(r); p.second = Inflate.ReadId(r); p.text = Inflate.ReadId(r);
                        p.entity = r.ReadInt32(); p.x = r.ReadSingle(); p.y = r.ReadSingle(); p.lifetime = r.ReadSingle(); p.flags = r.ReadByte();
                        break;
                    default: throw new InvalidDataException("feedback: unknown record " + p.kind);
                }
                timeline.Enqueue(p);
            }
            if (Puppets.RenderTime < 0f) ApplyTimeline(float.PositiveInfinity);
        }

        /// <summary>Guest, every frame after the puppets are placed: what the timeline reached.</summary>
        public static void ApplyTimeline(float renderTime)
        {
            if (timeline.Count == 0) return;
            Applying = true;
            try
            {
                while (timeline.Count > 0)
                {
                    Pending p = timeline.Peek();
                    if (p.at > renderTime && p.at < renderTime + 10f) break;
                    timeline.Dequeue();
                    try { Apply(p); }
                    catch (Exception e) { Log.Warn($"feedback: record {p.kind} failed: {e.Message}"); }
                }
            }
            finally { Applying = false; }
        }

        private static void Apply(Pending p)
        {
            switch (p.kind)
            {
                case RBarNew:
                {
                    GameObject go = KPrefabIDTracker.Get().GetInstance(p.entity)?.gameObject;
                    if (go == null) return;
                    if (guestBars.TryGetValue(p.id, out GuestBar old) && old.bar != null) old.bar.gameObject.DeleteObject();
                    var g = new GuestBar { pct = p.pct };
                    g.bar = ProgressBar.CreateProgressBar(go, () => g.pct, new Vector3(p.x, p.y, 0f));
                    if (g.bar.bar != null && p.color.a >= 0f) g.bar.bar.color = p.color;
                    g.bar.SetVisibility(p.visible);
                    guestBars[p.id] = g;
                    GuestBars++;
                    break;
                }
                case RBar:
                    if (guestBars.TryGetValue(p.id, out GuestBar b) && b.bar != null)
                    {
                        b.pct = p.pct;
                        b.bar.SetVisibility(p.visible);
                    }
                    break;
                case RBarGone:
                    if (guestBars.TryGetValue(p.id, out GuestBar gone))
                    {
                        if (gone.bar != null) gone.bar.gameObject.DeleteObject();
                        guestBars.Remove(p.id);
                    }
                    break;
                case RPop:
                {
                    if (PopFXManager.Instance == null || !PopFXManager.Instance.Ready()) return;
                    Transform target = null;
                    if (p.entity != 0) target = KPrefabIDTracker.Get().GetInstance(p.entity)?.transform;
                    var offset = new Vector3(p.x, p.y, 0f);
                    if (p.entity != 0 && target == null) return; // (gone already)
                    PopFXManager.Instance.SpawnFX(SpriteOf(p.main), SpriteOf(p.second), p.text, target, offset, p.lifetime,
                        (p.flags & 1) != 0, (p.flags & 2) != 0, (p.flags & 4) != 0);
                    GuestPops++;
                    break;
                }
            }
        }

        private static Dictionary<global::Tuple<KAnimFile, string, bool>, Sprite> KnownUISprites() =>
            typeof(Def).GetField("knownUISprites", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                as Dictionary<global::Tuple<KAnimFile, string, bool>, Sprite>;
        private static readonly Dictionary<Sprite, string> spriteDescs = new Dictionary<Sprite, string>();

        /// <summary>Server: how a guest finds this icon. Item icons are made at run time from the item's animation
        /// (Def.GetUISprite): those go as "anim:file:anim:centered" and a guest makes its own the same way.</summary>
        private static string SpriteDesc(Sprite s)
        {
            if (s == null) return "";
            if (spriteDescs.TryGetValue(s, out string d)) return d;
            d = s.name;
            try
            {
                var known = KnownUISprites();
                if (known != null) foreach (var kv in known)
                    if (kv.Value == s && kv.Key.first != null) { d = $"anim:{kv.Key.first.name}:{kv.Key.second}:{kv.Key.third}"; break; }
            }
            catch (Exception) { }
            spriteDescs[s] = d;
            return d;
        }

        private static Dictionary<string, Sprite> managerSprites;

        private static Sprite SpriteOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.StartsWith("anim:", StringComparison.Ordinal))
            {
                string[] p = name.Split(':');
                if (p.Length == 4)
                {
                    KAnimFile file = Assets.GetAnim(p[1]);
                    if (file != null) return Def.GetUISpriteFromMultiObjectAnim(file, p[2], p[3] == "True");
                }
            }
            if (managerSprites == null && PopFXManager.Instance != null)
            {
                managerSprites = new Dictionary<string, Sprite>();
                foreach (FieldInfo f in typeof(PopFXManager).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (f.FieldType == typeof(Sprite) && f.GetValue(PopFXManager.Instance) is Sprite s && s != null && !managerSprites.ContainsKey(s.name))
                        managerSprites[s.name] = s;
            }
            if (managerSprites != null && managerSprites.TryGetValue(name, out Sprite m)) return m;
            Sprite a = Assets.GetSprite(name);
            if (a == null && MissingSprites++ < 10) Log.Info($"feedback: no sprite '{name}' here");
            return a;
        }

        /// <summary>Guest: a local bar asked to show stays hidden.</summary>
        private static void SetVisibilityPrefix(ProgressBar __instance, ref bool visible)
        {
            if (visible && localBars.Count > 0 && Puppets.Mirroring && !Applying && localBars.Contains(__instance)) visible = false;
        }

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ProgressBar), nameof(ProgressBar.CreateProgressBar), new[] { typeof(GameObject), typeof(Func<float>), typeof(Vector3) }),
                    postfix: new HarmonyMethod(typeof(Feedback), nameof(CreateBarPostfix)));
                h.Patch(AccessTools.Method(typeof(ProgressBar), nameof(ProgressBar.SetVisibility)), prefix: new HarmonyMethod(typeof(Feedback), nameof(SetVisibilityPrefix)));
                h.Patch(AccessTools.Method(typeof(PopFXManager), nameof(PopFXManager.SpawnFX),
                        new[] { typeof(Sprite), typeof(Sprite), typeof(string), typeof(Transform), typeof(Vector3), typeof(float), typeof(bool), typeof(bool), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(Feedback), nameof(SpawnFxPrefix)));
            }
            catch (Exception e) { Log.Warn("feedback: can't patch: " + e.Message); }
        }

        public static string Report(bool host) => host
            ? $"feedback: {HostBars} progress bars ({HostBarUpdates} updates), {HostPops} floating texts"
            : $"feedback: {GuestBars} progress bars shown, {GuestPops} floating texts; own ones kept hidden: {BlockedLocalBars} bars, {BlockedLocalPops} texts; sprites not found {MissingSprites}";
    }
}
