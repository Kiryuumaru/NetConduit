var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Presentation>("presentation-server1")
    .WithArgs("server", "start")
    .WithArgs("-l", "trace")
    .WithArgs("--api-urls", "http://*:21110")
    .WithArgs("--server-host", "localhost")
    .WithArgs("--server-port", "21000")
    .WithArgs("--home", "C:\\ProgramData\\netc\\server0");

for (int i = 0; i < 3; i++)
{
    builder.AddProject<Projects.Presentation>($"presentation-client{i}")
        .WithArgs("client", "start")
        .WithArgs("-l", "trace")
        .WithArgs("--api-urls", $"http://*:{31110 + i}")
        .WithArgs("--server-host", "localhost")
        .WithArgs("--server-port", "21000")
        .WithArgs("--home", $"C:\\ProgramData\\netc\\client{i}");
}

//builder.AddProject<Projects.TestTCPMocker>("relayapi1-serverapi1")
//    .WithEnvironment("TCP_MOCKER_RELAY_TO_MOQ", "23457:localhost:23456");

//builder.AddProject<Projects.Presentation>("presentation-edge1")
//    .WithReference(server)
//    .WithArgs("run -s")
//    .WithEnvironment("NET_CONDUIT_SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
//    .WithEnvironment("NET_CONDUIT_HANDSHAKE_TOKEN", "eyJ0b2tlbiI6IkU2cTE4RTdiYXI3RWlQajlkYmZ1Nmx1YWFxYWNOa1dTdEpVRDFRT0RDRGw5QlcxYnBUIiwiaWQiOiJrODFUdXV1OE9VV2l0LXpzV01DeGp3IiwibmFtZSI6IkNMWU5ULVBDIn0=");

//builder.AddProject<Projects.Presentation>("presentation-edge2")
//    .WithReference(server)
//    .WithArgs("run -s")
//    .WithEnvironment("NET_CONDUIT_SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
//    .WithEnvironment("NET_CONDUIT_HANDSHAKE_TOKEN", "eyJ0b2tlbiI6InQzc0NQQUZBUnF5V0FDMHplQWE2azFpTktNQVF3eU1idkdYT05tWlF6OWVzeHdQb3NlIiwiaWQiOiJub3ROc0JEekFrMk9MNjBSM2tFVHdnIiwibmFtZSI6IkxFQS1QQyJ9");

//builder.AddProject<Projects.Presentation>("presentation-edge3")
//    .WithReference(server)
//    .WithArgs("run -s")
//    .WithEnvironment("NET_CONDUIT_SERVER_ENDPOINT", "@ref:services:presentation-server:https:0")
//    .WithEnvironment("NET_CONDUIT_HANDSHAKE_TOKEN", "eyJ0b2tlbiI6InBWUWVPaWFOWWZrblZoMnJ2OG13OFRsWnZVQzBkaDAxNGhmeXNyTjM1NWN4SEdCOGtEIiwiaWQiOiJZZjBCLU9EMmpFbW1JeUNTeFRWczB3IiwibmFtZSI6IkxBTklBS0VBLVBDIn0=");

//builder.AddProject<Projects.TestTCPMocker>("server1")
//    .WithEnvironment("TCP_MOCKER_SERVER_TO_MOQ", "20000");

//builder.AddProject<Projects.TestTCPMocker>("relay1-server1")
//    .WithEnvironment("TCP_MOCKER_RELAY_TO_MOQ", "20001:localhost:20000");

//builder.AddProject<Projects.TestTCPMocker>("relay2-server1")
//    .WithEnvironment("TCP_MOCKER_RELAY_TO_MOQ", "20002:localhost:20000");

//builder.AddProject<Projects.TestTCPMocker>("relay3-relay2")
//    .WithEnvironment("TCP_MOCKER_RELAY_TO_MOQ", "20003:localhost:20002");

//builder.AddProject<Projects.TestTCPMocker>("client1-server1")
//    .WithEnvironment("TCP_MOCKER_CLIENT_TO_MOQ", "localhost:20000");

//builder.AddProject<Projects.TestTCPMocker>("client2-server1")
//    .WithEnvironment("TCP_MOCKER_CLIENT_TO_MOQ", "localhost:20000");

//builder.AddProject<Projects.TestTCPMocker>("client3-relay1")
//    .WithEnvironment("TCP_MOCKER_CLIENT_TO_MOQ", "localhost:20001");

//builder.AddProject<Projects.TestTCPMocker>("client4-relay2")
//    .WithEnvironment("TCP_MOCKER_CLIENT_TO_MOQ", "localhost:20002");

//builder.AddProject<Projects.TestTCPMocker>("client5-relay3")
//    .WithEnvironment("TCP_MOCKER_CLIENT_TO_MOQ", "localhost:20003");

builder.Build().Run();
