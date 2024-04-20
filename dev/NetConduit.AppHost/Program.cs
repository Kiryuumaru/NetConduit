var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.Presentation_Server>("presentation-server");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge1")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge2")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0");

builder.Build().Run();
