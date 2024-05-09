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

//builder.AddProject<Projects.Presentation_Edge>("presentation-edge3")
//    .WithReference(server)
//    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
//    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAiaWQiOiAiZFBfWC1CU0VjMDZEa3M1VGlBWEhrZyIsDQogICJuYW1lIjogIkxlYSBQQyIsDQogICJ0b2tlbiI6ICJuZ1IxS0dLdGhDWE00N05DMHcwaDc0MGRVVnlNR1Rxc1VPa0daMDJSNGNHcVBlQ2ZMdiINCn0=");

builder.Build().Run();
