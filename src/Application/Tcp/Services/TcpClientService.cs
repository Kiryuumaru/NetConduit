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
using Microsoft.Extensions.Hosting;

namespace Application.Tcp.Services;

[Disposable]
public partial class TcpClientService(ILogger<TcpClientService> logger)
{
    private readonly ILogger<TcpClientService> _logger = logger;

    private CancellationTokenSource? _cts = null;
    private IPAddress? _ipAddress = null;
    private int _port = 0;
    private int _bufferSize = 0;

    public async Task Start(IPAddress address, int port, Func<TcpClient, NetworkStream, TcpClientStream> clientStreamFactory, CancellationToken stoppingToken)
    {
        if (_cts != null)
        {
            throw new Exception("TCP server already started");
        }

        _ipAddress = address;
        _port = port;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = _cts.Token;

        TcpClient client = new();
        ct.Register(() =>
        {
            client.Close();
            client.Dispose();
        });

        while (!ct.IsCancellationRequested)
        {
            client = new();
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await client.ConnectAsync(address, port, stoppingToken);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError("{Error}", ex.Message);
                }

                await Task.Delay(2000, stoppingToken);
            }

            _logger.LogInformation("Connected to server {ServerHost}:{ServerPort}", address, port);

            NetworkStream ns = client.GetStream();

            while (!ct.IsCancellationRequested)
            {
                string sendStr = Guid.NewGuid().ToString();
                byte[] sendBytes = Encoding.Default.GetBytes(sendStr);

                try
                {
                    DateTimeOffset sendTime = DateTimeOffset.UtcNow;

                    await ns.WriteAsync(sendBytes, stoppingToken);
                    byte[] receivedBytes = new byte[4096];
                    await ns.ReadAsync(receivedBytes, stoppingToken);

                    DateTimeOffset receivedTime = DateTimeOffset.UtcNow;

                    string receivedStr = Encoding.Default.GetString(receivedBytes.Where(x => x != 0).ToArray());

                    if (sendStr != receivedStr)
                    {
                        _logger.LogError("Mismatch: {Sent} != {Received}", sendStr, receivedStr);
                    }
                    else
                    {
                        _logger.LogInformation("Received time {TimeStamp}ms...", (receivedTime - sendTime).TotalMilliseconds);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("{Error}", ex.Message);
                }

                await Task.Delay(5000, stoppingToken);
            }
        }
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