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
            result.WithError(new ArgumentException("Port route field fromEdgeId does not exists"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        if (!string.IsNullOrEmpty(toEdgeId) &&
            (!result.Success(await edgeStore.Contains(toEdgeId, cancellationToken: cancellationToken), out bool containsToEdgeId) || !containsToEdgeId))
        {
            result.WithError(new ArgumentException("Port route field toEdgeId does not exists"));
            result.WithStatusCode(HttpStatusCode.NotFound);
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
            result.WithError(new ArgumentException("Id is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
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
            result.WithError(new Exception("Port route not found"));
            result.WithStatusCode(HttpStatusCode.NotFound);
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
            result.WithError(new ArgumentException("Port route field fromEdgeId is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        if (portRouteAddDto.FromEdgePort == 0)
        {
            result.WithError(new ArgumentException("Port route field fromEdgePort is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        if (string.IsNullOrEmpty(portRouteAddDto.ToEdgeId))
        {
            result.WithError(new ArgumentException("Port route field toEdgeId is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        if (portRouteAddDto.ToEdgePort == 0)
        {
            result.WithError(new ArgumentException("Port route field toEdgePort is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        var edgeStore = _edgeStoreService.GetStore();

        if (!string.IsNullOrEmpty(portRouteAddDto.FromEdgeId))
        {
            if (!result.Success(await edgeStore.Contains(portRouteAddDto.FromEdgeId, cancellationToken: cancellationToken), out bool containsEdgeId) || !containsEdgeId)
            {
                result.WithError(new ArgumentException("Port route field fromEdgeId does not exists"));
                result.WithStatusCode(HttpStatusCode.NotFound);
                return result;
            }

            if (!result.Success(await GetAll(fromEdgeId: portRouteAddDto.FromEdgeId, cancellationToken: cancellationToken), out PortRouteEntity[]? fromEdgePorts))
            {
                return result;
            }

            if (fromEdgePorts != null &&
                fromEdgePorts.Any(i => i.FromEdgePort == portRouteAddDto.FromEdgePort))
            {
                result.WithError(new ArgumentException("Port route fromEdgePort " + portRouteAddDto.FromEdgePort + " already exists"));
                result.WithStatusCode(HttpStatusCode.BadRequest);
                return result;
            }
        }

        if (!string.IsNullOrEmpty(portRouteAddDto.ToEdgeId))
        {
            if (!result.Success(await edgeStore.Contains(portRouteAddDto.ToEdgeId, cancellationToken: cancellationToken), out bool containsEdgeId) || !containsEdgeId)
            {
                result.WithError(new ArgumentException("Port route field toEdgeId does not exists"));
                result.WithStatusCode(HttpStatusCode.NotFound);
                return result;
            }

            if (!result.Success(await GetAll(toEdgeId: portRouteAddDto.ToEdgeId, cancellationToken: cancellationToken), out PortRouteEntity[]? toEdgePorts))
            {
                return result;
            }

            if (toEdgePorts != null &&
                toEdgePorts.Any(i => i.ToEdgePort == portRouteAddDto.ToEdgePort))
            {
                result.WithError(new ArgumentException("Port route toEdgePort " + portRouteAddDto.ToEdgePort + " already exists"));
                result.WithStatusCode(HttpStatusCode.BadRequest);
                return result;
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
            result.WithError(new ArgumentException("Id is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        if (string.IsNullOrEmpty(portRouteEditDto.FromEdgeId) &&
            string.IsNullOrEmpty(portRouteEditDto.ToEdgeId) &&
            portRouteEditDto.FromEdgePort == null &&
            portRouteEditDto.ToEdgePort == null)
        {
            result.WithError(new ArgumentException("No port route field to edit"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
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
            result.WithError(new Exception("Port route not found"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        if ((string.IsNullOrEmpty(portRouteEditDto.FromEdgeId) || portRouteEditDto.FromEdgeId == portRoute.FromEdgeId) &&
            (string.IsNullOrEmpty(portRouteEditDto.ToEdgeId) || portRouteEditDto.ToEdgeId == portRoute.ToEdgeId) &&
            (portRouteEditDto.FromEdgePort == null || portRouteEditDto.FromEdgePort == portRoute.FromEdgePort) &&
            (portRouteEditDto.ToEdgePort == null || portRouteEditDto.ToEdgePort == portRoute.ToEdgePort))
        {
            result.WithError(new ArgumentException("No port route field changes"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        var edgeStore = _edgeStoreService.GetStore();

        if (!string.IsNullOrEmpty(portRouteEditDto.FromEdgeId) &&
            (!result.Success(await edgeStore.Contains(portRouteEditDto.FromEdgeId, cancellationToken: cancellationToken), out bool containsFromEdgeId) || !containsFromEdgeId))
        {
            result.WithError(new ArgumentException("Port route field fromEdgeId does not exists"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        if (!string.IsNullOrEmpty(portRouteEditDto.ToEdgeId) &&
            (!result.Success(await edgeStore.Contains(portRouteEditDto.ToEdgeId, cancellationToken: cancellationToken), out bool containsToEdgeId) || !containsToEdgeId))
        {
            result.WithError(new ArgumentException("Port route field toEdgeId does not exists"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
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
            result.WithError(new ArgumentException("Id is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
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
            result.WithError(new ArgumentException("Port route does not exists"));
            result.WithStatusCode(HttpStatusCode.NotFound);
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
