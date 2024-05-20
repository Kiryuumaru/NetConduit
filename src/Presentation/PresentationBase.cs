using Application;
using Application.Common;
using Application.Server;
using ApplicationBuilderHelpers;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using System.Reflection;

namespace Presentation;

internal class PresentationBase : ApplicationBase
{
    public override void AddConfiguration(ApplicationDependencyBuilder builder, IConfiguration configuration)
    {
        base.AddConfiguration(builder, configuration);

        builder.Builder.AddServiceDefaults();
        (configuration as ConfigurationManager)!.AddEnvironmentVariables();
    }

    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        base.AddServices(builder, services);

        services.AddMvc();
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Version = "v1",
                Title = "NetConduit API",
                Description = "Just another edge tunnel solution",
                Contact = new OpenApiContact
                {
                    Name = "Kiryuumaru",
                    Url = new Uri("https://github.com/Kiryuumaru")
                }
            });

            var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));
        });
    }

    public override void AddMiddlewares(ApplicationDependencyBuilder builder, IHost host)
    {
        base.AddMiddlewares(builder, host);

        if ((host as WebApplication)!.Environment.IsDevelopment())
        {
            (host as IApplicationBuilder)!.UseSwagger();
            (host as IApplicationBuilder)!.UseSwaggerUI();
        }

        (host as IApplicationBuilder)!.UseHttpsRedirection();
    }

    public override void AddMappings(ApplicationDependencyBuilder builder, IHost host)
    {
        base.AddMappings(builder, host);

        (host as WebApplication)!.MapDefaultEndpoints();
        (host as WebApplication)!.UseHttpsRedirection();
        (host as WebApplication)!.UseAuthorization();
        (host as WebApplication)!.MapControllers();
    }
}
