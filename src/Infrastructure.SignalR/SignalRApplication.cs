using Application.Common;
using ApplicationBuilderHelpers;
using Infrastructure.SignalR.Client.Edge.Applet.Workers;
using Infrastructure.SignalR.Common;
using Infrastructure.SignalR.Edge.Connection.Services;
using Infrastructure.SignalR.Edge.Connection.Workers;
using Infrastructure.SignalR.Server;
using Infrastructure.SignalR.Server.Handshake.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.SignalR.Edge;

public class SignalRApplication : ApplicationDependency
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        if (builder.Configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
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

    public override void AddMiddlewares(ApplicationDependencyBuilder builder, IHost host)
    {
        if (!builder.Configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            (host as IApplicationBuilder)!.UseResponseCompression();
        }
    }

    public override void AddMappings(ApplicationDependencyBuilder builder, IHost host)
    {
        if (!builder.Configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            (host as IEndpointRouteBuilder)!.MapHub<SignalRStreamHub>(Defaults.DefaultStream);
        }
    }
}
