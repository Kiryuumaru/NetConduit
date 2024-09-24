using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ApplicationBuilderHelpers;
using TestTCPMocker;

var appBuilder = ApplicationHost.FromBuilder(Host.CreateApplicationBuilder(args))
    .Add<TestTCPMockerApplication>()
    .Build()
    .Run();
