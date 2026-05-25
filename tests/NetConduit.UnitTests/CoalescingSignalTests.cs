using NetConduit.Internal;

namespace NetConduit.UnitTests;

public class CoalescingSignalTests
{
    [Fact]
    public void Signal_then_Wait_returns_synchronously()
    {
        using var signal = new CoalescingSignal();
        signal.Signal();
        signal.Wait(CancellationToken.None);
    }

    [Fact]
    public void Multiple_signals_coalesce_into_single_wakeup()
    {
        using var signal = new CoalescingSignal();
        signal.Signal();
        signal.Signal();
        signal.Signal();

        signal.Wait(CancellationToken.None);

        // After Wait, the gate is reset. A second Wait must block — verify by
        // using a short cancellation timeout.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.Throws<OperationCanceledException>(() => signal.Wait(cts.Token));
    }

    [Fact]
    public void Wait_throws_OperationCanceledException_when_cancelled_before_signal()
    {
        using var signal = new CoalescingSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.Throws<OperationCanceledException>(() => signal.Wait(cts.Token));
    }

    [Fact]
    public async Task Dispose_during_Wait_does_not_throw_ObjectDisposedException_on_consumer_thread()
    {
        // Regression for issue: Dispose() calls Set() then Dispose() on the
        // underlying ManualResetEventSlim. A consumer woken by the Set() (rather
        // than by cancellation) would race to Reset() against the dispose and
        // throw ObjectDisposedException on the consumer thread. The fix tolerates
        // this race.
        for (int iteration = 0; iteration < 200; iteration++)
        {
            var signal = new CoalescingSignal();
            var consumerStarted = new ManualResetEventSlim(false);
            Exception? consumerException = null;

            var consumer = Task.Factory.StartNew(() =>
            {
                try
                {
                    consumerStarted.Set();
                    signal.Wait(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    consumerException = ex;
                }
            }, TaskCreationOptions.LongRunning);

            consumerStarted.Wait();
            // Tiny stagger to maximize the chance the consumer is parked inside
            // ManualResetEventSlim.Wait when Dispose races.
            await Task.Yield();

            signal.Dispose();
            await consumer;

            Assert.Null(consumerException);
        }
    }

    [Fact]
    public void Signal_after_Dispose_does_not_throw()
    {
        var signal = new CoalescingSignal();
        signal.Dispose();
        signal.Signal();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var signal = new CoalescingSignal();
        signal.Dispose();
        signal.Dispose();
    }

    [Fact]
    public async Task Concurrent_Signal_and_Dispose_does_not_throw()
    {
        // Producer thread fires Signal() in a tight loop while another thread
        // disposes the signal. With the volatile _disposed guard + swallowed
        // ObjectDisposedException, neither side should propagate an exception.
        for (int iteration = 0; iteration < 50; iteration++)
        {
            var signal = new CoalescingSignal();
            var stop = new ManualResetEventSlim(false);

            var producer = Task.Run(() =>
            {
                while (!stop.IsSet)
                    signal.Signal();
            });

            await Task.Delay(2);
            signal.Dispose();
            stop.Set();

            await producer;
        }
    }
}
