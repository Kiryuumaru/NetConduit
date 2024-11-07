using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.Edge.Workers;
using Application.LocalStore.Services;
using Application.ServiceMaster.Services;
using Application.Tcp.Services;
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
        services.AddScoped<ServiceManagerService>();
        services.AddScoped<DaemonManagerService>();
        services.AddTransient<TcpClientService>();
        services.AddTransient<TcpServerService>();

        if (applicationBuilder.Configuration.GetStartAsServerMode())
        {
            services.AddScoped<IEdgeService, EdgeService>();
            services.AddScoped<EdgeStoreService>();
            services.AddHostedService<EdgeServerWorker>();
        }
        else
        {
            services.AddScoped<IEdgeService, EdgeApiService>();
            services.AddHostedService<EdgeClientWorker>();
        }
    }
}
