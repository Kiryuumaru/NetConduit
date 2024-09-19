using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Services;
using Application.EventHub.Services;
using Application.Handshake.Services;
using Application.LocalStore.Services;
using Application.PortRoute.Interfaces;
using Application.PortRoute.Services;
using Application.Server.Edge.Services;
using Application.Server.Edge.Workers;
using Application.Server.PortRoute.Services;
using Application.StreamLine.Services;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public class Application : ApplicationDependency
{
    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        base.AddServices(applicationBuilder, services);

        services.AddTransient<LocalStoreService>();
        services.AddSingleton<LocalStoreConcurrencyService>();
        services.AddSingleton<EventHubService>();
        services.AddSingleton<StreamLineService>();
        services.AddScoped<EdgeApiService>();
        services.AddScoped<PortRouteApiService>();

        if (applicationBuilder.Configuration.GetServerEndpoint() != null)
        {
            services.AddSingleton<HandshakeService>();
        }
        else
        {
            services.AddScoped<EdgeService>();
            services.AddScoped<EdgeStoreService>();
            services.AddScoped<PortRouteService>();
            services.AddScoped<PortRouteStoreService>();
            services.AddSingleton<PortRouteEventHubService>();
            services.AddHostedService<EdgeWorker>();
        }
    }
}
