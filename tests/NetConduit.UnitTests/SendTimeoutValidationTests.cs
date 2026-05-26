using NetConduit.Models;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for #387: <see cref="ChannelOptions.SendTimeout"/> (and
/// <see cref="DefaultChannelOptions.SendTimeout"/>) was not validated at the
/// <c>OpenChannel</c> / <c>StreamMultiplexer.Create</c> / <c>TryRegisterChannels</c>
/// boundary. Misconfigured values (negative non-infinite, or greater than
/// <see cref="int.MaxValue"/> milliseconds) surfaced as a confusing
/// <see cref="ArgumentOutOfRangeException"/> from <see cref="SemaphoreSlim.WaitAsync(TimeSpan, CancellationToken)"/>
/// deep inside <c>WriteChannel.WriteAsync</c> only after the channel was
/// already open and parking on backpressure. The fail-fast pattern used by
/// every other timing/sizing field (<c>ValidateSlabSize</c>,
/// <c>ValidateTimingOptions</c>) must apply to <c>SendTimeout</c> too.
/// </summary>
public sealed class SendTimeoutValidationTests
{
    [Fact]
    public void OpenChannel_NegativeSendTimeout_ThrowsAtBoundary()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<NetConduit.Interfaces.IStreamPair>(new DuplexMemoryStream().SideA),
        });

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            mux.OpenChannel(new ChannelOptions
            {
                ChannelId = "neg",
                SendTimeout = TimeSpan.FromSeconds(-5),
            }));

        Assert.Contains("SendTimeout", ex.Message);
    }

    [Fact]
    public void OpenChannel_TooLargeSendTimeout_ThrowsAtBoundary()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<NetConduit.Interfaces.IStreamPair>(new DuplexMemoryStream().SideA),
        });

        // > int.MaxValue ms is rejected by SemaphoreSlim.WaitAsync.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            mux.OpenChannel(new ChannelOptions
            {
                ChannelId = "big",
                SendTimeout = TimeSpan.FromDays(30),
            }));

        Assert.Contains("SendTimeout", ex.Message);
    }

    [Fact]
    public void OpenChannel_InfiniteSendTimeout_Accepted()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<NetConduit.Interfaces.IStreamPair>(new DuplexMemoryStream().SideA),
        });

        // Timeout.InfiniteTimeSpan is the documented sentinel for "no timeout";
        // it is the only negative TimeSpan that must be accepted.
        var ex = Record.Exception(() =>
        {
            // Channel registration validates SendTimeout before any I/O so the
            // mux does not need to be started for this assertion.
            mux.OpenChannel(new ChannelOptions
            {
                ChannelId = "inf",
                SendTimeout = Timeout.InfiniteTimeSpan,
            });
        });

        // The only acceptable exception here is "not started" from the
        // post-validation gate, not ArgumentOutOfRangeException.
        if (ex is not null)
            Assert.IsNotType<ArgumentOutOfRangeException>(ex);
    }

    [Fact]
    public void Create_NegativeDefaultSendTimeout_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<NetConduit.Interfaces.IStreamPair>(new DuplexMemoryStream().SideA),
                DefaultChannelOptions = new DefaultChannelOptions
                {
                    SendTimeout = TimeSpan.FromSeconds(-1),
                },
            }));

        Assert.Contains("SendTimeout", ex.Message);
    }

    [Fact]
    public void Create_TooLargeDefaultSendTimeout_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            StreamMultiplexer.Create(new MultiplexerOptions
            {
                StreamFactory = _ => Task.FromResult<NetConduit.Interfaces.IStreamPair>(new DuplexMemoryStream().SideA),
                DefaultChannelOptions = new DefaultChannelOptions
                {
                    SendTimeout = TimeSpan.FromDays(30),
                },
            }));

        Assert.Contains("SendTimeout", ex.Message);
    }
}
