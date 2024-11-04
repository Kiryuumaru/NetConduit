using DisposableHelpers.Attributes;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;

namespace TestTCPMocker.Services;

[Disposable]
internal partial class TCPServerMocker(ILogger<TCPServerMocker> logger)
{
    private readonly ILogger<TCPServerMocker> _logger = logger;

    private CancellationTokenSource? cts = new();

    public async Task StartWait(int port, CancellationToken stoppingToken)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = cts.Token;

        TcpListener server = new(IPAddress.Any, port);

        server.Start();

        ct.Register(() =>
        {
            server.Stop();
            server.Dispose();
        });

        while (!ct.IsCancellationRequested)
        {
            TcpClient client = await server.AcceptTcpClientAsync(ct);
            StartSend(client, ct);
        }
    }

    public async void Start(int port, CancellationToken stoppingToken)
    {
        await StartWait(port, stoppingToken);
    }

    private async void StartSend(TcpClient client, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Client connected {ClientEndPoint}", (client.Client.LocalEndPoint as IPEndPoint)?.Address);

        NetworkStream ns = client.GetStream();
        byte[] buffer = new byte[4096];

        while (client.Connected)
        {
            try
            {
                int bytesread = await ns.ReadAsync(buffer, stoppingToken);
                await ns.WriteAsync(buffer.AsMemory(0, bytesread), stoppingToken);
            }
            catch { }
        }
    }

    private void Stop()
    {
        if (cts == null)
        {
            return;
        }

        cts.Cancel();
    }

    protected void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
        }
    }
}
