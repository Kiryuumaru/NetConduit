using AbsolutePathHelpers;
using Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Configuration.Extensions;

public static class ApplicationConfigurationExtensions
{
    public static string GetServerEndpoint(this IConfiguration configuration)
    {
        return configuration.GetVarRefValue("NET_CONDUIT_SERVER_ENDPOINT");
    }
    public static void SetServerEndpoint(this IConfiguration configuration, string? serverEndpoint)
    {
        configuration["NET_CONDUIT_SERVER_ENDPOINT"] = serverEndpoint;
    }

    public static string GetHandshakeToken(this IConfiguration configuration)
    {
        return configuration.GetVarRefValue("NET_CONDUIT_HANDSHAKE_TOKEN");
    }
    public static void SetHandshakeToken(this IConfiguration configuration, string? handshakeToken)
    {
        configuration["NET_CONDUIT_HANDSHAKE_TOKEN"] = handshakeToken;
    }

    public static bool GetStartAsServerMode(this IConfiguration configuration)
    {
        var operationMode = configuration.GetVarRefValue("NET_CONDUIT_OPERATION_MODE");
        return operationMode.ToLowerInvariant() switch
        {
            "server" => true,
            "client" => false,
            _ => throw new Exception($"Invalid NET_CONDUIT_OPERATION_MODE value \"{operationMode}\"")
        };
    }
    public static void SetStartAsServerMode(this IConfiguration configuration, bool? startAsServerMode)
    {
        configuration["NET_CONDUIT_OPERATION_MODE"] = startAsServerMode.HasValue ? (startAsServerMode.Value ? "server" : "client") : null;
    }
}
