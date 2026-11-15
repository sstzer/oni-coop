namespace OniCoop
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Lives for the whole process (DontDestroyOnLoad): the relay connection, join flow, world transfer,
    /// presence, and the on-screen overlay.</summary>
    public sealed class CoopRuntime : MonoBehaviour
    {
        public static CoopRuntime Instance { get; private set; }

        private enum Phase { Offline, Connecting, Online, Rejected }

        private sealed class Remote
        {
            public PresenceData data;
            public float lastSeen;
            public bool shown;
            public float x0, y0, x1, y1, cx, cy; // smoothed for display
        }

        private CoopSettings S;
        private CoopClient client;
        private Phase phase = Phase.Offline;
        private float retryAt;
        private int retries;
        private RejectMsg reject;
        private string closeReason = "";
        private string notice = "";
        private float noticeAt;
        private List<string> warnings = new List<string>();
        private float warningsAt;
        private List<MemberInfo> members = new List<MemberInfo>();
        private ushort myId;
        private readonly Dictionary<ushort, Remote> remotes = new Dictionary<ushort, Remote>();
        private Manifest manifest;

        private bool gameSpawned;
        private bool wantWorld;      // guest: send me the host's world
        private bool downloading;
        private bool loading;
        private string pendingLoad;
        private MemberState sentState = (MemberState)255;
        private bool hostSaving;
        private bool resyncRequested;
        private readonly Dictionary<ushort, MemberState> lastStates = new Dictionary<ushort, MemberState>();
        private float gameStartAt;
        private uint presenceSeq;
        private float nextPresenceAt;

        // test helpers
        private bool autoLoadDone;
        private float windowCheckUntil, nextWindowFix;
        private bool windowMoved;
        private int shotCount;
        private float nextShotAt;
        private Vector3 wanderOrigin;

        private bool InGame => gameSpawned && Game.Instance != null;

        // ================================================================== lifecycle

        private void Awake()
        {
            Instance = this;
            S = CoopMod.Settings;
            wantWorld = !S.IsHost;
            useGUILayout = false; // the panel is laid out by hand (no Layout pass, no GUILayout garbage)
            if (Application.isBatchMode)
            {
                // Nothing is drawn: don't spin a core at hundreds of fps. The simulation and dupe AI only need a
                // normal frame rate (BrainScheduler runs per frame).
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
            }
            if (S.TryGetWindow(out _, out _, out int w, out int h))
            {
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
                windowCheckUntil = Time.unscaledTime + 60f;
            }
        }

        private void OnApplicationQuit()
        {
            ServerSpeed.WaitWriter();
            client?.Close("game closed");
        }

        private void OnDestroy() => client?.Dispose();

        public void OnMainMenu()
        {
            if (S.IsHost && !autoLoadDone && !string.IsNullOrEmpty(S.autoLoad))
            {
                autoLoadDone = true;
                StartCoroutine(AutoLoad(S.autoLoad));
            }
        }

        private IEnumerator AutoLoad(string file)
        {
            yield return new WaitForSecondsRealtime(2f);
            if (!File.Exists(file)) { Log.Warn("autoLoad: no such save " + file); yield break; }
            if (!InGame) GameBridge.LoadWorld(file);
        }

        public void OnGameSpawned()
        {
            gameSpawned = true;
            loading = false;
            wantWorld = false;
            pendingLoad = null;
            resyncRequested = false;
            Puppets.Reset();
            WorldSync.Reset();
            EntitySync.Reset();
            BuildingSync.Reset();
            GlobalSync.Reset();
            StatusSync.Reset();
            ReportSync.Reset();
            OperationSync.Reset();
            Feedback.Reset();
            Meteors.Reset();
            Printing.Reset();
            if (!S.IsHost) EntitySync.UseLocalIds();
            // headroom for the garbage collector: a guest, and the server (its pauses stop every guest's stream)
            if (!S.IsHost || S.IsServer) StartCoroutine(EnterGc());
            gameStartAt = Time.realtimeSinceStartup;
            hostGone = false;
            pausedForEmpty = S.IsServer; // a loaded game starts paused: the first player in starts it
            nextShotAt = gameStartAt + 8f;
            if (Camera.main != null) wanderOrigin = Camera.main.transform.position;
            Log.Info($"in game: colony '{GameBridge.ColonyName()}', dlc [{string.Join(",", GameBridge.GameDlcs().ToArray())}], world {GameBridge.ActiveWorld()}");
        }

        /// <summary>Unity's garbage collector (Boehm) collects whenever what was allocated since the last collection
        /// reaches a fraction of the heap, and each collection stops the game for ~0.5 s with a 1.5 GB colony. A host's
        /// heap grows a lot when it saves the world for a guest, so it collects about once every minute or two; a
        /// guest's heap is just big enough, so it collected every ~15 s. Growing it once, right after loading (one
        /// pause then), gives the guest the same headroom. Costs that much more memory.</summary>
        /// <summary>A few seconds into a game: the collector goes manual (GcPolicy); where that isn't available, the heap
        /// is grown once instead so automatic collections come less often.</summary>
        private IEnumerator EnterGc()
        {
            yield return new WaitForSecondsRealtime(3f);
            if (!InGame) yield break;
            // room in the heap first: in manual mode a heap without free space still gets collected when it has to grow
            // (seen on the server: a collection a minute with 15-25 MB of growth, until the heap was grown up front)
            if (S.heapReserveMb > 0) yield return ReserveHeap(S.IsServer ? S.heapReserveMb * 2 : S.heapReserveMb, false);
            GcPolicy.Enter(S.IsServer);
        }

        private IEnumerator ReserveHeap(int mb, bool wait = true)
        {
            if (wait) yield return new WaitForSecondsRealtime(3f);
            if (!InGame || mb <= 0) yield break;
            var sw = Stopwatch.StartNew();
            long before = GC.GetTotalMemory(false);
            var chunks = new List<byte[]>(mb);
            for (int i = 0; i < mb; i++) chunks.Add(new byte[1 << 20]);
            chunks = null;
            GC.Collect();
            Log.Info($"heap: grew by {mb} MB in {sw.ElapsedMilliseconds} ms (in use {before / 1048576} -> {GC.GetTotalMemory(false) / 1048576} MB); " +
                     $"incremental GC {(UnityEngine.Scripting.GarbageCollector.isIncremental ? "available" : "not available")}, mode {UnityEngine.Scripting.GarbageCollector.GCMode}");
        }

        private void OnLeftGame()
        {
            GcPolicy.Leave();
            gameSpawned = false;
            remotes.Clear();
            Puppets.Mirroring = false;
            Puppets.Reset();
            WorldSync.Reset();
            Log.Info("left the game");
        }

        // ================================================================== frame

        private void Update()
        {
            float now = Time.unscaledTime;
            if (InGame) FrameProfiler.Frame(Time.realtimeSinceStartup);
            if (InGame) ServerSpeed.Tick(now);
            if (InGame && GcPolicy.Active)
                GcPolicy.Tick(now, S.IsServer, (SpeedControlScreen.Instance != null && SpeedControlScreen.Instance.IsPaused) || EntitySync.GuestsInGame == 0);
            KeepWindow(now);
            if (gameSpawned && Game.Instance == null) OnLeftGame();

            if (client == null && phase != Phase.Rejected && now >= retryAt) Connect();
            if (client != null)
            {
                client.Tick();
                while (client != null && client.TryPoll(out ClientEvent ev)) Handle(ev, now);
            }
            CommandBus.InGame = InGame;
            if (client != null && client.Welcomed)
            {
                SyncState();
                if (InGame && !S.IsServer) SendPresence(now); // the server has no view of its own
                if (InGame && S.IsHost) SpeedSync.HostTick(now);
            }
            MirrorTick(now);
            if (pendingLoad != null && ((!InGame && MainMenu.Instance != null) || (InGame && resyncRequested)))
            {
                string f = pendingLoad;
                pendingLoad = null;
                StartCoroutine(LoadSoon(f));
            }
            SmoothRemotes(now);
            if (InGame) TestHelpers(Time.realtimeSinceStartup);
        }

        private void LateUpdate()
        {
            CommandBus.Flush();
            EntitySync.HostFlush(Time.unscaledTime); // after the orders: a guest replays an order before hearing of its objects
        }

        private bool resyncOnReconnect, hostGone, hostWasHere, pausedForEmpty, serverSaving;
        private float savingSince;

        /// <summary>Server: nobody in the game -> pause (the colony doesn't live on without its players); somebody is
        /// back -> carry on, unless a player had paused it themselves.</summary>
        private void PauseWhileEmpty(int playersInGame)
        {
            SpeedControlScreen speed = SpeedControlScreen.Instance;
            if (speed == null) return;
            if (playersInGame == 0 && !pausedForEmpty && !speed.IsPaused)
            {
                pausedForEmpty = true;
                speed.Pause(false);
                Log.Info("server: nobody is in the game, paused");
            }
            else if (playersInGame > 0 && pausedForEmpty)
            {
                pausedForEmpty = false;
                if (speed.IsPaused) speed.Unpause(false);
                Log.Info($"server: {playersInGame} player(s) in the game, running");
            }
        }

        /// <summary>Guest in a game: the host left (or lost the relay) -> pause and say so; it's back -> take its world
        /// again (whatever it did while we couldn't hear it is missing here).</summary>
        private void WatchHost()
        {
            if (S.IsHost) return;
            bool here = members.Exists(m => m.role == Role.Host && m.state == MemberState.InGame);
            if (InGame && hostWasHere && !here && !hostGone)
            {
                hostGone = true;
                Log.Info("the host left: pausing until it is back");
                if (SpeedControlScreen.Instance != null && !SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Pause(false);
            }
            if (here && hostGone)
            {
                hostGone = false;
                if (InGame && !resyncRequested)
                {
                    Log.Info("the host is back: getting its world again");
                    resyncRequested = true;
                }
            }
            hostWasHere = here;
        }

        /// <summary>Host: a save of the game (autosave or the player's) also goes to the relay, kept there as a backup.
        /// The file is read and sent off the main thread.</summary>
        public const byte ChNotice = 19; // server -> guests: [1] saving now, [2] done

        /// <summary>Server: a save freezes the game for seconds (as in single player). Guests are told, so they can
        /// show why everything stopped and do their own garbage collection while they wait anyway.</summary>
        public void OnHostSaving()
        {
            if (S.IsHost && client != null && client.Welcomed && EntitySync.GuestsInGame > 0)
                client.SendRelay(Proto.TargetAll, ChNotice, new byte[] { 1 });
        }

        public void OnHostSaved(string path)
        {
            if (S.IsServer)
            {
                // the save left a lot of garbage; collecting now adds to a pause everyone already has, instead of a
                // separate one a minute later
                // the game's own SaveLoader.Save collects at its end: just note it (collecting again freed nothing and
                // cost another 0.4 s for everyone)
                if (GcPolicy.Active) GcPolicy.NoteCollected("the game collected at the end of its save");
                else
                {
                    var sw = Stopwatch.StartNew();
                    GC.Collect();
                    Log.Info($"saved; collected garbage in {sw.ElapsedMilliseconds} ms");
                }
            }
            if (S.IsHost && client != null && client.Welcomed && EntitySync.GuestsInGame > 0)
                client.SendRelay(Proto.TargetAll, ChNotice, new byte[] { 2 });
            CoopClient c = client;
            if (!S.IsHost || c == null || !c.Welcomed || string.IsNullOrEmpty(path)) return;
            new Thread(() =>
            {
                try
                {
                    ServerSpeed.WaitWriter(); // (the server writes the end of its saves in the background)
                    byte[] data = File.ReadAllBytes(path);
                    c.SendSave(0, Path.GetFileName(path), data);
                    Log.Info($"backup: {Path.GetFileName(path)} ({data.Length / 1048576.0:F1} MB) going to the relay in the background (after the game's messages, {c.BackupRate / 1024} KB/s)");
                }
                catch (Exception e) { Log.Warn("backup failed: " + e.Message); }
            }) { IsBackground = true, Name = "coop-backup" }.Start();
        }

        /// <summary>Test: lose the relay (as a network drop would); the usual retry reconnects.</summary>
        public void TestDrop() => client?.Close("autotest drop");

        private void Connect()
        {
            if (manifest == null)
            {
                manifest = GameBridge.BuildManifest();
                GameBridge.DumpManifest(manifest);
                Log.Info("manifest: " + manifest.Describe());
            }
            string host = S.server;
            int port = Proto.DefaultPort;
            int colon = S.server.LastIndexOf(':');
            if (colon > 0 && S.server.IndexOf(':') == colon && int.TryParse(S.server.Substring(colon + 1), out int p))
            {
                host = S.server.Substring(0, colon);
                port = p;
            }
            var hello = new HelloMsg
            {
                token = S.token, room = S.room, name = S.IsServer && string.IsNullOrEmpty(S.name) ? "服务器" : S.name, role = S.IsHost ? Role.Host : Role.Guest,
                wantWorld = !S.IsHost && wantWorld && !InGame, manifest = manifest,
            };
            var sim = LinkSim.Parse(S.netsim);
            Log.Info($"connecting to {host}:{port} as {hello.role} '{hello.name}', room '{hello.room}', wantWorld={hello.wantWorld}, netsim {sim}");
            client = new CoopClient(host, port, hello, sim, m => Log.Info("net: " + m));
            client.Start();
            phase = Phase.Connecting;
            reject = null;
            sentState = (MemberState)255;
        }

        /// <summary>Guest: what the server's game did to the world (objects and items, cells, pipes, status icons,
        /// whether buildings work) is shown when the duplicants' timeline gets to the moment the server sent it, not
        /// when it arrives (half a second earlier than the duplicants are shown): an item reaches a duplicant's hands
        /// when the duplicant gets to it, a tile goes when the digging ends. A message is stamped with the newest
        /// puppet message's time seen before it (the server sends those 10 times a second, all in one ordered stream).
        /// Players' orders and panel settings stay immediate (they are the players' own actions). Global values (the
        /// bars: calories, stress...; research, the clock) are on the timeline too.</summary>
        private struct Held { public ushort from; public byte channel; public byte[] data; public float at; }
        private readonly Queue<Held> held = new Queue<Held>();
        public static long HeldMessages;

        private static bool OnTimeline(byte channel) =>
            channel == EntitySync.ChEntities || channel == WorldSync.ChCells || channel == BuildingSync.ChPipes
            || channel == StatusSync.Ch || channel == OperationSync.Ch || channel == GlobalSync.ChGlobal || channel == AutoTest.ChShot;

        /// <summary>Guest, every frame after the puppets moved: what the timeline reached.</summary>
        private void ReleaseHeld(float now)
        {
            while (held.Count > 0)
            {
                Held h = held.Peek();
                // (far ahead of what is shown: the server's clock started over; release now)
                if (h.at > Puppets.RenderTime && h.at < Puppets.RenderTime + 10f) break;
                held.Dequeue();
                HeldMessages++;
                Relay(h.from, h.channel, h.data, 0, now);
            }
        }

        private void Relay(ushort from, byte channel, byte[] data, long waitedMs, float now)
        {
            if (channel == Puppets.ChPuppets) { if (Puppets.Mirroring) Puppets.OnSnapshot(data, waitedMs); }
            else if (channel == WorldSync.ChCells) { if (Puppets.Mirroring) WorldSync.OnCells(data); }
            else if (channel == EntitySync.ChEntities) { if (!S.IsHost) EntitySync.OnRecords(data); }
            else if (channel == GlobalSync.ChGlobal) { if (!S.IsHost) GlobalSync.OnRecords(data); }
            else if (channel == ReportSync.Ch) { if (!S.IsHost) ReportSync.OnRecords(data); }
            else if (channel == StatusSync.Ch) { if (!S.IsHost) StatusSync.OnRecords(data); else StatusSync.OnReport(data); }
            else if (channel == Printing.Ch) { if (InGame) Printing.OnRelay(from, data); }
            else if (channel == Calls.ChRequest || channel == Calls.ChApplied) Calls.OnRelay(from, channel, data);
            else if (channel == BuildingSync.ChPipes) { if (!S.IsHost) BuildingSync.OnPipes(data); }
            else if (channel == EntitySync.ChAsk) { if (S.IsHost) EntitySync.OnAsk(from, data); }
            else if (channel == PuppetLooks.ChAsk) { if (S.IsHost) Puppets.OnLooksAsk(data); }
            else if (channel == OperationSync.Ch) { if (!S.IsHost) OperationSync.OnRecords(data); }
            else if (channel == AutoTest.ChShot) AutoTest.SyncShot(S, data);
            else if (channel == AutoTest.ChPhase) { if (!S.IsHost) AutoTest.OnPhase(data); }
            else if (channel == ChNotice && !S.IsHost && data.Length > 0)
            {
                serverSaving = data[0] == 1;
                savingSince = now;
                if (serverSaving) Puppets.CollectWhenWaiting = true;
                Log.Info(serverSaving ? "the server is saving" : "the server saved");
            }
            else CommandBus.OnRelay(from, channel, data);
        }

        private void Handle(ClientEvent ev, float now)
        {
            if (ev.type != EvType.Presence && ev.type != EvType.Members && ev.type != EvType.RelayFrom) Log.Info("event: " + ev);
            switch (ev.type)
            {
                case EvType.Connected:
                    CommandBus.Client = client;
                    CommandBus.IsHost = S.IsHost;
                    myId = ev.welcome.id;
                    phase = Phase.Online;
                    retries = 0;
                    warnings = ev.welcome.warnings;
                    warningsAt = now;
                    if (resyncOnReconnect && !S.IsHost && InGame)
                    {
                        Log.Info("reconnected during a game: getting the host's world again");
                        resyncRequested = true;
                    }
                    resyncOnReconnect = false;
                    foreach (string w in warnings) Log.Warn("join warning: " + w);
                    break;
                case EvType.Rejected:
                    reject = ev.reject;
                    foreach (string d in reject.details) Log.Warn("  " + d);
                    break;
                case EvType.Closed:
                    // a guest that loses the relay in a game misses whatever happened meanwhile: take the world again
                    if (!S.IsHost && InGame) resyncOnReconnect = true;
                    client = null;
                    CommandBus.Client = null;
                    members.Clear();
                    remotes.Clear();
                    downloading = false;
                    hostSaving = false;
                    if (reject != null) phase = Phase.Rejected;
                    else
                    {
                        phase = Phase.Offline;
                        closeReason = ev.text;
                        retries++;
                        retryAt = now + Mathf.Min(15f, 2f * retries);
                    }
                    break;
                case EvType.Members:
                    members = ev.members;
                    WatchHost();
                    foreach (MemberInfo m in members)
                    {
                        // a guest that (re)entered the game has the host's save, not what was streamed to it before
                        lastStates.TryGetValue(m.id, out MemberState was);
                        if (S.IsHost && m.state == MemberState.InGame && was != MemberState.InGame) { WorldSync.ResetGuest(m.id); EntitySync.OnGuestJoined(); BuildingSync.OnGuestJoined(); BuildingSync.ResetGuest(m.id); GlobalSync.OnGuestJoined(); StatusSync.OnGuestJoined(); ReportSync.OnGuestJoined(); Printing.OnGuestJoined(); Puppets.OnGuestJoined(); OperationSync.OnGuestJoined(); Feedback.OnGuestJoined(); }
                        lastStates[m.id] = m.state;
                    }
                    var alive = new HashSet<ushort>();
                    foreach (MemberInfo m in members) alive.Add(m.id);
                    foreach (ushort id in new List<ushort>(remotes.Keys)) if (!alive.Contains(id)) remotes.Remove(id);
                    Log.Info("members: " + DescribeMembers());
                    break;
                case EvType.Notice:
                    notice = ev.text;
                    noticeAt = now;
                    break;
                case EvType.SaveRequest:
                    if (S.IsHost) StartCoroutine(SaveAndSend(ev.requestId));
                    break;
                case EvType.SaveIncoming:
                    downloading = true;
                    notice = "";
                    break;
                case EvType.SaveReceived:
                    downloading = false;
                    OnWorldReceived(ev.data);
                    break;
                case EvType.SaveFailed:
                    downloading = false;
                    notice = ev.text;
                    noticeAt = now;
                    break;
                case EvType.RelayFrom:
                    // the world the server's game made goes on the same timeline as the duplicants (see Held)
                    if (!S.IsHost && Puppets.Mirroring && Puppets.TimelineReady && OnTimeline(ev.channel))
                    {
                        if (ev.channel == AutoTest.ChShot) AutoTest.ShotComing(ev.data);
                        held.Enqueue(new Held { from = ev.from, channel = ev.channel, data = ev.data, at = Puppets.LatestSent });
                    }
                    else Relay(ev.from, ev.channel, ev.data, ev.waitedMs, now);
                    break;
                case EvType.Presence:
                {
                    if (!remotes.TryGetValue(ev.from, out Remote r)) remotes[ev.from] = r = new Remote();
                    if (r.data != null && ev.presence.seq <= r.data.seq) break; // late / duplicate
                    r.data = ev.presence;
                    r.lastSeen = now;
                    break;
                }
            }
        }

        /// <summary>Step 3: host streams duplicants/critters and each guest's visible cells; a guest follows them.</summary>
        private void MirrorTick(float now)
        {
            bool online = client != null && client.Welcomed && InGame;
            bool mirror = online && !S.IsHost && S.mirror && !resyncRequested;
            if (mirror != Puppets.Mirroring)
            {
                Puppets.Mirroring = mirror;
                Log.Info(mirror ? "mirroring the host's duplicants, critters and cells" : "mirroring off");
                if (!mirror) { held.Clear(); Feedback.Reset(); Meteors.Reset(); Puppets.Reset(); WorldSync.Reset(); EntitySync.Reset(); BuildingSync.Reset(); GlobalSync.Reset(); StatusSync.Reset(); ReportSync.Reset(); OperationSync.Reset(); }
            }
            if (!online) return;
            if (S.IsHost)
            {
                int inGame = 0;
                foreach (MemberInfo m in members) if (m.role == Role.Guest && m.state == MemberState.InGame) inGame++;
                EntitySync.GuestsInGame = inGame;
                if (S.IsServer) PauseWhileEmpty(inGame);
                Puppets.HostTick();
                Feedback.HostTick();
                Meteors.HostTick();
                Printing.HostTick();
                if (inGame > 0) { BuildingSync.HostTick(Time.realtimeSinceStartup); GlobalSync.HostTick(Time.realtimeSinceStartup); StatusSync.HostTick(Time.realtimeSinceStartup); ReportSync.HostTick(Time.realtimeSinceStartup); OperationSync.HostTick(Time.realtimeSinceStartup); }
                foreach (MemberInfo m in members)
                    if (m.role == Role.Guest && m.state == MemberState.InGame && remotes.TryGetValue(m.id, out Remote r) && r.data != null)
                    {
                        WorldSync.HostTick(now, m.id, r.data);
                        BuildingSync.HostPipes(now, m.id, r.data);
                    }
            }
            else if (mirror)
            {
                Puppets.GuestTick();
                ReleaseHeld(now);
                PuppetLooks.GuestTick(Time.realtimeSinceStartup);
                AutoTest.PendingShots(S);
                EntitySync.GuestTick(Time.realtimeSinceStartup);
                BuildingSync.GuestTick(now);
                OperationSync.GuestTick(Time.realtimeSinceStartup);
                StatusSync.GuestTick();
                ReportSync.GuestTick(now);
                WorldSync.GuestTick(now);
                if (S.resyncEvery > 0 && Time.realtimeSinceStartup - gameStartAt > S.resyncEvery * 60f && !resyncRequested)
                {
                    Log.Info($"automatic resync after {S.resyncEvery} min");
                    resyncRequested = true;
                }
            }
        }

        private void SyncState()
        {
            // order matters: a resync passes WaitingForWorld -> Downloading -> Loading -> InGame again
            MemberState st = loading || pendingLoad != null ? MemberState.Loading
                : downloading ? MemberState.Downloading
                : resyncRequested && InGame ? MemberState.WaitingForWorld
                : InGame ? MemberState.InGame
                : !S.IsHost && wantWorld ? MemberState.WaitingForWorld
                : MemberState.Lobby;
            if (st == sentState) return;
            sentState = st;
            client.SendState(new StateMsg { state = st, gameDlcs = InGame ? GameBridge.GameDlcs() : new List<string>(), colony = InGame ? GameBridge.ColonyName() : "" });
        }

        private void SendPresence(float now)
        {
            if (now < nextPresenceAt) return;
            nextPresenceAt = now + 0.1f;
            var p = new PresenceData { seq = ++presenceSeq, rttMs = (ushort)Mathf.Clamp(client.RttMs, 0, 65535) };
            if (GameBridge.Capture(p, S.wander, Time.realtimeSinceStartup - gameStartAt)) client.SendPresence(p);
        }

        // ================================================================== world transfer

        private IEnumerator SaveAndSend(uint requestId)
        {
            hostSaving = true;
            yield return null; // let the overlay show "saving" first
            CoopClient c = client;
            if (c == null) { hostSaving = false; yield break; }
            if (!InGame)
            {
                c.SendSaveFailed(requestId, "服务器不在游戏中");
                hostSaving = false;
                yield break;
            }
            // Only the snapshot blocks the game; compression and upload run on a worker thread.
            GameBridge.WorldSnapshot snap = null;
            var sw = Stopwatch.StartNew();
            try { snap = GameBridge.CaptureWorld(); }
            catch (Exception e) { Log.Warn("world snapshot failed, falling back to a normal save: " + e); }
            long snapMs = sw.ElapsedMilliseconds;
            if (snap == null)
            {
                byte[] data = null;
                string name = null, error = null;
                try { data = GameBridge.SaveWorld(out name); }
                catch (Exception e) { error = e.Message; Log.Warn("saving the world for guests failed: " + e); }
                hostSaving = false;
                if (data == null) c.SendSaveFailed(requestId, error);
                else
                {
                    Log.Info($"world #{requestId} saved for guests (fallback): {data.Length / 1048576f:F2} MB in {sw.ElapsedMilliseconds} ms, uploading");
                    c.SendSave(requestId, name, data);
                }
                yield break;
            }
            hostSaving = false;
            var worker = new Thread(() =>
            {
                try
                {
                    var t = Stopwatch.StartNew();
                    byte[] data = GameBridge.EncodeWorld(snap);
                    long encMs = t.ElapsedMilliseconds;
                    c.SendSave(requestId, snap.name, data);
                    Log.Info($"world #{requestId} for guests: snapshot {snapMs} ms on the main thread ({snap.bodyLength / 1048576f:F1} MB raw), " +
                             $"compress {encMs} ms + hash {t.ElapsedMilliseconds - encMs} ms in background -> {data.Length / 1048576f:F2} MB, uploading");
                }
                catch (Exception e)
                {
                    Log.Warn("compressing the world failed: " + e);
                    c.SendSaveFailed(requestId, e.Message);
                }
            }) { IsBackground = true, Name = "coop-world-encode" };
            worker.Start();
        }

        private void OnWorldReceived(byte[] data)
        {
            if (S.IsHost) return;
            if (InGame && !resyncRequested)
            {
                Log.Warn("received a world while already in a game; ignored");
                return;
            }
            string file = GameBridge.WriteReceivedWorld(S.room, data);
            Log.Info($"world received: {data.Length / 1048576f:F2} MB -> {file}");
            wantWorld = false;
            pendingLoad = file;
        }

        private IEnumerator LoadSoon(string file)
        {
            loading = true;
            yield return null;
            yield return null; // let the Loading state go out
            GameBridge.LoadWorld(file);
        }

        // ================================================================== test helpers

        private void KeepWindow(float now)
        {
            if (now > windowCheckUntil || now < nextWindowFix || !S.TryGetWindow(out int x, out int y, out int w, out int h)) return;
            nextWindowFix = now + 1f;
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != w || Screen.height != h)
            {
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
                windowMoved = false;
                return;
            }
            if (!windowMoved) windowMoved = WindowUtil.Move(x, y);
        }

        private void TestHelpers(float now)
        {
            float t = now - gameStartAt;
            if (S.wander && t > 3f) GameBridge.Wander(wanderOrigin, t - 3f);
            if (S.screenshotEvery > 0 && !Application.isBatchMode && shotCount < S.maxScreenshots && now >= nextShotAt) // a headless server can't
            {
                nextShotAt = now + S.screenshotEvery;
                string dir = Path.Combine(GameBridge.CoopDir, "shots");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"{S.name}_{shotCount++:00}.png");
                ScreenCapture.CaptureScreenshot(file);
                Log.Info("screenshot " + file);
            }
            if (Application.isBatchMode && now >= nextHeadlessAt)
            {
                nextHeadlessAt = now + 1f;
                HeadlessTick(t);
            }
            else if (S.unpause && t > 5f && !Application.isBatchMode)
            {
                S.unpause = false;
                // paused on load, and maybe by a pop-up that opened before coop was up: clear them all
                for (int i = 0; i < 10 && SpeedControlScreen.Instance != null && SpeedControlScreen.Instance.IsPaused; i++)
                    SpeedControlScreen.Instance.Unpause(false);
                Log.Info("unpaused");
            }
            statFrames++;
            if (S.statsEvery > 0 && now >= nextStatsAt)
            {
                if (nextStatsAt > 0) LogStats(now);
                nextStatsAt = now + S.statsEvery;
                statFrames = 0;
                statSince = now;
            }
            AutoTest.Tick(this, S, t);
            if (S.saveBench && t > 20f)
            {
                S.saveBench = false;
                SaveBench();
            }
            if (S.quitAfter > 0 && t > S.quitAfter)
            {
                S.quitAfter = 0;
                Log.Info("quitAfter reached, quitting");
                client?.Close("test finished");
                App.Quit();
            }
        }

        private float nextHeadlessAt;

        /// <summary>No screen, nobody to click: answer the game's pop-ups (e.g. "mods differ from the save") and keep
        /// the game running if asked to. Every dialog is logged.</summary>
        /// <summary>Headless: pop-ups that were enabled or shown (instead of searching the scene every second, which
        /// took 150-260 ms on the server, a hitch every second for every guest).</summary>
        internal static readonly HashSet<KModalScreen> OpenedModals = new HashSet<KModalScreen>();

        private void HeadlessTick(float t)
        {
            if (OpenedModals.Count == 0) { HeadlessUnpause(t); return; }
            var opened = new List<KModalScreen>(OpenedModals);
            OpenedModals.Clear();
            foreach (KModalScreen m in opened)
            {
                // Only pop-ups that are actually up and holding the game paused; screens like ResearchScreen
                // stay active and are just hidden.
                if (m == null || !m.isActiveAndEnabled || !m.pause) continue;
                bool shown = false;
                try { shown = HarmonyLib.Traverse.Create(m).Field("shown").GetValue<bool>(); } catch { }
                if (!shown) { OpenedModals.Add(m); continue; } // up but not shown yet: look again next second
                if (m is ConfirmDialogScreen d)
                {
                    string text = "";
                    try { text = HarmonyLib.Traverse.Create(d).Field("popupMessage").Property("text").GetValue<string>(); } catch { }
                    Log.Info($"headless: answering dialog with OK: {text?.Replace('\n', ' ')}");
                    d.OnSelect_OK();
                }
                else
                {
                    Log.Info($"headless: closing {m.GetType().Name}");
                    m.Deactivate();
                }
            }
            HeadlessUnpause(t);
        }

        private void HeadlessUnpause(float t)
        {
            if (S.unpause && t > 5f && SpeedControlScreen.Instance != null && SpeedControlScreen.Instance.IsPaused)
            {
                SpeedControlScreen.Instance.Unpause(false);
                Log.Info($"headless: unpaused (still paused: {SpeedControlScreen.Instance.IsPaused})");
            }
        }

        private float nextStatsAt, statSince;
        private int statFrames;

        private void LogStats(float now)
        {
            float fps = statFrames / Mathf.Max(0.001f, now - statSince);
            var clock = GameClock.Instance;
            var speed = SpeedControlScreen.Instance;
            long ws = 0;
            try { ws = Process.GetCurrentProcess().WorkingSet64; } catch { }
            Log.Info($"stats: cycle {(clock != null ? clock.GetTimeInCycles() : -1):F3}, " +
                     $"{(speed == null ? "no speed ctl" : speed.IsPaused ? "paused" : "speed " + (speed.GetSpeed() + 1))}, " +
                     $"{fps:F1} fps, managed {GC.GetTotalMemory(false) / 1048576} MB, working set {ws / 1048576} MB, " +
                     $"gfx {SystemInfo.graphicsDeviceType}, batchmode {Application.isBatchMode}");
        }

        private static void SaveBench()
        {
            for (int i = 0; i < 2; i++)
            {
                var sw = Stopwatch.StartNew();
                byte[] full = GameBridge.SaveWorld(out _);
                long fullMs = sw.ElapsedMilliseconds;
                sw = Stopwatch.StartNew();
                GameBridge.WorldSnapshot snap = GameBridge.CaptureWorld();
                long snapMs = sw.ElapsedMilliseconds;
                sw = Stopwatch.StartNew();
                byte[] enc = GameBridge.EncodeWorld(snap);
                long encMs = sw.ElapsedMilliseconds;
                Log.Info($"savebench #{i + 1}: game's own save {fullMs} ms ({full.Length / 1048576f:F2} MB) | coop snapshot {snapMs} ms " +
                         $"({snap.bodyLength / 1048576f:F1} MB raw) + compress {encMs} ms off the main thread -> {enc.Length / 1048576f:F2} MB");
            }
        }

        // ================================================================== overlay

        private static readonly Color[] Palette =
        {
            new Color(1.00f, 0.36f, 0.36f), new Color(0.32f, 0.66f, 1.00f), new Color(0.38f, 0.86f, 0.42f), new Color(1.00f, 0.72f, 0.30f),
            new Color(0.78f, 0.50f, 1.00f), new Color(0.30f, 0.88f, 0.88f), new Color(1.00f, 0.48f, 0.78f), new Color(0.90f, 0.90f, 0.36f),
        };

        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
        private static Color ColorOf(byte i) => Palette[i % Palette.Length];

        private GUIStyle panelStyle, textStyle, tagStyle, buttonStyle;
        private Texture2D white, arrowFill, arrowEdge, panelBg;
        private float uiScale = 1f;

        private string DescribeMembers()
        {
            var parts = new List<string>();
            foreach (MemberInfo m in members) parts.Add($"#{m.id} {m.name} {m.role} {m.state}{(m.udp ? " udp" : "")}");
            return string.Join(", ", parts.ToArray());
        }

        private void SmoothRemotes(float now)
        {
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f);
            foreach (Remote r in remotes.Values)
            {
                PresenceData d = r.data;
                if (d == null) continue;
                if (!r.shown)
                {
                    r.x0 = d.viewMinX; r.y0 = d.viewMinY; r.x1 = d.viewMaxX; r.y1 = d.viewMaxY; r.cx = d.cursorX; r.cy = d.cursorY;
                    r.shown = true;
                    continue;
                }
                r.x0 += (d.viewMinX - r.x0) * k; r.y0 += (d.viewMinY - r.y0) * k;
                r.x1 += (d.viewMaxX - r.x1) * k; r.y1 += (d.viewMaxY - r.y1) * k;
                r.cx += (d.cursorX - r.cx) * k; r.cy += (d.cursorY - r.cy) * k;
            }
        }

        private void EnsureStyles()
        {
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            if (panelStyle != null && Mathf.Approximately(scale, uiScale)) return;
            uiScale = scale;
            white = Texture2D.whiteTexture;
            if (arrowFill == null) MakeArrow(out arrowFill, out arrowEdge);
            int fs = Mathf.RoundToInt(15 * scale);
            if (panelBg == null)
            {
                panelBg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                panelBg.SetPixel(0, 0, new Color(0.08f, 0.09f, 0.12f, 0.72f));
                panelBg.Apply();
            }
            panelStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 6, 8), alignment = TextAnchor.UpperLeft };
            panelStyle.normal.background = panelBg;
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = fs, richText = true, wordWrap = true, margin = new RectOffset(0, 0, 1, 1), padding = new RectOffset(0, 0, 0, 0) };
            textStyle.normal.textColor = Color.white;
            tagStyle = new GUIStyle(GUI.skin.label) { fontSize = fs, fontStyle = FontStyle.Bold, richText = true, wordWrap = false };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = fs };
        }

        private void OnGUI()
        {
            EnsureStyles();
            if (InGame && Event.current.type == EventType.Repaint) DrawRemotes();
            DrawPanel();
        }

        private string MemberLine(MemberInfo m)
        {
            string role = m.role == Role.Host ? "服务器" : "玩家";
            string state;
            switch (m.state)
            {
                case MemberState.InGame: state = "游戏中"; break;
                case MemberState.Loading: state = "加载中"; break;
                case MemberState.Downloading: state = "下载世界中"; break;
                case MemberState.WaitingForWorld: state = "等待世界"; break;
                default: state = "主菜单"; break;
            }
            string extra = "";
            if (m.id != myId && remotes.TryGetValue(m.id, out Remote r) && r.data != null && Time.unscaledTime - r.lastSeen < 3f)
            {
                extra = $" · {r.data.rttMs} ms";
                if (r.data.world != GameBridge.ActiveWorld() && InGame) extra += " · 在另一个星球";
            }
            string me = m.id == myId ? "（你）" : "";
            return $"<color=#{Hex(ColorOf(m.color))}>■ {m.name}</color>{me}  {role} · {state}{extra}{(m.udp ? "" : " · TCP")}";
        }

        // The panel is laid out 4 times a second into a list of lines and buttons with their rectangles; OnGUI only
        // draws that list (no GUILayout: every GUILayout call allocates layout objects, on every GUI event, several
        // times a frame; it was 200-300 KB/s of the guest's garbage, and each collection freezes the game).
        private sealed class PanelItem { public GUIContent content; public bool button; public System.Action click; public Rect rect; }
        private readonly List<PanelItem> panelItems = new List<PanelItem>();
        private Rect panelRect;
        private float panelUntil;

        private void Line(string text) => panelItems.Add(new PanelItem { content = new GUIContent(text) });
        private void Button(string text, float width, System.Action click) => panelItems.Add(new PanelItem { content = new GUIContent(text), button = true, click = click, rect = new Rect(0, 0, width, 0) });

        private void BuildPanel(float now)
        {
            panelItems.Clear();
            Line($"<b>联机</b> v{CoopMod.Version} · 房间 {S.room} · {(S.IsServer ? "服务器" : S.IsHost ? "主机（开发）" : "玩家")} {S.name}");
            switch (phase)
            {
                case Phase.Connecting:
                    Line($"正在连接 {S.server} …");
                    break;
                case Phase.Offline:
                    Line($"<color=#ffb050>未连接</color>：{closeReason}（{Mathf.Max(0, retryAt - now):F0} 秒后重试）");
                    break;
                case Phase.Rejected:
                    Line($"<color=#ff6060>无法加入</color>：{reject.message}");
                    foreach (string d in reject.details) Line("· " + d);
                    Button("重试", 120 * uiScale, () => { phase = Phase.Offline; retryAt = 0; manifest = null; });
                    break;
                case Phase.Online:
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"已连接 · 延迟 {(client.RttMs < 0 ? "…" : client.RttMs + " ms")} · {(client.UdpReady ? "UDP" : "TCP（UDP 不通）")}");
                    foreach (MemberInfo m in members) sb.Append("\n").Append(MemberLine(m));
                    if (Puppets.Mirroring)
                        sb.Append($"\n同步：复制人/小动物 {Puppets.Tracked} 个，缓冲 {Puppets.BufferMs:F0} ms，格子 收到 {WorldSync.GuestCellsReceived}、校正 {WorldSync.GuestCellsApplied + WorldSync.GuestCellsPinned}");
                    if (serverSaving && Time.unscaledTime - savingSince < 30f) sb.Append("\n<color=#c0d8ff>服务器正在存档…</color>");
                    Line(sb.ToString());
                    if (hostSaving) Line("正在保存世界给新玩家…");
                    long up = Interlocked.Read(ref client.UploadSent), upTotal = Interlocked.Read(ref client.UploadTotal);
                    if (S.IsHost && upTotal > 0 && up < upTotal) Line($"上传世界 {100.0 * up / upTotal:F0}%（{upTotal / 1048576.0:F1} MB）");
                    if (downloading)
                    {
                        long got = Interlocked.Read(ref client.DownloadReceived), total = Interlocked.Read(ref client.DownloadTotal);
                        Line($"下载服务器的世界 {(total > 0 ? 100.0 * got / total : 0):F0}%（{got / 1048576.0:F1}/{total / 1048576.0:F1} MB）");
                    }
                    if (loading || pendingLoad != null) Line("正在加载服务器的世界…");
                    if (!S.IsHost && !InGame && !wantWorld && !loading && pendingLoad == null && !downloading)
                        Button("加入服务器的世界", 200 * uiScale, () => { wantWorld = true; panelUntil = 0f; });
                    if (!S.IsHost && InGame && !resyncRequested)
                        Button("重新同步世界", 200 * uiScale, () => { Log.Info("resync requested by the player"); resyncRequested = true; panelUntil = 0f; });
                    if (resyncRequested) Line("正在从服务器重新获取世界…");
                    if (hostGone) Line("<color=#ffb050>服务器暂时断开，游戏已暂停，等它回来…</color>");
                    break;
                }
            }
            if (notice.Length > 0 && now - noticeAt < 30f && phase == Phase.Online && !InGame)
                Line($"<color=#c0d8ff>{notice}</color>");
            if (warnings.Count > 0 && now - warningsAt < 30f)
                foreach (string wn in warnings) Line($"<color=#ffd070>注意：{wn}</color>");
            // layout
            float w = 470 * uiScale;
            RectOffset pad = panelStyle.padding;
            float inner = w - pad.horizontal, y = pad.top;
            foreach (PanelItem it in panelItems)
            {
                if (it.button)
                {
                    float h = buttonStyle.CalcHeight(it.content, it.rect.width) + 4;
                    it.rect = new Rect(pad.left, y + 2, it.rect.width, h - 4);
                    y += h;
                }
                else
                {
                    float h = textStyle.CalcHeight(it.content, inner) + textStyle.margin.vertical;
                    it.rect = new Rect(pad.left, y + textStyle.margin.top, inner, h - textStyle.margin.vertical);
                    y += h;
                }
            }
            panelRect = new Rect((Screen.width - w) * 0.5f, 4, w, y + pad.bottom);
        }

        private void DrawPanel()
        {
            float now = Time.unscaledTime;
            if (now >= panelUntil || Event.current.type == EventType.Repaint && panelItems.Count == 0)
            {
                BuildPanel(now);
                panelUntil = now + 0.25f;
            }
            GUI.Box(panelRect, GUIContent.none, panelStyle);
            System.Action clicked = null;
            foreach (PanelItem it in panelItems)
            {
                var r = new Rect(panelRect.x + it.rect.x, panelRect.y + it.rect.y, it.rect.width, it.rect.height);
                if (it.button) { if (GUI.Button(r, it.content, buttonStyle)) clicked = it.click; }
                else GUI.Label(r, it.content, textStyle);
            }
            clicked?.Invoke();
        }

        private Vector2 ToGui(Camera cam, float x, float y)
        {
            Vector3 s = cam.WorldToScreenPoint(new Vector3(x, y, 0f));
            return new Vector2(s.x, Screen.height - s.y);
        }

        private void DrawRemotes()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            int myWorld = GameBridge.ActiveWorld();
            float now = Time.unscaledTime;
            foreach (MemberInfo m in members)
            {
                if (m.id == myId || !remotes.TryGetValue(m.id, out Remote r) || r.data == null) continue;
                if (now - r.lastSeen > 3f || (r.data.flags & PresenceData.FlagInGame) == 0 || r.data.world != myWorld) continue;
                Color c = ColorOf(m.color);
                Vector2 a = ToGui(cam, r.x0, r.y1), b = ToGui(cam, r.x1, r.y0); // top-left, bottom-right
                var frame = Rect.MinMaxRect(a.x, a.y, b.x, b.y);
                float t = Mathf.Max(2f, 2f * uiScale);
                DrawFrame(frame, c, t);

                var screen = new Rect(0, 0, Screen.width, Screen.height);
                Vector2 tagPos = new Vector2(frame.xMin + 6, frame.yMin + 2);
                if (!screen.Overlaps(frame))
                {
                    // whole view off screen: pin the name to the nearest screen edge
                    tagPos = new Vector2(Mathf.Clamp(frame.center.x, 8, Screen.width - 120 * uiScale), Mathf.Clamp(frame.center.y, 60 * uiScale, Screen.height - 40 * uiScale));
                }
                else
                {
                    tagPos.x = Mathf.Clamp(tagPos.x, 4, Screen.width - 120 * uiScale);
                    tagPos.y = Mathf.Clamp(tagPos.y, 4, Screen.height - 30 * uiScale);
                }
                if (!viewLabels.TryGetValue(m.name, out string viewLabel)) viewLabels[m.name] = viewLabel = $"{m.name} 的视野";
                Shadowed(new Rect(tagPos.x, tagPos.y, 300 * uiScale, 30 * uiScale), viewLabel, c);

                if ((r.data.flags & PresenceData.FlagCursor) != 0)
                {
                    Vector2 p = ToGui(cam, r.cx, r.cy);
                    float s = 24 * uiScale;
                    GUI.color = new Color(0, 0, 0, 0.9f);
                    GUI.DrawTexture(new Rect(p.x, p.y, s, s), arrowEdge);
                    GUI.color = c;
                    GUI.DrawTexture(new Rect(p.x, p.y, s, s), arrowFill);
                    GUI.color = Color.white;
                    Shadowed(new Rect(p.x + s * 0.7f, p.y + s * 0.7f, 200 * uiScale, 30 * uiScale), m.name, c);
                }
            }
        }

        private readonly Dictionary<string, string> viewLabels = new Dictionary<string, string>();

        private void DrawFrame(Rect r, Color c, float t)
        {
            GUI.color = new Color(c.r, c.g, c.b, 0.9f);
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, r.width, t), white);
            GUI.DrawTexture(new Rect(r.xMin, r.yMax - t, r.width, t), white);
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, t, r.height), white);
            GUI.DrawTexture(new Rect(r.xMax - t, r.yMin, t, r.height), white);
            GUI.color = Color.white;
        }

        private void Shadowed(Rect r, string text, Color c)
        {
            tagStyle.normal.textColor = new Color(0, 0, 0, 0.85f);
            GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, tagStyle);
            tagStyle.normal.textColor = c;
            GUI.Label(r, text, tagStyle);
        }

        /// <summary>A classic mouse-pointer shape, as a fill and an outline texture (white; tinted when drawn).</summary>
        private static void MakeArrow(out Texture2D fill, out Texture2D edge)
        {
            const int N = 32;
            Vector2[] poly = { new Vector2(1, 1), new Vector2(1, 24), new Vector2(7, 18.5f), new Vector2(11.5f, 28), new Vector2(15.5f, 26.5f), new Vector2(11, 17.5f), new Vector2(19, 17.5f) };
            fill = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            edge = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var clear = new Color(1, 1, 1, 0);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f); // image coordinates, y down
                    bool inside = Inside(poly, p);
                    float d = EdgeDistance(poly, p);
                    bool isEdge = d < 1.6f && (inside || d < 1.0f);
                    int ty = N - 1 - y; // textures are stored bottom-up
                    fill.SetPixel(x, ty, inside && !isEdge ? Color.white : clear);
                    edge.SetPixel(x, ty, isEdge || inside ? (isEdge ? Color.white : clear) : clear);
                }
            fill.Apply();
            edge.Apply();
        }

        private static bool Inside(Vector2[] poly, Vector2 p)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    c = !c;
            return c;
        }

        private static float EdgeDistance(Vector2[] poly, Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                Vector2 a = poly[j], b = poly[i], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }
    }
}
