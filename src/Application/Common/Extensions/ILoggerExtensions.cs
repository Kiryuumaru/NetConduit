using Microsoft.Extensions.Logging;

namespace Application.Common.Extensions;

public static class ILoggerExtensions
{
    public static IDisposable? BeginScopeMap(this ILogger logger, Dictionary<string, object?> scopeMap)
    {
        return logger.BeginScope(scopeMap);
    }

    public static IDisposable? BeginScopeMap(this ILogger logger, string serviceName, string serviceAction, Dictionary<string, object?>? scopeMap = null)
    {
        scopeMap ??= [];
        scopeMap["Service"] = serviceName;
        scopeMap[$"{serviceName}_ServiceAction"] = serviceAction;
        return logger.BeginScope(scopeMap);
    }
}
