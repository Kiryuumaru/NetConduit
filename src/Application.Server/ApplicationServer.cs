using Application.Edge.StreamLine.Workers;
using Application.Server.Edge.Services;
using Application.Server.PortRoute.Services;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Server;

public class ApplicationServer : Application
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        base.AddServices(builder, services);

        services.AddScoped<EdgeService>();
        services.AddScoped<EdgeStoreService>();
        services.AddSingleton<PortRouteEventHubService>();
        services.AddScoped<PortRouteService>();
        services.AddScoped<PortRouteStoreService>();
    }
}
