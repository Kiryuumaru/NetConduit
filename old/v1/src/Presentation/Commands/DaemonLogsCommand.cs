using ApplicationBuilderHelpers;
using CliFx.Attributes;
using Application.Logger.Interfaces;
using CliFx.Exceptions;

namespace Presentation.Commands;

[Command("daemon logs", Description = "Daemon logs command.")]
public class DaemonLogsCommand : BaseCommand
{
    [CommandOption("tail", 't', Description = "Log lines print.")]
    public int Tail { get; set; } = 10;

    [CommandOption("follow", 'f', Description = "Follows logs.")]
    public bool Follow { get; set; }

    [CommandOption("scope", 's', Description = "Scope of logs.")]
    public IReadOnlyList<string>? Scope { get; set; }

    public override async ValueTask Run(ApplicationHostBuilder<WebApplicationBuilder> appBuilder, CancellationToken stoppingToken)
    {
        Dictionary<string, string> scopePairs = [];
        if (Scope != null)
        {
            foreach (var s in Scope)
            {
                try
                {
                    var pair = s.Split('=');
                    if (pair.Length != 2)
                    {
                        throw new Exception();
                    }
                    scopePairs[pair[0]] = pair[1];
                }
                catch
                {
                    throw new CommandException($"Invalid scope value \"{s}\".", 1001);
                }
            }
        }

        var appHost = appBuilder.Build();

        var loggerReader = appHost.Host.Services.GetRequiredService<ILoggerReader>();

        await loggerReader.Start(Tail, Follow, scopePairs, stoppingToken);
    }
}
