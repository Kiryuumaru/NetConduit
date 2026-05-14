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
/// The multiplexer — a pure router. Channels do all heavy lifting.
/// The mux just picks up ready frames from channels and sends them, and routes
/// incoming frames to the correct channel by reading the 8-byte header.
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

    private IStreamPair? _transport;
    private Task? _mainLoopTask;
    private Task? _writerTask;
    private Task? _readerTask;
    private Task? _flusherTask;
    private Task? _keepaliveTask;
    private CancellationTokenSource? _loopCts;
    private WriteChannel? _controlChannel;
    private volatile bool _isRunning;
    private volatile bool _isConnected;
    private volatile bool _isReady;
    private volatile bool _isShuttingDown;
    private volatile bool _disconnectedFired;
    private DisconnectReason? _disconnectReason;
    private Guid _sessionId;
    private Guid _remoteSessionId;
    private long _lastPongTicks;

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
    public Guid SessionId => _sessionId;

    /// <inheritdoc />
    public Guid RemoteSessionId => _remoteSessionId;

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
        _sessionId = options.SessionId ?? Guid.NewGuid();
        _registry = new ChannelRegistry(useOddIndices);
    }

    /// <summary>
    /// Create a new multiplexer with the given options.
    /// </summary>
    public static StreamMultiplexer Create(MultiplexerOptions options)
    {
        return new StreamMultiplexer(options, useOddIndices: true);
    }

    /// <inheritdoc />
    public void Start()
    {
        if (_isRunning)
            throw new InvalidOperationException("Multiplexer is already running.");

        _isRunning = true;
        _stats._startTicks = Environment.TickCount64;
        _mainLoopTask = Task.Run(() => MainLoopAsync(_cts.Token));
    }

    /// <inheritdoc />
    public Task WaitForReadyAsync(CancellationToken ct = default) => _readyTcs.Task.WaitAsync(ct);

    /// <inheritdoc />
    public IWriteChannel OpenChannel(ChannelOptions options)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Multiplexer has not been started.");

        bool enableReplay = _options.MaxAutoReconnectAttempts > 0;
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
        byte[] channelIdBytes = Encoding.UTF8.GetBytes(options.ChannelId);
        channel.WriteInitFrame(channelIdBytes);
        // Channel stays in Opening/Pending state until remote ACKs the INIT

        if (_isConnected)
            channel.MarkConnected();

        Interlocked.Increment(ref _stats._openChannels);
        Interlocked.Increment(ref _stats._totalChannelsOpened);
        RaiseEvent(ChannelOpened, new ChannelEventArgs(options.ChannelId));

        return channel;
    }

    /// <inheritdoc />
    public IReadChannel AcceptChannel(string channelId)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Multiplexer has not been started.");

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
    public IAsyncEnumerable<IReadChannel> AcceptChannelsAsync(CancellationToken ct = default)
    {
        return _registry.AcceptChannelsAsync(ct);
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

        // Allow the GoAway frame to be sent by the writer thread
        try
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delayCts.CancelAfter(_options.GoAwayTimeout);
            // Wait briefly for the writer to flush the GoAway
            await Task.Delay(100, delayCts.Token);
        }
        catch (OperationCanceledException) { /* timeout or caller cancel is fine */ }

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

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Connect with retry (no delay on first attempt)
                var transport = await ConnectWithRetryAsync(hasConnectedBefore, ct);

                // Handshake: reconnect if we've connected before, initial otherwise
                if (hasConnectedBefore)
                {
                    await PerformReconnectHandshakeAsync(transport, ct);

                    foreach (var ch in _registry.GetAllWriteChannels())
                        ch.PrepareReplay();
                }
                else
                {
                    _transport = transport;
                    await PerformHandshakeAsync(ct);
                }

                _transport = transport;
                _isConnected = true;
                _disconnectReason = null;

                // Create control channel on first connect
                if (_controlChannel is null)
                {
                    _controlChannel = new WriteChannel(
                        "__control__",
                        ChannelConstants.ControlChannel,
                        ChannelPriority.Highest,
                        FrameConstants.MinSlabSize,
                        TimeSpan.FromSeconds(5),
                        this);
                    _controlChannel.MarkOpen();
                }

                // Start loops with a per-session CTS
                _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var loopCt = _loopCts.Token;

                _lastPongTicks = Environment.TickCount64;

                _writerTask = Task.Factory.StartNew(
                    () => RunWriterLoop(loopCt),
                    loopCt,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                _flusherTask = Task.Factory.StartNew(
                    () => RunFlusherLoop(loopCt),
                    loopCt,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                _readerTask = Task.Run(() => RunReaderLoopAsync(loopCt), loopCt);

                if (_options.PingInterval > TimeSpan.Zero)
                    _keepaliveTask = Task.Run(() => RunKeepaliveLoopAsync(loopCt), loopCt);

                RaiseEvent(Connected);

                if (!hasConnectedBefore)
                {
                    _isReady = true;
                    _readyTcs.TrySetResult();
                    RaiseEvent(Ready);
                }
                hasConnectedBefore = true;

                // Notify all channels that transport is connected
                foreach (var ch in _registry.GetAllWriteChannels())
                    ch.MarkConnected();
                foreach (var ch in _registry.GetAllReadChannels())
                    ch.MarkConnected();

                // Block until any loop faults (transport died)
                var faulted = await Task.WhenAny(
                    _writerTask,
                    _readerTask,
                    _flusherTask,
                    _keepaliveTask ?? Task.Delay(Timeout.Infinite, ct));

                // Transport is dead — cancel all loops and clean up
                _isConnected = false;
                _loopCts.Cancel();

                // Notify all channels that transport is disconnected
                foreach (var ch in _registry.GetAllWriteChannels())
                    ch.MarkDisconnected(Enums.DisconnectReason.TransportError, null);
                foreach (var ch in _registry.GetAllReadChannels())
                    ch.MarkDisconnected(Enums.DisconnectReason.TransportError, null);

                await WaitForLoopsAsync();
                _loopCts.Dispose();
                _loopCts = null;

                // Capture the exception if any
                Exception? transportEx = faulted.Exception?.InnerException;
                if (transportEx is not null)
                    RaiseEvent(Error, new Events.ErrorEventArgs(transportEx));

                // Dispose old transport
                await transport.DisposeAsync();
                _transport = null;

                if (_isShuttingDown)
                {
                    if (_disconnectReason == Enums.DisconnectReason.GoAwayReceived)
                    {
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
            _readyTcs.TrySetException(ex);
        }
    }

    private async Task<IStreamPair> ConnectWithRetryAsync(bool isReconnect, CancellationToken ct)
    {
        int maxAttempts = _options.MaxAutoReconnectAttempts;
        double delay = _options.AutoReconnectDelay.TotalMilliseconds;
        double maxDelay = _options.MaxAutoReconnectDelay.TotalMilliseconds;

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Enforce max attempts (0 = unlimited)
            if (maxAttempts > 0 && attempt > maxAttempts)
                throw new MultiplexerException(ErrorCode.Internal,
                    $"Connection failed after {maxAttempts} attempts.");

            // Delay before retry (skip on first attempt of first connect)
            if (attempt > 1 || isReconnect)
            {
                RaiseEvent(Reconnecting, new ReconnectingEventArgs(attempt));
                await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                delay = Math.Min(delay * _options.AutoReconnectBackoffMultiplier, maxDelay);
            }

            try
            {
                if (_options.ConnectionTimeout > TimeSpan.Zero
                    && _options.ConnectionTimeout != Timeout.InfiniteTimeSpan)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(_options.ConnectionTimeout);
                    try
                    {
                        return await _options.StreamFactory(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"Connection timed out after {_options.ConnectionTimeout}");
                    }
                }

                return await _options.StreamFactory(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RaiseEvent(Error, new Events.ErrorEventArgs(ex));

                // If max attempts is 0 (no retry on initial) and not reconnecting, propagate immediately
                if (!isReconnect && maxAttempts == 0)
                    throw;
            }
        }
    }

    private async Task WaitForLoopsAsync()
    {
        Task[] loops = [_writerTask!, _readerTask!, _flusherTask!];
        if (_keepaliveTask is not null)
            loops = [.. loops, _keepaliveTask];

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

    // =====================================================================
    // Writer Thread — THE DUMB ROUTER (send side)
    // Picks ready channels, writes their pre-built frames to the stream.
    // Flush is handled by the separate flusher thread.
    // Runs on a dedicated LongRunning thread; uses synchronous I/O so the
    // hot path does not allocate Task continuations or hop ThreadPool threads.
    // =====================================================================
    private void RunWriterLoop(CancellationToken ct)
    {
        var transport = _transport ?? throw new InvalidOperationException("Transport not initialized.");
        var writeStream = transport.WriteStream;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                _readySignal.Wait(ct);

                // Snapshot and sort ready channels by priority (highest first)
                WriteChannel[] snapshot;
                lock (_readyLock)
                {
                    if (_readyChannels.Count == 0) continue;
                    _readyChannels.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
                    snapshot = _readyChannels.ToArray();
                    _readyChannels.Clear();
                }

                bool anyWritten = false;
                foreach (var channel in snapshot)
                {
                    Memory<byte> frames = channel.TakeReady();
                    if (frames.IsEmpty) continue;

                    writeStream.Write(frames.Span);
                    channel.MarkSent(frames.Length);
                    Interlocked.Add(ref _stats._bytesSent, frames.Length);
                    anyWritten = true;
                }

                if (anyWritten)
                    _flushSignal.Signal();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    // =====================================================================
    // Flusher Thread — dedicated thread that flushes the transport stream.
    // Decouples write batching from kernel flush syscall.
    // =====================================================================
    private void RunFlusherLoop(CancellationToken ct)
    {
        var transport = _transport ?? throw new InvalidOperationException("Transport not initialized.");
        var writeStream = transport.WriteStream;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                _flushSignal.Wait(ct);
                writeStream.Flush();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — do one final flush to push any remaining data
            try { writeStream.Flush(); }
            catch { /* transport may already be closed */ }
        }
    }

    // =====================================================================
    // Reader Thread — THE DISPATCHER (receive side)
    // Reads 8-byte header, routes payload to the correct channel.
    // =====================================================================
    private async Task RunReaderLoopAsync(CancellationToken ct)
    {
        var transport = _transport ?? throw new InvalidOperationException("Transport not initialized.");
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

                if (header.PayloadLength > FrameConstants.MaxFramePayloadSize)
                    throw new MultiplexerException(ErrorCode.ProtocolError,
                        $"Frame payload exceeds maximum: {header.PayloadLength} > {FrameConstants.MaxFramePayloadSize}");

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
                    ProcessControlFrame(header, payload);
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

                // Check if a pending accept channel was pre-created via AcceptChannel
                var pendingChannel = _registry.GetPendingAcceptChannel(channelId);

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
            return;
        }

        // Route data/ack/fin/err to existing channel
        var channel = _registry.GetReadChannel(header.ChannelIndex);
        if (channel is null)
        {
            // Could be an ACK for our write channel
            var writeChannel = _registry.GetWriteChannel(header.ChannelIndex);
            if (writeChannel is not null && header.Flags == FrameFlags.Ack && payload.Length >= 4)
            {
                int ackPos = (int)BinaryPrimitives.ReadUInt32BigEndian(payload);
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

    private void ProcessControlFrame(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        switch (header.Flags)
        {
            case FrameFlags.Ping:
                // Respond with Pong on control channel (echo the payload)
                SendControlFrame(FrameFlags.Pong, payload);
                break;
            case FrameFlags.Pong:
                // Record pong receipt for keepalive tracking
                Volatile.Write(ref _lastPongTicks, Environment.TickCount64);
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

    // =====================================================================
    // Keepalive Loop — sends periodic PING frames, monitors PONG responses
    // =====================================================================
    private async Task RunKeepaliveLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PingInterval);
        int missedPings = 0;
        byte[] pingPayload = new byte[8];

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Check if we received a pong since last ping
                long lastPong = Volatile.Read(ref _lastPongTicks);
                long elapsed = Environment.TickCount64 - lastPong;

                if (elapsed > _options.PingTimeout.TotalMilliseconds)
                {
                    missedPings++;
                    if (missedPings >= _options.MaxMissedPings)
                    {
                        throw new IOException(
                            $"Keepalive timeout: {missedPings} missed pings (timeout: {_options.PingTimeout})");
                    }
                }
                else
                {
                    missedPings = 0;
                }

                // Send ping with 8-byte timestamp
                BinaryPrimitives.WriteInt64BigEndian(pingPayload, Environment.TickCount64);
                SendControlFrame(FrameFlags.Ping, pingPayload);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private void SendControlFrame(FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        if (_controlChannel is null) return;

        // Build the control frame in the control channel's slab — writer thread sends it
        int frameSize = FrameHeader.Size + payload.Length;
        byte[] temp = new byte[frameSize];
        FrameHeader.WriteTo(temp, ChannelConstants.ControlChannel, flags, payload.Length);
        if (!payload.IsEmpty)
            payload.CopyTo(temp.AsSpan(FrameHeader.Size));

        // Write through the control channel's slab so the writer thread picks it up
        // The control channel uses ChannelIndex 0, so frames are stamped with channel 0
        _controlChannel.WriteRawFrame(temp);
    }

    private void SendInitAck(ushort channelIndex)
    {
        if (_controlChannel is null) return;

        // ACK frame on the opener's channel index with position 0 — signals channel established
        byte[] frame = new byte[FrameHeader.Size + 4];
        FrameHeader.WriteTo(frame, channelIndex, FrameFlags.Ack, 4);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(FrameHeader.Size, 4), 0);
        _controlChannel.WriteRawFrame(frame);
    }

    private async Task PerformHandshakeAsync(CancellationToken ct)
    {
        var transport = _transport ?? throw new InvalidOperationException("Transport not initialized.");

        // Send our session ID
        byte[] handshake = new byte[FrameHeader.Size + 16];
        FrameHeader.WriteTo(handshake, ChannelConstants.ControlChannel, FrameFlags.Ctrl, 16);
        _sessionId.TryWriteBytes(handshake.AsSpan(FrameHeader.Size));

        await transport.WriteStream.WriteAsync(handshake, ct);
        await transport.WriteStream.FlushAsync(ct);

        // Read remote session ID
        byte[] remoteHandshake = new byte[FrameHeader.Size + 16];
        await ReadExactAsync(transport.ReadStream, remoteHandshake, ct);

        var remoteHeader = FrameHeader.Parse(remoteHandshake);
        if (remoteHeader.Flags != FrameFlags.Ctrl || remoteHeader.PayloadLength != 16)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Invalid handshake from remote.");

        _remoteSessionId = new Guid(remoteHandshake.AsSpan(FrameHeader.Size, 16));

        // Determine odd/even index allocation based on session ID comparison
        // Higher session ID gets odd indices
        bool useOdd = _sessionId.CompareTo(_remoteSessionId) > 0;
        _registry.SetIndexParity(useOdd);
    }

    private async Task PerformReconnectHandshakeAsync(IStreamPair transport, CancellationToken ct)
    {
        // Symmetric reconnect: both sides send Reconnect, both read Reconnect.
        // Same pattern as initial handshake (send session ID, read session ID).

        // Send reconnect frame: [CtrlSubtype.Reconnect][sessionId:16B]
        byte[] reconnectPayload = new byte[1 + 16];
        reconnectPayload[0] = CtrlSubtype.Reconnect;
        _sessionId.TryWriteBytes(reconnectPayload.AsSpan(1));

        byte[] frame = new byte[FrameHeader.Size + reconnectPayload.Length];
        FrameHeader.WriteTo(frame, ChannelConstants.ControlChannel, FrameFlags.Ctrl, reconnectPayload.Length);
        reconnectPayload.CopyTo(frame.AsSpan(FrameHeader.Size));

        await transport.WriteStream.WriteAsync(frame, ct);
        await transport.WriteStream.FlushAsync(ct);

        // Read remote reconnect frame: [CtrlSubtype.Reconnect][sessionId:16B]
        byte[] headerBuf = new byte[FrameHeader.Size];
        await ReadExactAsync(transport.ReadStream, headerBuf, ct);
        var header = FrameHeader.Parse(headerBuf);

        if (header.Flags != FrameFlags.Ctrl || header.PayloadLength < 17)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Invalid reconnect frame.");

        byte[] remotePayload = new byte[header.PayloadLength];
        await ReadExactAsync(transport.ReadStream, remotePayload, ct);

        if (remotePayload[0] != CtrlSubtype.Reconnect)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Expected reconnect subtype.");

        var remoteSession = new Guid(remotePayload.AsSpan(1, 16));
        if (remoteSession != _remoteSessionId)
            throw new MultiplexerException(ErrorCode.SessionMismatch, "Remote session ID mismatch on reconnect.");
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0)
                throw new IOException("Transport stream closed unexpectedly.");
            totalRead += read;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_isRunning && _transport is null) return;

        _isRunning = false;
        _isConnected = false;
        _disconnectReason ??= Enums.DisconnectReason.LocalDispose;

        _cts.Cancel();

        _registry.AbortAllChannels(ChannelCloseReason.MuxDisposed);
        _registry.CancelAllPendingAccepts();

        if (_mainLoopTask is not null)
        {
            try { await _mainLoopTask; }
            catch (OperationCanceledException) { }
            catch { /* swallow during dispose */ }
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
            _transport = null;
        }

        if (_controlChannel is not null)
        {
            _controlChannel.Abort(ChannelCloseReason.MuxDisposed);
            _controlChannel = null;
        }

        _loopCts?.Dispose();
        _readySignal.Dispose();
        _flushSignal.Dispose();
        _cts.Dispose();

        if (!_disconnectedFired && _disconnectReason.HasValue)
            RaiseEvent(Disconnected, new DisconnectedEventArgs(_disconnectReason.Value, null));
    }

    private void RaiseEvent<T>(EventHandler<T>? handler, T args) where T : EventArgs
    {
        try { handler?.Invoke(this, args); }
        catch { }
    }

    private void RaiseEvent(EventHandler? handler)
    {
        try { handler?.Invoke(this, EventArgs.Empty); }
        catch { }
    }
}
