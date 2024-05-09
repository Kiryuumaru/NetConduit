var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.Presentation_Server>("presentation-server");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge1")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAiaWQiOiAiZDdwODV4YmJ0RTJSWmtEa1lUMUNhQSIsDQogICJuYW1lIjogIkNseW50IFBDIiwNCiAgInRva2VuIjogImVuVVVGYmRqbU9HOThGY1RmcmJDQ1RXSmpKR1pkVXVMU2xpSHBXTjJlTjlmZVFYUDJCIg0KfQ==");

builder.AddProject<Projects.Presentation_Edge>("presentation-edge2")
    .WithReference(server)
    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAiaWQiOiAiZFBfWC1CU0VjMDZEa3M1VGlBWEhrZyIsDQogICJuYW1lIjogIkxlYSBQQyIsDQogICJ0b2tlbiI6ICJuZ1IxS0dLdGhDWE00N05DMHcwaDc0MGRVVnlNR1Rxc1VPa0daMDJSNGNHcVBlQ2ZMdiINCn0=");

//builder.AddProject<Projects.Presentation_Edge>("presentation-edge3")
//    .WithReference(server)
//    .WithEnvironment("SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
//    .WithEnvironment("HANDSHAKE_TOKEN", "ew0KICAiaWQiOiAiZFBfWC1CU0VjMDZEa3M1VGlBWEhrZyIsDQogICJuYW1lIjogIkxlYSBQQyIsDQogICJ0b2tlbiI6ICJuZ1IxS0dLdGhDWE00N05DMHcwaDc0MGRVVnlNR1Rxc1VPa0daMDJSNGNHcVBlQ2ZMdiINCn0=");

builder.Build().Run();
