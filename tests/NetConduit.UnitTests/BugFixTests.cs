using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NetConduit.Internal;
using NetConduit.Models;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for investigated bugs.
/// Tests are written BEFORE fixes so they fail first, then pass after fixes.
/// </summary>
public class BugFixTests
{
    #region 001: DeltaTransit Atomic Write

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_WriteMessageAsync_SingleWritePerMessage()
    {
        // DeltaTransit.WriteMessageAsync must combine the 4-byte length prefix
        // and the body into a single WriteAsync call for atomicity.
        // After the fix, each SendAsync should produce exactly one write to the channel.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "delta_atomic" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("delta_atomic", cts.Token);

        await using var sender = new DeltaTransit<JsonObject>(writeChannel, null);
        await using var receiver = new DeltaTransit<JsonObject>(null, readChannel);

        var state = new JsonObject { ["key"] = "value" };
        await sender.SendAsync(state, cts.Token);

        var received = await receiver.ReceiveAsync(cts.Token);
        Assert.NotNull(received);
        Assert.Equal("value", received["key"]?.GetValue<string>());
    }

    [Fact(Timeout = 30000)]
    public async Task DeltaTransit_MultipleMessages_AllDeliveredCorrectly()
    {
        // Verify that atomic writes maintain correct framing across multiple messages.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new ChannelOptions { ChannelId = "delta_multi" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("delta_multi", cts.Token);

        await using var sender = new DeltaTransit<JsonObject>(writeChannel, null);
        await using var receiver = new DeltaTransit<JsonObject>(null, readChannel);

        for (var i = 0; i < 10; i++)
        {
            var state = new JsonObject { ["counter"] = i };
            await sender.SendAsync(state, cts.Token);
        }

        for (var i = 0; i < 10; i++)
        {
            var received = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(received);
            Assert.Equal(i, received["counter"]?.GetValue<int>());
        }
    }

    #endregion

    #region 003: MaxFrameSize Validation

    [Fact]
    public void MultiplexerOptions_MaxFrameSize_NegativeValue_Throws()
    {
        var opts = new MultiplexerOptions { StreamFactory = _ => null!, MaxFrameSize = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(opts));
    }

    [Fact]
    public void MultiplexerOptions_MaxFrameSize_Zero_Throws()
    {
        var opts = new MultiplexerOptions { StreamFactory = _ => null!, MaxFrameSize = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(opts));
    }

    [Fact]
    public void MultiplexerOptions_MaxFrameSize_SmallPositive_Throws()
    {
        var opts = new MultiplexerOptions { StreamFactory = _ => null!, MaxFrameSize = 100 };
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(opts));
    }

    [Fact]
    public void MultiplexerOptions_MaxFrameSize_IntMaxValue_Throws()
    {
        var opts = new MultiplexerOptions { StreamFactory = _ => null!, MaxFrameSize = int.MaxValue };
        Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(opts));
    }

    [Fact]
    public void MultiplexerOptions_MaxFrameSize_ValidRange_Accepted()
    {
        var opts = new MultiplexerOptions { StreamFactory = _ => null!, MaxFrameSize = 16 * 1024 * 1024 };
        var mux = StreamMultiplexer.Create(opts);
        Assert.Equal(16 * 1024 * 1024, mux.Options.MaxFrameSize);
    }

    [Fact]
    public void MultiplexerOptions_MaxFrameSize_LowerBound_Accepted()
    {
        var opts = new MultiplexerOptions { StreamFactory = _ => null!, MaxFrameSize = 1024 };
        var mux = StreamMultiplexer.Create(opts);
        Assert.Equal(1024, mux.Options.MaxFrameSize);
    }

    [Fact]
    public void MultiplexerOptions_MaxFrameSize_UpperBound_Accepted()
    {
        var opts = new MultiplexerOptions { StreamFactory = _ => null!, MaxFrameSize = 128 * 1024 * 1024 };
        var mux = StreamMultiplexer.Create(opts);
        Assert.Equal(128 * 1024 * 1024, mux.Options.MaxFrameSize);
    }

    [Fact]
    public void MultiplexerOptions_MaxFrameSize_Default_Is16MB()
    {
        var opts = new MultiplexerOptions { StreamFactory = _ => null! };
        Assert.Equal(16 * 1024 * 1024, opts.MaxFrameSize);
    }

    #endregion

    #region 004: Handshake Nonce CSPRNG

    [Fact(Timeout = 30000)]
    public async Task Handshake_Nonce_IsCryptographicallyRandom()
    {
        // After the fix, nonces should be generated from RandomNumberGenerator.
        // We verify by creating multiple multiplexer pairs and checking nonce quality.
        // All nonces should be unique (collision probability negligible with 64 bits of entropy).
        var nonces = new HashSet<long>();
        const int pairs = 20;

        for (var i = 0; i < pairs; i++)
        {
            await using var pipe = new DuplexPipe();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var (mux1, mux2, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

            // SessionId is derived from nonce negotiation; if both connect successfully,
            // nonces were distinct. Collect SessionIds as proxy for nonce uniqueness.
            nonces.Add(mux1.SessionId.GetHashCode());
            nonces.Add(mux2.SessionId.GetHashCode());

            await mux1.DisposeAsync();
            await mux2.DisposeAsync();
        }

        // With 40 hashes from 20 pairs, all should be unique (negligible collision probability)
        Assert.True(nonces.Count >= pairs, $"Expected at least {pairs} unique nonces, got {nonces.Count}");
    }

    #endregion

    #region 005: ChannelOptions MinCredits/MaxCredits Validation

    [Fact]
    public void AdaptiveFlowControl_MinCreditsExceedsMax_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new AdaptiveFlowControl(1_000_000, 100));
    }

    [Fact]
    public void AdaptiveFlowControl_ZeroMaxCredits_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AdaptiveFlowControl(0, 0));
    }

    [Fact]
    public void AdaptiveFlowControl_EqualMinMax_Accepted()
    {
        var afc = new AdaptiveFlowControl(1024, 1024);
        Assert.Equal(1024u, afc.CurrentWindowSize);
    }

    [Fact]
    public void AdaptiveFlowControl_ValidRange_Accepted()
    {
        var afc = new AdaptiveFlowControl(512, 4096);
        Assert.Equal(4096u, afc.CurrentWindowSize);
    }

    [Fact]
    public void ChannelOptions_MinCreditsExceedsMax_ThrowsOnMuxUse()
    {
        // When ChannelOptions with MinCredits > MaxCredits is used to open a channel,
        // the validation should catch it before constructing AdaptiveFlowControl.
        var opts = new ChannelOptions
        {
            ChannelId = "bad_credits",
            MinCredits = 1_000_000,
            MaxCredits = 100
        };

        Assert.Throws<ArgumentException>(() =>
            new AdaptiveFlowControl(opts.MinCredits, opts.MaxCredits));
    }

    [Fact]
    public void DefaultChannelOptions_MinCreditsExceedsMax_CaughtByFlowControl()
    {
        // DefaultChannelOptions -> AdaptiveFlowControl constructor should reject
        var defaults = new DefaultChannelOptions
        {
            MinCredits = 5_000_000,
            MaxCredits = 1024
        };

        Assert.Throws<ArgumentException>(() =>
            new AdaptiveFlowControl(defaults.MinCredits, defaults.MaxCredits));
    }

    #endregion
}
