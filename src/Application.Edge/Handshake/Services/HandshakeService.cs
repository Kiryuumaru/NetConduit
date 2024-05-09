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

namespace Application.Edge.Handshake.Services;

public class HandshakeService(ILogger<HandshakeService> logger, IServiceProvider serviceProvider, StreamLineService streamLineService)
{
    private readonly ILogger<HandshakeService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly StreamLineService _streamLineService = streamLineService;
    private readonly SemaphoreSlim _subscriptionLocker = new(1);
    private EdgeRoutingTable? currentEdgeRoutingTable;

    public async void OnHandshakeChanges(EdgeRoutingTable edgeRoutingTable)
    {
        try
        {
            await _subscriptionLocker.WaitAsync();
            if (currentEdgeRoutingTable != null)
            {
                foreach (var localRoute in currentEdgeRoutingTable.Table.Values)
                {
                    var upstreamRoute = edgeRoutingTable.Table.GetValueOrDefault(localRoute.Id);
                    if (upstreamRoute == null)
                    {
                        if (edgeRoutingTable.Id.Equals(localRoute.FromEdgeId))
                        {
                            AddOutgoingLine(localRoute.FromEdgePort);
                        }
                        else if (edgeRoutingTable.Id.Equals(localRoute.ToEdgeId))
                        {
                            AddIncomingLine(localRoute.ToEdgePort);
                        }
                    }
                }
            }
            foreach (var upstreamRoute in edgeRoutingTable.Table.Values)
            {
                var localRoute = currentEdgeRoutingTable?.Table?.GetValueOrDefault(upstreamRoute.Id);
                if (localRoute == null)
                {
                    if (edgeRoutingTable.Id.Equals(upstreamRoute.FromEdgeId))
                    {
                        AddOutgoingLine(upstreamRoute.FromEdgePort);
                    }
                    else if (edgeRoutingTable.Id.Equals(upstreamRoute.ToEdgeId))
                    {
                        AddIncomingLine(upstreamRoute.ToEdgePort);
                    }
                }
                else
                {
                    if (edgeRoutingTable.Id.Equals(upstreamRoute.FromEdgeId))
                    {
                        if (upstreamRoute.FromEdgeId != localRoute.FromEdgeId ||
                            upstreamRoute.FromEdgePort != localRoute.FromEdgePort)
                        {
                            RemoveOutgoingLine(localRoute.FromEdgePort);
                            AddOutgoingLine(upstreamRoute.FromEdgePort);
                        }
                    }
                    else if (edgeRoutingTable.Id.Equals(upstreamRoute.ToEdgeId))
                    {
                        if (upstreamRoute.ToEdgeId != localRoute.ToEdgeId ||
                            upstreamRoute.ToEdgePort != localRoute.ToEdgePort)
                        {
                            RemoveIncomingLine(localRoute.ToEdgePort);
                            AddIncomingLine(upstreamRoute.ToEdgePort);
                        }
                    }
                }
            }
            currentEdgeRoutingTable = edgeRoutingTable;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error on handshake changes: {}", ex.Message);
        }
        finally
        {
            _subscriptionLocker.Release();
        }
    }

    private async void RemoveOutgoingLine(int port)
    {
        _logger.LogInformation("REMOVE Outgoing: {}", port);
    }

    private async void AddOutgoingLine(int port)
    {
        _logger.LogInformation("ADD Outgoing: {}", port);
    }

    private async void RemoveIncomingLine(int port)
    {
        _logger.LogInformation("REMOVE Incoming: {}", port);
    }

    private async void AddIncomingLine(int port)
    {
        _logger.LogInformation("ADD Incoming: {}", port);
    }
}
