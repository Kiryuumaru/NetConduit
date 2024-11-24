using Application.Common;
using Application.StreamPipeline.Common;
using Application.StreamPipeline.Models;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Application.StreamPipeline.Services;

[Disposable]
public partial class StreamMultiplexer
{
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
    private const int _paddingSize = 10;

    private const int _channelKeySize = 16;
    private const int _packetLengthSize = 8;
    private const int _chunkLengthSize = 4;
    private const int _headerSize = _paddingSize + _channelKeySize + _packetLengthSize + _chunkLengthSize;
    private const int _totalSize = _headerSize + StreamPipelineDefaults.StreamMultiplexerChunkSize;

    private const int _paddingPos = 0;
    private const int _channelKeyPos = _paddingPos + _paddingSize;
    private const int _packetLengthPos = _channelKeyPos + _channelKeySize;
    private const int _chunkLengthPos = _packetLengthPos + _packetLengthSize;
    private const int _chunkPos = _chunkLengthPos + _chunkLengthSize;

    private readonly byte[] _padding;

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

        _padding = Encoding.Default.GetBytes(_paddingValue);

        if (_padding.Length != _paddingSize)
        {
            throw new Exception("Padding incorrect size");
        }
    }

    public Task Start()
    {
        _onStarted();

        return Task.Run(async () =>
        {
            await Task.WhenAll(
                DemultiplexAll(),
                MultiplexAll()
            );

            _onStopped();
        });
    }
    private Task MultiplexAll()
    {
        return Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var register = await _registerQueue.ReceiveAsync(_cts.Token);
                    if (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    var channelCt = register.TranceiverStream.CancelWhenDisposed(_cts.Token);
                    MultiplexOne(register.Channel, register.TranceiverStream, channelCt).Forget();
                }
                catch (Exception ex)
                {
                    if (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    _onError(ex);
                }
            }
        }, _cts.Token);
    }

    private Task MultiplexOne(Guid channelKey, TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        return Task.Run(() =>
        {
            Span<byte> paddingBytes = _padding.AsSpan();
            Span<byte> channelBytes = stackalloc byte[_channelKeySize];
            Span<byte> receivedBytes = stackalloc byte[_totalSize];

            MemoryMarshal.Write(channelBytes, in channelKey);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    paddingBytes.CopyTo(receivedBytes[.._paddingSize]);
                    channelBytes.CopyTo(receivedBytes.Slice(_channelKeyPos, _channelKeySize));

                    var bytesChunkRead = tranceiverStream.SenderStream.Read(receivedBytes.Slice(_chunkPos, StreamPipelineDefaults.StreamMultiplexerChunkSize));
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    long length = (tranceiverStream.SenderStream.Length - tranceiverStream.SenderStream.Position) + bytesChunkRead;
                    BinaryPrimitives.WriteInt64LittleEndian(receivedBytes.Slice(_packetLengthPos, _packetLengthSize), length);
                    BinaryPrimitives.WriteInt32LittleEndian(receivedBytes.Slice(_chunkLengthPos, _chunkLengthSize), bytesChunkRead);

                    _mainTranceiverStream.Write(receivedBytes[..(_headerSize + bytesChunkRead)]);
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

            Remove(channelKey);

        }, stoppingToken);
    }

    private Task DemultiplexAll()
    {
        return Task.Run(() =>
        {
            Span<byte> paddingBytes = _padding.AsSpan();
            Span<byte> headerBytes = stackalloc byte[_headerSize];
            Span<byte> receivedBytes = stackalloc byte[StreamPipelineDefaults.StreamMultiplexerChunkSize];

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    headerBytes[.._paddingSize].Clear();
                    _mainTranceiverStream.ReadExactly(headerBytes);
                    if (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    if (!headerBytes[.._paddingSize].SequenceEqual(paddingBytes))
                    {
                        throw new Exception("Sender received corrupted header bytes");
                    }

                    Guid channelKey = new(headerBytes.Slice(_channelKeyPos, _channelKeySize));
                    long packetLength = BinaryPrimitives.ReadInt64LittleEndian(headerBytes.Slice(_packetLengthPos, _packetLengthSize));
                    int chunkLength = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.Slice(_chunkLengthPos, _chunkLengthSize));

                    var chunkBytes = receivedBytes[.. chunkLength];

                    _mainTranceiverStream.ReadExactly(chunkBytes);

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

                    destinationStreamHolder.WriteToReceiverStream(packetLength, chunkBytes);
                }
                catch (Exception ex)
                {
                    if (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    _cts.Token.WaitHandle.WaitOne(100);
                    _onError(ex);
                }
            }
        });
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
