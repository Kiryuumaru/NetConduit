using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace NetConduit.Mesh.IntegrationTests;

public class TurbulenceTests
{
    private const int MessageSize = 256;
    private const int PayloadSize = MessageSize - 8;

    /// <summary>
    /// Continuous 256-byte messages streamed A→D through a redundant
    /// 5-node mesh while links are randomly killed and recreated.
    /// The routed sub-mux must survive without terminal disconnect
    /// and deliver all messages uncorrupted and in order.
    /// </summary>
    [Fact(Timeout = 180_000)]
    [Trait("Category", "HighMemory")]
    public async Task ContinuousStream_SurvivesRapidTopologyChurn()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
        var meshD = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "D" });
        var meshR1 = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "R1" });
        var meshR2 = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "R2" });
        var meshR3 = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "R3" });

        meshA.Start(); meshD.Start(); meshR1.Start(); meshR2.Start(); meshR3.Start();

        var links = new List<MuxLink>
        {
            await MuxLink.CreateAsync("A", meshA, "R1", meshR1, cts.Token),
            await MuxLink.CreateAsync("A", meshA, "R2", meshR2, cts.Token),
            await MuxLink.CreateAsync("A", meshA, "R3", meshR3, cts.Token),
            await MuxLink.CreateAsync("R1", meshR1, "D", meshD, cts.Token),
            await MuxLink.CreateAsync("R2", meshR2, "D", meshD, cts.Token),
            await MuxLink.CreateAsync("R3", meshR3, "D", meshD, cts.Token),
            await MuxLink.CreateAsync("R1", meshR1, "R2", meshR2, cts.Token),
            await MuxLink.CreateAsync("R2", meshR2, "R3", meshR3, cts.Token),
            await MuxLink.CreateAsync("R1", meshR1, "R3", meshR3, cts.Token),
        };

        await meshA.WaitForReachableAsync("D", cts.Token);

        var acceptAtD = Task.Run(async () =>
        {
            await foreach (var inc in meshD.AcceptMultiplexersAsync(cts.Token))
                return inc;
            throw new Exception("never accepted");
        });

        var subA = await meshA.OpenMultiplexerAsync("D", "turbulence", cts.Token);
        var atD = await acceptAtD;

        var writer = subA.OpenChannel(new ChannelOptions { ChannelId = "stream", SlabSize = 8 * 1024 * 1024 });
        var reader = atD.Multiplexer.AcceptChannel("stream");
        await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

        int terminalDisconnect = 0;
        subA.Disconnected += (_, _) => Interlocked.Exchange(ref terminalDisconnect, 1);

        long lastSeqWritten = -1;

        // ─ Churn: randomly kill and recreate links ────────────────────
        using var churnCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var churnTask = Task.Run(async () =>
        {
            var rng = new Random(42);
            var snapshot = links.ToArray();
            var sync = new object();
            try
            {
                while (!churnCts.IsCancellationRequested)
                {
                    MuxLink target;
                    lock (sync) target = snapshot[rng.Next(snapshot.Length)];

                    await MuxLink.TearDownAsync(target);
                    await Task.Delay(400 + rng.Next(200), churnCts.Token);

                    var fresh = await MuxLink.CreateAsync(
                        target.NodeA_Id, target.NodeA_Mesh,
                        target.NodeB_Id, target.NodeB_Mesh, churnCts.Token);
                    lock (sync) { var i = Array.IndexOf(snapshot, target); if (i >= 0) snapshot[i] = fresh; }

                    await Task.Delay(400 + rng.Next(200), churnCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }, churnCts.Token);

        // ─ Stream: A writes continuously ──────────────────────────────
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var writerDone = new TaskCompletionSource();
        var writerTask = Task.Run(async () =>
        {
            try
            {
                for (long seq = 0; !streamCts.IsCancellationRequested; seq++)
                {
                    var msg = BuildMessage(seq);
                    while (!streamCts.IsCancellationRequested)
                    {
                        try { await writer.WriteAsync(msg, streamCts.Token); break; }
                        catch (OperationCanceledException) when (!streamCts.IsCancellationRequested)
                        { await Task.Delay(100, streamCts.Token); }
                    }
                    await Task.Delay(50, streamCts.Token);
                    Volatile.Write(ref lastSeqWritten, seq);
                }
            }
            catch (OperationCanceledException) { }
            writerDone.TrySetResult();
        }, streamCts.Token);

        // ─ Reader: D drains live ─────────────────────────────────────
        var received = new ConcurrentBag<(long Seq, byte[] Payload)>();
        var readerDone = new TaskCompletionSource();
        var readerTask = Task.Run(async () =>
        {
            var buf = new byte[65536]; int off = 0, cnt = 0;
            try
            {
                while (true)
                {
                    int n = await reader.ReadAsync(buf.AsMemory(off + cnt), cts.Token);
                    if (n == 0) break;
                    cnt += n;
                    while (cnt >= MessageSize)
                    {
                        long seq = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(off, 8));
                        var payload = new byte[PayloadSize];
                        Array.Copy(buf, off + 8, payload, 0, PayloadSize);
                        received.Add((seq, payload));
                        off += MessageSize; cnt -= MessageSize;
                    }
                    if (off > 0 && cnt > 0) { Array.Copy(buf, off, buf, 0, cnt); off = 0; }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            readerDone.TrySetResult();
        }, cts.Token);

        // ─ Run churn + streaming for 90s ──────────────────────────────
        await Task.Delay(TimeSpan.FromSeconds(90), cts.Token);
        churnCts.Cancel(); try { await churnTask; } catch { }
        await meshA.WaitForReachableAsync("D", cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

        streamCts.Cancel(); await writerDone.Task;
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

        try { await writer.DisposeAsync(); } catch { }
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        await readerDone.Task;

        // ─ Assert ─────────────────────────────────────────────────────
        var sorted = received.OrderBy(r => r.Seq).ToList();
        long totalWritten = Volatile.Read(ref lastSeqWritten) + 1;

        Assert.Equal(0, Volatile.Read(ref terminalDisconnect));

        // All messages written must arrive — in order, no gaps, no corruption.
        Assert.Equal(totalWritten, sorted.Count);

        for (int i = 0; i < sorted.Count; i++)
        {
            var (seq, payload) = sorted[i];
            Assert.Equal((long)i, seq);
            Assert.Equal(CreatePayload(seq), payload);
        }

        Assert.True(meshR1.Stats.RelayBytesForwarded + meshR2.Stats.RelayBytesForwarded
                  + meshR3.Stats.RelayBytesForwarded > 0, "No relay bytes");

        await writer.DisposeAsync(); await reader.DisposeAsync();
        await subA.DisposeAsync(); await atD.Multiplexer.DisposeAsync();
        foreach (var l in links) try { await MuxLink.TearDownAsync(l); } catch { }
        await meshA.DisposeAsync(); await meshD.DisposeAsync();
        await meshR1.DisposeAsync(); await meshR2.DisposeAsync(); await meshR3.DisposeAsync();
    }

    private static byte[] BuildMessage(long seq)
    {
        var m = new byte[MessageSize];
        BinaryPrimitives.WriteInt64LittleEndian(m, seq);
        CreatePayload(seq).CopyTo(m.AsSpan(8));
        return m;
    }

    private static byte[] CreatePayload(long seq)
    {
        var p = new byte[PayloadSize];
        for (int i = 0; i < p.Length; i++) p[i] = (byte)((seq + i * 7 + 13) & 0xFF);
        return p;
    }

    private sealed class MuxLink(string na, MeshMultiplexer ma, string nb, MeshMultiplexer mb,
        StreamMultiplexer sa, StreamMultiplexer sb, DuplexMemoryStream bk)
    {
        public string NodeA_Id => na; public MeshMultiplexer NodeA_Mesh => ma;
        public string NodeB_Id => nb; public MeshMultiplexer NodeB_Mesh => mb;
        public StreamMultiplexer MuxA => sa; public StreamMultiplexer MuxB => sb;
        public DuplexMemoryStream Backing => bk;

        internal static async Task<MuxLink> CreateAsync(
            string ia, MeshMultiplexer ma, string ib, MeshMultiplexer mb, CancellationToken ct)
        {
            var (a, b, bk) = await MuxFixture.CreateMuxPairAsync(ct);
            ma.AddNeighbor(ib, a); mb.AddNeighbor(ia, b);
            return new MuxLink(ia, ma, ib, mb, a, b, bk);
        }

        internal static async Task TearDownAsync(MuxLink l)
        {
            l.NodeA_Mesh.RemoveNeighbor(l.NodeB_Id);
            l.NodeB_Mesh.RemoveNeighbor(l.NodeA_Id);
            try { await l.MuxA.DisposeAsync(); } catch { }
            try { await l.MuxB.DisposeAsync(); } catch { }
            try { await ((IAsyncDisposable)l.Backing).DisposeAsync(); } catch { }
        }
    }
}
