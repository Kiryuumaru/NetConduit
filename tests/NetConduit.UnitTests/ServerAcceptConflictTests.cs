using System.Net.Sockets;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Issue #612: the concurrent server one-shot loser (second concurrent
/// <c>StreamFactory</c> invocation, <c>prev==1</c>) must fail fast instead of
/// burning <c>MaxAutoReconnectAttempts</c> and firing a <c>Reconnecting</c> storm.
/// Distinct from closed #406 (sequential <c>state==2</c>).
/// </summary>
public sealed class ServerAcceptConflictTests
{
    private static MultiplexerOptions OneShotLoserOptions(
        StreamFactoryDelegate factory,
        int maxAttempts = 5)
    {
        return new MultiplexerOptions
        {
            StreamFactory = factory,
            MaxAutoReconnectAttempts = maxAttempts,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaxAutoReconnectDelay = TimeSpan.FromMilliseconds(50),
            ConnectionTimeout = Timeout.InfiniteTimeSpan,
            PingInterval = TimeSpan.Zero,
        };
    }

    [Fact]
    public async Task ConcurrentLoser_TypedConflict_SurfacesImmediately_WithoutRetryBudget()
    {
        int factoryCalls = 0;
        int reconnectingCount = 0;
        var options = OneShotLoserOptions(_ =>
        {
            Interlocked.Increment(ref factoryCalls);
            throw new ServerAcceptConflictException(
                "Server-side multiplexer is already accepting a connection.");
        });
        var retry = new MuxConnectRetry(
            options,
            _ => Interlocked.Increment(ref reconnectingCount),
            _ => { });

        var ex = await Assert.ThrowsAsync<ServerAcceptConflictException>(
            () => retry.ConnectWithRetryAsync(isReconnect: false, CancellationToken.None));

        Assert.Equal("Server-side multiplexer is already accepting a connection.", ex.Message);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(0, reconnectingCount);
    }

    [Fact]
    public async Task ConcurrentLoser_UntypedAlreadyAccepting_BackstopStillFatal()
    {
        int factoryCalls = 0;
        int reconnectingCount = 0;
        var options = OneShotLoserOptions(_ =>
        {
            Interlocked.Increment(ref factoryCalls);
            throw new InvalidOperationException(
                "Server-side multiplexer is already accepting a connection.");
        });
        var retry = new MuxConnectRetry(
            options,
            _ => Interlocked.Increment(ref reconnectingCount),
            _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => retry.ConnectWithRetryAsync(isReconnect: false, CancellationToken.None));

        Assert.Equal(1, factoryCalls);
        Assert.Equal(0, reconnectingCount);
    }

    [Fact]
    public async Task SequentialLoser_ReconnectionRefusal_StillFatal()
    {
        int factoryCalls = 0;
        int reconnectingCount = 0;
        var options = OneShotLoserOptions(_ =>
        {
            Interlocked.Increment(ref factoryCalls);
            throw new InvalidOperationException(
                "Server-side multiplexer does not support reconnection. " +
                "Create a new multiplexer instance to accept another connection.");
        });
        var retry = new MuxConnectRetry(
            options,
            _ => Interlocked.Increment(ref reconnectingCount),
            _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => retry.ConnectWithRetryAsync(isReconnect: false, CancellationToken.None));

        Assert.Equal(1, factoryCalls);
        Assert.Equal(0, reconnectingCount);
    }

    [Fact]
    public async Task HonestTransient_SocketException_StillRetries()
    {
        int factoryCalls = 0;
        int reconnectingCount = 0;
        var duplex = new DuplexMemoryStream();
        var options = OneShotLoserOptions(ct =>
        {
            int current = Interlocked.Increment(ref factoryCalls);
            if (current <= 2)
                throw new SocketException((int)SocketError.ConnectionRefused);
            return Task.FromResult<IStreamPair>(duplex.SideA);
        });
        var retry = new MuxConnectRetry(
            options,
            _ => Interlocked.Increment(ref reconnectingCount),
            _ => { });

        var pair = await retry.ConnectWithRetryAsync(isReconnect: false, CancellationToken.None);

        Assert.Same(duplex.SideA, pair);
        Assert.Equal(3, factoryCalls);
        Assert.Equal(2, reconnectingCount);
    }

    [Fact]
    public void ConflictException_PreservesInvalidOperationExceptionCatchSites()
    {
        var ex = new ServerAcceptConflictException("already accepting");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }
}
