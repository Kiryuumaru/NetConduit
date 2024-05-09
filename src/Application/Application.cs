using Application.EventHub.Services;
using Application.LocalStore.Services;
using Application.StreamLine.Services;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public class Application : ApplicationDependency
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        base.AddServices(builder, services);

        services.AddTransient<LocalStoreService>();
        services.AddSingleton<EventHubService>();
        services.AddSingleton<StreamLineService>();
    }
}
