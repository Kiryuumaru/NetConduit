using Application.Edge.StreamLine.Workers;
using ApplicationBuilderHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace Application.Edge;

public class ApplicationEdge : Application
{
    public override void AddServices(ApplicationDependencyBuilder builder, IServiceCollection services)
    {
        base.AddServices(builder, services);

    }
}
