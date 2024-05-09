using Application.Common;
using Domain.Edge.Models;
using Infrastructure.SignalR.Edge.Connection.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using Application.Edge.Handshake.Services;

namespace Infrastructure.SignalR.Client.Edge.Applet.Workers;

public class HandshakeWorker(ILogger<HandshakeWorker> logger, IServiceProvider serviceProvider, IConfiguration configuration) : BackgroundService
{
    private readonly ILogger<HandshakeWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    private readonly int _batchToSend = 100;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartStream(stoppingToken);

        return Task.CompletedTask;
    }

    private async void StartStream(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var portRouteEventHubService = _serviceProvider.GetRequiredService<HandshakeService>();
        var signalRStreamService = scope.ServiceProvider.GetRequiredService<SignalRStreamService>();
        var handshakeToken = _configuration.GetVarRefValue("HANDSHAKE_TOKEN");

        while (!stoppingToken.IsCancellationRequested)
        {
            var getHubToken = stoppingToken.WithTimeout(TimeSpan.FromSeconds(10));

            try
            {
                var getHubConnectionResult = await signalRStreamService.GetHubConnectionAsync(getHubToken);
                if (getHubToken.IsCancellationRequested)
                {
                    continue;
                }
                else
                {
                    getHubConnectionResult.ThrowIfErrorOrHasNoValue();
                }

                bool hasFirstPayload = false;

                await foreach (var edgeInvoke in getHubConnectionResult.Value.StreamAsync<EdgeRoutingTable>(Defaults.HandshakeMethod, handshakeToken, stoppingToken))
                {
                    if (!hasFirstPayload)
                    {
                        hasFirstPayload = true;
                        _logger.LogInformation("Handshake stream attached ({})", edgeInvoke.Name);
                    }
                    _logger.LogInformation("Handshake received updates");
                    portRouteEventHubService.OnHandshakeChanges(edgeInvoke);
                }

                _logger.LogInformation("Handshake subscribe done");
            }
            catch (Exception ex)
            {
                _logger.LogError("Handshake subscribe error: {}", ex.Message);
            }
            finally
            {
                await Task.Delay(2000, stoppingToken);
            }
        }
    }
}
