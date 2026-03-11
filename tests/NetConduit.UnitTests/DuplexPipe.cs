using System.Buffers;
using System.IO.Pipelines;

namespace NetConduit.UnitTests;

/// <summary>
/// A pair of connected in-memory streams for testing.
/// </summary>
public sealed class DuplexPipe : IAsyncDisposable
{
    private readonly Pipe _pipe1;
    private readonly Pipe _pipe2;
    
    public Stream Stream1 { get; }
    public Stream Stream2 { get; }

    public DuplexPipe()
    {
        _pipe1 = new Pipe();
        _pipe2 = new Pipe();
        
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

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var result = await _reader.ReadAsync(cancellationToken);
            var toCopy = Math.Min(count, (int)result.Buffer.Length);
            
            if (toCopy == 0 && result.IsCompleted)
                return 0;
                
            CopySequenceToSpan(result.Buffer.Slice(0, toCopy), buffer.AsSpan(offset, toCopy));
            _reader.AdvanceTo(result.Buffer.GetPosition(toCopy));
            return toCopy;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await _reader.ReadAsync(cancellationToken);
            var toCopy = Math.Min(buffer.Length, (int)result.Buffer.Length);
            
            if (toCopy == 0 && result.IsCompleted)
                return 0;
                
            CopySequenceToSpan(result.Buffer.Slice(0, toCopy), buffer.Span[..toCopy]);
            _reader.AdvanceTo(result.Buffer.GetPosition(toCopy));
            return toCopy;
        }
        
        private static void CopySequenceToSpan(ReadOnlySequence<byte> source, Span<byte> destination)
        {
            if (source.IsSingleSegment)
            {
                source.FirstSpan.CopyTo(destination);
            }
            else
            {
                var offset = 0;
                foreach (var segment in source)
                {
                    segment.Span.CopyTo(destination[offset..]);
                    offset += segment.Length;
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _writer.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _writer.WriteAsync(buffer, cancellationToken);
        }
    }
}

/// <summary>
/// Helper methods for creating StreamMultiplexer instances in tests.
/// </summary>
public static class TestMuxHelper
{
    /// <summary>
    /// Creates MultiplexerOptions with a StreamFactory that returns the provided streams.
    /// </summary>
    public static MultiplexerOptions CreateOptionsFor(Stream readStream, Stream writeStream, Action<MultiplexerOptions>? configure = null)
    {
        var opts = new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(readStream, writeStream))
        };
        return opts;
    }
    
    /// <summary>
    /// Creates MultiplexerOptions with a StreamFactory that returns the same stream for read/write.
    /// </summary>
    public static MultiplexerOptions CreateOptionsFor(Stream stream)
        => CreateOptionsFor(stream, stream);
    
    /// <summary>
    /// Creates a pair of connected StreamMultiplexer instances for testing.
    /// Uses the new Create() + Start() + WaitForReadyAsync() API.
    /// Both muxes are started and ready to use when this method returns.
    /// </summary>
    public static async Task<(StreamMultiplexer Mux1, StreamMultiplexer Mux2, Task RunTask1, Task RunTask2)> CreateMuxPairAsync(
        DuplexPipe pipe,
        MultiplexerOptions? options1 = null,
        MultiplexerOptions? options2 = null,
        CancellationToken cancellationToken = default)
    {
        var opts1 = CopyOptionsWithStreamFactory(options1, _ => Task.FromResult<IStreamPair>(new StreamPair(pipe.Stream1)));
        var opts2 = CopyOptionsWithStreamFactory(options2, _ => Task.FromResult<IStreamPair>(new StreamPair(pipe.Stream2)));
        
        var mux1 = StreamMultiplexer.Create(opts1);
        var mux2 = StreamMultiplexer.Create(opts2);
        
        var runTask1 = mux1.Start(cancellationToken);
        var runTask2 = mux2.Start(cancellationToken);
        
        // Wait for both to be ready in parallel
        await Task.WhenAll(mux1.WaitForReadyAsync(cancellationToken), mux2.WaitForReadyAsync(cancellationToken));
        
        return (mux1, mux2, runTask1, runTask2);
    }
    
    /// <summary>
    /// Creates a StreamMultiplexer for the specified stream.
    /// Does NOT start the mux - caller must call Start().
    /// </summary>
    public static StreamMultiplexer CreateMux(
        Stream stream, 
        MultiplexerOptions? baseOptions = null)
    {
        var opts = CopyOptionsWithStreamFactory(baseOptions, _ => Task.FromResult<IStreamPair>(new StreamPair(stream)));
        return StreamMultiplexer.Create(opts);
    }
    
    /// <summary>
    /// Creates a StreamMultiplexer for the specified stream.
    /// Does NOT start the mux - caller must call Start().
    /// This is an async method for API compatibility (returns Task.FromResult).
    /// </summary>
    public static Task<StreamMultiplexer> CreateMuxAsync(
        Stream stream, 
        MultiplexerOptions? baseOptions = null,
        CancellationToken cancellationToken = default)
    {
        var mux = CreateMux(stream, baseOptions);
        return Task.FromResult(mux);
    }
    
    private static MultiplexerOptions CopyOptionsWithStreamFactory(MultiplexerOptions? baseOptions, StreamFactoryDelegate streamFactory)
    {
        if (baseOptions == null)
        {
            return new MultiplexerOptions { StreamFactory = streamFactory };
        }
        
        return new MultiplexerOptions
        {
            SessionId = baseOptions.SessionId,
            MaxFrameSize = baseOptions.MaxFrameSize,
            PingInterval = baseOptions.PingInterval,
            PingTimeout = baseOptions.PingTimeout,
            MaxMissedPings = baseOptions.MaxMissedPings,
            GoAwayTimeout = baseOptions.GoAwayTimeout,
            GracefulShutdownTimeout = baseOptions.GracefulShutdownTimeout,
            DefaultChannelOptions = baseOptions.DefaultChannelOptions,
            EnableReconnection = baseOptions.EnableReconnection,
            ReconnectTimeout = baseOptions.ReconnectTimeout,
            ReconnectBufferSize = baseOptions.ReconnectBufferSize,
            FlushMode = baseOptions.FlushMode,
            FlushInterval = baseOptions.FlushInterval,
            StreamFactory = streamFactory,
            MaxAutoReconnectAttempts = baseOptions.MaxAutoReconnectAttempts,
            AutoReconnectDelay = baseOptions.AutoReconnectDelay,
            MaxAutoReconnectDelay = baseOptions.MaxAutoReconnectDelay,
            AutoReconnectBackoffMultiplier = baseOptions.AutoReconnectBackoffMultiplier,
            ConnectionTimeout = baseOptions.ConnectionTimeout,
            HandshakeTimeout = baseOptions.HandshakeTimeout
        };
    }
}
