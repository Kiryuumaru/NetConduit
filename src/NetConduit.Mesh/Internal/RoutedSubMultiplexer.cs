using NetConduit;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// User-facing IStreamMultiplexer wrapper that hides transient transport drops
/// caused by mid-stream rerouting. The underlying StreamMultiplexer raises
/// Disconnected on every transport drop, including ones it will recover from
/// via StreamFactory (BFS reroute). This wrapper only forwards Disconnected
/// for terminal reasons (GoAwayReceived, LocalDispose) so seamless reroutes
/// are invisible to the application.
/// </summary>
internal sealed class RoutedSubMultiplexer : IStreamMultiplexer
{
    private readonly StreamMultiplexer _inner;

    internal RoutedSubMultiplexer(StreamMultiplexer inner)
    {
        _inner = inner;
        _inner.Ready += (s, e) => Ready?.Invoke(this, e);
        _inner.ChannelOpened += (s, e) => ChannelOpened?.Invoke(this, e);
        _inner.ChannelAccepted += (s, e) => ChannelAccepted?.Invoke(this, e);
        _inner.ChannelClosed += (s, e) => ChannelClosed?.Invoke(this, e);
        _inner.Error += (s, e) => Error?.Invoke(this, e);
        _inner.Connected += (s, e) => Connected?.Invoke(this, e);
        _inner.Reconnecting += (s, e) => Reconnecting?.Invoke(this, e);
        _inner.Disconnected += OnInnerDisconnected;
    }

    private void OnInnerDisconnected(object? sender, DisconnectedEventArgs e)
    {
        // Suppress only transient transport drops that the inner mux will
        // recover from via StreamFactory-driven reroute: after TransportError
        // the inner mux stays alive (IsRunning=true) while it reconnects.
        // Terminal failures (retry budget exhausted, GoAway, LocalDispose)
        // set IsRunning=false and must reach the app.
        if (e.Reason == NetConduit.Enums.DisconnectReason.TransportError && _inner.IsRunning) return;
        Disconnected?.Invoke(this, e);
    }

    internal StreamMultiplexer Inner => _inner;

    public MultiplexerOptions Options => _inner.Options;
    public MultiplexerStats Stats => _inner.Stats;
    public bool IsReady => _inner.IsReady;
    public bool IsConnected => _inner.IsConnected;
    public bool IsRunning => _inner.IsRunning;
    public bool IsShuttingDown => _inner.IsShuttingDown;
    public Guid SessionId => _inner.SessionId;
    public Guid RemoteSessionId => _inner.RemoteSessionId;
    public IReadOnlyCollection<string> ActiveChannelIds => _inner.ActiveChannelIds;
    public int ActiveChannelCount => _inner.ActiveChannelCount;
    public DisconnectReason? DisconnectReason => _inner.DisconnectReason;

    public event EventHandler? Ready;
    public event EventHandler<ChannelEventArgs>? ChannelOpened;
    public event EventHandler<ChannelEventArgs>? ChannelAccepted;
    public event EventHandler<ChannelClosedEventArgs>? ChannelClosed;
    public event EventHandler<NetConduit.Events.ErrorEventArgs>? Error;
    public event EventHandler<DisconnectedEventArgs>? Disconnected;
    public event EventHandler? Connected;
    public event EventHandler<ReconnectingEventArgs>? Reconnecting;

    public void Start() => _inner.Start();
    public Task WaitForReadyAsync(CancellationToken ct = default) => _inner.WaitForReadyAsync(ct);
    public IWriteChannel OpenChannel(ChannelOptions options) => _inner.OpenChannel(options);
    public IReadChannel AcceptChannel(string channelId) => _inner.AcceptChannel(channelId);
    public IAsyncEnumerable<IReadChannel> AcceptChannelsAsync(string? channelIdPrefix = null, CancellationToken ct = default)
        => _inner.AcceptChannelsAsync(channelIdPrefix, ct);
    public bool TryRegisterChannels(
        ReadOnlySpan<ChannelRegistration> registrations,
        out IReadOnlyDictionary<ChannelRegistration, IChannel> channels)
        => _inner.TryRegisterChannels(registrations, out channels);
    public IWriteChannel? GetWriteChannel(string channelId) => _inner.GetWriteChannel(channelId);
    public IReadChannel? GetReadChannel(string channelId) => _inner.GetReadChannel(channelId);
    public ValueTask GoAwayAsync(CancellationToken ct = default) => _inner.GoAwayAsync(ct);
    public ValueTask FlushAsync(CancellationToken ct = default) => _inner.FlushAsync(ct);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
