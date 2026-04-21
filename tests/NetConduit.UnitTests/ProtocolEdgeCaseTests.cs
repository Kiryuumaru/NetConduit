namespace NetConduit.UnitTests;

/// <summary>
/// Tests for protocol edge cases and boundary conditions not covered elsewhere:
/// - Channel ID edge cases (long, special characters, unicode, duplicate)
/// - Large single-write payloads
/// - Single-byte writes
/// - Multiple channels with same prefix
/// - Accept before Open timing
/// </summary>
public class ProtocolEdgeCaseTests
{
    private const int TestTimeout = 30000;

    #region Channel ID Edge Cases

    [Fact(Timeout = TestTimeout)]
    public async Task ChannelId_LongName_WorksCorrectly()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var longId = new string('x', 500);
        var writer = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = longId }, cts.Token);
        var reader = await muxB.AcceptChannelAsync(longId, cts.Token);

        await writer.WriteAsync(new byte[] { 42 }, cts.Token);
        await writer.CloseAsync();

        var buf = new byte[1];
        var n = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(42, buf[0]);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ChannelId_SpecialCharacters_WorksCorrectly()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var specialId = "ch/test:with.special-chars_and spaces!@#$%";
        var writer = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = specialId }, cts.Token);
        var reader = await muxB.AcceptChannelAsync(specialId, cts.Token);

        await writer.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        await writer.CloseAsync();

        var buf = new byte[3];
        var total = 0;
        while (total < 3)
        {
            var n = await reader.ReadAsync(buf.AsMemory(total), cts.Token);
            if (n == 0) break;
            total += n;
        }
        Assert.Equal(3, total);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ChannelId_Unicode_WorksCorrectly()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var unicodeId = "频道_チャンネル_канал";
        var writer = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = unicodeId }, cts.Token);
        var reader = await muxB.AcceptChannelAsync(unicodeId, cts.Token);

        await writer.WriteAsync(new byte[] { 99 }, cts.Token);
        await writer.CloseAsync();

        var buf = new byte[1];
        var n = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(99, buf[0]);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ChannelId_SimilarPrefixes_NoConfusion()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        // Open channels with similar prefixes
        var w1 = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "channel" }, cts.Token);
        var r1 = await muxB.AcceptChannelAsync("channel", cts.Token);

        var w2 = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "channel_1" }, cts.Token);
        var r2 = await muxB.AcceptChannelAsync("channel_1", cts.Token);

        var w3 = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "channel_10" }, cts.Token);
        var r3 = await muxB.AcceptChannelAsync("channel_10", cts.Token);

        // Write unique data to each
        await w1.WriteAsync(new byte[] { 1 }, cts.Token);
        await w1.CloseAsync();
        await w2.WriteAsync(new byte[] { 2 }, cts.Token);
        await w2.CloseAsync();
        await w3.WriteAsync(new byte[] { 3 }, cts.Token);
        await w3.CloseAsync();

        // Verify each reader gets its own data
        var buf1 = new byte[1];
        var n1 = await r1.ReadAsync(buf1, cts.Token);
        Assert.Equal(1, n1);
        Assert.Equal(1, buf1[0]);

        var buf2 = new byte[1];
        var n2 = await r2.ReadAsync(buf2, cts.Token);
        Assert.Equal(1, n2);
        Assert.Equal(2, buf2[0]);

        var buf3 = new byte[1];
        var n3 = await r3.ReadAsync(buf3, cts.Token);
        Assert.Equal(1, n3);
        Assert.Equal(3, buf3[0]);

        cts.Cancel();
    }

    #endregion

    #region Data Size Edge Cases

    [Fact(Timeout = TestTimeout)]
    public async Task SingleByteWrite_ReceivedCorrectly()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "tiny" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("tiny", cts.Token);

        await writer.WriteAsync(new byte[] { 0xFF }, cts.Token);
        await writer.CloseAsync();

        var buf = new byte[1];
        var n = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(0xFF, buf[0]);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task LargePayload_256KB_ReceivedCorrectly()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "large" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("large", cts.Token);

        var data = new byte[256 * 1024];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);
        await writer.CloseAsync();

        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int read;
        while ((read = await reader.ReadAsync(buf, cts.Token)) > 0)
        {
            ms.Write(buf, 0, read);
        }

        Assert.Equal(data, ms.ToArray());

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ManySmallWrites_AllReceivedInOrder()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "small" }, cts.Token);
        var reader = await muxB.AcceptChannelAsync("small", cts.Token);

        // Write 1000 single-byte messages
        for (int i = 0; i < 1000; i++)
        {
            await writer.WriteAsync(new byte[] { (byte)(i % 256) }, cts.Token);
        }
        await writer.CloseAsync();

        // Read all and verify order
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        int read;
        while ((read = await reader.ReadAsync(buf, cts.Token)) > 0)
        {
            ms.Write(buf, 0, read);
        }

        var received = ms.ToArray();
        Assert.Equal(1000, received.Length);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal((byte)(i % 256), received[i]);
        }

        cts.Cancel();
    }

    #endregion

    #region Accept Before Open Timing

    [Fact(Timeout = TestTimeout)]
    public async Task AcceptBeforeOpen_NamedAccept_Works()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        // Start accept first
        var acceptTask = muxB.AcceptChannelAsync("future", cts.Token);

        // Small delay
        await Task.Delay(100);

        // Then open
        var writer = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "future" }, cts.Token);

        var reader = await acceptTask;
        Assert.NotNull(reader);
        Assert.Equal("future", reader.ChannelId);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MultipleAcceptBeforeOpen_AllResolveCorrectly()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        // Queue multiple named accepts
        var t1 = muxB.AcceptChannelAsync("ch_a", cts.Token);
        var t2 = muxB.AcceptChannelAsync("ch_b", cts.Token);
        var t3 = muxB.AcceptChannelAsync("ch_c", cts.Token);

        await Task.Delay(50);

        // Open in reverse order
        await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "ch_c" }, cts.Token);
        await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "ch_a" }, cts.Token);
        await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "ch_b" }, cts.Token);

        var r1 = await t1;
        var r2 = await t2;
        var r3 = await t3;

        Assert.Equal("ch_a", r1.ChannelId);
        Assert.Equal("ch_b", r2.ChannelId);
        Assert.Equal("ch_c", r3.ChannelId);

        cts.Cancel();
    }

    #endregion

    #region Multiplexer State Edge Cases

    [Fact(Timeout = TestTimeout)]
    public async Task IsRunning_TrueAfterStart_FalseAfterCancel()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, runA, runB) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.True(muxA.IsRunning);
        Assert.True(muxB.IsRunning);

        cts.Cancel();

        await Task.WhenAll(
            runA.ContinueWith(_ => { }),
            runB.ContinueWith(_ => { }));

        Assert.False(muxA.IsRunning);
    }

    [Fact(Timeout = TestTimeout)]
    public async Task IsShuttingDown_FalseInitially_TrueAfterGoAway()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.False(muxA.IsShuttingDown);

        await muxA.GoAwayAsync(cts.Token);

        Assert.True(muxA.IsShuttingDown);

        cts.Cancel();
    }

    #endregion
}
