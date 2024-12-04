using DisposableHelpers.Attributes;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using System.Text;

namespace TestTCPMocker.Services;

[Disposable]
internal partial class TCPClientMocker(ILogger<TCPClientMocker> logger)
{
    private readonly ILogger<TCPClientMocker> _logger = logger;

    private CancellationTokenSource? cts = new();

    public async Task StartWait(string host, int port, CancellationToken stoppingToken)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = cts.Token;

        TcpClient client = new();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await client.ConnectAsync(host, port, stoppingToken);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("{Error}", ex.Message);
            }

            await Task.Delay(2000, stoppingToken);
        }

        ct.Register(() =>
        {
            client.Close();
            client.Dispose();
        });

        _logger.LogInformation("Connected to server {ServerHost}:{ServerPort}", host, port);

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
                var readBytes = await ns.ReadAsync(receivedBytes, stoppingToken);

                DateTimeOffset receivedTime = DateTimeOffset.UtcNow;

                string receivedStr = Encoding.Default.GetString(receivedBytes.AsSpan()[..readBytes]);

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

    public async void Start(string host, int port, CancellationToken stoppingToken)
    {
        await StartWait(host, port, stoppingToken);
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
