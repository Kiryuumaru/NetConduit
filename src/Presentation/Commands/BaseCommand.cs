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

internal abstract class BaseCommand : ICommand
{
    [CommandOption("log-level", 'l', Description = "Level of logs to show.")]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    [CommandOption("as-json", Description = "Output as json.")]
    public bool AsJson { get; set; } = false;

    [CommandOption("home", Description = "Home directory.", EnvironmentVariable = "NET_CONDUIT_HOME")]
    public string Home { get; set; } = AbsolutePath.Create(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)) / "netc";

    public ApplicationHostBuilder<WebApplicationBuilder> CreateBuilder()
    {
        var appBuilder = ApplicationHost.FromBuilder(WebApplication.CreateBuilder())
            .Add<Presentation>()
            .Add<SerilogInfrastructure>()
            .Add<SQLiteLocalStoreInfrastructure>();

        appBuilder.Configuration.SetLoggerLevel(LogLevel);

        try
        {
            appBuilder.Configuration.SetHomePath(AbsolutePath.Create(Home));
        }
        catch
        {
            throw new CommandException($"Invalid home directory \"{Home}\".", 1000);
        }

        return appBuilder;
    }

    public async ValueTask ExecuteAsync(IConsole console)
    {
        var appBuilder = CreateBuilder();
        var cancellationToken = console.RegisterCancellationHandler();

        try
        {
            await Run(appBuilder, cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    public abstract ValueTask Run(ApplicationHostBuilder<WebApplicationBuilder> appBuilder, CancellationToken stoppingToken);
}
