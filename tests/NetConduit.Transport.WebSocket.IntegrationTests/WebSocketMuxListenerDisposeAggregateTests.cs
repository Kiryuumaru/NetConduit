using System.Reflection;
using System.Threading.Channels;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;
using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

/// <summary>
/// <see cref="WebSocketMuxListener.DisposeAsync"/> must continue disposing
/// remaining sessions when one mux's <c>DisposeAsync</c> throws, must clear
/// <c>_sessions</c>, and must surface the failure(s).
/// </summary>
public class WebSocketMuxListenerDisposeAggregateTests
{
    [Fact(Timeout = 15000)]
    public async Task DisposeAsync_OneMuxThrows_StillDisposesOthers_ClearsSessions_AggregatesError()
    {
        var listener = new WebSocketMuxListener();

        var (sessionsField, sessionEntryCtor, channelInstance) = GetReflectionAccess(listener);

        var throwError = new InvalidOperationException("simulated mux dispose failure");
        var throwingMux = StubMux.Create(throwError);
        var trackingMux = StubMux.Create(null);

        var sessions = (System.Collections.IDictionary)sessionsField.GetValue(listener)!;
        sessions[throwingMux.SessionId] = sessionEntryCtor.Invoke([throwingMux, channelInstance()]);
        sessions[trackingMux.SessionId] = sessionEntryCtor.Invoke([trackingMux, channelInstance()]);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(async () => await listener.DisposeAsync());

        // The throwing mux should not block the tracking mux from being disposed.
        Assert.True(throwingMux.DisposeAsyncCalls >= 1, "throwing mux was not visited");
        Assert.True(trackingMux.DisposeAsyncCalls >= 1, "tracking mux was not visited after the throwing one");

        // _sessions must be cleared even when a dispose throws.
        Assert.Empty(sessions);

        // Surface: the original exception must be observable.
        if (thrown is AggregateException agg)
        {
            Assert.Contains(agg.InnerExceptions, e => ReferenceEquals(e, throwError));
        }
        else
        {
            Assert.Same(throwError, thrown);
        }
    }

    private static (FieldInfo SessionsField, ConstructorInfo SessionEntryCtor, Func<object> ChannelFactory) GetReflectionAccess(WebSocketMuxListener listener)
    {
        var listenerType = typeof(WebSocketMuxListener);
        var sessionsField = listenerType.GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_sessions field not found");

        var sessionEntryType = listenerType.GetNestedType("SessionEntry", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SessionEntry nested type not found");

        var completionPairType = listenerType.GetNestedType("CompletionStreamPair", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("CompletionStreamPair nested type not found");

        var sessionEntryCtor = sessionEntryType.GetConstructors().Single();

        // Channel<CompletionStreamPair> instance via Channel.CreateUnbounded<T>().
        var createUnbounded = typeof(Channel).GetMethods()
            .Single(m => m.Name == nameof(Channel.CreateUnbounded) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
            .MakeGenericMethod(completionPairType);

        return (sessionsField, sessionEntryCtor, () => createUnbounded.Invoke(null, null)!);
    }

    private sealed class StubMux : IStreamMultiplexer
    {
        private readonly Exception? _toThrow;
        public int DisposeAsyncCalls;

        public static StubMux Create(Exception? toThrow) => new(toThrow);

        private StubMux(Exception? toThrow)
        {
            _toThrow = toThrow;
            SessionId = Guid.NewGuid();
            Options = new MultiplexerOptions { StreamFactory = _ => throw new NotSupportedException() };
            Stats = new MultiplexerStats();
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeAsyncCalls);
            return _toThrow is null ? ValueTask.CompletedTask : ValueTask.FromException(_toThrow);
        }

        public MultiplexerOptions Options { get; }
        public MultiplexerStats Stats { get; }
        public bool IsReady => false;
        public bool IsConnected => false;
        public bool IsRunning => false;
        public bool IsShuttingDown => false;
        public Guid SessionId { get; }
        public Guid RemoteSessionId => Guid.Empty;
        public IReadOnlyCollection<string> ActiveChannelIds => Array.Empty<string>();
        public int ActiveChannelCount => 0;
        public DisconnectReason? DisconnectReason => null;

#pragma warning disable CS0067
        public event EventHandler? Ready;
        public event EventHandler<ChannelEventArgs>? ChannelOpened;
        public event EventHandler<ChannelEventArgs>? ChannelAccepted;
        public event EventHandler<ChannelClosedEventArgs>? ChannelClosed;
        public event EventHandler<NetConduit.Events.ErrorEventArgs>? Error;
        public event EventHandler<DisconnectedEventArgs>? Disconnected;
        public event EventHandler? Connected;
        public event EventHandler<ReconnectingEventArgs>? Reconnecting;
#pragma warning restore CS0067

        public void Start() => throw new NotSupportedException();
        public Task WaitForReadyAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public IWriteChannel OpenChannel(NetConduit.Models.ChannelOptions options) => throw new NotSupportedException();
        public IReadChannel AcceptChannel(string channelId) => throw new NotSupportedException();
        public IAsyncEnumerable<IReadChannel> AcceptChannelsAsync(string? channelIdPrefix = null, CancellationToken ct = default) => throw new NotSupportedException();
        public bool TryRegisterChannels(ReadOnlySpan<ChannelRegistration> registrations, out IReadOnlyDictionary<ChannelRegistration, IChannel> channels) => throw new NotSupportedException();
        public IWriteChannel? GetWriteChannel(string channelId) => null;
        public IReadChannel? GetReadChannel(string channelId) => null;
        public ValueTask GoAwayAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask FlushAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
