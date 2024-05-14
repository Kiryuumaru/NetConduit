var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.Presentation>("presentation-server");

builder.AddProject<Projects.Presentation>("presentation-edge1")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAidG9rZW4iOiAiTXdQM200T21yOXd5d2c3ZTRlSDZOQnNjcTZvcG9KVEJRUmlJZkFITFJYWHJESEIwajgiLA0KICAiaWQiOiAiNmtZcTF2NHdnRUsxRkpiTkUzY1BfZyIsDQogICJuYW1lIjogIkNMWU5ULVBDIg0KfQ==");

builder.AddProject<Projects.Presentation>("presentation-edge2")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAidG9rZW4iOiAiMHhxeFM0aTB2bFg0S3ZlMGNpbk94akZGRHFZM2dXZzRIeFoySU14Z3lXaXdYRk9KdlgiLA0KICAiaWQiOiAiUHdEWmhNekNsMHE3UmkzSGtFaTcydyIsDQogICJuYW1lIjogIkxFQS1QQyINCn0=");

builder.AddProject<Projects.Presentation>("presentation-edge3")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAidG9rZW4iOiAiRFhrZm9zWlVNNVB0SEZvSjlTZHhiZVQ1SGJNcTVGNHFSbU9LcUxUZ0w3d0FHR3ZIM2ciLA0KICAiaWQiOiAiUmFBQjJSWHNEay1MNS1mUWpDQ1JzdyIsDQogICJuYW1lIjogIlJFSUxFRU4tUEMiDQp9");

builder.Build().Run();
