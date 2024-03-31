var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.NetConduit_Client>("netconduit.client");

builder.AddProject<Projects.NetConduit_Server>("netconduit.server");

builder.Build().Run();
