using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Services;
using Application.Edge.Workers;
using Application.LocalStore.Services;
using Application.PortRoute.Services;
using Application.ServiceMaster.Services;
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
        services.AddScoped<EdgeApiService>();
        services.AddScoped<PortRouteApiService>();
        services.AddScoped<ServiceManagerService>();
        services.AddScoped<DaemonManagerService>();

        if (applicationBuilder.Configuration.GetServerEndpoint() != null)
        {
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
