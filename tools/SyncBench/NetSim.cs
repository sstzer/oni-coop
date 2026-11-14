namespace SyncBench;

sealed record NetProfile(string Name, double OneWayMs, double JitterMs);

abstract class LossModel
{
    public abstract string Name { get; }
    public abstract bool Drop(Random r);
    public virtual void Reset(Random r) { }
}

sealed class NoLoss : LossModel
{
    public override string Name => "no loss";
    public override bool Drop(Random r) => false;
}

sealed class Bernoulli(double p) : LossModel
{
    public override string Name => $"random {p:P0}";
    public override bool Drop(Random r) => r.NextDouble() < p;
}

/// <summary>Two-state bursty loss: in "bad" every packet is lost; mean burst length = 1/pBadToGood.</summary>
sealed class GilbertElliott(double avgLoss, double meanBurst) : LossModel
{
    private readonly double pBG = 1.0 / meanBurst;
    private readonly double pGB = avgLoss / (1 - avgLoss) / meanBurst;
    private bool bad;
    public override string Name => $"bursty {avgLoss:P0} (bursts of ~{meanBurst:F0})";
    public override void Reset(Random r) => bad = r.NextDouble() < avgLoss;
    public override bool Drop(Random r)
    {
        bad = bad ? r.NextDouble() >= pBG : r.NextDouble() < pGB;
        return bad;
    }
}

/// <summary>Replays a measured per-packet loss sequence (from NetProbe), starting at a random offset.</summary>
sealed class TraceLoss(bool[] lost, string name) : LossModel
{
    private int i;
    public override string Name => name;
    public override void Reset(Random r) => i = r.Next(lost.Length);
    public override bool Drop(Random r) => lost[i++ % lost.Length];
}

enum Strategy { Keyframe, DeltaPrevNack, DeltaAcked, ReliableOrdered }

sealed class SimResult
{
    public double DownKbps, UpKbps, P95PacketBytes;
    public double StaleMeanMs, StaleP95Ms, StaleMaxMs, FrozenOver1s;
    public double VisualErrMean, VisualErrP95, StrictErrMean;
}

/// <summary>
/// Event-driven replay of one viewport's real tick sequence over a lossy link. The client starts with
/// the tick-0 state (initial join is a reliable transfer). Metrics are sampled at every server tick:
/// how old the client's displayed state is, and how many cells differ from the server's current state.
/// </summary>
sealed class NetSim(int ticks, double tickMs, Func<int, int> fullSize, Func<int, int, int> deltaSize,
                    Func<int, int, (double vis, double str)> err)
{
    private const int Mtu = 1200, FragOverhead = 36, AckBytes = 16;

    private readonly PriorityQueue<Action, (double, long)> q = new();
    private long order;
    private double now;
    private Random rng = new(0);
    private LossModel down = new NoLoss(), up = new NoLoss();
    private NetProfile prof = new("", 0, 0);
    private long bytesDown, bytesUp;
    private readonly List<int> packetSizes = new();

    private void At(double t, Action a) => q.Enqueue(a, (t, order++));

    private double Delay() => prof.OneWayMs + Math.Abs(Gauss()) * prof.JitterMs;
    private double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    /// <summary>Server -> client. Large payloads are fragmented; losing any fragment loses the packet.</summary>
    private void SendDown(int size, Action onArrive)
    {
        int frags = Math.Max(1, (size + Mtu - 1) / Mtu);
        bool lost = false;
        for (int f = 0; f < frags; f++) lost |= down.Drop(rng);
        bytesDown += size + frags * FragOverhead;
        packetSizes.Add(size);
        if (!lost) At(now + Delay(), onArrive);
    }

    private void SendUp(Action onArrive)
    {
        bytesUp += AckBytes + FragOverhead;
        if (!up.Drop(rng)) At(now + Delay(), onArrive);
    }

    public SimResult Run(Strategy s, NetProfile p, LossModel downModel, LossModel upModel, int every, int seed)
    {
        rng = new Random(seed);
        prof = p; down = downModel; up = upModel;
        down.Reset(rng); up.Reset(rng);
        q.Clear(); order = 0; now = 0; bytesDown = bytesUp = 0; packetSizes.Clear();

        int displayed = 0;                  // client: newest state on screen
        var stale = new List<double>();
        var vis = new List<double>();
        var str = new List<double>();

        // per-strategy state
        int lastSent = 0; bool needKey = false;                 // DeltaPrevNack (server)
        int chain = 0; bool broken = false, nackTimer = false;  // DeltaPrevNack (client)
        int acked = 0;                                          // DeltaAcked (server)
        var unacked = new Dictionary<int, (int size, int baseTick, double sentAt)>(); // ReliableOrdered (server)
        var buffer = new HashSet<int>(); var order2 = new List<int>(); int nextIdx = 1; // ReliableOrdered (client)
        double rto = Math.Max(200, 2 * p.OneWayMs + 4 * p.JitterMs + 50);
        for (int i = 0; i < ticks; i += every) order2.Add(i);

        void Nack()
        {
            SendUp(() => needKey = true);
            if (!nackTimer)
            {
                nackTimer = true;
                At(now + 250, () => { nackTimer = false; if (broken) Nack(); });
            }
        }

        void ReliableSend(int tick, int size, int baseTick)
        {
            unacked[tick] = (size, baseTick, now);
            SendDown(size, () =>
            {
                buffer.Add(tick);
                while (nextIdx < order2.Count && buffer.Contains(order2[nextIdx]))
                    displayed = Math.Max(displayed, order2[nextIdx++]);
                int cum = order2[nextIdx - 1];
                SendUp(() => { foreach (int t in unacked.Keys.Where(t => t <= cum || t == tick).ToList()) unacked.Remove(t); });
            });
        }

        if (s == Strategy.ReliableOrdered)
        {
            void Check()
            {
                foreach (var (t, u) in unacked.ToList())
                    if (now - u.sentAt >= rto) ReliableSend(t, u.size, u.baseTick);
                if (now < ticks * tickMs) At(now + 50, Check);
            }
            At(50, Check);
        }

        for (int i = 0; i < ticks; i++)
        {
            int tick = i;
            At(i * tickMs, () =>
            {
                if (tick >= 5)
                {
                    stale.Add((tick - displayed) * tickMs);
                    var (v, st) = err(displayed, tick);
                    vis.Add(v); str.Add(st);
                }
                if (tick == 0 || tick % every != 0) return;
                switch (s)
                {
                    case Strategy.Keyframe:
                        SendDown(fullSize(tick), () => displayed = Math.Max(displayed, tick));
                        break;
                    case Strategy.DeltaPrevNack:
                    {
                        bool key = needKey; needKey = false;
                        int b = key ? -1 : lastSent;
                        lastSent = tick;
                        SendDown(key ? fullSize(tick) : deltaSize(b, tick), () =>
                        {
                            if (b == -1 || b == chain)
                            {
                                if (tick > chain) { chain = tick; displayed = Math.Max(displayed, tick); broken = false; }
                            }
                            else if (tick > chain && !broken) { broken = true; Nack(); }
                        });
                        break;
                    }
                    case Strategy.DeltaAcked:
                    {
                        int b = acked;
                        SendDown(deltaSize(b, tick), () =>
                        {
                            displayed = Math.Max(displayed, tick);
                            SendUp(() => acked = Math.Max(acked, tick));
                        });
                        break;
                    }
                    case Strategy.ReliableOrdered:
                    {
                        int prevIdx = order2.IndexOf(tick) - 1;
                        ReliableSend(tick, deltaSize(order2[prevIdx], tick), order2[prevIdx]);
                        break;
                    }
                }
            });
        }

        while (q.TryDequeue(out var a, out var key))
        {
            now = key.Item1;
            if (now > ticks * tickMs + 5000) break;
            a();
        }

        double secs = ticks * tickMs / 1000.0;
        stale.Sort(); vis.Sort(); packetSizes.Sort();
        return new SimResult
        {
            DownKbps = bytesDown * 8 / 1000.0 / secs,
            UpKbps = bytesUp * 8 / 1000.0 / secs,
            P95PacketBytes = Pct(packetSizes.Select(x => (double)x).ToList(), 0.95),
            StaleMeanMs = stale.Average(), StaleP95Ms = Pct(stale, 0.95), StaleMaxMs = stale[^1],
            FrozenOver1s = stale.Count(x => x > 1000) / (double)stale.Count,
            VisualErrMean = vis.Average(), VisualErrP95 = Pct(vis, 0.95), StrictErrMean = str.Average(),
        };
    }

    private static double Pct(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
}
