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

    /// <summary>
    /// A bidirectional stream backed by pipes. Public for test reuse.
    /// </summary>
    public class DuplexPipeStream : Stream
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
