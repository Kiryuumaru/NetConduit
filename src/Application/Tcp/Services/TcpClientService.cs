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
using Application.Common;
using Application.StreamPipeline.Common;
using Microsoft.AspNetCore.Hosting.Server;
using System.Net.Http;
using System.IO;
using Application.Tcp.Common;

namespace Application.Tcp.Services;

[Disposable]
public partial class TcpClientService(ILogger<TcpClientService> logger)
{
    private readonly ILogger<TcpClientService> _logger = logger;

    private CancellationTokenSource? _cts = null;
    private string? _serverHost = null;
    private int _serverPort = 0;

    public Task Start(string serverHost, int serverPort, Func<TranceiverStream, CancellationTokenSource, Task> onClientCallback, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(Start), new()
        {
            ["ServerHost"] = serverHost,
            ["ServerPort"] = serverPort,
        });

        if (_cts != null)
        {
            throw new Exception("TCP client already started");
        }

        _serverHost = serverHost;
        _serverPort = serverPort;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            CancelWhenDisposing());

        TcpClient tcpClient = new();
        _cts.Token.Register(() =>
        {
            tcpClient.Close();
            tcpClient.Dispose();
        });

        return Task.Run(async () =>
        {
            _logger.LogInformation("TCP client {Host}:{Port} started", _serverHost, _serverPort);

            while (!_cts.Token.IsCancellationRequested)
            {
                CancellationTokenSource clientCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

                try
                {
                    var serverAddress = Dns.GetHostEntry(_serverHost).AddressList.Last();

                    using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(Start), new()
                    {
                        ["ServerHost"] = _serverHost,
                        ["ServerPort"] = _serverPort,
                        ["ServerAddress"] = serverAddress,
                    });

                    tcpClient = new();

                    await tcpClient.ConnectAsync(serverAddress, serverPort, clientCts.Token);

                    NetworkStream networkStream = tcpClient.GetStream();
                    TranceiverStream tranceiverStream = new(networkStream, networkStream);

                    await StartClient(tcpClient, serverAddress, networkStream, tranceiverStream, onClientCallback, clientCts);
                }
                catch (Exception ex)
                {
                    if (clientCts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("Error {Host}:{Port}: {ErrorMessage}", _serverHost, _serverPort, ex.Message);
                }

                await Task.Delay(TcpDefaults.LivelinessSpan, _cts.Token);
            }

            _logger.LogInformation("TCP client {Host}:{Port} ended", _serverHost, _serverPort);

        }, _cts.Token);
    }

    private async Task StartClient(TcpClient tcpClient, IPAddress serverAddress, NetworkStream networkStream, TranceiverStream tranceiverStream, Func<TranceiverStream, CancellationTokenSource, Task> onClientCallback, CancellationTokenSource cts)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(Start), new()
        {
            ["ServerHost"] = _serverHost,
            ["ServerPort"] = _serverPort,
            ["ServerAddress"] = serverAddress,
        });

        try
        {
            _logger.LogInformation("TCP client connected to the server {Host}:{Port}", serverAddress, _serverPort);

            onClientCallback.Invoke(tranceiverStream, cts).Forget();

            await TcpClientHelpers.WatchLiveliness(tcpClient, networkStream, tranceiverStream, cts, TcpDefaults.LivelinessSpan);

            _logger.LogInformation("TCP client disconnected from the server {Host}:{Port}", serverAddress, _serverPort);

            cts.Cancel();
            tcpClient.Close();
            tcpClient.Dispose();
            networkStream.Close();
            networkStream.Dispose();
            tranceiverStream.Dispose();
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }
            _logger.LogError("Error {Host}:{Port}: {ErrorMessage}", serverAddress, _serverPort, ex.Message);
        }
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