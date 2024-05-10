using Application.StreamLine.Common;
using Domain.Edge.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.StreamLine.Services;

public class StreamLineService(ILogger<StreamLineService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<StreamLineService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ConcurrentDictionary<int, OutgoingStreamLine> _outgoingStreams = [];
    private readonly ConcurrentDictionary<int, IncomingStreamLine> _incomingStreams = [];
    private readonly SemaphoreSlim _locker = new(1);
    private EdgeRoutingTable? currentEdgeRoutingTable;

    public async void UpdateRoutingTable(EdgeRoutingTable edgeRoutingTable)
    {
        try
        {
            await _locker.WaitAsync();
            if (currentEdgeRoutingTable != null)
            {
                foreach (var localRoute in currentEdgeRoutingTable.Table.Values)
                {
                    var upstreamRoute = edgeRoutingTable.Table.GetValueOrDefault(localRoute.Id);
                    if (upstreamRoute == null)
                    {
                        if (edgeRoutingTable.Id.Equals(localRoute.FromEdgeId))
                        {
                            RemoveOutgoingLine(localRoute.FromEdgePort);
                        }
                        else if (edgeRoutingTable.Id.Equals(localRoute.ToEdgeId))
                        {
                            RemoveIncomingLine(localRoute.ToEdgePort);
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
                    if (edgeRoutingTable.Id.Equals(localRoute.FromEdgeId))
                    {
                        if (upstreamRoute.FromEdgeId != localRoute.FromEdgeId ||
                            upstreamRoute.FromEdgePort != localRoute.FromEdgePort)
                        {
                            RemoveOutgoingLine(localRoute.FromEdgePort);
                            AddOutgoingLine(upstreamRoute.FromEdgePort);
                        }
                    }
                    else if (edgeRoutingTable.Id.Equals(localRoute.ToEdgeId))
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
            _locker.Release();
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
