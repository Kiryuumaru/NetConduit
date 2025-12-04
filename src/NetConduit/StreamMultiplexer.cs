using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NetConduit.Internal;

namespace NetConduit;

/// <summary>
/// A transport-agnostic stream multiplexer that creates multiple virtual channels over a single connection.
/// </summary>
public sealed class StreamMultiplexer : IAsyncDisposable
{
    private readonly Stream _readStream;
    private readonly Stream _writeStream;
    private readonly MultiplexerOptions _options;
    private readonly Guid _sessionId;
    
    // Write channels use LOCAL index space (indices we allocate)
    private readonly ConcurrentDictionary<uint, WriteChannel> _writeChannelsByIndex = new();
    // Read channels use REMOTE index space (indices allocated by remote)
    private readonly ConcurrentDictionary<uint, ReadChannel> _readChannelsByIndex = new();
    private readonly ConcurrentDictionary<string, WriteChannel> _writeChannelsById = new();
    private readonly ConcurrentDictionary<string, ReadChannel> _readChannelsById = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ReadChannel>> _pendingAccepts = new();
    private readonly Channel<ReadChannel> _acceptChannel;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TaskCompletionSource _handshakeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PriorityQueue<(uint ChannelIndex, ReadOnlyMemory<byte> Data), byte> _sendQueue = new();
    private readonly SemaphoreSlim _sendQueueSemaphore = new(0);
    private readonly object _sendQueueLock = new();
    
    // Reconnection support
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly List<(uint ChannelIndex, byte[] Data)> _pendingReconnectBuffer = new();
    private volatile bool _isConnected;
    private volatile bool _isReconnecting;
    private TaskCompletionSource? _disconnectedTcs;
    
    // Index space negotiation via handshake nonce
    private long _localNonce;
    private long _remoteNonce;
    private bool _indexSpaceDetermined;
    
    // Channel index allocation - determined after handshake nonce exchange
    private uint _nextChannelIndex;
    private volatile bool _isRunning;
    private volatile bool _goAwaySent;
    private volatile bool _goAwayReceived;
    private bool _disposed;
    private Task? _readTask;
    private Task? _writeTask;
    private Task? _pingTask;
    private Task? _flushTask;
    private long _lastPingTimestamp;
    private Guid _remoteSessionId;
    private volatile bool _pendingFlush;
    private readonly byte[] _headerBuffer = new byte[FrameHeader.Size]; // Reusable header buffer

    /// <summary>
    /// Creates a new stream multiplexer.
    /// </summary>
    /// <param name="readStream">Stream for reading data from remote.</param>
    /// <param name="writeStream">Stream for writing data to remote.</param>
    /// <param name="options">Multiplexer options.</param>
    public StreamMultiplexer(Stream readStream, Stream writeStream, MultiplexerOptions? options = null)
    {
        _readStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
        _writeStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
        _options = options ?? new MultiplexerOptions();
        _sessionId = _options.SessionId ?? Guid.NewGuid();
        
        // Index space will be determined after handshake nonce exchange
        // Higher nonce uses odd indices (1, 3, 5, ...), lower uses even (2, 4, 6, ...)
        _nextChannelIndex = 0; // Will be set in DetermineIndexSpace()
        
        _acceptChannel = Channel.CreateUnbounded<ReadChannel>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        
        Stats = new MultiplexerStats();
    }

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

    /// <summary>
    /// Gets the IDs of all active channels (both opened and accepted).
    /// </summary>
    public IReadOnlyCollection<string> ActiveChannelIds
    {
        get
        {
            var ids = new HashSet<string>(_writeChannelsById.Keys);
            foreach (var id in _readChannelsById.Keys)
                ids.Add(id);
            return ids;
        }
    }

    /// <summary>
    /// Gets the IDs of channels opened by this side.
    /// </summary>
    public IReadOnlyCollection<string> OpenedChannelIds => _writeChannelsById.Keys.ToArray();

    /// <summary>
    /// Gets the IDs of channels accepted from the remote side.
    /// </summary>
    public IReadOnlyCollection<string> AcceptedChannelIds => _readChannelsById.Keys.ToArray();

    /// <summary>
    /// Gets the count of active channels.
    /// </summary>
    public int ActiveChannelCount => _writeChannelsByIndex.Count + _readChannelsByIndex.Count;

    /// <summary>
    /// Event raised when a channel is opened.
    /// </summary>
    public event Action<string>? OnChannelOpened;
    
    /// <summary>
    /// Event raised when a channel is closed.
    /// </summary>
    public event Action<string, Exception?>? OnChannelClosed;
    
    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event Action<Exception>? OnError;

    /// <summary>
    /// Event raised when the connection is lost but reconnection is possible.
    /// </summary>
    public event Action? OnDisconnected;

    /// <summary>
    /// Event raised when the connection is restored after a disconnection.
    /// </summary>
    public event Action? OnReconnected;

    /// <summary>
    /// Whether the multiplexer is currently connected.
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Whether the multiplexer is currently attempting to reconnect.
    /// </summary>
    public bool IsReconnecting => _isReconnecting;

    /// <summary>
    /// Waits for the handshake to complete.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal Task WaitForHandshakeAsync(CancellationToken cancellationToken = default)
        => _handshakeCompleted.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Starts the multiplexer and waits for handshake to complete.
    /// After this method returns, the multiplexer is ready to open and accept channels.
    /// The background processing continues until the multiplexer is disposed or cancelled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the background processing. This task completes when the multiplexer shuts down.</returns>
    /// <example>
    /// <code>
    /// // Start the multiplexer and wait for it to be ready
    /// var runTask = await mux.StartAsync(cancellationToken);
    /// 
    /// // Now you can open channels
    /// var channel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "my-channel" });
    /// 
    /// // Later, wait for the multiplexer to complete (e.g., on shutdown)
    /// await runTask;
    /// </code>
    /// </example>
    public async Task<Task> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Multiplexer is already running.");

        // Start RunAsync in the background
        var runTask = RunAsync(cancellationToken);
        
        // Wait for handshake to complete (or RunAsync to fail)
        var handshakeTask = _handshakeCompleted.Task.WaitAsync(cancellationToken);
        var completedTask = await Task.WhenAny(runTask, handshakeTask).ConfigureAwait(false);
        
        // If RunAsync completed first, it means an error occurred before handshake
        if (completedTask == runTask)
        {
            // Propagate any exception from RunAsync
            await runTask.ConfigureAwait(false);
            throw new InvalidOperationException("Multiplexer stopped before handshake completed.");
        }
        
        // Handshake completed, return the background task
        return runTask;
    }

    /// <summary>
    /// Starts the multiplexer read/write loops.
    /// </summary>
    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("Multiplexer is already running.");

        _isRunning = true;
        _isConnected = true;
        
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var ct = linkedCts.Token;

        try
        {
            // Send handshake
            await SendHandshakeAsync(ct).ConfigureAwait(false);
            
            // Start background tasks
            _readTask = ReadLoopAsync(ct);
            _writeTask = WriteLoopAsync(ct);
            _pingTask = PingLoopAsync(ct);
            
            // Start flush task only if batched mode
            if (_options.FlushMode == FlushMode.Batched)
                _flushTask = FlushLoopAsync(ct);

            // Wait for handshake to complete
            await _handshakeCompleted.Task.WaitAsync(ct).ConfigureAwait(false);

            // Wait for all tasks to complete
            var tasks = _flushTask != null 
                ? new[] { _readTask, _writeTask, _pingTask, _flushTask }
                : new[] { _readTask, _writeTask, _pingTask };
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            throw;
        }
        finally
        {
            _isRunning = false;
            _isConnected = false;
        }
    }

    /// <summary>
    /// Opens a new channel for writing.
    /// </summary>
    /// <param name="options">Channel options including required ChannelId.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The opened write channel.</returns>
    /// <exception cref="ArgumentNullException">If options is null.</exception>
    /// <exception cref="ArgumentException">If ChannelId is invalid.</exception>
    /// <exception cref="InvalidOperationException">If a channel with the same ChannelId already exists.</exception>
    public async ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        
        if (!_isRunning)
            throw new InvalidOperationException("Multiplexer is not running.");
        
        if (_goAwaySent || _goAwayReceived)
            throw new InvalidOperationException("Cannot open new channels after GOAWAY.");

        // Wait for handshake to complete (needed for index space negotiation)
        await _handshakeCompleted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Validate ChannelId
        var channelIdBytes = System.Text.Encoding.UTF8.GetByteCount(options.ChannelId);
        if (channelIdBytes > ChannelIdLimits.MaxLength)
            throw new ArgumentException($"ChannelId exceeds maximum length of {ChannelIdLimits.MaxLength} bytes (was {channelIdBytes} bytes).", nameof(options));

        // Check for duplicate ChannelId
        if (_writeChannelsById.ContainsKey(options.ChannelId))
            throw new InvalidOperationException($"A channel with ChannelId '{options.ChannelId}' already exists.");

        // Allocate channel index
        var channelIndex = AllocateChannelIndex();
        
        var channel = new WriteChannel(this, channelIndex, options.ChannelId, options.Priority, options);
        
        if (!_writeChannelsByIndex.TryAdd(channelIndex, channel))
            throw new MultiplexerException(ErrorCode.ChannelExists, $"Channel index {channelIndex} already exists.");

        if (!_writeChannelsById.TryAdd(options.ChannelId, channel))
        {
            _writeChannelsByIndex.TryRemove(channelIndex, out _);
            throw new InvalidOperationException($"A channel with ChannelId '{options.ChannelId}' already exists.");
        }

        Stats.IncrementTotalChannelsOpened();
        Stats.IncrementOpenChannels();

        // Send INIT frame with ChannelId
        await SendInitAsync(channelIndex, options.ChannelId, options.Priority, cancellationToken).ConfigureAwait(false);

        // Wait for ACK (will be handled by read loop calling SetOpen/SetClosed/SetError)
        var timeout = TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        try
        {
            await channel.WaitForOpenAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _writeChannelsByIndex.TryRemove(channelIndex, out _);
            _writeChannelsById.TryRemove(options.ChannelId, out _);
            Stats.DecrementOpenChannels();
            throw new TimeoutException($"Channel open timed out after {timeout}");
        }
        catch (MultiplexerException)
        {
            _writeChannelsByIndex.TryRemove(channelIndex, out _);
            _writeChannelsById.TryRemove(options.ChannelId, out _);
            Stats.DecrementOpenChannels();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // SetClosed() was called - channel was refused
            _writeChannelsByIndex.TryRemove(channelIndex, out _);
            _writeChannelsById.TryRemove(options.ChannelId, out _);
            Stats.DecrementOpenChannels();
            throw new MultiplexerException(ErrorCode.Refused, "Channel open was refused.");
        }

        // If WaitForOpenAsync completed successfully, channel is open
        OnChannelOpened?.Invoke(options.ChannelId);
        return channel;
    }

    /// <summary>
    /// Accepts incoming channels from the remote side.
    /// </summary>
    public async IAsyncEnumerable<ReadChannel> AcceptChannelsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var channel in _acceptChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return channel;
        }
    }

    /// <summary>
    /// Accepts a specific channel by its ChannelId.
    /// </summary>
    /// <param name="channelId">The ChannelId to wait for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The accepted read channel.</returns>
    public async ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        
        // Check if channel already exists
        if (_readChannelsById.TryGetValue(channelId, out var existingChannel))
            return existingChannel;

        // Create a TaskCompletionSource to wait for the channel
        var tcs = new TaskCompletionSource<ReadChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        if (!_pendingAccepts.TryAdd(channelId, tcs))
        {
            // Another task is already waiting for this channel
            if (_pendingAccepts.TryGetValue(channelId, out var existingTcs))
                return await existingTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            
            // Race condition - channel might have been added
            if (_readChannelsById.TryGetValue(channelId, out existingChannel))
                return existingChannel;
        }

        try
        {
            // Double-check after adding to pendingAccepts - channel may have arrived 
            // between our initial check and TryAdd
            if (_readChannelsById.TryGetValue(channelId, out existingChannel))
            {
                _pendingAccepts.TryRemove(channelId, out _);
                return existingChannel;
            }
            
            // Register cancellation
            await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingAccepts.TryRemove(channelId, out _);
        }
    }

    /// <summary>
    /// Gets a write channel by its ChannelId.
    /// </summary>
    /// <param name="channelId">The ChannelId to look up.</param>
    /// <returns>The write channel, or null if not found.</returns>
    public WriteChannel? GetWriteChannel(string channelId)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        return _writeChannelsById.TryGetValue(channelId, out var channel) ? channel : null;
    }

    /// <summary>
    /// Gets a read channel by its ChannelId.
    /// </summary>
    /// <param name="channelId">The ChannelId to look up.</param>
    /// <returns>The read channel, or null if not found.</returns>
    public ReadChannel? GetReadChannel(string channelId)
    {
        ArgumentNullException.ThrowIfNull(channelId);
        return _readChannelsById.TryGetValue(channelId, out var channel) ? channel : null;
    }

    /// <summary>
    /// Gets the multiplexer statistics.
    /// </summary>
    public MultiplexerStats GetStats() => Stats;

    /// <summary>
    /// Initiates graceful shutdown.
    /// </summary>
    public async ValueTask GoAwayAsync(CancellationToken cancellationToken = default)
    {
        if (_goAwaySent) return;
        
        _goAwaySent = true;
        await SendGoAwayAsync(ErrorCode.None, cancellationToken).ConfigureAwait(false);
        
        // Wait for existing channels to close
        using var timeoutCts = new CancellationTokenSource(_options.GoAwayTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        
        try
        {
            while (_writeChannelsByIndex.Count > 0 || _readChannelsByIndex.Count > 0)
            {
                await Task.Delay(100, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout reached, force close remaining channels
        }

        _shutdownCts.Cancel();
    }

    /// <summary>
    /// Reconnects the multiplexer with new streams after a disconnection.
    /// </summary>
    /// <param name="newReadStream">The new stream for reading data.</param>
    /// <param name="newWriteStream">The new stream for writing data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if reconnection is not enabled or the multiplexer wasn't previously running.</exception>
    public async Task ReconnectAsync(Stream newReadStream, Stream newWriteStream, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableReconnection)
            throw new InvalidOperationException("Reconnection is not enabled for this multiplexer.");

        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamMultiplexer));

        await _reconnectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _isReconnecting = true;

            // Reconnection protocol:
            // 1. Send RECONNECT with session ID and our read channel byte positions
            //    (telling remote how much we received on each channel they write to)
            // 2. Receive RECONNECT_ACK with their read channel byte positions
            //    (telling us how much they received on each channel we write to)
            // 3. Replay any unacknowledged data on our write channels

            // Send reconnect request with our receive positions
            await SendReconnectAsync(newWriteStream, cancellationToken).ConfigureAwait(false);

            // Wait for reconnect acknowledgment with their receive positions
            var remoteReceivePositions = await ReceiveReconnectAckAsync(newReadStream, cancellationToken).ConfigureAwait(false);

            // Replay unacknowledged data on write channels
            await ReplayUnacknowledgedDataAsync(newWriteStream, remoteReceivePositions, cancellationToken).ConfigureAwait(false);

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

    private async Task SendReconnectAsync(Stream writeStream, CancellationToken ct)
    {
        // RECONNECT payload format:
        // [subtype: 1B] [session_id: 16B] [channel_count: 4B]
        // For each read channel: [channel_index: 4B] [bytes_received: 8B]
        // (We send bytes_received so remote knows what we got and can replay the rest)
        
        var readChannelCount = _readChannelsByIndex.Count;
        var payloadSize = 1 + 16 + 4 + (readChannelCount * 12); // 12 bytes per channel (4B index + 8B bytes_received)
        var payload = new byte[payloadSize];
        
        payload[0] = (byte)Internal.ControlSubtype.Reconnect;
        _sessionId.TryWriteBytes(payload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17), (uint)readChannelCount);

        var offset = 21;
        foreach (var kvp in _readChannelsByIndex)
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
        // Read frame header
        var headerBytes = new byte[FrameHeader.Size];
        await readStream.ReadExactlyAsync(headerBytes, ct).ConfigureAwait(false);
        var header = FrameHeader.Read(headerBytes);

        if (header.ChannelId != ChannelIndexLimits.ControlChannel)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Expected control frame for RECONNECT_ACK.");

        // Read payload
        var payload = new byte[header.Length];
        await readStream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        if (payload[0] != (byte)Internal.ControlSubtype.ReconnectAck)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Expected RECONNECT_ACK.");

        // Parse remote session ID
        var remoteSessionId = new Guid(payload.AsSpan(1, 16));
        if (remoteSessionId != _remoteSessionId)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Session ID mismatch during reconnection.");

        // Parse remote's receive positions for our write channels
        var receivePositions = new Dictionary<uint, long>();
        var channelCount = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(17));
        var offset = 21;
        
        for (int i = 0; i < channelCount; i++)
        {
            var channelIndex = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset));
            var bytesReceived = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(offset + 4));
            receivePositions[channelIndex] = bytesReceived;
            offset += 12;
        }

        return receivePositions;
    }

    private async Task ReplayUnacknowledgedDataAsync(Stream writeStream, Dictionary<uint, long> remoteReceivePositions, CancellationToken ct)
    {
        var headerBytes = new byte[FrameHeader.Size];
        
        foreach (var kvp in _writeChannelsByIndex)
        {
            var channelIndex = kvp.Key;
            var channel = kvp.Value;
            
            // Get how much the remote received
            var remoteReceived = remoteReceivePositions.GetValueOrDefault(channelIndex, 0);
            
            // Update our ack tracking
            channel.SyncState.SetBytesAcked(remoteReceived);
            
            // Get unacknowledged data to replay
            var replayData = channel.SyncState.GetUnacknowledgedDataFrom(remoteReceived);
            
            if (replayData.Length > 0)
            {
                // Send the replayed data as a regular data frame
                var header = new FrameHeader(channelIndex, FrameFlags.Data, (uint)replayData.Length);
                header.Write(headerBytes);
                
                await writeStream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
                await writeStream.WriteAsync(replayData, ct).ConfigureAwait(false);
            }
        }
        
        await writeStream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Signals that the connection has been lost. Call this when the underlying transport fails.
    /// If reconnection is enabled, the multiplexer will wait for ReconnectAsync to be called.
    /// </summary>
    public void NotifyDisconnected()
    {
        if (!_options.EnableReconnection || _disposed)
            return;

        _isConnected = false;
        _disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OnDisconnected?.Invoke();
    }

    internal void OnWriteChannelDisposed(uint channelIndex, string channelId)
    {
        _writeChannelsByIndex.TryRemove(channelIndex, out _);
        _writeChannelsById.TryRemove(channelId, out _);
        Stats.DecrementOpenChannels();
        Stats.IncrementTotalChannelsClosed();
        OnChannelClosed?.Invoke(channelId, null);
    }

    internal void OnReadChannelDisposed(uint channelIndex, string channelId)
    {
        _readChannelsByIndex.TryRemove(channelIndex, out _);
        _readChannelsById.TryRemove(channelId, out _);
        Stats.DecrementOpenChannels();
        Stats.IncrementTotalChannelsClosed();
        OnChannelClosed?.Invoke(channelId, null);
    }

    /// <summary>
    /// Determines the index space based on handshake nonce comparison.
    /// Called after receiving remote handshake with nonce.
    /// Higher nonce gets odd indices (1, 3, 5, ...), lower gets even (2, 4, 6, ...).
    /// </summary>
    private void DetermineIndexSpace()
    {
        if (_indexSpaceDetermined)
            return;
            
        // Compare nonces: higher nonce uses odd indices, lower uses even
        // This ensures symmetric peers automatically get different index spaces
        bool useOddIndices;
        
        if (_localNonce != _remoteNonce)
        {
            useOddIndices = _localNonce > _remoteNonce;
        }
        else
        {
            // Extremely rare tie-breaker: compare session IDs
            useOddIndices = _sessionId.CompareTo(_remoteSessionId) > 0;
        }
        
        _nextChannelIndex = useOddIndices ? 1u : 2u;
        _indexSpaceDetermined = true;
    }

    private uint AllocateChannelIndex()
    {
        if (!_indexSpaceDetermined)
            throw new InvalidOperationException("Cannot allocate channel index before handshake completes.");
            
        // Increment by 2 to maintain odd/even separation
        var id = Interlocked.Add(ref _nextChannelIndex, 2) - 2;
        
        if (id > ChannelIndexLimits.MaxDataChannel)
            throw new MultiplexerException(ErrorCode.Internal, "Channel index space exhausted.");
        
        return id;
    }

    #region Send Methods
    
    private const int CombinedBufferThreshold = 8192; // Combine writes for payloads <= 8KB

    internal ValueTask SendDataFrameAsync(uint channelIndex, ReadOnlyMemory<byte> data, ChannelPriority priority, CancellationToken ct)
    {
        var header = new FrameHeader(channelIndex, FrameFlags.Data, (uint)data.Length);
        return SendFrameOptimizedAsync(header, data, ct);
    }
    
    private async ValueTask SendFrameOptimizedAsync(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // Fast path: combine header + payload into single write for small frames
        if (payload.Length <= CombinedBufferThreshold)
        {
            var combinedLength = FrameHeader.Size + payload.Length;
            
            // Rent buffer from pool - each call gets its own buffer, safe for concurrency
            var buffer = ArrayPool<byte>.Shared.Rent(combinedLength);
            try
            {
                // Prepare buffer OUTSIDE the lock (can run in parallel)
                header.Write(buffer);
                payload.Span.CopyTo(buffer.AsSpan(FrameHeader.Size));
                
                // Only hold lock for the actual write
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _writeStream.WriteAsync(buffer.AsMemory(0, combinedLength), ct).ConfigureAwait(false);
                    
                    if (_options.FlushMode == FlushMode.Immediate)
                        await _writeStream.FlushAsync(ct).ConfigureAwait(false);
                    else if (_options.FlushMode == FlushMode.Batched)
                        _pendingFlush = true;
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else
        {
            // Large payload: two writes but still single lock acquisition
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                header.Write(_headerBuffer);
                await _writeStream.WriteAsync(_headerBuffer, ct).ConfigureAwait(false);
                await _writeStream.WriteAsync(payload, ct).ConfigureAwait(false);
                
                if (_options.FlushMode == FlushMode.Immediate)
                    await _writeStream.FlushAsync(ct).ConfigureAwait(false);
                else if (_options.FlushMode == FlushMode.Batched)
                    _pendingFlush = true;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        
        Stats.AddBytesSent(FrameHeader.Size + payload.Length);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        var headerBuffer = new byte[FrameHeader.Size];
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _sendQueueSemaphore.WaitAsync(ct).ConfigureAwait(false);
                
                (uint channelIndex, ReadOnlyMemory<byte> data) item;
                lock (_sendQueueLock)
                {
                    if (!_sendQueue.TryDequeue(out item, out _))
                        continue;
                }

                var header = new FrameHeader(item.channelIndex, FrameFlags.Data, (uint)item.data.Length);
                header.Write(headerBuffer);

                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _writeStream.WriteAsync(headerBuffer, ct).ConfigureAwait(false);
                    if (!item.data.IsEmpty)
                        await _writeStream.WriteAsync(item.data, ct).ConfigureAwait(false);
                    
                    // Conditional flush based on FlushMode
                    if (_options.FlushMode == FlushMode.Immediate)
                        await _writeStream.FlushAsync(ct).ConfigureAwait(false);
                    else if (_options.FlushMode == FlushMode.Batched)
                        _pendingFlush = true;
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
                break;
            }
        }
    }
    
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.FlushInterval, ct).ConfigureAwait(false);
                
                if (_pendingFlush)
                {
                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        _pendingFlush = false;
                        await _writeStream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Ignore flush errors - write loop will catch stream issues
            }
        }
    }

    private async ValueTask SendFrameDirectAsync(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Write header using reusable buffer
            header.Write(_headerBuffer);
            await _writeStream.WriteAsync(_headerBuffer, ct).ConfigureAwait(false);
            
            if (!payload.IsEmpty)
                await _writeStream.WriteAsync(payload, ct).ConfigureAwait(false);
            
            // Conditional flush based on FlushMode
            switch (_options.FlushMode)
            {
                case FlushMode.Immediate:
                    await _writeStream.FlushAsync(ct).ConfigureAwait(false);
                    break;
                case FlushMode.Batched:
                    _pendingFlush = true;
                    // Flush task will handle periodic flushing
                    break;
                case FlushMode.Manual:
                    // Do not flush - rely on underlying stream buffering
                    break;
            }
        }
        finally
        {
            _writeLock.Release();
        }
        
        Stats.AddBytesSent(FrameHeader.Size + payload.Length);
    }

    private async ValueTask SendHandshakeAsync(CancellationToken ct)
    {
        // Handshake payload: [subtype: 1B][session_id: 16B (GUID)][nonce: 8B]
        var payload = new byte[25];
        payload[0] = (byte)ControlSubtype.Handshake;
        _sessionId.TryWriteBytes(payload.AsSpan(1));
        
        // Generate random nonce for index space negotiation
        _localNonce = Random.Shared.NextInt64();
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(17), _localNonce);
        
        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        await SendFrameDirectAsync(header, payload, ct).ConfigureAwait(false);
    }

    private async ValueTask SendInitAsync(uint channelIndex, string channelId, ChannelPriority priority, CancellationToken ct)
    {
        // INIT payload format: [priority: u8][id_length: u16 BE][channel_id: 0-1024B UTF8]
        var channelIdBytes = System.Text.Encoding.UTF8.GetBytes(channelId);
        var payload = new byte[1 + 2 + channelIdBytes.Length];
        payload[0] = (byte)priority;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)channelIdBytes.Length);
        channelIdBytes.CopyTo(payload.AsSpan(3));
        
        var header = new FrameHeader(channelIndex, FrameFlags.Init, (uint)payload.Length);
        await SendFrameDirectAsync(header, payload, ct).ConfigureAwait(false);
    }

    internal async ValueTask SendFinAsync(uint channelIndex, CancellationToken ct)
    {
        // Wait for any pending data in the queue for this channel to be sent
        // FIN must come after all data for proper stream semantics
        await FlushSendQueueAsync(ct).ConfigureAwait(false);
        
        var header = new FrameHeader(channelIndex, FrameFlags.Fin, 0);
        await SendFrameDirectAsync(header, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        
        // Force flush after FIN to ensure all data is sent immediately
        // This guarantees the peer receives the FIN and all preceding data
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writeStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
    
    private async ValueTask FlushSendQueueAsync(CancellationToken ct)
    {
        // Wait a brief moment for pending queue items to be processed
        // This is a simple approach - a more robust solution would track per-channel pending writes
        var maxWait = 100; // Max 100 iterations (1 second total)
        while (maxWait-- > 0)
        {
            bool isEmpty;
            lock (_sendQueueLock)
            {
                isEmpty = _sendQueue.Count == 0;
            }
            if (isEmpty) break;
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask SendAckAsync(uint channelIndex, uint credits, CancellationToken ct)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, credits);
        
        var header = new FrameHeader(channelIndex, FrameFlags.Ack, (uint)payload.Length);
        await SendFrameDirectAsync(header, payload, ct).ConfigureAwait(false);
    }

    internal async ValueTask SendCreditGrantAsync(uint channelIndex, uint credits, CancellationToken ct)
    {
        var payload = new byte[9];
        payload[0] = (byte)ControlSubtype.CreditGrant;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), channelIndex);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), credits);
        
        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        await SendFrameDirectAsync(header, payload, ct).ConfigureAwait(false);
    }

    private async ValueTask SendPingAsync(CancellationToken ct)
    {
        var payload = new byte[9];
        payload[0] = (byte)ControlSubtype.Ping;
        _lastPingTimestamp = DateTime.UtcNow.Ticks;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), _lastPingTimestamp);
        
        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        await SendFrameDirectAsync(header, payload, ct).ConfigureAwait(false);
    }

    private async ValueTask SendPongAsync(long timestamp, CancellationToken ct)
    {
        var payload = new byte[9];
        payload[0] = (byte)ControlSubtype.Pong;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), timestamp);
        
        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        await SendFrameDirectAsync(header, payload, ct).ConfigureAwait(false);
    }

    private async ValueTask SendGoAwayAsync(ErrorCode code, CancellationToken ct)
    {
        var lastChannelIndex = Volatile.Read(ref _nextChannelIndex) - 1;
        if (lastChannelIndex < ChannelIndexLimits.MinDataChannel)
            lastChannelIndex = 0;
            
        var payload = new byte[7];
        payload[0] = (byte)ControlSubtype.GoAway;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)code);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(3), lastChannelIndex);
        
        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        await SendFrameDirectAsync(header, payload, ct).ConfigureAwait(false);
    }

    private async ValueTask SendErrorAsync(uint channelIndex, ErrorCode code, string message, CancellationToken ct)
    {
        var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var payload = new byte[2 + messageBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)code);
        messageBytes.CopyTo(payload.AsSpan(2));
        
        var header = new FrameHeader(channelIndex, FrameFlags.Err, (uint)payload.Length);
        await SendFrameDirectAsync(header, payload, ct).ConfigureAwait(false);
    }

    #endregion

    #region Read Methods

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var headerBuffer = new byte[FrameHeader.Size];
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Read header
                await ReadExactlyAsync(_readStream, headerBuffer, ct).ConfigureAwait(false);
                var header = FrameHeader.Read(headerBuffer);
                
                Stats.AddBytesReceived(FrameHeader.Size);

                // Validate frame size
                if (header.Length > _options.MaxFrameSize)
                {
                    throw new MultiplexerException(ErrorCode.ProtocolError, 
                        $"Frame size {header.Length} exceeds maximum {_options.MaxFrameSize}");
                }

                // Read payload into owned buffer (not pooled - workers will return it)
                byte[] payload;
                int payloadLength = (int)header.Length;
                
                if (payloadLength > 0)
                {
                    payload = ArrayPool<byte>.Shared.Rent(payloadLength);
                    await ReadExactlyAsync(_readStream, payload.AsMemory(0, payloadLength), ct).ConfigureAwait(false);
                    Stats.AddBytesReceived(header.Length);
                }
                else
                {
                    payload = Array.Empty<byte>();
                }
                
                // Dispatch to worker pool for parallel processing
                // Control frames (channel 0) are processed inline for ordering guarantees
                if (header.ChannelId == ChannelIndexLimits.ControlChannel)
                {
                    try
                    {
                        await ProcessFrameAsync(header, payload.AsMemory(0, payloadLength), ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (payloadLength > 0)
                            ArrayPool<byte>.Shared.Return(payload);
                    }
                }
                else
                {
                    // For data frames, process inline to maintain per-channel ordering
                    // The parallelism happens at the channel level (multiple channels can be read concurrently)
                    // not at the frame level within a single channel
                    try
                    {
                        await ProcessFrameAsync(header, payload.AsMemory(0, payloadLength), ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (payloadLength > 0)
                            ArrayPool<byte>.Shared.Return(payload);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                // Connection closed
                break;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
                break;
            }
        }
        
        // Close accept channel
        _acceptChannel.Writer.TryComplete();
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Connection closed.");
            totalRead += read;
        }
    }

    private async ValueTask ProcessFrameAsync(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (header.ChannelId == ChannelIndexLimits.ControlChannel)
        {
            await ProcessControlFrameAsync(header, payload, ct).ConfigureAwait(false);
            return;
        }

        var channelIndex = header.ChannelId; // ChannelId in header is actually the index

        switch (header.Flags)
        {
            case FrameFlags.Data:
                ProcessDataFrame(channelIndex, payload);
                break;
                
            case FrameFlags.Init:
                await ProcessInitFrameAsync(channelIndex, payload, ct).ConfigureAwait(false);
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

    private async ValueTask ProcessControlFrameAsync(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length < 1)
            return;

        var subtype = (ControlSubtype)payload.Span[0];
        var data = payload[1..];

        switch (subtype)
        {
            case ControlSubtype.Handshake:
                // Parse session_id (16 bytes) and nonce (8 bytes) from handshake
                if (data.Length >= 16)
                {
                    _remoteSessionId = new Guid(data.Span[..16]);
                }
                if (data.Length >= 24)
                {
                    _remoteNonce = BinaryPrimitives.ReadInt64BigEndian(data.Span[16..24]);
                }
                // Determine index space based on nonce comparison
                DetermineIndexSpace();
                _handshakeCompleted.TrySetResult();
                break;
                
            case ControlSubtype.Ping:
                if (data.Length >= 8)
                {
                    var timestamp = BinaryPrimitives.ReadInt64BigEndian(data.Span);
                    await SendPongAsync(timestamp, ct).ConfigureAwait(false);
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
                    
                    if (_writeChannelsByIndex.TryGetValue(channelIndex, out var channel))
                    {
                        channel.GrantCredits(credits);
                    }
                }
                break;
                
            case ControlSubtype.GoAway:
                _goAwayReceived = true;
                _acceptChannel.Writer.TryComplete();
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
                await ProcessReconnectFrameAsync(data, ct).ConfigureAwait(false);
                break;

            case ControlSubtype.ReconnectAck:
                // ReconnectAck is processed synchronously in ReconnectAsync
                break;
        }
    }

    private async ValueTask ProcessReconnectFrameAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!_options.EnableReconnection)
        {
            await SendErrorAsync(0, ErrorCode.ProtocolError, "Reconnection not enabled", ct).ConfigureAwait(false);
            return;
        }

        if (data.Length < 20) // 16B session_id + 4B channel_count
        {
            await SendErrorAsync(0, ErrorCode.ProtocolError, "Invalid RECONNECT payload", ct).ConfigureAwait(false);
            return;
        }

        var incomingSessionId = new Guid(data.Span[..16]);
        var channelCount = BinaryPrimitives.ReadUInt32BigEndian(data.Span[16..]);

        // Verify this is a valid reconnection for this session
        if (incomingSessionId != _remoteSessionId && _remoteSessionId != Guid.Empty)
        {
            await SendErrorAsync(0, ErrorCode.SessionMismatch, "Session ID mismatch", ct).ConfigureAwait(false);
            return;
        }

        // Parse remote's receive positions (for channels we write to)
        // Remote is telling us how much they received on each of our write channels
        var offset = 20;
        for (int i = 0; i < channelCount && offset + 12 <= data.Length; i++)
        {
            var channelIndex = BinaryPrimitives.ReadUInt32BigEndian(data.Span[offset..]);
            var bytesReceived = BinaryPrimitives.ReadInt64BigEndian(data.Span[(offset + 4)..]);
            
            // Update our write channel's ack position
            if (_writeChannelsByIndex.TryGetValue(channelIndex, out var writeChannel))
            {
                writeChannel.SyncState.SetBytesAcked(bytesReceived);
            }
            
            offset += 12;
        }

        // Send RECONNECT_ACK with our read channel byte positions
        // (telling remote how much we received on each channel they write to)
        var readChannelCount = _readChannelsByIndex.Count;
        var payloadSize = 1 + 16 + 4 + (readChannelCount * 12);
        var payload = new byte[payloadSize];
        
        payload[0] = (byte)ControlSubtype.ReconnectAck;
        _sessionId.TryWriteBytes(payload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17), (uint)readChannelCount);
        
        var payloadOffset = 21;
        foreach (var kvp in _readChannelsByIndex)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(payloadOffset), kvp.Key);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(payloadOffset + 4), kvp.Value.SyncState.BytesReceived);
            payloadOffset += 12;
        }

        var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, (uint)payload.Length);
        await SendFrameDirectAsync(header, payload, ct).ConfigureAwait(false);
    }

    private void ProcessDataFrame(uint channelIndex, ReadOnlyMemory<byte> payload)
    {
        if (_readChannelsByIndex.TryGetValue(channelIndex, out var channel))
        {
            // Zero-copy path: rent buffer from pool, copy payload, transfer ownership to channel
            var ownedMemory = OwnedMemory.Rent(payload.Length);
            payload.CopyTo(ownedMemory.Memory);
            channel.EnqueueData(ownedMemory);
        }
    }

    private async ValueTask ProcessInitFrameAsync(uint channelIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_goAwayReceived || _goAwaySent)
        {
            await SendErrorAsync(channelIndex, ErrorCode.Refused, "GOAWAY received", ct).ConfigureAwait(false);
            return;
        }

        // Parse INIT payload: [priority: u8][id_length: u16 BE][channel_id: 0-1024B UTF8]
        if (payload.Length < 3)
        {
            await SendErrorAsync(channelIndex, ErrorCode.ProtocolError, "Invalid INIT payload", ct).ConfigureAwait(false);
            return;
        }

        var priority = (ChannelPriority)payload.Span[0];
        var idLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Span[1..]);
        
        if (payload.Length < 3 + idLength)
        {
            await SendErrorAsync(channelIndex, ErrorCode.ProtocolError, "Invalid INIT payload length", ct).ConfigureAwait(false);
            return;
        }

        var channelId = System.Text.Encoding.UTF8.GetString(payload.Span.Slice(3, idLength));
        
        // Check for duplicate ChannelId
        if (_readChannelsById.ContainsKey(channelId))
        {
            await SendErrorAsync(channelIndex, ErrorCode.ChannelExists, $"Channel '{channelId}' already exists", ct).ConfigureAwait(false);
            return;
        }

        var options = new ChannelOptions 
        { 
            ChannelId = channelId,
            Priority = priority,
            InitialCredits = _options.DefaultChannelOptions.InitialCredits,
            CreditGrantThreshold = _options.DefaultChannelOptions.CreditGrantThreshold,
            SendTimeout = _options.DefaultChannelOptions.SendTimeout
        };
        
        var channel = new ReadChannel(this, channelIndex, channelId, priority, options);
        
        if (!_readChannelsByIndex.TryAdd(channelIndex, channel))
        {
            await SendErrorAsync(channelIndex, ErrorCode.ChannelExists, "Channel index already exists", ct).ConfigureAwait(false);
            return;
        }

        if (!_readChannelsById.TryAdd(channelId, channel))
        {
            _readChannelsByIndex.TryRemove(channelIndex, out _);
            await SendErrorAsync(channelIndex, ErrorCode.ChannelExists, $"Channel '{channelId}' already exists", ct).ConfigureAwait(false);
            return;
        }

        Stats.IncrementTotalChannelsOpened();
        Stats.IncrementOpenChannels();

        // Send ACK with initial credits
        await SendAckAsync(channelIndex, options.InitialCredits, ct).ConfigureAwait(false);
        
        // Check if someone is waiting for this specific channel
        if (_pendingAccepts.TryRemove(channelId, out var tcs))
        {
            tcs.TrySetResult(channel);
        }
        else
        {
            // Add to accept queue
            await _acceptChannel.Writer.WriteAsync(channel, ct).ConfigureAwait(false);
        }
        
        OnChannelOpened?.Invoke(channelId);
    }

    private void ProcessFinFrame(uint channelIndex)
    {
        // channelIndex is from REMOTE's index space - close our ReadChannel for their channel
        // Do NOT touch _writeChannelsByIndex - that uses OUR local index space, not remote's
        if (_readChannelsByIndex.TryGetValue(channelIndex, out var readChannel))
        {
            readChannel.SetClosed();
        }
    }

    private void ProcessAckFrame(uint channelIndex, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 4)
            return;
            
        var credits = BinaryPrimitives.ReadUInt32BigEndian(payload.Span);
        
        if (_writeChannelsByIndex.TryGetValue(channelIndex, out var channel))
        {
            channel.SetOpen(credits);
        }
    }

    private void ProcessErrorFrame(uint channelIndex, ReadOnlyMemory<byte> payload)
    {
        var code = payload.Length >= 2 
            ? (ErrorCode)BinaryPrimitives.ReadUInt16BigEndian(payload.Span)
            : ErrorCode.Internal;
        var message = payload.Length > 2
            ? System.Text.Encoding.UTF8.GetString(payload.Span[2..])
            : string.Empty;

        if (_writeChannelsByIndex.TryGetValue(channelIndex, out var writeChannel))
        {
            writeChannel.SetError(code, message);
        }
        
        if (_readChannelsByIndex.TryGetValue(channelIndex, out var readChannel))
        {
            readChannel.SetError(code, message);
        }
    }
    #endregion

    #region Ping Loop

    private async Task PingLoopAsync(CancellationToken ct)
    {
        // Both sides send pings to detect connection health
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PingInterval, ct).ConfigureAwait(false);
                
                await SendPingAsync(ct).ConfigureAwait(false);
                
                // Wait for pong
                await Task.Delay(_options.PingTimeout, ct).ConfigureAwait(false);
                
                // Check if we got a pong (missed pings counter would be reset)
                if (Stats.MissedPings > 0)
                {
                    Stats.IncrementMissedPings();
                    
                    if (Stats.MissedPings >= _options.MaxMissedPings)
                    {
                        // Connection dead
                        await GoAwayAsync(ct).ConfigureAwait(false);
                        break;
                    }
                }
                else
                {
                    Stats.IncrementMissedPings(); // Will be reset when pong arrives
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    #endregion

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _shutdownCts.Cancel();

        // Close all channels
        foreach (var channel in _writeChannelsByIndex.Values)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        
        foreach (var channel in _readChannelsByIndex.Values)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _acceptChannel.Writer.TryComplete();
        
        // Cancel any pending accepts
        foreach (var tcs in _pendingAccepts.Values)
        {
            tcs.TrySetCanceled();
        }
        _pendingAccepts.Clear();
        
        // Wait for tasks
        if (_readTask != null) await _readTask.ConfigureAwait(false);
        if (_writeTask != null) await _writeTask.ConfigureAwait(false);
        if (_pingTask != null) await _pingTask.ConfigureAwait(false);

        _writeLock.Dispose();
        _sendQueueSemaphore.Dispose();
        _shutdownCts.Dispose();
    }
}
