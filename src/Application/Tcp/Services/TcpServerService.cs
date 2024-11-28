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
using Application.Common;
using Application.StreamPipeline.Common;
using Application.Tcp.Common;
using Microsoft.AspNetCore.Hosting.Server;

namespace Application.Tcp.Services;

[Disposable]
public partial class TcpServerService(ILogger<TcpServerService> logger)
{
    private readonly ILogger<TcpServerService> _logger = logger;

    private CancellationTokenSource? _cts = null;
    private string? _serverHost = null;
    private int _serverPort = 0;

    public Task Start(string serverHost, int serverPort, Func<TcpClient, TranceiverStream, CancellationTokenSource, Task> onClientCallback, CancellationToken stoppingToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(Start), new()
        {
            ["ServerHost"] = serverHost,
            ["ServerPort"] = serverPort,
        });

        if (_cts != null)
        {
            throw new Exception("TCP server already started");
        }

        _serverHost = serverHost;
        _serverPort = serverPort;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            CancelWhenDisposing());

        var serverAddress = Dns.GetHostEntry(serverHost).AddressList.Last();

        TcpListener server = new(serverAddress, serverPort);
        _cts.Token.Register(() =>
        {
            server.Stop();
            server.Dispose();
        });

        server.Start();

        return Task.Run(async () =>
        {
            using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(Start), new()
            {
                ["ServerAddress"] = serverAddress,
                ["ServerHost"] = _serverHost,
                ["ServerPort"] = _serverPort,
            });

            _logger.LogTrace("TCP server {Address}:{Port} started", serverAddress, serverPort);

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    TcpClient tcpClient = await server.AcceptTcpClientAsync(_cts.Token);
                    IPAddress clientAddress = (tcpClient.Client.LocalEndPoint as IPEndPoint)?.Address!;
                    NetworkStream networkStream = tcpClient.GetStream();
                    TranceiverStream tranceiverStream = new(networkStream, networkStream);
                    CancellationTokenSource clientCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

                    StartClient(tcpClient, serverAddress, networkStream, tranceiverStream, clientAddress, clientCts, onClientCallback).Forget();
                }
                catch (Exception ex)
                {
                    if (_cts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    _logger.LogError("Error {Address}:{Port}: {ErrorMessage}", serverAddress, serverPort, ex.Message);
                }
            }

            _logger.LogTrace("TCP server {Address}:{Port} ended", serverAddress, serverPort);

        }, _cts.Token);
    }

    private async Task StartClient(TcpClient tcpClient, IPAddress serverAddress, NetworkStream networkStream, TranceiverStream tranceiverStream, IPAddress clientAddress, CancellationTokenSource cts, Func<TcpClient, TranceiverStream, CancellationTokenSource, Task> onClientCallback)
    {
        using var _ = _logger.BeginScopeMap(nameof(TcpServerService), nameof(StartClient), new()
        {
            ["ServerAddress"] = serverAddress,
            ["ServerHost"] = _serverHost,
            ["ServerPort"] = _serverPort,
            ["ClientAddress"] = clientAddress,
        });

        try
        {
            _logger.LogInformation("TCP server {Address}:{Port} connected to the client {ClientAddress}", serverAddress, _serverPort, clientAddress);

            onClientCallback.Invoke(tcpClient, tranceiverStream, cts).Forget();

            await TcpClientHelpers.WatchLiveliness(tcpClient, networkStream, tranceiverStream, cts, TcpDefaults.LivelinessSpan);

            _logger.LogInformation("TCP server {Address}:{Port} disconnected from the client {ClientAddress}", serverAddress, _serverPort, clientAddress);

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
            _logger.LogError("Error {Address}:{Port} client {ClientAddress}: {ErrorMessage}", serverAddress, _serverPort, clientAddress, ex.Message);
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
