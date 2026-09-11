using NetConduit;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Mesh.Internal;

/// <summary>
/// Shared lifecycle for routed sub-multiplexer sessions on both the opener and
/// acceptor sides. The session owns an inner <see cref="StreamMultiplexer"/>
/// whose <c>StreamFactory</c> yields route-channel pairs. The user holds the
/// wrapping <see cref="RoutedSubMultiplexer"/>.
/// <para>
/// Lifecycle is a single atomic state machine: <c>Open → Closing → Closed</c>.
/// All cleanup runs in exactly one <see cref="CloseAsync"/> invocation, regardless
/// of whether close is triggered by:
/// <list type="bullet">
///   <item><description>the user disposing the routed sub-mux,</description></item>
///   <item><description>the inner mux raising a terminal <c>Disconnected</c>,</description></item>
///   <item><description>or the owning mesh disposing.</description></item>
/// </list>
/// Concurrent close callers await the same completion. Inline event handlers
/// fire-and-forget but register the close task with the mesh so dispose can
/// drain it deterministically.
/// </para>
/// </summary>
internal abstract class RoutedSessionBase : IAsyncDisposable
{
    private const int StateOpen = 0;
    private const int StateClosing = 1;
    private const int StateClosed = 2;

    private readonly TaskCompletionSource _closeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state = StateOpen;

    /// <summary>The owning mesh, used for stat updates and dict removal.</summary>
    protected MeshMultiplexer Mesh { get; }

    /// <summary>The inner stream multiplexer. Null until <see cref="Construct"/> runs.</summary>
    protected StreamMultiplexer? Inner { get; private set; }

    /// <summary>The user-facing wrapper. Null until <see cref="Construct"/> runs.</summary>
    protected RoutedSubMultiplexer? UserFacing { get; private set; }

    protected RoutedSessionBase(MeshMultiplexer mesh)
    {
        Mesh = mesh;
    }

    /// <summary>True once <see cref="CloseAsync"/> has been entered by any caller.</summary>
    protected bool IsClosed => Volatile.Read(ref _state) != StateOpen;

    /// <summary>The user-facing routed mux. Throws if not constructed.</summary>
    internal IStreamMultiplexer SubMultiplexer
        => UserFacing ?? throw new InvalidOperationException("Sub-mux not constructed.");

    /// <summary>The user-facing routed mux, or null if not yet constructed.</summary>
    internal IStreamMultiplexer? SubMultiplexerOrNull => UserFacing;

    /// <summary>
    /// Build the inner <see cref="StreamMultiplexer"/>, wire the terminal-disconnect
    /// handler, and create the user-facing wrapper. Called once per session.
    /// </summary>
    internal void Construct()
    {
        if (Inner is not null) return;

        var muxOptions = BuildMultiplexerOptions();
        Inner = StreamMultiplexer.Create(muxOptions);
        Inner.Disconnected += OnInnerDisconnected;
        UserFacing = new RoutedSubMultiplexer(Inner);
        Inner.Start();
        OnConstructed();
    }

    /// <summary>Build the inner <c>MultiplexerOptions</c>. Subclass-specific.</summary>
    protected abstract MultiplexerOptions BuildMultiplexerOptions();

    /// <summary>Hook fired once after the inner mux is constructed and started.</summary>
    protected virtual void OnConstructed() { }

    /// <summary>Idempotent close logic specific to the subclass, run before stat
    /// release and dict removal.</summary>
    protected virtual ValueTask OnClosingAsync() => default;

    /// <summary>Remove this session from the owning mesh's session dictionary.</summary>
    protected abstract void RemoveFromMesh();

    /// <summary>Forward <c>GoAwayAsync</c> to the inner mux. No-op if not constructed.</summary>
    internal async Task GoAwayAsync(CancellationToken ct)
    {
        if (Inner is not null)
        {
            try { await Inner.GoAwayAsync(ct).ConfigureAwait(false); } catch { }
        }
    }

    private void OnInnerDisconnected(object? sender, DisconnectedEventArgs e)
    {
        // TransportError is part of seamless reroute: the inner mux will recover
        // via StreamFactory. Only terminal reasons (LocalDispose, GoAwayReceived,
        // or TransportError once retries are exhausted and IsRunning is false)
        // trigger session close. The mesh tracks the close task so dispose can drain.
        if (e.Reason == DisconnectReason.TransportError && Inner!.IsRunning) return;
        Mesh.TrackSessionClose(CloseAsync());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <summary>
    /// Idempotent close. The first caller drives shutdown to completion; concurrent
    /// callers await the same completion task.
    /// </summary>
    internal Task CloseAsync()
    {
        if (Interlocked.CompareExchange(ref _state, StateClosing, StateOpen) != StateOpen)
        {
            return _closeCompletion.Task;
        }
        return CloseCoreAsync();
    }

    private async Task CloseCoreAsync()
    {
        try
        {
            if (Inner is not null)
            {
                Inner.Disconnected -= OnInnerDisconnected;
                try { await Inner.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            try { await OnClosingAsync().ConfigureAwait(false); } catch { }

            Mesh.OnSubMultiplexerClosed();
            RemoveFromMesh();
        }
        finally
        {
            Volatile.Write(ref _state, StateClosed);
            _closeCompletion.TrySetResult();
        }
    }
}
