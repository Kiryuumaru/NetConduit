using Application.Configuration.Extensions;
using Application.Logger.Interfaces;
using ApplicationBuilderHelpers;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Infrastructure.Serilog;
using Infrastructure.SQLite.LocalStore;
using Microsoft.Extensions.Logging;
using Presentation.Services;
using System.Threading;

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
