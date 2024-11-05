using DisposableHelpers.Attributes;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Application.Tcp.Models;
using System.Net.Http;

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

    public async Task Start(IPAddress address, int port, int bufferSize, Func<TcpClient, NetworkStream, TcpClientStream> tcpClientStreamFactory, CancellationToken stoppingToken)
    {
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

        _logger.LogTrace("TCP Server {Address}:{Port} started", address, port);

        while (!ct.IsCancellationRequested)
        {
            TcpClient tcpClient = await server.AcceptTcpClientAsync(ct);
            IPAddress clientEndPoint = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address!;
            NetworkStream networkStream = tcpClient.GetStream();
            TcpClientStream tcpClientStream = tcpClientStreamFactory(tcpClient, networkStream);
            CancellationTokenSource clientCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            StartSend(tcpClient, clientEndPoint, networkStream, tcpClientStream, clientCts.Token);
            WatchLiveliness(tcpClient, clientEndPoint, tcpClientStream, clientCts);
        }
    }

    private async void StartSend(TcpClient tcpClient, IPAddress clientAddress, NetworkStream networkStream, TcpClientStream tcpClientStream, CancellationToken stoppingToken)
    {
        _logger.LogTrace("TCP Server {Address}:{Port} client {ClientEndPoint} connected", _ipAddress, _port, clientAddress);

        byte[] buffer = new byte[_bufferSize];

        while (tcpClient.Connected && !tcpClientStream.IsDisposedOrDisposing && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                int bytesread = await networkStream.ReadAsync(buffer, stoppingToken);
                await tcpClientStream.ReceiverStream.WriteAsync(buffer.AsMemory(0, bytesread), stoppingToken);
            }
            catch { }
        }
    }

    private async void WatchLiveliness(TcpClient tcpClient, IPAddress clientAddress, TcpClientStream tcpClientStream, CancellationTokenSource cts)
    {
        byte[] buffer = new byte[1];

        while (tcpClient.Connected && !tcpClientStream.IsDisposedOrDisposing && !cts.IsCancellationRequested)
        {
            try
            {
                if (tcpClient.Client.Poll(0, SelectMode.SelectRead) &&
                    await tcpClient.Client.ReceiveAsync(buffer, SocketFlags.Peek) == 0)
                {
                    break;
                }

                await Task.Delay(_livelinessSpan, cts.Token);
            }
            catch { }
        }

        cts.Cancel();
        tcpClient.Close();
        tcpClient.Dispose();
        tcpClientStream.Dispose();

        _logger.LogTrace("TCP Server {Address}:{Port} client {ClientEndPoint} disconnected", _ipAddress, _port, clientAddress);
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
