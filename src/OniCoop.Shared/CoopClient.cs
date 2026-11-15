namespace OniCoop.Net
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Security.Cryptography;
    using System.Threading;

    /// <summary>Artificial network conditions applied inside the client (both directions), so a 300 ms / lossy line
    /// can be reproduced against a relay on the same PC. Loss only applies to unreliable (UDP) traffic.</summary>
    public sealed class LinkSim
    {
        public int delayMs, jitterMs;
        public double loss;
        public bool Enabled => delayMs > 0 || jitterMs > 0 || loss > 0;

        /// <summary>"delay=150,jitter=20,loss=0.05" (one-way values, applied each way). Empty → no simulation.</summary>
        public static LinkSim Parse(string spec)
        {
            var s = new LinkSim();
            if (string.IsNullOrEmpty(spec)) return s;
            foreach (string part in spec.Split(','))
            {
                string[] kv = part.Split('=');
                if (kv.Length != 2) continue;
                string k = kv[0].Trim().ToLowerInvariant(), v = kv[1].Trim();
                if (k == "delay") s.delayMs = int.Parse(v);
                else if (k == "jitter") s.jitterMs = int.Parse(v);
                else if (k == "loss") s.loss = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            return s;
        }

        public override string ToString() => Enabled ? $"delay {delayMs}±{jitterMs} ms, loss {loss:P0} each way" : "off";
    }

    public enum EvType
    {
        Connected, Rejected, Closed, Members, Notice, SaveRequest, SaveIncoming, SaveReceived, SaveFailed, Presence, RelayFrom,
    }

    public sealed class ClientEvent
    {
        public EvType type;
        public WelcomeMsg welcome;
        public RejectMsg reject;
        public List<MemberInfo> members;
        public string text;
        public uint requestId;
        public SaveBeginMsg save;
        public byte[] data;
        public ushort from;
        public byte channel;
        public PresenceData presence;
        /// <summary>How long it waited for the game to poll it after it arrived (simulated delay included in the
        /// arrival), e.g. while the game's main thread was busy loading.</summary>
        public long waitedMs;

        public override string ToString()
        {
            switch (type)
            {
                case EvType.Connected: return $"Connected id={welcome.id} color={welcome.color} warnings={welcome.warnings.Count}";
                case EvType.Rejected: return $"Rejected {reject.code}: {reject.message} | {string.Join(" | ", reject.details.ToArray())}";
                case EvType.Members: return $"Members x{members.Count}";
                case EvType.SaveIncoming: return $"SaveIncoming #{save.requestId} '{save.name}' {save.size} B";
                case EvType.SaveReceived: return $"SaveReceived #{save.requestId} {data.Length} B";
                case EvType.Presence: return $"Presence from {from} seq {presence.seq}";
                default: return $"{type} {text}";
            }
        }
    }

    /// <summary>One connection to the relay: TCP for reliable messages, UDP for unreliable ones (with TCP fallback
    /// while UDP is not confirmed). Background threads do the I/O and keepalive; the owner drains events with
    /// <see cref="TryPoll"/> from its own thread (Unity main thread in the mod).</summary>
    public sealed class CoopClient : IDisposable
    {
        public const long MaxSaveBytes = 256L << 20;

        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static long NowMs => Clock.ElapsedMilliseconds;

        readonly string host;
        readonly int port;
        readonly HelloMsg hello;
        readonly LinkSim sim;
        readonly Action<string> log;
        readonly System.Random rng = new System.Random();

        TcpClient tcp;
        Stream stream;
        Socket udp;
        IPEndPoint serverEp;

        readonly object gate = new object();
        readonly Queue<Outgoing> tcpOut = new Queue<Outgoing>();
        // The host's backup saves: sent only while nothing else waits, and paced, so a 5 MB backup never sits in front
        // of the game's own messages (on a slow uplink it delayed the duplicants' timeline by over a second).
        readonly Queue<Outgoing> bulkOut = new Queue<Outgoing>();
        long bulkNextAt;
        /// <summary>Pace of backup uploads (bytes per second).</summary>
        public int BackupRate = 96 * 1024;
        const int BackupChunk = 16 * 1024;
        readonly List<Outgoing> udpOut = new List<Outgoing>();
        readonly List<Incoming> inbox = new List<Incoming>();
        long lastTcpOutDue, lastTcpInDue;
        volatile bool closed, closing;
        string closingReason;
        System.Threading.Timer closeTimer;
        bool closedEventSent;

        public ushort Id { get; private set; }
        ulong udpKey;
        public bool Welcomed { get; private set; }
        /// <summary>The relay answered our UDP hello: unreliable traffic goes over UDP.</summary>
        public bool UdpReady => udpReady;
        volatile int rttMs = -1;
        /// <summary>Smoothed round trip to the relay (ms), -1 until known.</summary>
        public int RttMs { get => rttMs; private set => rttMs = value; }
        public string CloseReason { get; private set; }

        // Save transfer progress (read from any thread).
        public long UploadSent, UploadTotal, DownloadReceived, DownloadTotal;
        // Everything sent / received over TCP, frames included (read from any thread).
        public long TcpBytesOut, TcpBytesIn;

        volatile bool netWelcomed, udpReady;

        // Incoming save assembly (TCP reader thread only).
        SaveBeginMsg rxSave;
        byte[] rxData;
        long rxOffset;

        struct Outgoing { public long due; public byte[] bytes; public int saveBytes; }
        struct Incoming { public long due; public ClientEvent ev; }

        public CoopClient(string host, int port, HelloMsg hello, LinkSim sim, Action<string> log)
        {
            this.host = host; this.port = port; this.hello = hello; this.sim = sim ?? new LinkSim(); this.log = log ?? (_ => { });
        }

        public void Start()
        {
            var t = new Thread(ConnectAndRead) { IsBackground = true, Name = "coop-tcp" };
            t.Start();
        }

        // ------------------------------------------------------------------ public sends (any thread)

        public void SendState(StateMsg s) => SendTcp(Msg.State, s.Encode());
        public void SendRelay(ushort target, byte channel, byte[] data) =>
            SendTcp(Msg.Relay, Wire.Build(w => { w.Write(target); w.Write(channel); w.Write(data); }));
        public void SendSaveFailed(uint requestId, string reason) =>
            SendTcp(Msg.SaveFailed, Wire.Build(w => { w.Write(requestId); w.Write(reason ?? ""); }));

        private readonly object saveSendLock = new object();

        /// <summary>A save (requestId 0: the host's own save, kept by the relay as a backup). Its frames go out together:
        /// two uploads from two threads must not interleave (the relay takes one upload at a time). A backup goes out in
        /// the background (<see cref="bulkOut"/>); a save someone is waiting for first pushes out what is left of a
        /// backup, then goes in the normal order.</summary>
        public void SendSave(uint requestId, string name, byte[] data)
        {
            lock (saveSendLock)
            {
                byte[] hash;
                using (var sha = SHA256.Create()) hash = sha.ComputeHash(data);
                bool backup = requestId == 0;
                if (!backup)
                {
                    lock (gate)
                    {
                        while (bulkOut.Count > 0) EnqueueTcp(bulkOut.Dequeue().bytes, 0);
                        Interlocked.Exchange(ref UploadSent, 0);
                        Interlocked.Exchange(ref UploadTotal, data.Length);
                    }
                }
                Action<Msg, byte[], int> send = backup ? (Action<Msg, byte[], int>)SendBulk : SendTcp;
                send(Msg.SaveBegin, new SaveBeginMsg { requestId = requestId, name = name, size = data.Length, sha256 = hash }.Encode(), 0);
                int chunk = backup ? BackupChunk : Proto.SaveChunk;
                for (int off = 0; off < data.Length; off += chunk)
                {
                    int n = Math.Min(chunk, data.Length - off);
                    var payload = new byte[4 + n];
                    BitConverter.GetBytes(requestId).CopyTo(payload, 0);
                    Buffer.BlockCopy(data, off, payload, 4, n);
                    send(Msg.SaveChunk, payload, backup ? 0 : n);
                }
                send(Msg.SaveEnd, BitConverter.GetBytes(requestId), 0);
            }
        }

        /// <summary>Backup frames: after everything else, at <see cref="BackupRate"/>.</summary>
        void SendBulk(Msg type, byte[] payload, int saveBytes)
        {
            if (closed || closing) return;
            byte[] frame = Wire.Frame(type, payload);
            lock (gate)
            {
                bulkOut.Enqueue(new Outgoing { due = NowMs, bytes = frame });
                Monitor.PulseAll(gate);
            }
        }

        /// <summary>Backup frames still waiting (the panel can show a backup in progress).</summary>
        public int BackupFramesLeft { get { lock (gate) return bulkOut.Count; } }

        public void SendPresence(PresenceData p) => SendUnreliable(Msg.Presence, p.Encode());

        /// <summary>Kept for callers; pings and UDP registration run on a background thread (<see cref="KeepaliveLoop"/>),
        /// so the connection survives the game's main thread freezing (loading a world takes up to a minute).</summary>
        public void Tick() { }

        void KeepaliveLoop()
        {
            long nextPingAt = 0, nextUdpHelloAt = 0;
            int udpHelloCount = 0;
            uint pingSeq = 0;
            while (!closed)
            {
                Thread.Sleep(50);
                if (!netWelcomed || closing) continue;
                long now = NowMs;
                if (!UdpReady && now >= nextUdpHelloAt)
                {
                    udpHelloCount++;
                    nextUdpHelloAt = now + (udpHelloCount < 30 ? 300 : 3000);
                    SendUdpRaw(Msg.UdpHello, new byte[0]);
                    if (udpHelloCount == 30) log("UDP to the relay is not answering; using TCP for presence (check UDP port forwarding)");
                }
                if (now >= nextPingAt)
                {
                    nextPingAt = now + 1000;
                    uint seq = ++pingSeq;
                    SendUnreliable(Msg.Ping, Wire.Build(w => { w.Write(seq); w.Write(now); }));
                }
            }
        }

        public bool TryPoll(out ClientEvent ev)
        {
            while (true)
            {
                Incoming item;
                lock (gate)
                {
                    if (inbox.Count == 0 || inbox[0].due > NowMs) { ev = null; return false; }
                    item = inbox[0];
                    inbox.RemoveAt(0);
                }
                if (item.ev.type == EvType.Connected) Welcomed = true;
                ev = item.ev;
                ev.waitedMs = Math.Max(0, NowMs - item.due);
                return true;
            }
        }

        /// <summary>Says goodbye after everything already queued has been sent, then closes.</summary>
        public void Close(string reason)
        {
            if (closed || closing) return;
            if (stream == null) { Shutdown(reason ?? "closed"); return; }
            lock (gate) while (bulkOut.Count > 0) EnqueueTcp(bulkOut.Dequeue().bytes, 0); // a backup still going: at full speed
            SendTcp(Msg.Bye, Wire.Build(w => w.Write(reason ?? "")));
            closing = true;
            closingReason = reason ?? "closed";
            var t = new System.Threading.Timer(_ => Shutdown(closingReason), null, 3000, Timeout.Infinite); // in case the send stalls
            closeTimer = t;
        }

        /// <summary>Immediate close (no goodbye).</summary>
        public void Dispose() => Shutdown(closing ? closingReason : "disposed");

        // ------------------------------------------------------------------ internals

        void SendTcp(Msg type, byte[] payload, int saveBytes = 0)
        {
            if (closed || closing) return;
            byte[] frame = Wire.Frame(type, payload);
            lock (gate) EnqueueTcp(frame, saveBytes);
        }

        /// <summary>(holding <see cref="gate"/>)</summary>
        void EnqueueTcp(byte[] frame, int saveBytes)
        {
            long due = NowMs + SimDelay();
            if (due < lastTcpOutDue) due = lastTcpOutDue; // a TCP stream never reorders
            lastTcpOutDue = due;
            tcpOut.Enqueue(new Outgoing { due = due, bytes = frame, saveBytes = saveBytes });
            Monitor.PulseAll(gate);
        }

        void SendUnreliable(Msg type, byte[] payload)
        {
            if (UdpReady) SendUdpRaw(type, payload);
            else
            {
                var inner = new byte[1 + payload.Length];
                inner[0] = (byte)type;
                Buffer.BlockCopy(payload, 0, inner, 1, payload.Length);
                SendTcp(Msg.UnreliableOverTcp, inner);
            }
        }

        void SendUdpRaw(Msg type, byte[] payload)
        {
            if (closed || closing || udp == null) return;
            if (sim.loss > 0 && type != Msg.UdpHello && rng.NextDouble() < sim.loss) return;
            var d = new byte[11 + payload.Length];
            d[0] = (byte)type;
            BitConverter.GetBytes(Id).CopyTo(d, 1);
            BitConverter.GetBytes(udpKey).CopyTo(d, 3);
            Buffer.BlockCopy(payload, 0, d, 11, payload.Length);
            if (d.Length > Proto.MaxDatagram) throw new InvalidOperationException($"datagram too large: {d.Length}");
            lock (gate)
            {
                var o = new Outgoing { due = NowMs + SimDelay(), bytes = d };
                int i = udpOut.Count;
                while (i > 0 && udpOut[i - 1].due > o.due) i--;
                udpOut.Insert(i, o);
                Monitor.PulseAll(gate);
            }
        }

        int SimDelay()
        {
            if (!sim.Enabled) return 0;
            int j = sim.jitterMs > 0 ? rng.Next(-sim.jitterMs, sim.jitterMs + 1) : 0;
            return Math.Max(0, sim.delayMs + j);
        }

        long Deliver(Incoming item, bool reliable)
        {
            lock (gate)
            {
                long due = NowMs + SimDelay();
                if (reliable)
                {
                    if (due < lastTcpInDue) due = lastTcpInDue;
                    lastTcpInDue = due;
                }
                item.due = due;
                if (item.ev == null) return due; // timing only
                int i = inbox.Count;
                while (i > 0 && inbox[i - 1].due > due) i--;
                inbox.Insert(i, item);
                return due;
            }
        }

        void Emit(ClientEvent ev, bool reliable = true) => Deliver(new Incoming { ev = ev }, reliable);

        void Shutdown(string reason)
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                CloseReason = reason;
                Monitor.PulseAll(gate);
            }
            try { tcp?.Close(); } catch { }
            try { udp?.Close(); } catch { }
            lock (gate)
            {
                if (closedEventSent) return;
                closedEventSent = true;
                // Delivered after everything already queued (e.g. a Reject that explains the close).
                long due = Math.Max(NowMs, lastTcpInDue);
                inbox.Add(new Incoming { due = due, ev = new ClientEvent { type = EvType.Closed, text = reason } });
            }
        }

        void ConnectAndRead()
        {
            try
            {
                IPAddress addr = Resolve(host);
                serverEp = new IPEndPoint(addr, port);
                tcp = new TcpClient(addr.AddressFamily) { NoDelay = true };
                var connect = tcp.ConnectAsync(addr, port);
                if (!connect.Wait(10000)) throw new TimeoutException($"no answer from {host}:{port}");
                if (connect.IsFaulted) throw connect.Exception.GetBaseException();
                stream = tcp.GetStream();

                udp = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(addr.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                new Thread(UdpReceiveLoop) { IsBackground = true, Name = "coop-udp-rx" }.Start();
                new Thread(SendLoop) { IsBackground = true, Name = "coop-tx" }.Start();
                new Thread(KeepaliveLoop) { IsBackground = true, Name = "coop-keepalive" }.Start();

                SendTcp(Msg.Hello, hello.Encode());
                var header = new byte[4];
                while (!closed)
                {
                    byte[] p = Wire.ReadFrame(stream, header, out Msg type);
                    if (p == null) { Shutdown("relay closed the connection"); return; }
                    Interlocked.Add(ref TcpBytesIn, p.Length + header.Length + 1);
                    HandleTcp(type, p);
                }
            }
            catch (Exception e)
            {
                if (!closed) Shutdown(Describe(e));
            }
        }

        static string Describe(Exception e)
        {
            if (e is AggregateException ae) e = ae.GetBaseException();
            if (e is SocketException se) return $"{se.SocketErrorCode}: {se.Message}";
            if (e is IOException io && io.InnerException is SocketException se2) return $"{se2.SocketErrorCode}: {se2.Message}";
            return e.Message;
        }

        static IPAddress Resolve(string host)
        {
            if (IPAddress.TryParse(host, out IPAddress ip)) return ip;
            IPAddress[] all = Dns.GetHostAddresses(host);
            foreach (var a in all) if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            if (all.Length > 0) return all[0];
            throw new IOException($"cannot resolve {host}");
        }

        void HandleTcp(Msg type, byte[] p)
        {
            switch (type)
            {
                case Msg.Welcome:
                {
                    var w = WelcomeMsg.Decode(p);
                    Id = w.id;
                    udpKey = w.udpKey;
                    netWelcomed = true;
                    Emit(new ClientEvent { type = EvType.Connected, welcome = w });
                    break;
                }
                case Msg.Reject:
                    Emit(new ClientEvent { type = EvType.Rejected, reject = RejectMsg.Decode(p) });
                    break;
                case Msg.Members:
                    Emit(new ClientEvent { type = EvType.Members, members = MemberInfo.DecodeList(p) });
                    break;
                case Msg.Notice:
                    Emit(new ClientEvent { type = EvType.Notice, text = Wire.Reader(p).ReadString() });
                    break;
                case Msg.SaveRequest:
                    Emit(new ClientEvent { type = EvType.SaveRequest, requestId = BitConverter.ToUInt32(p, 0) });
                    break;
                case Msg.SaveBegin:
                {
                    var s = SaveBeginMsg.Decode(p);
                    if (s.size < 0 || s.size > MaxSaveBytes) throw new InvalidDataException($"save too large ({s.size} B)");
                    rxSave = s;
                    rxData = new byte[s.size];
                    rxOffset = 0;
                    Interlocked.Exchange(ref DownloadReceived, 0);
                    Interlocked.Exchange(ref DownloadTotal, s.size);
                    Emit(new ClientEvent { type = EvType.SaveIncoming, save = s });
                    break;
                }
                case Msg.SaveChunk:
                {
                    uint id = BitConverter.ToUInt32(p, 0);
                    int n = p.Length - 4;
                    if (rxSave == null || id != rxSave.requestId || rxOffset + n > rxData.Length)
                        throw new InvalidDataException("unexpected save chunk");
                    Buffer.BlockCopy(p, 4, rxData, (int)rxOffset, n);
                    rxOffset += n;
                    Interlocked.Exchange(ref DownloadReceived, rxOffset);
                    break;
                }
                case Msg.SaveEnd:
                {
                    var s = rxSave;
                    byte[] data = rxData;
                    rxSave = null; rxData = null;
                    if (s == null || BitConverter.ToUInt32(p, 0) != s.requestId) throw new InvalidDataException("unexpected save end");
                    byte[] hash;
                    using (var sha = SHA256.Create()) hash = sha.ComputeHash(data, 0, (int)rxOffset);
                    if (rxOffset != s.size || Wire.Hex(hash) != Wire.Hex(s.sha256))
                        Emit(new ClientEvent { type = EvType.SaveFailed, requestId = s.requestId, text = $"save corrupted in transfer ({rxOffset}/{s.size} B)" });
                    else
                        Emit(new ClientEvent { type = EvType.SaveReceived, save = s, data = data });
                    break;
                }
                case Msg.SaveFailed:
                {
                    var r = Wire.Reader(p);
                    Emit(new ClientEvent { type = EvType.SaveFailed, requestId = r.ReadUInt32(), text = r.ReadString() });
                    break;
                }
                case Msg.RelayFrom:
                {
                    var r = Wire.Reader(p);
                    ushort from = r.ReadUInt16();
                    byte ch = r.ReadByte();
                    Emit(new ClientEvent { type = EvType.RelayFrom, from = from, channel = ch, data = r.ReadBytes(p.Length - 3) });
                    break;
                }
                case Msg.UnreliableOverTcp:
                    if (p.Length > 0)
                    {
                        var inner = new byte[p.Length - 1];
                        Buffer.BlockCopy(p, 1, inner, 0, inner.Length);
                        HandleUnreliable((Msg)p[0], inner, reliable: true);
                    }
                    break;
                case Msg.Bye:
                    Shutdown("relay: " + Wire.Reader(p).ReadString());
                    break;
            }
        }

        /// <summary>Server→client unreliable payloads (from UDP or wrapped in TCP).</summary>
        void HandleUnreliable(Msg type, byte[] p, bool reliable)
        {
            switch (type)
            {
                case Msg.UdpAck:
                    if (!udpReady) log("UDP path to the relay confirmed");
                    udpReady = true;
                    break;
                case Msg.Pong:
                {
                    var r = Wire.Reader(p);
                    r.ReadUInt32();
                    long sentAt = r.ReadInt64();
                    long arrives = Deliver(new Incoming(), reliable); // includes simulated delay
                    int sample = (int)Math.Max(0, arrives - sentAt);
                    int rtt = RttMs;
                    RttMs = rtt < 0 ? sample : (rtt * 4 + sample) / 5;
                    break;
                }
                case Msg.Presence:
                {
                    var r = Wire.Reader(p);
                    ushort from = r.ReadUInt16();
                    Emit(new ClientEvent { type = EvType.Presence, from = from, presence = PresenceData.Read(r) }, reliable);
                    break;
                }
            }
        }

        void UdpReceiveLoop()
        {
            var buf = new byte[2048];
            while (!closed)
            {
                try
                {
                    EndPoint from = new IPEndPoint(serverEp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    int n = udp.ReceiveFrom(buf, ref from);
                    if (n < 1 || !from.Equals(serverEp)) continue;
                    if (sim.loss > 0 && rng.NextDouble() < sim.loss) continue;
                    var p = new byte[n - 1];
                    Buffer.BlockCopy(buf, 1, p, 0, n - 1);
                    HandleUnreliable((Msg)buf[0], p, reliable: false);
                }
                catch (SocketException e)
                {
                    // Windows reports an ICMP "port unreachable" from an earlier send as ConnectionReset: keep going.
                    if (closed) return;
                    if (e.SocketErrorCode != SocketError.ConnectionReset) Thread.Sleep(50);
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception e)
                {
                    if (closed) return;
                    log("udp receive: " + e.Message);
                }
            }
        }

        void SendLoop()
        {
            try
            {
                while (true)
                {
                    Outgoing o;
                    bool isUdp;
                    long now;
                    lock (gate)
                    {
                        while (true)
                        {
                            if (closed) return;
                            now = NowMs;
                            long next = long.MaxValue;
                            if (tcpOut.Count > 0) next = tcpOut.Peek().due;
                            if (udpOut.Count > 0) next = Math.Min(next, udpOut[0].due);
                            // a backup frame only when nothing else is waiting, at the backup pace
                            if (tcpOut.Count == 0 && bulkOut.Count > 0) next = Math.Min(next, Math.Max(bulkNextAt, bulkOut.Peek().due));
                            if (next <= now) break;
                            Monitor.Wait(gate, next == long.MaxValue ? 1000 : (int)Math.Min(1000, next - now));
                        }
                        isUdp = udpOut.Count > 0 && udpOut[0].due <= now && (tcpOut.Count == 0 || udpOut[0].due <= tcpOut.Peek().due);
                        if (isUdp) { o = udpOut[0]; udpOut.RemoveAt(0); }
                        else if (tcpOut.Count > 0 && tcpOut.Peek().due <= now) o = tcpOut.Dequeue();
                        else
                        {
                            o = bulkOut.Dequeue();
                            bulkNextAt = Math.Max(now, bulkNextAt) + o.bytes.Length * 1000L / Math.Max(1024, BackupRate);
                        }
                    }
                    if (isUdp)
                    {
                        try { udp.SendTo(o.bytes, serverEp); }
                        catch (SocketException) { }
                    }
                    else
                    {
                        stream.Write(o.bytes, 0, o.bytes.Length);
                        Interlocked.Add(ref TcpBytesOut, o.bytes.Length);
                        if (o.saveBytes > 0) Interlocked.Add(ref UploadSent, o.saveBytes);
                        if (o.bytes[4] == (byte)Msg.Bye) { stream.Flush(); Shutdown(closingReason ?? "closed"); return; }
                    }
                }
            }
            catch (Exception e)
            {
                if (!closed) Shutdown("send failed: " + Describe(e));
            }
        }
    }
}
