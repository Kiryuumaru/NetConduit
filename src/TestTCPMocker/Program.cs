using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApplicationBuilderHelpers;
using TestTCPMocker;

await ApplicationHost.FromBuilder(Host.CreateApplicationBuilder(args))
    .Add<TestTCPMockerApplication>()
    .Build()
    .Run();
