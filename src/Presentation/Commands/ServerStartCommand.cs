using AbsolutePathHelpers;
using Application.Configuration.Extensions;
using ApplicationBuilderHelpers;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Infrastructure.Serilog;
using Infrastructure.SQLite.LocalStore;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using System.Reflection.PortableExecutable;
using System.Threading;

namespace Presentation.Commands;

[Command("server start", Description = "The main server command.")]
internal class ServerStartCommand : BaseCommand
{
    public override async ValueTask Run(ApplicationHostBuilder<WebApplicationBuilder> appBuilder, CancellationToken stoppingToken)
    {
        await appBuilder.Build().Run(stoppingToken);
    }
}
