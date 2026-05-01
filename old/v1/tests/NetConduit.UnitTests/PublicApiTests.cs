using NetConduit.Enums;
using NetConduit.Models;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for public API surface that is not covered by other test files:
/// GetWriteChannel, GetReadChannel, ActiveChannelIds, OpenedChannelIds,
/// AcceptedChannelIds, RemoteSessionId, SessionId, MultiplexerStats properties.
/// </summary>
public class PublicApiTests
{
    private const int TestTimeout = 15000;

    #region GetWriteChannel / GetReadChannel

    [Fact(Timeout = TestTimeout)]
    public async Task GetWriteChannel_ExistingChannel_ReturnsChannel()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("ch1", cts.Token);
        await muxB.AcceptChannelAsync("ch1", cts.Token);

        var found = muxA.GetWriteChannel("ch1");

        Assert.NotNull(found);
        Assert.Same(writer, found);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task GetWriteChannel_NonExistentChannel_ReturnsNull()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var found = muxA.GetWriteChannel("nonexistent");

        Assert.Null(found);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task GetReadChannel_ExistingChannel_ReturnsChannel()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.OpenChannelAsync("ch1", cts.Token);
        var reader = await muxB.AcceptChannelAsync("ch1", cts.Token);

        var found = muxB.GetReadChannel("ch1");

        Assert.NotNull(found);
        Assert.Same(reader, found);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task GetReadChannel_NonExistentChannel_ReturnsNull()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var found = muxB.GetReadChannel("nonexistent");

        Assert.Null(found);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task GetWriteChannel_AfterChannelClosed_ReturnsClosedOrNull()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("ch1", cts.Token);
        var reader = await muxB.AcceptChannelAsync("ch1", cts.Token);

        await writer.CloseAsync();

        // Drain reader to complete the close handshake
        var buf = new byte[1024];
        while (await reader.ReadAsync(buf, cts.Token) > 0) { }
        await Task.Delay(300);

        var found = muxA.GetWriteChannel("ch1");

        // Channel is either removed from dict or still present but in Closed state
        if (found is not null)
        {
            Assert.True(found.State == ChannelState.Closed || found.State == ChannelState.Closing);
        }

        cts.Cancel();
    }

    #endregion

    #region ActiveChannelIds / OpenedChannelIds / AcceptedChannelIds

    [Fact(Timeout = TestTimeout)]
    public async Task ActiveChannelIds_NoChannels_ReturnsEmpty()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.Empty(muxA.ActiveChannelIds);
        Assert.Empty(muxB.ActiveChannelIds);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ActiveChannelIds_WithOpenChannels_ContainsChannelIds()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.OpenChannelAsync("ch1", cts.Token);
        await muxB.AcceptChannelAsync("ch1", cts.Token);

        await muxA.OpenChannelAsync("ch2", cts.Token);
        await muxB.AcceptChannelAsync("ch2", cts.Token);

        var idsA = muxA.ActiveChannelIds;
        Assert.Contains("ch1", idsA);
        Assert.Contains("ch2", idsA);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OpenedChannelIds_ReturnsOnlyLocallyOpenedChannels()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.OpenChannelAsync("fromA", cts.Token);
        await muxB.AcceptChannelAsync("fromA", cts.Token);

        var openedA = muxA.OpenedChannelIds;
        var openedB = muxB.OpenedChannelIds;

        Assert.Contains("fromA", openedA);
        Assert.Empty(openedB);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task AcceptedChannelIds_ReturnsOnlyRemotelyOpenedChannels()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.OpenChannelAsync("fromA", cts.Token);
        await muxB.AcceptChannelAsync("fromA", cts.Token);

        var acceptedA = muxA.AcceptedChannelIds;
        var acceptedB = muxB.AcceptedChannelIds;

        Assert.Empty(acceptedA);
        Assert.Contains("fromA", acceptedB);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ActiveChannelIds_BothDirections_ContainsAll()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.OpenChannelAsync("AtoB", cts.Token);
        await muxB.AcceptChannelAsync("AtoB", cts.Token);

        await muxB.OpenChannelAsync("BtoA", cts.Token);
        await muxA.AcceptChannelAsync("BtoA", cts.Token);

        var idsA = muxA.ActiveChannelIds;
        Assert.Contains("AtoB", idsA);
        Assert.Contains("BtoA", idsA);

        Assert.Contains("AtoB", (IReadOnlyCollection<string>)muxA.OpenedChannelIds);
        Assert.Contains("BtoA", (IReadOnlyCollection<string>)muxA.AcceptedChannelIds);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ActiveChannelIds_AfterFullClose_ChannelCleaned()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("ch1", cts.Token);
        var reader = await muxB.AcceptChannelAsync("ch1", cts.Token);

        Assert.Contains("ch1", muxA.ActiveChannelIds);

        await writer.CloseAsync();

        // Drain reader to complete close handshake
        var buf = new byte[1024];
        while (await reader.ReadAsync(buf, cts.Token) > 0) { }

        // Dispose both sides to fully clean up
        await writer.DisposeAsync();
        reader.Dispose();
        await Task.Delay(300);

        // After full cleanup, OpenChannels stat should be 0
        Assert.Equal(0, muxA.Stats.OpenChannels);

        cts.Cancel();
    }

    #endregion

    #region SessionId / RemoteSessionId

    [Fact(Timeout = TestTimeout)]
    public async Task SessionId_IsNonEmpty()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.NotEqual(Guid.Empty, muxA.SessionId);
        Assert.NotEqual(Guid.Empty, muxB.SessionId);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task SessionId_IsDifferentBetweenPeers()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.NotEqual(muxA.SessionId, muxB.SessionId);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task RemoteSessionId_MatchesPeerSessionId()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.Equal(muxA.SessionId, muxB.RemoteSessionId);
        Assert.Equal(muxB.SessionId, muxA.RemoteSessionId);

        cts.Cancel();
    }

    #endregion

    #region MultiplexerStats Properties

    [Fact(Timeout = TestTimeout)]
    public async Task Stats_InitialState_AllZero()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        Assert.Equal(0, muxA.Stats.OpenChannels);
        Assert.Equal(0, muxA.Stats.TotalChannelsOpened);
        Assert.Equal(0, muxA.Stats.TotalChannelsClosed);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Stats_TotalChannelsOpened_IncrementsOnOpen()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.OpenChannelAsync("ch1", cts.Token);
        await muxB.AcceptChannelAsync("ch1", cts.Token);

        Assert.True(muxA.Stats.TotalChannelsOpened >= 1);

        await muxA.OpenChannelAsync("ch2", cts.Token);
        await muxB.AcceptChannelAsync("ch2", cts.Token);

        Assert.True(muxA.Stats.TotalChannelsOpened >= 2);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Stats_TotalChannelsClosed_IncrementsOnClose()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("ch1", cts.Token);
        var reader = await muxB.AcceptChannelAsync("ch1", cts.Token);

        await writer.CloseAsync();

        // Drain reader to complete close handshake
        var buf = new byte[1024];
        while (await reader.ReadAsync(buf, cts.Token) > 0) { }

        reader.Dispose();
        await writer.DisposeAsync();
        await Task.Delay(500);

        Assert.True(muxA.Stats.TotalChannelsClosed >= 1);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Stats_Uptime_IsPositiveAfterStart()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await Task.Delay(100);

        Assert.True(muxA.Stats.Uptime > TimeSpan.Zero);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task Stats_BytesSentAndReceived_TrackDataTransfer()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("data", cts.Token);
        var reader = await muxB.AcceptChannelAsync("data", cts.Token);

        var data = new byte[5000];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);
        await writer.CloseAsync();

        var buf = new byte[8192];
        while (await reader.ReadAsync(buf, cts.Token) > 0) { }

        Assert.True(muxA.Stats.BytesSent > 0);
        Assert.True(muxB.Stats.BytesReceived > 0);

        cts.Cancel();
    }

    #endregion

    #region Channel Events

    [Fact(Timeout = TestTimeout)]
    public async Task OnChannelOpened_EventFires_WhenChannelOpened()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        string? openedChannelId = null;
        muxA.OnChannelOpened += id => openedChannelId = id;

        await muxA.OpenChannelAsync("event_ch", cts.Token);
        await muxB.AcceptChannelAsync("event_ch", cts.Token);
        await Task.Delay(100);

        Assert.Equal("event_ch", openedChannelId);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OnChannelClosed_EventFires_WhenChannelClosed()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var closedTcs = new TaskCompletionSource<string>();
        muxA.OnChannelClosed += (id, _) => closedTcs.TrySetResult(id);

        var writer = await muxA.OpenChannelAsync("close_ch", cts.Token);
        var reader = await muxB.AcceptChannelAsync("close_ch", cts.Token);

        await writer.CloseAsync();

        // Drain reader to complete close handshake
        var buf = new byte[1024];
        while (await reader.ReadAsync(buf, cts.Token) > 0) { }
        reader.Dispose();
        await writer.DisposeAsync();

        var closedId = await closedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.Equal("close_ch", closedId);

        cts.Cancel();
    }

    #endregion

    #region WriteChannel / ReadChannel Properties

    [Fact(Timeout = TestTimeout)]
    public async Task WriteChannel_Properties_AreCorrectlySet()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync(
            new ChannelOptions { ChannelId = "props", Priority = ChannelPriority.High }, cts.Token);
        await muxB.AcceptChannelAsync("props", cts.Token);

        Assert.Equal("props", writer.ChannelId);
        Assert.Equal(ChannelState.Open, writer.State);
        Assert.Equal(ChannelPriority.High, writer.Priority);
        Assert.True(writer.AvailableCredits > 0);
        Assert.NotNull(writer.Stats);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ReadChannel_Properties_AreCorrectlySet()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.OpenChannelAsync("rprops", cts.Token);
        var reader = await muxB.AcceptChannelAsync("rprops", cts.Token);

        Assert.Equal("rprops", reader.ChannelId);
        Assert.Equal(ChannelState.Open, reader.State);
        Assert.NotNull(reader.Stats);
        Assert.True(reader.CurrentWindowSize > 0);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task WriteChannel_AfterClose_StateIsClosingOrClosed()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("closing", cts.Token);
        var reader = await muxB.AcceptChannelAsync("closing", cts.Token);

        await writer.CloseAsync();

        // Writer transitions to Closing after sending FIN
        Assert.True(writer.State == ChannelState.Closing || writer.State == ChannelState.Closed);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ReadChannel_AfterRemoteClose_StateIsClosed()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("rclose", cts.Token);
        var reader = await muxB.AcceptChannelAsync("rclose", cts.Token);

        await writer.CloseAsync();
        await Task.Delay(200);

        // Read until EOF
        var buf = new byte[1024];
        while (await reader.ReadAsync(buf, cts.Token) > 0) { }

        Assert.Equal(ChannelState.Closed, reader.State);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task WriteChannel_ChannelStats_TrackBytesAndFrames()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("stats_ch", cts.Token);
        var reader = await muxB.AcceptChannelAsync("stats_ch", cts.Token);

        var data = new byte[2048];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);

        var buf = new byte[4096];
        while (await reader.ReadAsync(buf, cts.Token) > 0)
        {
            break; // Read at least once
        }

        Assert.True(writer.Stats.BytesSent > 0);
        Assert.True(writer.Stats.FramesSent > 0);

        cts.Cancel();
    }

    #endregion

    #region WriteChannel OnClosed Event

    [Fact(Timeout = TestTimeout)]
    public async Task WriteChannel_OnClosed_FiresOnDispose()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, runA, runB) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writer = await muxA.OpenChannelAsync("onclose", cts.Token);
        var reader = await muxB.AcceptChannelAsync("onclose", cts.Token);

        var closedTcs = new TaskCompletionSource<ChannelCloseReason>();
        writer.OnClosed += (reason, _) => closedTcs.TrySetResult(reason);

        // Dispose mux to trigger channel abort, which fires OnClosed
        await muxA.DisposeAsync();

        var closeReason = await closedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ChannelCloseReason.MuxDisposed, closeReason);

        await muxB.DisposeAsync();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ReadChannel_OnClosed_Fires()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("ronclose", cts.Token);
        var reader = await muxB.AcceptChannelAsync("ronclose", cts.Token);

        var closedTcs = new TaskCompletionSource<bool>();
        reader.OnClosed += (_, _) => closedTcs.TrySetResult(true);

        await writer.CloseAsync();
        // Drain reader to process the close
        var buf = new byte[1024];
        while (await reader.ReadAsync(buf, cts.Token) > 0) { }

        var fired = await closedTcs.Task.WaitAsync(cts.Token);
        Assert.True(fired);

        cts.Cancel();
    }

    #endregion

    #region Stream API (CanRead/CanWrite/CanSeek)

    [Fact(Timeout = TestTimeout)]
    public async Task WriteChannel_StreamProperties_Correct()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        var writer = await muxA.OpenChannelAsync("stream", cts.Token);
        await muxB.AcceptChannelAsync("stream", cts.Token);

        Assert.True(writer.CanWrite);
        Assert.False(writer.CanRead);
        Assert.False(writer.CanSeek);

        cts.Cancel();
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ReadChannel_StreamProperties_Correct()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = muxA;
        await using var b = muxB;

        await muxA.OpenChannelAsync("stream", cts.Token);
        var reader = await muxB.AcceptChannelAsync("stream", cts.Token);

        Assert.True(reader.CanRead);
        Assert.False(reader.CanWrite);
        Assert.False(reader.CanSeek);

        cts.Cancel();
    }

    #endregion
}
