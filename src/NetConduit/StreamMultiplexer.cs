using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NetConduit.Internal;

namespace NetConduit;

/// <summary>
/// A transport-agnostic stream multiplexer that creates multiple virtual channels over a single connection.
/// </summary>
public sealed class StreamMultiplexer : IStreamMultiplexer
{
    private IStreamPair? _streamPair;
    private Stream? _readStream;
    private Stream? _writeStream;
    private readonly MultiplexerOptions _options;
    
    /// <summary>
    /// Gets the read stream, throwing if not connected.
    /// </summary>
    private Stream ReadStream => _readStream ?? throw new InvalidOperationException("Not connected. Ensure Start() has been called and connection is established.");
    
    /// <summary>
    /// Gets the write stream, throwing if not connected.
    /// </summary>
    private Stream WriteStream => _writeStream ?? throw new InvalidOperationException("Not connected. Ensure Start() has been called and connection is established.");
    private readonly Guid _sessionId;
    
    // Write channels use LOCAL index space (indices we allocate)
    private readonly ConcurrentDictionary<uint, WriteChannel> _writeChannelsByIndex = new();
    // Read channels use REMOTE index space (indices allocated by remote)
    private readonly ConcurrentDictionary<uint, ReadChannel> _readChannelsByIndex = new();
    private readonly ConcurrentDictionary<string, WriteChannel> _writeChannelsById = new();
    private readonly ConcurrentDictionary<string, ReadChannel> _readChannelsById = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ReadChannel>> _pendingAccepts = new();
    private readonly Channel<ReadChannel> _acceptChannel;
    private readonly object _writeLock = new();
    private readonly SemaphoreSlim _streamLock = new(1, 1);
    private readonly SemaphoreSlim _flushSignal = new(0, 1);
    private readonly ConcurrentQueue<ReadChannel> _pendingCreditChannels = new();
    private Pipe? _pipe;
    private PipeReader? _readPipeReader;
    private volatile Exception? _writeError;
    private readonly CancellationTokenSource _shutdownCts = new();
    private TaskCompletionSource _handshakeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    
    // Reconnection support
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly List<(uint ChannelIndex, byte[] Data)> _pendingReconnectBuffer = new();
    private volatile bool _isConnected;
    private volatile bool _isReconnecting;
    private TaskCompletionSource? _disconnectedTcs;
    private Task? _autoReconnectTask;
    private CancellationTokenSource? _autoReconnectCts;
    
    // Ready gate for WaitForReadyAsync() and channel operations
    private readonly TaskCompletionSource _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile int _currentConnectionAttempt;
    private Task? _mainLoopTask;
    
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
    private DisconnectReason? _disconnectReason;
    private Exception? _disconnectException;
    private Task? _readTask;
    private Task? _pingTask;
    private Task? _flushTask;
    private long _lastPingTimestamp;
    private Guid _remoteSessionId;
    private volatile bool _pendingFlush;

    /// <summary>
    /// Creates a new stream multiplexer (no I/O). Use <see cref="Create"/> to create an instance.
    /// </summary>
    /// <param name="options">Multiplexer options with required StreamFactory.</param>
    private StreamMultiplexer(MultiplexerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessionId = _options.SessionId ?? Guid.NewGuid();
        
        // Streams will be assigned when Start() calls StreamFactory
        _readStream = null;
        _writeStream = null;
        
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

    /// <summary>
    /// Creates a new multiplexer. No I/O occurs until Start() is called.
    /// </summary>
    /// <param name="options">Multiplexer options with required StreamFactory.</param>
    /// <returns>A new StreamMultiplexer instance.</returns>
    /// <example>
    /// <code>
    /// var options = new MultiplexerOptions 
    /// { 
    ///     StreamFactory = async ct => {
    ///         var client = new TcpClient();
    ///         await client.ConnectAsync("localhost", 5000, ct);
    ///         var stream = client.GetStream();
    ///         return (stream, stream);
    ///     }
    /// };
    /// var mux = StreamMultiplexer.Create(options);
    /// var runTask = mux.Start();
    /// await mux.WaitForReadyAsync();
    /// </code>
    /// </example>
    public static StreamMultiplexer Create(MultiplexerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new StreamMultiplexer(options);
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
    public event Action<DisconnectReason, Exception?>? OnDisconnected;
    
    /// <summary>
    /// The reason for disconnection, if disconnected.
    /// </summary>
    public DisconnectReason? DisconnectReason => _disconnectReason;
    
    /// <summary>
    /// The exception associated with disconnection, if any.
    /// </summary>
    public Exception? DisconnectException => _disconnectException;

    /// <summary>
    /// Event raised when the connection is restored after a disconnection.
    /// </summary>
    public event Action? OnReconnected;

    /// <summary>
    /// Event raised during auto-reconnection attempts. 
    /// Allows monitoring progress and optionally cancelling the reconnection process.
    /// </summary>
    public event Action<AutoReconnectEventArgs>? OnAutoReconnecting;

    /// <summary>
    /// Event raised when auto-reconnection has permanently failed after all attempts.
    /// </summary>
    public event Action<Exception>? OnAutoReconnectFailed;

    /// <summary>
    /// Whether the multiplexer is currently connected.
    /// </summary>
    public bool IsConnected => _isConnected;

    /// <summary>
    /// Whether the multiplexer is currently attempting to reconnect.
    /// </summary>
    public bool IsReconnecting => _isReconnecting;
    
    /// <summary>
    /// Current connection attempt number (0 if connected, >0 during connecting/reconnecting).
    /// </summary>
    public int CurrentConnectionAttempt => _currentConnectionAttempt;
    
    /// <summary>
    /// Event raised when the first successful connection + handshake completes.
    /// </summary>
    public event Action? OnReady;

    /// <summary>
    /// Waits for the handshake to complete.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal Task WaitForHandshakeAsync(CancellationToken cancellationToken = default)
        => _handshakeCompleted.Task.WaitAsync(cancellationToken);
    
    /// <summary>
    /// Waits until the multiplexer is ready (first connection + handshake complete).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default)
        => _readyTcs.Task.WaitAsync(cancellationToken);
    
    /// <summary>
    /// Starts the multiplexer background processing. No I/O occurs until this is called.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the multiplexer.</param>
    /// <returns>A task representing the background processing. Completes when the multiplexer shuts down.</returns>
    /// <example>
    /// <code>
    /// var mux = StreamMultiplexer.Create(options);
    /// var runTask = mux.Start(cancellationToken);
    /// await mux.WaitForReadyAsync();
    /// 
    /// // Now you can open channels
    /// var channel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "my-channel" });
    /// 
    /// // Later, wait for the multiplexer to complete (e.g., on shutdown)
    /// await runTask;
    /// </code>
    /// </example>
    public Task Start(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("Start() has already been called. The multiplexer can only be started once.");
        }

        _isRunning = true;
        
        // Spawn MainLoopAsync as background task
        _mainLoopTask = MainLoopAsync(cancellationToken);
        return _mainLoopTask;
    }
    
    /// <summary>
    /// Main loop that handles connection and reconnection.
    /// </summary>
    private async Task MainLoopAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var ct = linkedCts.Token;

        try
        {
            // Initial connection with retry (isReconnecting = false)
            await ConnectWithRetryAsync(isReconnecting: false, ct).ConfigureAwait(false);
            
            // Signal ready after first successful connection
            _readyTcs.TrySetResult();
            try { OnReady?.Invoke(); } catch { }
            
            // Run the main processing loops
            await RunProcessingLoopsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _shutdownCts.IsCancellationRequested)
        {
            // Normal shutdown - set ready as cancelled if not yet ready
            _readyTcs.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            // Fatal error - propagate to ready waiters
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
    
    /// <summary>
    /// Unified connection logic used for both initial connection and reconnection.
    /// </summary>
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
                
                // Check if we've exceeded max attempts (0 = unlimited)
                if (maxAttempts > 0 && attempt > maxAttempts)
                {
                    var failEx = lastException != null 
                        ? new MultiplexerException(ErrorCode.Internal, $"Connection failed after {maxAttempts} attempts.", lastException)
                        : new MultiplexerException(ErrorCode.Internal, $"Connection failed after {maxAttempts} attempts.");
                    
                    if (isReconnecting)
                    {
                        AbortAllChannels(failEx);
                    }
                    
                    OnAutoReconnectFailed?.Invoke(failEx);
                    throw failEx;
                }
                
                // Fire OnAutoReconnecting event BEFORE each attempt (including initial)
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
                    {
                        AbortAllChannels(cancelEx);
                    }
                    OnAutoReconnectFailed?.Invoke(cancelEx);
                    throw cancelEx;
                }
                
                // Wait before attempting (skip delay on first attempt)
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
                    // Step 1: Create stream pair using StreamFactory (with optional timeout)
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
                    {
                        throw new InvalidOperationException("StreamFactory returned null stream(s).");
                    }
                    
                    // Assign stream pair
                    _streamPair = streamPair;
                    _readStream = streamPair.ReadStream;
                    _writeStream = streamPair.WriteStream;
                    _pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
                    _readPipeReader = PipeReader.Create(_readStream, new StreamPipeReaderOptions(bufferSize: 16384));
                    _writeError = null;
                    
                    // Step 2: Perform handshake (with optional timeout)
                    await PerformHandshakeAsync(ct).ConfigureAwait(false);
                    
                    // Success!
                    _isConnected = true;
                    _currentConnectionAttempt = 0;
                    _disconnectReason = null;
                    _disconnectException = null;
                    
                    if (isReconnecting)
                    {
                        try { OnReconnected?.Invoke(); } catch { }
                    }
                    
                    return; // Exit retry loop on success
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    
                    // Clean up failed stream pair
                    await DisposeStreamsAsync().ConfigureAwait(false);
                    
                    // Apply exponential backoff for next attempt
                    var nextDelay = TimeSpan.FromMilliseconds(currentDelay.TotalMilliseconds * _options.AutoReconnectBackoffMultiplier);
                    currentDelay = nextDelay > _options.MaxAutoReconnectDelay ? _options.MaxAutoReconnectDelay : nextDelay;
                }
            }
        }
        finally
        {
            _isReconnecting = false;
        }
        
        // If we get here without returning, we were cancelled
        ct.ThrowIfCancellationRequested();
    }
    
    /// <summary>
    /// Performs handshake with the remote peer.
    /// </summary>
    private async Task PerformHandshakeAsync(CancellationToken ct)
    {
        // Reset handshake state
        _handshakeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        
        // Send handshake
        await SendHandshakeAsync(ct).ConfigureAwait(false);
        
        // Wait for handshake response with optional timeout
        if (_options.HandshakeTimeout != Timeout.InfiniteTimeSpan)
        {
            using var handshakeTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeoutCts.CancelAfter(_options.HandshakeTimeout);
            
            // Start read loop to receive handshake response
            var readTask = ReadSingleFrameAsync(handshakeTimeoutCts.Token);
            
            try
            {
                await Task.WhenAny(_handshakeCompleted.Task, readTask).ConfigureAwait(false);
                
                if (!_handshakeCompleted.Task.IsCompleted)
                {
                    // Keep reading until handshake completes
                    while (!_handshakeCompleted.Task.IsCompleted && !handshakeTimeoutCts.IsCancellationRequested)
                    {
                        await ReadSingleFrameAsync(handshakeTimeoutCts.Token).ConfigureAwait(false);
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
            // No timeout - read frames until handshake completes
            while (!_handshakeCompleted.Task.IsCompleted && !ct.IsCancellationRequested)
            {
                await ReadSingleFrameAsync(ct).ConfigureAwait(false);
            }
            
            await _handshakeCompleted.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
    
    /// <summary>
    /// Reads a single frame from the PipeReader (used during handshake).
    /// </summary>
    private async Task ReadSingleFrameAsync(CancellationToken ct)
    {
        var pipeReader = _readPipeReader ?? throw new InvalidOperationException("Not connected.");
        
        while (true)
        {
            var readResult = await pipeReader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = readResult.Buffer;
            
            if (TryParseFrame(ref buffer, ct))
            {
                pipeReader.AdvanceTo(buffer.Start, buffer.End);
                return;
            }
            
            pipeReader.AdvanceTo(buffer.Start, buffer.End);
            
            if (readResult.IsCompleted)
                throw new EndOfStreamException("Connection closed.");
        }
    }
    
    /// <summary>
    /// Disposes the current stream pair and clears stream references.
    /// </summary>
    private async Task DisposeStreamsAsync()
    {
        try
        {
            if (_readPipeReader != null)
            {
                await _readPipeReader.CompleteAsync().ConfigureAwait(false);
                _readPipeReader = null;
            }
        }
        catch { }
        
        try
        {
            if (_pipe != null)
            {
                await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
                await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
                _pipe = null;
            }
        }
        catch { }
        
        try
        {
            if (_streamPair != null)
            {
                await _streamPair.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch { }
        
        _streamPair = null;
        _readStream = null;
        _writeStream = null;
    }
    
    /// <summary>
    /// Runs the main processing loops after connection is established.
    /// </summary>
    private async Task RunProcessingLoopsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            // Create linked token to cancel all tasks when one fails
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedToken = linkedCts.Token;
            
            try
            {
                // Start background tasks with linked token
                _readTask = ReadLoopAsync(linkedToken);
                _pingTask = PingLoopAsync(linkedToken);
                
                // Start flush task for all modes (drains Pipe to stream)
                _flushTask = FlushLoopAsync(linkedToken);

                // Wait for any task to complete (or fail)
                var tasks = new[] { _readTask, _pingTask, _flushTask };
                
                // Wait for first task to complete
                var completedTask = await Task.WhenAny(tasks).ConfigureAwait(false);
                
                // Cancel remaining tasks
                await linkedCts.CancelAsync().ConfigureAwait(false);
                
                // Wait for all tasks to finish (they should exit due to cancellation)
                // Use try/catch to handle exceptions from cancelled and faulted tasks
                Exception? firstException = null;
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Capture the first non-cancellation exception
                    firstException = ex;
                }
                
                // Check if the first completed task faulted
                if (completedTask.IsFaulted)
                {
                    throw completedTask.Exception!.InnerException ?? completedTask.Exception;
                }
                
                // If we captured an exception from WhenAll, throw it
                if (firstException != null)
                {
                    throw firstException;
                }
                
                break; // Normal completion
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Transport error - attempt reconnection if enabled
                if (_options.EnableReconnection && !_disposed && !_goAwayReceived)
                {
                    _isConnected = false;
                    _disconnectReason = NetConduit.DisconnectReason.TransportError;
                    _disconnectException = ex;
                    
                    try { OnDisconnected?.Invoke(NetConduit.DisconnectReason.TransportError, ex); } catch { }
                    
                    // Clean up old stream pair
                    await DisposeStreamsAsync().ConfigureAwait(false);
                    
                    // Reconnect with same retry logic
                    await ConnectWithRetryAsync(isReconnecting: true, ct).ConfigureAwait(false);
                    
                    // Successfully reconnected - continue loop to restart processing tasks
                    continue;
                }
                else
                {
                    // No reconnection - propagate error
                    HandleTransportError(ex);
                    throw;
                }
            }
        }
    }


    
    /// <summary>
    /// Waits until the multiplexer is connected. Used by channel operations.
    /// </summary>
    private async Task WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        // If not running yet, throw immediately
        if (!_isRunning)
            throw new InvalidOperationException("Multiplexer is not running. Call Start() first.");
        
        // If already connected, return immediately
        if (_isConnected)
            return;
        
        // Wait for ready (first connection) or reconnection
        await _readyTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        
        // If reconnecting, wait for reconnection to complete
        while (_isReconnecting && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            if (_isConnected)
                return;
        }
        
        cancellationToken.ThrowIfCancellationRequested();
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
        
        // Wait until ready (handles both initial connection and reconnection)
        await WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        
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

        // Send INIT frame — writes to in-memory PipeWriter, always fast
        SendInit(channelIndex, options.ChannelId, options.Priority, cancellationToken);

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
    /// If StreamFactory is configured, auto-reconnection will be attempted.
    /// Otherwise, if reconnection is enabled, the multiplexer will wait for ReconnectAsync to be called.
    /// </summary>
    public void NotifyDisconnected()
    {
        if (!_options.EnableReconnection || _disposed)
            return;

        _isConnected = false;
        _disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disconnectReason = NetConduit.DisconnectReason.TransportError;
        
        try
        {
            OnDisconnected?.Invoke(NetConduit.DisconnectReason.TransportError, null);
        }
        catch
        {
            // Swallow exceptions from event handlers
        }
        
        // If StreamFactory is configured, attempt auto-reconnect
        if (_options.StreamFactory != null)
        {
            _autoReconnectCts = new CancellationTokenSource();
            _autoReconnectTask = Task.Run(() => AutoReconnectLoopAsync(
                new Exception("Connection manually disconnected"), 
                _autoReconnectCts.Token));
        }
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

    internal void SendDataFrame(uint channelIndex, ReadOnlyMemory<byte> data, ChannelPriority priority, CancellationToken ct)
    {
        var header = new FrameHeader(channelIndex, FrameFlags.Data, (uint)data.Length);
        SendFrameToWriter(header, data, priority >= ChannelPriority.High, ct);
    }
    
    private void SendFrameToWriter(FrameHeader header, ReadOnlyMemory<byte> payload, bool forceFlush, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var combinedLength = FrameHeader.Size + payload.Length;
        
        lock (_writeLock)
        {
            if (_writeError != null) throw new IOException("Write pipe failed.", _writeError);
            var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");
            var span = writer.GetSpan(combinedLength);
            header.Write(span);
            payload.Span.CopyTo(span[FrameHeader.Size..]);
            writer.Advance(combinedLength);
            
            _pendingFlush = true;
            if (_options.FlushMode == FlushMode.Immediate || forceFlush)
                SignalFlush();
        }
        
        Stats.AddBytesSent(FrameHeader.Size + payload.Length);
    }
    
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for signal or timer — whichever comes first
                await _flushSignal.WaitAsync(_options.FlushInterval, ct).ConfigureAwait(false);
                
                var profiling = HotPathProfiler.IsEnabled;
                long cycleStart = profiling ? HotPathProfiler.Timestamp() : 0;
                long t0;
                
                t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                var hasPendingGrants = !_pendingCreditChannels.IsEmpty;
                if (profiling) HotPathProfiler.RecordHasPendingGrantsScan(HotPathProfiler.Timestamp() - t0);
                
                if (_pendingFlush || hasPendingGrants)
                {
                    // Phase 1: Commit writes to Pipe under sync lock (~35-65ns)
                    lock (_writeLock)
                    {
                        var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");
                        
                        if (hasPendingGrants)
                        {
                            t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                            WritePendingCreditGrants(writer);
                            if (profiling) HotPathProfiler.RecordWritePendingGrants(HotPathProfiler.Timestamp() - t0);
                        }
                        
                        _pendingFlush = false;
                        t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                        CommitPipeWriter(writer);
                        if (profiling) HotPathProfiler.RecordCommitPipeWriter(HotPathProfiler.Timestamp() - t0);
                    }
                    // Writers unblocked — can GetSpan/Advance concurrently now
                    
                    // Phase 2: Drain pipe to stream under stream lock (I/O, ~100us-1ms)
                    t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                    await DrainPipeToStreamAsync(ct).ConfigureAwait(false);
                    if (profiling) HotPathProfiler.RecordDrainPipe(HotPathProfiler.Timestamp() - t0);
                    
                    if (profiling) HotPathProfiler.RecordFlushCycle(HotPathProfiler.Timestamp() - cycleStart);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Propagate stream errors to writers for fast-fail
                _writeError = ex;
            }
        }
        
        // Final drain: flush any remaining buffered data before shutdown
        // Stream is still open at this point (disposed after FlushLoop completes)
        try
        {
            await ForceFlushPipeToStreamAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — stream may already be broken
        }
    }
    
    internal void SignalFlush()
    {
        try { _flushSignal.Release(); }
        catch (SemaphoreFullException) { }
    }
    
    internal void EnqueuePendingCredit(ReadChannel channel)
    {
        _pendingCreditChannels.Enqueue(channel);
    }
    
    /// <summary>
    /// Flushes PipeWriter data synchronously under write lock, then calls CommitPipeWriter.
    /// With pauseWriterThreshold: 0, FlushAsync always completes synchronously.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CommitPipeWriter(PipeWriter writer)
    {
        var flushTask = writer.FlushAsync(CancellationToken.None);
        flushTask.GetAwaiter().GetResult();
    }
    
    /// <summary>
    /// Drains committed Pipe data to the underlying stream.
    /// Serialized by _streamLock — only one drain at a time (FlushLoop vs ForceFlush).
    /// </summary>
    private async ValueTask DrainPipeToStreamAsync(CancellationToken ct)
    {
        await _streamLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pipeReader = _pipe?.Reader;
            if (pipeReader != null && pipeReader.TryRead(out var readResult))
            {
                if (readResult.Buffer.Length > 0)
                {
                    var writeStream = _writeStream!;
                    await WriteBufferToStreamAsync(readResult.Buffer, writeStream, ct).ConfigureAwait(false);
                }
                pipeReader.AdvanceTo(readResult.Buffer.End);
            }
        }
        finally
        {
            _streamLock.Release();
        }
    }
    
    /// <summary>
    /// Commits buffered Pipe.Writer data and drains it to the underlying stream in one shot.
    /// Used by FIN/GoAway to guarantee delivery before dispose.
    /// </summary>
    private async ValueTask ForceFlushPipeToStreamAsync(CancellationToken ct)
    {
        lock (_writeLock)
        {
            var writer = _pipe?.Writer;
            if (writer == null) return;
            _pendingFlush = false;
            CommitPipeWriter(writer);
        }
        
        await DrainPipeToStreamAsync(ct).ConfigureAwait(false);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static async ValueTask WriteBufferToStreamAsync(ReadOnlySequence<byte> buffer, Stream writeStream, CancellationToken ct)
    {
        var profiling = HotPathProfiler.IsEnabled;
        if (profiling) HotPathProfiler.RecordDrainSegment(buffer.IsSingleSegment, buffer.Length);
        
        long t0 = profiling ? HotPathProfiler.Timestamp() : 0;
        if (buffer.IsSingleSegment)
        {
            await writeStream.WriteAsync(buffer.First, ct).ConfigureAwait(false);
        }
        else
        {
            // Write each segment directly — avoids renting+copying into contiguous array
            foreach (var segment in buffer)
            {
                await writeStream.WriteAsync(segment, ct).ConfigureAwait(false);
            }
        }
        if (profiling) HotPathProfiler.RecordStreamWrite(HotPathProfiler.Timestamp() - t0);
        
        t0 = profiling ? HotPathProfiler.Timestamp() : 0;
        await writeStream.FlushAsync(ct).ConfigureAwait(false);
        if (profiling) HotPathProfiler.RecordStreamFlush(HotPathProfiler.Timestamp() - t0);
    }
    
    private bool HasPendingCreditGrants()
    {
        return !_pendingCreditChannels.IsEmpty;
    }
    
    private void WritePendingCreditGrants(PipeWriter writer)
    {
        // Already under _writeLock — drain the pending credit queue
        while (_pendingCreditChannels.TryDequeue(out var channel))
        {
            var credits = channel.DrainPendingCredits();
            if (credits == 0) continue;
            
            const int frameLen = FrameHeader.Size + 9;
            var span = writer.GetSpan(frameLen);
            var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, 9);
            header.Write(span);
            span[FrameHeader.Size] = (byte)ControlSubtype.CreditGrant;
            BinaryPrimitives.WriteUInt32BigEndian(span[(FrameHeader.Size + 1)..], channel.ChannelIndex);
            BinaryPrimitives.WriteUInt32BigEndian(span[(FrameHeader.Size + 5)..], credits);
            writer.Advance(frameLen);
            
            Stats.AddBytesSent(frameLen);
        }
    }

    private void SendFrameDirect(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var combinedLength = FrameHeader.Size + payload.Length;
        
        lock (_writeLock)
        {
            var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");
            var span = writer.GetSpan(combinedLength);
            header.Write(span);
            if (!payload.IsEmpty)
                payload.Span.CopyTo(span[FrameHeader.Size..]);
            writer.Advance(combinedLength);
            
            _pendingFlush = true;
            if (_options.FlushMode == FlushMode.Immediate)
                SignalFlush();
        }
        
        Stats.AddBytesSent(FrameHeader.Size + payload.Length);
    }

    private async ValueTask SendHandshakeAsync(CancellationToken ct)
    {
        // Handshake is sent before FlushLoop starts — write directly to stream
        var payload = new byte[25];
        payload[0] = (byte)ControlSubtype.Handshake;
        _sessionId.TryWriteBytes(payload.AsSpan(1));
        
        // Generate random nonce for index space negotiation
        _localNonce = Random.Shared.NextInt64();
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(17), _localNonce);
        
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
        // INIT payload format: [priority: u8][id_length: u16 BE][channel_id: 0-1024B UTF8]
        var channelIdBytes = System.Text.Encoding.UTF8.GetBytes(channelId);
        var payload = new byte[1 + 2 + channelIdBytes.Length];
        payload[0] = (byte)priority;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), (ushort)channelIdBytes.Length);
        channelIdBytes.CopyTo(payload.AsSpan(3));
        
        var header = new FrameHeader(channelIndex, FrameFlags.Init, (uint)payload.Length);
        SendFrameDirect(header, payload, ct);
    }

    internal void SendFin(uint channelIndex, CancellationToken ct)
    {
        var header = new FrameHeader(channelIndex, FrameFlags.Fin, 0);
        SendFrameDirect(header, ReadOnlyMemory<byte>.Empty, ct);
        SignalFlush();
    }

    private void SendAck(uint channelIndex, uint credits, CancellationToken ct)
    {
        const int frameLen = FrameHeader.Size + 4;
        ct.ThrowIfCancellationRequested();
        
        lock (_writeLock)
        {
            var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");
            var span = writer.GetSpan(frameLen);
            var header = new FrameHeader(channelIndex, FrameFlags.Ack, 4);
            header.Write(span);
            BinaryPrimitives.WriteUInt32BigEndian(span[FrameHeader.Size..], credits);
            writer.Advance(frameLen);
            
            _pendingFlush = true;
            if (_options.FlushMode == FlushMode.Immediate)
                SignalFlush();
        }
        Stats.AddBytesSent(frameLen);
    }

    internal void SendCreditGrant(uint channelIndex, uint credits, CancellationToken ct)
    {
        const int frameLen = FrameHeader.Size + 9;
        ct.ThrowIfCancellationRequested();
        
        lock (_writeLock)
        {
            var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");
            var span = writer.GetSpan(frameLen);
            var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, 9);
            header.Write(span);
            span[FrameHeader.Size] = (byte)ControlSubtype.CreditGrant;
            BinaryPrimitives.WriteUInt32BigEndian(span[(FrameHeader.Size + 1)..], channelIndex);
            BinaryPrimitives.WriteUInt32BigEndian(span[(FrameHeader.Size + 5)..], credits);
            writer.Advance(frameLen);
            
            _pendingFlush = true;
            if (_options.FlushMode == FlushMode.Immediate)
                SignalFlush();
        }
        Stats.AddBytesSent(frameLen);
    }

    private void SendPing(CancellationToken ct)
    {
        const int frameLen = FrameHeader.Size + 9;
        ct.ThrowIfCancellationRequested();
        
        lock (_writeLock)
        {
            var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");
            var span = writer.GetSpan(frameLen);
            var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, 9);
            header.Write(span);
            span[FrameHeader.Size] = (byte)ControlSubtype.Ping;
            _lastPingTimestamp = DateTime.UtcNow.Ticks;
            BinaryPrimitives.WriteInt64BigEndian(span[(FrameHeader.Size + 1)..], _lastPingTimestamp);
            writer.Advance(frameLen);
            
            _pendingFlush = true;
            if (_options.FlushMode == FlushMode.Immediate)
                SignalFlush();
        }
        Stats.AddBytesSent(frameLen);
    }

    private void SendPong(long timestamp, CancellationToken ct)
    {
        const int frameLen = FrameHeader.Size + 9;
        ct.ThrowIfCancellationRequested();
        
        lock (_writeLock)
        {
            var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");
            var span = writer.GetSpan(frameLen);
            var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, 9);
            header.Write(span);
            span[FrameHeader.Size] = (byte)ControlSubtype.Pong;
            BinaryPrimitives.WriteInt64BigEndian(span[(FrameHeader.Size + 1)..], timestamp);
            writer.Advance(frameLen);
            
            _pendingFlush = true;
            if (_options.FlushMode == FlushMode.Immediate)
                SignalFlush();
        }
        Stats.AddBytesSent(frameLen);
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
        SendFrameDirect(header, payload, ct);
        
        // Force drain Pipe to stream so peer receives GoAway before stream is disposed
        await ForceFlushPipeToStreamAsync(ct).ConfigureAwait(false);
    }

    private void SendError(uint channelIndex, ErrorCode code, string message, CancellationToken ct)
    {
        var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var payload = new byte[2 + messageBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)code);
        messageBytes.CopyTo(payload.AsSpan(2));
        
        var header = new FrameHeader(channelIndex, FrameFlags.Err, (uint)payload.Length);
        SendFrameDirect(header, payload, ct);
    }

    #endregion

    #region Read Methods

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var pipeReader = _readPipeReader ?? throw new InvalidOperationException("Not connected.");
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var readResult = await pipeReader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = readResult.Buffer;
                
                // Parse as many complete frames as possible from the buffer
                while (TryParseFrame(ref buffer, ct))
                {
                }
                
                pipeReader.AdvanceTo(buffer.Start, buffer.End);
                
                if (readResult.IsCompleted)
                    throw new EndOfStreamException("Connection closed.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                // Connection closed - propagate to trigger reconnection
                throw;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
                throw;
            }
        }
        
        // Close accept channel
        _acceptChannel.Writer.TryComplete();
    }
    
    /// <summary>
    /// Tries to parse one complete frame from the buffer. Returns true if a frame was parsed.
    /// Advances buffer past the consumed frame.
    /// </summary>
    private bool TryParseFrame(ref ReadOnlySequence<byte> buffer, CancellationToken ct)
    {
        if (buffer.Length < FrameHeader.Size)
            return false;
        
        var profiling = HotPathProfiler.IsEnabled;
        long frameStart = profiling ? HotPathProfiler.Timestamp() : 0;
        long t0;
        
        // Parse header — fast path reads directly from contiguous FirstSpan
        t0 = profiling ? HotPathProfiler.Timestamp() : 0;
        FrameHeader header;
        if (buffer.FirstSpan.Length >= FrameHeader.Size)
        {
            header = FrameHeader.Read(buffer.FirstSpan);
        }
        else
        {
            Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
            buffer.Slice(0, FrameHeader.Size).CopyTo(headerBytes);
            header = FrameHeader.Read(headerBytes);
        }
        if (profiling) HotPathProfiler.RecordHeaderParse(HotPathProfiler.Timestamp() - t0);
        
        if (header.Length > _options.MaxFrameSize)
        {
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Frame size {header.Length} exceeds maximum {_options.MaxFrameSize}");
        }
        
        var totalFrameSize = FrameHeader.Size + (long)header.Length;
        if (buffer.Length < totalFrameSize)
            return false;
        
        Stats.AddBytesReceived(FrameHeader.Size + header.Length);
        
        int payloadLength = (int)header.Length;
        
        // Data frame fast path: copy into OwnedMemory, transfer to channel
        if (header.ChannelId != ChannelIndexLimits.ControlChannel
            && header.Flags == FrameFlags.Data
            && payloadLength > 0)
        {
            t0 = profiling ? HotPathProfiler.Timestamp() : 0;
            if (_readChannelsByIndex.TryGetValue(header.ChannelId, out var channel))
            {
                if (profiling) HotPathProfiler.RecordChannelLookup(HotPathProfiler.Timestamp() - t0);
                
                // Pass raw payload slice directly — channel PipeWriter handles buffering
                var payloadSlice = buffer.Slice(FrameHeader.Size, payloadLength);
                
                t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                channel.EnqueueData(payloadSlice);
                if (profiling) HotPathProfiler.RecordEnqueueData(HotPathProfiler.Timestamp() - t0);
            }
            else if (profiling)
            {
                HotPathProfiler.RecordChannelLookup(HotPathProfiler.Timestamp() - t0);
            }
            // else: discard unknown channel data
            
            if (profiling) HotPathProfiler.RecordParseFrame(frameStart);
            buffer = buffer.Slice(totalFrameSize);
            return true;
        }
        
        // Control/other frames
        if (payloadLength > 0)
        {
            var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
            try
            {
                buffer.Slice(FrameHeader.Size, payloadLength).CopyTo(payload);
                ProcessFrame(header, payload.AsMemory(0, payloadLength), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        else
        {
            ProcessFrame(header, ReadOnlyMemory<byte>.Empty, ct);
        }
        
        buffer = buffer.Slice(totalFrameSize);
        return true;
    }
    
    /// <summary>
    /// Handles transport errors by triggering auto-reconnect (if configured) or aborting all channels.
    /// </summary>
    private void HandleTransportError(Exception ex)
    {
        if (_disconnectReason.HasValue)
            return; // Already handled
            
        _disconnectReason = NetConduit.DisconnectReason.TransportError;
        _disconnectException = ex;
        _isConnected = false;
        
        // If StreamFactory is configured, attempt auto-reconnect (don't abort channels yet)
        if (_options.StreamFactory != null && _options.EnableReconnection && !_disposed)
        {
            // Fire disconnect event first for auto-reconnect case
            try
            {
                OnDisconnected?.Invoke(NetConduit.DisconnectReason.TransportError, ex);
            }
            catch
            {
                // Swallow exceptions from event handlers
            }
            
            _autoReconnectCts = new CancellationTokenSource();
            _autoReconnectTask = Task.Run(() => AutoReconnectLoopAsync(ex, _autoReconnectCts.Token));
            return;
        }
        
        // No auto-reconnect - abort all channels first, then fire disconnect event
        // (maintains original ordering for backward compatibility)
        AbortAllChannels(ex);
        
        try
        {
            OnDisconnected?.Invoke(NetConduit.DisconnectReason.TransportError, ex);
        }
        catch
        {
            // Swallow exceptions from event handlers
        }
    }
    
    /// <summary>
    /// Aborts all channels with the specified exception.
    /// </summary>
    private void AbortAllChannels(Exception ex)
    {
        // Abort all write channels
        foreach (var channel in _writeChannelsByIndex.Values)
        {
            channel.Abort(ChannelCloseReason.TransportFailed, ex);
        }
        
        // Abort all read channels
        foreach (var channel in _readChannelsByIndex.Values)
        {
            channel.Abort(ChannelCloseReason.TransportFailed, ex);
        }
    }
    
    /// <summary>
    /// Auto-reconnection loop using the configured StreamFactory.
    /// </summary>
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
                
                // Check if we've exceeded max attempts (0 = unlimited)
                if (maxAttempts > 0 && attempt > maxAttempts)
                {
                    var failEx = new MultiplexerException(
                        ErrorCode.Internal, 
                        $"Auto-reconnection failed after {maxAttempts} attempts.", 
                        lastException);
                    AbortAllChannels(failEx);
                    OnAutoReconnectFailed?.Invoke(failEx);
                    return;
                }
                
                // Notify listeners of reconnection attempt
                var eventArgs = new AutoReconnectEventArgs
                {
                    AttemptNumber = attempt,
                    MaxAttempts = maxAttempts,
                    NextDelay = currentDelay,
                    LastException = lastException
                };
                
                try
                {
                    OnAutoReconnecting?.Invoke(eventArgs);
                }
                catch
                {
                    // Swallow exceptions from event handlers
                }
                
                if (eventArgs.Cancel)
                {
                    var cancelEx = new OperationCanceledException("Auto-reconnection cancelled by event handler.");
                    AbortAllChannels(cancelEx);
                    OnAutoReconnectFailed?.Invoke(cancelEx);
                    return;
                }
                
                // Wait before attempting (skip delay on first attempt)
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
                    // Create new stream pair using factory
                    var newStreamPair = await _options.StreamFactory!(ct).ConfigureAwait(false);
                    
                    // Attempt reconnection with new streams
                    await ReconnectAsync(newStreamPair.ReadStream, newStreamPair.WriteStream, ct).ConfigureAwait(false);
                    
                    // Success! Update stream pair reference and clear disconnect state
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
                    
                    // Apply exponential backoff for next attempt
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

    private void ProcessFrame(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (header.ChannelId == ChannelIndexLimits.ControlChannel)
        {
            ProcessControlFrame(header, payload, ct);
            return;
        }

        var channelIndex = header.ChannelId; // ChannelId in header is actually the index

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
                    
                    if (_writeChannelsByIndex.TryGetValue(channelIndex, out var channel))
                    {
                        channel.GrantCredits(credits);
                    }
                }
                break;
                
            case ControlSubtype.GoAway:
                _goAwayReceived = true;
                _acceptChannel.Writer.TryComplete();
                
                // Fire OnDisconnected with GoAwayReceived reason
                if (!_disconnectReason.HasValue)
                {
                    _disconnectReason = NetConduit.DisconnectReason.GoAwayReceived;
                    
                    // Abort all channels with MuxDisposed reason
                    foreach (var channel in _writeChannelsByIndex.Values)
                    {
                        channel.Abort(ChannelCloseReason.MuxDisposed, null);
                    }
                    foreach (var channel in _readChannelsByIndex.Values)
                    {
                        channel.Abort(ChannelCloseReason.MuxDisposed, null);
                    }
                    
                    try
                    {
                        OnDisconnected?.Invoke(NetConduit.DisconnectReason.GoAwayReceived, null);
                    }
                    catch
                    {
                        // Swallow exceptions from event handlers
                    }
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
                // ReconnectAck is processed synchronously in ReconnectAsync
                break;
        }
    }

    private void ProcessReconnectFrame(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!_options.EnableReconnection)
        {
            SendError(0, ErrorCode.ProtocolError, "Reconnection not enabled", ct);
            return;
        }

        if (data.Length < 20) // 16B session_id + 4B channel_count
        {
            SendError(0, ErrorCode.ProtocolError, "Invalid RECONNECT payload", ct);
            return;
        }

        var incomingSessionId = new Guid(data.Span[..16]);
        var channelCount = BinaryPrimitives.ReadUInt32BigEndian(data.Span[16..]);

        // Verify this is a valid reconnection for this session
        if (incomingSessionId != _remoteSessionId && _remoteSessionId != Guid.Empty)
        {
            SendError(0, ErrorCode.SessionMismatch, "Session ID mismatch", ct);
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
        SendFrameDirect(header, payload, ct);
    }

    private void ProcessDataFrame(uint channelIndex, ReadOnlyMemory<byte> payload)
    {
        if (_readChannelsByIndex.TryGetValue(channelIndex, out var channel))
        {
            var seq = new ReadOnlySequence<byte>(payload);
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

        // Parse INIT payload: [priority: u8][id_length: u16 BE][channel_id: 0-1024B UTF8]
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
        
        // Check for duplicate ChannelId
        if (_readChannelsById.ContainsKey(channelId))
        {
            SendError(channelIndex, ErrorCode.ChannelExists, $"Channel '{channelId}' already exists", ct);
            return;
        }

        var options = new ChannelOptions 
        { 
            ChannelId = channelId,
            Priority = priority,
            MinCredits = _options.DefaultChannelOptions.MinCredits,
            MaxCredits = _options.DefaultChannelOptions.MaxCredits,
            SendTimeout = _options.DefaultChannelOptions.SendTimeout
        };
        
        var channel = new ReadChannel(this, channelIndex, channelId, priority, options);
        
        if (!_readChannelsByIndex.TryAdd(channelIndex, channel))
        {
            SendError(channelIndex, ErrorCode.ChannelExists, "Channel index already exists", ct);
            return;
        }

        if (!_readChannelsById.TryAdd(channelId, channel))
        {
            _readChannelsByIndex.TryRemove(channelIndex, out _);
            SendError(channelIndex, ErrorCode.ChannelExists, $"Channel '{channelId}' already exists", ct);
            return;
        }

        Stats.IncrementTotalChannelsOpened();
        Stats.IncrementOpenChannels();

        // Send ACK with initial credits from adaptive flow control
        SendAck(channelIndex, channel.GetInitialCredits(), ct);
        
        // Check if someone is waiting for this specific channel
        if (_pendingAccepts.TryRemove(channelId, out var tcs))
        {
            tcs.TrySetResult(channel);
        }
        else
        {
            // Add to accept queue (unbounded channel, always succeeds)
            _acceptChannel.Writer.TryWrite(channel);
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
                
                SendPing(ct);
                
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

        try
        {
            // Step 1: Send GoAway (fire-and-forget with timeout)
            if (_isRunning && !_goAwaySent)
            {
                try
                {
                    using var goAwayTimeoutCts = new CancellationTokenSource(_options.GracefulShutdownTimeout);
                    await SendGoAwayAsync(ErrorCode.None, goAwayTimeoutCts.Token).ConfigureAwait(false);
                    _goAwaySent = true;
                }
                catch
                {
                    // Ignore errors sending GoAway - transport may already be dead
                }
            }

            // Step 2: Abort all channels with MuxDisposed reason FIRST
            // This ensures channels know they're being closed due to mux disposal
            foreach (var channel in _writeChannelsByIndex.Values.ToArray())
            {
                channel.Abort(ChannelCloseReason.MuxDisposed, null);
            }
            
            foreach (var channel in _readChannelsByIndex.Values.ToArray())
            {
                channel.Abort(ChannelCloseReason.MuxDisposed, null);
            }

            // Step 3: Wait for channels to finish cleanup with timeout
            var gracefulTimeout = _options.GracefulShutdownTimeout;
            
            try
            {
                var channelDisposeTasks = new List<Task>();
                
                foreach (var channel in _writeChannelsByIndex.Values.ToArray())
                {
                    channelDisposeTasks.Add(channel.DisposeAsync().AsTask());
                }
                
                foreach (var channel in _readChannelsByIndex.Values.ToArray())
                {
                    channelDisposeTasks.Add(channel.DisposeAsync().AsTask());
                }
                
                if (channelDisposeTasks.Count > 0)
                {
                    await Task.WhenAny(
                        Task.WhenAll(channelDisposeTasks),
                        Task.Delay(gracefulTimeout)
                    ).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore errors during graceful shutdown
            }

            // Step 4: Set disconnect reason and fire event
            if (!_disconnectReason.HasValue)
            {
                _disconnectReason = NetConduit.DisconnectReason.LocalDispose;
                
                try
                {
                    OnDisconnected?.Invoke(NetConduit.DisconnectReason.LocalDispose, null);
                }
                catch
                {
                    // Swallow exceptions from event handlers
                }
            }

            _shutdownCts.Cancel();
            _acceptChannel.Writer.TryComplete();
            
            // Cancel auto-reconnect if in progress
            try
            {
                _autoReconnectCts?.Cancel();
                if (_autoReconnectTask != null)
                {
                    await Task.WhenAny(_autoReconnectTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore errors cancelling auto-reconnect
            }
            
            // Cancel any pending accepts
            foreach (var tcs in _pendingAccepts.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingAccepts.Clear();
            
            // Wait for tasks to complete (with timeout to avoid hanging)
            try
            {
                var tasks = new List<Task>();
                if (_readTask != null) tasks.Add(_readTask);
                if (_pingTask != null) tasks.Add(_pingTask);
                if (_flushTask != null) tasks.Add(_flushTask);
                
                if (tasks.Count > 0)
                {
                    await Task.WhenAny(
                        Task.WhenAll(tasks),
                        Task.Delay(TimeSpan.FromSeconds(1))
                    ).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore errors waiting for tasks
            }
        }
        finally
        {
            // Always clean up resources
            try
            {
                if (_streamPair != null)
                {
                    await _streamPair.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch { }
            
            _streamPair = null;
            _readStream = null;
            _writeStream = null;
            
            try { _streamLock.Dispose(); } catch { }
            try { _shutdownCts.Dispose(); } catch { }
            try { _reconnectLock.Dispose(); } catch { }
            try { _autoReconnectCts?.Dispose(); } catch { }
        }
    }
}
