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

namespace Application.Tcp.Services;

[Disposable]
public partial class TcpServerService(ILogger<TcpServerService> logger)
{
    private readonly ILogger<TcpServerService> _logger = logger;

    private readonly TimeSpan _livelinessSpan = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _cts = null;
    private IPAddress? _ipAddress = null;
    private int _port = 0;
    private int _bufferSize = 0;

    public async Task Start(IPAddress address, int port, int bufferSize, Action<TcpClient, TranceiverStream> onClientCallback, CancellationToken stoppingToken)
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
        _bufferSize = bufferSize;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = _cts.Token;

        TcpListener server = new(address, port);
        ct.Register(() =>
        {
            server.Stop();
            server.Dispose();
        });

        server.Start();

        _logger.LogTrace("TCP server {Address}:{Port} started", address, port);

        while (!ct.IsCancellationRequested)
        {
            TcpClient tcpClient = await server.AcceptTcpClientAsync(ct);
            IPAddress clientEndPoint = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address!;
            NetworkStream networkStream = tcpClient.GetStream();
            TranceiverStream tranceiverStream = new(networkStream, networkStream);
            onClientCallback(tcpClient, tranceiverStream);
            CancellationTokenSource clientCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            WatchLiveliness(tcpClient, networkStream, clientEndPoint, tranceiverStream, clientCts);
        }
    }

    private async void WatchLiveliness(TcpClient tcpClient, NetworkStream networkStream, IPAddress clientAddress, TranceiverStream tranceiverStream, CancellationTokenSource cts)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(WatchLiveliness), new()
        {
            ["ServerAddress"] = _ipAddress,
            ["ServerPort"] = _port,
            ["ClientAddress"] = clientAddress,
        });

        _logger.LogTrace("TCP server {Address}:{Port} client {ClientEndPoint} connected", _ipAddress, _port, clientAddress);

        await TcpClientHelpers.WatchLiveliness(tcpClient, networkStream, tranceiverStream, cts, _livelinessSpan);

        _logger.LogTrace("TCP server {Address}:{Port} client {ClientEndPoint} disconnected", _ipAddress, _port, clientAddress);
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
