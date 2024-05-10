var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.Presentation_Server>("presentation-server");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge1")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAiaWQiOiAiZmlfYVNpWXFZVVNQRXV2S29Kal9tZyIsDQogICJuYW1lIjogIkNMWU5ULVBDIiwNCiAgInRva2VuIjogIkZlVDZvRWhjOEpPb1RSZ28yWU5BSzFFTk55d1hQbDkybXVQdGVwclg3cjhOWmtobThIIg0KfQ==");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge2")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAiaWQiOiAiOUxJN2x2MDBkMFdzSUl5WS1TLURCQSIsDQogICJuYW1lIjogIkxFQS1QQyIsDQogICJ0b2tlbiI6ICI4aTdzQ01CQ3FpQVU3ZUdCbWo3VTAzOWg0cDg1UnVIckVzZEF0aTFYMGRSTGlSVUU5cyINCn0=");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge3")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAiaWQiOiAicVl3MkNiZ3ZvRTZBYmx5Z3BUWGFjUSIsDQogICJuYW1lIjogIlJFSUxFRU4tUEMiLA0KICAidG9rZW4iOiAiajV3SDlWR1p3T1V4WExXeUZVUDJyUmVIdmhRd2RRejdMeDkzS1dSdVhFakFZajNpdVEiDQp9");

builder.Build().Run();
