using System.Buffers.Binary;
using System.Text;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Exceptions;
using NetConduit.Interfaces;
using NetConduit.Internal;
using NetConduit.Models;

namespace NetConduit;

/// <summary>
/// The multiplexer. Owns the connection lifecycle (initial connect, retry/backoff,
/// reconnect with replay), the wire-level handshake and reconnect handshake, the
/// writer/flusher/reader/keepalive loops, control-frame processing (GoAway, ping/pong),
/// channel-id validation, GoAway drain orchestration, and inbound-channel accept
/// dispatch. Per-channel send/receive slabs, frame construction, flow control,
/// and replay state live on the channels themselves (<see cref="IWriteChannel"/>,
/// <see cref="IReadChannel"/>); this class routes frames between the transport
/// and those channels and arbitrates session-level state.
/// </summary>
public sealed class StreamMultiplexer : IStreamMultiplexer, IChannelOwner
{
    private readonly MultiplexerOptions _options;
    private readonly ChannelRegistry _registry;
    private readonly MultiplexerStats _stats = new();
    private readonly CoalescingSignal _readySignal = new();
    private readonly CoalescingSignal _flushSignal = new();
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _readyLock = new();
    private readonly List<WriteChannel> _readyChannels = [];
    private readonly MuxConnection _conn = new();
    private readonly MuxConnectRetry _connectRetry;
    private readonly ChannelBatchRegistrar _channelRegistrar;

    private volatile bool _isRunning;
    private volatile bool _isConnected;
    private volatile bool _isReady;
    private volatile bool _isShuttingDown;
    private volatile bool _disconnectedFired;
    private volatile bool _disposed;
    private DisconnectReason? _disconnectReason;

    internal static byte[] EncodeValidatedChannelId(string channelId, string paramName)
    {
        ArgumentNullException.ThrowIfNull(channelId, paramName);

        if (channelId.Length == 0)
        {
            throw new ArgumentException("Channel ID must not be empty.", paramName);
        }

        int byteCount = Encoding.UTF8.GetByteCount(channelId);
        if (byteCount > ChannelConstants.MaxChannelIdLength)
        {
            throw new ArgumentException($"Channel ID must be at most {ChannelConstants.MaxChannelIdLength} UTF-8 bytes.", paramName);
        }

        return Encoding.UTF8.GetBytes(channelId);
    }

    /// <inheritdoc />
    public MultiplexerOptions Options => _options;

    /// <inheritdoc />
    public MultiplexerStats Stats => _stats;

    /// <inheritdoc />
    public bool IsReady => _isReady;

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public bool IsRunning => _isRunning;

    /// <inheritdoc />
    public bool IsShuttingDown => _isShuttingDown;

    /// <inheritdoc />
    public Guid SessionId => _conn.SessionId;

    /// <inheritdoc />
    public Guid RemoteSessionId => _conn.RemoteSessionId;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ActiveChannelIds =>
        _registry.GetAllWriteChannels().Select(c => c.ChannelId)
            .Concat(_registry.GetAllReadChannels().Select(c => c.ChannelId))
            .Distinct()
            .ToArray();

    /// <inheritdoc />
    public int ActiveChannelCount =>
        _registry.GetAllWriteChannels().Count + _registry.GetAllReadChannels().Count;

    /// <inheritdoc />
    public DisconnectReason? DisconnectReason => _disconnectReason;

    /// <inheritdoc />
    public event EventHandler? Ready;
    /// <inheritdoc />
    public event EventHandler<ChannelEventArgs>? ChannelOpened;
    /// <inheritdoc />
    public event EventHandler<ChannelEventArgs>? ChannelAccepted;
    /// <inheritdoc />
    public event EventHandler<ChannelClosedEventArgs>? ChannelClosed;
    /// <inheritdoc />
    public event EventHandler<Events.ErrorEventArgs>? Error;
    /// <inheritdoc />
    public event EventHandler<DisconnectedEventArgs>? Disconnected;
    /// <inheritdoc />
    public event EventHandler? Connected;
    /// <inheritdoc />
    public event EventHandler<ReconnectingEventArgs>? Reconnecting;

    private StreamMultiplexer(MultiplexerOptions options, bool useOddIndices)
    {
        _options = options;
        _conn.SessionId = options.SessionId ?? Guid.NewGuid();
        _registry = new ChannelRegistry(useOddIndices);
        _connectRetry = new MuxConnectRetry(
            options,
            args => RaiseEvent(Reconnecting, args),
            RaiseError);
        _channelRegistrar = new ChannelBatchRegistrar(_registry, _options, _stats, this);
    }

    /// <summary>
    /// Create a new multiplexer with the given options.
    /// </summary>
    public static StreamMultiplexer Create(MultiplexerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxAutoReconnectAttempts < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxAutoReconnectAttempts must be -1 (unlimited), 0 (no reconnect), or a positive bound.");
        }
        ValidateSlabSize(options.DefaultChannelOptions.SlabSize, $"{nameof(options)}.{nameof(MultiplexerOptions.DefaultChannelOptions)}.{nameof(DefaultChannelOptions.SlabSize)}");
        ValidateTimingOptions(options);
        return new StreamMultiplexer(options, useOddIndices: true);
    }

    internal static void ValidateSlabSize(int slabSize, string paramName)
    {
        if (slabSize < FrameConstants.MinSlabSize || slabSize > FrameConstants.MaxSlabSize)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                slabSize,
                $"SlabSize must be between {FrameConstants.MinSlabSize} ({FrameConstants.MinSlabSize / 1024} KiB) and {FrameConstants.MaxSlabSize} ({FrameConstants.MaxSlabSize / (1024 * 1024)} MiB) inclusive.");
        }
    }

    private static void ValidateTimingOptions(MultiplexerOptions options)
    {
        if (options.PingInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PingInterval,
                "PingInterval must be non-negative. Use TimeSpan.Zero to disable keepalive.");

        if (options.PingInterval > TimeSpan.Zero)
        {
            if (options.PingTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.PingTimeout,
                    "PingTimeout must be positive when keepalive is enabled (PingInterval > TimeSpan.Zero).");

            if (options.MaxMissedPings < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.MaxMissedPings,
                    "MaxMissedPings must be at least 1 when keepalive is enabled (PingInterval > TimeSpan.Zero).");
        }

        if (options.GoAwayTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.GoAwayTimeout,
                "GoAwayTimeout must be non-negative.");

        if (options.AutoReconnectDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.AutoReconnectDelay,
                "AutoReconnectDelay must be non-negative.");

        if (options.MaxAutoReconnectDelay < options.AutoReconnectDelay)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxAutoReconnectDelay,
                $"MaxAutoReconnectDelay ({options.MaxAutoReconnectDelay}) must be greater than or equal to AutoReconnectDelay ({options.AutoReconnectDelay}).");

        if (double.IsNaN(options.AutoReconnectBackoffMultiplier) || options.AutoReconnectBackoffMultiplier < 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.AutoReconnectBackoffMultiplier,
                "AutoReconnectBackoffMultiplier must be greater than or equal to 1.0.");

        if (options.ConnectionTimeout != Timeout.InfiniteTimeSpan && options.ConnectionTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ConnectionTimeout,
                "ConnectionTimeout must be non-negative, or Timeout.InfiniteTimeSpan to disable per-attempt timeout.");
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_isRunning)
            throw new InvalidOperationException("Multiplexer is already running.");

        _isRunning = true;
        _stats._startTicks = Environment.TickCount64;
        _conn.MainLoopTask = Task.Run(() => MainLoopAsync(_cts.Token));
    }

    /// <inheritdoc />
    public Task WaitForReadyAsync(CancellationToken ct = default) => _readyTcs.Task.WaitAsync(ct);

    /// <inheritdoc />
    public IWriteChannel OpenChannel(ChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        byte[] channelIdBytes = EncodeValidatedChannelId(options.ChannelId, nameof(options));
        ValidateSlabSize(options.SlabSize, $"{nameof(options)}.{nameof(ChannelOptions.SlabSize)}");

        if (!_isRunning)
            throw new InvalidOperationException("Multiplexer has not been started.");

        if (_isShuttingDown)
            throw new InvalidOperationException("Cannot open new channels after GoAwayAsync.");

        bool enableReplay = _options.MaxAutoReconnectAttempts != 0;
        WriteChannel channel;
        // Lock against ReassignPreHandshakeWriteChannelIndices: allocate +
        // register + stamp the INIT frame as one atomic unit so a concurrent
        // post-handshake reassign either sees this channel and rekeys it, or
        // we observe the already-flipped parity and allocate correctly.
        lock (_registry.ChannelIndexLock)
        {
            ushort index = _registry.AllocateChannelIndex();
            channel = new WriteChannel(
                options.ChannelId,
                index,
                options.Priority,
                options.SlabSize,
                options.SendTimeout,
                this,
                enableReplay);

            _registry.RegisterWriteChannel(index, channel);

            // Send INIT frame (channel does it itself — builds the frame in its slab)
            channel.WriteInitFrame(channelIdBytes);
            // Channel stays in Opening/Pending state until remote ACKs the INIT
        }

        if (_isConnected)
            channel.MarkConnected();

        Interlocked.Increment(ref _stats._openChannels);
        Interlocked.Increment(ref _stats._totalChannelsOpened);

        return channel;
    }

    /// <inheritdoc />
    public IReadChannel AcceptChannel(string channelId)
    {
        _ = EncodeValidatedChannelId(channelId, nameof(channelId));

        if (!_isRunning)
            throw new InvalidOperationException("Multiplexer has not been started.");

        if (_isShuttingDown)
            throw new InvalidOperationException("Cannot accept new channels after GoAwayAsync.");

        // Atomically observe registry state and either return an existing channel
        // or commit a new pending channel that the reader will adopt when INIT arrives.
        lock (_registry.AcceptLock)
        {
            // Check if channel already arrived from remote
            var existing = _registry.GetReadChannelById(channelId);
            if (existing is not null) return existing;

            // Check if a pending accept already exists for this ID
            var pendingExisting = _registry.GetPendingAcceptChannel(channelId);
            if (pendingExisting is not null) return pendingExisting;

            // Create a pending ReadChannel that will be wired up when remote INIT arrives
            var channel = new ReadChannel(
                channelId,
                0, // index assigned later when remote INIT arrives
                _options.DefaultChannelOptions.Priority,
                _options.DefaultChannelOptions.SlabSize,
                this);

            if (!_registry.TryRegisterPendingAcceptChannel(channelId, channel))
                throw new MultiplexerException(ErrorCode.ChannelExists, $"A channel with ID '{channelId}' is already being accepted.");

            if (_isConnected)
                channel.MarkConnected();

            return channel;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<IReadChannel> AcceptChannelsAsync(string? channelIdPrefix = null, CancellationToken ct = default)
    {
        return _registry.AcceptChannelsAsync(channelIdPrefix, ct);
    }

    /// <inheritdoc />
    public bool TryRegisterChannels(
        ReadOnlySpan<ChannelRegistration> registrations,
        out IReadOnlyDictionary<ChannelRegistration, IChannel> channels)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Multiplexer has not been started.");
        if (_isShuttingDown)
            throw new InvalidOperationException("Cannot register new channels after GoAwayAsync.");
        if (registrations.IsEmpty)
            throw new ArgumentException("At least one registration is required.", nameof(registrations));

        return _channelRegistrar.TryRegisterChannels(registrations, _isConnected, out channels);
    }

    /// <inheritdoc />
    public IWriteChannel? GetWriteChannel(string channelId) => _registry.GetWriteChannelById(channelId);

    /// <inheritdoc />
    public IReadChannel? GetReadChannel(string channelId) => _registry.GetReadChannelById(channelId);

    /// <inheritdoc />
    public async ValueTask GoAwayAsync(CancellationToken ct = default)
    {
        if (_isShuttingDown) return;

        // Try to stage the GoAway frame BEFORE latching _isShuttingDown so a
        // transient slab-full does not leave the mux in half-shutdown limbo
        // with no GoAway on the wire. Short retry budget; if the slab
        // is still full after, proceed anyway - peer observes transport close
        // and reports TransportError instead of GoAwayReceived, but the local
        // mux still tears down cleanly.
        byte[] goAwayPayload = [CtrlSubtype.GoAway];
        const int MaxGoAwayAttempts = 5;
        for (int attempt = 0; attempt < MaxGoAwayAttempts; attempt++)
        {
            if (SendControlFrame(FrameFlags.Ctrl, goAwayPayload)) break;
            await Task.Yield();
        }

        _isShuttingDown = true;

        // Wait up to GoAwayTimeout for already-open channels to drain. The caller token cancels the wait;
        // GoAwayTimeout bounds it. Either path falls through to forced abort below.
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(_options.GoAwayTimeout);
        try
        {
            while (ActiveChannelCount > 0)
            {
                await Task.Delay(20, drainCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* timeout or caller cancel — proceed to terminal cleanup */ }

        // Abort any channels that did not drain within the timeout so they observe MuxDisposed instead of Open.
        _registry.AbortAllChannels(ChannelCloseReason.MuxDisposed);
        _registry.CancelAllPendingAccepts();

        _disconnectReason = Enums.DisconnectReason.LocalDispose;
        _cts.Cancel();
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct = default)
    {
        _flushSignal.Signal();
        return ValueTask.CompletedTask;
    }

    // =====================================================================
    // Main Lifecycle Loop — single loop for initial connect AND reconnect.
    // Connect → handshake → run loops → transport dies → loop back.
    // =====================================================================
    private async Task MainLoopAsync(CancellationToken ct)
    {
        bool hasConnectedBefore = false;
        int handshakeAttempt = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Connect with retry (no delay on first attempt)
                var transport = await _connectRetry.ConnectWithRetryAsync(hasConnectedBefore, ct);

                // Handshake: reconnect if we've connected before, initial otherwise.
                // Only transport I/O failures raised by the handshake path are retryable.
                // Protocol errors, session mismatches, and timeouts fail fast because they
                // indicate a configuration or peer fault, not route churn.
                try
                {
                    if (hasConnectedBefore)
                    {
                        await PerformReconnectHandshakeAsync(transport, ct);

                        foreach (var ch in _registry.GetAllWriteChannels())
                            ch.PrepareReplay();
                    }
                    else
                    {
                        _conn.Transport = transport;
                        await PerformHandshakeAsync(ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HandshakeTransportException handshakeEx)
                {
                    handshakeAttempt++;
                    _conn.Transport = null;
                    try { await transport.DisposeAsync(); } catch { }

                    RaiseError(handshakeEx);

                    if (!_connectRetry.HasHandshakeRetryBudget(handshakeAttempt))
                        throw;

                    try
                    {
                        await Task.Delay(_options.AutoReconnectDelay, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }

                    continue;
                }
                catch
                {
                    // Non-transport handshake failures (MultiplexerException for
                    // SessionMismatch / ProtocolError / Internal, applyRemotePositions
                    // throws, etc.) propagate to terminate the mux. The freshly
                    // connected transport is still owned by this loop and must be
                    // disposed before leaving the scope. On the reconnect path
                    // _conn.Transport was never assigned, so the outer DisposeAsync
                    // safety net cannot reach it.
                    _conn.Transport = null;
                    try { await transport.DisposeAsync(); } catch { }
                    throw;
                }

                handshakeAttempt = 0;

                _conn.Transport = transport;
                _isConnected = true;
                _disconnectReason = null;

                // Reassign any pre-handshake-allocated WriteChannel that landed
                // on the wrong default-odd parity space so its queued INIT and
                // any DATA frames go out under the parity decided by the
                // session-GUID handshake. Done here on the first connect
                // only; reconnects re-use the same parity (decided by the same
                // session GUIDs) and AllocateChannelIndex below allocates from
                // the correct space already.
                if (!hasConnectedBefore)
                    ReassignPreHandshakeWriteChannelIndices();

                // Create control channel on first connect
                if (_conn.ControlChannel is null)
                {
                    _conn.ControlChannel = new WriteChannel(
                        "__control__",
                        ChannelConstants.ControlChannel,
                        ChannelPriority.Highest,
                        FrameConstants.MinSlabSize,
                        TimeSpan.FromSeconds(5),
                        this);
                    _conn.ControlChannel.MarkOpen();
                }

                // Start loops with a per-session CTS
                _conn.LoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var loopCt = _conn.LoopCts.Token;
                var conn = _conn;

                var transportWriter = new MuxTransportWriter(
                    conn, _readySignal, _flushSignal, _readyChannels, _readyLock, _stats);

                _conn.WriterTask = Task.Factory.StartNew(
                    () => transportWriter.RunWriterLoop(loopCt),
                    loopCt,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                _conn.FlusherTask = Task.Factory.StartNew(
                    () => transportWriter.RunFlusherLoop(loopCt),
                    loopCt,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                _conn.ReaderTask = Task.Run(() => RunReaderLoopAsync(conn, loopCt), loopCt);

                if (_options.PingInterval > TimeSpan.Zero)
                {
                    var keepalive = new MuxKeepalive(
                        conn,
                        _options.PingInterval,
                        _options.PingTimeout,
                        _options.MaxMissedPings);
                    _conn.KeepaliveTask = Task.Run(() => keepalive.RunAsync(loopCt), loopCt);
                }

                RaiseEvent(Connected);

                if (!hasConnectedBefore)
                {
                    _isReady = true;
                    // Raise Ready synchronously first so handlers observe a ready multiplexer,
                    // then complete the TCS so async awaiters resume only after handlers ran.
                    RaiseEvent(Ready);
                    _readyTcs.TrySetResult();
                }
                hasConnectedBefore = true;

                // Notify all channels that transport is connected
                foreach (var ch in _registry.GetAllWriteChannels())
                    ch.MarkConnected();
                foreach (var ch in _registry.GetAllReadChannels())
                    ch.MarkConnected();

                // Block until any loop faults (transport died)
                var faulted = await Task.WhenAny(
                    _conn.WriterTask!,
                    _conn.ReaderTask!,
                    _conn.FlusherTask!,
                    _conn.KeepaliveTask ?? Task.Delay(Timeout.Infinite, ct));

                // Transport is dead — cancel all loops and clean up
                _isConnected = false;
                _conn.LoopCts.Cancel();

                // Notify all channels that transport is disconnected
                foreach (var ch in _registry.GetAllWriteChannels())
                    ch.MarkDisconnected(Enums.DisconnectReason.TransportError, null);
                foreach (var ch in _registry.GetAllReadChannels())
                    ch.MarkDisconnected(Enums.DisconnectReason.TransportError, null);

                await WaitForLoopsAsync();
                _conn.LoopCts.Dispose();
                _conn.LoopCts = null;

                // Capture the exception if any
                Exception? transportEx = faulted.Exception?.InnerException;
                if (transportEx is not null)
                    RaiseError(transportEx);

                // Dispose old transport
                await transport.DisposeAsync();
                _conn.Transport = null;

                if (_isShuttingDown)
                {
                    if (_disconnectReason == Enums.DisconnectReason.GoAwayReceived)
                    {
                        // Remote-initiated GoAway: the local GoAwayAsync path drains
                        // then aborts channels, but the remote path skips both. Without
                        // this, local channels remain in Open state forever after the
                        // peer disappears — ReadAsync hangs and WriteAsync stalls until
                        // SendTimeout. Mirror GoAwayAsync's terminal abort so awaiting
                        // reads see EOF and writers see ChannelClosedException promptly.
                        _registry.AbortAllChannels(ChannelCloseReason.MuxDisposed);
                        _registry.CancelAllPendingAccepts();
                        _disconnectedFired = true;
                        RaiseEvent(Disconnected, new DisconnectedEventArgs(Enums.DisconnectReason.GoAwayReceived, null));
                    }
                    break;
                }

                // If outer CTS is cancelled (Dispose was called), don't fire TransportError
                if (ct.IsCancellationRequested)
                    break;

                _disconnectedFired = true;
                RaiseEvent(Disconnected, new DisconnectedEventArgs(Enums.DisconnectReason.TransportError, transportEx));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown via Dispose/GoAway. Complete _readyTcs so any
            // WaitForReadyAsync caller without a CancellationToken observes a
            // deterministic TaskCanceledException instead of hanging forever
            // (fixes #395, #401). TrySetCanceled is a no-op once _readyTcs has
            // already been completed via TrySetResult on a prior successful
            // handshake, so reconnect-after-Ready dispose semantics are
            // unchanged.
            _readyTcs.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            // Fatal error (protocol error, exhausted retries) — notify waiters
            if (hasConnectedBefore)
                AbortChannelsForTerminalTransportFailure(ex);

            _readyTcs.TrySetException(ex);
        }
    }

    private void AbortChannelsForTerminalTransportFailure(Exception exception)
    {
        _registry.AbortAllChannels(ChannelCloseReason.TransportFailed, exception);
        _registry.CancelAllPendingAccepts();
    }

    private async Task WaitForLoopsAsync()
    {
        Task[] loops = [_conn.WriterTask!, _conn.ReaderTask!, _conn.FlusherTask!];
        if (_conn.KeepaliveTask is not null)
            loops = [.. loops, _conn.KeepaliveTask];

        foreach (var loop in loops)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
            catch { /* loop faults are already handled */ }
        }
    }

    // IChannelOwner — channels call back into the multiplexer for routing and lifecycle
    void IChannelOwner.NotifyReady(WriteChannel channel)
    {
        lock (_readyLock)
        {
            if (!_readyChannels.Contains(channel))
                _readyChannels.Add(channel);
        }
        _readySignal.Signal();
    }

    void IChannelOwner.NotifyChannelOpened(string channelId)
    {
        // Only raise the public event for user-registered write channels —
        // the internal control channel is not part of the registry.
        if (_registry.GetWriteChannelById(channelId) is null) return;
        RaiseEvent(ChannelOpened, new ChannelEventArgs(channelId));
    }

    void IChannelOwner.NotifyChannelCompleted(ushort channelIndex, string channelId)
    {
        // Capture role BEFORE unregistering, since GetWriteChannel/GetReadChannel
        // return null after the registry mutation.
        var writeChannel = _registry.GetWriteChannel(channelIndex);
        var readChannel = writeChannel is null ? _registry.GetReadChannel(channelIndex) : null;

        _registry.UnregisterChannel(channelIndex, channelId);

        if (writeChannel is not null)
        {
            // Write channels are always closed locally (FIN-out then drain),
            // so this is the single accounting site. No CAS needed.
            Interlocked.Decrement(ref _stats._openChannels);
            Interlocked.Increment(ref _stats._totalChannelsClosed);
        }
        else if (readChannel is not null && readChannel.TryClaimCompletionAccounting())
        {
            // Read channels can be closed by inbound FIN, by local Dispose,
            // or by mux-level abort — whichever runs first claims the accounting.
            // Pre-fix this branch only fired on FIN, so a local-Dispose-before-FIN
            // permanently inflated OpenChannels and skipped ChannelClosed.
            Interlocked.Decrement(ref _stats._openChannels);
            Interlocked.Increment(ref _stats._totalChannelsClosed);
            RaiseEvent(ChannelClosed, new ChannelClosedEventArgs(channelId, readChannel.CloseException));
        }
    }

    void IChannelOwner.NotifyPendingAcceptCancelled(string channelId)
    {
        // Disposing a pending-accept channel cancels the accept. Removing the
        // entry from the pending-accept map prevents DispatchToChannel from
        // resurrecting the disposed instance when the peer's INIT eventually
        // arrives. Idempotent — TryRemove is a no-op if already gone.
        _registry.RemovePendingAcceptChannel(channelId);
    }

    void IChannelOwner.NotifyEventHandlerException(Exception exception)
    {
        // Forward channel-level event handler failures to the mux's Error
        // event surface so they are observable without crashing the producer
        // thread that raised the channel event.
        RaiseError(exception);
    }

    int IChannelOwner.PeerMaxRecvPayload => _conn.PeerMaxRecvPayload;

    // =====================================================================
    // Reader Thread — THE DISPATCHER (receive side)
    // Reads 8-byte header, routes payload to the correct channel.
    // =====================================================================
    private async Task RunReaderLoopAsync(MuxConnection conn, CancellationToken ct)
    {
        var transport = conn.Transport ?? throw new InvalidOperationException("Transport not initialized.");
        var readStream = transport.ReadStream;
        byte[] headerBuf = new byte[FrameHeader.Size];

        // Fixed 64KB buffer for typical frames. Large frames rent from ArrayPool.
        const int InlineBufferSize = 65_536;
        byte[] inlineBuf = new byte[InlineBufferSize];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 1. Read exactly 8 bytes (frame header)
                await ReadExactAsync(readStream, headerBuf, ct);
                var header = FrameHeader.Parse(headerBuf);

                Interlocked.Add(ref _stats._bytesReceived, FrameHeader.Size + header.PayloadLength);

                // 2. Read payload — use inline buffer for small frames, rent for large
                byte[]? rentedBuf = null;
                byte[] payloadBuf = inlineBuf;
                if (header.PayloadLength > InlineBufferSize)
                {
                    rentedBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(header.PayloadLength);
                    payloadBuf = rentedBuf;
                }

                try
                {
                if (header.PayloadLength > 0)
                {
                    await ReadExactAsync(readStream, payloadBuf.AsMemory(0, header.PayloadLength), ct);
                }

                var payload = header.PayloadLength > 0
                    ? payloadBuf.AsSpan(0, header.PayloadLength)
                    : ReadOnlySpan<byte>.Empty;

                // 3. Route to channel
                if (header.ChannelIndex == ChannelConstants.ControlChannel)
                {
                    ProcessControlFrame(conn, header, payload);
                }
                else
                {
                    DispatchToChannel(header, payload);
                }
                }
                finally
                {
                    if (rentedBuf is not null)
                        System.Buffers.ArrayPool<byte>.Shared.Return(rentedBuf);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private void DispatchToChannel(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.Flags == FrameFlags.Init)
        {
            HandleInitFrame(header, payload);
            return;
        }

        DispatchExistingChannelFrame(header, payload);
    }

    private void HandleInitFrame(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ChannelConstants.MaxChannelIdLength)
            return; // channel name too long — drop frame

        string channelId = Encoding.UTF8.GetString(payload);
        ReadChannel readChannel;
        bool isNewlyAccepted;

        // Atomic registration: serialized with AcceptChannel so we cannot
        // race between adopting a pending channel and the test grabbing
        // a transient _readChannels entry.
        lock (_registry.AcceptLock)
        {
            // After reconnect, Init frames are replayed from the slab.
            // If the channel already exists, skip re-registration.
            var existing = _registry.GetReadChannel(header.ChannelIndex);
            if (existing is not null)
                return;

            // Check if a pending accept channel was pre-created via AcceptChannel.
            // A pending entry whose state is already Closed was disposed by the
            // caller before INIT arrived; treat it as if no pending exists and
            // fall through to creating a fresh channel. Adopting the disposed
            // instance would resurrect a channel whose slab has been returned
            // to ArrayPool<byte>.Shared.
            var pendingChannel = _registry.GetPendingAcceptChannel(channelId);
            if (pendingChannel is not null && pendingChannel.State == ChannelState.Closed)
            {
                _registry.RemovePendingAcceptChannel(channelId);
                pendingChannel = null;
            }

            if (pendingChannel is not null)
            {
                readChannel = pendingChannel;
                readChannel.SetChannelIndex(header.ChannelIndex);
                _registry.RemovePendingAcceptChannel(channelId);
                isNewlyAccepted = true;
            }
            else
            {
                readChannel = new ReadChannel(
                    channelId,
                    header.ChannelIndex,
                    _options.DefaultChannelOptions.Priority,
                    _options.DefaultChannelOptions.SlabSize,
                    this);
                isNewlyAccepted = false;
            }

            // Peer-reopen reconciliation (fixes #367). The peer's WriteChannel
            // auto-unregisters its ChannelId when it closes, so the peer is
            // free to immediately open a fresh channel under the same id at
            // a brand-new index. On this side, the prior ReadChannel for that
            // id stays in the registry until the local consumer disposes it,
            // by design, so the consumer can still drain buffered post-FIN
            // bytes through its existing reference and inspect the close
            // reason. The two invariants collide when the peer's reopen INIT
            // arrives before the consumer disposes: RegisterReadChannel
            // would throw ChannelExists, fault the reader, and trigger an
            // infinite reconnect-and-refault loop on every replay of the
            // INIT frame. The structural resolution is to honour the
            // peer's reopen atomically with the new registration under the
            // same AcceptLock: if the existing ChannelId slot is held by a
            // Closed channel, evict it. The consumer's reference to the
            // closed channel remains valid; only the registry slot is
            // yielded.
            var stale = _registry.GetReadChannelById(channelId);
            if (stale is not null && stale.State == ChannelState.Closed)
            {
                _registry.UnregisterChannel(stale.ChannelIndex, channelId);
            }

            _registry.RegisterReadChannel(header.ChannelIndex, readChannel);
        }

        // Account for the INIT frame bytes that just arrived; the writer-side slab
        // includes them in its _sentPos counter so the reader's FrameBytesReceived
        // must match (otherwise reconnect replay-base lands mid-frame).
        readChannel.AccountInboundFrame(FrameHeader.Size + payload.Length);

        readChannel.MarkOpen();
        readChannel.MarkConnected();

        // Send init-ack so the opener knows the channel is established
        SendInitAck(header.ChannelIndex);

        Interlocked.Increment(ref _stats._openChannels);
        Interlocked.Increment(ref _stats._totalChannelsOpened);
        RaiseEvent(ChannelAccepted, new ChannelEventArgs(channelId));

        // Only enqueue for generic AcceptChannelsAsync if no specific accept claimed it.
        if (!isNewlyAccepted)
            _registry.EnqueueForAccept(readChannel);
    }

    private void DispatchExistingChannelFrame(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        // Route data/ack/fin/err to existing channel
        var channel = _registry.GetReadChannel(header.ChannelIndex);
        if (channel is null)
        {
            // Could be an ACK for our write channel
            var writeChannel = _registry.GetWriteChannel(header.ChannelIndex);
            if (writeChannel is not null && header.Flags == FrameFlags.Ack && payload.Length >= 8)
            {
                long ackPos = (long)BinaryPrimitives.ReadUInt64BigEndian(payload);
                writeChannel.OnAck(ackPos);
                return;
            }
            return; // Unknown channel — drop frame
        }

        channel.ReceivePayload(header.Flags, payload);

        if (header.Flags == FrameFlags.Fin)
        {
            // Single-decrement contract via CAS: whichever runs first
            // (this FIN handler or NotifyChannelCompleted on local Dispose)
            // claims the accounting; the other becomes a no-op.
            if (channel.TryClaimCompletionAccounting())
            {
                Interlocked.Decrement(ref _stats._openChannels);
                Interlocked.Increment(ref _stats._totalChannelsClosed);
                RaiseEvent(ChannelClosed, new ChannelClosedEventArgs(channel.ChannelId, null));
            }
        }
    }

    private void ProcessControlFrame(MuxConnection conn, FrameHeader header, ReadOnlySpan<byte> payload)
    {
        switch (header.Flags)
        {
            case FrameFlags.Ping:
                // Respond with Pong on control channel (echo the payload)
                SendControlFrame(FrameFlags.Pong, payload);
                break;
            case FrameFlags.Pong:
                // Correlate the echoed 8-byte token to the currently outstanding ping.
                // A late pong from a previous (timed-out) ping must not satisfy the
                // *next* ping's TCS — that would mask real liveness failures by
                // resetting the missed-ping counter.
                if (payload.Length >= 8)
                {
                    long echoedToken = BinaryPrimitives.ReadInt64BigEndian(payload);
                    var pending = Volatile.Read(ref conn.PendingPong);
                    if (pending is not null
                        && pending.ExpectedToken == echoedToken
                        && Interlocked.CompareExchange(ref conn.PendingPong, null, pending) == pending)
                    {
                        pending.Tcs.TrySetResult();
                    }
                    // else: stale or already-cleared — drop silently.
                }
                break;
            case FrameFlags.Ctrl:
                ProcessCtrlSubframe(payload);
                break;
        }
    }

    private void ProcessCtrlSubframe(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return;

        byte subtype = payload[0];
        switch (subtype)
        {
            case CtrlSubtype.GoAway:
                HandleRemoteGoAway();
                break;
            case CtrlSubtype.Reconnect:
                // Reconnection handled separately
                break;
        }
    }

    private void HandleRemoteGoAway()
    {
        // If local already initiated shutdown (via GoAwayAsync or DisposeAsync),
        // the existing path owns teardown. Don't double-drive it.
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        _disconnectReason = Enums.DisconnectReason.GoAwayReceived;

        // Mirror GoAwayAsync's drain-then-cancel semantics on the remote-initiated
        // path. Cancelling _cts immediately would wake the main loop, which then
        // cancels _loopCts and aborts the writer mid-flush — frames already stamped
        // into channel slabs by WriteAsync (which returned successfully to the caller)
        // would be silently dropped on the wire.
        //
        // Run the drain off-thread because this handler executes on the reader loop;
        // the writer needs the reader to keep pumping inbound ACKs to release slab
        // capacity while it finishes flushing.
        _ = Task.Run(DrainAndCancelOnRemoteGoAwayAsync);
    }

    private async Task DrainAndCancelOnRemoteGoAwayAsync()
    {
        try
        {
            using var drainCts = new CancellationTokenSource(_options.GoAwayTimeout);
            while (!drainCts.IsCancellationRequested)
            {
                bool anyPending = false;
                foreach (var ch in _registry.GetAllWriteChannels())
                {
                    if (ch.HasPendingData())
                    {
                        anyPending = true;
                        break;
                    }
                }
                if (!anyPending) break;

                try
                {
                    await Task.Delay(20, drainCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch
        {
            // Best-effort drain — fall through to cancel regardless. Any error here
            // (e.g. registry mutation race during teardown) must not leave _cts
            // un-cancelled or the main loop never observes the GoAway.
        }
        finally
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* DisposeAsync already ran */ }
        }
    }

    // Non-throwing control-frame senders. Returning false (rather than throwing
    // InvalidOperationException as the old WriteRawFrame did) preserves the
    // invariant that control-slab pressure must never fault the reader thread,
    // keepalive loop, or graceful-shutdown path. Callers decide the recovery
    // policy per frame kind (drop, retry, escalate).
    private bool SendControlFrame(FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        if (_conn.ControlChannel is null) return false;
        return _conn.ControlChannel.TryWriteRawFrame(ControlFrameBuilder.BuildControlFrame(flags, payload));
    }

    private bool SendInitAck(ushort channelIndex)
    {
        if (_conn.ControlChannel is null) return false;
        return _conn.ControlChannel.TryWriteRawFrame(ControlFrameBuilder.BuildAckFrame(channelIndex, 0));
    }

    // Re-attempt every queued INIT-ACK whose original SendInitAck failed because
    // the control slab was transiently full. Bounded by the peer's outstanding
    // open budget. Any ACK that still cannot be staged is re-queued for the next
    // drain pass.
    private void DrainPendingInitAcks(MuxConnection conn)
    {
        if (conn.PendingInitAcks.IsEmpty) return;
        int initialCount = conn.PendingInitAcks.Count;
        int requeue = 0;
        while (requeue < initialCount && conn.PendingInitAcks.TryDequeue(out ushort idx))
        {
            if (!SendInitAck(idx))
            {
                conn.PendingInitAcks.Enqueue(idx);
                requeue++;
            }
        }
    }

    bool IChannelOwner.SendAck(ushort channelIndex, ulong consumedPosition)
    {
        // Return false (without throwing) when the control channel is gone or
        // its slab is currently full. The caller (ReadChannel.MaybeSendAck) is
        // expected to retain its unacked accumulator and retry on the next
        // gate crossing
        if (_conn.ControlChannel is null) return false;

        return _conn.ControlChannel.TryWriteRawFrame(
            ControlFrameBuilder.BuildAckFrame(channelIndex, consumedPosition));
    }

    private async Task PerformHandshakeAsync(CancellationToken ct)
    {
        var transport = _conn.Transport ?? throw new InvalidOperationException("Transport not initialized.");
        var result = await MuxHandshake.PerformInitialAsync(
            transport,
            _conn.SessionId,
            _options.DefaultChannelOptions.SlabSize,
            ct);
        _conn.RemoteSessionId = result.RemoteSessionId;
        _conn.PeerMaxRecvPayload = result.PeerMaxRecvPayload;
        _registry.SetIndexParity(result.UseOddIndices);
    }

    private async Task PerformReconnectHandshakeAsync(IStreamPair transport, CancellationToken ct)
    {
        var result = await MuxHandshake.PerformReconnectAsync(
            transport,
            _conn.SessionId,
            _conn.RemoteSessionId,
            _options.DefaultChannelOptions.SlabSize,
            BuildLocalReplayPositions(),
            ApplyRemoteReplayPositions,
            ct);
        _conn.PeerMaxRecvPayload = result.PeerMaxRecvPayload;
    }

    private IReadOnlyList<ChannelReplayPosition> BuildLocalReplayPositions()
    {
        var readChannels = _registry.GetAllReadChannels();
        if (readChannels.Count == 0)
            return Array.Empty<ChannelReplayPosition>();

        var result = new ChannelReplayPosition[readChannels.Count];
        int i = 0;
        foreach (var ch in readChannels)
        {
            result[i++] = new ChannelReplayPosition(ch.ChannelIndex, ch.FrameBytesReceived);
        }
        return result;
    }

    private void ApplyRemoteReplayPositions(IReadOnlyList<ChannelReplayPosition> positions)
    {
        for (int i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            var writeChannel = _registry.GetWriteChannel(pos.ChannelIndex);
            writeChannel?.SetReplayBase(pos.FrameBytesReceived);
        }
    }

    // Walks the registry's WriteChannels after the initial handshake set the
    // real index parity. Any pre-handshake OpenChannel / TryRegisterChannels
    // call allocated indices from the default odd seed (Create(.) hardcodes
    // useOddIndices: true on both peers); on the side whose session GUID lost
    // the handshake comparison those indices are now in the wrong parity
    // space and must move before the writer thread transmits any frame from
    // them. Both peers always allocate index 1 first pre-handshake, so without
    // this both sides would transmit INIT for the same wire index and the
    // peer's INIT-ACK would route to the wrong local channel.
    //
    // Called only on the first connect (after PerformHandshakeAsync, before
    // the writer/flusher/reader tasks start) so no concurrent slab reader can
    // observe an in-flight rewrite. Reconnects re-use the same parity (it is
    // a deterministic function of the unchanging session GUIDs) and never
    // re-enter this path.
    private void ReassignPreHandshakeWriteChannelIndices()
    {
        foreach (var channel in _registry.GetAllWriteChannels())
        {
            if (_registry.IsCurrentParity(channel.ChannelIndex))
                continue;

            ushort oldIndex = channel.ChannelIndex;
            ushort newIndex = _registry.AllocateChannelIndex();
            channel.RestampChannelIndex(newIndex);
            _registry.RekeyWriteChannel(oldIndex, newIndex, channel);
        }
    }


    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0)
                throw new HandshakeTransportException(
                    "Transport stream closed before the handshake completed.",
                    new EndOfStreamException("Transport stream closed unexpectedly."));
            totalRead += read;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Snapshot before reset: a never-started mux must not emit Disconnected.
        bool wasStarted = _isRunning;

        _isRunning = false;
        _isConnected = false;
        _disconnectReason ??= Enums.DisconnectReason.LocalDispose;

        // Complete _readyTcs before signalling shutdown so a never-started mux
        // (MainLoopAsync never ran, OCE catch never fires) still surfaces a
        // TaskCanceledException to any pending WaitForReadyAsync caller
        // instead of hanging forever (fixes #395, #401). Idempotent vs. a
        // prior TrySetResult on the happy path.
        _readyTcs.TrySetCanceled();

        _cts.Cancel();

        _registry.AbortAllChannels(ChannelCloseReason.MuxDisposed);
        _registry.CancelAllPendingAccepts();

        if (_conn.MainLoopTask is not null)
        {
            try { await _conn.MainLoopTask; }
            catch (OperationCanceledException) { }
            catch { /* swallow during dispose */ }
        }

        if (_conn.Transport is not null)
        {
            await _conn.Transport.DisposeAsync();
            _conn.Transport = null;
        }

        if (_conn.ControlChannel is not null)
        {
            _conn.ControlChannel.Abort(ChannelCloseReason.MuxDisposed);
            _conn.ControlChannel = null;
        }

        _conn.LoopCts?.Dispose();
        _readySignal.Dispose();
        _flushSignal.Dispose();
        _cts.Dispose();

        if (wasStarted && !_disconnectedFired && _disconnectReason.HasValue)
            RaiseEvent(Disconnected, new DisconnectedEventArgs(_disconnectReason.Value, null));
    }

    // Multicast-safe event raise. A throwing handler must not prevent the remaining
    // handlers in the invocation list from running, nor crash the producer thread.
    // Non-fatal exceptions from a handler are routed to the Error event so they are
    // observable from outside the process. Fatal exceptions (OOM, AV) propagate.
    private void RaiseEvent<T>(EventHandler<T>? handler, T args) where T : EventArgs
        => SafeEventRaiser.Raise(this, handler, args, RaiseError);

    private void RaiseEvent(EventHandler? handler)
        => SafeEventRaiser.Raise(this, handler, RaiseError);

    // Direct path for raising the Error event. Passes a null exception route to
    // SafeEventRaiser so a throwing Error handler cannot recurse back into Error;
    // its exception is dropped as the absolute last resort.
    private void RaiseError(Exception exception)
        => SafeEventRaiser.Raise(this, Error, new Events.ErrorEventArgs(exception), onHandlerException: null);
}
