var builder = DistributedApplication.CreateBuilder(args);

var server = builder.AddProject<Projects.Presentation>("presentation-server")
    .WithArgs("run -s");

builder.AddProject<Projects.Presentation>("presentation-edge1")
    .WithReference(server)
    .WithArgs("run -s")
    .WithEnvironment("NET_CONDUIT_SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("NET_CONDUIT_HANDSHAKE_TOKEN", "eyJ0b2tlbiI6IkU2cTE4RTdiYXI3RWlQajlkYmZ1Nmx1YWFxYWNOa1dTdEpVRDFRT0RDRGw5QlcxYnBUIiwiaWQiOiJrODFUdXV1OE9VV2l0LXpzV01DeGp3IiwibmFtZSI6IkNMWU5ULVBDIn0=");

builder.AddProject<Projects.Presentation>("presentation-edge2")
    .WithReference(server)
    .WithArgs("run -s")
    .WithEnvironment("NET_CONDUIT_SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("NET_CONDUIT_HANDSHAKE_TOKEN", "eyJ0b2tlbiI6InQzc0NQQUZBUnF5V0FDMHplQWE2azFpTktNQVF3eU1idkdYT05tWlF6OWVzeHdQb3NlIiwiaWQiOiJub3ROc0JEekFrMk9MNjBSM2tFVHdnIiwibmFtZSI6IkxFQS1QQyJ9");

builder.AddProject<Projects.Presentation>("presentation-edge3")
    .WithReference(server)
    .WithArgs("run -s")
    .WithEnvironment("NET_CONDUIT_SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
    .WithEnvironment("NET_CONDUIT_HANDSHAKE_TOKEN", "eyJ0b2tlbiI6InBWUWVPaWFOWWZrblZoMnJ2OG13OFRsWnZVQzBkaDAxNGhmeXNyTjM1NWN4SEdCOGtEIiwiaWQiOiJZZjBCLU9EMmpFbW1JeUNTeFRWczB3IiwibmFtZSI6IkxBTklBS0VBLVBDIn0=");

builder.AddProject<Projects.TestTCPMocker>("testtcpmocker-server1")
    .WithEnvironment("TCP_MOCKER_SERVER_MODE", "yes");

builder.AddProject<Projects.TestTCPMocker>("testtcpmocker-client1")
    .WithEnvironment("TCP_MOCKER_SERVER_MODE", "no");

builder.AddProject<Projects.TestTCPMocker>("testtcpmocker-client2")
    .WithEnvironment("TCP_MOCKER_SERVER_MODE", "no");

builder.Build().Run();
