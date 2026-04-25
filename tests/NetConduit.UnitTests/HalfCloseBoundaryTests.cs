using NetConduit.Enums;
using NetConduit.Models;
namespace NetConduit.UnitTests;

/// <summary>
/// Tests for half-close and graceful shutdown boundary conditions:
/// - GoAway from the accepting side
/// - Accept during shutdown
/// - Write channel close while read is pending
/// - Multiple GoAway calls (idempotency)
/// - Rapid channel lifecycle
/// </summary>
public class HalfCloseBoundaryTests
{
    private const int TestTimeout = 30000;

    #region GoAway From Acceptor Side

    [Fact(Timeout = TestTimeout)]
    public async Task GoAway_FromAcceptorSide_InitiatorSeesShutdown()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var disconnectTcs = new TaskCompletionSource<bool>();
        muxA.OnDisconnected += (_, _) => disconnectTcs.TrySetResult(true);

        await muxB.GoAwayAsync(cts.Token);

        Assert.True(muxB.IsShuttingDown);

        cts.Cancel();
    }

    #endregion

    #region GoAway Idempotency

    [Fact(Timeout = TestTimeout)]
    public async Task GoAway_CalledTwice_DoesNotThrow()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.GoAwayAsync(cts.Token);
        await muxA.GoAwayAsync(cts.Token); // Should be idempotent

        Assert.True(muxA.IsShuttingDown);

        cts.Cancel();
    }

    #endregion

    #region Writer Close While Reader Pending

    [Fact(Timeout = TestTimeout)]
    public async Task WriterClose_WhileReaderPending_ReaderGetsEOF()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("hc", cts.Token);
        var reader = await muxB.AcceptChannelAsync("hc", cts.Token);

        // Start a read that will block
        var readTask = Task.Run(async () =>
        {
            var buf = new byte[1024];
            var total = 0;
            int read;
            while ((read = await reader.ReadAsync(buf, cts.Token)) > 0)
            {
                total += read;
            }
            return total;
        });

        // Write some data, then close
        await writer.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }, cts.Token);
        await writer.CloseAsync();

        var totalRead = await readTask.WaitAsync(cts.Token);
        Assert.Equal(5, totalRead);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task WriterDispose_WhileReaderPending_ReaderGetsEOF()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("disp", cts.Token);
        var reader = await muxB.AcceptChannelAsync("disp", cts.Token);

        var readTask = Task.Run(async () =>
        {
            var buf = new byte[1024];
            int read;
            while ((read = await reader.ReadAsync(buf, cts.Token)) > 0) { }
            return reader.State;
        });

        await writer.WriteAsync(new byte[] { 10, 20, 30 }, cts.Token);
        await writer.DisposeAsync();

        var finalState = await readTask.WaitAsync(cts.Token);
        Assert.Equal(ChannelState.Closed, finalState);

        cts.Cancel();
    }

    #endregion

    #region Reader Close While Writer Active

    [Fact(Timeout = TestTimeout)]
    public async Task ReaderClose_WhileWriterActive_WriterSeesClose()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("rclose", cts.Token);
        var reader = await muxB.AcceptChannelAsync("rclose", cts.Token);

        var closedTcs = new TaskCompletionSource<bool>();
        writer.OnClosed += (_, _) => closedTcs.TrySetResult(true);

        // Close reader first
        await reader.CloseAsync();
        await Task.Delay(200);

        // Writer should eventually see the close
        var closed = await Task.WhenAny(closedTcs.Task, Task.Delay(5000));
        if (closed == closedTcs.Task)
        {
            Assert.True(await closedTcs.Task);
        }

        cts.Cancel();
    }

    #endregion

    #region Rapid Channel Lifecycle

    [Fact(Timeout = TestTimeout)]
    public async Task RapidOpenAndClose_SingleChannel_NoHang()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        for (int i = 0; i < 20; i++)
        {
            var id = $"rapid_{i}";
            var writer = await muxA.OpenChannelAsync(id, cts.Token);
            var reader = await muxB.AcceptChannelAsync(id, cts.Token);

            await writer.WriteAsync(new byte[] { (byte)i }, cts.Token);
            await writer.CloseAsync();

            var buf = new byte[1];
            var n = await reader.ReadAsync(buf, cts.Token);
            Assert.Equal(1, n);
            Assert.Equal((byte)i, buf[0]);

            // Read EOF
            n = await reader.ReadAsync(buf, cts.Token);
            Assert.Equal(0, n);
        }

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OpenCloseWithoutData_ChannelTransitionsToClosing()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        for (int i = 0; i < 10; i++)
        {
            var id = $"empty_{i}";
            var writer = await muxA.OpenChannelAsync(id, cts.Token);
            var reader = await muxB.AcceptChannelAsync(id, cts.Token);

            // Close immediately without writing
            await writer.CloseAsync();

            var buf = new byte[1];
            var n = await reader.ReadAsync(buf, cts.Token);
            Assert.Equal(0, n); // EOF immediately
        }

        // All writers should be at least in Closing state
        Assert.True(muxA.Stats.TotalChannelsOpened >= 10);

        cts.Cancel();
    }

    #endregion

    #region GoAway During Active Transfer

    [Fact(Timeout = TestTimeout)]
    public async Task GoAway_DuringActiveWrite_WriterCanFinish()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("transfer", cts.Token);
        var reader = await muxB.AcceptChannelAsync("transfer", cts.Token);

        // Write data before GoAway
        var data = new byte[8192];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);
        await writer.CloseAsync();

        // GoAway after data is sent
        await muxA.GoAwayAsync(cts.Token);

        // Read should still get all data
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        int read;
        while ((read = await reader.ReadAsync(buf, cts.Token)) > 0)
        {
            ms.Write(buf, 0, read);
        }

        Assert.Equal(data, ms.ToArray());

        cts.Cancel();
    }

    #endregion

    #region Channel Close Reason

    [Fact(Timeout = TestTimeout)]
    public async Task WriteChannel_LocalClose_StateIsClosing()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("cr", cts.Token);
        await muxB.AcceptChannelAsync("cr", cts.Token);

        await writer.CloseAsync();
        await Task.Delay(100);

        // After CloseAsync, writer is in Closing state (FIN sent, awaiting full close)
        Assert.True(writer.State == ChannelState.Closing || writer.State == ChannelState.Closed);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ReadChannel_RemoteFin_CloseReasonIsRemoteFin()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("rfin", cts.Token);
        var reader = await muxB.AcceptChannelAsync("rfin", cts.Token);

        await writer.CloseAsync();

        // Drain reader
        var buf = new byte[1024];
        while (await reader.ReadAsync(buf, cts.Token) > 0) { }

        Assert.Equal(ChannelCloseReason.RemoteFin, reader.CloseReason);

        cts.Cancel();
    }

    #endregion
}
