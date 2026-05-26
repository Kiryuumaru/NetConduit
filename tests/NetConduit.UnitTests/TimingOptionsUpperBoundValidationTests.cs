using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for the timing fields on <see cref="MultiplexerOptions"/>
/// that sink into <see cref="Task.Delay(TimeSpan, CancellationToken)"/> or
/// <see cref="System.Threading.CancellationTokenSource(TimeSpan)"/>. Both
/// reject values greater than <see cref="int.MaxValue"/> milliseconds
/// (approximately 24.86 days) with
/// <see cref="ArgumentOutOfRangeException"/>. Without fail-fast validation at
/// the <c>StreamMultiplexer.Create</c> boundary, an oversized value passed
/// construction and faulted the first keepalive tick / drain wait / connect
/// retry — surfacing as a confusing internal exception. Mirrors the discipline
/// applied to <c>SendTimeout</c> by <c>ValidateSendTimeout</c>.
/// </summary>
public sealed class TimingOptionsUpperBoundValidationTests
{
    [Fact]
    public void Create_TooLargePingInterval_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            PingInterval = TimeSpan.FromDays(50),
        }));
        Assert.Contains("PingInterval", ex.Message);
    }

    [Fact]
    public void Create_TooLargePingTimeout_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            PingInterval = TimeSpan.FromSeconds(10),
            PingTimeout = TimeSpan.FromDays(50),
        }));
        Assert.Contains("PingTimeout", ex.Message);
    }

    [Fact]
    public void Create_TooLargeGoAwayTimeout_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            GoAwayTimeout = TimeSpan.FromDays(50),
        }));
        Assert.Contains("GoAwayTimeout", ex.Message);
    }

    [Fact]
    public void Create_TooLargeAutoReconnectDelay_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            AutoReconnectDelay = TimeSpan.FromDays(50),
            MaxAutoReconnectDelay = TimeSpan.FromDays(50),
        }));
        Assert.Contains("AutoReconnectDelay", ex.Message);
    }

    [Fact]
    public void Create_TooLargeMaxAutoReconnectDelay_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            AutoReconnectDelay = TimeSpan.FromSeconds(1),
            MaxAutoReconnectDelay = TimeSpan.FromDays(50),
        }));
        Assert.Contains("MaxAutoReconnectDelay", ex.Message);
    }

    [Fact]
    public void Create_TooLargeConnectionTimeout_ThrowsAtBoundary()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            ConnectionTimeout = TimeSpan.FromDays(50),
        }));
        Assert.Contains("ConnectionTimeout", ex.Message);
    }

    [Fact]
    public void Create_InfiniteConnectionTimeout_Accepted()
    {
        // Timeout.InfiniteTimeSpan is the documented sentinel for "no per-attempt
        // timeout". Its TotalMilliseconds is -1; the upper-bound check must
        // special-case it just like ValidateSendTimeout does.
        var ex = Record.Exception(() => StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new DuplexMemoryStream().SideA),
            ConnectionTimeout = Timeout.InfiniteTimeSpan,
        }));
        Assert.Null(ex);
    }
}
