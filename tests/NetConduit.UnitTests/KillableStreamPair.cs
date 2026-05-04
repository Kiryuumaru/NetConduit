namespace NetConduit.UnitTests;

/// <summary>
/// A stream pair wrapper that can be killed on demand to simulate transport failure.
/// After Kill() is called, all reads throw IOException and all writes throw IOException.
/// </summary>
internal sealed class KillableStreamPair : IStreamPair
{
    private readonly IStreamPair _inner;
    private volatile bool _killed;
    private readonly CancellationTokenSource _killCts = new();

    public KillableStreamPair(IStreamPair inner)
    {
        _inner = inner;
        ReadStream = new KillableStream(inner.ReadStream, this);
        WriteStream = new KillableStream(inner.WriteStream, this);
    }

    public Stream ReadStream { get; }
    public Stream WriteStream { get; }

    public bool IsKilled => _killed;
    public CancellationToken KillToken => _killCts.Token;

    public void Kill()
    {
        _killed = true;
        _killCts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        _killCts.Dispose();
        await _inner.DisposeAsync();
    }

    private sealed class KillableStream(Stream inner, KillableStreamPair parent) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (parent._killed)
                throw new IOException("Transport killed.");
            return inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (parent._killed)
                throw new IOException("Transport killed.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, parent.KillToken);
            try
            {
                return await inner.ReadAsync(buffer, linked.Token);
            }
            catch (OperationCanceledException) when (parent._killed)
            {
                throw new IOException("Transport killed.");
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (parent._killed)
                throw new IOException("Transport killed.");
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (parent._killed)
                throw new IOException("Transport killed.");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, parent.KillToken);
            try
            {
                await inner.WriteAsync(buffer, linked.Token);
            }
            catch (OperationCanceledException) when (parent._killed)
            {
                throw new IOException("Transport killed.");
            }
        }

        public override void Flush()
        {
            if (parent._killed)
                throw new IOException("Transport killed.");
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken ct)
        {
            if (parent._killed)
                throw new IOException("Transport killed.");
            return inner.FlushAsync(ct);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
