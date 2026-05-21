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
        ushort index = _registry.AllocateChannelIndex();
        var channel = new WriteChannel(
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
        _isShuttingDown = true;

        // Send GoAway control frame to remote before shutting down
        ReadOnlySpan<byte> goAwayPayload = [CtrlSubtype.GoAway];
        SendControlFrame(FrameFlags.Ctrl, goAwayPayload);

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

                handshakeAttempt = 0;

                _conn.Transport = transport;
                _isConnected = true;
                _disconnectReason = null;

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
            // Normal shutdown via Dispose/GoAway
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
        // Write channels: stats decrement here (after FIN sent + no pending data).
        // Read channels: stats already decremented at FIN receipt in DispatchToChannel.
        bool isWriteChannel = _registry.GetWriteChannel(channelIndex) is not null;

        _registry.UnregisterChannel(channelIndex, channelId);

        if (isWriteChannel)
        {
            Interlocked.Decrement(ref _stats._openChannels);
            Interlocked.Increment(ref _stats._totalChannelsClosed);
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
        // thread that raised the channel event (#286).
        RaiseError(exception);
    }

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

            _registry.RegisterReadChannel(header.ChannelIndex, readChannel);
        }

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
            Interlocked.Decrement(ref _stats._openChannels);
            Interlocked.Increment(ref _stats._totalChannelsClosed);
            RaiseEvent(ChannelClosed, new ChannelClosedEventArgs(channel.ChannelId, null));
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
                Interlocked.Exchange(ref conn.PendingPong, null)?.TrySetResult();
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
        _isShuttingDown = true;
        _disconnectReason = Enums.DisconnectReason.GoAwayReceived;
        _cts.Cancel();
    }

    private void SendControlFrame(FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        if (_conn.ControlChannel is null) return;

        // Write through the control channel's slab so the writer thread picks it up.
        // The control channel uses ChannelIndex 0, so frames are stamped with channel 0.
        _conn.ControlChannel.WriteRawFrame(ControlFrameBuilder.BuildControlFrame(flags, payload));
    }

    private void SendInitAck(ushort channelIndex)
    {
        if (_conn.ControlChannel is null) return;

        // ACK frame on the opener's channel index with position 0 — signals channel established.
        _conn.ControlChannel.WriteRawFrame(ControlFrameBuilder.BuildAckFrame(channelIndex, 0));
    }

    void IChannelOwner.SendAck(ushort channelIndex, ulong consumedPosition)
    {
        if (_conn.ControlChannel is null) return;

        _conn.ControlChannel.WriteRawFrame(ControlFrameBuilder.BuildAckFrame(channelIndex, consumedPosition));
    }

    private async Task PerformHandshakeAsync(CancellationToken ct)
    {
        var transport = _conn.Transport ?? throw new InvalidOperationException("Transport not initialized.");
        var result = await MuxHandshake.PerformInitialAsync(transport, _conn.SessionId, ct);
        _conn.RemoteSessionId = result.RemoteSessionId;
        _registry.SetIndexParity(result.UseOddIndices);
    }

    private Task PerformReconnectHandshakeAsync(IStreamPair transport, CancellationToken ct)
        => MuxHandshake.PerformReconnectAsync(transport, _conn.SessionId, _conn.RemoteSessionId, ct);

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
        if (!_isRunning && _conn.Transport is null) return;

        _isRunning = false;
        _isConnected = false;
        _disconnectReason ??= Enums.DisconnectReason.LocalDispose;

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

        if (!_disconnectedFired && _disconnectReason.HasValue)
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
