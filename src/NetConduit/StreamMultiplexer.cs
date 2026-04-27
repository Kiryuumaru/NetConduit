using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Exceptions;
using NetConduit.Internal;
using NetConduit.Models;
using ChannelOptions = NetConduit.Models.ChannelOptions;

namespace NetConduit;

/// <summary>
/// A transport-agnostic stream multiplexer that creates multiple virtual channels over a single connection.
/// </summary>
public sealed class StreamMultiplexer : IStreamMultiplexer, IFrameDispatcher
{
    // Components
    private readonly FrameWriter _frameWriter;
    private readonly FrameReader _frameReader;
    private readonly ChannelRegistry _channels;

    // Session & options
    private readonly Guid _sessionId;
    private readonly MultiplexerOptions _options;
    private Guid _remoteSessionId;

    // Connection state
    private IStreamPair? _streamPair;
    private Stream? _readStream;
    private Stream? _writeStream;
    private volatile bool _isConnected;
    private volatile bool _isReconnecting;
    private volatile int _currentConnectionAttempt;

    // Lifecycle
    private volatile bool _isRunning;
    private volatile bool _goAwaySent;
    private volatile bool _goAwayReceived;
    private bool _disposed;
    private readonly CancellationTokenSource _shutdownCts = new();

    // Coordination gates
    private TaskCompletionSource _handshakeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Reconnection
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly List<(uint ChannelIndex, byte[] Data)> _pendingReconnectBuffer = new();
    private TaskCompletionSource? _disconnectedTcs;
    private Task? _autoReconnectTask;
    private CancellationTokenSource? _autoReconnectCts;
    private DisconnectReason? _disconnectReason;
    private Exception? _disconnectException;

    // Background tasks
    private Task? _mainLoopTask;
    private Task? _readTask;
    private Task? _pingTask;
    private Task? _flushTask;
    private long _lastPingTimestamp;

    /// <summary>
    /// Creates a new stream multiplexer (no I/O). Use <see cref="Create"/> to create an instance.
    /// </summary>
    private StreamMultiplexer(MultiplexerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessionId = _options.SessionId ?? Guid.NewGuid();
        Stats = new MultiplexerStats();

        _frameWriter = new FrameWriter(_options, Stats);
        _frameReader = new FrameReader(_options, Stats);
        _channels = new ChannelRegistry();
    }

    /// <summary>
    /// Creates a new multiplexer. No I/O occurs until Start() is called.
    /// </summary>
    public static StreamMultiplexer Create(MultiplexerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new StreamMultiplexer(options);
    }

    // Expose writer for internal channel access (WriteChannel/ReadChannel)
    internal FrameWriter Writer => _frameWriter;

    /// <summary>Multiplexer options.</summary>
    public MultiplexerOptions Options => _options;

    /// <summary>The session identifier for this multiplexer.</summary>
    public Guid SessionId => _sessionId;

    /// <summary>The remote session identifier (available after handshake).</summary>
    public Guid RemoteSessionId => _remoteSessionId;

    /// <summary>Multiplexer statistics.</summary>
    public MultiplexerStats Stats { get; }

    /// <summary>Whether the multiplexer is running.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Whether a GOAWAY has been sent or received (graceful shutdown in progress).</summary>
    public bool IsShuttingDown => _goAwaySent || _goAwayReceived;

    /// <summary>Gets the IDs of all active channels (both opened and accepted).</summary>
    public IReadOnlyCollection<string> ActiveChannelIds => _channels.ActiveChannelIds;

    /// <summary>Gets the IDs of channels opened by this side.</summary>
    public IReadOnlyCollection<string> OpenedChannelIds => _channels.OpenedChannelIds;

    /// <summary>Gets the IDs of channels accepted from the remote side.</summary>
    public IReadOnlyCollection<string> AcceptedChannelIds => _channels.AcceptedChannelIds;

    /// <summary>Gets the count of active channels.</summary>
    public int ActiveChannelCount => _channels.ActiveChannelCount;

    /// <summary>Event raised when a channel is opened.</summary>
    public event Action<string>? OnChannelOpened;

    /// <summary>Event raised when a channel is closed.</summary>
    public event Action<string, Exception?>? OnChannelClosed;

    /// <summary>Event raised when an error occurs.</summary>
    public event Action<Exception>? OnError;

    /// <summary>Event raised when the connection is lost but reconnection is possible.</summary>
    public event Action<DisconnectReason, Exception?>? OnDisconnected;

    /// <summary>The reason for disconnection, if disconnected.</summary>
    public DisconnectReason? DisconnectReason => _disconnectReason;

    /// <summary>The exception associated with disconnection, if any.</summary>
    public Exception? DisconnectException => _disconnectException;

    /// <summary>Event raised when the connection is restored after a disconnection.</summary>
    public event Action? OnReconnected;

    /// <summary>Event raised during auto-reconnection attempts.</summary>
    public event Action<AutoReconnectEventArgs>? OnAutoReconnecting;

    /// <summary>Event raised when auto-reconnection has permanently failed after all attempts.</summary>
    public event Action<Exception>? OnAutoReconnectFailed;

    /// <summary>Whether the multiplexer is currently connected.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>Whether the multiplexer is currently attempting to reconnect.</summary>
    public bool IsReconnecting => _isReconnecting;

    /// <summary>Current connection attempt number (0 if connected, &gt;0 during connecting/reconnecting).</summary>
    public int CurrentConnectionAttempt => _currentConnectionAttempt;

    /// <summary>Event raised when the first successful connection + handshake completes.</summary>
    public event Action? OnReady;

    /// <summary>Waits for the handshake to complete.</summary>
    internal Task WaitForHandshakeAsync(CancellationToken cancellationToken = default)
        => _handshakeCompleted.Task.WaitAsync(cancellationToken);

    /// <summary>Waits until the multiplexer is ready (first connection + handshake complete).</summary>
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default)
        => _readyTcs.Task.WaitAsync(cancellationToken);

    #region IFrameDispatcher

    ReadChannel? IFrameDispatcher.LookupReadChannel(uint channelIndex)
        => _channels.GetReadChannelByIndex(channelIndex);

    void IFrameDispatcher.ProcessFrame(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => ProcessFrame(header, payload, ct);

    void IFrameDispatcher.OnReadLoopComplete()
        => _channels.CompleteAcceptChannel();

    void IFrameDispatcher.OnReadLoopError(Exception ex)
        => OnError?.Invoke(ex);

    #endregion

    #region Connection Lifecycle

    /// <summary>
    /// Starts the multiplexer background processing. No I/O occurs until this is called.
    /// </summary>
    public Task Start(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Start() has already been called. The multiplexer can only be started once.");

        _isRunning = true;
        _mainLoopTask = MainLoopAsync(cancellationToken);
        return _mainLoopTask;
    }

    private async Task MainLoopAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var ct = linkedCts.Token;

        try
        {
            await ConnectWithRetryAsync(isReconnecting: false, ct).ConfigureAwait(false);
            _readyTcs.TrySetResult();
            try { OnReady?.Invoke(); } catch { }
            await RunProcessingLoopsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdownCts.IsCancellationRequested)
        {
            _readyTcs.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            _readyTcs.TrySetException(ex);
            OnError?.Invoke(ex);
            throw;
        }
        finally
        {
            _isRunning = false;
            _isConnected = false;
        }
    }

    private async Task ConnectWithRetryAsync(bool isReconnecting, CancellationToken ct)
    {
        Exception? lastException = null;
        var currentDelay = _options.AutoReconnectDelay;
        var maxAttempts = _options.MaxAutoReconnectAttempts;
        var attempt = 0;

        _isReconnecting = isReconnecting;

        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                attempt++;
                _currentConnectionAttempt = attempt;

                if (maxAttempts > 0 && attempt > maxAttempts)
                {
                    var failEx = lastException != null
                        ? new MultiplexerException(ErrorCode.Internal, $"Connection failed after {maxAttempts} attempts.", lastException)
                        : new MultiplexerException(ErrorCode.Internal, $"Connection failed after {maxAttempts} attempts.");

                    if (isReconnecting)
                        _channels.AbortAllChannels(ChannelCloseReason.TransportFailed, failEx);

                    OnAutoReconnectFailed?.Invoke(failEx);
                    throw failEx;
                }

                var eventArgs = new AutoReconnectEventArgs
                {
                    AttemptNumber = attempt,
                    MaxAttempts = maxAttempts,
                    NextDelay = currentDelay,
                    LastException = lastException,
                    IsReconnecting = isReconnecting
                };

                try { OnAutoReconnecting?.Invoke(eventArgs); } catch { }

                if (eventArgs.Cancel)
                {
                    var cancelEx = new OperationCanceledException("Connection cancelled by event handler.");
                    if (isReconnecting)
                        _channels.AbortAllChannels(ChannelCloseReason.TransportFailed, cancelEx);
                    OnAutoReconnectFailed?.Invoke(cancelEx);
                    throw cancelEx;
                }

                if (attempt > 1)
                {
                    try
                    {
                        await Task.Delay(currentDelay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }

                try
                {
                    IStreamPair streamPair;
                    if (_options.ConnectionTimeout != Timeout.InfiniteTimeSpan)
                    {
                        using var connectionTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        connectionTimeoutCts.CancelAfter(_options.ConnectionTimeout);
                        try
                        {
                            streamPair = await _options.StreamFactory(connectionTimeoutCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new TimeoutException($"Connection timed out after {_options.ConnectionTimeout}");
                        }
                    }
                    else
                    {
                        streamPair = await _options.StreamFactory(ct).ConfigureAwait(false);
                    }

                    if (streamPair.ReadStream == null || streamPair.WriteStream == null)
                        throw new InvalidOperationException("StreamFactory returned null stream(s).");

                    _streamPair = streamPair;
                    _readStream = streamPair.ReadStream;
                    _writeStream = streamPair.WriteStream;
                    _frameWriter.EnsurePipeCreated();
                    if (isReconnecting)
                        _frameWriter.DrainStalePipeData();
                    _frameWriter.SetStream(_writeStream);
                    _frameWriter.ClearWriteError();
                    _frameReader.SetPipeReader(PipeReader.Create(_readStream, new StreamPipeReaderOptions(bufferSize: 1048576)));

                    // Set up the TCS before handshake so that if the remote's
                    // RECONNECT frame arrives during handshake reads, it is captured.
                    if (isReconnecting)
                        _syncPositionsTcs = new TaskCompletionSource<Dictionary<uint, long>>(TaskCreationOptions.RunContinuationsAsynchronously);

                    await PerformHandshakeAsync(ct).ConfigureAwait(false);

                    if (isReconnecting)
                    {
                        await WaitForReconnectPositionsAndReplayAsync(ct).ConfigureAwait(false);
                    }

                    _isConnected = true;
                    _currentConnectionAttempt = 0;
                    _disconnectReason = null;
                    _disconnectException = null;

                    if (isReconnecting)
                    {
                        try { OnReconnected?.Invoke(); } catch { }
                    }

                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    await DisposeStreamsAsync().ConfigureAwait(false);
                    var nextDelay = TimeSpan.FromMilliseconds(currentDelay.TotalMilliseconds * _options.AutoReconnectBackoffMultiplier);
                    currentDelay = nextDelay > _options.MaxAutoReconnectDelay ? _options.MaxAutoReconnectDelay : nextDelay;
                }
            }
        }
        finally
        {
            _isReconnecting = false;
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task PerformHandshakeAsync(CancellationToken ct)
    {
        _handshakeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendHandshakeAsync(ct).ConfigureAwait(false);

        if (_options.HandshakeTimeout != Timeout.InfiniteTimeSpan)
        {
            using var handshakeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeoutCts.CancelAfter(_options.HandshakeTimeout);

            var readTask = _frameReader.ReadSingleFrameAsync(this, handshakeTimeoutCts.Token);

            try
            {
                await Task.WhenAny(_handshakeCompleted.Task, readTask).ConfigureAwait(false);

                if (!_handshakeCompleted.Task.IsCompleted)
                {
                    while (!_handshakeCompleted.Task.IsCompleted && !handshakeTimeoutCts.IsCancellationRequested)
                    {
                        await _frameReader.ReadSingleFrameAsync(this, handshakeTimeoutCts.Token).ConfigureAwait(false);
                    }
                }

                await _handshakeCompleted.Task.WaitAsync(handshakeTimeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Handshake timed out after {_options.HandshakeTimeout}");
            }
        }
        else
        {
            while (!_handshakeCompleted.Task.IsCompleted && !ct.IsCancellationRequested)
            {
                await _frameReader.ReadSingleFrameAsync(this, ct).ConfigureAwait(false);
            }
            await _handshakeCompleted.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RunProcessingLoopsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedToken = linkedCts.Token;

            try
            {
                _readTask = _frameReader.RunReadLoopAsync(this, linkedToken);
                _pingTask = PingLoopAsync(linkedToken);
                _flushTask = _frameWriter.RunFlushLoopAsync(linkedToken);

                var tasks = new[] { _readTask, _pingTask, _flushTask };
                var completedTask = await Task.WhenAny(tasks).ConfigureAwait(false);
                await linkedCts.CancelAsync().ConfigureAwait(false);

                Exception? firstException = null;
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    firstException = ex;
                }

                if (completedTask.IsFaulted)
                    throw completedTask.Exception!.InnerException ?? completedTask.Exception;

                if (firstException != null)
                    throw firstException;

                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_disposed && !_goAwayReceived)
                {
                    _isConnected = false;
                    _frameWriter.ClearWriteError();
                    _disconnectReason = Enums.DisconnectReason.TransportError;
                    _disconnectException = ex;


                    try { OnDisconnected?.Invoke(Enums.DisconnectReason.TransportError, ex); } catch { }

                    await DisposeStreamsAsync().ConfigureAwait(false);
                    await ConnectWithRetryAsync(isReconnecting: true, ct).ConfigureAwait(false);
                    _frameWriter.SetPendingFlush();
                    continue;
                }
                else
                {
                    HandleTransportError(ex);
                    throw;
                }
            }
        }
    }

    private async Task DisposeStreamsAsync()
    {
        try { await _frameReader.CompletePipeReaderAsync().ConfigureAwait(false); } catch { }
        try { if (_streamPair != null) await _streamPair.DisposeAsync().ConfigureAwait(false); } catch { }
        _streamPair = null;
        _readStream = null;
        _writeStream = null;
    }

    private async Task WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Multiplexer is not running. Call Start() first.");

        if (_isConnected)
            return;

        await _readyTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        while (_isReconnecting && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            if (_isConnected)
                return;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    #endregion

    #region Protocol Encoding (Send Methods)

    internal void SendDataFrame(uint channelIndex, ReadOnlyMemory<byte> data, ChannelPriority priority, CancellationToken ct)
    {
        var header = new FrameHeader(channelIndex, FrameFlags.Data, (uint)data.Length);
        _frameWriter.WriteFrame(header, data, priority >= ChannelPriority.High, ct);
    }

    private async ValueTask SendHandshakeAsync(CancellationToken ct)
    {
        var payload = new byte[25];
        payload[0] = (byte)ControlSubtype.Handshake;
        _sessionId.TryWriteBytes(payload.AsSpan(1));

        Span<byte> nonceBytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonceBytes);
        _channels.LocalNonce = BinaryPrimitives.ReadInt64BigEndian(nonceBytes);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(17), _channels.LocalNonce);

        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        var frameLen = FrameHeader.Size + payload.Length;
        var buffer = new byte[frameLen];
        header.Write(buffer);
        payload.CopyTo(buffer.AsSpan(FrameHeader.Size));

        var writeStream = _writeStream ?? throw new InvalidOperationException("Not connected.");
        await writeStream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await writeStream.FlushAsync(ct).ConfigureAwait(false);
        Stats.AddBytesSent(frameLen);
    }

    private void SendInit(uint channelIndex, string channelId, ChannelPriority priority, CancellationToken ct)
    {
        var channelIdBytes = System.Text.Encoding.UTF8.GetBytes(channelId);
        var payload = new byte[1 + 2 + channelIdBytes.Length];
        payload[0] = (byte)priority;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)channelIdBytes.Length);
        channelIdBytes.CopyTo(payload.AsSpan(3));

        var header = new FrameHeader(channelIndex, FrameFlags.Init, (uint)payload.Length);
        _frameWriter.WriteFrameDirect(header, payload, ct);
        _frameWriter.SignalFlush();
    }

    internal void SendFin(uint channelIndex, CancellationToken ct)
    {
        var header = new FrameHeader(channelIndex, FrameFlags.Fin, 0);
        _frameWriter.WriteFrameDirect(header, ReadOnlyMemory<byte>.Empty, ct);
        _frameWriter.SignalFlush();
    }

    private void SendAck(uint channelIndex, uint credits, CancellationToken ct)
    {
        const int payloadLen = 4;
        var payload = new byte[payloadLen];
        BinaryPrimitives.WriteUInt32BigEndian(payload, credits);
        var header = new FrameHeader(channelIndex, FrameFlags.Ack, payloadLen);
        _frameWriter.WriteFrame(header, payload, forceFlush: true, ct);
    }

    internal void SendCreditGrant(uint channelIndex, uint credits, CancellationToken ct)
    {
        var payload = new byte[9];
        payload[0] = (byte)ControlSubtype.CreditGrant;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), channelIndex);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), credits);
        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, 9);
        _frameWriter.WriteFrame(header, payload, forceFlush: false, ct);
    }

    private void SendPing(CancellationToken ct)
    {
        var payload = new byte[9];
        payload[0] = (byte)ControlSubtype.Ping;
        _lastPingTimestamp = DateTime.UtcNow.Ticks;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), _lastPingTimestamp);
        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, 9);
        _frameWriter.WriteFrame(header, payload, forceFlush: false, ct);
    }

    private void SendPong(long timestamp, CancellationToken ct)
    {
        var payload = new byte[9];
        payload[0] = (byte)ControlSubtype.Pong;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), timestamp);
        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, 9);
        _frameWriter.WriteFrame(header, payload, forceFlush: false, ct);
    }

    private async ValueTask SendGoAwayAsync(ErrorCode code, CancellationToken ct)
    {
        var nextIdx = _channels.NextChannelIndex;
        uint lastChannelIndex;
        if (!_channels.IndexSpaceDetermined || nextIdx <= 2)
            lastChannelIndex = 0;
        else
            lastChannelIndex = nextIdx - 2;

        var payload = new byte[7];
        payload[0] = (byte)ControlSubtype.GoAway;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)code);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(3), lastChannelIndex);

        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        _frameWriter.WriteFrameDirect(header, payload, ct);
        await _frameWriter.ForceFlushAsync(ct).ConfigureAwait(false);
    }

    private void SendError(uint channelIndex, ErrorCode code, string message, CancellationToken ct)
    {
        var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var payload = new byte[2 + messageBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)code);
        messageBytes.CopyTo(payload.AsSpan(2));

        var header = new FrameHeader(channelIndex, FrameFlags.Err, (uint)payload.Length);
        _frameWriter.WriteFrameDirect(header, payload, ct);
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PingInterval, ct).ConfigureAwait(false);
                SendPing(ct);
                await Task.Delay(_options.PingTimeout, ct).ConfigureAwait(false);

                if (Stats.MissedPings > 0)
                {
                    Stats.IncrementMissedPings();
                    if (Stats.MissedPings >= _options.MaxMissedPings)
                    {
                        await GoAwayAsync(ct).ConfigureAwait(false);
                        break;
                    }
                }
                else
                {
                    Stats.IncrementMissedPings();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    #endregion

    #region Frame Processing

    private void ProcessFrame(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (header.ChannelId == ChannelIndexLimits.ControlChannel)
        {
            ProcessControlFrame(header, payload, ct);
            return;
        }

        var channelIndex = header.ChannelId;
        switch (header.Flags)
        {
            case FrameFlags.Data:
                ProcessDataFrame(channelIndex, payload);
                break;
            case FrameFlags.Init:
                ProcessInitFrame(channelIndex, payload, ct);
                break;
            case FrameFlags.Fin:
                ProcessFinFrame(channelIndex);
                break;
            case FrameFlags.Ack:
                ProcessAckFrame(channelIndex, payload);
                break;
            case FrameFlags.Err:
                ProcessErrorFrame(channelIndex, payload);
                break;
            default:
                throw new MultiplexerException(ErrorCode.ProtocolError, $"Unknown frame flag: {header.Flags}");
        }
    }

    private void ProcessControlFrame(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length < 1)
            return;

        var subtype = (ControlSubtype)payload.Span[0];
        var data = payload[1..];

        switch (subtype)
        {
            case ControlSubtype.Handshake:
                if (data.Length >= 16)
                    _remoteSessionId = new Guid(data.Span[..16]);
                if (data.Length >= 24)
                    _channels.RemoteNonce = BinaryPrimitives.ReadInt64BigEndian(data.Span[16..24]);
                _channels.DetermineIndexSpace(_sessionId, _remoteSessionId);
                _handshakeCompleted.TrySetResult();
                break;

            case ControlSubtype.Ping:
                if (data.Length >= 8)
                {
                    var timestamp = BinaryPrimitives.ReadInt64BigEndian(data.Span);
                    SendPong(timestamp, ct);
                }
                break;

            case ControlSubtype.Pong:
                if (data.Length >= 8)
                {
                    var timestamp = BinaryPrimitives.ReadInt64BigEndian(data.Span);
                    if (timestamp == _lastPingTimestamp)
                    {
                        var rtt = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - timestamp);
                        Stats.SetLastPingRtt(rtt);
                        Stats.ResetMissedPings();
                    }
                }
                break;

            case ControlSubtype.CreditGrant:
                if (data.Length >= 8)
                {
                    var channelIndex = BinaryPrimitives.ReadUInt32BigEndian(data.Span);
                    var credits = BinaryPrimitives.ReadUInt32BigEndian(data.Span[4..]);
                    _channels.GetWriteChannelByIndex(channelIndex)?.GrantCredits(credits);
                }
                break;

            case ControlSubtype.GoAway:
                _goAwayReceived = true;
                _channels.CompleteAcceptChannel();
                if (!_disconnectReason.HasValue)
                {
                    _disconnectReason = Enums.DisconnectReason.GoAwayReceived;
                    _channels.AbortAllChannels(ChannelCloseReason.MuxDisposed, null);
                    try { OnDisconnected?.Invoke(Enums.DisconnectReason.GoAwayReceived, null); } catch { }
                }
                break;

            case ControlSubtype.Error:
                if (data.Length >= 2)
                {
                    var code = (ErrorCode)BinaryPrimitives.ReadUInt16BigEndian(data.Span);
                    var message = data.Length > 2
                        ? System.Text.Encoding.UTF8.GetString(data.Span[2..])
                        : string.Empty;
                    OnError?.Invoke(new MultiplexerException(code, message));
                }
                break;

            case ControlSubtype.Reconnect:
                ProcessReconnectFrame(data, ct);
                break;

            case ControlSubtype.ReconnectAck:
                ProcessReconnectAckFrame(data, ct);
                break;
        }
    }

    private void ProcessDataFrame(uint channelIndex, ReadOnlyMemory<byte> payload)
    {
        var channel = _channels.GetReadChannelByIndex(channelIndex);
        if (channel != null)
        {
            var seq = new System.Buffers.ReadOnlySequence<byte>(payload);
            channel.EnqueueData(seq);
        }
    }

    private void ProcessInitFrame(uint channelIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_goAwayReceived || _goAwaySent)
        {
            SendError(channelIndex, ErrorCode.Refused, "GOAWAY received", ct);
            return;
        }

        if (payload.Length < 3)
        {
            SendError(channelIndex, ErrorCode.ProtocolError, "Invalid INIT payload", ct);
            return;
        }

        var priority = (ChannelPriority)payload.Span[0];
        var idLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Span[1..]);

        if (payload.Length < 3 + idLength)
        {
            SendError(channelIndex, ErrorCode.ProtocolError, "Invalid INIT payload length", ct);
            return;
        }

        var channelId = System.Text.Encoding.UTF8.GetString(payload.Span.Slice(3, idLength));

        if (_channels.ContainsReadChannelById(channelId))
        {
            SendError(channelIndex, ErrorCode.ChannelExists, $"Channel '{channelId}' already exists", ct);
            return;
        }

        var channelOptions = new ChannelOptions
        {
            ChannelId = channelId,
            Priority = priority,
            MinCredits = _options.DefaultChannelOptions.MinCredits,
            MaxCredits = _options.DefaultChannelOptions.MaxCredits,
            SendTimeout = _options.DefaultChannelOptions.SendTimeout
        };

        var channel = new ReadChannel(this, channelIndex, channelId, priority, channelOptions);

        if (!_channels.TryAddReadChannel(channelIndex, channel))
        {
            SendError(channelIndex, ErrorCode.ChannelExists, "Channel index already exists", ct);
            return;
        }

        if (!_channels.TryAddReadChannelById(channelId, channel))
        {
            _channels.RemoveReadChannel(channelIndex, channelId);
            SendError(channelIndex, ErrorCode.ChannelExists, $"Channel '{channelId}' already exists", ct);
            return;
        }

        Stats.IncrementTotalChannelsOpened();
        Stats.IncrementOpenChannels();

        SendAck(channelIndex, channel.GetInitialCredits(), ct);

        if (_channels.TryRemovePendingAccept(channelId, out var tcs) && tcs != null)
        {
            tcs.TrySetResult(channel);
        }
        else
        {
            _channels.WriteAcceptChannel(channel);
        }

        OnChannelOpened?.Invoke(channelId);
    }

    private void ProcessFinFrame(uint channelIndex)
    {
        _channels.GetReadChannelByIndex(channelIndex)?.SetClosed();
    }

    private void ProcessAckFrame(uint channelIndex, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 4)
            return;
        var credits = BinaryPrimitives.ReadUInt32BigEndian(payload.Span);
        _channels.GetWriteChannelByIndex(channelIndex)?.SetOpen(credits);
    }

    private void ProcessErrorFrame(uint channelIndex, ReadOnlyMemory<byte> payload)
    {
        var code = payload.Length >= 2
            ? (ErrorCode)BinaryPrimitives.ReadUInt16BigEndian(payload.Span)
            : ErrorCode.Internal;
        var message = payload.Length > 2
            ? System.Text.Encoding.UTF8.GetString(payload.Span[2..])
            : string.Empty;

        _channels.GetWriteChannelByIndex(channelIndex)?.SetError(code, message);
        _channels.GetReadChannelByIndex(channelIndex)?.SetError(code, message);
    }

    #endregion

    #region Channel Operations

    /// <summary>Opens a new channel for writing with default options.</summary>
    public ValueTask<WriteChannel> OpenChannelAsync(string channelId, CancellationToken cancellationToken = default)
        => OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cancellationToken);

    /// <summary>Opens a new channel for writing with custom options.</summary>
    public async ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (_goAwaySent || _goAwayReceived)
            throw new InvalidOperationException("Cannot open new channels after GOAWAY.");

        await _handshakeCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        var channelIdBytes = System.Text.Encoding.UTF8.GetByteCount(options.ChannelId);
        if (channelIdBytes > ChannelIdLimits.MaxLength)
            throw new ArgumentException($"ChannelId exceeds maximum length of {ChannelIdLimits.MaxLength} bytes (was {channelIdBytes} bytes).", nameof(options));

        if (_channels.ContainsWriteChannelById(options.ChannelId))
            throw new InvalidOperationException($"A channel with ChannelId '{options.ChannelId}' already exists.");

        var channelIndex = _channels.AllocateChannelIndex();
        var channel = new WriteChannel(this, channelIndex, options.ChannelId, options.Priority, options);

        if (!_channels.TryAddWriteChannel(channelIndex, channel))
            throw new MultiplexerException(ErrorCode.ChannelExists, $"Channel index {channelIndex} already exists.");

        if (!_channels.TryAddWriteChannelById(options.ChannelId, channel))
        {
            _channels.RemoveWriteChannel(channelIndex, options.ChannelId);
            throw new InvalidOperationException($"A channel with ChannelId '{options.ChannelId}' already exists.");
        }

        Stats.IncrementTotalChannelsOpened();
        Stats.IncrementOpenChannels();

        SendInit(channelIndex, options.ChannelId, options.Priority, cancellationToken);

        var timeout = TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await channel.WaitForOpenAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _channels.RemoveWriteChannel(channelIndex, options.ChannelId);
            Stats.DecrementOpenChannels();
            throw new TimeoutException($"Channel open timed out after {timeout}");
        }
        catch (MultiplexerException)
        {
            _channels.RemoveWriteChannel(channelIndex, options.ChannelId);
            Stats.DecrementOpenChannels();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _channels.RemoveWriteChannel(channelIndex, options.ChannelId);
            Stats.DecrementOpenChannels();
            throw new MultiplexerException(ErrorCode.Refused, "Channel open was refused.");
        }

        OnChannelOpened?.Invoke(options.ChannelId);
        return channel;
    }

    /// <summary>Accepts incoming channels from the remote side.</summary>
    public async IAsyncEnumerable<ReadChannel> AcceptChannelsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var channel in _channels.ReadAllAcceptedAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return channel;
        }
    }

    /// <summary>Accepts a specific channel by its ChannelId.</summary>
    public async ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelId);

        var existing = _channels.GetReadChannelById(channelId);
        if (existing != null)
            return existing;

        var tcs = new TaskCompletionSource<ReadChannel>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_channels.TryAddPendingAccept(channelId, tcs))
        {
            if (_channels.TryGetPendingAccept(channelId, out var existingTcs) && existingTcs != null)
                return await existingTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            existing = _channels.GetReadChannelById(channelId);
            if (existing != null)
                return existing;
        }

        try
        {
            existing = _channels.GetReadChannelById(channelId);
            if (existing != null)
            {
                _channels.TryRemovePendingAccept(channelId, out _);
                return existing;
            }

            await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _channels.TryRemovePendingAccept(channelId, out _);
        }
    }

    /// <summary>Gets a write channel by its ChannelId.</summary>
    public WriteChannel? GetWriteChannel(string channelId)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        return _channels.GetWriteChannelById(channelId);
    }

    /// <summary>Gets a read channel by its ChannelId.</summary>
    public ReadChannel? GetReadChannel(string channelId)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        return _channels.GetReadChannelById(channelId);
    }

    /// <summary>Initiates graceful shutdown.</summary>
    public async ValueTask GoAwayAsync(CancellationToken cancellationToken = default)
    {
        if (_goAwaySent) return;
        _goAwaySent = true;
        await SendGoAwayAsync(ErrorCode.None, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = new CancellationTokenSource(_options.GoAwayTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            while (_channels.WriteChannelCount > 0 || _channels.ReadChannelCount > 0)
            {
                await Task.Delay(100, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout reached
        }

        _shutdownCts.Cancel();
    }

    /// <summary>Triggers an immediate flush of buffered data when using Manual flush mode.</summary>
    public void Flush()
    {
        if (_options.FlushMode != FlushMode.Manual)
            throw new InvalidOperationException("Flush() can only be called when FlushMode is Manual.");
        _frameWriter.SignalFlush();
    }

    internal void OnWriteChannelDisposed(uint channelIndex, string channelId)
    {
        _channels.RemoveWriteChannel(channelIndex, channelId);
        Stats.DecrementOpenChannels();
        Stats.IncrementTotalChannelsClosed();
        OnChannelClosed?.Invoke(channelId, null);
    }

    internal void OnReadChannelDisposed(uint channelIndex, string channelId)
    {
        _channels.RemoveReadChannel(channelIndex, channelId);
        Stats.DecrementOpenChannels();
        Stats.IncrementTotalChannelsClosed();
        OnChannelClosed?.Invoke(channelId, null);
    }

    #endregion

    #region Reconnection

    /// <summary>Reconnects the multiplexer with new streams after a disconnection.</summary>
    public async Task ReconnectAsync(Stream newReadStream, Stream newWriteStream, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamMultiplexer));

        await _reconnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _isReconnecting = true;

            await SendReconnectAsync(newWriteStream, cancellationToken).ConfigureAwait(false);
            var positions = await ReceiveReconnectAckAsync(newReadStream, cancellationToken).ConfigureAwait(false);

            foreach (var (channelIndex, bytesReceived) in positions)
            {
                _channels.GetWriteChannelByIndex(channelIndex)?.SyncState.SetBytesAcked(bytesReceived);
            }

            ReplayUnacknowledgedData(positions, cancellationToken);

            _isConnected = true;
            _isReconnecting = false;
            OnReconnected?.Invoke();
        }
        finally
        {
            _isReconnecting = false;
            _reconnectLock.Release();
        }
    }

    /// <summary>Signals that the connection has been lost.</summary>
    public void NotifyDisconnected()
    {
        if (_disposed)
            return;

        _isConnected = false;
        _disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectReason = Enums.DisconnectReason.TransportError;

        try { OnDisconnected?.Invoke(Enums.DisconnectReason.TransportError, null); } catch { }

        if (_options.StreamFactory != null)
        {
            _autoReconnectCts = new CancellationTokenSource();
            _autoReconnectTask = Task.Run(() => AutoReconnectLoopAsync(
                new Exception("Connection manually disconnected"),
                _autoReconnectCts.Token));
        }
    }

    private void HandleTransportError(Exception ex)
    {
        if (_disconnectReason.HasValue)
            return;

        _disconnectReason = Enums.DisconnectReason.TransportError;
        _disconnectException = ex;
        _isConnected = false;

        if (_options.StreamFactory != null && !_disposed)
        {
            try { OnDisconnected?.Invoke(Enums.DisconnectReason.TransportError, ex); } catch { }
            _autoReconnectCts = new CancellationTokenSource();
            _autoReconnectTask = Task.Run(() => AutoReconnectLoopAsync(ex, _autoReconnectCts.Token));
            return;
        }

        _channels.AbortAllChannels(ChannelCloseReason.TransportFailed, ex);
        try { OnDisconnected?.Invoke(Enums.DisconnectReason.TransportError, ex); } catch { }
    }

    private async Task AutoReconnectLoopAsync(Exception initialException, CancellationToken ct)
    {
        var lastException = initialException;
        var currentDelay = _options.AutoReconnectDelay;
        var maxAttempts = _options.MaxAutoReconnectAttempts;
        var attempt = 0;

        _isReconnecting = true;

        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                attempt++;

                if (maxAttempts > 0 && attempt > maxAttempts)
                {
                    var failEx = new MultiplexerException(ErrorCode.Internal,
                        $"Auto-reconnection failed after {maxAttempts} attempts.", lastException);
                    _channels.AbortAllChannels(ChannelCloseReason.TransportFailed, failEx);
                    OnAutoReconnectFailed?.Invoke(failEx);
                    return;
                }

                var eventArgs = new AutoReconnectEventArgs
                {
                    AttemptNumber = attempt,
                    MaxAttempts = maxAttempts,
                    NextDelay = currentDelay,
                    LastException = lastException
                };

                try { OnAutoReconnecting?.Invoke(eventArgs); } catch { }

                if (eventArgs.Cancel)
                {
                    var cancelEx = new OperationCanceledException("Auto-reconnection cancelled by event handler.");
                    _channels.AbortAllChannels(ChannelCloseReason.TransportFailed, cancelEx);
                    OnAutoReconnectFailed?.Invoke(cancelEx);
                    return;
                }

                if (attempt > 1)
                {
                    try
                    {
                        await Task.Delay(currentDelay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                try
                {
                    var newStreamPair = await _options.StreamFactory!(ct).ConfigureAwait(false);
                    await ReconnectAsync(newStreamPair.ReadStream, newStreamPair.WriteStream, ct).ConfigureAwait(false);
                    _streamPair = newStreamPair;
                    _disconnectReason = null;
                    _disconnectException = null;
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    var nextDelay = TimeSpan.FromMilliseconds(currentDelay.TotalMilliseconds * _options.AutoReconnectBackoffMultiplier);
                    currentDelay = nextDelay > _options.MaxAutoReconnectDelay ? _options.MaxAutoReconnectDelay : nextDelay;
                }
            }
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    private async Task SendReconnectAsync(Stream writeStream, CancellationToken ct)
    {
        var readChannels = _channels.ReadChannelEntries.ToArray();
        var payloadSize = 1 + 16 + 4 + (readChannels.Length * 12);
        var payload = new byte[payloadSize];

        payload[0] = (byte)ControlSubtype.Reconnect;
        _sessionId.TryWriteBytes(payload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17), (uint)readChannels.Length);

        var offset = 21;
        foreach (var kvp in readChannels)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset), kvp.Key);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset + 4), kvp.Value.SyncState.BytesReceived);
            offset += 12;
        }

        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        var headerBytes = new byte[FrameHeader.Size];
        header.Write(headerBytes);

        await writeStream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await writeStream.WriteAsync(payload, ct).ConfigureAwait(false);
        await writeStream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<uint, long>> ReceiveReconnectAckAsync(Stream readStream, CancellationToken ct)
    {
        var headerBytes = new byte[FrameHeader.Size];
        await readStream.ReadExactlyAsync(headerBytes, ct).ConfigureAwait(false);
        var header = FrameHeader.Read(headerBytes);

        if (header.ChannelId != ChannelIndexLimits.ControlChannel)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Expected control frame for RECONNECT_ACK.");

        if (header.Length > _options.MaxFrameSize)
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Reconnect ACK frame length {header.Length} exceeds MaxFrameSize {_options.MaxFrameSize}.");

        var payload = new byte[header.Length];
        await readStream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        if (payload[0] != (byte)ControlSubtype.ReconnectAck)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Expected RECONNECT_ACK.");

        var remoteSessionId = new Guid(payload.AsSpan(1, 16));
        if (remoteSessionId != _remoteSessionId)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Session ID mismatch during reconnection.");

        var receivePositions = new Dictionary<uint, long>();
        if (payload.Length < 21)
            throw new MultiplexerException(ErrorCode.ProtocolError, "RECONNECT_ACK payload too small.");

        var channelCount = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(17));
        var readOffset = 21;

        for (int i = 0; i < channelCount && readOffset + 12 <= payload.Length; i++)
        {
            var channelIndex = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(readOffset));
            var bytesReceived = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(readOffset + 4));
            receivePositions[channelIndex] = bytesReceived;
            readOffset += 12;
        }

        return receivePositions;
    }

    private void ReplayUnacknowledgedData(Dictionary<uint, long> positions, CancellationToken ct)
    {
        foreach (var (channelIndex, bytesReceived) in positions)
        {
            var writeChannel = _channels.GetWriteChannelByIndex(channelIndex);
            if (writeChannel is null)
                continue;

            var unacked = writeChannel.SyncState.GetUnacknowledgedDataFrom(bytesReceived);
            if (unacked.Length == 0)
                continue;

            ReadOnlyMemory<byte> unackedMemory = unacked;
            var maxFrame = _options.MaxFrameSize;
            var offset = 0;
            while (offset < unackedMemory.Length)
            {
                var chunkSize = Math.Min(unackedMemory.Length - offset, maxFrame);
                SendDataFrame(channelIndex, unackedMemory.Slice(offset, chunkSize), writeChannel.Priority, ct);
                offset += chunkSize;
            }
        }
    }

    private TaskCompletionSource<Dictionary<uint, long>>? _syncPositionsTcs;

    private async Task WaitForReconnectPositionsAndReplayAsync(CancellationToken ct)
    {
        // _syncPositionsTcs was set before PerformHandshakeAsync so that
        // early RECONNECT frames arriving during handshake reads are captured.
        // Send our own RECONNECT directly to the stream.
        var writeStream = _writeStream ?? throw new InvalidOperationException("Write stream not set after reconnection");
        await SendReconnectAsync(writeStream, ct).ConfigureAwait(false);

        // Read frames until we get the RECONNECT from the other side.
        // If it already arrived during handshake, the TCS is already completed.
        while (!_syncPositionsTcs!.Task.IsCompleted)
        {
            await _frameReader.ReadSingleFrameAsync(this, ct).ConfigureAwait(false);
        }

        var positions = await _syncPositionsTcs.Task.ConfigureAwait(false);
        _syncPositionsTcs = null;

        // Write replay data directly to the stream (bypasses FrameWriter pipe)
        // to guarantee ordering before the flush loop drains any queued data.
        await ReplayUnacknowledgedDataToStreamAsync(positions, writeStream, ct).ConfigureAwait(false);
    }

    private async Task ReplayUnacknowledgedDataToStreamAsync(
        Dictionary<uint, long> positions, Stream writeStream, CancellationToken ct)
    {
        foreach (var (channelIndex, bytesReceived) in positions)
        {
            var writeChannel = _channels.GetWriteChannelByIndex(channelIndex);
            if (writeChannel is null)
                continue;

            var unacked = writeChannel.SyncState.GetUnacknowledgedDataFrom(bytesReceived);
            if (unacked.Length == 0)
                continue;

            ReadOnlyMemory<byte> unackedMemory = unacked;
            var maxFrame = _options.MaxFrameSize;
            var offset = 0;
            while (offset < unackedMemory.Length)
            {
                var chunkSize = Math.Min(unackedMemory.Length - offset, maxFrame);
                var header = new FrameHeader(channelIndex, FrameFlags.Data, (uint)chunkSize);
                var headerBytes = new byte[FrameHeader.Size];
                header.Write(headerBytes);
                await writeStream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
                await writeStream.WriteAsync(unackedMemory.Slice(offset, chunkSize), ct).ConfigureAwait(false);
                offset += chunkSize;
            }
        }

        await writeStream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void ProcessReconnectAckFrame(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.Length < 20)
        {
            return;
        }

        var channelCount = BinaryPrimitives.ReadUInt32BigEndian(data.Span[16..]);
        var expectedSize = 20 + (long)channelCount * 12;
        if (expectedSize > data.Length)
            return;

        var positions = new Dictionary<uint, long>((int)channelCount);
        var readOffset = 20;
        for (int i = 0; i < channelCount; i++)
        {
            var channelIndex = BinaryPrimitives.ReadUInt32BigEndian(data.Span[readOffset..]);
            var bytesReceived = BinaryPrimitives.ReadInt64BigEndian(data.Span[(readOffset + 4)..]);
            _channels.GetWriteChannelByIndex(channelIndex)?.SyncState.SetBytesAcked(bytesReceived);
            positions[channelIndex] = bytesReceived;
            readOffset += 12;
        }

        // During reconnection sync: if the other side was faster, it already
        // entered the normal read loop and responded with ACK instead of RECONNECT.
        // Complete the TCS so the reconnection can proceed.
        if (_syncPositionsTcs is { } tcs)
        {
            tcs.TrySetResult(positions);
            return;
        }

        ReplayUnacknowledgedData(positions, ct);
    }

    private void ProcessReconnectFrame(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.Length < 20)
        {
            SendError(0, ErrorCode.ProtocolError, "Invalid RECONNECT payload", ct);
            return;
        }

        var incomingSessionId = new Guid(data.Span[..16]);
        var channelCount = BinaryPrimitives.ReadUInt32BigEndian(data.Span[16..]);

        var expectedSize = 20 + (long)channelCount * 12;
        if (expectedSize > data.Length)
        {
            SendError(0, ErrorCode.ProtocolError, "RECONNECT payload too short for declared channel count", ct);
            return;
        }

        if (incomingSessionId != _remoteSessionId && _remoteSessionId != Guid.Empty)
        {
            SendError(0, ErrorCode.SessionMismatch, "Session ID mismatch", ct);
            return;
        }

        var dataOffset = 20;
        var positions = new Dictionary<uint, long>((int)channelCount);
        for (int i = 0; i < channelCount; i++)
        {
            var channelIndex = BinaryPrimitives.ReadUInt32BigEndian(data.Span[dataOffset..]);
            var bytesReceived = BinaryPrimitives.ReadInt64BigEndian(data.Span[(dataOffset + 4)..]);
            positions[channelIndex] = bytesReceived;
            dataOffset += 12;
        }

        // During reconnection sync: capture positions for the caller, skip ACK/replay
        if (_syncPositionsTcs is { } tcs)
        {
            foreach (var (channelIndex, bytesReceived) in positions)
                _channels.GetWriteChannelByIndex(channelIndex)?.SyncState.SetBytesAcked(bytesReceived);

            tcs.TrySetResult(positions);
            return;
        }

        // Normal read-loop path: SetBytesAcked, send ACK, replay
        foreach (var (channelIndex, bytesReceived) in positions)
            _channels.GetWriteChannelByIndex(channelIndex)?.SyncState.SetBytesAcked(bytesReceived);

        var readChannels = _channels.ReadChannelEntries.ToArray();
        var payloadSize = 1 + 16 + 4 + (readChannels.Length * 12);
        var payload = new byte[payloadSize];

        payload[0] = (byte)ControlSubtype.ReconnectAck;
        _sessionId.TryWriteBytes(payload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17), (uint)readChannels.Length);

        var payloadOffset = 21;
        foreach (var kvp in readChannels)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(payloadOffset), kvp.Key);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(payloadOffset + 4), kvp.Value.SyncState.BytesReceived);
            payloadOffset += 12;
        }

        var frameHeader = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        _frameWriter.WriteFrameDirect(frameHeader, payload, ct);

        ReplayUnacknowledgedData(positions, ct);
    }

    #endregion

    #region Disposal

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_isRunning && !_goAwaySent)
            {
                try
                {
                    using var goAwayTimeoutCts = new CancellationTokenSource(_options.GracefulShutdownTimeout);
                    await SendGoAwayAsync(ErrorCode.None, goAwayTimeoutCts.Token).ConfigureAwait(false);
                    _goAwaySent = true;
                }
                catch { }
            }

            _channels.AbortAllChannels(ChannelCloseReason.MuxDisposed, null);

            try
            {
                await _channels.DisposeAllChannelsAsync(_options.GracefulShutdownTimeout).ConfigureAwait(false);
            }
            catch { }

            if (!_disconnectReason.HasValue)
            {
                _disconnectReason = Enums.DisconnectReason.LocalDispose;
                try { OnDisconnected?.Invoke(Enums.DisconnectReason.LocalDispose, null); } catch { }
            }

            _shutdownCts.Cancel();
            _channels.CompleteAcceptChannel();

            try
            {
                _autoReconnectCts?.Cancel();
                if (_autoReconnectTask != null)
                    await Task.WhenAny(_autoReconnectTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            }
            catch { }

            _channels.CancelPendingAccepts();

            try
            {
                var tasks = new List<Task>();
                if (_readTask != null) tasks.Add(_readTask);
                if (_pingTask != null) tasks.Add(_pingTask);
                if (_flushTask != null) tasks.Add(_flushTask);

                if (tasks.Count > 0)
                    await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            }
            catch { }
        }
        finally
        {
            try { await _frameWriter.CompletePipeAsync().ConfigureAwait(false); } catch { }
            try { if (_streamPair != null) await _streamPair.DisposeAsync().ConfigureAwait(false); } catch { }
            _streamPair = null;
            _readStream = null;
            _writeStream = null;

            try { _frameWriter.Dispose(); } catch { }
            try { _shutdownCts.Dispose(); } catch { }
            try { _reconnectLock.Dispose(); } catch { }
            try { _autoReconnectCts?.Dispose(); } catch { }
        }
    }

    #endregion
}
