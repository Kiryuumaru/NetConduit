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
            StreamFactory = _ => Task.FromResult((readStream, writeStream))
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
    /// </summary>
    public static async Task<(StreamMultiplexer Mux1, StreamMultiplexer Mux2)> CreateMuxPairAsync(
        DuplexPipe pipe,
        MultiplexerOptions? options1 = null,
        MultiplexerOptions? options2 = null)
    {
        var opts1 = options1 ?? new MultiplexerOptions 
        { 
            StreamFactory = _ => Task.FromResult((pipe.Stream1, pipe.Stream1)) 
        };
        if (opts1.StreamFactory is null || opts1.StreamFactory == options1?.StreamFactory)
        {
            // Override StreamFactory if not set or if using the passed options
            opts1 = new MultiplexerOptions
            {
                SessionId = opts1.SessionId,
                MaxFrameSize = opts1.MaxFrameSize,
                PingInterval = opts1.PingInterval,
                PingTimeout = opts1.PingTimeout,
                MaxMissedPings = opts1.MaxMissedPings,
                GoAwayTimeout = opts1.GoAwayTimeout,
                GracefulShutdownTimeout = opts1.GracefulShutdownTimeout,
                DefaultChannelOptions = opts1.DefaultChannelOptions,
                EnableReconnection = opts1.EnableReconnection,
                ReconnectTimeout = opts1.ReconnectTimeout,
                ReconnectBufferSize = opts1.ReconnectBufferSize,
                FlushMode = opts1.FlushMode,
                FlushInterval = opts1.FlushInterval,
                StreamFactory = _ => Task.FromResult((pipe.Stream1, pipe.Stream1)),
                MaxAutoReconnectAttempts = opts1.MaxAutoReconnectAttempts,
                AutoReconnectDelay = opts1.AutoReconnectDelay,
                MaxAutoReconnectDelay = opts1.MaxAutoReconnectDelay,
                AutoReconnectBackoffMultiplier = opts1.AutoReconnectBackoffMultiplier
            };
        }
        
        var opts2 = options2 ?? new MultiplexerOptions 
        { 
            StreamFactory = _ => Task.FromResult((pipe.Stream2, pipe.Stream2)) 
        };
        if (opts2.StreamFactory is null || opts2.StreamFactory == options2?.StreamFactory)
        {
            opts2 = new MultiplexerOptions
            {
                SessionId = opts2.SessionId,
                MaxFrameSize = opts2.MaxFrameSize,
                PingInterval = opts2.PingInterval,
                PingTimeout = opts2.PingTimeout,
                MaxMissedPings = opts2.MaxMissedPings,
                GoAwayTimeout = opts2.GoAwayTimeout,
                GracefulShutdownTimeout = opts2.GracefulShutdownTimeout,
                DefaultChannelOptions = opts2.DefaultChannelOptions,
                EnableReconnection = opts2.EnableReconnection,
                ReconnectTimeout = opts2.ReconnectTimeout,
                ReconnectBufferSize = opts2.ReconnectBufferSize,
                FlushMode = opts2.FlushMode,
                FlushInterval = opts2.FlushInterval,
                StreamFactory = _ => Task.FromResult((pipe.Stream2, pipe.Stream2)),
                MaxAutoReconnectAttempts = opts2.MaxAutoReconnectAttempts,
                AutoReconnectDelay = opts2.AutoReconnectDelay,
                MaxAutoReconnectDelay = opts2.MaxAutoReconnectDelay,
                AutoReconnectBackoffMultiplier = opts2.AutoReconnectBackoffMultiplier
            };
        }
        
        var mux1 = await StreamMultiplexer.CreateAsync(opts1);
        var mux2 = await StreamMultiplexer.CreateAsync(opts2);
        return (mux1, mux2);
    }
    
    /// <summary>
    /// Creates a StreamMultiplexer for the specified stream.
    /// </summary>
    public static Task<StreamMultiplexer> CreateMuxAsync(Stream stream, MultiplexerOptions? baseOptions = null)
    {
        var opts = baseOptions ?? new MultiplexerOptions { StreamFactory = _ => Task.FromResult((stream, stream)) };
        if (baseOptions != null)
        {
            opts = new MultiplexerOptions
            {
                SessionId = opts.SessionId,
                MaxFrameSize = opts.MaxFrameSize,
                PingInterval = opts.PingInterval,
                PingTimeout = opts.PingTimeout,
                MaxMissedPings = opts.MaxMissedPings,
                GoAwayTimeout = opts.GoAwayTimeout,
                GracefulShutdownTimeout = opts.GracefulShutdownTimeout,
                DefaultChannelOptions = opts.DefaultChannelOptions,
                EnableReconnection = opts.EnableReconnection,
                ReconnectTimeout = opts.ReconnectTimeout,
                ReconnectBufferSize = opts.ReconnectBufferSize,
                FlushMode = opts.FlushMode,
                FlushInterval = opts.FlushInterval,
                StreamFactory = _ => Task.FromResult((stream, stream)),
                MaxAutoReconnectAttempts = opts.MaxAutoReconnectAttempts,
                AutoReconnectDelay = opts.AutoReconnectDelay,
                MaxAutoReconnectDelay = opts.MaxAutoReconnectDelay,
                AutoReconnectBackoffMultiplier = opts.AutoReconnectBackoffMultiplier
            };
        }
        return StreamMultiplexer.CreateAsync(opts);
    }
}
