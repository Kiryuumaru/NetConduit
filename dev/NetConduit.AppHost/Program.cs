var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.Presentation_Server>("presentation-server");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge1")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAidG9rZW4iOiAiSDRlQUxZc1Q2dFppOHprUVRqajd3WU1VWUxKTWVJZU1sMlVkNTdvVFdwS3FnNTNSZFAiLA0KICAiaWQiOiAiTklHaWxhVnUyMEt1b2FQT05iMllXdyIsDQogICJuYW1lIjogIkNMWU5ULVBDIg0KfQ==");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge2")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAidG9rZW4iOiAib3A0TzZOY2VzdGZVUVp0TWE0a21zRkwyN3EzWmdNSG9HYVczc2hZTnQwa05zNmZ4MmYiLA0KICAiaWQiOiAibmduZWtybXVNRWVvcDREZjU3UFlmZyIsDQogICJuYW1lIjogIkxFQS1QQyINCn0=");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge3")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAidG9rZW4iOiAiNHloMGlvTlVPZ2taS2dBSmw3Q2F5RHk4WlU2QW12elhZalBQdm9VaHFlNnpVV1lHUXgiLA0KICAiaWQiOiAiWk1vcXM3d2hWMEtDbnlTRVdsN2RRdyIsDQogICJuYW1lIjogIlJFSUxFRU4tUEMiDQp9");

builder.Build().Run();
