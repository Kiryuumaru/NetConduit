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
using Application.StreamPipeline.Models;
using Application.Common;
using Application.StreamPipeline.Common;
using Microsoft.AspNetCore.Hosting.Server;
using System.Net.Http;
using System.IO;

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

    public async Task Start(IPAddress address, int port, int bufferSize, Action<StreamPipe> onClientCallback, CancellationToken stoppingToken)
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

        while (!ct.IsCancellationRequested)
        {
            tcpClient = new();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await tcpClient.ConnectAsync(address, port, ct);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError("{Error}", ex.Message);
                }

                await Task.Delay(_livelinessSpan, ct);
            }

            NetworkStream networkStream = tcpClient.GetStream();
            StreamPipe streamPipe = new()
            {
                ReceiverStream = networkStream,
                SenderStream = networkStream,
            };
            onClientCallback(streamPipe);
            CancellationTokenSource clientCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            WatchLiveliness(tcpClient, networkStream, streamPipe, clientCts);

            await clientCts.Token.WhenCanceled();
        }
    }

    private async void WatchLiveliness(TcpClient tcpClient, NetworkStream networkStream, StreamPipe streamPipe, CancellationTokenSource cts)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpClientService), nameof(WatchLiveliness), new()
        {
            ["ServerAddress"] = _ipAddress,
            ["ServerPort"] = _port,
        });

        _logger.LogTrace("TCP client connected to server {ServerHost}:{ServerPort}", _ipAddress, _port);

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

        _logger.LogTrace("TCP client disconnected from server {ServerHost}:{ServerPort}", _ipAddress, _port);
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