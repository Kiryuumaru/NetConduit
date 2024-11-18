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
    public const string ApiUrlsKey = "NET_CONDUIT_API_URLS";
    public static string GetApiUrls(this IConfiguration configuration)
    {
        return configuration.GetVarRefValue(ApiUrlsKey);
    }
    public static void SetApiUrls(this IConfiguration configuration, string apiUrls)
    {
        configuration[ApiUrlsKey] = apiUrls;
    }

    public const string ServerTcpHostKey = "NET_CONDUIT_SERVER_TCP_HOST";
    public static string GetServerTcpHost(this IConfiguration configuration)
    {
        return configuration.GetVarRefValue(ServerTcpHostKey);
    }
    public static void SetServerTcpHost(this IConfiguration configuration, string tcpHost)
    {
        configuration[ServerTcpHostKey] = tcpHost;
    }

    public const string ServerTcpPortKey = "NET_CONDUIT_SERVER_TCP_PORT";
    public static int GetServerTcpPort(this IConfiguration configuration)
    {
        return int.Parse(configuration.GetVarRefValue(ServerTcpPortKey));
    }
    public static void SetServerTcpPort(this IConfiguration configuration, int tcpPort)
    {
        configuration[ServerTcpPortKey] = tcpPort.ToString();
    }

    public const string HandshakeTokenKey = "NET_CONDUIT_HANDSHAKE_TOKEN";
    public static string GetHandshakeToken(this IConfiguration configuration)
    {
        return configuration.GetVarRefValue(HandshakeTokenKey);
    }
    public static void SetHandshakeToken(this IConfiguration configuration, string? handshakeToken)
    {
        configuration[HandshakeTokenKey] = handshakeToken;
    }

    public const string OperationModeKey = "NET_CONDUIT_OPERATION_MODE";
    public static bool GetStartAsServerMode(this IConfiguration configuration)
    {
        var operationMode = configuration.GetVarRefValue(OperationModeKey);
        return operationMode.ToLowerInvariant() switch
        {
            "server" => true,
            "client" => false,
            _ => throw new Exception($"Invalid {OperationModeKey} value \"{operationMode}\"")
        };
    }
    public static void SetStartAsServerMode(this IConfiguration configuration, bool? startAsServerMode)
    {
        configuration[OperationModeKey] = startAsServerMode.HasValue ? (startAsServerMode.Value ? "server" : "client") : null;
    }
}
