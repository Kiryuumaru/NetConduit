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

    private const int _chunkSize = 4096;
    //private const int _chunkSize = 1048576;
    private const int _channelSize = 16;
    private const int _lengthSize = 8;
    private const int _headerSize = _channelSize + _lengthSize;
    private const int _totalSize = _headerSize + _chunkSize;

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
            Span<byte> channelBytes = stackalloc byte[_channelSize];
            Span<byte> lengthBytes = stackalloc byte[_lengthSize];
            Span<byte> receivedBytes = stackalloc byte[_totalSize];

            MemoryMarshal.Write(channelBytes, in channelKey);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    channelBytes.CopyTo(receivedBytes[.._channelSize]);

                    var bytesChunkRead = tranceiverStream.SenderStream.Read(receivedBytes.Slice(_headerSize, _chunkSize));
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    long length = (tranceiverStream.SenderStream.Length - tranceiverStream.SenderStream.Position) + bytesChunkRead;
                    BinaryPrimitives.WriteInt64LittleEndian(receivedBytes.Slice(_channelSize, _lengthSize), length);

                    _mainTranceiverStream!.Write(receivedBytes[..(_headerSize + bytesChunkRead)]);
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
            Span<byte> receivedBytes = stackalloc byte[_totalSize];

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var bytesAllRead = _mainTranceiverStream.Read(receivedBytes);
                    if (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    if (bytesAllRead == 0)
                    {
                        _cts.Token.WaitHandle.WaitOne(100);
                        continue;
                    }
                    if (_headerSize > bytesAllRead)
                    {
                        throw new Exception("Sender received bytes smaller than the header size");
                    }

                    int bytesChunkRead = bytesAllRead - _headerSize;
                    Guid channelKey = new(receivedBytes[.._channelSize]);
                    long chunkLength = BinaryPrimitives.ReadInt64LittleEndian(receivedBytes.Slice(_channelSize, _lengthSize));

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

                    destinationStreamHolder.WriteToReceiverStream(chunkLength, receivedBytes.Slice(_headerSize, bytesChunkRead));
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
