using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using OniCoop.Net;

namespace CoopRelay;

public sealed class RelayOptions
{
    public string Bind = "0.0.0.0";
    public int Port = Proto.DefaultPort;
    public string Token = "";
    public string DataDir = "relay-data";
    public int MaxMembersPerRoom = 8;
    public int MaxRooms = 4;
    public long MaxSaveBytes = 128L << 20;
    public int IdleTimeoutSec = 30;
    public int SaveTimeoutSec = 120;
    public int Backups = 20; // host saves kept per room
}

sealed class Member
{
    public ushort Id;
    public string Name = "";
    public Role Role;
    public MemberState State;
    public byte Color;
    public Room Room;
    public HelloMsg Hello;
    public StateMsg LastState;
    public bool WantsWorld;
    public ulong UdpKey;
    public IPEndPoint UdpEp;
    public bool UdpConfirmed;
    public TcpClient Tcp;
    public EndPoint Remote;
    public readonly Channel<byte[]> Out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    public long Queued;
    public long LastSeen = Environment.TickCount64;
    public bool Closed;
    // UDP token bucket
    public double Tokens = 120;
    public long TokensAt = Environment.TickCount64;

    public string Tag => $"#{Id} {Name}({Role})";
}

sealed class Room
{
    public string Name = "";
    public Member Host;
    public readonly List<Member> Members = [];
    public readonly HashSet<Member> PendingSave = [];
    public uint SaveReqId;
    public long SaveReqAt; // 0 = no request in flight
    public int SaveAttempts;
    public long LastFailAt;
    // upload being received from the host
    public SaveBeginMsg Upload;
    public byte[] UploadData;
    public long UploadOffset;
}

public sealed class RelayServer
{
    public const long MaxQueuedBytes = 512L << 20;

    readonly RelayOptions opt;
    readonly Action<string> log;
    readonly object sync = new();
    readonly Dictionary<string, Room> rooms = [];
    readonly Dictionary<ushort, Member> byId = [];
    ushort nextId = 1;
    TcpListener listener;
    Socket udp;

    public int Port { get; private set; }

    public RelayServer(RelayOptions opt, Action<string> log) { this.opt = opt; this.log = log; }

    public void Start(CancellationToken ct)
    {
        IPAddress bind = IPAddress.Parse(opt.Bind);
        for (int attempt = 0; ; attempt++)
        {
            listener = new TcpListener(bind, opt.Port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                udp = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows())
                {
                    const int SIO_UDP_CONNRESET = -1744830452; // don't let ICMP port-unreachable kill ReceiveFrom
                    udp.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
                }
                udp.Bind(new IPEndPoint(bind, Port)); // same port number for TCP and UDP
                break;
            }
            catch (SocketException) when (opt.Port == 0 && attempt < 20)
            {
                listener.Stop();
                udp.Dispose();
            }
        }
        log($"relay listening on {opt.Bind}:{Port} (TCP + UDP), token {(opt.Token.Length > 0 ? "required" : "NOT set")}, data dir {Path.GetFullPath(opt.DataDir)}");
        _ = AcceptLoop(ct);
        _ = UdpLoop(ct);
        _ = Housekeeping(ct);
    }

    // ================================================================== TCP

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log("accept: " + e.Message); continue; }
            c.NoDelay = true;
            _ = HandleConnection(c, ct);
        }
    }

    async Task HandleConnection(TcpClient c, CancellationToken ct)
    {
        EndPoint remote = c.Client.RemoteEndPoint;
        Member m = null;
        var stream = c.GetStream();
        try
        {
            var header = new byte[4];
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloCts.CancelAfter(TimeSpan.FromSeconds(10));
            var (type, payload) = await ReadFrameAsync(stream, header, helloCts.Token);
            if (payload == null) return;
            if (type != Msg.Hello) { log($"{remote}: first frame was {type}, dropping"); return; }
            HelloMsg hello;
            try { hello = HelloMsg.Decode(payload); }
            catch (Exception e) { await RejectNow(stream, RejectCode.BadRequest, "无法解析 Hello：" + e.Message); return; }

            m = Admit(hello, c, remote, out RejectMsg reject);
            if (m == null)
            {
                log($"{remote}: rejected '{hello.name}' ({hello.role}): {reject.code} {reject.message} {string.Join(" | ", reject.details)}");
                await WriteFrame(stream, Wire.Frame(Msg.Reject, reject.Encode()));
                await Task.Delay(200, ct);
                return;
            }
            _ = WriterLoop(m, stream);
            while (!ct.IsCancellationRequested)
            {
                (type, payload) = await ReadFrameAsync(stream, header, ct);
                if (payload == null) break;
                lock (sync)
                {
                    m.LastSeen = Environment.TickCount64;
                    if (!HandleFrame(m, type, payload)) break;
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException or EndOfStreamException)
        {
            if (m != null) log($"{m.Tag}: connection lost: {e.Message}");
        }
        catch (Exception e)
        {
            log($"{(m != null ? m.Tag : remote?.ToString())}: error: {e}");
        }
        finally
        {
            if (m != null) Remove(m, "disconnected");
            c.Dispose();
        }
    }

    static async Task<(Msg, byte[])> ReadFrameAsync(NetworkStream s, byte[] header, CancellationToken ct)
    {
        if (!await ReadExactly(s, header, 4, ct)) return (0, null);
        int len = Wire.ReadI32(header, 0);
        if (len < 1 || len > Proto.MaxFrame) throw new InvalidDataException($"bad frame length {len}");
        var body = new byte[len];
        if (!await ReadExactly(s, body, len, ct)) throw new EndOfStreamException("closed mid-frame");
        return ((Msg)body[0], body[1..]);
    }

    static async Task<bool> ReadExactly(NetworkStream s, byte[] buf, int n, CancellationToken ct)
    {
        int got = 0;
        while (got < n)
        {
            int k = await s.ReadAsync(buf.AsMemory(got, n - got), ct);
            if (k == 0)
            {
                if (got == 0) return false;
                throw new EndOfStreamException("closed mid-frame");
            }
            got += k;
        }
        return true;
    }

    static async Task WriteFrame(NetworkStream s, byte[] f) => await s.WriteAsync(f);

    static async Task RejectNow(NetworkStream s, RejectCode code, string message) =>
        await WriteFrame(s, Wire.Frame(Msg.Reject, new RejectMsg { code = code, message = message }.Encode()));

    async Task WriterLoop(Member m, NetworkStream s)
    {
        try
        {
            await foreach (byte[] f in m.Out.Reader.ReadAllAsync())
            {
                await s.WriteAsync(f);
                Interlocked.Add(ref m.Queued, -f.Length);
            }
        }
        catch (Exception e)
        {
            if (!m.Closed) log($"{m.Tag}: write failed: {e.Message}");
            try { m.Tcp.Close(); } catch { }
        }
    }

    // ================================================================== admission

    static string Clean(string s, int max, bool roomName)
    {
        s = (s ?? "").Trim();
        if (roomName) s = new string(s.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        else s = new string(s.Where(ch => !char.IsControl(ch)).ToArray());
        return s.Length > max ? s[..max] : s;
    }

    Member Admit(HelloMsg h, TcpClient c, EndPoint remote, out RejectMsg reject)
    {
        reject = null;
        if (h.protocol != Proto.Version)
        {
            reject = new RejectMsg { code = RejectCode.Protocol, message = $"联机协议版本不同：服务器 {Proto.Version}，你 {h.protocol}。请更新 mod / 服务端。" };
            return null;
        }
        if (opt.Token.Length > 0 && !Wire.FixedTimeEquals(h.token, opt.Token))
        {
            reject = new RejectMsg { code = RejectCode.BadToken, message = "房间口令不对。" };
            return null;
        }
        string roomName = Clean(h.room, 32, true);
        if (roomName.Length == 0) roomName = "default";

        lock (sync)
        {
            if (!rooms.TryGetValue(roomName, out Room room))
            {
                if (rooms.Count >= opt.MaxRooms)
                {
                    reject = new RejectMsg { code = RejectCode.Full, message = "服务器房间数已满。" };
                    return null;
                }
                room = new Room { Name = roomName };
                rooms[roomName] = room;
            }
            if (room.Members.Count >= opt.MaxMembersPerRoom)
            {
                reject = new RejectMsg { code = RejectCode.Full, message = $"房间已满（{opt.MaxMembersPerRoom} 人）。" };
                return null;
            }
            if (h.role == Role.Host && room.Host != null)
            {
                reject = new RejectMsg { code = RejectCode.RoomHasHost, message = $"房间「{roomName}」已经有服务器在跑：{room.Host.Name}。" };
                return null;
            }
            var warnings = new List<string>();
            if (h.role == Role.Guest && room.Host != null)
            {
                var errors = new List<string>();
                Compat.Check(room.Host.Hello.manifest, HostGameDlcs(room), h.manifest, errors, warnings);
                if (errors.Count > 0)
                {
                    reject = new RejectMsg { code = RejectCode.Incompatible, message = "和服务器的游戏配置不一致，无法加入：", details = errors };
                    return null;
                }
            }

            while (nextId == 0 || nextId == Proto.TargetAll || byId.ContainsKey(nextId)) nextId++;
            var m = new Member
            {
                Id = nextId++,
                Name = Clean(h.name, 24, false),
                Role = h.role,
                Room = room,
                Hello = h,
                Tcp = c,
                Remote = remote,
                UdpKey = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)),
                WantsWorld = h.role == Role.Guest && h.wantWorld,
                State = h.role == Role.Guest && h.wantWorld ? MemberState.WaitingForWorld : MemberState.Lobby,
            };
            if (m.Name.Length == 0) m.Name = "Player" + m.Id;
            var used = room.Members.Select(x => x.Color).ToHashSet();
            m.Color = (byte)Enumerable.Range(0, 8).First(i => !used.Contains((byte)i) || i == 7);
            room.Members.Add(m);
            byId[m.Id] = m;
            if (m.Role == Role.Host) room.Host = m;
            log($"{m.Tag} joined room '{room.Name}' from {remote}: {h.manifest.Describe()}" + (warnings.Count > 0 ? " warnings: " + string.Join(" | ", warnings) : ""));

            Send(m, Msg.Welcome, new WelcomeMsg { id = m.Id, udpKey = m.UdpKey, color = m.Color, relayVersion = typeof(RelayServer).Assembly.GetName().Version.ToString(), warnings = warnings }.Encode());
            BroadcastMembers(room);
            if (m.Role == Role.Host) OnHostChanged(room);
            else if (m.WantsWorld) ServeWorld(room, m);
            return m;
        }
    }

    static List<string> HostGameDlcs(Room room) =>
        room.Host != null && room.Host.State == MemberState.InGame ? room.Host.LastState?.gameDlcs ?? [] : null;

    /// <summary>A host arrived or entered a game: recheck every guest still waiting for the world.</summary>
    void OnHostChanged(Room room)
    {
        foreach (Member g in room.Members.ToArray())
        {
            if (g.Role != Role.Guest || !g.WantsWorld || g.State > MemberState.WaitingForWorld) continue;
            var errors = new List<string>();
            var warnings = new List<string>();
            Compat.Check(room.Host.Hello.manifest, HostGameDlcs(room), g.Hello.manifest, errors, warnings);
            if (errors.Count > 0)
            {
                Send(g, Msg.Reject, new RejectMsg { code = RejectCode.Incompatible, message = "和服务器的游戏配置不一致，无法加入：", details = errors }.Encode());
                Kick(g, "incompatible with host: " + string.Join(" | ", errors));
                continue;
            }
            ServeWorld(room, g);
        }
    }

    void ServeWorld(Room room, Member g)
    {
        if (room.Host == null)
        {
            Notice(g, "服务器还没有连上，等待中…");
            return;
        }
        if (room.Host.State != MemberState.InGame)
        {
            Notice(g, $"等待服务器 {room.Host.Name} 读档…");
            return;
        }
        room.PendingSave.Add(g);
        if (room.SaveReqAt == 0) RequestSave(room);
        else Notice(g, $"服务器 {room.Host.Name} 正在保存世界…");
    }

    void RequestSave(Room room)
    {
        room.SaveReqId++;
        room.SaveReqAt = Environment.TickCount64;
        room.SaveAttempts++;
        room.Upload = null;
        room.UploadData = null;
        Send(room.Host, Msg.SaveRequest, BitConverter.GetBytes(room.SaveReqId));
        foreach (Member g in room.PendingSave) Notice(g, $"服务器 {room.Host.Name} 正在保存世界…");
        log($"room '{room.Name}': save #{room.SaveReqId} requested from {room.Host.Tag} for {string.Join(", ", room.PendingSave.Select(x => x.Tag))}");
    }

    // ================================================================== frames

    /// <summary>Returns false to drop the connection.</summary>
    bool HandleFrame(Member m, Msg type, byte[] p)
    {
        Room room = m.Room;
        switch (type)
        {
            case Msg.State:
            {
                var s = StateMsg.Decode(p);
                MemberState before = m.State;
                bool wasInGame = before == MemberState.InGame;
                m.State = s.state;
                m.LastState = s;
                if (m.Role == Role.Guest && s.state == MemberState.InGame) m.WantsWorld = false;
                if (m.Role == Role.Guest && s.state == MemberState.WaitingForWorld && !m.WantsWorld)
                {
                    m.WantsWorld = true; // guest asks for a (new) world
                    ServeWorld(room, m);
                }
                if (before != s.state)
                {
                    log($"{m.Tag}: {before} -> {s.state}" + (m.Role == Role.Host && s.state == MemberState.InGame ? $" colony '{s.colony}' dlc [{string.Join(",", s.gameDlcs)}]" : ""));
                    BroadcastMembers(room);
                }
                if (m.Role == Role.Host && s.state == MemberState.InGame && !wasInGame) OnHostChanged(room);
                return true;
            }
            case Msg.SaveBegin:
            {
                if (m != room.Host) return Violation(m, "SaveBegin from a guest");
                var s = SaveBeginMsg.Decode(p);
                if (s.size < 0 || s.size > opt.MaxSaveBytes)
                {
                    log($"{m.Tag}: save of {s.size} B exceeds limit {opt.MaxSaveBytes}");
                    FailPending(room, $"存档太大（{s.size / 1048576.0:F1} MB）。");
                    room.Upload = null;
                    return true;
                }
                room.Upload = s;
                room.UploadData = new byte[s.size];
                room.UploadOffset = 0;
                return true;
            }
            case Msg.SaveChunk:
            {
                if (m != room.Host) return Violation(m, "SaveChunk from a guest");
                if (room.Upload == null) return true; // dropped upload (too large)
                uint id = BitConverter.ToUInt32(p, 0);
                int n = p.Length - 4;
                if (id != room.Upload.requestId || room.UploadOffset + n > room.UploadData.Length) return Violation(m, "bad save chunk");
                Buffer.BlockCopy(p, 4, room.UploadData, (int)room.UploadOffset, n);
                room.UploadOffset += n;
                return true;
            }
            case Msg.SaveEnd:
            {
                if (m != room.Host) return Violation(m, "SaveEnd from a guest");
                if (room.Upload == null) return true;
                SaveBeginMsg s = room.Upload;
                byte[] data = room.UploadData;
                room.Upload = null;
                room.UploadData = null;
                if (room.UploadOffset != s.size || !SHA256.HashData(data).AsSpan().SequenceEqual(s.sha256))
                {
                    log($"{m.Tag}: save #{s.requestId} corrupted ({room.UploadOffset}/{s.size} B)");
                    FailPending(room, "存档上传出错，请重试。");
                    return true;
                }
                if (s.requestId == 0)
                {
                    // nobody asked: the host's own save (autosave / manual), kept on the server as a backup
                    log($"room '{room.Name}': backup '{s.name}' received from host, {s.size / 1048576.0:F2} MB");
                    Persist(room, s, data);
                    PersistBackup(room, s, data);
                    return true;
                }
                log($"room '{room.Name}': save #{s.requestId} '{s.name}' received from host, {s.size / 1048576.0:F2} MB in {(Environment.TickCount64 - room.SaveReqAt) / 1000.0:F1} s");
                Persist(room, s, data);
                var targets = room.PendingSave.Where(g => !g.Closed && g.WantsWorld).ToList();
                room.PendingSave.Clear();
                room.SaveReqAt = 0;
                room.SaveAttempts = 0;
                foreach (Member g in targets) SendSave(g, s, data);
                return true;
            }
            case Msg.SaveFailed:
            {
                if (m != room.Host) return Violation(m, "SaveFailed from a guest");
                var r = Wire.Reader(p);
                r.ReadUInt32();
                string reason = r.ReadString();
                log($"{m.Tag}: save failed: {reason}");
                FailPending(room, "服务器保存失败：" + reason);
                return true;
            }
            case Msg.Relay:
            {
                var r = Wire.Reader(p);
                ushort target = r.ReadUInt16();
                byte ch = r.ReadByte();
                byte[] data = p[3..];
                byte[] payload = Wire.Build(w => { w.Write(m.Id); w.Write(ch); w.Write(data); });
                foreach (Member t in Targets(m, target)) Send(t, Msg.RelayFrom, payload);
                return true;
            }
            case Msg.UnreliableOverTcp:
                if (p.Length > 0) HandleUnreliable(m, (Msg)p[0], p[1..], viaUdp: false);
                return true;
            case Msg.Bye:
                log($"{m.Tag}: bye ({Wire.Reader(p).ReadString()})");
                return false;
            default:
                return Violation(m, $"unexpected {type}");
        }
    }

    IEnumerable<Member> Targets(Member from, ushort target)
    {
        Room room = from.Room;
        if (target == Proto.TargetAll) return room.Members.Where(x => x != from);
        if (target == Proto.TargetHost) return room.Host != null && room.Host != from ? [room.Host] : [];
        return byId.TryGetValue(target, out Member t) && t.Room == room && t != from ? [t] : [];
    }

    bool Violation(Member m, string what)
    {
        log($"{m.Tag}: protocol violation: {what}");
        return false;
    }

    void FailPending(Room room, string message)
    {
        bool retry = room.SaveAttempts < 5;
        foreach (Member g in room.PendingSave) Notice(g, message + (retry ? " 稍后自动重试…" : " 已多次失败，请检查服务器后重新加入。"));
        room.LastFailAt = Environment.TickCount64;
        room.SaveReqAt = 0;
        room.Upload = null;
        room.UploadData = null;
    }

    void SendSave(Member g, SaveBeginMsg s, byte[] data)
    {
        g.State = MemberState.Downloading;
        Send(g, Msg.SaveBegin, s.Encode());
        for (int off = 0; off < data.Length; off += Proto.SaveChunk)
        {
            int n = Math.Min(Proto.SaveChunk, data.Length - off);
            var chunk = new byte[4 + n];
            BitConverter.TryWriteBytes(chunk, s.requestId);
            Buffer.BlockCopy(data, off, chunk, 4, n);
            Send(g, Msg.SaveChunk, chunk);
        }
        Send(g, Msg.SaveEnd, BitConverter.GetBytes(s.requestId));
        log($"{g.Tag}: sending save #{s.requestId} ({data.Length / 1048576.0:F2} MB)");
        BroadcastMembers(g.Room);
    }

    /// <summary>rooms/&lt;room&gt;/backups/&lt;UTC time&gt;_&lt;name&gt;, the newest <see cref="RelayOptions.Backups"/> kept.</summary>
    void PersistBackup(Room room, SaveBeginMsg s, byte[] data)
    {
        try
        {
            string dir = Path.Combine(opt.DataDir, "rooms", room.Name, "backups");
            Directory.CreateDirectory(dir);
            string safe = string.Concat((s.name ?? "save.sav").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            File.WriteAllBytes(Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}_{safe}"), data);
            var old = new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.Name).Skip(opt.Backups).ToList();
            foreach (FileInfo f in old) f.Delete();
        }
        catch (Exception e)
        {
            log($"room '{room.Name}': could not store the backup: {e.Message}");
        }
    }

    void Persist(Room room, SaveBeginMsg s, byte[] data)
    {
        try
        {
            string dir = Path.Combine(opt.DataDir, "rooms", room.Name);
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, "latest.sav.tmp");
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, Path.Combine(dir, "latest.sav"), overwrite: true);
            var meta = new
            {
                name = s.name, size = s.size, sha256 = Convert.ToHexStringLower(s.sha256), savedAt = DateTime.UtcNow.ToString("o"),
                host = room.Host?.Name, colony = room.Host?.LastState?.colony, dlc = room.Host?.LastState?.gameDlcs,
            };
            File.WriteAllText(Path.Combine(dir, "latest.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            log($"room '{room.Name}': could not store the save: {e.Message}");
        }
    }

    // ================================================================== UDP

    async Task UdpLoop(CancellationToken ct)
    {
        var buf = new byte[2048];
        EndPoint any = new IPEndPoint(udp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await udp.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            if (r.ReceivedBytes < 11) continue;
            var type = (Msg)buf[0];
            ushort id = BitConverter.ToUInt16(buf, 1);
            ulong key = BitConverter.ToUInt64(buf, 3);
            var from = (IPEndPoint)r.RemoteEndPoint;
            byte[] payload = buf[11..r.ReceivedBytes];
            lock (sync)
            {
                if (!byId.TryGetValue(id, out Member m) || m.UdpKey != key || m.Closed) continue;
                if (!TakeToken(m)) continue;
                m.LastSeen = Environment.TickCount64;
                if (m.UdpEp == null || !m.UdpEp.Equals(from))
                {
                    log($"{m.Tag}: UDP endpoint {from}");
                    m.UdpEp = from;
                }
                if (type == Msg.UdpHello)
                {
                    SendUdp(m, Msg.UdpAck, []);
                    continue;
                }
                if (!m.UdpConfirmed)
                {
                    m.UdpConfirmed = true; // the client only uses UDP for these after it got our ack
                    BroadcastMembers(m.Room);
                }
                HandleUnreliable(m, type, payload, viaUdp: true);
            }
        }
    }

    static bool TakeToken(Member m)
    {
        long now = Environment.TickCount64;
        m.Tokens = Math.Min(120, m.Tokens + (now - m.TokensAt) * 0.06); // 60 packets/s sustained
        m.TokensAt = now;
        if (m.Tokens < 1) return false;
        m.Tokens -= 1;
        return true;
    }

    void HandleUnreliable(Member m, Msg type, byte[] p, bool viaUdp)
    {
        switch (type)
        {
            case Msg.Ping:
                SendUnreliable(m, Msg.Pong, p);
                break;
            case Msg.Presence:
                if (p.Length > 200) return;
                byte[] fwd = new byte[2 + p.Length];
                BitConverter.TryWriteBytes(fwd, m.Id);
                Buffer.BlockCopy(p, 0, fwd, 2, p.Length);
                foreach (Member t in m.Room.Members)
                    if (t != m) SendUnreliable(t, Msg.Presence, fwd);
                break;
        }
    }

    void SendUnreliable(Member m, Msg type, byte[] payload)
    {
        if (m.UdpConfirmed && m.UdpEp != null) SendUdp(m, type, payload);
        else
        {
            var inner = new byte[1 + payload.Length];
            inner[0] = (byte)type;
            Buffer.BlockCopy(payload, 0, inner, 1, payload.Length);
            Send(m, Msg.UnreliableOverTcp, inner);
        }
    }

    void SendUdp(Member m, Msg type, byte[] payload)
    {
        var d = new byte[1 + payload.Length];
        d[0] = (byte)type;
        Buffer.BlockCopy(payload, 0, d, 1, payload.Length);
        try { udp.SendTo(d, m.UdpEp); }
        catch (SocketException) { }
    }

    // ================================================================== members

    void Send(Member m, Msg type, byte[] payload)
    {
        if (m.Closed) return;
        byte[] f = Wire.Frame(type, payload);
        if (Interlocked.Add(ref m.Queued, f.Length) > MaxQueuedBytes)
        {
            Kick(m, "send queue overflow (client too slow)");
            return;
        }
        m.Out.Writer.TryWrite(f);
    }

    void Notice(Member m, string text) => Send(m, Msg.Notice, Wire.Build(w => w.Write(text)));

    void BroadcastMembers(Room room)
    {
        byte[] list = MemberInfo.EncodeList(room.Members.Select(x => new MemberInfo
        {
            id = x.Id, name = x.Name, role = x.Role, state = x.State, color = x.Color, udp = x.UdpConfirmed,
        }).ToList());
        foreach (Member m in room.Members) Send(m, Msg.Members, list);
    }

    /// <summary>Lets queued frames (e.g. a Reject) go out, then closes.</summary>
    void Kick(Member m, string reason)
    {
        log($"{m.Tag}: kicked: {reason}");
        m.Out.Writer.TryComplete();
        Remove(m, reason);
        _ = Task.Delay(1000).ContinueWith(_ => { try { m.Tcp.Close(); } catch { } });
    }

    void Remove(Member m, string reason)
    {
        lock (sync)
        {
            if (m.Closed) return;
            m.Closed = true;
            m.Out.Writer.TryComplete();
            Room room = m.Room;
            room.Members.Remove(m);
            room.PendingSave.Remove(m);
            byId.Remove(m.Id);
            log($"{m.Tag} left room '{room.Name}' ({reason})");
            if (room.Host == m)
            {
                room.Host = null;
                if (room.SaveReqAt != 0 || room.PendingSave.Count > 0)
                {
                    foreach (Member g in room.PendingSave) Notice(g, "服务器断开了，等待它重新连接…");
                    room.SaveReqAt = 0;
                    room.Upload = null;
                    room.UploadData = null;
                }
            }
            if (room.Members.Count == 0) rooms.Remove(room.Name);
            else BroadcastMembers(room);
        }
    }

    async Task Housekeeping(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
            lock (sync)
            {
                long now = Environment.TickCount64;
                foreach (Member m in byId.Values.ToArray())
                    if (now - m.LastSeen > opt.IdleTimeoutSec * 1000L) Kick(m, $"no traffic for {opt.IdleTimeoutSec} s");
                foreach (Room room in rooms.Values.ToArray())
                {
                    if (room.SaveReqAt != 0 && now - room.SaveReqAt >= opt.SaveTimeoutSec * 1000L)
                    {
                        log($"room '{room.Name}': save #{room.SaveReqId} timed out");
                        FailPending(room, "服务器保存超时。");
                    }
                    // retry after a failure, a few times
                    if (room.SaveReqAt == 0 && room.PendingSave.Count > 0 && room.Host?.State == MemberState.InGame
                        && room.SaveAttempts < 5 && now - room.LastFailAt > 15000)
                        RequestSave(room);
                }
            }
        }
    }
}
