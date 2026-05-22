using System.Text.Json;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transit.Message.UnitTests;

// Regression for issue #292: every dispose step must run unconditionally
// and exceptions must be aggregated, mirroring the pattern established by
// StreamPair after PR #224. A throw from one channel's DisposeAsync must
// not strand the other channel, the pending payload buffer, or the
// semaphores.
public sealed class DisposeAggregateErrorsTests
{
    [Fact]
    public async Task DisposeAsync_WriteChannelThrows_StillDisposesReadChannelAndAggregatesErrors()
    {
        var write = new ThrowingChannel(throwOnDispose: true);
        var read = new ThrowingChannel(throwOnDispose: false);
        var transit = new MessageTransit<string, string>(write, read, jsonOptions: (JsonSerializerOptions?)null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await transit.DisposeAsync());
        Assert.Equal("from write dispose", ex.Message);

        Assert.True(write.DisposeAsyncCalled, "Write dispose should have been attempted");
        Assert.True(read.DisposeAsyncCalled, "Read dispose must run even though write threw");
    }

    [Fact]
    public async Task DisposeAsync_BothChannelsThrow_AggregatesIntoAggregateException()
    {
        var write = new ThrowingChannel(throwOnDispose: true);
        var read = new ThrowingChannel(throwOnDispose: true);
        var transit = new MessageTransit<string, string>(write, read, jsonOptions: (JsonSerializerOptions?)null);

        var ex = await Assert.ThrowsAsync<AggregateException>(async () => await transit.DisposeAsync());
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.True(write.DisposeAsyncCalled);
        Assert.True(read.DisposeAsyncCalled);
    }

    [Fact]
    public void Dispose_WriteChannelThrows_StillDisposesReadChannel()
    {
        var write = new ThrowingChannel(throwOnDispose: true);
        var read = new ThrowingChannel(throwOnDispose: false);
        var transit = new MessageTransit<string, string>(write, read, jsonOptions: (JsonSerializerOptions?)null);

        Assert.Throws<InvalidOperationException>(() => transit.Dispose());

        Assert.True(write.DisposeCalled);
        Assert.True(read.DisposeCalled);
    }

    // Minimal IWriteChannel + IReadChannel test double whose only meaningful
    // behavior is to optionally throw from Dispose / DisposeAsync.
    private sealed class ThrowingChannel(bool throwOnDispose) : IWriteChannel, IReadChannel
    {
        public bool DisposeCalled;
        public bool DisposeAsyncCalled;

        public string ChannelId => "stub";
        public ChannelState State => ChannelState.Open;
        public bool IsReady => false;
        public bool IsConnected => false;
        public ChannelPriority Priority => ChannelPriority.Normal;
        public ChannelStats Stats => new();
        public ChannelCloseReason? CloseReason => null;
        public Exception? CloseException => null;

        public event EventHandler? Ready { add { } remove { } }
        public event EventHandler? Connected { add { } remove { } }
        public event EventHandler<DisconnectedEventArgs>? Disconnected { add { } remove { } }
        public event EventHandler<ChannelCloseEventArgs>? Closed { add { } remove { } }

        public Task WaitForReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask CloseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public Stream AsStream() => Stream.Null;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(0);

        public void Dispose()
        {
            DisposeCalled = true;
            if (throwOnDispose) throw new InvalidOperationException("from write dispose");
        }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCalled = true;
            if (throwOnDispose) throw new InvalidOperationException("from write dispose");
            return ValueTask.CompletedTask;
        }
    }
}
