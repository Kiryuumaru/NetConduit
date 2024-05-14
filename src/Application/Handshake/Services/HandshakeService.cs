using Application.StreamLine.Services;
using DisposableHelpers;
using Domain.Edge.Models;
using Domain.PortRoute.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.Handshake.Services;

public class HandshakeService(ILogger<HandshakeService> logger, IServiceProvider serviceProvider, StreamLineService streamLineService)
{
    private readonly ILogger<HandshakeService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly StreamLineService _streamLineService = streamLineService;
    private readonly SemaphoreSlim _subscriptionLocker = new(1);

    public void OnHandshakeChanges(EdgeRoutingTable edgeRoutingTable)
    {
        _streamLineService.UpdateRoutingTable(edgeRoutingTable);
    }
}
