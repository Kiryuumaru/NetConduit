using Application.Common;
using Application.StreamPipeline.Common;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.Logging;
using System;
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
        int bufferSize,
        Action onStarted,
        Action onStopped,
        Action<Exception> onError,
        CancellationToken stoppingToken)
    {
        return new(mainTranceiverStream, bufferSize, onStarted, onStopped, onError, stoppingToken);
    }

    private readonly Dictionary<Guid, TranceiverStream> _tranceiverStreamMap = [];
    private readonly ReaderWriterLockSlim _rwl = new();
    private readonly BufferBlock<(Guid Channel, TranceiverStream TranceiverStream)> _registerQueue = new();

    private readonly int _bufferSize;
    private readonly TranceiverStream _mainTranceiverStream;
    private readonly Action _onStarted;
    private readonly Action _onStopped;
    private readonly Action<Exception> _onError;
    private readonly CancellationTokenSource _cts;

    private const int _headerSize = 16;

    private StreamMultiplexer(
        TranceiverStream mainTranceiverStream,
        int bufferSize,
        Action onStarted,
        Action onStopped,
        Action<Exception> onError,
        CancellationToken stoppingToken)
    {
        _bufferSize = bufferSize;
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
                StartSender(),
                Task.Run(async () =>
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
                            StartReceiverForwarder(register.Channel, register.TranceiverStream, channelCt).Forget();
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
                }, _cts.Token)
            );

            _onStopped();
        });
    }

    private Task StartSender()
    {
        return Task.Run(() =>
        {
            Span<byte> receivedBytes = stackalloc byte[_bufferSize + _headerSize];

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var bytesRead = _mainTranceiverStream.Read(receivedBytes);
                    if (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    if (bytesRead == 0)
                    {
                        _cts.Token.WaitHandle.WaitOne(100);
                        continue;
                    }
                    if (_headerSize > bytesRead)
                    {
                        throw new Exception("Sender received bytes smaller than the header size");
                    }
                    Guid channel = new(receivedBytes[.._headerSize]);
                    _tranceiverStreamMap[channel].ReceiverStream.Write(receivedBytes[_headerSize..bytesRead]);
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

    private Task StartReceiverForwarder(Guid channelKey, TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        return Task.Run(() =>
        {
            Span<byte> header = stackalloc byte[_headerSize];
            MemoryMarshal.Write(header, in channelKey);

            Span<byte> receivedBytes = stackalloc byte[_bufferSize + _headerSize];

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    header.CopyTo(receivedBytes[.._headerSize]);
                    var bytesRead = tranceiverStream.SenderStream.Read(receivedBytes.Slice(_headerSize, _bufferSize));
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    _mainTranceiverStream!.Write(receivedBytes[..(bytesRead + _headerSize)]);
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

    public void Set(Guid channelKey, TranceiverStream tranceiverStream)
    {
        try
        {
            _rwl.EnterWriteLock();

            _tranceiverStreamMap[channelKey] = tranceiverStream;
            _registerQueue.Post((channelKey, tranceiverStream));
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
                if (!_tranceiverStreamMap.ContainsKey(channelKey))
                {
                    _tranceiverStreamMap[channelKey] = tranceiverStream;
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

            if (_tranceiverStreamMap.TryGetValue(channelKey, out TranceiverStream? tranceiverStream))
            {
                tranceiverStream.Dispose();
                return _tranceiverStreamMap.Remove(channelKey);
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

            return _tranceiverStreamMap[channelKey];
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

            return _tranceiverStreamMap.ContainsKey(channelKey);
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
                foreach (var pipe in _tranceiverStreamMap.Values)
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
