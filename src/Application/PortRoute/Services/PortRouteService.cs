using Application.Common;
using Application.LocalStore.Services;
using Application.PortRoute.Interfaces;
using Application.Server.Edge.Services;
using Domain.PortRoute.Dtos;
using Domain.PortRoute.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulHelpers.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.Server.PortRoute.Services;

public class PortRouteService(ILogger<PortRouteService> logger, PortRouteStoreService portRouteStoreService, EdgeStoreService edgeStoreService, PortRouteEventHubService portRouteEventHubService) : IPortRouteService
{
    private readonly ILogger<PortRouteService> _logger = logger;
    private readonly PortRouteStoreService _portRouteStoreService = portRouteStoreService;
    private readonly EdgeStoreService _edgeStoreService = edgeStoreService;
    private readonly PortRouteEventHubService _portRouteEventHubService = portRouteEventHubService;

    public async Task<HttpResult<PortRouteEntity[]>> GetAll(string? sourceEdgeId = null, string? destinationEdgeId = null, CancellationToken cancellationToken = default)
    {
        HttpResult<PortRouteEntity[]> result = new();

        var store = _portRouteStoreService.GetStore();
        var edgeStore = _edgeStoreService.GetStore();

        if (!string.IsNullOrEmpty(sourceEdgeId) &&
            (!result.Success(await edgeStore.Contains(sourceEdgeId, cancellationToken: cancellationToken), out bool containsSourceEdgeId) || !containsSourceEdgeId))
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("SOURCE_EDGE_ID_NOT_FOUND", "Port route sourceEdgeId does not exists");
            return result;
        }

        if (!string.IsNullOrEmpty(destinationEdgeId) &&
            (!result.Success(await edgeStore.Contains(destinationEdgeId, cancellationToken: cancellationToken), out bool containsDestinationEdgeId) || !containsDestinationEdgeId))
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("DESTINATION_EDGE_ID_NOT_FOUND", "Port route destinationEdgeId does not exists");
            return result;
        }

        if (!result.SuccessAndHasValue(await store.GetIds(cancellationToken: cancellationToken), out string[]? portRouteIds))
        {
            _logger.LogError("Error port route GetAll: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        List<PortRouteEntity> portRouteEntities = [];

        foreach (var id in portRouteIds)
        {
            if (!result.SuccessAndHasValue(await store.Get<PortRouteEntity>(id, cancellationToken: cancellationToken), out PortRouteEntity? portRouteEntity))
            {
                _logger.LogError("Error port route GetAll: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }
            if ((string.IsNullOrEmpty(sourceEdgeId) && string.IsNullOrEmpty(destinationEdgeId)) ||
                (!string.IsNullOrEmpty(sourceEdgeId) && sourceEdgeId.Equals(portRouteEntity.SourceEdgeId)) ||
                (!string.IsNullOrEmpty(destinationEdgeId) && destinationEdgeId.Equals(portRouteEntity.DestinationEdgeId)))
            {
                portRouteEntities.Add(portRouteEntity);
            }
        }

        result.WithValue(portRouteEntities.ToArray());
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<PortRouteEntity>> Get(string id, CancellationToken cancellationToken = default)
    {
        HttpResult<PortRouteEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("PORT_ROUTE_ID_INVALID", "Port route ID is invalid");
            return result;
        }

        var store = _portRouteStoreService.GetStore();

        if (!result.Success(await store.Get<PortRouteEntity>(id, cancellationToken: cancellationToken), out PortRouteEntity? portRouteEntity))
        {
            _logger.LogError("Error port route Get: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (portRouteEntity == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("PORT_ROUTE_ID_NOT_FOUND", "Port route ID not found");
            return result;
        }

        result.WithValue(portRouteEntity);
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<PortRouteEntity>> Create(PortRouteAddDto portRouteAddDto, CancellationToken cancellationToken = default)
    {
        HttpResult<PortRouteEntity> result = new();

        if (string.IsNullOrEmpty(portRouteAddDto.SourceEdgeId))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("SOURCE_EDGE_ID_INVALID", "Port route sourceEdgeId is invalid");
            return result;
        }

        if (string.IsNullOrEmpty(portRouteAddDto.DestinationEdgeId))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("DESTINATION_EDGE_ID_INVALID", "Port route destinationEdgeId is invalid");
            return result;
        }

        if (portRouteAddDto.SourceEdgePort == 0)
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("SOURCE_EDGE_PORT_INVALID", "Port route sourceEdgeId is invalid");
            return result;
        }

        if (portRouteAddDto.DestinationEdgePort == 0)
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("DESTINATION_EDGE_PORT_INVALID", "Port route destinationEdgePort is invalid");
            return result;
        }

        var edgeStore = _edgeStoreService.GetStore();

        List<string> edgeIds = [portRouteAddDto.SourceEdgeId, portRouteAddDto.DestinationEdgeId];

        foreach (var edgeId in edgeIds)
        {
            bool isFrom = portRouteAddDto.SourceEdgeId == edgeId;
            if (!result.Success(await edgeStore.Contains(edgeId, cancellationToken: cancellationToken), out bool containsEdgeId) || !containsEdgeId)
            {
                result.WithStatusCode(HttpStatusCode.NotFound);
                if (isFrom)
                {
                    result.WithError("SOURCE_EDGE_ID_NOT_FOUND", "Port route sourceEdgeId not found");
                }
                else
                {
                    result.WithError("DESTINATION_EDGE_ID_NOT_FOUND", "Port route destinationEdgeId not found");
                }
                return result;
            }

            if (!result.SuccessAndHasValue(await GetAll(sourceEdgeId: edgeId, destinationEdgeId: edgeId, cancellationToken: cancellationToken), out PortRouteEntity[]? edgePorts))
            {
                return result;
            }

            foreach (var edgePort in edgePorts)
            {
                if (edgePort.SourceEdgeId.Equals(edgeId) &&
                    edgePort.SourceEdgePort == portRouteAddDto.SourceEdgePort)
                {
                    result.WithStatusCode(HttpStatusCode.BadRequest);
                    result.WithError("SOURCE_EDGE_PORT_ALREADY_EXISTS", "Port route sourceEdgePort already exists");
                    return result;
                }
            }
        }

        var store = _portRouteStoreService.GetStore();

        PortRouteEntity newRoute = new()
        {
            Id = Guid.NewGuid().Encode(),
            SourceEdgeId = portRouteAddDto.SourceEdgeId,
            SourceEdgePort = portRouteAddDto.SourceEdgePort,
            DestinationEdgeId = portRouteAddDto.DestinationEdgeId,
            DestinationEdgePort = portRouteAddDto.DestinationEdgePort,
        };

        if (!result.Success(await store.Set(newRoute.Id, newRoute, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error port route Create: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        _portRouteEventHubService.OnPortRouteChanges(newRoute.SourceEdgeId);
        _portRouteEventHubService.OnPortRouteChanges(newRoute.DestinationEdgeId);

        result.WithValue(newRoute);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Port route id {} was created", newRoute.Id);

        return result;
    }

    public async Task<HttpResult<PortRouteEntity>> Edit(string id, PortRouteEditDto portRouteEditDto, CancellationToken cancellationToken = default)
    {
        HttpResult<PortRouteEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("PORT_ROUTE_ID_INVALID", "Port route ID is invalid");
            return result;
        }

        if (string.IsNullOrEmpty(portRouteEditDto.SourceEdgeId) &&
            string.IsNullOrEmpty(portRouteEditDto.DestinationEdgeId) &&
            portRouteEditDto.SourceEdgePort == null &&
            portRouteEditDto.DestinationEdgePort == null)
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("PORT_ROUTE_NO_CHANGES", "No port route field to edit");
            return result;
        }

        var store = _portRouteStoreService.GetStore();

        if (!result.Success(await store.Get<PortRouteEntity>(id, cancellationToken: cancellationToken), false, out PortRouteEntity? portRoute))
        {
            _logger.LogError("Error port route Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (portRoute == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("PORT_ROUTE_ID_NOT_FOUND", "Port route ID not found");
            return result;
        }

        if ((string.IsNullOrEmpty(portRouteEditDto.SourceEdgeId) || portRouteEditDto.SourceEdgeId == portRoute.SourceEdgeId) &&
            (string.IsNullOrEmpty(portRouteEditDto.DestinationEdgeId) || portRouteEditDto.DestinationEdgeId == portRoute.DestinationEdgeId) &&
            (portRouteEditDto.SourceEdgePort == null || portRouteEditDto.SourceEdgePort == portRoute.SourceEdgePort) &&
            (portRouteEditDto.DestinationEdgePort == null || portRouteEditDto.DestinationEdgePort == portRoute.DestinationEdgePort))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("PORT_ROUTE_NO_CHANGES", "No port route field to edit");
            return result;
        }

        var edgeStore = _edgeStoreService.GetStore();

        List<string> edgeIds = [];

        edgeIds.Add(string.IsNullOrEmpty(portRouteEditDto.SourceEdgeId) ? portRoute.SourceEdgeId : portRouteEditDto.SourceEdgeId);
        edgeIds.Add(string.IsNullOrEmpty(portRouteEditDto.DestinationEdgeId) ? portRoute.DestinationEdgeId : portRouteEditDto.DestinationEdgeId);

        foreach (var edgeId in edgeIds)
        {
            bool isFrom = (string.IsNullOrEmpty(portRouteEditDto.SourceEdgeId) ? portRoute.SourceEdgeId : portRouteEditDto.SourceEdgeId) == edgeId;
            if (!result.Success(await edgeStore.Contains(edgeId, cancellationToken: cancellationToken), out bool containsEdgeId) || !containsEdgeId)
            {
                result.WithStatusCode(HttpStatusCode.NotFound);
                if (isFrom)
                {
                    result.WithError("SOURCE_EDGE_ID_NOT_FOUND", "Port route sourceEdgeId not found");
                }
                else
                {
                    result.WithError("DESTINATION_EDGE_ID_NOT_FOUND", "Port route destinationEdgeId not found");
                }
                return result;
            }

            if (!result.SuccessAndHasValue(await GetAll(sourceEdgeId: edgeId, destinationEdgeId: edgeId, cancellationToken: cancellationToken), out PortRouteEntity[]? edgePorts))
            {
                return result;
            }

            foreach (var edgePort in edgePorts)
            {
                if (edgePort.Id.Equals(portRoute.Id))
                {
                    continue;
                }
                if (edgePort.SourceEdgeId.Equals(edgeId) &&
                    edgePort.SourceEdgePort == portRouteEditDto.SourceEdgePort)
                {
                    result.WithStatusCode(HttpStatusCode.BadRequest);
                    result.WithError("SOURCE_EDGE_PORT_ALREADY_EXISTS", "Port route sourceEdgePort already exists");
                    return result;
                }
            }
        }

        PortRouteEntity newPortRoute = new()
        {
            Id = portRoute.Id,
            SourceEdgeId = string.IsNullOrEmpty(portRouteEditDto.SourceEdgeId) ? portRoute.SourceEdgeId : portRouteEditDto.SourceEdgeId,
            DestinationEdgeId = string.IsNullOrEmpty(portRouteEditDto.DestinationEdgeId) ? portRoute.DestinationEdgeId : portRouteEditDto.DestinationEdgeId,
            SourceEdgePort = portRouteEditDto.SourceEdgePort == null ? portRoute.SourceEdgePort : portRouteEditDto.SourceEdgePort.Value,
            DestinationEdgePort = portRouteEditDto.DestinationEdgePort == null ? portRoute.DestinationEdgePort : portRouteEditDto.DestinationEdgePort.Value
        };

        if (!result.Success(await store.Set(id, newPortRoute, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error port route Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        _portRouteEventHubService.OnPortRouteChanges(newPortRoute.SourceEdgeId);
        _portRouteEventHubService.OnPortRouteChanges(newPortRoute.DestinationEdgeId);

        result.WithValue(newPortRoute);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Port route id {} was edited", newPortRoute.Id);

        return result;
    }

    public async Task<HttpResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        HttpResult result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("PORT_ROUTE_ID_INVALID", "Port route ID is invalid");
            return result;
        }

        var store = _portRouteStoreService.GetStore();

        if (!result.Success(await store.Get<PortRouteEntity>(id, cancellationToken: cancellationToken), false, out PortRouteEntity? portRoute))
        {
            _logger.LogError("Error port route Delete: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (portRoute == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("PORT_ROUTE_ID_NOT_FOUND", "Port route ID not found");
            return result;
        }

        if (!result.Success(await store.Delete(id, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error port route Delete: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        _portRouteEventHubService.OnPortRouteChanges(portRoute.SourceEdgeId);
        _portRouteEventHubService.OnPortRouteChanges(portRoute.DestinationEdgeId);

        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Port route id {} was deleted", id);

        return result;
    }
}
