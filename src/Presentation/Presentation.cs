using Application;
using ApplicationBuilderHelpers;
using Microsoft.OpenApi.Models;
using Presentation.Components;
using Presentation.Services;
using Microsoft.Extensions.Options;
using System.Reflection;
using Application.Configuration.Extensions;
using Presentation.Aspire.ServiceDefaults;

namespace Presentation;

internal class Presentation : Application.Application
{
    public override void AddConfiguration(ApplicationHostBuilder applicationBuilder, IConfiguration configuration)
    {
        base.AddConfiguration(applicationBuilder, configuration);

        applicationBuilder.Builder.AddServiceDefaults();

        (applicationBuilder.Builder as WebApplicationBuilder)!.WebHost.UseUrls(configuration.GetApiUrls());

        (configuration as ConfigurationManager)!.AddEnvironmentVariables();
    }

    public override void AddServices(ApplicationHostBuilder applicationBuilder, IServiceCollection services)
    {
        base.AddServices(applicationBuilder, services);

        services.AddScoped<ClientManager>();

        services.AddHttpClient(Options.DefaultName, client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", ApplicationDefaults.AppNamePascalCase);
        });

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

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

    public override void AddMiddlewares(ApplicationHost applicationHost, IHost host)
    {
        base.AddMiddlewares(applicationHost, host);

        //if ((host as WebApplication)!.Environment.IsDevelopment())
        //{
        //}
        (host as IApplicationBuilder)!.UseSwagger();
        (host as IApplicationBuilder)!.UseSwaggerUI();
    }

    public override void AddMappings(ApplicationHost applicationHost, IHost host)
    {
        base.AddMappings(applicationHost, host);

        (host as WebApplication)!.MapDefaultEndpoints();
        (host as WebApplication)!.UseAuthorization();
        (host as WebApplication)!.MapControllers();

        (host as WebApplication)!.UseStaticFiles();
        (host as WebApplication)!.UseAntiforgery();
        (host as WebApplication)!.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
    }
}
