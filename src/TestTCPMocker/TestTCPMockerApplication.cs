using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestTCPMocker.Configuration.Extensions;
using TestTCPMocker.Services;
using TestTCPMocker.Workers;

namespace TestTCPMocker;

internal class TestTCPMockerApplication : ApplicationDependency
{
    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        base.AddServices(applicationBuilder, services);

        services.AddScoped<TCPClientMocker>();
        services.AddScoped<TCPRelayMocker>();
        services.AddScoped<TCPServerMocker>();

        if (!string.IsNullOrEmpty(applicationBuilder.Configuration.GetClientConnect()))
        {
            services.AddHostedService<TCPClientWorker>();
        }

        if (!string.IsNullOrEmpty(applicationBuilder.Configuration.GetRelayConnect()))
        {
            services.AddHostedService<TCPRelayWorker>();
        }

        if (!string.IsNullOrEmpty(applicationBuilder.Configuration.GetServerConnect()))
        {
            services.AddHostedService<TCPServerWorker>();
        }
    }
}
