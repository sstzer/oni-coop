namespace OniCoop
{
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using HarmonyLib;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>The printing pod: who gets printed is the host's call (it runs the simulation, possibly with nobody at
    /// its screen), but any player chooses.
    ///
    /// When the pod has printables, the host makes this round's offers itself (duplicants and care packages, with the
    /// same rules as the pod screen: how many packages, no repeated personality, no personality already in the colony)
    /// and sends them to everybody. Whoever opens the pod screen, host or guest, gets those offers in its cards instead
    /// of random ones: the screen lays out the cards for this round, and each card's new duplicant / package is filled
    /// with the offer. Printing or rejecting all on a guest asks the host, which prints that very offer (the new
    /// duplicant reaches everybody as a new object); then the round is over everywhere and old cards are cleared.</summary>
    internal static class Printing
    {
        public const byte Ch = 15;
        private const byte MOffers = 1, MConsumed = 2, MChoose = 3, MReject = 4;

        // ================================================================== offers

        private sealed class StatsData
        {
            public string name, nameKey, genderKey, sticker, outfit, personality;
            public List<string> traits = new List<string>(), accessories = new List<string>();
            public string stressTrait, joyTrait, congenitalTrait;
            public int rarity, voice;
            public bool valid;
            public List<KeyValuePair<string, int>> levels = new List<KeyValuePair<string, int>>();
            public List<KeyValuePair<string, float>> aptitudes = new List<KeyValuePair<string, float>>();

            public static StatsData From(MinionStartingStats s) => new StatsData
            {
                name = s.Name, nameKey = s.NameStringKey, genderKey = s.GenderStringKey, sticker = s.stickerType, outfit = s.overrideOutfitID,
                personality = s.personality?.Id, traits = s.Traits.Where(t => t != null).Select(t => t.Id).ToList(),
                accessories = s.accessories.Where(a => a != null).Select(a => a.Id).ToList(),
                stressTrait = s.stressTrait?.Id, joyTrait = s.joyTrait?.Id, congenitalTrait = s.congenitaltrait?.Id,
                rarity = s.rarityBalance, voice = s.voiceIdx, valid = s.IsValid,
                levels = s.StartingLevels.ToList(),
                aptitudes = s.skillAptitudes.Where(kv => kv.Key != null).Select(kv => new KeyValuePair<string, float>(kv.Key.Id, kv.Value)).ToList(),
            };

            /// <summary>Make a freshly generated MinionStartingStats this one.</summary>
            public void ApplyTo(MinionStartingStats s)
            {
                Db db = Db.Get();
                s.Name = name; s.NameStringKey = nameKey; s.GenderStringKey = genderKey; s.stickerType = sticker; s.overrideOutfitID = outfit;
                s.personality = db.Personalities.TryGet(personality) ?? s.personality;
                s.Traits = traits.Select(id => db.traits.TryGet(id)).Where(t => t != null).ToList();
                s.accessories = accessories.Select(id => db.Accessories.TryGet(id)).Where(a => a != null).ToList();
                s.stressTrait = stressTrait != null ? db.traits.TryGet(stressTrait) : null;
                s.joyTrait = joyTrait != null ? db.traits.TryGet(joyTrait) : null;
                s.congenitaltrait = congenitalTrait != null ? db.traits.TryGet(congenitalTrait) : null;
                s.rarityBalance = rarity; s.voiceIdx = voice; s.IsValid = valid;
                s.StartingLevels = levels.ToDictionary(kv => kv.Key, kv => kv.Value);
                s.skillAptitudes = new Dictionary<Database.SkillGroup, float>();
                foreach (var kv in aptitudes) { Database.SkillGroup g = db.SkillGroups.TryGet(kv.Key); if (g != null) s.skillAptitudes[g] = kv.Value; }
            }

            public void Write(BinaryWriter w)
            {
                Str(w, name); Str(w, nameKey); Str(w, genderKey); Str(w, sticker); Str(w, outfit); Str(w, personality);
                Strs(w, traits); Strs(w, accessories); Str(w, stressTrait); Str(w, joyTrait); Str(w, congenitalTrait);
                w.Write(rarity); w.Write(voice); w.Write(valid);
                w.Write(levels.Count); foreach (var kv in levels) { w.Write(kv.Key); w.Write(kv.Value); }
                w.Write(aptitudes.Count); foreach (var kv in aptitudes) { w.Write(kv.Key); w.Write(kv.Value); }
            }

            public static StatsData Read(BinaryReader r)
            {
                var d = new StatsData { name = RStr(r), nameKey = RStr(r), genderKey = RStr(r), sticker = RStr(r), outfit = RStr(r), personality = RStr(r) };
                d.traits = RStrs(r); d.accessories = RStrs(r); d.stressTrait = RStr(r); d.joyTrait = RStr(r); d.congenitalTrait = RStr(r);
                d.rarity = r.ReadInt32(); d.voice = r.ReadInt32(); d.valid = r.ReadBoolean();
                int n = r.ReadInt32(); for (int i = 0; i < n; i++) d.levels.Add(new KeyValuePair<string, int>(r.ReadString(), r.ReadInt32()));
                n = r.ReadInt32(); for (int i = 0; i < n; i++) d.aptitudes.Add(new KeyValuePair<string, float>(r.ReadString(), r.ReadSingle()));
                return d;
            }
        }

        private static void Str(BinaryWriter w, string s) { w.Write(s != null); if (s != null) w.Write(s); }
        private static string RStr(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;
        private static void Strs(BinaryWriter w, List<string> l) { w.Write(l.Count); foreach (string s in l) w.Write(s ?? ""); }
        private static List<string> RStrs(BinaryReader r) { int n = r.ReadInt32(); var l = new List<string>(n); for (int i = 0; i < n; i++) l.Add(r.ReadString()); return l; }

        private sealed class Offer
        {
            public StatsData dupe;
            public string package; public float quantity; public string facade;
            public ITelepadDeliverable hostObject; // host: what it prints
        }

        private sealed class OfferSet { public int id; public List<Offer> offers = new List<Offer>(); }

        private static OfferSet current;
        private static int nextId = 1;
        public static int Printed, Rejected, Requests;

        private static bool Active => CommandBus.Active;
        private static bool IsGuest => CommandBus.IsGuest;

        public static void Reset()
        {
            current = null;
            dealt.Clear();
            byDeliverable.Clear();
            dealtSet = 0;
        }

        // ================================================================== host: making a round

        private static readonly List<Tag> Models = new List<Tag> { GameTags.Minions.Models.Standard, GameTags.Minions.Models.Bionic };

        private static OfferSet Generate()
        {
            var set = new OfferSet { id = nextId++ };
            bool packages = CustomGameSettings.Instance.GetCurrentQualitySetting(Klei.CustomSettings.CustomGameSettingConfigs.CarePackages).id == "Enabled";
            int nPackages = packages ? (Random.Range(0, 101) <= 70 ? 1 : 2) : 0;
            int nDupes = packages ? 4 - nPackages : 3;
            var dupes = new List<MinionStartingStats>();
            for (int i = 0; i < nDupes; i++)
            {
                MinionStartingStats s = null;
                for (int tries = 0; tries < 20; tries++)
                {
                    s = new MinionStartingStats(Models, false);
                    if (!Invalid(s, dupes)) break;
                }
                dupes.Add(s);
                set.offers.Add(new Offer { dupe = StatsData.From(s), hostObject = s });
            }
            var infos = new List<CarePackageInfo>();
            for (int j = 0; j < nPackages; j++)
            {
                CarePackageInfo info = null;
                for (int tries = 0; tries < 20; tries++)
                {
                    info = Immigration.Instance.RandomCarePackage();
                    if (!infos.Contains(info)) break;
                }
                infos.Add(info);
                string facade = info.facadeID == "SELECTRANDOM"
                    ? Db.GetEquippableFacades().resources.FindAll(f => f.DefID == info.id).GetRandom().Id
                    : info.facadeID;
                var data = new CarePackageContainer.CarePackageInstanceData { info = info, facadeID = facade };
                set.offers.Add(new Offer { package = info.id, quantity = info.quantity, facade = facade, hostObject = data });
            }
            Log.Info($"printing pod: round {set.id}: {string.Join(", ", set.offers.Select(Describe).ToArray())}");
            return set;
        }

        /// <summary>The pod screen's own rules (CharacterContainer.IsCharacterInvalid).</summary>
        private static bool Invalid(MinionStartingStats s, List<MinionStartingStats> others)
        {
            if (others.Any(o => o.personality.Id == s.personality.Id && o.IsValid)) return true;
            if (Game.Instance != null && !Game.IsDlcActiveForCurrentSave(s.personality.requiredDlcId)) return true;
            return s.personality.model != GameTags.Minions.Models.Bionic && Components.LiveMinionIdentities.Items.Any(m => m.personalityResourceId == s.personality.Id);
        }

        private static string Describe(Offer o) => o.dupe != null ? o.dupe.name : $"{o.package} x{o.quantity}";

        /// <summary>Host, every frame: printables came up and nobody has this round's offers yet.</summary>
        public static void HostTick()
        {
            if (!Active || !CommandBus.IsHost || Immigration.Instance == null) return;
            if (Immigration.Instance.ImmigrantsAvailable && current == null)
            {
                current = Generate();
                SendOffers();
            }
        }

        public static void OnGuestJoined() { if (current != null) SendOffers(); }

        private static void SendOffers()
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(MOffers); w.Write(current.id); w.Write(current.offers.Count);
                foreach (Offer o in current.offers)
                {
                    w.Write(o.dupe != null);
                    if (o.dupe != null) o.dupe.Write(w);
                    else { w.Write(o.package); w.Write(o.quantity); Str(w, o.facade); }
                }
            }
            CommandBus.Client.SendRelay(Proto.TargetAll, Ch, ms.ToArray());
        }

        /// <summary>Host: the round is over (printed or rejected, by anybody).</summary>
        private static void Consumed()
        {
            if (current == null) return;
            int id = current.id;
            current = null;
            ClearScreen();
            CommandBus.Client.SendRelay(Proto.TargetAll, Ch, new[] { MConsumed }.Concat(System.BitConverter.GetBytes(id)).ToArray());
        }

        // ================================================================== messages

        public static void OnRelay(ushort from, byte[] data)
        {
            var r = new BinaryReader(new MemoryStream(data));
            byte kind = r.ReadByte();
            switch (kind)
            {
                case MOffers when IsGuest:
                {
                    var set = new OfferSet { id = r.ReadInt32() };
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                        set.offers.Add(r.ReadBoolean() ? new Offer { dupe = StatsData.Read(r) } : new Offer { package = r.ReadString(), quantity = r.ReadSingle(), facade = RStr(r) });
                    current = set;
                    if (dealtSet != set.id) ClearScreen();
                    Log.Info($"printing pod: round {set.id} from the host: {string.Join(", ", set.offers.Select(Describe).ToArray())}");
                    break;
                }
                case MConsumed when IsGuest:
                {
                    int id = r.ReadInt32();
                    if (current != null && current.id == id) current = null;
                    ClearScreen();
                    Log.Info($"printing pod: round {id} is over");
                    break;
                }
                case MChoose when CommandBus.IsHost:
                {
                    int id = r.ReadInt32(), index = r.ReadInt32(), telepadId = r.ReadInt32();
                    if (current == null || current.id != id || index < 0 || index >= current.offers.Count)
                    {
                        Log.Warn($"printing pod: #{from} chose offer {index} of round {id}, but that round is over");
                        break;
                    }
                    Telepad pad = FindTelepad(telepadId);
                    if (pad == null) { Log.Warn("printing pod: no telepad #" + telepadId); break; }
                    Offer o = current.offers[index];
                    Log.Info($"printing pod: #{from} prints {Describe(o)}");
                    pad.OnAcceptDelivery(o.hostObject); // Consumed() follows in the postfix
                    break;
                }
                case MReject when CommandBus.IsHost:
                {
                    int id = r.ReadInt32(), telepadId = r.ReadInt32();
                    if (current == null || current.id != id) break;
                    Telepad pad = FindTelepad(telepadId);
                    Log.Info($"printing pod: #{from} rejects round {id}");
                    pad?.RejectAll();
                    break;
                }
            }
        }

        private static Telepad FindTelepad(int id) =>
            KPrefabIDTracker.Get().GetInstance(id)?.GetComponent<Telepad>() ?? Object.FindObjectOfType<Telepad>();

        // ================================================================== the pod screen

        private static readonly Dictionary<object, int> dealt = new Dictionary<object, int>();          // card -> offer
        private static readonly Dictionary<object, int> byDeliverable = new Dictionary<object, int>();  // what a card offers -> offer
        private static int dealtSet;
        private static StatsData pendingStats;
        private static Offer pendingPackage;

        /// <summary>CharacterSelectionController.InitializeContainers on the pod screen: this round's cards.</summary>
        public static bool InitializeContainers(CharacterSelectionController screen)
        {
            if (!(screen is ImmigrantScreen) || !Active) return true;
            if (current == null && CommandBus.IsHost && Immigration.Instance != null && Immigration.Instance.ImmigrantsAvailable) { current = Generate(); SendOffers(); }
            if (current == null) { Log.Warn("printing pod: no offers from the host yet; the pod screen makes its own (they can't be printed)"); return true; }
            var t = Traverse.Create(screen);
            var containers = t.Field("containers").GetValue<List<ITelepadDeliverableContainer>>();
            AccessTools.Method(typeof(CharacterSelectionController), "DisableProceedButton").Invoke(screen, null);
            if (containers != null && containers.Count > 0)
            {
                if (dealtSet == current.id) return false; // this round's cards are already there
                foreach (var c in containers) Object.Destroy(c.GetGameObject());
            }
            screen.OnReplacedEvent = null;
            containers = new List<ITelepadDeliverableContainer>();
            t.Field("containers").SetValue(containers);
            var dupePrefab = t.Field("containerPrefab").GetValue<CharacterContainer>();
            var packagePrefab = t.Field("carePackageContainerPrefab").GetValue<CarePackageContainer>();
            var parent = t.Field("containerParent").GetValue<GameObject>();
            dealt.Clear();
            byDeliverable.Clear();
            for (int i = 0; i < current.offers.Count; i++)
            {
                if (current.offers[i].dupe != null)
                {
                    var card = Util.KInstantiateUI<CharacterContainer>(dupePrefab.gameObject, parent);
                    card.SetController(screen);
                    card.SetReshufflingState(enable: true);
                    containers.Add(card);
                    dealt[card] = i;
                }
                else
                {
                    var card = Util.KInstantiateUI<CarePackageContainer>(packagePrefab.gameObject, parent);
                    card.SetController(screen);
                    containers.Add(card);
                    dealt[card] = i;
                }
            }
            t.Field("selectedDeliverables").SetValue(new List<ITelepadDeliverable>());
            dealtSet = current.id;
            return false;
        }

        public static void BeforeDupeCard(CharacterContainer card)
        {
            pendingStats = current != null && dealt.TryGetValue(card, out int i) && i < current.offers.Count ? current.offers[i].dupe : null;
        }

        public static void AfterDupeCard(CharacterContainer card)
        {
            if (pendingStats != null && dealt.TryGetValue(card, out int i) && card.Stats != null) byDeliverable[card.Stats] = i;
            pendingStats = null;
        }

        /// <summary>MinionStartingStats constructor: the card's new duplicant is the offered one.</summary>
        public static void NewStats(MinionStartingStats s) => pendingStats?.ApplyTo(s);

        public static void BeforePackageCard(CarePackageContainer card)
        {
            pendingPackage = current != null && dealt.TryGetValue(card, out int i) && i < current.offers.Count && current.offers[i].dupe == null ? current.offers[i] : null;
        }

        public static void AfterPackageCard(CarePackageContainer card)
        {
            if (pendingPackage != null && dealt.TryGetValue(card, out int i) && card.carePackageInstanceData != null)
            {
                card.carePackageInstanceData.facadeID = pendingPackage.facade;
                byDeliverable[card.carePackageInstanceData] = i;
            }
            pendingPackage = null;
        }

        /// <summary>Immigration.RandomCarePackage while a card is being filled: the offered package.</summary>
        public static void PickPackage(ref CarePackageInfo result)
        {
            if (pendingPackage == null) return;
            var all = Traverse.Create(Immigration.Instance).Field("carePackages").GetValue<List<CarePackageInfo>>();
            CarePackageInfo match = all?.FirstOrDefault(p => p.id == pendingPackage.package && Mathf.Approximately(p.quantity, pendingPackage.quantity));
            result = match ?? new CarePackageInfo(pendingPackage.package, pendingPackage.quantity, null, pendingPackage.facade);
        }

        /// <summary>Round over: the cards of the pod screen go (and the screen, if open).</summary>
        private static void ClearScreen()
        {
            ImmigrantScreen screen = ImmigrantScreen.instance;
            if (screen == null) return;
            var t = Traverse.Create(screen);
            var containers = t.Field("containers").GetValue<List<ITelepadDeliverableContainer>>();
            if (containers != null)
            {
                foreach (var c in containers) if (c != null && c.GetGameObject() != null) Object.Destroy(c.GetGameObject());
                containers.Clear();
            }
            dealt.Clear();
            byDeliverable.Clear();
            dealtSet = 0;
            if (screen.isActiveAndEnabled) screen.Show(false);
        }

        // ================================================================== printing / rejecting

        /// <summary>Telepad.OnAcceptDelivery: a guest asks the host instead.</summary>
        public static bool BeforeAccept(Telepad pad, ITelepadDeliverable delivery)
        {
            if (!IsGuest) return true;
            Requests++;
            if (current == null || delivery == null || !byDeliverable.TryGetValue(delivery, out int index))
            {
                Log.Warn("printing pod: that choice isn't one of the host's offers; nothing printed");
                return false;
            }
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) { w.Write(MChoose); w.Write(current.id); w.Write(index); w.Write(pad.GetComponent<KPrefabID>().InstanceID); }
            CommandBus.Client.SendRelay(Proto.TargetHost, Ch, ms.ToArray());
            Log.Info($"printing pod: asked the host to print {Describe(current.offers[index])}");
            return false;
        }

        public static void AfterAccept()
        {
            if (Active && CommandBus.IsHost) { Printed++; Consumed(); }
        }

        public static bool BeforeReject(Telepad pad)
        {
            if (!IsGuest) return true;
            Requests++;
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms)) { w.Write(MReject); w.Write(current != null ? current.id : 0); w.Write(pad.GetComponent<KPrefabID>().InstanceID); }
            CommandBus.Client.SendRelay(Proto.TargetHost, Ch, ms.ToArray());
            return false;
        }

        public static void AfterReject()
        {
            if (Active && CommandBus.IsHost) { Rejected++; Consumed(); }
        }

        public static string Report() => $"printing pod: round {(current != null ? current.id : 0)}, printed {Printed}, rejected {Rejected}, guest requests {Requests}";

        // ================================================================== patches (applied at the main menu)

        public static void ApplyPatches(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(CharacterSelectionController), "InitializeContainers"), prefix: new HarmonyMethod(typeof(Printing), nameof(InitPrefix)));
                h.Patch(AccessTools.Method(typeof(CharacterContainer), nameof(CharacterContainer.GenerateCharacter)),
                    prefix: new HarmonyMethod(typeof(Printing), nameof(DupeCardPrefix)), postfix: new HarmonyMethod(typeof(Printing), nameof(DupeCardPostfix)));
                h.Patch(AccessTools.Constructor(typeof(MinionStartingStats), new[] { typeof(List<Tag>), typeof(bool), typeof(string), typeof(string), typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(Printing), nameof(StatsPostfix)));
                h.Patch(AccessTools.Method(typeof(CarePackageContainer), "GenerateCharacter"),
                    prefix: new HarmonyMethod(typeof(Printing), nameof(PackageCardPrefix)), postfix: new HarmonyMethod(typeof(Printing), nameof(PackageCardPostfix)));
                h.Patch(AccessTools.Method(typeof(Immigration), nameof(Immigration.RandomCarePackage)), postfix: new HarmonyMethod(typeof(Printing), nameof(PackagePostfix)));
                h.Patch(AccessTools.Method(typeof(Telepad), nameof(Telepad.OnAcceptDelivery)),
                    prefix: new HarmonyMethod(typeof(Printing), nameof(AcceptPrefix)), postfix: new HarmonyMethod(typeof(Printing), nameof(AcceptPostfix)));
                h.Patch(AccessTools.Method(typeof(Telepad), nameof(Telepad.RejectAll)),
                    prefix: new HarmonyMethod(typeof(Printing), nameof(RejectPrefix)), postfix: new HarmonyMethod(typeof(Printing), nameof(RejectPostfix)));
            }
            catch (System.Exception e) { Log.Warn("printing pod: patching failed: " + e.Message); }
        }

        private static bool InitPrefix(CharacterSelectionController __instance) => InitializeContainers(__instance);
        private static void DupeCardPrefix(CharacterContainer __instance) => BeforeDupeCard(__instance);
        private static void DupeCardPostfix(CharacterContainer __instance) => AfterDupeCard(__instance);
        private static void StatsPostfix(MinionStartingStats __instance) => NewStats(__instance);
        private static void PackageCardPrefix(CarePackageContainer __instance) => BeforePackageCard(__instance);
        private static void PackageCardPostfix(CarePackageContainer __instance) => AfterPackageCard(__instance);
        private static void PackagePostfix(ref CarePackageInfo __result) => PickPackage(ref __result);
        private static bool AcceptPrefix(Telepad __instance, ITelepadDeliverable __0) => BeforeAccept(__instance, __0);
        private static void AcceptPostfix(Telepad __instance) { if (!IsGuest) AfterAccept(); }
        private static bool RejectPrefix(Telepad __instance) => BeforeReject(__instance);
        private static void RejectPostfix(Telepad __instance) { if (!IsGuest) AfterReject(); }
    }
}
