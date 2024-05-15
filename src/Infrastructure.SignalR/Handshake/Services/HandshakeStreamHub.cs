using Application.Common;
using Application.Edge.Common;
using Application.Server.Edge.Services;
using Application.Server.PortRoute.Services;
using Domain.Edge.Entities;
using Domain.Edge.Models;
using Domain.PortRoute.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Infrastructure.SignalR.Server.Handshake.Services;

public class HandshakeStreamHub(
    ILogger<HandshakeStreamHub> logger,
    IServiceProvider serviceProvider,
    HandshakeLockerService handshakeLockerService)
{
    private readonly ILogger<HandshakeStreamHub> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly HandshakeLockerService _handshakeLockerService = handshakeLockerService;

    internal async void Routine(SignalRStreamHub hub, string handshakeToken, Channel<EdgeRoutingTable> channel)
    {
        var portRouteEventHubService = _serviceProvider.GetRequiredService<PortRouteEventHubService>();
        var edgeService = _serviceProvider.GetRequiredService<EdgeService>();
        var portRouteService = _serviceProvider.GetRequiredService<PortRouteService>();

        try
        {
            EdgeConnectionEntity edgeEntity;

            try
            {
                var fromPayloadEdgeEntity = EdgeEntityHelpers.Decode(handshakeToken);
                edgeEntity = (await edgeService.Get(fromPayloadEdgeEntity.Id, hub.Context.ConnectionAborted)).GetValueOrThrow();
            }
            catch
            {
                _logger.LogInformation("Handshake attempt error: handshake token: {}", handshakeToken);
                channel.Writer.TryComplete(new Exception("Invalid handshake token"));
                return;
            }

            try
            {
                _handshakeLockerService.Lock(edgeEntity);
            }
            catch
            {
                _logger.LogInformation("Handshake attempt error: Edge already locked: {}", handshakeToken);
                channel.Writer.TryComplete(new Exception("Edge already locked"));
                return;
            }

            _logger.LogInformation("New handshake stream ({}, {})", edgeEntity.Name, edgeEntity.Id);

            async Task Send()
            {
                try
                {
                    var sourceEdgePortRoute = (await portRouteService.GetAll(sourceEdgeId: edgeEntity.Id, cancellationToken: hub.Context.ConnectionAborted)).GetValueOrThrow();
                    var destinationEdgePortRoute = (await portRouteService.GetAll(destinationEdgeId: edgeEntity.Id, cancellationToken: hub.Context.ConnectionAborted)).GetValueOrThrow();

                    Dictionary<string, PortRouteEntity> table = [];
                    Dictionary<string, EdgeEntity> edges = [];
                    foreach (var route in sourceEdgePortRoute)
                    {
                        table.Add(route.Id, route);
                        if (!edges.ContainsKey(route.SourceEdgeId))
                        {
                            edges.Add(route.SourceEdgeId, (await edgeService.Get(route.SourceEdgeId)).GetValueOrThrow());
                        }
                        if (!edges.ContainsKey(route.DestinationEdgeId))
                        {
                            edges.Add(route.DestinationEdgeId, (await edgeService.Get(route.DestinationEdgeId)).GetValueOrThrow());
                        }
                    }
                    foreach (var route in destinationEdgePortRoute)
                    {
                        table.Add(route.Id, route);
                        if (!edges.ContainsKey(route.SourceEdgeId))
                        {
                            edges.Add(route.SourceEdgeId, (await edgeService.Get(route.SourceEdgeId)).GetValueOrThrow());
                        }
                        if (!edges.ContainsKey(route.DestinationEdgeId))
                        {
                            edges.Add(route.DestinationEdgeId, (await edgeService.Get(route.DestinationEdgeId)).GetValueOrThrow());
                        }
                    }

                    EdgeRoutingTable edgeRoutingTable = new()
                    {
                        Id = edgeEntity.Id,
                        Name = edgeEntity.Name,
                        Table = table,
                        Edges = edges
                    };

                    await channel.Writer.WriteAsync(edgeRoutingTable, hub.Context.ConnectionAborted.WithTimeout(TimeSpan.FromSeconds(10)));
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error ({}, {}): {}", edgeEntity.Name, edgeEntity.Id, ex.Message);
                }
            }

            await Send();

            var subscription = portRouteEventHubService.SubscribeEvent(edgeEntity.Id, async () =>
            {
                await Send();
            });

            await hub.Context.ConnectionAborted.WhenCanceled();

            subscription.Dispose();

            _handshakeLockerService.Unlock(edgeEntity);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Routine stopped: {}", ex.Message);
        }
    }
}
