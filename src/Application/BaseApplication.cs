using Application.Common;
using Application.Edge.Services;
using Application.EventHub.Services;
using Application.Handshake.Services;
using Application.LocalStore.Services;
using Application.PortRoute.Interfaces;
using Application.Server.Edge.Services;
using Application.Server.PortRoute.Services;
using Application.StreamLine.Services;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public class BaseApplication : ApplicationDependency
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        base.AddServices(builder, services);

        services.AddTransient<LocalStoreService>();
        services.AddSingleton<LocalStoreConcurrencyService>();
        services.AddSingleton<EventHubService>();
        services.AddSingleton<StreamLineService>();
        services.AddScoped<EdgeApiService>();
        services.AddScoped<PortRouteApiService>();

        if (builder.Configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
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
        }
    }
}
