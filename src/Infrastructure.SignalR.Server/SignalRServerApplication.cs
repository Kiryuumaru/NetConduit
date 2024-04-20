using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Application.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ApplicationBuilderHelpers;

namespace Infrastructure.SignalR.Server;

public class SignalRServerApplication : ApplicationDependency
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        services.AddSignalR(hubOptions =>
        {
            hubOptions.EnableDetailedErrors = true;
            hubOptions.KeepAliveInterval = TimeSpan.FromSeconds(30);
        });
        services.AddResponseCompression(opts =>
        {
            opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/octet-stream"]);
        });
    }

    public override void AddMiddlewares(ApplicationDependencyBuilder builder, IHost host)
    {
        (host as IApplicationBuilder)!.UseResponseCompression();
    }

    public override void AddMappings(ApplicationDependencyBuilder builder, IHost host)
    {
        (host as IEndpointRouteBuilder)!.MapHub<SignalRStreamHub>(Defaults.DefaultStream);
    }
}
