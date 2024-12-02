using Application.Common;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.Edge.Services.HiveStore;
using Application.LocalStore.Common;
using Application.LocalStore.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulHelpers.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TransactionHelpers;
using TransactionHelpers.Interface;

namespace Application.Edge.Services.LocalStore;

public class EdgeLocalStoreService(ILogger<EdgeLocalStoreService> logger, LocalStoreFactoryService localStoreFactoryService) : IEdgeLocalStoreService
{
    private readonly ILogger<EdgeLocalStoreService> _logger = logger;
    private readonly LocalStoreFactoryService _localStoreFactoryService = localStoreFactoryService;

    public const string EdgeGroupStore = "edge_local_group_store";
    public const string EdgeLocalId = "local_id";

    Task<ConcurrentLocalStore> GetStore(CancellationToken cancellationToken)
    {
        return _localStoreFactoryService.GetStore(EdgeGroupStore, cancellationToken);
    }

    async Task<HttpResult<GetEdgeWithTokenDto>> IEdgeLocalStoreService.Get(CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeLocalStoreService), nameof(IEdgeLocalStoreService.Get));

        using var store = await GetStore(cancellationToken);

        HttpResult<GetEdgeWithTokenDto> result = new();

        if (!result.SuccessAndHasValue(await Get(store, EdgeLocalId, cancellationToken), out EdgeEntity? edge))
        {
            return result;
        }

        string token = EdgeEntityHelpers.Encode(new()
        {
            Id = edge.Id,
            EdgeType = edge.EdgeType,
            Name = edge.Name,
            Key = edge.Key,
        });

        result.WithValue(new GetEdgeWithTokenDto()
        {
            Id = edge.Id,
            EdgeType = edge.EdgeType,
            Name = edge.Name,
            Token = token
        });
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    async Task<HttpResult<GetEdgeWithTokenDto>> IEdgeLocalStoreService.Create(AddEdgeDto edgeAddDto, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeLocalStoreService), nameof(IEdgeLocalStoreService.Create), new()
        {
            ["EdgeType"] = edgeAddDto.EdgeType,
            ["EdgeName"] = edgeAddDto.Name
        });

        HttpResult<GetEdgeWithTokenDto> result = new();

        if (string.IsNullOrEmpty(edgeAddDto.Name))
        {
            _logger.LogError("Error: Edge name is invalid");
            result.WithError("EDGE_NAME_INVALID", "Edge name is invalid");
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        using var store = await GetStore(cancellationToken);

        EdgeEntity newEdge = new()
        {
            EdgeType = edgeAddDto.EdgeType,
            Name = edgeAddDto.Name,
            Key = RandomHelpers.ByteArray(EdgeDefaults.EdgeKeySize)
        };

        if (!result.Success(await store.Set(EdgeLocalId, newEdge, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        string token = EdgeEntityHelpers.Encode(new()
        {
            Id = newEdge.Id,
            EdgeType = edgeAddDto.EdgeType,
            Name = newEdge.Name,
            Key = newEdge.Key,
        });

        result.WithValue(new GetEdgeWithTokenDto()
        {
            Id = newEdge.Id,
            EdgeType = edgeAddDto.EdgeType,
            Name = newEdge.Name,
            Token = token
        });
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    async Task<HttpResult<bool>> IEdgeLocalStoreService.Contains(CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeLocalStoreService), nameof(IEdgeLocalStoreService.Contains));

        HttpResult<bool> result = new();

        using var store = await GetStore(cancellationToken);

        if (!result.Success(await store.Contains(EdgeLocalId, cancellationToken: cancellationToken), out bool contains))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(contains);
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    async Task<HttpResult<GetEdgeWithTokenDto>> IEdgeLocalStoreService.GetOrCreate(Func<AddEdgeDto> onCreate, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeHiveStoreService), nameof(IEdgeLocalStoreService.GetOrCreate));

        HttpResult<GetEdgeWithTokenDto> result = new();

        using var store = await GetStore(cancellationToken);

        if (!result.Success(await store.Contains(EdgeLocalId, cancellationToken: cancellationToken), out bool contains))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
        }

        EdgeEntity? edge = null;
        string token;
        if (contains)
        {
            if (!result.SuccessAndHasValue(await Get(store, EdgeLocalId, cancellationToken), out edge))
            {
                return result;
            }
        }
        else
        {
            var edgeAddDto = onCreate();

            if (string.IsNullOrEmpty(edgeAddDto.Name))
            {
                _logger.LogError("Error: Edge name is invalid");
                result.WithError("EDGE_NAME_INVALID", "Edge name is invalid");
                result.WithStatusCode(HttpStatusCode.BadRequest);
                return result;
            }

            edge = new()
            {
                EdgeType = edgeAddDto.EdgeType,
                Name = edgeAddDto.Name,
                Key = RandomHelpers.ByteArray(EdgeDefaults.EdgeKeySize)
            };

            if (!result.Success(await store.Set(EdgeLocalId, edge, cancellationToken: cancellationToken)))
            {
                _logger.LogError("Error: {Error}", result.Error);
                result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }
        }

        token = EdgeEntityHelpers.Encode(new()
        {
            Id = edge.Id,
            EdgeType = edge.EdgeType,
            Name = edge.Name,
            Key = edge.Key,
        });
        result.WithValue(new GetEdgeWithTokenDto()
        {
            Id = edge.Id,
            EdgeType = edge.EdgeType,
            Name = edge.Name,
            Token = token
        });

        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    private async Task<HttpResult<EdgeEntity>> Get(ConcurrentLocalStore store, string id, CancellationToken cancellationToken)
    {
        HttpResult<EdgeEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            _logger.LogError("Error: Edge ID is invalid");
            result.WithError("EDGE_ID_INVALID", "Edge ID is invalid");
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        if (!result.Success(await store.Get<EdgeEntity>(id, cancellationToken: cancellationToken), out EdgeEntity? edge))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (edge == null)
        {
            _logger.LogError("Error: Edge ID {EdgeId} not found", id);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", "Edge ID not found");
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        result.WithValue(edge);
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }
}
