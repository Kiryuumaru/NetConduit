using NetConduit.Enums;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression coverage for #163: <see cref="WriteChannel.MarkOpen"/> and
/// <see cref="ReadChannel.MarkOpen"/> performed a check-then-set on a volatile
/// <c>_state</c> field, so a concurrent <c>SetClosed</c> could land between
/// the check and the write and resurrect a Closed channel back to Open.
/// </summary>
public sealed class ChannelLifecycleRaceTests
{
    [Fact]
    public async Task WriteChannel_MarkOpen_RacingWithSetClosed_NeverResurrectsClosedToOpen()
    {
        const int iterations = 10_000;
        var owner = new NoopOwner();

        for (int i = 0; i < iterations; i++)
        {
            var channel = new WriteChannel(
                $"c{i}", (ushort)((i % 32_000) + 1), ChannelPriority.Normal,
                64 * 1024, TimeSpan.FromSeconds(30), owner);

            using var barrier = new Barrier(2);
            var openTask = Task.Run(() => { barrier.SignalAndWait(); channel.MarkOpen(); });
            var closeTask = Task.Run(() => { barrier.SignalAndWait(); channel.SetClosed(ChannelCloseReason.LocalClose); });
            await Task.WhenAll(openTask, closeTask).WaitAsync(TimeSpan.FromSeconds(5));

            // Once SetClosed has fully run, _state must be Closed regardless of who
            // the racer was. The bug let MarkOpen overwrite Closed → Open.
            Assert.Equal(ChannelState.Closed, channel.State);
        }
    }

    [Fact]
    public async Task ReadChannel_MarkOpen_RacingWithSetClosed_NeverResurrectsClosedToOpen()
    {
        const int iterations = 10_000;

        for (int i = 0; i < iterations; i++)
        {
            var channel = new ReadChannel(
                $"r{i}", (ushort)((i % 32_000) + 2), ChannelPriority.Normal, 64 * 1024);

            using var barrier = new Barrier(2);
            var openTask = Task.Run(() => { barrier.SignalAndWait(); channel.MarkOpen(); });
            var closeTask = Task.Run(() => { barrier.SignalAndWait(); channel.SetClosed(ChannelCloseReason.LocalClose); });
            await Task.WhenAll(openTask, closeTask).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ChannelState.Closed, channel.State);
        }
    }

    private sealed class NoopOwner : IChannelOwner
    {
        public void NotifyReady(WriteChannel channel) { }
        public void NotifyChannelCompleted(ushort channelIndex, string channelId) { }
        public void NotifyPendingAcceptCancelled(string channelId) { }
        public void NotifyChannelOpened(string channelId) { }
        public bool SendAck(ushort channelIndex, ulong consumedPosition) => true;
        public void NotifyEventHandlerException(Exception exception) { }
    }
}
