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

namespace TestTCPMocker.Services;

[Disposable]
internal partial class TCPServerMocker(ILogger<TCPServerMocker> logger)
{
    private readonly ILogger<TCPServerMocker> _logger = logger;

    private CancellationTokenSource? cts = new();

    public async void Start(int port, CancellationToken stoppingToken)
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

    public async void StartSend(TcpClient client, CancellationToken stoppingToken)
    {
        NetworkStream ns = client.GetStream();

        ConcurrentDictionary<Guid, DateTimeOffset> packetMap = [];

        while (!stoppingToken.IsCancellationRequested)
        {
            Guid msgGuid = Guid.NewGuid();
            packetMap[msgGuid] = DateTimeOffset.UtcNow;
            byte[] msgBytes = Encoding.Default.GetBytes(msgGuid.ToString());
            await ns.WriteAsync(msgBytes, stoppingToken);

            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch { }
        }

        while (client.Connected)
        {
            byte[] msgBytes = new byte[4096];
            await ns.ReadAsync(msgBytes, stoppingToken);
            string msgStr = Encoding.Default.GetString(msgBytes);
            if (Guid.TryParse(msgStr, out var msgGuid) && packetMap.TryGetValue(msgGuid, out var dateTimeSent))
            {
                TimeSpan span = DateTimeOffset.UtcNow - dateTimeSent;

            }
        }
    }

    public void Stop()
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
