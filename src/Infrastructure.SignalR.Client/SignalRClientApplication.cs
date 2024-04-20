using ApplicationBuilderHelpers;
using Infrastructure.SignalR.Client.Connection.Services;
using Infrastructure.SignalR.Client.Connection.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.SignalR.Client;

public class SignalRClientApplication : ApplicationDependency
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        services.AddScoped<SignalRStreamService>();
        services.AddSingleton<SignalRStreamProxyService>();
        services.AddHostedService<SignalRStreamWorker>();
    }
}
