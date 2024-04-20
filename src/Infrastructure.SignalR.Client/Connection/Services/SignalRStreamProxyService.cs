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

namespace Infrastructure.SignalR.Client.Connection.Services;

internal class SignalRStreamProxyService
{
    internal Func<CancellationToken, Task<Result<HubConnection>>>? GetHubConnection { get; set; }

    public async Task<Result<HubConnection>> GetHubConnectionAsync(CancellationToken cancellationToken)
    {
        while (GetHubConnection == null && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        if (GetHubConnection == null || cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException();
        }

        return await GetHubConnection.Invoke(cancellationToken);
    }
}
