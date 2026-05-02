using System.Buffers;
using System.IO.Pipelines;
using NetConduit.Models;

namespace NetConduit.UnitTests;

/// <summary>
/// A pair of connected in-memory streams for testing.
/// Optionally injects per-operation latency to simulate slow transports.
/// </summary>
public sealed class DuplexPipe : IAsyncDisposable
{
    private readonly Pipe _pipe1;
    private readonly Pipe _pipe2;
    
    public Stream Stream1 { get; }
    public Stream Stream2 { get; }

    public DuplexPipe(int latencyMs = 0)
    {
        var options = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0);
        _pipe1 = new Pipe(options);
        _pipe2 = new Pipe(options);
        
        Stream raw1 = new DuplexPipeStream(_pipe1.Reader, _pipe2.Writer);
        Stream raw2 = new DuplexPipeStream(_pipe2.Reader, _pipe1.Writer);
        
        Stream1 = latencyMs > 0 ? new LatencyStream(raw1, latencyMs) : raw1;
        Stream2 = latencyMs > 0 ? new LatencyStream(raw2, latencyMs) : raw2;
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
/// A DuplexPipe that supports reconnection by creating fresh stream pairs on demand.
/// Both sides share the same underlying pipe, so data flows between them.
/// </summary>
public sealed class ReconnectableDuplexPipe : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly int _latencyMs;
    private DuplexPipe _current;
    private bool _reconnectPending;

    public ReconnectableDuplexPipe(int latencyMs = 0)
    {
        _latencyMs = latencyMs;
        _current = new DuplexPipe(latencyMs);
    }

    public Task<IStreamPair> CreateStream1(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_reconnectPending)
            {
                _current = new DuplexPipe(_latencyMs);
                _reconnectPending = false;
            }
            return Task.FromResult<IStreamPair>(new StreamPair(_current.Stream1));
        }
    }

    public Task<IStreamPair> CreateStream2(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_reconnectPending)
            {
                _current = new DuplexPipe(_latencyMs);
                _reconnectPending = false;
            }
            return Task.FromResult<IStreamPair>(new StreamPair(_current.Stream2));
        }
    }

    public async Task DisconnectAsync()
    {
        DuplexPipe old;
        lock (_lock)
        {
            old = _current;
            _reconnectPending = true;
        }
        await old.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _current.DisposeAsync();
    }
}

/// <summary>
/// A write-only stream wrapper that can hold writes in a buffer instead
/// of forwarding them. Simulates an OS socket send buffer: the sender's
/// write "succeeds" (data accepted), but the data hasn't reached the
/// remote yet. Calling DropHeld() discards the buffer — simulating
/// data loss when a network connection dies.
/// </summary>
public sealed class HoldableWriteStream : Stream
{
    private readonly Stream _destination;
    private readonly object _lock = new();
    private readonly List<byte[]> _held = new();
    private volatile bool _holding;

    public HoldableWriteStream(Stream destination) => _destination = destination;

    public void StartHolding() => _holding = true;

    public int DropHeld()
    {
        lock (_lock)
        {
            var total = 0;
            foreach (var chunk in _held) total += chunk.Length;
            _held.Clear();
            return total;
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use async");

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_holding) return Task.CompletedTask;
        return _destination.FlushAsync(cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_holding)
        {
            lock (_lock) { _held.Add(buffer.ToArray()); }
            return ValueTask.CompletedTask;
        }
        return _destination.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_holding)
        {
            lock (_lock) { _held.Add(buffer.AsSpan(offset, count).ToArray()); }
            return Task.CompletedTask;
        }
        return _destination.WriteAsync(buffer, offset, count, cancellationToken);
    }
}

/// <summary>
/// A ReconnectableDuplexPipe where mux1's transport writes pass through a
/// HoldableWriteStream. When holding is active, the FlushLoop's writes are
/// accepted but buffered locally — simulating data in an OS send buffer.
/// DropHeld() discards the buffer, simulating in-flight data loss on disconnect.
/// </summary>
public sealed class InFlightLossDuplexPipe : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly int _latencyMs;
    private DuplexPipe _current;
    private bool _reconnectPending;
    private HoldableWriteStream? _holdableWrite;

    public InFlightLossDuplexPipe(int latencyMs = 0)
    {
        _latencyMs = latencyMs;
        _current = new DuplexPipe(latencyMs);
    }

    public Task<IStreamPair> CreateStream1(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_reconnectPending)
            {
                _current = new DuplexPipe(_latencyMs);
                _reconnectPending = false;
            }
            _holdableWrite = new HoldableWriteStream(_current.Stream1);
            return Task.FromResult<IStreamPair>(new StreamPair(_current.Stream1, _holdableWrite));
        }
    }

    public Task<IStreamPair> CreateStream2(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_reconnectPending)
            {
                _current = new DuplexPipe(_latencyMs);
                _reconnectPending = false;
            }
            return Task.FromResult<IStreamPair>(new StreamPair(_current.Stream2));
        }
    }

    public void StartHolding() => _holdableWrite?.StartHolding();
    public int DropHeld() => _holdableWrite?.DropHeld() ?? 0;

    public async Task DisconnectAsync()
    {
        DuplexPipe old;
        lock (_lock)
        {
            old = _current;
            _reconnectPending = true;
        }
        await old.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _current.DisposeAsync();
    }
}

/// <summary>
/// Stream wrapper that injects a fixed delay before every read and write,
/// simulating network latency on a transport.
/// </summary>
public sealed class LatencyStream(Stream inner, int latencyMs) : Stream
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

    public override void Flush() => inner.Flush();

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(latencyMs, cancellationToken);
        await inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(latencyMs, cancellationToken);
        return await inner.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(latencyMs, cancellationToken);
        return await inner.ReadAsync(buffer, cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(latencyMs, cancellationToken);
        await inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(latencyMs, cancellationToken);
        await inner.WriteAsync(buffer, cancellationToken);
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
    public static MultiplexerOptions CreateOptionsFor(Stream readStream, Stream writeStream)
    {
        return new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(new StreamPair(readStream, writeStream))
        };
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
    /// Creates a pair of connected StreamMultiplexer instances with reconnectable transport.
    /// After calling pipe.DisconnectAsync(), both muxes will auto-reconnect using fresh streams.
    /// </summary>
    public static async Task<(StreamMultiplexer Mux1, StreamMultiplexer Mux2, Task RunTask1, Task RunTask2, ReconnectableDuplexPipe Pipe)> CreateReconnectableMuxPairAsync(
        MultiplexerOptions? options1 = null,
        MultiplexerOptions? options2 = null,
        CancellationToken cancellationToken = default)
    {
        var pipe = new ReconnectableDuplexPipe();
        var opts1 = CopyOptionsWithStreamFactory(options1, pipe.CreateStream1);
        var opts2 = CopyOptionsWithStreamFactory(options2, pipe.CreateStream2);
        
        var mux1 = StreamMultiplexer.Create(opts1);
        var mux2 = StreamMultiplexer.Create(opts2);
        
        var runTask1 = mux1.Start(cancellationToken);
        var runTask2 = mux2.Start(cancellationToken);
        
        await Task.WhenAll(mux1.WaitForReadyAsync(cancellationToken), mux2.WaitForReadyAsync(cancellationToken));
        
        return (mux1, mux2, runTask1, runTask2, pipe);
    }
    
    /// <summary>
    /// Creates a pair of connected StreamMultiplexer instances where mux1's
    /// transport writes can be held and dropped — simulating in-flight data loss.
    /// </summary>
    public static async Task<(StreamMultiplexer Mux1, StreamMultiplexer Mux2, Task RunTask1, Task RunTask2, InFlightLossDuplexPipe Pipe)> CreateInFlightLossMuxPairAsync(
        MultiplexerOptions? options1 = null,
        MultiplexerOptions? options2 = null,
        CancellationToken cancellationToken = default)
    {
        var pipe = new InFlightLossDuplexPipe();
        var opts1 = CopyOptionsWithStreamFactory(options1, pipe.CreateStream1);
        var opts2 = CopyOptionsWithStreamFactory(options2, pipe.CreateStream2);
        
        var mux1 = StreamMultiplexer.Create(opts1);
        var mux2 = StreamMultiplexer.Create(opts2);
        
        var runTask1 = mux1.Start(cancellationToken);
        var runTask2 = mux2.Start(cancellationToken);
        
        await Task.WhenAll(mux1.WaitForReadyAsync(cancellationToken), mux2.WaitForReadyAsync(cancellationToken));
        
        return (mux1, mux2, runTask1, runTask2, pipe);
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
        
        return baseOptions with { StreamFactory = streamFactory };
    }
}
