using Application.StreamLine.Common;
using DisposableHelpers;
using Domain.Edge.Models;
using Domain.PortRoute.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.StreamLine.Services;

public class StreamLineService(ILogger<StreamLineService> logger, IServiceProvider serviceProvider)
{
    private record StreamLineHolder(BaseStreamLine StreamLine, Action<EdgeRoutingTable> Validator);

    private readonly ILogger<StreamLineService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly Dictionary<string, StreamLineHolder> _streamLines = [];
    private readonly SemaphoreSlim _locker = new(1);

    public async void UpdateRoutingTable(EdgeRoutingTable edgeRoutingTable)
    {
        try
        {
            await _locker.WaitAsync();
            foreach (var streamLine in _streamLines.Values.ToArray())
            {
                streamLine.Validator.Invoke(edgeRoutingTable);
            }
            foreach (var route in edgeRoutingTable.Table.Values)
            {
                if (edgeRoutingTable.Id.Equals(route.FromEdgeId))
                {
                    UpdateOutgoingLine(edgeRoutingTable, route);
                }
                else if (edgeRoutingTable.Id.Equals(route.ToEdgeId))
                {
                    UpdateIncomingLine(edgeRoutingTable, route);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error on updating route table: {}", ex.Message);
        }
        finally
        {
            _locker.Release();
        }
    }

    private void UpdateOutgoingLine(EdgeRoutingTable edgeRoutingTable, PortRouteEntity portRouteEntity)
    {
        StreamLineHolder? streamLineHolder = _streamLines.GetValueOrDefault(portRouteEntity.Id);
        if (streamLineHolder != null && streamLineHolder.StreamLine is not OutgoingStreamLine)
        {
            _streamLines.Remove(portRouteEntity.Id, out _);
            streamLineHolder.StreamLine.Dispose();
            streamLineHolder = null;
        }
        if (streamLineHolder == null)
        {
            OutgoingStreamLine? outgoingStreamLine = new(portRouteEntity);
            Action<EdgeRoutingTable> validator = validatorEdgeRoutingTable => { };
            void add(EdgeRoutingTable ert)
            {
                _streamLines.Add(outgoingStreamLine.Route.Id, new(outgoingStreamLine, validator));
                _logger.LogInformation("ADD Outgoing: ({}, {})", ert.Edges[outgoingStreamLine.Route.ToEdgeId].Name, outgoingStreamLine.Route.FromEdgePort);
            }
            void remove(EdgeRoutingTable ert)
            {
                _streamLines.Remove(outgoingStreamLine.Route.Id, out _);
                outgoingStreamLine.Dispose();
                _logger.LogInformation("REMOVE Outgoing: ({}, {})", ert.Edges[outgoingStreamLine.Route.ToEdgeId].Name, outgoingStreamLine.Route.FromEdgePort);
            }
            validator = validatorEdgeRoutingTable =>
            {
                if (!validatorEdgeRoutingTable.Table.TryGetValue(outgoingStreamLine.Route.Id, out var latestPortRouteEntity))
                {
                    remove(validatorEdgeRoutingTable);
                }
                else if (!latestPortRouteEntity.FromEdgeId.Equals(outgoingStreamLine.Route.FromEdgeId) ||
                    !latestPortRouteEntity.ToEdgeId.Equals(outgoingStreamLine.Route.ToEdgeId) ||
                    latestPortRouteEntity.FromEdgePort != outgoingStreamLine.Route.ToEdgePort ||
                    latestPortRouteEntity.ToEdgePort != outgoingStreamLine.Route.ToEdgePort)
                {
                    remove(validatorEdgeRoutingTable);
                    outgoingStreamLine = new(latestPortRouteEntity);
                    add(validatorEdgeRoutingTable);
                }
            };
            add(edgeRoutingTable);
        }
    }

    private void UpdateIncomingLine(EdgeRoutingTable edgeRoutingTable, PortRouteEntity portRouteEntity)
    {
        StreamLineHolder? streamLineHolder = _streamLines.GetValueOrDefault(portRouteEntity.Id);
        if (streamLineHolder != null && streamLineHolder.StreamLine is not IncomingStreamLine)
        {
            _streamLines.Remove(portRouteEntity.Id, out _);
            streamLineHolder.StreamLine.Dispose();
            streamLineHolder = null;
        }
        if (streamLineHolder == null)
        {
            IncomingStreamLine? incomingStreamLine = new(portRouteEntity);
            Action<EdgeRoutingTable> validator = validatorEdgeRoutingTable => { };
            void add(EdgeRoutingTable ert)
            {
                _streamLines.Add(incomingStreamLine.Route.Id, new(incomingStreamLine, validator));
                _logger.LogInformation("ADD Incoming: ({}, {})", ert.Edges[incomingStreamLine.Route.FromEdgeId].Name, incomingStreamLine.Route.ToEdgePort);
            }
            void remove(EdgeRoutingTable ert)
            {
                _streamLines.Remove(incomingStreamLine.Route.Id, out _);
                incomingStreamLine.Dispose();
                _logger.LogInformation("REMOVE Incoming: ({}, {})", ert.Edges[incomingStreamLine.Route.FromEdgeId].Name, incomingStreamLine.Route.ToEdgePort);
            }
            validator = validatorEdgeRoutingTable =>
            {
                if (!validatorEdgeRoutingTable.Table.TryGetValue(incomingStreamLine.Route.Id, out var latestPortRouteEntity))
                {
                    remove(validatorEdgeRoutingTable);
                }
                else if (!latestPortRouteEntity.FromEdgeId.Equals(incomingStreamLine.Route.FromEdgeId) ||
                    !latestPortRouteEntity.ToEdgeId.Equals(incomingStreamLine.Route.ToEdgeId) ||
                    latestPortRouteEntity.FromEdgePort != incomingStreamLine.Route.ToEdgePort ||
                    latestPortRouteEntity.ToEdgePort != incomingStreamLine.Route.ToEdgePort)
                {
                    remove(validatorEdgeRoutingTable);
                    incomingStreamLine = new(latestPortRouteEntity);
                    add(validatorEdgeRoutingTable);
                }
            };
            add(edgeRoutingTable);
        }
    }
}
