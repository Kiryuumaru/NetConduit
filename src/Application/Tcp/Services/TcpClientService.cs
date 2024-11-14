using DisposableHelpers.Attributes;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Application.Common;
using Application.StreamPipeline.Common;
using Microsoft.AspNetCore.Hosting.Server;
using System.Net.Http;
using System.IO;
using Application.Tcp.Common;

namespace Application.Tcp.Services;

[Disposable]
public partial class TcpClientService(ILogger<TcpClientService> logger)
{
    private readonly ILogger<TcpClientService> _logger = logger;

    private readonly TimeSpan _livelinessSpan = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _cts = null;
    private IPAddress? _ipAddress = null;
    private int _port = 0;
    private int _bufferSize = 0;

    public Task Start(IPAddress address, int port, int bufferSize, Func<TranceiverStream, CancellationToken, Task> onClientCallback, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(Start), new()
        {
            ["ServerAddress"] = address,
            ["ServerPort"] = port,
        });

        if (_cts != null)
        {
            throw new Exception("TCP client already started");
        }

        _ipAddress = address;
        _port = port;
        _bufferSize = bufferSize;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = _cts.Token;

        TcpClient tcpClient = new();
        ct.Register(() =>
        {
            tcpClient.Close();
            tcpClient.Dispose();
        });

        return Task.Run(async () =>
        {
            _logger.LogTrace("TCP client {Address}:{Port} started", address, port);

            while (!ct.IsCancellationRequested)
            {
                tcpClient = new();

                try
                {
                    await tcpClient.ConnectAsync(address, port, ct);

                    NetworkStream networkStream = tcpClient.GetStream();
                    TranceiverStream tranceiverStream = new(networkStream, networkStream);
                    CancellationTokenSource clientCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    await StartClient(tcpClient, networkStream, tranceiverStream, onClientCallback, clientCts);
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("Error {Address}:{Port}: {ErrorMessage}", _ipAddress, _port, ex.Message);
                }

                await Task.Delay(_livelinessSpan, ct);
            }

            _logger.LogTrace("TCP client {Address}:{Port} ended", address, port);

        }, ct);
    }

    private async Task StartClient(TcpClient tcpClient, NetworkStream networkStream, TranceiverStream tranceiverStream, Func<TranceiverStream, CancellationToken, Task> onClientCallback, CancellationTokenSource cts)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpClientService), nameof(StartClient), new()
        {
            ["ServerAddress"] = _ipAddress,
            ["ServerPort"] = _port,
        });

        try
        {
            _logger.LogTrace("TCP client connected to server {ServerHost}:{ServerPort}", _ipAddress, _port);

            onClientCallback.Invoke(tranceiverStream, cts.Token).Forget();

            await TcpClientHelpers.WatchLiveliness(tcpClient, networkStream, tranceiverStream, cts, _livelinessSpan);

            _logger.LogTrace("TCP client disconnected from server {ServerHost}:{ServerPort}", _ipAddress, _port);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }
            _logger.LogError("Error {Address}:{Port}: {ErrorMessage}", _ipAddress, _port, ex.Message);
        }
    }

    private void Stop()
    {
        if (_cts == null)
        {
            return;
        }

        _cts.Cancel();
        _cts = null;
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
        }
    }
}