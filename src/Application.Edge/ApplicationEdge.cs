using Application.Edge.StreamLine.Workers;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;
using Application.Edge.Handshake.Services;

namespace Application.Edge;

public class ApplicationEdge : Application
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        base.AddServices(builder, services);

        services.AddSingleton<HandshakeService>();
    }
}
