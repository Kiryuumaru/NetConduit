using Application.Common;
using Application.Configuration.Extensions;
using ApplicationBuilderHelpers;
using Infrastructure.SignalR.Common;
using Infrastructure.SignalR.Edge.Connection.Services;
using Infrastructure.SignalR.Edge.Connection.Workers;
using Infrastructure.SignalR.Handshake.Services;
using Infrastructure.SignalR.Handshake.Workers;
using Infrastructure.SignalR.Server;
using Infrastructure.SignalR.Server.Handshake.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.SignalR.Edge;

public class SignalRInfrastructure : ApplicationDependency
{
    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        if (applicationBuilder.Configuration.GetServerEndpoint() != null)
        {
            services.AddScoped<SignalRStreamService>();
            services.AddSingleton<SignalRStreamProxyService>();
            services.AddHostedService<SignalRStreamWorker>();

            services.AddHostedService<HandshakeWorker>();
        }
        else
        {
            services.AddSignalR(hubOptions =>
            {
                hubOptions.EnableDetailedErrors = true;
                hubOptions.KeepAliveInterval = TimeSpan.FromSeconds(5);
            });
            services.AddResponseCompression(opts =>
            {
                opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/octet-stream"]);
            });
            services.AddTransient<HandshakeStreamHub>();
            services.AddSingleton<HandshakeLockerService>();
        }
    }

    public override void AddMiddlewares(ApplicationHost applicationHost, IHost host)
    {
        if (applicationHost.Configuration.GetServerEndpoint() == null)
        {
            (host as IApplicationBuilder)!.UseResponseCompression();
        }
    }

    public override void AddMappings(ApplicationHost applicationHost, IHost host)
    {
        if (applicationHost.Configuration.GetServerEndpoint() == null)
        {
            (host as IEndpointRouteBuilder)!.MapHub<SignalRStreamHub>(Defaults.DefaultStream);
        }
    }
}
