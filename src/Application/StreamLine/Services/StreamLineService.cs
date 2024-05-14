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
                if (edgeRoutingTable.Id.Equals(route.SourceEdgeId))
                {
                    UpdateLine<OutgoingStreamLine>(edgeRoutingTable, route, pre => new(pre), _ => true);
                }
                else if (edgeRoutingTable.Id.Equals(route.DestinationEdgeId))
                {
                    UpdateLine<IncomingStreamLine>(edgeRoutingTable, route, pre => new(pre), _ => false);
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

    private void UpdateLine<TStreamLine>(
        EdgeRoutingTable edgeRoutingTable,
        PortRouteEntity portRouteEntity,
        Func<PortRouteEntity, TStreamLine> streamLineFactory,
        Func<TStreamLine, bool> isOutgoingCallback)
        where TStreamLine : BaseStreamLine
    {
        StreamLineHolder? streamLineHolder = _streamLines.GetValueOrDefault(portRouteEntity.Id);
        if (streamLineHolder != null && streamLineHolder.StreamLine is not TStreamLine)
        {
            _streamLines.Remove(portRouteEntity.Id, out _);
            streamLineHolder.StreamLine.Dispose();
            streamLineHolder = null;
        }
        if (streamLineHolder == null)
        {
            TStreamLine? streamLine = streamLineFactory(portRouteEntity);
            Action<EdgeRoutingTable> validator = validatorEdgeRoutingTable => { };
            void add(EdgeRoutingTable ert)
            {
                _streamLines.Add(streamLine.Route.Id, new(streamLine, validator));
                if (isOutgoingCallback(streamLine))
                {
                    _logger.LogInformation("ADD Outgoing: ({}, {})", ert.Edges[streamLine.Route.DestinationEdgeId].Name, streamLine.Route.SourceEdgePort);
                }
                else
                {
                    _logger.LogInformation("ADD Incoming: ({}, {})", ert.Edges[streamLine.Route.SourceEdgeId].Name, streamLine.Route.DestinationEdgePort);
                }
            }
            void remove(EdgeRoutingTable ert)
            {
                _streamLines.Remove(streamLine.Route.Id, out _);
                streamLine.Dispose();
                if (isOutgoingCallback(streamLine))
                {
                    _logger.LogInformation("REMOVE Outgoing: ({}, {})", ert.Edges[streamLine.Route.DestinationEdgeId].Name, streamLine.Route.SourceEdgePort);
                }
                else
                {
                    _logger.LogInformation("REMOVE Incoming: ({}, {})", ert.Edges[streamLine.Route.SourceEdgeId].Name, streamLine.Route.DestinationEdgePort);
                }
            }
            validator = validatorEdgeRoutingTable =>
            {
                if (!validatorEdgeRoutingTable.Table.TryGetValue(streamLine.Route.Id, out var latestPortRouteEntity))
                {
                    remove(validatorEdgeRoutingTable);
                }
                else if (latestPortRouteEntity.SourceEdgeId.Equals(streamLine.Route.SourceEdgeId) && latestPortRouteEntity.DestinationEdgeId.Equals(streamLine.Route.DestinationEdgeId))
                {
                    if (latestPortRouteEntity.SourceEdgePort != streamLine.Route.SourceEdgePort ||
                        latestPortRouteEntity.DestinationEdgePort != streamLine.Route.DestinationEdgePort)
                    {
                        remove(validatorEdgeRoutingTable);
                        streamLine = streamLineFactory(latestPortRouteEntity);
                        add(validatorEdgeRoutingTable);
                    }
                }
                else
                {
                    remove(validatorEdgeRoutingTable);
                }
            };
            add(edgeRoutingTable);
        }
    }
}
