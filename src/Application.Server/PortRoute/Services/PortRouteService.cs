using Application.Common;
using Application.LocalStore.Services;
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

public class PortRouteService(ILogger<PortRouteService> logger, PortRouteStoreService portRouteStoreService, EdgeStoreService edgeStoreService, PortRouteEventHubService portRouteEventHubService)
{
    private readonly ILogger<PortRouteService> _logger = logger;
    private readonly PortRouteStoreService _portRouteStoreService = portRouteStoreService;
    private readonly EdgeStoreService _edgeStoreService = edgeStoreService;
    private readonly PortRouteEventHubService _portRouteEventHubService = portRouteEventHubService;

    public async Task<HttpResult<PortRouteEntity[]>> GetAll(string? fromEdgeId = null, string? toEdgeId = null, CancellationToken cancellationToken = default)
    {
        HttpResult<PortRouteEntity[]> result = new();

        var store = _portRouteStoreService.GetStore();
        var edgeStore = _edgeStoreService.GetStore();

        if (!string.IsNullOrEmpty(fromEdgeId) &&
            (!result.Success(await edgeStore.Contains(fromEdgeId, cancellationToken: cancellationToken), out bool containsFromEdgeId) || !containsFromEdgeId))
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("FROM_EDGE_ID_NOT_FOUND", "Port route fromEdgeId does not exists");
            return result;
        }

        if (!string.IsNullOrEmpty(toEdgeId) &&
            (!result.Success(await edgeStore.Contains(toEdgeId, cancellationToken: cancellationToken), out bool containsToEdgeId) || !containsToEdgeId))
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("TO_EDGE_ID_NOT_FOUND", "Port route toEdgeId does not exists");
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
            if ((string.IsNullOrEmpty(fromEdgeId) && string.IsNullOrEmpty(toEdgeId)) ||
                (!string.IsNullOrEmpty(fromEdgeId) && fromEdgeId.Equals(portRouteEntity.FromEdgeId)) ||
                (!string.IsNullOrEmpty(toEdgeId) && toEdgeId.Equals(portRouteEntity.ToEdgeId)))
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

        if (string.IsNullOrEmpty(portRouteAddDto.FromEdgeId))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("FROM_EDGE_ID_INVALID", "Port route fromEdgeId is invalid");
            return result;
        }

        if (string.IsNullOrEmpty(portRouteAddDto.ToEdgeId))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("TO_EDGE_ID_INVALID", "Port route toEdgeId is invalid");
            return result;
        }

        if (portRouteAddDto.FromEdgePort == 0)
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("FROM_EDGE_PORT_INVALID", "Port route fromEdgeId is invalid");
            return result;
        }

        if (portRouteAddDto.ToEdgePort == 0)
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("TO_EDGE_PORT_INVALID", "Port route toEdgePort is invalid");
            return result;
        }

        var edgeStore = _edgeStoreService.GetStore();

        List<string> edgeIds = [portRouteAddDto.FromEdgeId, portRouteAddDto.ToEdgeId];

        foreach (var edgeId in edgeIds)
        {
            bool isFrom = portRouteAddDto.FromEdgeId == edgeId;
            if (!result.Success(await edgeStore.Contains(edgeId, cancellationToken: cancellationToken), out bool containsEdgeId) || !containsEdgeId)
            {
                result.WithStatusCode(HttpStatusCode.NotFound);
                if (isFrom)
                {
                    result.WithError("FROM_EDGE_ID_NOT_FOUND", "Port route fromEdgeId not found");
                }
                else
                {
                    result.WithError("TO_EDGE_ID_NOT_FOUND", "Port route toEdgeId not found");
                }
                return result;
            }

            if (!result.SuccessAndHasValue(await GetAll(fromEdgeId: edgeId, toEdgeId: edgeId, cancellationToken: cancellationToken), out PortRouteEntity[]? edgePorts))
            {
                return result;
            }

            foreach (var edgePort in edgePorts)
            {
                if (edgePort.FromEdgeId.Equals(edgeId) &&
                    edgePort.FromEdgePort == portRouteAddDto.FromEdgePort)
                {
                    result.WithStatusCode(HttpStatusCode.BadRequest);
                    result.WithError("FROM_EDGE_PORT_ALREADY_EXISTS", "Port route fromEdgePort already exists");
                    return result;
                }
                if (edgePort.ToEdgeId.Equals(edgeId) &&
                    edgePort.ToEdgePort == portRouteAddDto.ToEdgePort)
                {
                    result.WithStatusCode(HttpStatusCode.BadRequest);
                    result.WithError("TO_EDGE_PORT_ALREADY_EXISTS", "Port route toEdgePort already exists");
                    return result;
                }
            }
        }

        var store = _portRouteStoreService.GetStore();

        PortRouteEntity newRoute = new()
        {
            Id = Guid.NewGuid().Encode(),
            FromEdgeId = portRouteAddDto.FromEdgeId,
            FromEdgePort = portRouteAddDto.FromEdgePort,
            ToEdgeId = portRouteAddDto.ToEdgeId,
            ToEdgePort = portRouteAddDto.ToEdgePort,
        };

        if (!result.Success(await store.Set(newRoute.Id, newRoute, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error port route Create: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        _portRouteEventHubService.OnPortRouteChanges(newRoute.FromEdgeId);
        _portRouteEventHubService.OnPortRouteChanges(newRoute.ToEdgeId);

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

        if (string.IsNullOrEmpty(portRouteEditDto.FromEdgeId) &&
            string.IsNullOrEmpty(portRouteEditDto.ToEdgeId) &&
            portRouteEditDto.FromEdgePort == null &&
            portRouteEditDto.ToEdgePort == null)
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

        if ((string.IsNullOrEmpty(portRouteEditDto.FromEdgeId) || portRouteEditDto.FromEdgeId == portRoute.FromEdgeId) &&
            (string.IsNullOrEmpty(portRouteEditDto.ToEdgeId) || portRouteEditDto.ToEdgeId == portRoute.ToEdgeId) &&
            (portRouteEditDto.FromEdgePort == null || portRouteEditDto.FromEdgePort == portRoute.FromEdgePort) &&
            (portRouteEditDto.ToEdgePort == null || portRouteEditDto.ToEdgePort == portRoute.ToEdgePort))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("PORT_ROUTE_NO_CHANGES", "No port route field to edit");
            return result;
        }

        var edgeStore = _edgeStoreService.GetStore();

        List<string> edgeIds = [];

        edgeIds.Add(string.IsNullOrEmpty(portRouteEditDto.FromEdgeId) ? portRoute.FromEdgeId : portRouteEditDto.FromEdgeId);
        edgeIds.Add(string.IsNullOrEmpty(portRouteEditDto.ToEdgeId) ? portRoute.ToEdgeId : portRouteEditDto.ToEdgeId);

        foreach (var edgeId in edgeIds)
        {
            bool isFrom = (string.IsNullOrEmpty(portRouteEditDto.FromEdgeId) ? portRoute.FromEdgeId : portRouteEditDto.FromEdgeId) == edgeId;
            if (!result.Success(await edgeStore.Contains(edgeId, cancellationToken: cancellationToken), out bool containsEdgeId) || !containsEdgeId)
            {
                result.WithStatusCode(HttpStatusCode.NotFound);
                if (isFrom)
                {
                    result.WithError("FROM_EDGE_ID_NOT_FOUND", "Port route fromEdgeId not found");
                }
                else
                {
                    result.WithError("TO_EDGE_ID_NOT_FOUND", "Port route toEdgeId not found");
                }
                return result;
            }

            if (!result.SuccessAndHasValue(await GetAll(fromEdgeId: edgeId, toEdgeId: edgeId, cancellationToken: cancellationToken), out PortRouteEntity[]? edgePorts))
            {
                return result;
            }

            foreach (var edgePort in edgePorts)
            {
                if (edgePort.Id.Equals(portRoute.Id))
                {
                    continue;
                }
                if (edgePort.FromEdgeId.Equals(edgeId) &&
                    edgePort.FromEdgePort == portRouteEditDto.FromEdgePort)
                {
                    result.WithStatusCode(HttpStatusCode.BadRequest);
                    result.WithError("FROM_EDGE_PORT_ALREADY_EXISTS", "Port route fromEdgePort already exists");
                    return result;
                }
                if (edgePort.ToEdgeId.Equals(edgeId) &&
                    edgePort.ToEdgePort == portRouteEditDto.ToEdgePort)
                {
                    result.WithStatusCode(HttpStatusCode.BadRequest);
                    result.WithError("TO_EDGE_PORT_ALREADY_EXISTS", "Port route toEdgePort already exists");
                    return result;
                }
            }
        }

        PortRouteEntity newPortRoute = new()
        {
            Id = portRoute.Id,
            FromEdgeId = string.IsNullOrEmpty(portRouteEditDto.FromEdgeId) ? portRoute.FromEdgeId : portRouteEditDto.FromEdgeId,
            ToEdgeId = string.IsNullOrEmpty(portRouteEditDto.ToEdgeId) ? portRoute.ToEdgeId : portRouteEditDto.ToEdgeId,
            FromEdgePort = portRouteEditDto.FromEdgePort == null ? portRoute.FromEdgePort : portRouteEditDto.FromEdgePort.Value,
            ToEdgePort = portRouteEditDto.ToEdgePort == null ? portRoute.ToEdgePort : portRouteEditDto.ToEdgePort.Value
        };

        if (!result.Success(await store.Set(id, newPortRoute, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error port route Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        _portRouteEventHubService.OnPortRouteChanges(newPortRoute.FromEdgeId);
        _portRouteEventHubService.OnPortRouteChanges(newPortRoute.ToEdgeId);

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

        _portRouteEventHubService.OnPortRouteChanges(portRoute.FromEdgeId);
        _portRouteEventHubService.OnPortRouteChanges(portRoute.ToEdgeId);

        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Port route id {} was deleted", id);

        return result;
    }
}
