using Application.StreamPipeline.Common;
using DisposableHelpers.Attributes;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Services;

[Disposable]
public partial class StreamPipelineService(ILogger<StreamPipelineService> logger)
{
    private readonly ILogger<StreamPipelineService> _logger = logger;

    private readonly Dictionary<Guid, TranceiverStream> _tranceiverStreamMap = [];
    private readonly ReaderWriterLockSlim _rwl = new();

    private const int _headerSize = 16;

    private TranceiverStream? _tranceiverStream = null;
    private CancellationTokenSource? _cts = null;

    public async void Start(TranceiverStream tranceiverStream, int bufferSize, CancellationToken stoppingToken)
    {
        var fullBufferSize = bufferSize + _headerSize;

        if (_cts != null)
        {
            throw new Exception("TranceiverStream already started");
        }

        _tranceiverStream = tranceiverStream;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        Memory<byte> receivedBytes = new byte[fullBufferSize];
        await _tranceiverStream.ReadAsync(receivedBytes, _cts.Token);
        Guid guid = new(receivedBytes[.._headerSize].Span);

        BlockingMemoryStream blockingMemoryStream = new(bufferSize);

        await blockingMemoryStream.WriteAsync(receivedBytes[_headerSize..], _cts.Token);
    }

    private void RegisterPipe(Guid guid, TranceiverStream tranceiverStream)
    {

    }

    private void UnregisterPipe(Guid guid)
    {
        if (!_tranceiverStreamMap.TryGetValue(guid, out TranceiverStream? tranceiverStream))
        {
            return;
        }

        tranceiverStream.Dispose();
    }

    public void Set(Guid guid, TranceiverStream tranceiverStream)
    {
        try
        {
            _rwl.EnterWriteLock();

            if (_cts == null)
            {
                throw new Exception("StreamPipeline is not started");
            }

            _tranceiverStreamMap[guid] = tranceiverStream;
            RegisterPipe(guid, tranceiverStream);
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public bool Remove(Guid guid)
    {
        try
        {
            _rwl.EnterWriteLock();

            if (_cts == null)
            {
                throw new Exception("StreamPipeline is not started");
            }

            UnregisterPipe(guid);
            return _tranceiverStreamMap.Remove(guid);
        }
        finally
        {
            _rwl.ExitWriteLock();
        }
    }

    public TranceiverStream Get(Guid guid)
    {
        try
        {
            _rwl.EnterReadLock();

            if (_cts == null)
            {
                throw new Exception("StreamPipeline is not started");
            }

            return _tranceiverStreamMap[guid];
        }
        finally
        {
            _rwl.ExitReadLock();
        }
    }

    public bool Contains(Guid guid)
    {
        try
        {
            _rwl.EnterReadLock();

            if (_cts == null)
            {
                throw new Exception("StreamPipeline is not started");
            }

            return _tranceiverStreamMap.ContainsKey(guid);
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

                _tranceiverStream?.Dispose();

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
