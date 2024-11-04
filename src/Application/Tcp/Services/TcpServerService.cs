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

namespace Application.Tcp.Services;

[Disposable]
public partial class TcpServerService(ILogger<TcpServerService> logger)
{
    private readonly ILogger<TcpServerService> _logger = logger;

    private IPAddress? _ipAddress = null;
    private int _port = 0;
    private int _bufferSize = 0;
    private CancellationTokenSource? _cts = null;

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
            TcpClient client = await server.AcceptTcpClientAsync(ct);
            NetworkStream networkStream = client.GetStream();
            TcpClientStream tcpClientStream = tcpClientStreamFactory(client, networkStream);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void ClientStream_Disposing(object? sender, EventArgs e)
            {
                cts.Cancel();
                tcpClientStream.Disposing -= ClientStream_Disposing;
            }
            tcpClientStream.Disposing += ClientStream_Disposing;
            StartSend(client, networkStream, tcpClientStream, cts.Token);
        }
    }

    private async void StartSend(TcpClient tcpClient, NetworkStream networkStream, TcpClientStream tcpClientStream, CancellationToken stoppingToken)
    {
        var clientEndPoint = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address;

        _logger.LogTrace("TCP Server {Address}:{Port} client {ClientEndPoint} connected", _ipAddress, _port, clientEndPoint);

        byte[] buffer = new byte[_bufferSize];

        while (!tcpClientStream.IsDisposedOrDisposing && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                int bytesread = await networkStream.ReadAsync(buffer, stoppingToken);
                await tcpClientStream.ReceiverStream.WriteAsync(buffer.AsMemory(0, bytesread), stoppingToken);
            }
            catch { }
        }

        _logger.LogTrace("TCP Server {Address}:{Port} client {ClientEndPoint} disconnected", _ipAddress, _port, clientEndPoint);
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