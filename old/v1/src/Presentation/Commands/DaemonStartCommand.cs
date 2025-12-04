using ApplicationBuilderHelpers;
using CliFx.Attributes;
using Presentation.Services;

namespace Presentation.Commands;

[Command("daemon start", Description = "Daemon start command.")]
public class DaemonStartCommand : BaseCommand
{
    public override async ValueTask Run(ApplicationHostBuilder<WebApplicationBuilder> appBuilder, CancellationToken stoppingToken)
    {
        var appHost = appBuilder.Build();

        var clientServiceManager = appHost.Host.Services.GetRequiredService<ClientManager>();

        await clientServiceManager.Start(stoppingToken);
    }
}
