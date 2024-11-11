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
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Application.StreamPipeline.Services;

[Disposable]
public partial class StreamPipelineService(ILogger<StreamPipelineService> logger)
{
    private readonly ILogger<StreamPipelineService> _logger = logger;

    private readonly Dictionary<Guid, TranceiverStream> _tranceiverStreamMap = [];
    private readonly ReaderWriterLockSlim _rwl = new();
    private readonly BufferBlock<(Guid Channel, TranceiverStream TranceiverStream)> _registerQueue = new();

    private const int _headerSize = 16;

    private int? _bufferSize = null;
    private TranceiverStream? _mainTranceiverStream = null;
    private CancellationTokenSource? _cts = null;

    public async void Start(TranceiverStream mainTranceiverStream, int bufferSize, CancellationToken stoppingToken)
    {
        if (_cts != null)
        {
            throw new Exception("TranceiverStream already started");
        }

        _logger.LogInformation("Stream pipeline started");

        _bufferSize = bufferSize;
        _mainTranceiverStream = mainTranceiverStream;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = _cts.Token;

        var fullBufferSize = _bufferSize.Value + _headerSize;

        await Task.WhenAll(
            Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && !_mainTranceiverStream.IsDisposedOrDisposing)
                {
                    try
                    {
                        Memory<byte> receivedBytes = new byte[fullBufferSize];
                        await _mainTranceiverStream.ReadAsync(receivedBytes, ct);
                        Guid channel = new(receivedBytes[.._headerSize].Span);
                        await _tranceiverStreamMap[channel].WriteAsync(receivedBytes[_headerSize..], ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("{Error}", ex.Message);
                    }
                }
            }, ct),
            Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && !_mainTranceiverStream.IsDisposedOrDisposing)
                {
                    var register = await _registerQueue.ReceiveAsync(ct);
                    var channelCt = register.TranceiverStream.CancelWhenDisposed(ct);
                    StartPipe(register.Channel, register.TranceiverStream, channelCt);
                }
            }, ct)
        );

        _logger.LogInformation("Stream pipeline ended");
    }

    private async void StartPipe(Guid channelKey, TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        var fullBufferSize = _bufferSize!.Value + _headerSize;

        Memory<byte> header = channelKey.ToByteArray();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Memory<byte> receivedBytes = new byte[fullBufferSize];
                header.CopyTo(receivedBytes[.._headerSize]);
                await tranceiverStream.ReadAsync(receivedBytes.Slice(_headerSize, _bufferSize.Value), stoppingToken);
                await _mainTranceiverStream!.WriteAsync(receivedBytes, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError("{Error}", ex.Message);
            }
        }
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

                _cts?.Cancel();
                _cts = null;

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
