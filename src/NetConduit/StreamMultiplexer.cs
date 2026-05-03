using System.Buffers.Binary;
using System.Text;
using NetConduit.Constants;
using NetConduit.Internal;

namespace NetConduit;

/// <summary>
/// The multiplexer — a pure router. Channels do all heavy lifting.
/// The mux just picks up ready frames from channels and sends them, and routes
/// incoming frames to the correct channel by reading the 8-byte header.
/// </summary>
public sealed class StreamMultiplexer : IStreamMultiplexer, IFrameRouter
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
    private Task? _writerTask;
    private Task? _readerTask;
    private Task? _flusherTask;
    private Task? _keepaliveTask;
    private WriteChannel? _controlChannel;
    private volatile bool _isRunning;
    private volatile bool _isShuttingDown;
    private DisconnectReason? _disconnectReason;
    private Guid _sessionId;
    private Guid _remoteSessionId;
    private long _lastPongTicks;

    /// <inheritdoc />
    public MultiplexerOptions Options => _options;

    /// <inheritdoc />
    public MultiplexerStats Stats => _stats;

    /// <inheritdoc />
    public bool IsConnected => _transport is not null && _isRunning;

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
    public event Action<string>? OnChannelOpened;
    /// <inheritdoc />
    public event Action<string, Exception?>? OnChannelClosed;
    /// <inheritdoc />
    public event Action<Exception>? OnError;
    /// <inheritdoc />
    public event Action<DisconnectReason, Exception?>? OnDisconnected;
    /// <inheritdoc />
    public event Action<int>? OnReconnecting;

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
    public async Task Start(CancellationToken ct = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Multiplexer is already running.");

        _transport = await _options.StreamFactory(ct);
        _isRunning = true;
        _stats._startTicks = Environment.TickCount64;

        // Perform handshake
        await PerformHandshakeAsync(ct);

        // Create control channel (channel 0) — used for ping/pong, GoAway, reconnect
        _controlChannel = new WriteChannel(
            "__control__",
            ChannelConstants.ControlChannel,
            ChannelPriority.Highest,
            FrameConstants.MinSlabSize,
            TimeSpan.FromSeconds(5),
            this);
        _controlChannel.MarkOpen();

        // Start the writer, flusher, and reader threads
        var linkedCt = _cts.Token;
        _writerTask = Task.Run(() => RunWriterLoopAsync(linkedCt), linkedCt);
        _flusherTask = Task.Factory.StartNew(
            () => RunFlusherLoop(linkedCt),
            linkedCt,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _readerTask = Task.Run(() => RunReaderLoopAsync(linkedCt), linkedCt);

        // Start keepalive if ping interval is configured
        if (_options.PingInterval > TimeSpan.Zero)
        {
            _lastPongTicks = Environment.TickCount64;
            _keepaliveTask = Task.Run(() => RunKeepaliveLoopAsync(linkedCt), linkedCt);
        }

        _readyTcs.TrySetResult();
    }

    /// <inheritdoc />
    public Task WaitForReadyAsync(CancellationToken ct = default) => _readyTcs.Task.WaitAsync(ct);

    /// <inheritdoc />
    public ValueTask<WriteChannel> OpenChannelAsync(string channelId, CancellationToken ct = default)
    {
        return OpenChannelAsync(new ChannelOptions
        {
            ChannelId = channelId,
            Priority = _options.DefaultChannelOptions.Priority,
            SlabSize = _options.DefaultChannelOptions.SlabSize,
            SendTimeout = _options.DefaultChannelOptions.SendTimeout,
        }, ct);
    }

    /// <inheritdoc />
    public ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken ct = default)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Multiplexer is not running.");

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
        channel.MarkOpen();

        Interlocked.Increment(ref _stats._openChannels);
        Interlocked.Increment(ref _stats._totalChannelsOpened);
        OnChannelOpened?.Invoke(options.ChannelId);

        return new ValueTask<WriteChannel>(channel);
    }

    /// <inheritdoc />
    public ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken ct = default)
    {
        return _registry.AcceptChannelAsync(channelId, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken ct = default)
    {
        return _registry.AcceptChannelsAsync(ct);
    }

    /// <inheritdoc />
    public WriteChannel? GetWriteChannel(string channelId) => _registry.GetWriteChannelById(channelId);

    /// <inheritdoc />
    public ReadChannel? GetReadChannel(string channelId) => _registry.GetReadChannelById(channelId);

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

        _disconnectReason = NetConduit.DisconnectReason.LocalDispose;
        _cts.Cancel();
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct = default)
    {
        _flushSignal.Signal();
        return ValueTask.CompletedTask;
    }

    // IFrameRouter — channels call this to signal they have ready frames
    void IFrameRouter.NotifyReady(WriteChannel channel)
    {
        lock (_readyLock)
        {
            if (!_readyChannels.Contains(channel))
                _readyChannels.Add(channel);
        }
        _readySignal.Signal();
    }

    // =====================================================================
    // Writer Thread — THE DUMB ROUTER (send side)
    // Picks ready channels, writes their pre-built frames to the stream.
    // Flush is handled by the separate flusher thread.
    // =====================================================================
    private async Task RunWriterLoopAsync(CancellationToken ct)
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

                    await writeStream.WriteAsync(frames, ct);
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
        catch (Exception ex)
        {
            HandleTransportError(ex);
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
        catch (Exception ex)
        {
            HandleTransportError(ex);
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            HandleTransportError(ex);
        }
        catch (Exception ex)
        {
            HandleTransportError(ex);
        }
    }

    private void DispatchToChannel(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.Flags == FrameFlags.Init)
        {
            if (payload.Length > ChannelConstants.MaxChannelIdLength)
                return; // channel name too long — drop frame

            // Remote side is opening a channel — create a ReadChannel
            string channelId = Encoding.UTF8.GetString(payload);
            var readChannel = new ReadChannel(
                channelId,
                header.ChannelIndex,
                _options.DefaultChannelOptions.Priority,
                _options.DefaultChannelOptions.SlabSize);

            _registry.RegisterReadChannel(header.ChannelIndex, readChannel);
            readChannel.MarkOpen();

            Interlocked.Increment(ref _stats._openChannels);
            Interlocked.Increment(ref _stats._totalChannelsOpened);
            OnChannelOpened?.Invoke(channelId);

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
            OnChannelClosed?.Invoke(channel.ChannelId, null);
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
        _disconnectReason = NetConduit.DisconnectReason.GoAwayReceived;
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
                        HandleTransportError(new IOException(
                            $"Keepalive timeout: {missedPings} missed pings (timeout: {_options.PingTimeout})"));
                        return;
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

    private void HandleTransportError(Exception ex)
    {
        if (!_isRunning) return;

        if (_isShuttingDown || _options.MaxAutoReconnectAttempts == 0)
        {
            // No reconnection — abort everything
            _isRunning = false;
            _disconnectReason = NetConduit.DisconnectReason.TransportError;
            _registry.AbortAllChannels(ChannelCloseReason.TransportFailed, ex);
            _registry.CancelAllPendingAccepts();
            OnDisconnected?.Invoke(NetConduit.DisconnectReason.TransportError, ex);
            OnError?.Invoke(ex);
            return;
        }

        // Reconnection enabled — attempt to re-establish transport
        OnError?.Invoke(ex);
        _ = Task.Run(() => AttemptReconnectAsync());
    }

    private async Task AttemptReconnectAsync()
    {
        int maxAttempts = _options.MaxAutoReconnectAttempts;
        double delay = _options.AutoReconnectDelay.TotalMilliseconds;
        double maxDelay = _options.MaxAutoReconnectDelay.TotalMilliseconds;

        for (int attempt = 1; maxAttempts == 0 || attempt <= maxAttempts; attempt++)
        {
            if (_cts.IsCancellationRequested) break;

            try
            {
                OnReconnecting?.Invoke(attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(delay), _cts.Token);

                // Get new transport from factory
                var newTransport = await _options.StreamFactory(_cts.Token);

                // Perform reconnect handshake (sends session ID with Reconnect subtype)
                await PerformReconnectHandshakeAsync(newTransport, _cts.Token);

                // Success — swap transport and restart loops
                if (_transport is not null)
                    await _transport.DisposeAsync();
                _transport = newTransport;

                // Tell all write channels to prepare replay (reset sentPos to ackedPos)
                foreach (var ch in _registry.GetAllWriteChannels())
                    ch.PrepareReplay();

                // Reset keepalive tracking
                _lastPongTicks = Environment.TickCount64;

                // Restart writer, flusher, and reader
                var linkedCt = _cts.Token;
                _writerTask = Task.Run(() => RunWriterLoopAsync(linkedCt), linkedCt);
                _flusherTask = Task.Factory.StartNew(
                    () => RunFlusherLoop(linkedCt),
                    linkedCt,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                _readerTask = Task.Run(() => RunReaderLoopAsync(linkedCt), linkedCt);

                if (_options.PingInterval > TimeSpan.Zero)
                    _keepaliveTask = Task.Run(() => RunKeepaliveLoopAsync(linkedCt), linkedCt);

                return; // reconnection succeeded
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception reconnectEx)
            {
                OnError?.Invoke(reconnectEx);
                delay = Math.Min(delay * _options.AutoReconnectBackoffMultiplier, maxDelay);
            }
        }

        // All reconnect attempts exhausted
        _isRunning = false;
        _disconnectReason = NetConduit.DisconnectReason.TransportError;
        _registry.AbortAllChannels(ChannelCloseReason.TransportFailed);
        _registry.CancelAllPendingAccepts();
        OnDisconnected?.Invoke(NetConduit.DisconnectReason.TransportError, null);
    }

    private async Task PerformReconnectHandshakeAsync(IStreamPair transport, CancellationToken ct)
    {
        // Send reconnect frame: [CtrlSubtype.Reconnect][sessionId:16B]
        byte[] reconnectPayload = new byte[1 + 16];
        reconnectPayload[0] = CtrlSubtype.Reconnect;
        _sessionId.TryWriteBytes(reconnectPayload.AsSpan(1));

        byte[] frame = new byte[FrameHeader.Size + reconnectPayload.Length];
        FrameHeader.WriteTo(frame, ChannelConstants.ControlChannel, FrameFlags.Ctrl, reconnectPayload.Length);
        reconnectPayload.CopyTo(frame.AsSpan(FrameHeader.Size));

        await transport.WriteStream.WriteAsync(frame, ct);
        await transport.WriteStream.FlushAsync(ct);

        // Read reconnect ack: [CtrlSubtype.ReconnectAck][sessionId:16B]
        byte[] headerBuf = new byte[FrameHeader.Size];
        await ReadExactAsync(transport.ReadStream, headerBuf, ct);
        var header = FrameHeader.Parse(headerBuf);

        if (header.Flags != FrameFlags.Ctrl || header.PayloadLength < 17)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Invalid reconnect ack.");

        byte[] ackPayload = new byte[header.PayloadLength];
        await ReadExactAsync(transport.ReadStream, ackPayload, ct);

        if (ackPayload[0] != CtrlSubtype.ReconnectAck)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Expected reconnect ack subtype.");

        var remoteSession = new Guid(ackPayload.AsSpan(1, 16));
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
        _disconnectReason ??= NetConduit.DisconnectReason.LocalDispose;

        _cts.Cancel();

        _registry.AbortAllChannels(ChannelCloseReason.MuxDisposed);
        _registry.CancelAllPendingAccepts();

        if (_writerTask is not null)
        {
            try { await _writerTask; }
            catch (OperationCanceledException) { }
            catch { /* swallow during dispose */ }
        }

        if (_flusherTask is not null)
        {
            try { await _flusherTask; }
            catch (OperationCanceledException) { }
            catch { /* swallow during dispose */ }
        }

        if (_readerTask is not null)
        {
            try { await _readerTask; }
            catch (OperationCanceledException) { }
            catch { /* swallow during dispose */ }
        }

        if (_keepaliveTask is not null)
        {
            try { await _keepaliveTask; }
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
            await _controlChannel.DisposeAsync();
            _controlChannel = null;
        }

        _readySignal.Dispose();
        _flushSignal.Dispose();
        _cts.Dispose();

        OnDisconnected?.Invoke(_disconnectReason.Value, null);
    }
}
