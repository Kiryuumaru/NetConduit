using System.Text.Json.Serialization;
using Xunit;

namespace NetConduit.Transit.Message.UnitTests;

/// <summary>
/// Regression tests for: MessageTransit.DisposeAsync / Dispose must run every
/// step even if an earlier one throws, and aggregate the failures. Otherwise a
/// throwing _writeChannel.DisposeAsync skips _readChannel disposal (slab leak),
/// the pending payload buffer return (up to 16 MiB ArrayPool rental leak), and
/// the SemaphoreSlim disposals.
/// </summary>
public sealed partial class MessageTransitDisposeAggregateTests
{
    [JsonSerializable(typeof(string))]
    internal sealed partial class StringContext : JsonSerializerContext { }

    private sealed class DisposeTrackingChannel(bool throwOnDispose) : IWriteChannel, IReadChannel
    {
        public int SyncDisposeCalls;
        public int AsyncDisposeCalls;

        public string ChannelId => "track";
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
        public System.IO.Stream AsStream() => System.IO.Stream.Null;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(0);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref AsyncDisposeCalls);
            if (throwOnDispose)
                return ValueTask.FromException(new InvalidOperationException("simulated channel dispose failure"));
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref SyncDisposeCalls);
            if (throwOnDispose)
                throw new InvalidOperationException("simulated channel dispose failure");
        }
    }

    private static MessageTransit<string, string> CreateTransit(IWriteChannel write, IReadChannel read) =>
        new(write, read, StringContext.Default.String, StringContext.Default.String);

    [Fact]
    public async Task DisposeAsync_WriteThrows_StillDisposesRead_AndSurfacesError()
    {
        var write = new DisposeTrackingChannel(throwOnDispose: true);
        var read = new DisposeTrackingChannel(throwOnDispose: false);
        var transit = CreateTransit(write, read);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await transit.DisposeAsync());

        Assert.Equal(1, write.AsyncDisposeCalls);
        Assert.Equal(1, read.AsyncDisposeCalls);
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task DisposeAsync_BothThrow_AggregatesErrors()
    {
        var write = new DisposeTrackingChannel(throwOnDispose: true);
        var read = new DisposeTrackingChannel(throwOnDispose: true);
        var transit = CreateTransit(write, read);

        var agg = await Assert.ThrowsAsync<AggregateException>(async () => await transit.DisposeAsync());
        Assert.Equal(2, agg.InnerExceptions.Count);
        Assert.Equal(1, write.AsyncDisposeCalls);
        Assert.Equal(1, read.AsyncDisposeCalls);
    }

    [Fact]
    public void Dispose_WriteThrows_StillDisposesRead_AndSurfacesError()
    {
        var write = new DisposeTrackingChannel(throwOnDispose: true);
        var read = new DisposeTrackingChannel(throwOnDispose: false);
        var transit = CreateTransit(write, read);

        var ex = Assert.ThrowsAny<Exception>(() => transit.Dispose());

        Assert.Equal(1, write.SyncDisposeCalls);
        Assert.Equal(1, read.SyncDisposeCalls);
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Dispose_BothThrow_AggregatesErrors()
    {
        var write = new DisposeTrackingChannel(throwOnDispose: true);
        var read = new DisposeTrackingChannel(throwOnDispose: true);
        var transit = CreateTransit(write, read);

        var agg = Assert.Throws<AggregateException>(() => transit.Dispose());
        Assert.Equal(2, agg.InnerExceptions.Count);
        Assert.Equal(1, write.SyncDisposeCalls);
        Assert.Equal(1, read.SyncDisposeCalls);
    }
}
