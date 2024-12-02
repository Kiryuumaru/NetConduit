using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Application.Edge.Services;
using Application.Edge.Mockers;
using Application.LocalStore.Services;
using Application.ServiceMaster.Services;
using Application.StreamPipeline.Pipes;
using Application.StreamPipeline.Services;
using Application.Tcp.Services;
using Application.Watchdog.Workers;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;
using Application.Edge.Workers;
using Application.Edge.Services.Handshake;
using Application.Edge.Services.HiveStore;
using Application.Edge.Services.LocalStore;

namespace Application;

public class Application : ApplicationDependency
{
    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        base.AddServices(applicationBuilder, services);

        services.AddHostedService<GCCollector>();

        services.AddTransient<LocalStoreFactoryService>();
        services.AddSingleton<LocalStoreConcurrencyService>();

        services.AddScoped<ServiceManagerService>();
        services.AddScoped<DaemonManagerService>();

        services.AddScoped<StreamPipelineService>();
        services.AddScoped<StreamPipelineFactory>();
        services.AddTransient(typeof(MessagingPipe<,>));
        services.AddTransient(typeof(CommandPipe<,>));

        services.AddTransient<TcpClientService>();
        services.AddTransient<TcpServerService>();

        services.AddSingleton<EdgeWorkerStartedService>();

        if (applicationBuilder.Configuration.GetStartAsServerMode())
        {
            services.AddScoped<EdgeServerHandshakeService>();

            services.AddScoped<IEdgeLocalStoreService, EdgeLocalStoreService>();
            services.AddScoped<IEdgeHiveStoreService, EdgeHiveStoreService>();

            services.AddHostedService<EdgeServerWorker>();

            services.AddHostedService<EdgeServerMockWorker>();
        }
        else
        {
            services.AddScoped<EdgeClientHandshakeService>();

            services.AddScoped<IEdgeLocalStoreService, EdgeLocalStoreService>();
            services.AddScoped<IEdgeHiveStoreService, EdgeHiveStoreApiService>();

            services.AddHostedService<EdgeClientWorker>();

            services.AddHostedService<EdgeClientMockWorker>();
        }
    }
}
