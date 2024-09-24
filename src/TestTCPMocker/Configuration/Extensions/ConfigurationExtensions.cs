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

namespace TestTCPMocker.Configuration.Extensions;

public static class ConfigurationExtensions
{
    public static bool GetIsServerMode(this IConfiguration configuration)
    {
        return configuration.GetVarRefValueOrDefault("TCP_MOCKER_SERVER_MODE", "no").Equals("yes", StringComparison.InvariantCultureIgnoreCase);
    }
    public static void SetIsServerMode(this IConfiguration configuration, bool makeFileLogs)
    {
        configuration["TCP_MOCKER_SERVER_MODE"] = makeFileLogs ? "yes" : "no";
    }
}
