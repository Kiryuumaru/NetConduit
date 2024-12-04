using Application.Configuration.Extensions;
using ApplicationBuilderHelpers;
using CliFx.Attributes;

namespace Presentation.Commands;

[Command("client start", Description = "The main client command.")]
public class ClientStartCommand : BaseCommand
{
    [CommandOption("server-host", Description = "Server TCP host for client-to-server communication.", EnvironmentVariable = ApplicationConfigurationExtensions.ServerTcpHostKey)]
    public string ServerHost { get; set; } = "localhost";

    [CommandOption("server-port", Description = "Server TCP port for client-to-server communication.", EnvironmentVariable = ApplicationConfigurationExtensions.ServerTcpPortKey)]
    public int ServerPort { get; set; } = 21000;

    [CommandOption("api-urls", Description = "API port for configurations.", EnvironmentVariable = ApplicationConfigurationExtensions.ApiUrlsKey)]
    public string ApiUrls { get; set; } = "http://*:21100";

    [CommandOption("server-handshake-token", Description = "Override handshake token to use.", EnvironmentVariable = ApplicationConfigurationExtensions.HandshakeTokenKey)]
    public string? HandshakeToken { get; set; } = null;

    public override async ValueTask Run(ApplicationHostBuilder<WebApplicationBuilder> appBuilder, CancellationToken stoppingToken)
    {
        appBuilder.Configuration.SetStartAsServerMode(false);

        appBuilder.Configuration.SetServerTcpHost(ServerHost);
        appBuilder.Configuration.SetServerTcpPort(ServerPort);
        appBuilder.Configuration.SetApiUrls(ApiUrls);

        if (!string.IsNullOrEmpty(HandshakeToken))
        {
            appBuilder.Configuration.SetHandshakeToken(HandshakeToken);
        }

        await appBuilder.Build().Run(stoppingToken);
    }
}
