using NetConduit;

namespace NetConduit.UnitTests;

public class StreamPairTests
{
    private sealed class ThrowingStream(Exception toThrow) : Stream
    {
        public bool DisposeCalled { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        public override ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            throw toThrow;
        }
    }

    private sealed class TrackingStream : Stream
    {
        public bool DisposeCalled { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        public override ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingAsyncOwner : IAsyncDisposable
    {
        public bool DisposeCalled { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingSyncOwner : IDisposable
    {
        public bool DisposeCalled { get; private set; }

        public void Dispose() => DisposeCalled = true;
    }

    [Fact]
    public async Task DisposeAsync_ReadStreamThrows_StillDisposesWriteStreamAndAsyncOwner()
    {
        var readStream = new ThrowingStream(new IOException("peer reset"));
        var writeStream = new TrackingStream();
        var owner = new TrackingAsyncOwner();
        var pair = new StreamPair(readStream, writeStream, owner);

        await Assert.ThrowsAsync<IOException>(async () => await pair.DisposeAsync());

        Assert.True(readStream.DisposeCalled);
        Assert.True(writeStream.DisposeCalled);
        Assert.True(owner.DisposeCalled);
    }

    [Fact]
    public async Task DisposeAsync_ReadStreamThrows_StillDisposesSyncOwner()
    {
        var readStream = new ThrowingStream(new IOException("peer reset"));
        var writeStream = new TrackingStream();
        var owner = new TrackingSyncOwner();
        var pair = new StreamPair(readStream, writeStream, owner);

        await Assert.ThrowsAsync<IOException>(async () => await pair.DisposeAsync());

        Assert.True(writeStream.DisposeCalled);
        Assert.True(owner.DisposeCalled);
    }

    [Fact]
    public async Task DisposeAsync_WriteStreamThrows_StillDisposesAsyncOwner()
    {
        var readStream = new TrackingStream();
        var writeStream = new ThrowingStream(new IOException("peer reset"));
        var owner = new TrackingAsyncOwner();
        var pair = new StreamPair(readStream, writeStream, owner);

        await Assert.ThrowsAsync<IOException>(async () => await pair.DisposeAsync());

        Assert.True(readStream.DisposeCalled);
        Assert.True(owner.DisposeCalled);
    }

    [Fact]
    public async Task DisposeAsync_MultipleStepsThrow_AggregatesExceptions()
    {
        var readStream = new ThrowingStream(new IOException("read fault"));
        var writeStream = new ThrowingStream(new InvalidOperationException("write fault"));
        var owner = new TrackingSyncOwner();
        var pair = new StreamPair(readStream, writeStream, owner);

        var ex = await Assert.ThrowsAsync<AggregateException>(async () => await pair.DisposeAsync());

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e is IOException);
        Assert.Contains(ex.InnerExceptions, e => e is InvalidOperationException);
        Assert.True(owner.DisposeCalled);
    }

    [Fact]
    public async Task DisposeAsync_AllStepsSucceed_DoesNotThrow()
    {
        var stream = new TrackingStream();
        var owner = new TrackingAsyncOwner();
        var pair = new StreamPair(stream, owner);

        await pair.DisposeAsync();

        Assert.True(stream.DisposeCalled);
        Assert.True(owner.DisposeCalled);
    }
}
