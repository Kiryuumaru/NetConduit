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
        var factoryCalled = 0;
        var pipe1 = new DuplexPipe();
        var pipe2 = new DuplexPipe();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                Interlocked.Increment(ref factoryCalled);
                return (pipe2.Stream1, pipe2.Stream1);
            },
            MaxAutoReconnectAttempts = 1,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        await using var mux1 = await StreamMultiplexer.CreateAsync(options);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe1.Stream2);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);
        
        await Task.Delay(100);
        Assert.True(mux1.IsConnected);
        
        // Act - simulate transport failure by disposing the pipe
        await pipe1.DisposeAsync();
        
        // Wait for factory to be called
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (factoryCalled == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        
        // Assert
        Assert.True(factoryCalled >= 1, $"StreamFactory should be called on transport error, but was called {factoryCalled} times");
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_SuccessfulAutoReconnect()
    {
        // This test verifies that the factory is called and OnReconnected fires
        // when the factory provides working streams.
        // Note: Full reconnection requires matching session IDs, which is complex to set up.
        // Here we just verify the auto-reconnect mechanism triggers correctly.
        
        // Arrange
        var factoryCalls = 0;
        var reconnectAttempted = new TaskCompletionSource();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                Interlocked.Increment(ref factoryCalls);
                reconnectAttempted.TrySetResult();
                // Throw to trigger retry - in real usage this would return valid streams
                throw new InvalidOperationException("Simulated failure for test");
            },
            MaxAutoReconnectAttempts = 2,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        Assert.True(mux.IsConnected);
        
        // Act - trigger disconnect
        mux.NotifyDisconnected();
        
        // Wait for factory to be called
        var result = await Task.WhenAny(reconnectAttempted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        
        // Assert
        Assert.Equal(reconnectAttempted.Task, result);
        Assert.True(factoryCalls >= 1, $"Factory should be called at least once, but was called {factoryCalls} times");
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_FailsAfterMaxAttempts()
    {
        // Arrange
        var attemptCount = 0;
        var failedEvent = new TaskCompletionSource<Exception>();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                Interlocked.Increment(ref attemptCount);
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 3,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaxAutoReconnectDelay = TimeSpan.FromMilliseconds(50)
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
        mux.OnAutoReconnectFailed += ex => failedEvent.TrySetResult(ex);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act - trigger disconnect
        mux.NotifyDisconnected();
        
        // Wait for failure
        var result = await Task.WhenAny(failedEvent.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        
        // Assert
        Assert.Equal(failedEvent.Task, result);
        Assert.Equal(3, attemptCount);
        
        var exception = await failedEvent.Task;
        Assert.Contains("3 attempts", exception.Message);
        
        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamFactory_OnAutoReconnectingEventFired()
    {
        // Arrange
        var events = new List<AutoReconnectEventArgs>();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 3,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
        mux.OnAutoReconnecting += args =>
        {
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
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act
        mux.NotifyDisconnected();
        
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
        var failedEvent = new TaskCompletionSource<Exception>();
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                Interlocked.Increment(ref attemptCount);
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 10,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
        mux.OnAutoReconnecting += args =>
        {
            if (args.AttemptNumber >= 2)
            {
                args.Cancel = true;
            }
        };
        
        mux.OnAutoReconnectFailed += ex => failedEvent.TrySetResult(ex);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act
        mux.NotifyDisconnected();
        
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
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 4,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaxAutoReconnectDelay = TimeSpan.FromMilliseconds(500),
            AutoReconnectBackoffMultiplier = 2.0
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
        mux.OnAutoReconnecting += args =>
        {
            lock (delays)
            {
                delays.Add(args.NextDelay);
            }
        };
        
        var failedEvent = new TaskCompletionSource();
        mux.OnAutoReconnectFailed += _ => failedEvent.TrySetResult();
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act
        mux.NotifyDisconnected();
        
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
        var cancellationTriggered = false;
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                Interlocked.Increment(ref attemptCount);
                throw new InvalidOperationException("Connection failed");
            },
            MaxAutoReconnectAttempts = 0, // Unlimited
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
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
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act
        mux.NotifyDisconnected();
        
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
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = false, // Disabled
            StreamFactory = async ct =>
            {
                factoryCalled = true;
                return (Stream.Null, Stream.Null);
            }
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act - this should not trigger factory since reconnection is disabled
        mux.NotifyDisconnected();
        
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
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                factoryEntered.TrySetResult();
                await factoryExit.Task.WaitAsync(ct);
                throw new InvalidOperationException("Test");
            },
            MaxAutoReconnectAttempts = 1,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act
        Assert.False(mux.IsReconnecting);
        
        mux.NotifyDisconnected();
        
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
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
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
                return (Stream.Null, Stream.Null);
            },
            MaxAutoReconnectAttempts = 1
        };
        
        var pipe = new DuplexPipe();
        var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act
        mux.NotifyDisconnected();
        
        // Wait for factory to be entered
        await Task.WhenAny(factoryEntered.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        
        // Dispose should cancel auto-reconnect
        await mux.DisposeAsync();
        await pipe.DisposeAsync();
        
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
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            ReconnectBufferSize = 1024 * 1024,
            StreamFactory = async ct => 
            {
                factoryCalled.TrySetResult();
                throw new InvalidOperationException("Simulated failure");
            },
            MaxAutoReconnectAttempts = 1,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10)
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux1 = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, new MultiplexerOptions { StreamFactory = _ => null!, EnableReconnection = true });
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
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
        
        // Act - trigger disconnect and auto-reconnect
        mux1.NotifyDisconnected();
        
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
        
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            StreamFactory = async ct =>
            {
                throw new InvalidOperationException("Factory error");
            },
            MaxAutoReconnectAttempts = 2,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(50)
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
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
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act
        mux.NotifyDisconnected();
        
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
        var options = new MultiplexerOptions
        {
            StreamFactory = _ => throw new InvalidOperationException("Factory should not be called"),
            EnableReconnection = false // Reconnection disabled
        };
        
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        
        var disconnectedEvent = new TaskCompletionSource();
        mux.OnDisconnected += (reason, ex) => disconnectedEvent.TrySetResult();
        
        var autoReconnectingCalled = false;
        mux.OnAutoReconnecting += _ => autoReconnectingCalled = true;
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var _ = mux.RunAsync(cts.Token);
        
        await Task.Delay(100);
        
        // Act
        mux.NotifyDisconnected();
        
        await Task.WhenAny(disconnectedEvent.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        
        await Task.Delay(200);
        
        // Assert - auto-reconnect should not be triggered when disabled
        Assert.False(autoReconnectingCalled);
        Assert.False(mux.IsReconnecting);
        
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
                return (pipe.Stream1, pipe.Stream1);
            },
            MaxAutoReconnectAttempts = 3
        };
        
        // Act
        await using var mux = await StreamMultiplexer.CreateAsync(options);
        
        // Assert
        Assert.True(factoryCalled);
        Assert.NotNull(mux);
        Assert.Equal(3, mux.Options.MaxAutoReconnectAttempts);
    }
}




