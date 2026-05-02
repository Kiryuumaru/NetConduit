namespace NetConduit.UnitTests;

/// <summary>
/// An in-memory bidirectional stream pair for testing.
/// Writing to one side makes data available on the other side's read.
/// </summary>
internal sealed class DuplexMemoryStream : IStreamPair
{
    private readonly MemoryStream _aToB = new();
    private readonly MemoryStream _bToA = new();
    private readonly SemaphoreSlim _aToBSignal = new(0);
    private readonly SemaphoreSlim _bToASignal = new(0);

    public IStreamPair SideA { get; }
    public IStreamPair SideB { get; }

    public DuplexMemoryStream()
    {
        SideA = new Side(_aToB, _bToA, _aToBSignal, _bToASignal);
        SideB = new Side(_bToA, _aToB, _bToASignal, _aToBSignal);
    }

    // IStreamPair — not used directly, use SideA/SideB instead
    Stream IStreamPair.ReadStream => throw new NotSupportedException("Use SideA or SideB.");
    Stream IStreamPair.WriteStream => throw new NotSupportedException("Use SideA or SideB.");
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        _aToB.Dispose();
        _bToA.Dispose();
        _aToBSignal.Dispose();
        _bToASignal.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Side(
        MemoryStream writeTarget,
        MemoryStream readSource,
        SemaphoreSlim writeSignal,
        SemaphoreSlim readSignal) : IStreamPair
    {
        public Stream ReadStream { get; } = new SignaledReadStream(readSource, readSignal);
        public Stream WriteStream { get; } = new SignaledWriteStream(writeTarget, writeSignal);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SignaledWriteStream(MemoryStream target, SemaphoreSlim signal) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (target)
            {
                long pos = target.Position;
                target.Seek(0, SeekOrigin.End);
                target.Write(buffer, offset, count);
                target.Position = pos;
            }
            if (signal.CurrentCount == 0)
            {
                try { signal.Release(); }
                catch (SemaphoreFullException) { }
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (target)
            {
                long pos = target.Position;
                target.Seek(0, SeekOrigin.End);
                target.Write(buffer.Span);
                target.Position = pos;
            }
            if (signal.CurrentCount == 0)
            {
                try { signal.Release(); }
                catch (SemaphoreFullException) { }
            }
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class SignaledReadStream(MemoryStream source, SemaphoreSlim signal) : Stream
    {
        private long _readPos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                lock (source)
                {
                    long available = source.Length - _readPos;
                    if (available > 0)
                    {
                        int toRead = (int)Math.Min(available, count);
                        source.Position = _readPos;
                        int read = source.Read(buffer, offset, toRead);
                        _readPos += read;
                        return read;
                    }
                }
                signal.Wait();
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                lock (source)
                {
                    long available = source.Length - _readPos;
                    if (available > 0)
                    {
                        int toRead = (int)Math.Min(available, buffer.Length);
                        source.Position = _readPos;
                        int read = source.Read(buffer.Span[..toRead]);
                        _readPos += read;
                        return read;
                    }
                }
                await signal.WaitAsync(ct);
            }
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
