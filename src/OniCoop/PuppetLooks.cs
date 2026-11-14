namespace OniCoop
{
    using System.Collections.Generic;
    using System.IO;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>What a duplicant or critter looks like beyond its animation, decided on the server and shown the same on
    /// every guest. A guest runs no chores, so nothing there would put the tool in a duplicant's hand (SnapOn), aim the
    /// beam (AnimEventHandler context + target, which the LaserEffect children follow), spawn the hit sparks at the
    /// target (MultitoolController), make the face (FaceGraph expressions) or swap the parts a chore swaps.
    ///
    /// The server watches, per puppet: the symbol overrides (SymbolOverrideController: clothes, hats, tools, helmets,
    /// a bee's ore...), the hidden symbols, the face's expressions, the work context / target and the hit effect. A
    /// change rides on the next keyframe as a delta, so it shows at the same moment of the timeline as the animation it
    /// belongs to. Every keyframe of the regular full refresh (2 s) carries a hash of the whole state; a guest whose own
    /// copy hashes differently (it joined later, the puppet arrived after its first keyframes, keyframes were cut)
    /// asks for that puppet in full.
    ///
    /// Blinking and the talking mouth (eyes / mouth overrides at priority 3) stay local: they are many tiny, random
    /// changes, and the guest runs those monitors itself.</summary>
    internal static class PuppetLooks
    {
        public const byte ChAsk = 20; // guest -> server: puppets whose looks it needs in full

        private const byte POv = 1, PHidden = 2, PExpr = 4, PWork = 8, PFull = 16, PHash = 32, PThought = 64;
        private const int FacePriority = 3;
        private static readonly int EyesHash = new HashedString("snapto_eyes").HashValue, MouthHash = new HashedString("snapto_mouth").HashValue;

        private static readonly AccessTools.FieldRef<SymbolOverrideController, List<SymbolOverrideController.SymbolEntry>> SocList =
            AccessTools.FieldRefAccess<SymbolOverrideController, List<SymbolOverrideController.SymbolEntry>>("symbolOverrides");
        private static readonly AccessTools.FieldRef<KAnimControllerBase, HashSet<KAnimHashedString>> HiddenSet =
            AccessTools.FieldRefAccess<KAnimControllerBase, HashSet<KAnimHashedString>>("hiddenSymbolsSet");
        private static readonly AccessTools.FieldRef<FaceGraph, List<Expression>> FaceList =
            AccessTools.FieldRefAccess<FaceGraph, List<Expression>>("expressions");
        private static readonly AccessTools.FieldRef<ThoughtGraph.Instance, List<Thought>> ThoughtList =
            AccessTools.FieldRefAccess<ThoughtGraph.Instance, List<Thought>>("thoughts");

        // ================================================================== thoughts (the bubbles over duplicants' heads)

        /// <summary>A thought as it travels: one of the game's (Db) by id, or one made on the spot (a conversation topic)
        /// by what it shows.</summary>
        internal sealed class ThoughtDesc
        {
            public string id, sprite, mode, sound, bubble, speech, hoverKey, hoverText;
            public bool db, immediate;
            public float showTime;
            public int priority;

            public int Key => new HashedString(id).HashValue;

            public static ThoughtDesc Of(Thought t)
            {
                var d = new ThoughtDesc { id = t.Id, immediate = t.showImmediately, showTime = t.showTime, priority = t.priority };
                d.db = Db.Get().Thoughts.TryGet(t.Id) == t;
                if (d.db) return d;
                d.sprite = t.sprite != null ? t.sprite.name : ""; d.mode = t.modeSprite != null ? t.modeSprite.name : "";
                d.sound = t.sound ?? ""; d.bubble = t.bubbleSprite != null ? t.bubbleSprite.name : ""; d.speech = t.speechPrefix ?? "";
                d.hoverKey = t.hoverText != null ? t.hoverText.key.String ?? "" : ""; d.hoverText = t.hoverText != null ? t.hoverText.text ?? "" : "";
                return d;
            }

            public void Write(BinaryWriter w)
            {
                w.Write(id); w.Write(db);
                if (db) return;
                w.Write(sprite); w.Write(mode); w.Write(sound); w.Write(bubble); w.Write(speech); w.Write(hoverKey); w.Write(hoverText);
                w.Write(immediate); w.Write(showTime); w.Write(priority);
            }

            public static ThoughtDesc Read(BinaryReader r)
            {
                var d = new ThoughtDesc { id = Inflate.ReadId(r), db = r.ReadBoolean() };
                if (d.db) return d;
                d.sprite = Inflate.ReadId(r); d.mode = Inflate.ReadId(r); d.sound = Inflate.ReadId(r); d.bubble = Inflate.ReadId(r);
                d.speech = Inflate.ReadId(r); d.hoverKey = Inflate.ReadId(r); d.hoverText = Inflate.ReadId(r);
                d.immediate = r.ReadBoolean(); d.showTime = r.ReadSingle(); d.priority = r.ReadInt32();
                return d;
            }
        }

        private static readonly Dictionary<string, Thought> madeThoughts = new Dictionary<string, Thought>();
        public static long MissingThoughts;

        /// <summary>Guest: the thought object for a description (the game's own, or one made here like the server made it).</summary>
        private static Thought ThoughtOf(ThoughtDesc d)
        {
            if (d.db) { Thought t = Db.Get().Thoughts.TryGet(d.id); if (t == null) MissingThoughts++; return t; }
            if (madeThoughts.TryGetValue(d.id, out Thought m)) return m;
            Sprite icon = d.sprite.Length > 0 ? Assets.GetSprite(d.sprite) : null;
            if (icon == null && d.id.StartsWith("Topic_", System.StringComparison.Ordinal))
            {
                try { icon = Def.GetUISprite(d.id.Substring(6), "ui", true)?.first; } catch (System.Exception) { }
            }
            var hover = new LocString(d.hoverText, d.hoverKey);
            m = new Thought(d.id, null, icon, d.mode.Length > 0 ? d.mode : null, d.sound, d.bubble, d.speech, hover, d.immediate, d.showTime) { priority = d.priority };
            madeThoughts[d.id] = m;
            return m;
        }

        /// <summary>Server: thoughts that show at once (a conversation's next line) leave the list the moment they show,
        /// so they are caught as they are added.</summary>
        private static readonly Dictionary<GameObject, List<Thought>> instantThoughts = new Dictionary<GameObject, List<Thought>>();

        private static void AddThoughtPostfix(ThoughtGraph.Instance __instance, Thought thought)
        {
            if (!CommandBus.Active || !CommandBus.IsHost || thought == null || !thought.showImmediately || __instance == null) return;
            GameObject go = __instance.gameObject;
            if (go == null) return;
            if (!instantThoughts.TryGetValue(go, out List<Thought> l)) instantThoughts[go] = l = new List<Thought>(2);
            if (l.Count < 8) l.Add(thought);
        }

        private static bool ThoughtPrefix(ThoughtGraph.Instance __instance)
        {
            if (!Puppets.Mirroring || Puppets.Applying || __instance == null || !Puppets.IsPuppet(__instance.AnimController)) return true;
            BlockedLocal++;
            return false;
        }

        public static long HostDeltas, HostFulls, GuestDeltas, GuestFulls, Asked, HashMismatches, MissingSymbols, MissingExpressions, HitEffects;

        internal struct Ov { public int target, src, batch, prio; }

        private static long Key(int target, int prio) => ((long)target << 32) | (uint)prio;
        private static bool IsFace(int target, int prio) => prio == FacePriority && (target == EyesHash || target == MouthHash);

        private static int Mix(int h)
        {
            unchecked
            {
                uint x = (uint)h;
                x ^= x >> 16; x *= 0x7feb352dU; x ^= x >> 15; x *= 0x846ca68bU; x ^= x >> 16;
                return (int)x;
            }
        }

        private static int Mix(int a, int b) => Mix(unchecked(a * -1640531535 + b));

        /// <summary>The looks a guest should show: what the server last sent (the same on both sides).</summary>
        internal sealed class State
        {
            public readonly Dictionary<long, Ov> ov = new Dictionary<long, Ov>();
            public readonly HashSet<int> hidden = new HashSet<int>();
            public readonly List<int> expr = new List<int>();
            public readonly List<int> thoughts = new List<int>();       // the lasting ones (keys)
            public readonly List<ThoughtDesc> thoughtDescs = new List<ThoughtDesc>();
            public int exprOverride, ctx, hit, laser, mine;
            public Vector2 target;

            public int Hash()
            {
                int h = 0;
                unchecked
                {
                    foreach (Ov o in ov.Values) h += Mix(Mix(Mix(o.target, o.src), o.batch), o.prio);
                    foreach (int s in hidden) h += Mix(s, 0x2545f491);
                    foreach (int e in expr) h += Mix(e, 0x1b873593);
                    foreach (int t in thoughts) h += Mix(t, 0x3c6ef372);
                    h = Mix(h, exprOverride);
                    h = Mix(h, ctx);
                    h = Mix(h, hit);
                    h = Mix(h, laser);
                    h = Mix(h, mine);
                    h = Mix(h, Mathf.RoundToInt(target.x * 100f));
                    h = Mix(h, Mathf.RoundToInt(target.y * 100f));
                }
                return h;
            }

            public void Clear()
            {
                ov.Clear(); hidden.Clear(); expr.Clear(); thoughts.Clear(); thoughtDescs.Clear();
                exprOverride = ctx = hit = laser = mine = 0;
                target = Vector2.zero;
            }
        }

        /// <summary>One change, as it travels on a keyframe.</summary>
        internal sealed class Delta
        {
            public byte parts;
            // (made when needed: most deltas carry only one kind of change, and every 2 s each puppet sends a hash)
            private List<Ov> _ovSet;
            private List<long> _ovRemove;
            private List<int> _hidAdd, _hidRemove, _expr;
            public List<ThoughtDesc> thoughts, instant;     // PThought: the lasting list, and ones shown at once
            public List<Ov> ovSet => _ovSet ?? (_ovSet = new List<Ov>());
            public List<long> ovRemove => _ovRemove ?? (_ovRemove = new List<long>());
            public List<int> hidAdd => _hidAdd ?? (_hidAdd = new List<int>());
            public List<int> hidRemove => _hidRemove ?? (_hidRemove = new List<int>());
            public List<int> expr => _expr ?? (_expr = new List<int>());
            public int OvChanges => (_ovSet?.Count ?? 0) + (_ovRemove?.Count ?? 0);
            public int HiddenChanges => (_hidAdd?.Count ?? 0) + (_hidRemove?.Count ?? 0);
            public int exprOverride, ctx, hit, laser, mine, hash;
            public Vector2 target;
        }

        public static bool HasChange(Delta d) => d != null && (d.parts & ~PHash) != 0;

        public static void Write(BinaryWriter w, Delta d)
        {
            w.Write(d.parts);
            if ((d.parts & POv) != 0)
            {
                w.Write((ushort)d.ovRemove.Count);
                foreach (long k in d.ovRemove) { w.Write((int)(k >> 32)); w.Write((int)(k & 0xffffffffL)); }
                w.Write((ushort)d.ovSet.Count);
                foreach (Ov o in d.ovSet) { w.Write(o.target); w.Write(o.src); w.Write(o.batch); w.Write(o.prio); }
            }
            if ((d.parts & PHidden) != 0)
            {
                w.Write((ushort)d.hidRemove.Count);
                foreach (int s in d.hidRemove) w.Write(s);
                w.Write((ushort)d.hidAdd.Count);
                foreach (int s in d.hidAdd) w.Write(s);
            }
            if ((d.parts & PExpr) != 0)
            {
                w.Write((byte)d.expr.Count);
                foreach (int e in d.expr) w.Write(e);
                w.Write(d.exprOverride);
            }
            if ((d.parts & PWork) != 0) { w.Write(d.ctx); w.Write(d.target.x); w.Write(d.target.y); w.Write(d.hit); w.Write((byte)d.laser); w.Write(d.mine); }
            if ((d.parts & PThought) != 0)
            {
                w.Write((byte)(d.thoughts?.Count ?? 0));
                if (d.thoughts != null) foreach (ThoughtDesc t in d.thoughts) t.Write(w);
                w.Write((byte)(d.instant?.Count ?? 0));
                if (d.instant != null) foreach (ThoughtDesc t in d.instant) t.Write(w);
            }
            if ((d.parts & PHash) != 0) w.Write(d.hash);
        }

        public static Delta Read(BinaryReader r)
        {
            var d = new Delta { parts = r.ReadByte() };
            if ((d.parts & POv) != 0)
            {
                int n = r.ReadUInt16();
                for (int i = 0; i < n; i++) d.ovRemove.Add(Key(r.ReadInt32(), r.ReadInt32()));
                n = r.ReadUInt16();
                for (int i = 0; i < n; i++) d.ovSet.Add(new Ov { target = r.ReadInt32(), src = r.ReadInt32(), batch = r.ReadInt32(), prio = r.ReadInt32() });
            }
            if ((d.parts & PHidden) != 0)
            {
                int n = r.ReadUInt16();
                for (int i = 0; i < n; i++) d.hidRemove.Add(r.ReadInt32());
                n = r.ReadUInt16();
                for (int i = 0; i < n; i++) d.hidAdd.Add(r.ReadInt32());
            }
            if ((d.parts & PExpr) != 0)
            {
                int n = r.ReadByte();
                for (int i = 0; i < n; i++) d.expr.Add(r.ReadInt32());
                d.exprOverride = r.ReadInt32();
            }
            if ((d.parts & PWork) != 0) { d.ctx = r.ReadInt32(); d.target = new Vector2(r.ReadSingle(), r.ReadSingle()); d.hit = r.ReadInt32(); d.laser = r.ReadByte(); d.mine = r.ReadInt32(); }
            if ((d.parts & PThought) != 0)
            {
                int n = r.ReadByte();
                d.thoughts = new List<ThoughtDesc>(n);
                for (int i = 0; i < n; i++) d.thoughts.Add(ThoughtDesc.Read(r));
                n = r.ReadByte();
                if (n > 0) { d.instant = new List<ThoughtDesc>(n); for (int i = 0; i < n; i++) d.instant.Add(ThoughtDesc.Read(r)); }
            }
            if ((d.parts & PHash) != 0) d.hash = r.ReadInt32();
            return d;
        }

        // ================================================================== expressions by id

        private static Dictionary<int, Expression> exprByHash;
        private static readonly Dictionary<Expression, int> hashOfExpr = new Dictionary<Expression, int>();

        private static int ExprHash(Expression e)
        {
            if (e == null) return 0;
            if (!hashOfExpr.TryGetValue(e, out int h)) hashOfExpr[e] = h = new HashedString(e.Id).HashValue;
            return h;
        }

        private static Expression ExprOf(int hash)
        {
            if (hash == 0) return null;
            if (exprByHash == null)
            {
                exprByHash = new Dictionary<int, Expression>();
                foreach (Expression e in Db.Get().Expressions.resources) exprByHash[new HashedString(e.Id).HashValue] = e;
            }
            exprByHash.TryGetValue(hash, out Expression x);
            return x;
        }

        // ================================================================== server

        /// <summary>The server's view of one puppet.</summary>
        internal sealed class HostLook
        {
            public KBatchedAnimController kbac;
            public SymbolOverrideController soc;
            public FaceGraph face;
            public AnimEventHandler aeh;
            public ThoughtGraph.Instance tg;
            public GameObject go;
            public int socVersion = int.MinValue, hiddenSig, exprSig, thoughtSig, ctx, hit, laser, mine;
            public Vector2 target;
            public State sent = new State(), scratch = new State();
            public int sentHash;
            public bool needFull = true;
        }

        public static HostLook NewHostLook(Brain b, KBatchedAnimController kbac) => new HostLook
        {
            go = b.gameObject, kbac = kbac,
            soc = b.GetComponent<SymbolOverrideController>(), face = b.GetComponent<FaceGraph>(), aeh = b.GetComponent<AnimEventHandler>(),
            tg = b.gameObject.GetSMI<ThoughtGraph.Instance>(),
        };

        private static int ThoughtSig(ThoughtGraph.Instance tg)
        {
            if (tg == null) return 0;
            List<Thought> l = ThoughtList(tg);
            int h = 0;
            unchecked { if (l != null) foreach (Thought t in l) if (t != null && !t.showImmediately) h += Mix(new HashedString(t.Id).HashValue, 0x3c6ef372); }
            return h;
        }

        /// <summary>Server: what a duplicant's tool is doing right now (MultitoolController's hit effect: the sparks at
        /// the target, the beam turned on by LaserOn, the digging sound by StartMining), by worker.</summary>
        private sealed class WorkFx { public GameObject fx; public int laser, mine; }
        private static readonly Dictionary<GameObject, WorkFx> hitEffects = new Dictionary<GameObject, WorkFx>();
        private static readonly AccessTools.FieldRef<MultitoolController.Instance, GameObject> HitEffectField =
            AccessTools.FieldRefAccess<MultitoolController.Instance, GameObject>("hitEffect");

        private static void CreateHitEffectPostfix(MultitoolController.Instance __instance)
        {
            if (!CommandBus.Active || !CommandBus.IsHost || __instance == null || __instance.workable == null) return;
            GameObject worker = __instance.gameObject;
            if (worker == null) return;
            // as CreateHitEffect itself decides: the beam when the target isn't the worker's own cell
            var w = new WorkFx { fx = HitEffectField(__instance), laser = Grid.PosToCell(__instance.workable) != Grid.PosToCell(worker) ? 1 : 0 };
            if (__instance.workable is Diggable dg) { Element e = dg.GetTargetElement(); if (e != null) w.mine = (int)e.id; }
            hitEffects[worker] = w;
        }

        private static void DestroyHitEffectPostfix(MultitoolController.Instance __instance)
        {
            if (__instance != null && __instance.gameObject != null) hitEffects.Remove(__instance.gameObject);
        }

        private static int HiddenSig(KBatchedAnimController kbac)
        {
            HashSet<KAnimHashedString> set = HiddenSet(kbac);
            if (set == null) return 0;
            int h = set.Count;
            unchecked { foreach (KAnimHashedString s in set) h += Mix(s.HashValue, 0x2545f491); }
            return h;
        }

        private static int ExprSig(FaceGraph face)
        {
            if (face == null) return 0;
            List<Expression> list = FaceList(face);
            int h = ExprHash(face.overrideExpression);
            unchecked { if (list != null) foreach (Expression e in list) h += Mix(ExprHash(e), 0x1b873593); }
            return h;
        }

        private static void WorkOf(HostLook h, out int hit, out int laser, out int mine)
        {
            hit = laser = mine = 0;
            if (hitEffects.Count == 0 || h.go == null || !hitEffects.TryGetValue(h.go, out WorkFx w)) return;
            laser = w.laser;
            mine = w.mine;
            KPrefabID k = w.fx != null ? w.fx.GetComponent<KPrefabID>() : null;
            hit = k != null ? k.PrefabTag.GetHash() : 0;
        }

        private static void Capture(HostLook h, State s)
        {
            s.Clear();
            if (h.soc != null)
            {
                List<SymbolOverrideController.SymbolEntry> list = SocList(h.soc);
                for (int i = 0; i < list.Count; i++)
                {
                    SymbolOverrideController.SymbolEntry e = list[i];
                    int t = e.targetSymbol.HashValue;
                    if (IsFace(t, e.priority)) continue;
                    s.ov[Key(t, e.priority)] = new Ov { target = t, src = e.sourceSymbolId.HashValue, batch = e.sourceSymbolBatchTag.HashValue, prio = e.priority };
                }
            }
            HashSet<KAnimHashedString> hid = HiddenSet(h.kbac);
            if (hid != null) foreach (KAnimHashedString x in hid) s.hidden.Add(x.HashValue);
            if (h.face != null)
            {
                List<Expression> ex = FaceList(h.face);
                if (ex != null) foreach (Expression e in ex) s.expr.Add(ExprHash(e));
                s.exprOverride = ExprHash(h.face.overrideExpression);
            }
            if (h.tg != null)
            {
                List<Thought> tl = ThoughtList(h.tg);
                if (tl != null)
                    foreach (Thought t in tl)
                        if (t != null && !t.showImmediately) { s.thoughts.Add(new HashedString(t.Id).HashValue); s.thoughtDescs.Add(ThoughtDesc.Of(t)); }
            }
            s.ctx = h.ctx; s.target = h.target; s.hit = h.hit; s.laser = h.laser; s.mine = h.mine;
        }

        /// <summary>Server, every frame per puppet: the delta to put on its next keyframe (null: nothing to say).
        /// withHash: this is a full-refresh keyframe, which carries the hash of the whole state.</summary>
        public static Delta HostSample(HostLook h, bool withHash)
        {
            if (h.kbac == null) return null;
            int ver = h.soc != null ? h.soc.version : 0;
            int hidSig = HiddenSig(h.kbac), exprSig = ExprSig(h.face);
            int ctx = 0;
            Vector2 tgt = Vector2.zero;
            if (h.aeh != null) { ctx = h.aeh.GetContext().HashValue; tgt = h.aeh.GetTargetPos(); }
            WorkOf(h, out int hit, out int laser, out int mine);
            int thoughtSig = ThoughtSig(h.tg);
            List<Thought> instants = null;
            if (instantThoughts.Count > 0 && h.go != null && instantThoughts.TryGetValue(h.go, out instants) && instants.Count == 0) instants = null;
            bool work = ctx != h.ctx || hit != h.hit || laser != h.laser || mine != h.mine || (tgt - h.target).sqrMagnitude > 1e-6f;
            bool dirty = h.needFull || ver != h.socVersion || hidSig != h.hiddenSig || exprSig != h.exprSig || thoughtSig != h.thoughtSig || instants != null || work;
            Delta d = null;
            if (dirty)
            {
                if (ver != h.socVersion) DirtyVersion++;
                if (hidSig != h.hiddenSig) DirtyHidden++;
                if (exprSig != h.exprSig) DirtyFace++;
                if (work) DirtyWork++;
                h.socVersion = ver; h.hiddenSig = hidSig; h.exprSig = exprSig; h.thoughtSig = thoughtSig; h.ctx = ctx; h.target = tgt; h.hit = hit; h.laser = laser; h.mine = mine;
                Capture(h, h.scratch);
                d = Diff(h.sent, h.scratch, h.needFull);
                if (instants != null)
                {
                    // a line of a conversation: shown once, at this moment of the timeline
                    if (d == null) d = new Delta();
                    d.parts |= PThought;
                    d.instant = new List<ThoughtDesc>(instants.Count);
                    foreach (Thought t in instants) d.instant.Add(ThoughtDesc.Of(t));
                    instants.Clear();
                    if (d.thoughts == null) { d.thoughts = new List<ThoughtDesc>(h.scratch.thoughtDescs); }
                }
                if (d != null)
                {
                    State old = h.sent; h.sent = h.scratch; h.scratch = old;
                    h.sentHash = h.sent.Hash();
                    if (h.needFull) HostFulls++; else HostDeltas++;
                }
                else DirtyNoChange++;
                h.needFull = false;
            }
            if (withHash)
            {
                if (d == null) d = new Delta();
                d.parts |= PHash;
                d.hash = h.sentHash;
            }
            return d;
        }

        /// <summary>Whether a guest would show a and b the same (no allocation: this runs for every puppet whose
        /// looks may have changed, every frame).</summary>
        private static bool Same(State a, State b)
        {
            if (a.ov.Count != b.ov.Count || a.hidden.Count != b.hidden.Count || a.expr.Count != b.expr.Count) return false;
            if (a.exprOverride != b.exprOverride || a.ctx != b.ctx || a.hit != b.hit || a.laser != b.laser || a.mine != b.mine) return false;
            if ((a.target - b.target).sqrMagnitude > 1e-6f) return false;
            foreach (var kv in b.ov)
                if (!a.ov.TryGetValue(kv.Key, out Ov o) || o.src != kv.Value.src || o.batch != kv.Value.batch) return false;
            foreach (int x in b.hidden) if (!a.hidden.Contains(x)) return false;
            for (int i = 0; i < b.expr.Count; i++) if (!a.expr.Contains(b.expr[i])) return false;
            if (a.thoughts.Count != b.thoughts.Count) return false;
            for (int i = 0; i < b.thoughts.Count; i++) if (!a.thoughts.Contains(b.thoughts[i])) return false;
            return true;
        }

        public static long DirtyVersion, DirtyHidden, DirtyFace, DirtyWork, DirtyNoChange;

        /// <summary>What changed from a to b (all of b when full); null when nothing did.</summary>
        private static Delta Diff(State a, State b, bool full)
        {
            if (!full && Same(a, b)) return null;
            var d = new Delta();
            if (full)
            {
                d.parts = POv | PHidden | PExpr | PWork | PFull | PThought;
                d.ovSet.AddRange(b.ov.Values);
                d.hidAdd.AddRange(b.hidden);
            }
            else
            {
                foreach (var kv in a.ov) if (!b.ov.ContainsKey(kv.Key)) d.ovRemove.Add(kv.Key);
                foreach (var kv in b.ov)
                    if (!a.ov.TryGetValue(kv.Key, out Ov o) || o.src != kv.Value.src || o.batch != kv.Value.batch) d.ovSet.Add(kv.Value);
                if (d.OvChanges > 0) d.parts |= POv;
                foreach (int s in a.hidden) if (!b.hidden.Contains(s)) d.hidRemove.Add(s);
                foreach (int s in b.hidden) if (!a.hidden.Contains(s)) d.hidAdd.Add(s);
                if (d.HiddenChanges > 0) d.parts |= PHidden;
                bool exprSame = a.exprOverride == b.exprOverride && a.expr.Count == b.expr.Count;
                for (int i = 0; exprSame && i < b.expr.Count; i++) exprSame = a.expr.Contains(b.expr[i]);
                if (!exprSame) d.parts |= PExpr;
                bool thoughtsSame = a.thoughts.Count == b.thoughts.Count;
                for (int i = 0; thoughtsSame && i < b.thoughts.Count; i++) thoughtsSame = a.thoughts.Contains(b.thoughts[i]);
                if (!thoughtsSame) d.parts |= PThought;
                if (a.ctx != b.ctx || a.hit != b.hit || a.laser != b.laser || a.mine != b.mine || (a.target - b.target).sqrMagnitude > 1e-6f) d.parts |= PWork;
                if (d.parts == 0) return null;
            }
            if ((d.parts & PExpr) != 0) d.expr.AddRange(b.expr);
            if ((d.parts & PThought) != 0) d.thoughts = new List<ThoughtDesc>(b.thoughtDescs);
            d.exprOverride = b.exprOverride;
            d.ctx = b.ctx; d.target = b.target; d.hit = b.hit; d.laser = b.laser; d.mine = b.mine;
            return d;
        }

        /// <summary>Server: a guest said its copy of these puppets is off.</summary>
        public static void OnAsk(byte[] data, IEnumerable<KeyValuePair<int, HostLook>> looks)
        {
            var ids = new HashSet<int>();
            using (var r = new BinaryReader(new MemoryStream(data)))
            {
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++) ids.Add(r.ReadInt32());
            }
            foreach (var kv in looks) if (ids.Contains(kv.Key)) kv.Value.needFull = true;
            Asked += ids.Count;
        }

        // ================================================================== guest

        /// <summary>A guest's copy of one puppet's looks.</summary>
        internal sealed class GuestLook
        {
            public readonly State state = new State();
            public int seq = -1;          // keyframes up to this one are applied
            public float askedAt = -10f;
            public GameObject hitObj;
            public int hitObjTag, shownLaser, shownMine;
            public bool known;            // a full state arrived at some point
        }

        private static readonly HashSet<int> toAsk = new HashSet<int>();
        private static float nextAsk;

        public static void Reset()
        {
            toAsk.Clear();
            hitEffects.Clear();
            instantThoughts.Clear();
        }

        /// <summary>Guest, in timeline order: apply one keyframe's looks to the puppet.</summary>
        public static void GuestApply(int id, GuestLook g, Delta d, KBatchedAnimController kbac)
        {
            State s = g.state;
            byte parts = d.parts;
            if ((parts & PFull) != 0) { s.Clear(); g.known = true; GuestFulls++; }
            else if ((parts & ~PHash) != 0) GuestDeltas++;
            if ((parts & POv) != 0)
            {
                foreach (long k in d.ovRemove) s.ov.Remove(k);
                foreach (Ov o in d.ovSet) s.ov[Key(o.target, o.prio)] = o;
            }
            if ((parts & PHidden) != 0)
            {
                foreach (int x in d.hidRemove) s.hidden.Remove(x);
                foreach (int x in d.hidAdd) s.hidden.Add(x);
            }
            if ((parts & PExpr) != 0) { s.expr.Clear(); s.expr.AddRange(d.expr); s.exprOverride = d.exprOverride; }
            if ((parts & PWork) != 0) { s.ctx = d.ctx; s.target = d.target; s.hit = d.hit; s.laser = d.laser; s.mine = d.mine; }
            if ((parts & PThought) != 0 && d.thoughts != null)
            {
                s.thoughts.Clear(); s.thoughtDescs.Clear();
                foreach (ThoughtDesc t in d.thoughts) { s.thoughts.Add(t.Key); s.thoughtDescs.Add(t); }
            }
            if ((parts & PHash) != 0 && (!g.known || s.Hash() != d.hash))
            {
                if (g.known) HashMismatches++;
                if (Puppets.Clock - g.askedAt > 3f) { g.askedAt = Puppets.Clock; toAsk.Add(id); }
            }
            if ((parts & ~PHash) != 0 && g.known) Realize(g, parts, kbac);
            if (d.instant != null && g.known && kbac != null) SyncThoughts(kbac, s, d.instant);
        }

        /// <summary>Make the puppet show the state.</summary>
        private static void Realize(GuestLook g, byte parts, KBatchedAnimController kbac)
        {
            if (kbac == null) return;
            State s = g.state;
            bool full = (parts & PFull) != 0;
            if (full || (parts & PExpr) != 0)
            {
                // first: a face change can add or drop an accessory (head effects), which the lists below then settle
                FaceGraph face = kbac.GetComponent<FaceGraph>();
                if (face != null) SyncFace(face, s);
            }
            if (full || (parts & (POv | PExpr)) != 0)
            {
                SymbolOverrideController soc = kbac.GetComponent<SymbolOverrideController>();
                if (soc != null) SyncOverrides(soc, s);
            }
            if (full || (parts & (PHidden | PExpr)) != 0) SyncHidden(kbac, s);
            if (full || (parts & PWork) != 0) SyncWork(g, kbac, s);
            if (full || (parts & PThought) != 0) SyncThoughts(kbac, s, null);
        }

        /// <summary>The lasting thoughts as the server has them; and the ones shown at once (a conversation's line).</summary>
        private static void SyncThoughts(KBatchedAnimController kbac, State s, List<ThoughtDesc> instant)
        {
            ThoughtGraph.Instance tg = kbac.gameObject.GetSMI<ThoughtGraph.Instance>();
            if (tg == null) return;
            List<Thought> have = ThoughtList(tg);
            if (have != null)
                foreach (Thought t in new List<Thought>(have))
                    if (t != null && !t.showImmediately && !s.thoughts.Contains(new HashedString(t.Id).HashValue)) tg.RemoveThought(t);
            foreach (ThoughtDesc d in s.thoughtDescs)
            {
                Thought t = ThoughtOf(d);
                if (t != null && (have == null || !have.Contains(t))) tg.AddThought(t);
            }
            if (instant != null)
                foreach (ThoughtDesc d in instant)
                {
                    Thought t = ThoughtOf(d);
                    if (t != null) { tg.AddThought(t); InstantThoughts++; }
                }
        }

        public static long InstantThoughts;

        private static void SyncFace(FaceGraph face, State s)
        {
            List<Expression> have = FaceList(face);
            if (have != null)
            {
                // (each change re-sorts the face's own list: go through a copy). One of each wanted id stays: a second
                // expression object with the same id (seen: two "Sticker") goes too.
                var seen = new HashSet<int>();
                foreach (Expression e in new List<Expression>(have))
                {
                    int h = ExprHash(e);
                    if (!s.expr.Contains(h) || !seen.Add(h) || (ExprOf(h) != null && ExprOf(h) != e && have.Contains(ExprOf(h)))) face.RemoveExpression(e);
                }
                foreach (int h in s.expr)
                {
                    Expression e = ExprOf(h);
                    if (e == null) { MissingExpressions++; continue; }
                    if (!have.Contains(e)) face.AddExpression(e);
                }
            }
            Expression ov = ExprOf(s.exprOverride);
            if (face.overrideExpression != ov) face.SetOverrideExpression(ov);
        }

        private static readonly List<SymbolOverrideController.SymbolEntry> socScratch = new List<SymbolOverrideController.SymbolEntry>();

        private static void SyncOverrides(SymbolOverrideController soc, State s)
        {
            socScratch.Clear();
            socScratch.AddRange(SocList(soc));
            foreach (SymbolOverrideController.SymbolEntry e in socScratch)
            {
                int t = e.targetSymbol.HashValue;
                if (IsFace(t, e.priority)) continue;
                if (s.ov.TryGetValue(Key(t, e.priority), out Ov o) && o.src == e.sourceSymbolId.HashValue && o.batch == e.sourceSymbolBatchTag.HashValue) continue;
                soc.RemoveSymbolOverride(e.targetSymbol, e.priority);
            }
            foreach (Ov o in s.ov.Values)
            {
                var target = new HashedString(o.target);
                if (soc.GetSymbolOverrideIdx(target, o.prio) >= 0) continue; // there, and the same (the others went above)
                KAnim.Build.Symbol sym = null;
                try { sym = KAnimBatchManager.Instance().GetBatchGroupData(new HashedString(o.batch))?.GetSymbol(new KAnimHashedString(o.src)); }
                catch (System.Exception) { }
                if (sym == null) { MissingSymbols++; continue; }
                soc.AddSymbolOverride(target, sym, o.prio);
            }
            socScratch.Clear();
        }

        private static readonly List<KAnimHashedString> hidScratch = new List<KAnimHashedString>();

        private static void SyncHidden(KBatchedAnimController kbac, State s)
        {
            HashSet<KAnimHashedString> have = HiddenSet(kbac);
            if (have == null) return;
            hidScratch.Clear();
            foreach (KAnimHashedString x in have) if (!s.hidden.Contains(x.HashValue)) hidScratch.Add(x);
            foreach (KAnimHashedString x in hidScratch) kbac.SetSymbolVisiblity(x, true);
            hidScratch.Clear();
            foreach (int x in s.hidden) if (!have.Contains(new KAnimHashedString(x))) hidScratch.Add(new KAnimHashedString(x));
            foreach (KAnimHashedString x in hidScratch) kbac.SetSymbolVisiblity(x, false);
            hidScratch.Clear();
        }

        private static void SyncWork(GuestLook g, KBatchedAnimController kbac, State s)
        {
            var target = new Vector3(s.target.x, s.target.y, 0f);
            AnimEventHandler aeh = kbac.GetComponent<AnimEventHandler>();
            if (aeh != null)
            {
                if (s.ctx == 0) aeh.ClearContext(); else aeh.SetContext(new HashedString(s.ctx));
                aeh.SetTargetPos(target);
                aeh.UpdateWorkTarget(target); // the beams (LaserEffect children) aim here
            }
            // the beam (KBatchedAnimEventToggler on LaserOn: the one for the current context) and the digging sound
            if (g.shownLaser != s.laser)
            {
                kbac.gameObject.Trigger(s.laser != 0 ? (int)GameHashes.LaserOn : (int)GameHashes.LaserOff);
                g.shownLaser = s.laser;
            }
            if (g.shownMine != s.mine)
            {
                if (g.shownMine != 0) kbac.gameObject.Trigger((int)GameHashes.StopMining);
                Element el = s.mine != 0 ? ElementLoader.FindElementByHash((SimHashes)s.mine) : null;
                if (el != null) kbac.gameObject.Trigger((int)GameHashes.StartMining, el);
                g.shownMine = s.mine;
            }
            if (g.hitObjTag != s.hit || (s.hit != 0 && g.hitObj == null))
            {
                DropHit(g);
                if (s.hit != 0)
                {
                    GameObject prefab = Assets.GetPrefab(new Tag(s.hit));
                    if (prefab != null)
                    {
                        g.hitObj = GameUtil.KInstantiate(prefab, target, Grid.SceneLayer.FXFront2);
                        g.hitObj.SetActive(true);
                        var fxAnim = g.hitObj.GetComponent<KBatchedAnimController>();
                        if (fxAnim != null) { fxAnim.sceneLayer = Grid.SceneLayer.FXFront2; fxAnim.enabled = false; fxAnim.enabled = true; }
                        HitEffects++;
                    }
                    g.hitObjTag = s.hit;
                }
            }
            else if (g.hitObj != null)
            {
                Vector3 p = g.hitObj.transform.GetPosition();
                if (Mathf.Abs(p.x - target.x) > 1e-3f || Mathf.Abs(p.y - target.y) > 1e-3f) g.hitObj.transform.SetPosition(new Vector3(target.x, target.y, p.z));
            }
        }

        public static void DropHit(GuestLook g)
        {
            if (g.hitObj != null) Util.KDestroyGameObject(g.hitObj);
            g.hitObj = null;
            g.hitObjTag = 0;
        }

        /// <summary>Guest, every frame: send the asks that piled up.</summary>
        public static void GuestTick(float now)
        {
            if (toAsk.Count == 0 || now < nextAsk || CommandBus.Client == null) return;
            nextAsk = now + 0.5f;
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(toAsk.Count);
                foreach (int id in toAsk) w.Write(id);
            }
            Asked += toAsk.Count;
            toAsk.Clear();
            CommandBus.Client.SendRelay(Proto.TargetHost, ChAsk, ms.ToArray());
        }

        public static string Report(bool host) => host
            ? $"looks: {HostDeltas} deltas, {HostFulls} full, {Asked} asked in full; looked again for overrides {DirtyVersion}, hidden {DirtyHidden}, face {DirtyFace}, work {DirtyWork}, nothing changed {DirtyNoChange}"
            : $"looks: {GuestDeltas} deltas, {GuestFulls} full, asked {Asked} ({HashMismatches} hash mismatches), missing symbols {MissingSymbols}, expressions {MissingExpressions}, thoughts {MissingThoughts}, hit effects {HitEffects}, conversation lines {InstantThoughts}, blocked local {BlockedLocal}";

        // ================================================================== a guest's own code keeps its hands off

        public static long BlockedLocal;

        private static bool AllowSoc(SymbolOverrideController soc, int target, int prio)
        {
            if (!Puppets.Mirroring || Puppets.Applying || IsFace(target, prio) || soc == null) return true;
            if (!Puppets.IsPuppet(soc.GetComponent<KBatchedAnimController>())) return true;
            BlockedLocal++;
            return false;
        }

        private static bool AddPrefix(SymbolOverrideController __instance, HashedString __0, int __2, ref int __result)
        {
            if (AllowSoc(__instance, __0.HashValue, __2)) return true;
            __result = -1;
            return false;
        }

        private static bool RemovePrefix(SymbolOverrideController __instance, HashedString __0, int __1, ref bool __result)
        {
            if (AllowSoc(__instance, __0.HashValue, __1)) return true;
            __result = false;
            return false;
        }

        private static bool RemoveAllPrefix(SymbolOverrideController __instance) => AllowSoc(__instance, 0, -1);

        private static bool VisibilityPrefix(KAnimControllerBase __instance)
        {
            if (!Puppets.Mirroring || Puppets.Applying || !Puppets.IsPuppet(__instance)) return true;
            BlockedLocal++;
            return false;
        }

        private static bool FacePrefix(FaceGraph __instance)
        {
            if (!Puppets.Mirroring || Puppets.Applying || !Puppets.IsPuppet(__instance.GetComponent<KBatchedAnimController>())) return true;
            BlockedLocal++;
            return false;
        }

        public static void ApplyPatches(Harmony h)
        {
            void P(System.Type t, string name, System.Type[] args, string prefix = null, string postfix = null)
            {
                try
                {
                    var m = args != null ? AccessTools.Method(t, name, args) : AccessTools.Method(t, name);
                    h.Patch(m, prefix: prefix != null ? new HarmonyMethod(typeof(PuppetLooks), prefix) : null,
                        postfix: postfix != null ? new HarmonyMethod(typeof(PuppetLooks), postfix) : null);
                }
                catch (System.Exception e) { Log.Warn($"looks: can't patch {t.Name}.{name}: {e.Message}"); }
            }
            P(typeof(MultitoolController.Instance), nameof(MultitoolController.Instance.CreateHitEffect), null, postfix: nameof(CreateHitEffectPostfix));
            P(typeof(MultitoolController.Instance), nameof(MultitoolController.Instance.DestroyHitEffect), null, postfix: nameof(DestroyHitEffectPostfix));
            P(typeof(SymbolOverrideController), nameof(SymbolOverrideController.AddSymbolOverride), null, prefix: nameof(AddPrefix));
            P(typeof(SymbolOverrideController), nameof(SymbolOverrideController.RemoveSymbolOverride), null, prefix: nameof(RemovePrefix));
            P(typeof(SymbolOverrideController), nameof(SymbolOverrideController.RemoveAllSymbolOverrides), null, prefix: nameof(RemoveAllPrefix));
            P(typeof(KAnimControllerBase), nameof(KAnimControllerBase.SetSymbolVisiblity), null, prefix: nameof(VisibilityPrefix));
            P(typeof(KAnimControllerBase), nameof(KAnimControllerBase.BatchSetSymbolsVisiblity), null, prefix: nameof(VisibilityPrefix));
            P(typeof(FaceGraph), nameof(FaceGraph.AddExpression), null, prefix: nameof(FacePrefix));
            P(typeof(FaceGraph), nameof(FaceGraph.RemoveExpression), null, prefix: nameof(FacePrefix));
            P(typeof(FaceGraph), nameof(FaceGraph.SetOverrideExpression), null, prefix: nameof(FacePrefix));
            P(typeof(ThoughtGraph.Instance), nameof(ThoughtGraph.Instance.AddThought), null, prefix: nameof(ThoughtPrefix), postfix: nameof(AddThoughtPostfix));
            P(typeof(ThoughtGraph.Instance), nameof(ThoughtGraph.Instance.RemoveThought), null, prefix: nameof(ThoughtPrefix));
        }

        private static string ThoughtIds(GameObject go)
        {
            ThoughtGraph.Instance tg = go.GetSMI<ThoughtGraph.Instance>();
            List<Thought> l = tg != null ? ThoughtList(tg) : null;
            if (l == null || l.Count == 0) return "-";
            var ids = new List<string>();
            foreach (Thought t in l) if (t != null && !t.showImmediately) ids.Add(t.Id);
            ids.Sort(System.StringComparer.Ordinal);
            return ids.Count > 0 ? string.Join(",", ids.ToArray()) : "-";
        }

        /// <summary>For the sync test: every puppet's looks as this side shows them.</summary>
        public static string Dump()
        {
            var sb = new System.Text.StringBuilder();
            var tmp = new HostLook();
            var st = new State();
            foreach (Brain b in Components.Brains.Items)
            {
                if (b == null) continue;
                var kpid = b.GetComponent<KPrefabID>();
                var kbac = b.GetComponent<KBatchedAnimController>();
                if (kpid == null || kbac == null) continue;
                tmp.go = b.gameObject; tmp.kbac = kbac;
                tmp.soc = b.GetComponent<SymbolOverrideController>(); tmp.face = b.GetComponent<FaceGraph>(); tmp.aeh = b.GetComponent<AnimEventHandler>();
                tmp.ctx = tmp.aeh != null ? tmp.aeh.GetContext().HashValue : 0;
                tmp.target = tmp.aeh != null ? (Vector2)tmp.aeh.GetTargetPos() : Vector2.zero;
                WorkOf(tmp, out tmp.hit, out tmp.laser, out tmp.mine);
                Capture(tmp, st);
                var ov = new List<string>();
                foreach (Ov o in st.ov.Values) ov.Add($"{o.target:x8}:{o.prio}={o.src:x8}/{o.batch:x8}");
                ov.Sort(System.StringComparer.Ordinal);
                var hid = new List<int>(st.hidden);
                hid.Sort();
                var ex = new List<string>();
                if (tmp.face != null) { foreach (Expression e in FaceList(tmp.face)) ex.Add(e.Id); }
                ex.Sort(System.StringComparer.Ordinal);
                sb.Append(kpid.InstanceID).Append(' ').Append(kpid.PrefabTag.Name)
                  .Append(" ov=").Append(string.Join(",", ov.ToArray()))
                  .Append(" hidden=").Append(string.Join(",", hid.ConvertAll(x => x.ToString("x8")).ToArray()))
                  .Append(" expr=").Append(string.Join(",", ex.ToArray())).Append('|').Append(tmp.face != null && tmp.face.overrideExpression != null ? tmp.face.overrideExpression.Id : "-")
                  .Append(" ctx=").Append(tmp.ctx == 0 ? "-" : HashCache.Get().Get(tmp.ctx) ?? tmp.ctx.ToString("x8"))
                  .Append(" thoughts=").Append(ThoughtIds(b.gameObject))
                  .Append('\n');
            }
            return sb.ToString();
        }
    }
}
