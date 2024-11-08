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
using Application.StreamPipeline.Models;
using Application.Common;
using Application.StreamPipeline.Common;

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

    public async Task Start(IPAddress address, int port, int bufferSize, Action<StreamPipe> onClientCallback, CancellationToken stoppingToken)
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
            StreamPipe streamPipe = new()
            {
                ReceiverStream = networkStream,
                SenderStream = networkStream,
            };
            onClientCallback(streamPipe);
            CancellationTokenSource clientCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            WatchLiveliness(tcpClient, networkStream, clientEndPoint, streamPipe, clientCts);
        }
    }

    private async void WatchLiveliness(TcpClient tcpClient, NetworkStream networkStream, IPAddress clientAddress, StreamPipe streamPipe, CancellationTokenSource cts)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(WatchLiveliness), new()
        {
            ["ServerAddress"] = _ipAddress,
            ["ServerPort"] = _port,
            ["ClientAddress"] = clientAddress,
        });

        _logger.LogTrace("TCP server {Address}:{Port} client {ClientEndPoint} connected", _ipAddress, _port, clientAddress);

        byte[] buffer = new byte[1];

        while (tcpClient.Connected && !streamPipe.IsDisposedOrDisposing && !cts.IsCancellationRequested)
        {
            try
            {
                if (tcpClient.Client.Poll(0, SelectMode.SelectRead) &&
                    await tcpClient.Client.ReceiveAsync(buffer, SocketFlags.Peek, cts.Token) == 0)
                {
                    break;
                }

                await Task.Delay(_livelinessSpan, cts.Token);
            }
            catch { }
        }

        tcpClient.Close();
        tcpClient.Dispose();
        networkStream.Close();
        networkStream.Dispose();
        streamPipe.Dispose();
        cts.Cancel();

        _logger.LogTrace("TCP server {Address}:{Port} client {ClientEndPoint} disconnected", _ipAddress, _port, clientAddress);
    }

    private void Stop()
    {
        if (_cts == null)
        {
            return;
        }

        _cts.Cancel();
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
        }
    }
}
