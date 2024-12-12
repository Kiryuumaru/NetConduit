using Application.Common.Extensions;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Features;
using DisposableHelpers.Attributes;
using Domain.StreamPipeline.Exceptions;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks.Dataflow;

namespace Application.StreamPipeline.Services;

[Disposable]
public partial class StreamMultiplexer
{
    [Disposable]
    private partial class ChunkedTranceiverStreamHolder(TranceiverStream tranceiverStream)
    {
        public readonly TranceiverStream TranceiverStream = tranceiverStream;

        private readonly StreamChunkWriter _streamChunkWriter = new(tranceiverStream.ReceiverStream);

        public void WriteToReceiverStream(long length, ReadOnlySpan<byte> buffer)
            => _streamChunkWriter.WriteChunk(length, buffer);

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                TranceiverStream.Dispose();
            }
        }
    }

    public static StreamMultiplexer Create(
        TranceiverStream mainTranceiverStream,
        Action onStarted,
        Action onStopped,
        Action<Exception> onError,
        CancellationToken stoppingToken)
    {
        return new(mainTranceiverStream, onStarted, onStopped, onError, stoppingToken);
    }

    private readonly Dictionary<Guid, ChunkedTranceiverStreamHolder> _tranceiverStreamHolderMap = [];
    private readonly ReaderWriterLockSlim _rwl = new();
    private readonly BufferBlock<(Guid Channel, TranceiverStream TranceiverStream)> _registerQueue = new();

    private readonly TranceiverStream _mainTranceiverStream;
    private readonly Action _onStarted;
    private readonly Action _onStopped;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _cts;

    private const string _paddingValue = "endofchunk";

    private const int _channelKeySize = 16;
    private const int _packetLengthSize = 8;
    private const int _chunkLengthSize = 4;

    private readonly byte[] _paddingBytes;

    private readonly int _paddingSize;
    private readonly int _headerSize;
    private readonly int _totalSize;

    private readonly int _paddingPos;
    private readonly int _channelKeyPos;
    private readonly int _packetLengthPos;
    private readonly int _chunkLengthPos;
    private readonly int _chunkPos;

    private StreamMultiplexer(
        TranceiverStream mainTranceiverStream,
        Action onStarted,
        Action onStopped,
        Action<Exception> onError,
        CancellationToken stoppingToken)
    {
        _mainTranceiverStream = mainTranceiverStream;
        _onStarted = onStarted;
        _onStopped = onStopped;
        _onError = onError;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _mainTranceiverStream.CancelWhenDisposing(stoppingToken));

        _cts.Token.Register(Dispose);

        _paddingBytes = Encoding.ASCII.GetBytes(_paddingValue);
        _paddingSize = _paddingBytes.Length;

        _headerSize = _paddingSize + _channelKeySize + _packetLengthSize + _chunkLengthSize;
        _totalSize = _headerSize + StreamPipelineDefaults.StreamMultiplexerChunkSize;

        _paddingPos = 0;
        _channelKeyPos = _paddingPos + _paddingSize;
        _packetLengthPos = _channelKeyPos + _channelKeySize;
        _chunkLengthPos = _packetLengthPos + _packetLengthSize;
        _chunkPos = _chunkLengthPos + _chunkLengthSize;
    }

    public Task Start()
    {
        _onStarted();

        return Task.Run(async () =>
        {
            await Task.WhenAll(
                MultiplexAll(_cts.Token),
                DemultiplexAll(_cts.Token)
            );

            _onStopped();
        });
    }

    private async Task MultiplexAll(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var register = await _registerQueue.ReceiveAsync(stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                var channelCt = register.TranceiverStream.CancelWhenDisposed(stoppingToken);
                MultiplexOne(register.Channel, register.TranceiverStream, channelCt).Forget();
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                _onError(ex);
            }
        }
    }

    private async Task MultiplexOne(Guid channelKey, TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        ReadOnlyMemory<byte> paddingBytes = _paddingBytes.AsMemory();
        Memory<byte> channelBytes = new byte[_channelKeySize];
        Memory<byte> receivedBytes = new byte[_totalSize];
        Memory<byte> packetBytes = new byte[StreamPipelineDefaults.StreamMultiplexerMaxPacketSize];

        MemoryMarshal.Write(channelBytes.Span, in channelKey);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var packetLength = await tranceiverStream.SenderStream.ReadAsync(packetBytes, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                int length = packetLength;
                int bytesChunkWrite = 0;

                while (length > 0)
                {
                    bytesChunkWrite = Math.Min(length, StreamPipelineDefaults.StreamMultiplexerChunkSize);

                    paddingBytes.CopyTo(receivedBytes[.._paddingSize]);
                    channelBytes.CopyTo(receivedBytes.Slice(_channelKeyPos, _channelKeySize));

                    BinaryPrimitives.WriteInt64LittleEndian(receivedBytes.Slice(_packetLengthPos, _packetLengthSize).Span, length);
                    BinaryPrimitives.WriteInt32LittleEndian(receivedBytes.Slice(_chunkLengthPos, _chunkLengthSize).Span, bytesChunkWrite);

                    packetBytes.Slice(packetLength - length, bytesChunkWrite).CopyTo(receivedBytes.Slice(_chunkPos, bytesChunkWrite));

                    length -= bytesChunkWrite;

                    _mainTranceiverStream.Write(receivedBytes[..(_headerSize + bytesChunkWrite)].Span);
                }
            }
            catch (Exception ex)
            {
                await stoppingToken.WaitHandle.WaitAsync(1000);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                _onError(ex);
            }
        }

        Remove(channelKey);
    }

    private async Task DemultiplexAll(CancellationToken stoppingToken)
    {
        ReadOnlyMemory<byte> paddingBytes = _paddingBytes.AsMemory();
        Memory<byte> headerBytes = new byte[_headerSize];
        Memory<byte> receivedBytes = new byte[StreamPipelineDefaults.StreamMultiplexerChunkSize];

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                headerBytes[.._paddingSize].Span.Clear();
                await _mainTranceiverStream.ReadExactlyAsync(headerBytes, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                if (!headerBytes[.._paddingSize].Span.SequenceEqual(paddingBytes.Span))
                {
                    throw CorruptedHeaderBytesException.Instance;
                }

                Guid channelKey = new(headerBytes.Slice(_channelKeyPos, _channelKeySize).Span);
                long packetLength = BinaryPrimitives.ReadInt64LittleEndian(headerBytes.Slice(_packetLengthPos, _packetLengthSize).Span);
                int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.Slice(_chunkLengthPos, _chunkLengthSize).Span);

                var chunkBytes = receivedBytes[..chunkLength];

                await _mainTranceiverStream.ReadExactlyAsync(chunkBytes, stoppingToken);

                ChunkedTranceiverStreamHolder destinationStreamHolder;

                try
                {
                    _rwl.EnterReadLock();

                    destinationStreamHolder = _tranceiverStreamHolderMap[channelKey];
                }
                finally
                {
                    _rwl.ExitReadLock();
                }

                destinationStreamHolder.WriteToReceiverStream(packetLength, chunkBytes.Span);
            }
            catch (Exception ex)
            {
                await stoppingToken.WaitHandle.WaitAsync(1000);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                _onError(ex);
            }
        }
    }

    public void Set(Guid channelKey, TranceiverStream tranceiverStream)
    {
        try
        {
            _rwl.EnterWriteLock();

            if (_tranceiverStreamHolderMap.ContainsKey(channelKey))
            {
                throw new Exception($"{channelKey} channel already exists");
            }

            _tranceiverStreamHolderMap[channelKey] = new(tranceiverStream);
            _registerQueue.Post((channelKey, tranceiverStream));
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public TranceiverStream Set(Guid channelKey, int bufferSize)
    {
        try
        {
            _rwl.EnterWriteLock();

            if (_tranceiverStreamHolderMap.ContainsKey(channelKey))
            {
                throw new Exception($"{channelKey} channel already exists");
            }

            TranceiverStream tranceiverStream = new(new BlockingMemoryStream(bufferSize), new BlockingMemoryStream(bufferSize));

            _tranceiverStreamHolderMap[channelKey] = new(tranceiverStream);
            _registerQueue.Post((channelKey, tranceiverStream));

            return tranceiverStream;
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public Guid Set(TranceiverStream tranceiverStream)
    {
        try
        {
            _rwl.EnterWriteLock();
            Guid channelKey;
            while (true)
            {
                channelKey = Guid.NewGuid();
                if (!_tranceiverStreamHolderMap.ContainsKey(channelKey))
                {
                    _tranceiverStreamHolderMap[channelKey] = new(tranceiverStream);
                    _registerQueue.Post((channelKey, tranceiverStream));
                    return channelKey;
                }
            }
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public TranceiverStream GetOrSet(Guid channelKey, Func<TranceiverStream> onSet)
    {
        try
        {
            _rwl.EnterWriteLock();

            if (_tranceiverStreamHolderMap.TryGetValue(channelKey, out var chunkedTranceiverStream))
            {
                return chunkedTranceiverStream.TranceiverStream;
            }

            chunkedTranceiverStream = new(onSet());

            _tranceiverStreamHolderMap[channelKey] = chunkedTranceiverStream;
            _registerQueue.Post((channelKey, chunkedTranceiverStream.TranceiverStream));

            return chunkedTranceiverStream.TranceiverStream;
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public bool Remove(Guid channelKey)
    {
        try
        {
            _rwl.EnterWriteLock();

            if (_tranceiverStreamHolderMap.TryGetValue(channelKey, out var tranceiverStreamHolder))
            {
                tranceiverStreamHolder.Dispose();
                return _tranceiverStreamHolderMap.Remove(channelKey);
            }
            return false;
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public TranceiverStream Get(Guid channelKey)
    {
        try
        {
            _rwl.EnterReadLock();

            return _tranceiverStreamHolderMap[channelKey].TranceiverStream;
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    public bool Contains(Guid channelKey)
    {
        try
        {
            _rwl.EnterReadLock();

            return _tranceiverStreamHolderMap.ContainsKey(channelKey);
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _rwl.EnterWriteLock();

                _cts.Cancel();
                _mainTranceiverStream?.Dispose();
                foreach (var pipe in _tranceiverStreamHolderMap.Values)
                {
                    pipe.Dispose();
                }
            }
            finally
            {
                _rwl.ExitWriteLock();
            }
        }
    }
}
