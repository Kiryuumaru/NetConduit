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
using Microsoft.AspNetCore.Hosting.Server;

namespace TestTCPMocker.Services;

[Disposable]
internal partial class TCPRelayMocker(ILogger<TCPRelayMocker> logger)
{
    private readonly ILogger<TCPRelayMocker> _logger = logger;

    private CancellationTokenSource? cts = new();

    public async Task StartWait(int serverPort, string destinationHost, int destinationPort, CancellationToken stoppingToken)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = cts.Token;

        TcpListener relay = new(IPAddress.Any, serverPort);

        relay.Start();

        ct.Register(() =>
        {
            relay.Stop();
            relay.Dispose();
        });

        while (!ct.IsCancellationRequested)
        {
            TcpClient client = await relay.AcceptTcpClientAsync(ct);
            StartSend(destinationHost, destinationPort, client, ct);
        }
    }

    public async void Start(int serverPort, string destinationHost, int destinationPort, CancellationToken stoppingToken)
    {
        await StartWait(serverPort, destinationHost, destinationPort, stoppingToken);
    }

    private async void StartSend(string destinationHost, int destinationPort, TcpClient client, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Client connected {ClientEndPoint}", (client.Client.LocalEndPoint as IPEndPoint)?.Address);

        TcpClient server = new();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await server.ConnectAsync(destinationHost, destinationPort, stoppingToken);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("{Error}", ex.Message);
            }

            await Task.Delay(2000, stoppingToken);
        }

        _logger.LogInformation("Connected to destination server {ServerHost}:{ServerPort}", destinationHost, destinationPort);

        NetworkStream serverNs = server.GetStream();
        NetworkStream clientNs = client.GetStream();

        var sendToServerTask = Task.Run(async () =>
        {
            byte[] buffer = new byte[4096];
            while (client.Connected && server.Connected)
            {
                try
                {
                    int bytesread = await clientNs.ReadAsync(buffer, stoppingToken);
                    await serverNs.WriteAsync(buffer.AsMemory(0, bytesread), stoppingToken);
                }
                catch { }
            }
        }, stoppingToken);

        var receiveFromServerTask = Task.Run(async () =>
        {
            byte[] buffer = new byte[4096];
            while (client.Connected && server.Connected)
            {
                try
                {
                    int bytesread = await serverNs.ReadAsync(buffer, stoppingToken);
                    await clientNs.WriteAsync(buffer.AsMemory(0, bytesread), stoppingToken);
                }
                catch { }
            }
        }, stoppingToken);

        await Task.WhenAll(sendToServerTask, receiveFromServerTask);

        client.Close();
        client.Dispose();

        server.Close();
        server.Dispose();

        _logger.LogInformation("Connection ended {ServerHost}:{ServerPort}", destinationHost, destinationPort);
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
