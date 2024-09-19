using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RestfulHelpers;
using RestfulHelpers.Common;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TransactionHelpers;
using Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using DisposableHelpers.Attributes;
using Domain.Edge.Models;
using Infrastructure.SignalR.Common;
using Infrastructure.SignalR.Handshake.Services;

namespace Infrastructure.SignalR.Server;

public class SignalRStreamHub(ILogger<SignalRStreamHub> logger, IServiceProvider serviceProvider) : Hub
{
    protected readonly ILogger<SignalRStreamHub> _logger = logger;
    protected readonly IServiceProvider ServiceProvider = serviceProvider;

    [HubMethodName(Defaults.HandshakeMethod)]
    public ChannelReader<EdgeRoutingTable> Handshake(string handshakeToken)
    {
        var channel = Channel.CreateUnbounded<EdgeRoutingTable>();

        var handshakeStreamHub = ServiceProvider.GetRequiredService<HandshakeStreamHub>();

        handshakeStreamHub.Routine(this, handshakeToken, channel);

        return channel;
    }

    [HubMethodName(Defaults.ListenMethod)]
    public ChannelReader<byte> Listen()
    {
        var channel = Channel.CreateUnbounded<byte>();



        return channel;
    }

    [HubMethodName(Defaults.SpeakMethod)]
    public async Task Speak(IAsyncEnumerable<byte> bytes)
    {
        await foreach (var b in bytes)
        {

        }
    }
}
