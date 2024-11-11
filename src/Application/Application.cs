using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.Edge.Workers;
using Application.LocalStore.Services;
using Application.ServiceMaster.Services;
using Application.StreamPipeline.Models;
using Application.StreamPipeline.Services;
using Application.Tcp.Services;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public class Application : ApplicationDependency
{
    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        base.AddServices(applicationBuilder, services);

        services.AddTransient<LocalStoreFactoryService>();
        services.AddSingleton<LocalStoreConcurrencyService>();

        services.AddScoped<ServiceManagerService>();
        services.AddScoped<DaemonManagerService>();

        services.AddTransient<StreamPipelineService>();
        services.AddScoped<StreamPipelineFactory>();

        services.AddTransient<TcpClientService>();
        services.AddTransient<TcpServerService>();

        if (applicationBuilder.Configuration.GetStartAsServerMode())
        {
            services.AddScoped<IEdgeStoreService, EdgeStoreService>();

            services.AddHostedService<EdgeServerWorker>();
        }
        else
        {
            services.AddScoped<IEdgeStoreService, EdgeStoreApiService>();

            services.AddHostedService<EdgeClientWorker>();
        }
    }
}
