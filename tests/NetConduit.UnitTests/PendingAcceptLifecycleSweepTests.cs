using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Pending-accept channels (those returned by <c>AcceptChannel(id)</c> /
/// <c>TryRegisterChannels(Inbound)</c> before the peer's matching INIT arrives)
/// have <c>MarkConnected</c> called on them at register time when the mux is
/// already transport-connected. Their lifecycle events must follow the same
/// Connected/Disconnected alternation as fully-registered channels through
/// every transport up/down edge — not just when the peer eventually adopts
/// them by sending INIT.
/// </summary>
[Collection("Sequential")]
public sealed class PendingAcceptLifecycleSweepTests : IAsyncDisposable
{
    private readonly ReconnectableTransportFactory _factory = new();

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    private static MultiplexerOptions OptsA(ReconnectableTransportFactory f) => new()
    {
        StreamFactory = ct => f.CreateSideA(ct),
        PingInterval = TimeSpan.Zero,
        MaxAutoReconnectAttempts = 5,
        AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
    };

    private static MultiplexerOptions OptsB(ReconnectableTransportFactory f) => new()
    {
        StreamFactory = ct => f.CreateSideB(ct),
        PingInterval = TimeSpan.Zero,
        MaxAutoReconnectAttempts = 5,
        AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
    };

    [Fact]
    public async Task PendingAccept_FiresDisconnected_OnTransportDrop()
    {
        var client = StreamMultiplexer.Create(OptsA(_factory));
        var server = StreamMultiplexer.Create(OptsB(_factory));
        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var pending = server.AcceptChannel("worker/42");
        Assert.True(pending.IsConnected, "pending accept should observe transport-connected at register time");

        var disconnectedFired = new TaskCompletionSource<DisconnectReason>();
        pending.Disconnected += (_, args) => disconnectedFired.TrySetResult(args.Reason);

        _factory.KillCurrentTransport();

        var reason = await disconnectedFired.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.Equal(DisconnectReason.TransportError, reason);
        Assert.False(pending.IsConnected, "pending accept must observe transport-down through IsConnected");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task PendingAccept_ReFiresConnected_OnReconnect()
    {
        var client = StreamMultiplexer.Create(OptsA(_factory));
        var server = StreamMultiplexer.Create(OptsB(_factory));
        client.Start();
        server.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var pending = server.AcceptChannel("worker/42");
        Assert.True(pending.IsConnected);

        int connectedCount = 0;
        var secondConnect = new TaskCompletionSource();
        pending.Connected += (_, _) =>
        {
            if (Interlocked.Increment(ref connectedCount) >= 1)
                secondConnect.TrySetResult();
        };

        // Wait for the server mux to reconnect.
        var serverReconnected = new TaskCompletionSource();
        int serverConnectedCount = 0;
        server.Connected += (_, _) =>
        {
            if (Interlocked.Increment(ref serverConnectedCount) >= 1)
                serverReconnected.TrySetResult();
        };

        _factory.KillCurrentTransport();
        await serverReconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        await secondConnect.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.True(pending.IsConnected, "pending accept must re-observe transport-up after reconnect");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
