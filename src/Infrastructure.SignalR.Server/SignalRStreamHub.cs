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

namespace Infrastructure.SignalR.Server;

public class SignalRStreamHub(ILogger<SignalRStreamHub> logger, IServiceProvider serviceProvider) : Hub
{
    protected readonly ILogger<SignalRStreamHub> _logger = logger;
    protected readonly IServiceProvider ServiceProvider = serviceProvider;

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
