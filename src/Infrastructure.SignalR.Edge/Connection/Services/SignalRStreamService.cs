using Application.Common;
using DisposableHelpers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;
using TransactionHelpers;
using System;

namespace Infrastructure.SignalR.Edge.Connection.Services;

public class SignalRStreamService(IServiceProvider serviceProvider)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public Task<Result<HubConnection>> GetHubConnectionAsync(CancellationToken cancellationToken)
    {
        var signalRStreamProxy = _serviceProvider.GetRequiredService<SignalRStreamProxyService>();

        return signalRStreamProxy.GetHubConnectionAsync(cancellationToken);
    }
}
