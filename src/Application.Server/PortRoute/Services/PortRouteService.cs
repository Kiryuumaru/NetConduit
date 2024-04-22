using Application.LocalStore.Services;
using Domain.Edge.Entities;
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

public class PortRouteService(ILogger<PortRouteService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<PortRouteService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private const string _portRouteGroupStore = "port_route_group_store";

    private LocalStoreService? _localStoreService = null;

    private LocalStoreService GetStore()
    {
        _localStoreService ??= _serviceProvider.GetRequiredService<LocalStoreService>();
        return _localStoreService;
    }

    public async Task<HttpResult<PortRouteEntity[]>> GetAll(CancellationToken cancellationToken = default)
    {
        HttpResult<PortRouteEntity[]> result = new();

        var store = GetStore();

        if (!result.SuccessAndHasValue(await store.GetIds(group: _portRouteGroupStore, cancellationToken: cancellationToken), out string[]? portRouteIds))
        {
            _logger.LogError("Error port route GetAll: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        List<PortRouteEntity> portRouteEntities = [];

        foreach (var id in portRouteIds)
        {
            if (!result.SuccessAndHasValue(await store.Get<PortRouteEntity>(id, group: _portRouteGroupStore, cancellationToken: cancellationToken), out PortRouteEntity? portRouteEntity))
            {
                _logger.LogError("Error port route GetAll: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }
            portRouteEntities.Add(portRouteEntity);
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

        var store = GetStore();

        if (!result.Success(await store.Get<PortRouteEntity>(id, group: _portRouteGroupStore, cancellationToken: cancellationToken), out PortRouteEntity? portRouteEntity))
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
}
