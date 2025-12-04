using ApplicationBuilderHelpers;
using CliFx.Attributes;
using Presentation.Services;

namespace Presentation.Commands;

[Command("update", Description = "Update client.")]
public class UpdateCommand : BaseCommand
{
    public override async ValueTask Run(ApplicationHostBuilder<WebApplicationBuilder> appBuilder, CancellationToken stoppingToken)
    {
        var appHost = appBuilder.Build();

        var serviceManager = appHost.Host.Services.GetRequiredService<ClientManager>();

        await serviceManager.UpdateClient(stoppingToken);
    }
}
