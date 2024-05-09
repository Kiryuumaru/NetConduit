using ApplicationBuilderHelpers;
using Infrastructure.SignalR.Client.Edge.Applet.Workers;
using Infrastructure.SignalR.Edge.Connection.Services;
using Infrastructure.SignalR.Edge.Connection.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.SignalR.Edge;

public class SignalREdgeApplication : ApplicationDependency
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        services.AddScoped<SignalRStreamService>();
        services.AddSingleton<SignalRStreamProxyService>();
        services.AddHostedService<SignalRStreamWorker>();

        services.AddHostedService<HandshakeWorker>();
    }
}
