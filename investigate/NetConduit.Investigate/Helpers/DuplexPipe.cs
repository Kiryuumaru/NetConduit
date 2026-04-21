using System.Buffers;
using System.IO.Pipelines;
using NetConduit.Models;

namespace NetConduit.Investigate.Helpers;

public sealed class DuplexPipe : IAsyncDisposable
{
    private readonly Pipe _pipe1;
    private readonly Pipe _pipe2;

    public Stream Stream1 { get; }
    public Stream Stream2 { get; }

    public DuplexPipe()
    {
        var options = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0);
        _pipe1 = new Pipe(options);
        _pipe2 = new Pipe(options);

        Stream1 = new DuplexPipeStream(_pipe1.Reader, _pipe2.Writer);
        Stream2 = new DuplexPipeStream(_pipe2.Reader, _pipe1.Writer);
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe1.Reader.CompleteAsync();
        await _pipe1.Writer.CompleteAsync();
        await _pipe2.Reader.CompleteAsync();
        await _pipe2.Writer.CompleteAsync();
    }

    private class DuplexPipeStream : Stream
    {
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;

        public DuplexPipeStream(PipeReader reader, PipeWriter writer)
        {
            _reader = reader;
            _writer = writer;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _writer.FlushAsync().AsTask().GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _writer.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await _reader.ReadAsync(cancellationToken);
            var toCopy = Math.Min(buffer.Length, (int)result.Buffer.Length);

            if (toCopy == 0 && result.IsCompleted)
                return 0;

            result.Buffer.Slice(0, toCopy).CopyTo(buffer.Span[..toCopy]);
            _reader.AdvanceTo(result.Buffer.GetPosition(toCopy));
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _writer.WriteAsync(buffer, cancellationToken);
        }
    }
}

public static class TestMuxHelper
{
    public static async Task<(StreamMultiplexer Mux1, StreamMultiplexer Mux2, Task RunTask1, Task RunTask2)> CreateMuxPairAsync(
        DuplexPipe pipe,
        MultiplexerOptions? options1 = null,
        MultiplexerOptions? options2 = null,
        CancellationToken ct = default)
    {
        var opts1 = new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(pipe.Stream1)),
            MaxAutoReconnectAttempts = options1?.MaxAutoReconnectAttempts ?? 1,
            FlushMode = options1?.FlushMode ?? Enums.FlushMode.Batched,
            FlushInterval = options1?.FlushInterval ?? TimeSpan.FromMilliseconds(1),
            MaxFrameSize = options1?.MaxFrameSize ?? 16 * 1024 * 1024,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            HandshakeTimeout = TimeSpan.FromSeconds(5),
        };
        var opts2 = new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(pipe.Stream2)),
            MaxAutoReconnectAttempts = options2?.MaxAutoReconnectAttempts ?? 1,
            FlushMode = options2?.FlushMode ?? Enums.FlushMode.Batched,
            FlushInterval = options2?.FlushInterval ?? TimeSpan.FromMilliseconds(1),
            MaxFrameSize = options2?.MaxFrameSize ?? 16 * 1024 * 1024,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            HandshakeTimeout = TimeSpan.FromSeconds(5),
        };

        var mux1 = StreamMultiplexer.Create(opts1);
        var mux2 = StreamMultiplexer.Create(opts2);

        var runTask1 = mux1.Start(ct);
        var runTask2 = mux2.Start(ct);

        await Task.WhenAll(mux1.WaitForReadyAsync(ct), mux2.WaitForReadyAsync(ct));

        return (mux1, mux2, runTask1, runTask2);
    }
}
