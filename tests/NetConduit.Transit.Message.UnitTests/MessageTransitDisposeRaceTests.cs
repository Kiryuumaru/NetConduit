using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Transit.Message;

namespace NetConduit.Transit.Message.UnitTests;

// Regression coverage for #219: MessageTransit.Dispose / DisposeAsync used to
// dispose the SemaphoreSlim mutexes without coordinating with an in-flight
// SendAsync / ReceiveAsync. The in-flight operation's finally block would call
// _sendLock.Release() on an already-disposed semaphore and surface
// ObjectDisposedException("The semaphore has been disposed."), masking any
// genuine channel exception and confusing callers.
public class MessageTransitDisposeRaceTests
{
    public class TestMessage
    {
        public string? Text { get; set; }
    }

    [Fact]
    public async Task DisposeAsync_WhileSendInFlight_DoesNotThrowSemaphoreObjectDisposed()
    {
        var slowChannel = new GatedChannel();

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(slowChannel, null);
#pragma warning restore IL2026, IL3050

        var sendTask = Task.Run(async () =>
        {
            try
            {
                await transit.SendAsync(new TestMessage { Text = "hello" }, CancellationToken.None);
                return (Threw: false, Exception: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Threw: true, Exception: ex);
            }
        });

        // Let SendAsync enter the lock and park inside WriteAsync.
        await slowChannel.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await transit.DisposeAsync();

        var (threw, exception) = await sendTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Acceptable outcomes: clean completion, OperationCanceledException
        // (linked dispose CTS), or a channel-level exception. NOT acceptable:
        // ObjectDisposedException whose message names the semaphore — that's
        // the #219 bug surfacing.
        if (threw && exception is ObjectDisposedException ode &&
            ode.Message.Contains("semaphore", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail("Dispose raced with in-flight SendAsync and surfaced semaphore ODE: " + ode.Message);
        }
    }

    [Fact]
    public async Task DisposeAsync_WhileReceiveInFlight_DoesNotThrowSemaphoreObjectDisposed()
    {
        var slowChannel = new GatedChannel();

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(null, slowChannel);
#pragma warning restore IL2026, IL3050

        var receiveTask = Task.Run(async () =>
        {
            try
            {
                await transit.ReceiveAsync(CancellationToken.None);
                return (Threw: false, Exception: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Threw: true, Exception: ex);
            }
        });

        await slowChannel.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await transit.DisposeAsync();

        var (threw, exception) = await receiveTask.WaitAsync(TimeSpan.FromSeconds(5));

        if (threw && exception is ObjectDisposedException ode &&
            ode.Message.Contains("semaphore", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail("Dispose raced with in-flight ReceiveAsync and surfaced semaphore ODE: " + ode.Message);
        }
    }

    [Fact]
    public async Task Dispose_Sync_WhileSendInFlight_DoesNotThrowSemaphoreObjectDisposed()
    {
        var slowChannel = new GatedChannel();

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(slowChannel, null);
#pragma warning restore IL2026, IL3050

        var sendTask = Task.Run(async () =>
        {
            try
            {
                await transit.SendAsync(new TestMessage { Text = "hello" }, CancellationToken.None);
                return (Threw: false, Exception: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Threw: true, Exception: ex);
            }
        });

        await slowChannel.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Sync dispose must observe the same drain ordering as DisposeAsync.
        await Task.Run(() => transit.Dispose()).WaitAsync(TimeSpan.FromSeconds(10));

        var (threw, exception) = await sendTask.WaitAsync(TimeSpan.FromSeconds(5));

        if (threw && exception is ObjectDisposedException ode &&
            ode.Message.Contains("semaphore", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail("Sync Dispose raced with in-flight SendAsync and surfaced semaphore ODE: " + ode.Message);
        }
    }

    [Fact]
    public async Task DisposeAsync_CancelsInFlightSend()
    {
        var slowChannel = new GatedChannel();

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(slowChannel, null);
#pragma warning restore IL2026, IL3050

        var sendTask = Task.Run(async () =>
        {
            try
            {
                await transit.SendAsync(new TestMessage { Text = "hello" }, CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        await slowChannel.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposeTask = transit.DisposeAsync().AsTask();

        // The dispose path must cancel the in-flight send, freeing the lock,
        // so dispose itself completes within the drain window rather than
        // waiting on a wedged operation.
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(6));

        // SendAsync should have unwound (OCE) instead of returning normally,
        // because GatedChannel.WriteAsync was still parked when Dispose ran.
        var sendOutcome = await sendTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsAssignableFrom<OperationCanceledException>(sendOutcome);
    }

    // Channel that parks WriteAsync / ReadAsync until either the test releases
    // a gate or the caller's CancellationToken fires. Signals an entry TCS so
    // tests can synchronize on "the protected section is now occupied".
    private sealed class GatedChannel : IWriteChannel, IReadChannel
    {
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ChannelId => "gated";
        public ChannelState State => ChannelState.Open;
        public bool IsReady => true;
        public bool IsConnected => true;
        public ChannelPriority Priority => ChannelPriority.Normal;
        public ChannelStats Stats { get; } = new();
        public ChannelCloseReason? CloseReason => null;
        public Exception? CloseException => null;

        public event EventHandler? Ready { add { } remove { } }
        public event EventHandler? Connected { add { } remove { } }
        public event EventHandler<DisconnectedEventArgs>? Disconnected { add { } remove { } }
        public event EventHandler<ChannelCloseEventArgs>? Closed { add { } remove { } }

        public Task WaitForReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask CloseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public Stream AsStream() => Stream.Null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            WriteEntered.TrySetResult();
            await Task.WhenAny(_release.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadEntered.TrySetResult();
            await Task.WhenAny(_release.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return 0;
        }
    }
}
