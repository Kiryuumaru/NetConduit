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

public static class ConfigurationExtensions
{
    public static bool ContainsVarRefValue(this IConfiguration configuration, string varName)
    {
        try
        {
            return !string.IsNullOrEmpty(GetVarRefValue(configuration, varName));
        }
        catch
        {
            return false;
        }
    }

    public static string GetVarRefValue(this IConfiguration configuration, string varName)
    {
        string? varValue = $"@ref:{varName}";
        while (true)
        {
            if (varValue.StartsWith("@ref:"))
            {
                varName = varValue[5..];
                varValue = configuration[varName];
                if (string.IsNullOrEmpty(varValue))
                {
                    throw new Exception($"{varName} is empty.");
                }
                continue;
            }
            break;
        }
        return varValue;
    }

    [return: NotNullIfNotNull(nameof(defaultValue))]
    public static string? GetVarRefValueOrDefault(this IConfiguration configuration, string varName, string? defaultValue = null)
    {
        try
        {
            return GetVarRefValue(configuration, varName);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static Guid? _runtimeGuid = null;
    public static Guid GetRuntimeGuid(this IConfiguration configuration)
    {
        if (_runtimeGuid == null)
        {
            var runtimeGuidStr = configuration.GetVarRefValueOrDefault("NET_CONDUIT_RUNTIME_GUID", null);
            if (string.IsNullOrEmpty(runtimeGuidStr))
            {
                _runtimeGuid = Guid.NewGuid();
            }
            else
            {
                _runtimeGuid = Guid.Parse(runtimeGuidStr);
            }
        }
        return _runtimeGuid.Value;
    }

    public static bool GetMakeFileLogs(this IConfiguration configuration)
    {
        return configuration.GetVarRefValueOrDefault("NET_CONDUIT_MAKE_LOGS", "no").Equals("yes", StringComparison.InvariantCultureIgnoreCase);
    }
    public static void SetMakeFileLogs(this IConfiguration configuration, bool makeFileLogs)
    {
        configuration["NET_CONDUIT_MAKE_LOGS"] = makeFileLogs ? "yes" : "no";
    }

    public static LogLevel GetLoggerLevel(this IConfiguration configuration)
    {
        var loggerLevel = configuration.GetVarRefValueOrDefault("NET_CONDUIT_LOGGER_LEVEL", LogLevel.Information.ToString());
        return Enum.Parse<LogLevel>(loggerLevel);
    }
    public static void SetLoggerLevel(this IConfiguration configuration, LogLevel loggerLevel)
    {
        configuration["NET_CONDUIT_LOGGER_LEVEL"] = loggerLevel.ToString();
    }

    public static AbsolutePath GetDataPath(this IConfiguration configuration)
    {
        //return configuration.GetVarRefValueOrDefault("NET_CONDUIT_DATA_PATH", AbsolutePath.Create(Environment.CurrentDirectory) / ".data");
        return configuration.GetVarRefValueOrDefault("NET_CONDUIT_DATA_PATH", AbsolutePath.Create("C:\\NetConduit") / ".data");
    }
    public static void SetDataPath(this IConfiguration configuration, AbsolutePath dataPath)
    {
        configuration["NET_CONDUIT_DATA_PATH"] = dataPath;
    }

    public static string? GetServerEndpoint(this IConfiguration configuration)
    {
        return configuration.GetVarRefValueOrDefault("NET_CONDUIT_SERVER_ENDPOINT", null);
    }
    public static void SetServerEndpoint(this IConfiguration configuration, string? serverEndpoint)
    {
        configuration["NET_CONDUIT_SERVER_ENDPOINT"] = serverEndpoint;
    }

    public static string? GetHandshakeToken(this IConfiguration configuration)
    {
        return configuration.GetVarRefValueOrDefault("NET_CONDUIT_HANDSHAKE_TOKEN", null);
    }
    public static void SetHandshakeToken(this IConfiguration configuration, string? handshakeToken)
    {
        configuration["NET_CONDUIT_HANDSHAKE_TOKEN"] = handshakeToken;
    }

    public static bool? GetStartAsServerMode(this IConfiguration configuration)
    {
        var operationMode = configuration.GetVarRefValueOrDefault("NET_CONDUIT_OPERATION_MODE", null);
        return operationMode?.ToLowerInvariant() switch
        {
            "server" => true,
            "client" => false,
            _ => null
        };
    }
    public static void SetStartAsServerMode(this IConfiguration configuration, bool? startAsServerMode)
    {
        configuration["NET_CONDUIT_OPERATION_MODE"] = startAsServerMode.HasValue ? (startAsServerMode.Value ? "server" : "client") : null;
    }
}
