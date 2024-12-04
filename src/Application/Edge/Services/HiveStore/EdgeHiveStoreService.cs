using Application.Common.Extensions;
using Application.Edge.Extensions;
using Application.Edge.Interfaces;
using Application.LocalStore.Common;
using Application.LocalStore.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.Edge.Enums;
using Microsoft.Extensions.Logging;
using RestfulHelpers.Common;
using System.Net;
using TransactionHelpers;

namespace Application.Edge.Services.HiveStore;

public class EdgeHiveStoreService(ILogger<EdgeHiveStoreService> logger, LocalStoreFactoryService localStoreFactoryService) : IEdgeHiveStoreService
{
    private readonly ILogger<EdgeHiveStoreService> _logger = logger;
    private readonly LocalStoreFactoryService _localStoreFactoryService = localStoreFactoryService;

    public const string EdgeGroupStore = "edge_hive_group_store";

    Task<ConcurrentLocalStore> GetStore(CancellationToken cancellationToken)
    {
        return _localStoreFactoryService.GetStore(EdgeGroupStore, cancellationToken);
    }

    async Task<HttpResult<bool>> IEdgeHiveStoreService.Contains(string id, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeHiveStoreService), nameof(IEdgeHiveStoreService.Contains));

        HttpResult<bool> result = new();

        using var store = await GetStore(cancellationToken);

        if (!result.Success(await store.Contains(id, cancellationToken: cancellationToken), out bool contains))
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

    async Task<HttpResult<GetEdgeInfoDto[]>> IEdgeHiveStoreService.GetAll(CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeHiveStoreService), nameof(IEdgeHiveStoreService.GetAll));

        HttpResult<GetEdgeInfoDto[]> result = new();

        using var store = await GetStore(cancellationToken);

        if (!result.SuccessAndHasValue(await store.GetIds(cancellationToken: cancellationToken), out string[]? edgeIds))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        List<GetEdgeInfoDto> edgeEntities = [];

        foreach (var id in edgeIds)
        {
            if (!result.SuccessAndHasValue(await store.Get<EdgeEntity>(id, cancellationToken: cancellationToken), out EdgeEntity? edge))
            {
                _logger.LogError("Error: {Error}", result.Error);
                result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }
            edgeEntities.Add(new()
            {
                Id = edge.Id,
                EdgeType = edge.EdgeType,
                Name = edge.Name,
            });
        }

        result.WithValue(edgeEntities.ToArray());
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    async Task<HttpResult<GetEdgeInfoDto>> IEdgeHiveStoreService.Get(string id, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeHiveStoreService), nameof(IEdgeHiveStoreService.Get));

        using var store = await GetStore(cancellationToken);

        HttpResult<GetEdgeInfoDto> result = new();

        if (!result.SuccessAndHasValue(await Get(store, id, cancellationToken), out EdgeEntity? edge))
        {
            return result;
        }

        result.WithValue(new GetEdgeInfoDto()
        {
            Id = edge.Id,
            EdgeType = edge.EdgeType,
            Name = edge.Name,
        });
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    async Task<HttpResult<GetEdgeWithTokenDto>> IEdgeHiveStoreService.GetToken(string id, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeHiveStoreService), nameof(IEdgeHiveStoreService.GetToken));

        using var store = await GetStore(cancellationToken);

        HttpResult<GetEdgeWithTokenDto> result = new();

        if (!result.SuccessAndHasValue(await Get(store, id, cancellationToken), out EdgeEntity? edge))
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

    async Task<HttpResult<GetEdgeWithTokenDto>> IEdgeHiveStoreService.Create(AddEdgeDto edgeAddDto, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeHiveStoreService), nameof(IEdgeHiveStoreService.Create), new()
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

        if (!result.Success(await store.Set(newEdge.Id.ToString(), newEdge, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        string token = EdgeEntityHelpers.Encode(new()
        {
            Id = newEdge.Id,
            EdgeType = newEdge.EdgeType,
            Name = newEdge.Name,
            Key = newEdge.Key,
        });

        result.WithValue(new GetEdgeWithTokenDto()
        {
            Id = newEdge.Id,
            EdgeType = newEdge.EdgeType,
            Name = newEdge.Name,
            Token = token
        });
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    async Task<HttpResult<GetEdgeWithTokenDto>> IEdgeHiveStoreService.Edit(string id, EditEdgeDto edgeEditDto, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeHiveStoreService), nameof(IEdgeHiveStoreService.Edit), new()
        {
            ["EdgeId"] = id
        });

        HttpResult<GetEdgeWithTokenDto> result = new();

        if (string.IsNullOrEmpty(id))
        {
            _logger.LogError("Error: Edge ID is invalid");
            result.WithError("EDGE_ID_INVALID", "Edge ID is invalid");
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        if (string.IsNullOrEmpty(edgeEditDto.NewName) && !edgeEditDto.RenewToken)
        {
            _logger.LogError("Error: No edge field to edit");
            result.WithError("EDGE_NO_CHANGES", "No edge field to edit");
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        using var store = await GetStore(cancellationToken);

        if (!result.Success(await store.Get<EdgeEntity>(id, cancellationToken: cancellationToken), false, out EdgeEntity? edge))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_SERVER_NOT_EDITABLE", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (edge == null)
        {
            _logger.LogError("Error: Edge ID not found");
            result.WithError("EDGE_ID_NOT_FOUND", "Edge ID not found");
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        if ((string.IsNullOrEmpty(edgeEditDto.NewName) || edgeEditDto.NewName == edge.Name) &&
            !edgeEditDto.RenewToken)
        {
            _logger.LogError("Error: No edge field to edit");
            result.WithError("EDGE_NO_CHANGES", "No edge field to edit");
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        EdgeEntity newEdge = new()
        {
            Id = edge.Id,
            EdgeType = edge.EdgeType,
            Name = string.IsNullOrEmpty(edgeEditDto.NewName) ? edge.Name : edgeEditDto.NewName,
            Key = edgeEditDto.RenewToken ? RandomHelpers.ByteArray(EdgeDefaults.EdgeKeySize) : edge.Key,
        };

        if (!result.Success(await store.Set(id, newEdge, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error edge Edit: {Error}", result.Error);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        string token = EdgeEntityHelpers.Encode(new()
        {
            Id = newEdge.Id,
            EdgeType = newEdge.EdgeType,
            Name = newEdge.Name,
            Key = newEdge.Key,
        });

        result.WithValue(new GetEdgeWithTokenDto()
        {
            Id = newEdge.Id,
            EdgeType = edge.EdgeType,
            Name = newEdge.Name,
            Token = token
        });
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Edge id {EdgeId} was edited", newEdge.Id);

        return result;
    }

    async Task<HttpResult<GetEdgeInfoDto>> IEdgeHiveStoreService.Delete(string id, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeHiveStoreService), nameof(IEdgeHiveStoreService.Delete), new()
        {
            ["EdgeId"] = id
        });

        HttpResult<GetEdgeInfoDto> result = new();

        if (string.IsNullOrEmpty(id))
        {
            _logger.LogError("Error: Edge ID is invalid");
            result.WithError("EDGE_ID_INVALID", "Edge ID is invalid");
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        using var store = await GetStore(cancellationToken);

        if (!result.Success(await store.Get<EdgeEntity>(id, cancellationToken: cancellationToken), false, out EdgeEntity? edge))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_SERVER_NOT_EDITABLE", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (edge == null)
        {
            _logger.LogError("Error: Edge ID not found");
            result.WithError("EDGE_ID_NOT_FOUND", "Edge ID not found");
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        if (edge.EdgeType == EdgeType.Server)
        {
            _logger.LogError("Error: Edge server is not deletable");
            result.WithError("EDGE_SERVER_NOT_DELETABLE", "Edge server is not deletable");
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        if (!result.Success(await store.Delete(id, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Edge id {EdgeId} was deleted", id);

        return result;
    }

    async Task<HttpResult<GetEdgeWithTokenDto>> IEdgeHiveStoreService.GetOrCreate(string id, Func<AddEdgeDto> onCreate, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(EdgeHiveStoreService), nameof(IEdgeHiveStoreService.GetOrCreate));

        HttpResult<GetEdgeWithTokenDto> result = new();

        using var store = await GetStore(cancellationToken);

        if (!result.Success(await store.Contains(id, cancellationToken: cancellationToken), out bool contains))
        {
            _logger.LogError("Error: {Error}", result.Error);
            result.WithError("EDGE_INTERNAL_SERVER_ERROR", $"Internal server error: {result.Error}");
            result.WithStatusCode(HttpStatusCode.InternalServerError);
        }

        EdgeEntity? edge = null;
        string token;
        if (contains)
        {
            if (!result.SuccessAndHasValue(await Get(store, id, cancellationToken), out edge))
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
                Id = Guid.Parse(id),
                EdgeType = edgeAddDto.EdgeType,
                Name = edgeAddDto.Name,
                Key = RandomHelpers.ByteArray(EdgeDefaults.EdgeKeySize)
            };

            if (!result.Success(await store.Set(edge.Id.ToString(), edge, cancellationToken: cancellationToken)))
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
