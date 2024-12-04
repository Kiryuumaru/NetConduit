using Application.Edge.Interfaces;
using Application.Edge.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
