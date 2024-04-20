using Application.Server;
using ApplicationBuilderHelpers;

namespace Presentation.Server;

internal class PresentationServer : ApplicationServer
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

        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

    }

    public override void AddMiddlewares(ApplicationDependencyBuilder builder, IHost host)
    {
        base.AddMiddlewares(builder, host);

        if ((host as WebApplication)!.Environment.IsDevelopment())
        {
            (host as IApplicationBuilder)!.UseSwagger();
            (host as IApplicationBuilder)!.UseSwaggerUI();
        }
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
