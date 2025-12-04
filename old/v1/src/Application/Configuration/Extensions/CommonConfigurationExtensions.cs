using AbsolutePathHelpers;
using Application.Configuration.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Application.Configuration.Extensions;

public static class CommonConfigurationExtensions
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
                    throw new NoConfigValueException(varName);
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
            _runtimeGuid = Guid.Parse(configuration.GetVarRefValueOrDefault($"{ApplicationDefaults.AppNameUpperSnakeCase}_RUNTIME_GUID", Guid.NewGuid().ToString()));
        }
        return _runtimeGuid.Value;
    }

    public static bool GetMakeFileLogs(this IConfiguration configuration)
    {
        return configuration.GetVarRefValueOrDefault($"{ApplicationDefaults.AppNameUpperSnakeCase}_MAKE_LOGS", "no").Equals("yes", StringComparison.InvariantCultureIgnoreCase);
    }
    public static void SetMakeFileLogs(this IConfiguration configuration, bool makeFileLogs)
    {
        configuration[$"{ApplicationDefaults.AppNameUpperSnakeCase}_MAKE_LOGS"] = makeFileLogs ? "yes" : "no";
    }

    public const string LoggerLevelKey = $"{ApplicationDefaults.AppNameUpperSnakeCase}_LOGGER_LEVEL";
    public static LogLevel GetLoggerLevel(this IConfiguration configuration)
    {
        var loggerLevel = configuration.GetVarRefValueOrDefault(LoggerLevelKey, LogLevel.Information.ToString());
        return Enum.Parse<LogLevel>(loggerLevel);
    }
    public static void SetLoggerLevel(this IConfiguration configuration, LogLevel loggerLevel)
    {
        configuration[LoggerLevelKey] = loggerLevel.ToString();
    }

    public const string HomePathKey = $"{ApplicationDefaults.AppNameUpperSnakeCase}_HOME_PATH";
    public static AbsolutePath GetHomePath(this IConfiguration configuration)
    {
        return configuration.GetVarRefValue(HomePathKey);
    }
    public static void SetHomePath(this IConfiguration configuration, AbsolutePath dataPath)
    {
        configuration[HomePathKey] = dataPath;
    }

    public static AbsolutePath GetDataPath(this IConfiguration configuration)
    {
        return GetHomePath(configuration) / ".data";
    }

    public static AbsolutePath GetTempPath(this IConfiguration configuration)
    {
        return GetDataPath(configuration) / "temp";
    }

    public static AbsolutePath GetDownloadsPath(this IConfiguration configuration)
    {
        return GetDataPath(configuration) / "downloads";
    }

    public static AbsolutePath GetServicesPath(this IConfiguration configuration)
    {
        return GetDataPath(configuration) / "svc";
    }

    public static AbsolutePath GetDaemonsPath(this IConfiguration configuration)
    {
        return GetDataPath(configuration) / "daemon";
    }
}
