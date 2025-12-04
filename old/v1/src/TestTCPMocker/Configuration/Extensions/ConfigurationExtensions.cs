using Application.Common.Extensions;
using Microsoft.Extensions.Configuration;

namespace TestTCPMocker.Configuration.Extensions;

public static class ConfigurationExtensions
{
    public static string? GetClientConnect(this IConfiguration configuration)
    {
        return configuration.GetVarRefValueOrDefault("TCP_MOCKER_CLIENT_TO_MOQ");
    }

    public static string? GetRelayConnect(this IConfiguration configuration)
    {
        return configuration.GetVarRefValueOrDefault("TCP_MOCKER_RELAY_TO_MOQ");
    }

    public static string? GetServerConnect(this IConfiguration configuration)
    {
        return configuration.GetVarRefValueOrDefault("TCP_MOCKER_SERVER_TO_MOQ");
    }
}
