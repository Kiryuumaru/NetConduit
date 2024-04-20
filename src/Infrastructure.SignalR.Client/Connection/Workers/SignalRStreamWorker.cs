using Application.Common;
using DisposableHelpers;
using Infrastructure.SignalR.Client.Connection.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;
using TransactionHelpers;

namespace Infrastructure.SignalR.Client.Connection.Workers;

internal class SignalRStreamWorker(ILogger<SignalRStreamWorker> logger, IConfiguration configuration, IServiceProvider serviceProvider) : BackgroundService
{
    private readonly ILogger<SignalRStreamWorker> _logger = logger;
    private readonly IConfiguration _configuration = configuration;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private HubConnection? _hubConnection;

    public async Task<Result<HubConnection>> GetHubConnectionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
                {
                    return _hubConnection;
                }
                await Task.Delay(500, cancellationToken);
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        return new OperationCanceledException();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var signalRStreamProxy = _serviceProvider.GetRequiredService<SignalRStreamProxyService>();

        signalRStreamProxy.GetHubConnection = GetHubConnectionAsync;

        StartStream(stoppingToken);
        return Task.CompletedTask;
    }

    private void StartStream(CancellationToken stoppingToken)
    {
        Task.Run(async () =>
        {
            await Start(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                {
                    _logger.LogWarning("SignalR client disconnected");
                    await Start(stoppingToken);
                }
                await Task.Delay(5000, stoppingToken);
            }

        }, stoppingToken);
    }

    private async Task Start(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalR client connecting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string apiEndpoint = _configuration.GetVarRefValue("SERVER_ENDPOINT");

                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(apiEndpoint.Trim('/') + '/' + Defaults.DefaultStream.Trim('/'))
                    .Build();

                await _hubConnection.StartAsync(stoppingToken.WithTimeout(TimeSpan.FromSeconds(10)));

                break;
            }
            catch (Exception ex)
            {
                if (_hubConnection != null)
                {
                    await _hubConnection.DisposeAsync();
                }
                _logger.LogError("SignalR client connect error: {}. Retrying...", ex);
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("SignalR client connected");
    }
}
