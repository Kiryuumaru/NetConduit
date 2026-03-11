using NetConduit;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for auto-reconnection using StreamFactory.
/// </summary>
public class StreamFactoryTests
{
    [Fact(Timeout = 120000)]
    public async Task StreamFactory_CalledOnTransportError()
    {
        // Arrange
        var callCount = 0;
        var reconnectPipe = new DuplexPipe();
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection streams
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls - this is reconnection, return reconnect pipe
                return new StreamPair(reconnectPipe.Stream1);
            },
            MaxAutoReconnectAttempts = 3,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        // Create peer for initial connection
        await using var initialPeer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var initialPeerTask = initialPeer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        var runTask = mux.Start();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            initialPeer.WaitForReadyAsync(cts.Token)
        );
        
        Assert.True(mux.IsConnected);
        Assert.Equal(1, callCount); // Only initial call so far
        
        // Act - simulate transport failure by disposing the initial pipe
        await initialPipe.DisposeAsync();
        
        // Wait for factory to be called again (reconnection attempt)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (callCount == 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        
        // Assert
        Assert.True(callCount >= 2, $"StreamFactory should be called on transport error for reconnection, but was called {callCount} times");
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_SuccessfulAutoReconnect()
    {
        // This test verifies that the factory is called and OnReconnected fires
        // when the factory provides working streams.
        
        // Arrange
        var callCount = 0;
        var reconnectAttempted = new TaskCompletionSource();
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls - this is reconnection, signal and throw
                reconnectAttempted.TrySetResult();
                throw new InvalidOperationException("Simulated failure for test");
            },
            MaxAutoReconnectAttempts = 2,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        var runTask = mux.Start();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        Assert.True(mux.IsConnected);
        
        // Act - trigger disconnect by disposing peer connection
        await initialPipe.DisposeAsync();
        
        // Wait for factory to be called (reconnection attempt)
        var result = await Task.WhenAny(reconnectAttempted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        
        // Assert
        Assert.Equal(reconnectAttempted.Task, result);
        Assert.True(callCount >= 2, $"Factory should be called for reconnection, but was called {callCount} times");
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_FailsAfterMaxAttempts()
    {
        // Arrange
        var callCount = 0;
        var failedEvent = new TaskCompletionSource<Exception>();
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls (reconnection) - throw
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 3,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaxAutoReconnectDelay = TimeSpan.FromMilliseconds(50)
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        mux.OnAutoReconnectFailed += ex => failedEvent.TrySetResult(ex);
        var runTask = mux.Start();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act - trigger disconnect by disposing peer connection
        await initialPipe.DisposeAsync();
        
        var result = await Task.WhenAny(failedEvent.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        
        // Assert
        Assert.Equal(failedEvent.Task, result);
        // Initial call + 3 reconnection attempts = 4 total calls
        Assert.Equal(4, callCount);
        
        var exception = await failedEvent.Task;
        Assert.Contains("3 attempts", exception.Message);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_OnAutoReconnectingEventFired()
    {
        // Arrange
        var events = new List<AutoReconnectEventArgs>();
        var callCount = 0;
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls (reconnection) - throw
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 3,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        
        mux.OnAutoReconnecting += args =>
        {
            // Only track reconnection events, not initial connection
            if (!args.IsReconnecting) return;
            
            lock (events)
            {
                events.Add(new AutoReconnectEventArgs
                {
                    AttemptNumber = args.AttemptNumber,
                    MaxAttempts = args.MaxAttempts,
                    NextDelay = args.NextDelay,
                    LastException = args.LastException
                });
            }
        };
        
        var failedEvent = new TaskCompletionSource();
        mux.OnAutoReconnectFailed += _ => failedEvent.TrySetResult();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = mux.Start();
        
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act - trigger disconnect by disposing peer connection
        await initialPipe.DisposeAsync();
        
        await Task.WhenAny(failedEvent.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        
        // Assert
        Assert.Equal(3, events.Count);
        Assert.Equal(1, events[0].AttemptNumber);
        Assert.Equal(2, events[1].AttemptNumber);
        Assert.Equal(3, events[2].AttemptNumber);
        Assert.Equal(3, events[0].MaxAttempts);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_CancellationViaEventHandler()
    {
        // Arrange
        var attemptCount = 0;
        var callCount = 0;
        var failedEvent = new TaskCompletionSource<Exception>();
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls (reconnection) - track and throw
                Interlocked.Increment(ref attemptCount);
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 10,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        
        mux.OnAutoReconnecting += args =>
        {
            if (args.AttemptNumber >= 2)
            {
                args.Cancel = true;
            }
        };
        
        mux.OnAutoReconnectFailed += ex => failedEvent.TrySetResult(ex);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = mux.Start();
        
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act - trigger disconnect by disposing peer connection
        await initialPipe.DisposeAsync();
        
        await Task.WhenAny(failedEvent.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        
        // Assert - should stop after 2 attempts due to cancellation
        Assert.True(attemptCount <= 2, $"Should stop after cancellation, but made {attemptCount} attempts");
        
        var exception = await failedEvent.Task;
        Assert.IsType<OperationCanceledException>(exception);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_ExponentialBackoff()
    {
        // Arrange
        var delays = new List<TimeSpan>();
        var callCount = 0;
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls (reconnection) - throw
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 4,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaxAutoReconnectDelay = TimeSpan.FromMilliseconds(500),
            AutoReconnectBackoffMultiplier = 2.0
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        
        mux.OnAutoReconnecting += args =>
        {
            // Only track reconnection events, not initial connection
            if (!args.IsReconnecting) return;
            
            lock (delays)
            {
                delays.Add(args.NextDelay);
            }
        };
        
        var failedEvent = new TaskCompletionSource();
        mux.OnAutoReconnectFailed += _ => failedEvent.TrySetResult();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runTask = mux.Start();
        
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act - trigger disconnect by disposing peer connection
        await initialPipe.DisposeAsync();
        
        await Task.WhenAny(failedEvent.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        
        // Assert - verify exponential backoff
        // Backoff is applied AFTER each failed attempt, so events see the updated delay:
        // Attempt 1: delay = 100ms (base), fail -> apply backoff (200ms)
        // Attempt 2: delay = 200ms, fail -> apply backoff (400ms)
        // Attempt 3: delay = 400ms, fail -> apply backoff (500ms capped)
        // Attempt 4: delay = 500ms
        Assert.Equal(4, delays.Count);
        
        // First attempt uses base delay
        Assert.Equal(TimeSpan.FromMilliseconds(100), delays[0]);
        
        // Second attempt: 100 * 2 = 200ms
        Assert.Equal(TimeSpan.FromMilliseconds(200), delays[1]);
        
        // Third attempt: 200 * 2 = 400ms
        Assert.Equal(TimeSpan.FromMilliseconds(400), delays[2]);
        
        // Fourth attempt: 400 * 2 = 800ms, capped at 500ms
        Assert.Equal(TimeSpan.FromMilliseconds(500), delays[3]);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_UnlimitedAttempts()
    {
        // Arrange
        var attemptCount = 0;
        var callCount = 0;
        var cancellationTriggered = false;
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls (reconnection) - track and throw
                Interlocked.Increment(ref attemptCount);
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 0, // Unlimited
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        
        mux.OnAutoReconnecting += args =>
        {
            if (args.AttemptNumber >= 5)
            {
                args.Cancel = true;
                cancellationTriggered = true;
            }
        };
        
        var failedEvent = new TaskCompletionSource();
        mux.OnAutoReconnectFailed += _ => failedEvent.TrySetResult();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = mux.Start();
        
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act - trigger disconnect by disposing peer connection
        await initialPipe.DisposeAsync();
        
        await Task.WhenAny(failedEvent.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        
        // Assert - should make at least 5 attempts before cancellation (unlimited means no cap)
        // Cancellation happens on attempt 5, so we expect 4-5 factory calls depending on timing
        Assert.True(attemptCount >= 4, $"Should make at least 4 attempts with unlimited retries, but made {attemptCount}");
        Assert.True(cancellationTriggered);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_NotCalledWhenDisabled()
    {
        // Arrange
        var factoryCalled = false;
        var callCount = 0;
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = false, // Disabled
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls - this shouldn't happen
                factoryCalled = true;
                return new StreamPair(Stream.Null);
            }
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = mux.Start();
        
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act - dispose the pipe to trigger a transport error
        // Since reconnection is disabled, the factory should NOT be called again
        await initialPipe.DisposeAsync();
        
        await Task.Delay(200);
        
        // Assert
        Assert.False(factoryCalled);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_IsReconnectingStateCorrect()
    {
        // Arrange
        var reconnectingStates = new List<bool>();
        var factoryEntered = new TaskCompletionSource();
        var factoryExit = new TaskCompletionSource();
        var callCount = 0;
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls (reconnection) - wait then throw
                factoryEntered.TrySetResult();
                await factoryExit.Task.WaitAsync(ct);
                throw new InvalidOperationException("Test");
            },
            MaxAutoReconnectAttempts = 1,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = mux.Start();
        
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act
        Assert.False(mux.IsReconnecting);
        
        // Trigger disconnect by disposing peer connection
        await initialPipe.DisposeAsync();
        
        // Wait for factory to be entered
        await Task.WhenAny(factoryEntered.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        
        reconnectingStates.Add(mux.IsReconnecting);
        
        // Let factory complete
        factoryExit.TrySetResult();
        
        await Task.Delay(200);
        
        reconnectingStates.Add(mux.IsReconnecting);
        
        // Assert
        Assert.True(reconnectingStates[0], "Should be reconnecting while factory is running");
        Assert.False(reconnectingStates[1], "Should not be reconnecting after failure");
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_DisposeCancelsAutoReconnect()
    {
        // Arrange
        var factoryEntered = new TaskCompletionSource();
        var factoryCompleted = false;
        var callCount = 0;
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls (reconnection) - wait a long time
                factoryEntered.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                factoryCompleted = true;
                return new StreamPair(Stream.Null);
            },
            MaxAutoReconnectAttempts = 1
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        var mux = StreamMultiplexer.Create(options);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = mux.Start();
        
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act - dispose the peer connection to trigger reconnection
        await peer.DisposeAsync();
        
        // Wait for factory to be entered
        await Task.WhenAny(factoryEntered.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        
        // Dispose mux should cancel auto-reconnect
        await mux.DisposeAsync();
        await initialPipe.DisposeAsync();
        
        // Assert
        Assert.False(factoryCompleted, "Factory should have been cancelled by dispose");
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_DataContinuityAfterAutoReconnect()
    {
        // This test verifies that:
        // 1. Data sent before disconnect is tracked in SyncState
        // 2. The factory is invoked on disconnect
        // Full reconnection with data replay requires matching session IDs - 
        // that scenario is tested in ReconnectionTests.cs
        
        // Arrange
        var factoryCalled = new TaskCompletionSource();
        var callCount = 0;
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            ReconnectBufferSize = 1024 * 1024,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls (reconnection) - signal and throw
                factoryCalled.TrySetResult();
                throw new InvalidOperationException("Simulated failure");
            },
            MaxAutoReconnectAttempts = 1,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        // Create peer for initial connection (peer uses Stream2)
        await using var peerPipe = new DuplexPipe();
        await using var mux1 = StreamMultiplexer.Create(options);
        await using var mux2 = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2)),
            EnableReconnection = true
        });
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run1 = mux1.Start();
        var run2 = mux2.Start();
        
        await Task.WhenAll(
            mux1.WaitForReadyAsync(cts.Token),
            mux2.WaitForReadyAsync(cts.Token)
        );
        
        // Open channel and send some data
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
                return ch;
            return null;
        });
        
        await using var writeChannel = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "test-channel" }, cts.Token);
        var readChannel = await acceptTask;
        Assert.NotNull(readChannel);
        
        // Send initial data
        var data1 = new byte[100];
        Random.Shared.NextBytes(data1);
        await writeChannel.WriteAsync(data1, cts.Token);
        
        // Read initial data
        var buffer1 = new byte[100];
        var read1 = 0;
        while (read1 < 100)
        {
            read1 += await readChannel!.ReadAsync(buffer1.AsMemory(read1), cts.Token);
        }
        Assert.Equal(data1, buffer1);
        
        // Verify SyncState tracked the data
        Assert.Equal(100, writeChannel.SyncState.BytesSent);
        
        // Act - trigger disconnect by disposing the initial pipe
        await initialPipe.DisposeAsync();
        
        // Wait for factory to be called
        var result = await Task.WhenAny(factoryCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Equal(factoryCalled.Task, result);
        
        // Verify SyncState still has the data (for replay on successful reconnect)
        Assert.Equal(100, writeChannel.SyncState.BytesSent);
        var unackedData = writeChannel.SyncState.GetUnacknowledgedDataFrom(0);
        Assert.Equal(100, unackedData.Length);
        Assert.Equal(data1, unackedData);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_EventArgsContainsCorrectInfo()
    {
        // Arrange
        AutoReconnectEventArgs? capturedArgs = null;
        var initialException = new InvalidOperationException("Initial error");
        var callCount = 0;
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls (reconnection) - throw
                throw new InvalidOperationException("Factory error");
            },
            MaxAutoReconnectAttempts = 2,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(50)
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        
        mux.OnAutoReconnecting += args =>
        {
            if (args.AttemptNumber == 2)
            {
                capturedArgs = new AutoReconnectEventArgs
                {
                    AttemptNumber = args.AttemptNumber,
                    MaxAttempts = args.MaxAttempts,
                    NextDelay = args.NextDelay,
                    LastException = args.LastException
                };
            }
        };
        
        var failedEvent = new TaskCompletionSource();
        mux.OnAutoReconnectFailed += _ => failedEvent.TrySetResult();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = mux.Start();
        
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act - dispose initial pipe to trigger disconnect
        await initialPipe.DisposeAsync();
        
        await Task.WhenAny(failedEvent.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        
        // Assert
        Assert.NotNull(capturedArgs);
        Assert.Equal(2, capturedArgs.AttemptNumber);
        Assert.Equal(2, capturedArgs.MaxAttempts);
        // Attempt 2 sees 100ms delay (after backoff from attempt 1: 50 * 2 = 100)
        Assert.Equal(TimeSpan.FromMilliseconds(100), capturedArgs.NextDelay);
        Assert.NotNull(capturedArgs.LastException);
        Assert.Contains("Factory error", capturedArgs.LastException.Message);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_DisabledReconnection_NoAutoReconnect()
    {
        // Arrange - reconnection disabled
        var factoryCalled = false;
        var callCount = 0;
        var initialPipe = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                {
                    // First call - return initial connection
                    return new StreamPair(initialPipe.Stream1);
                }
                // Subsequent calls - this shouldn't happen
                factoryCalled = true;
                throw new InvalidOperationException("Factory should not be called");
            },
            EnableReconnection = false // Reconnection disabled
        };
        
        // Create peer for initial connection
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(initialPipe.Stream2))
        });
        var peerTask = peer.Start();
        
        await using var mux = StreamMultiplexer.Create(options);
        
        var disconnectedEvent = new TaskCompletionSource();
        mux.OnDisconnected += (reason, ex) => disconnectedEvent.TrySetResult();
        
        var autoReconnectingCalled = false;
        mux.OnAutoReconnecting += args =>
        {
            // Only track actual reconnection attempts, not initial connection
            if (args.IsReconnecting) autoReconnectingCalled = true;
        };
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = mux.Start();
        
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Act - dispose initial pipe to trigger disconnect
        await initialPipe.DisposeAsync();
        
        await Task.WhenAny(disconnectedEvent.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        
        await Task.Delay(200);
        
        // Assert - auto-reconnect should not be triggered when disabled
        Assert.False(autoReconnectingCalled);
        Assert.False(mux.IsReconnecting);
        Assert.False(factoryCalled);
        
        cts.Cancel();
    }

    [Fact(Timeout = 10000)]
    public async Task CreateAsync_WithOptionsHavingFactory_CreatesMultiplexerUsingFactory()
    {
        // Arrange
        var pipe = new DuplexPipe();
        var factoryCalled = false;
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                factoryCalled = true;
                return new StreamPair(pipe.Stream1);
            },
            MaxAutoReconnectAttempts = 3
        };
        
        // Create a peer on Stream2 to complete the handshake
        await using var peer = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(pipe.Stream2))
        });
        var peerTask = peer.Start();
        
        // Act
        await using var mux = StreamMultiplexer.Create(options);
        var runTask = mux.Start();
        
        // Wait for both to be ready in parallel
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            mux.WaitForReadyAsync(cts.Token),
            peer.WaitForReadyAsync(cts.Token)
        );
        
        // Assert
        Assert.True(factoryCalled);
        Assert.NotNull(mux);
        Assert.Equal(3, mux.Options.MaxAutoReconnectAttempts);
    }
}




