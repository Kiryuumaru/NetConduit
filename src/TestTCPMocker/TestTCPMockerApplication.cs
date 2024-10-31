using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestTCPMocker.Services;
using TestTCPMocker.Workers;

namespace TestTCPMocker;

internal class TestTCPMockerApplication : ApplicationDependency
{
    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        base.AddServices(applicationBuilder, services);

        services.AddScoped<TCPClientMocker>();
        services.AddScoped<TCPServerMocker>();

        services.AddHostedService<TCPSenderWorker>();
        services.AddHostedService<TCPSenderWorker>();
    }
}
