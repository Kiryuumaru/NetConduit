using DisposableHelpers.Attributes;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using Application.Common;
using Application.StreamPipeline.Common;
using Application.Tcp.Common;
using Microsoft.AspNetCore.Hosting.Server;

namespace Application.Tcp.Services;

[Disposable]
public partial class TcpServerService(ILogger<TcpServerService> logger)
{
    private readonly ILogger<TcpServerService> _logger = logger;

    private readonly TimeSpan _livelinessSpan = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _cts = null;
    private IPAddress? _ipAddress = null;
    private int _port = 0;

    public Task Start(IPAddress address, int port, Func<TcpClient, TranceiverStream, CancellationToken, Task> onClientCallback, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(Start), new()
        {
            ["ServerAddress"] = address,
            ["ServerPort"] = port,
        });

        if (_cts != null)
        {
            throw new Exception("TCP server already started");
        }

        _ipAddress = address;
        _port = port;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = _cts.Token;

        TcpListener server = new(address, port);
        ct.Register(() =>
        {
            server.Stop();
            server.Dispose();
        });

        server.Start();

        return Task.Run(async () =>
        {
            _logger.LogTrace("TCP server {Address}:{Port} started", address, port);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient tcpClient = await server.AcceptTcpClientAsync(ct);
                    IPAddress clientAddress = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address!;
                    NetworkStream networkStream = tcpClient.GetStream();
                    TranceiverStream tranceiverStream = new(networkStream, networkStream);
                    CancellationTokenSource clientCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    StartClient(tcpClient, networkStream, tranceiverStream, clientAddress, clientCts, onClientCallback).Forget();
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("Error {Address}:{Port}: {ErrorMessage}", _ipAddress, _port, ex.Message);
                }
            }

            _logger.LogTrace("TCP server {Address}:{Port} ended", address, port);

        }, ct);
    }

    private async Task StartClient(TcpClient tcpClient, NetworkStream networkStream, TranceiverStream tranceiverStream, IPAddress clientAddress, CancellationTokenSource cts, Func<TcpClient, TranceiverStream, CancellationToken, Task> onClientCallback)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(StartClient), new()
        {
            ["ServerAddress"] = _ipAddress,
            ["ServerPort"] = _port,
            ["ClientAddress"] = clientAddress,
        });

        try
        {
            _logger.LogTrace("TCP server {Address}:{Port} client {ClientEndPoint} connected", _ipAddress, _port, clientAddress);

            onClientCallback.Invoke(tcpClient, tranceiverStream, cts.Token).Forget();

            await TcpClientHelpers.WatchLiveliness(tcpClient, networkStream, tranceiverStream, cts, _livelinessSpan);

            _logger.LogTrace("TCP server {Address}:{Port} client {ClientEndPoint} disconnected", _ipAddress, _port, clientAddress);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }
            _logger.LogError("Error {Address}:{Port} client {ClientEndPoint}: {ErrorMessage}", _ipAddress, _port, clientAddress, ex.Message);
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
