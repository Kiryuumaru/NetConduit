using AbsolutePathHelpers;
using Application;
using ApplicationBuilderHelpers;
using CliFx;
using CliFx.Attributes;
using CliFx.Infrastructure;
using Infrastructure.Serilog;
using Infrastructure.SQLite.LocalStore;
using Microsoft.Extensions.Logging;
using Serilog;
using Application.Common;
using System.Runtime.InteropServices;
using Presentation.Services;
using Application.Logger.Interfaces;
using System.Threading;

namespace Presentation.Commands;

[Command("daemon uninstall", Description = "Daemon uninstall command.")]
public class DaemonUninstallCommand : BaseCommand
{
    public override async ValueTask Run(ApplicationHostBuilder<WebApplicationBuilder> appBuilder, CancellationToken stoppingToken)
    {
        var appHost = appBuilder.Build();

        var clientServiceManager = appHost.Host.Services.GetRequiredService<ClientManager>();

        await clientServiceManager.Uninstall(stoppingToken);
    }
}
