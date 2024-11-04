using Application.Common;
using Application.Edge.Common;
using Application.Edge.Interfaces;
using Application.LocalStore.Services;
using Application.Server.PortRoute.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.Edge.Models;
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

namespace Application.Edge.Services;

public class EdgeService(ILogger<EdgeService> logger, EdgeStoreService edgeStoreService) : IEdgeService
{
    private readonly ILogger<EdgeService> _logger = logger;
    private readonly EdgeStoreService _edgeStoreService = edgeStoreService;

    public async Task<HttpResult<EdgeEntity[]>> GetAll(CancellationToken cancellationToken = default)
    {
        using var _ = _logger.BeginScopeMap(new()
        {
            ["Service"] = nameof(EdgeService),
            ["ServiceAction"] = nameof(GetAll)
        });

        HttpResult<EdgeEntity[]> result = new();

        var store = _edgeStoreService.GetStore();

        if (!result.SuccessAndHasValue(await store.GetIds(cancellationToken: cancellationToken), out string[]? edgeIds))
        {
            _logger.LogError("Error edge GetAll: {Error}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        List<EdgeEntity> edgeEntities = [];

        foreach (var id in edgeIds)
        {
            if (!result.SuccessAndHasValue(await store.Get<EdgeTokenEntity>(id, cancellationToken: cancellationToken), out EdgeTokenEntity? edge))
            {
                _logger.LogError("Error edge GetAll: {Error}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }
            edgeEntities.Add(edge);
        }

        result.WithValue(edgeEntities.ToArray());
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<EdgeConnection>> Get(string id, CancellationToken cancellationToken = default)
    {
        using var _ = _logger.BeginScopeMap(new()
        {
            ["Service"] = nameof(EdgeService),
            ["ServiceAction"] = nameof(Get),
            ["EdgeId"] = id
        });

        HttpResult<EdgeConnection> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("EDGE_ID_INVALID", "Edge ID is invalid");
            return result;
        }

        var store = _edgeStoreService.GetStore();

        if (!result.Success(await store.Get<EdgeTokenEntity>(id, cancellationToken: cancellationToken), out EdgeTokenEntity? edge))
        {
            _logger.LogError("Error edge Get: {Error}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (edge == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("EDGE_ID_NOT_FOUND", "Edge ID not found");
            return result;
        }

        result.WithValue(EdgeEntityHelpers.Encode(edge));
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<EdgeConnection>> Create(EdgeAddDto edgeAddDto, CancellationToken cancellationToken = default)
    {
        using var _ = _logger.BeginScopeMap(new()
        {
            ["Service"] = nameof(EdgeService),
            ["ServiceAction"] = nameof(Create),
            ["EdgeName"] = edgeAddDto.Name
        });

        HttpResult<EdgeConnection> result = new();

        if (string.IsNullOrEmpty(edgeAddDto.Name))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("EDGE_NAME_INVALID", "Edge name is invalid");
            return result;
        }

        EdgeTokenEntity newEdge = EdgeEntityHelpers.GenerateToken(new()
        {
            Id = Guid.NewGuid().Encode(),
            Name = edgeAddDto.Name,
        });

        if (!result.Success(await Create(newEdge, cancellationToken), out EdgeConnection? edgeConnectionEntity))
        {
            return result;
        }

        result.WithValue(edgeConnectionEntity);
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<EdgeEntity>> Edit(string id, EdgeEditDto edgeEditDto, CancellationToken cancellationToken = default)
    {
        using var _ = _logger.BeginScopeMap(new()
        {
            ["Service"] = nameof(EdgeService),
            ["ServiceAction"] = nameof(Edit),
            ["EdgeId"] = id
        });

        HttpResult<EdgeEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("EDGE_ID_INVALID", "Edge ID is invalid");
            return result;
        }

        if (string.IsNullOrEmpty(edgeEditDto.NewName) && !edgeEditDto.RenewToken)
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("EDGE_NO_CHANGES", "No edge field to edit");
            return result;
        }

        if (id.Equals(EdgeDefaults.ServerEdgeEntity.Id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("EDGE_SERVER_NOT_EDITABLE", "Edge server is not editable");
            return result;
        }

        var store = _edgeStoreService.GetStore();

        if (!result.Success(await store.Get<EdgeTokenEntity>(id, cancellationToken: cancellationToken), false, out EdgeTokenEntity? edge))
        {
            _logger.LogError("Error edge Edit: {Error}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (edge == null)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("EDGE_ID_NOT_FOUND", "Edge ID not found");
            return result;
        }

        if ((string.IsNullOrEmpty(edgeEditDto.NewName) || edgeEditDto.NewName == edge.Name) &&
            !edgeEditDto.RenewToken)
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("EDGE_NO_CHANGES", "No edge field to edit");
            return result;
        }

        EdgeTokenEntity newEdge;

        if (edgeEditDto.RenewToken)
        {
            newEdge = EdgeEntityHelpers.GenerateToken(new()
            {
                Id = edge.Id,
                Name = string.IsNullOrEmpty(edgeEditDto.NewName) ? edge.Name : edgeEditDto.NewName,
            });
        }
        else
        {
            newEdge = new()
            {
                Id = edge.Id,
                Name = string.IsNullOrEmpty(edgeEditDto.NewName) ? edge.Name : edgeEditDto.NewName,
                Token = edge.Token
            };
        }

        if (!result.Success(await store.Set(id, newEdge, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error edge Edit: {Error}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(newEdge as EdgeEntity);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Edge id {EdgeId} was edited", newEdge.Id);

        return result;
    }

    public async Task<HttpResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        using var _ = _logger.BeginScopeMap(new()
        {
            ["Service"] = nameof(EdgeService),
            ["ServiceAction"] = nameof(Delete),
            ["EdgeId"] = id
        });

        HttpResult result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("EDGE_ID_INVALID", "Edge ID is invalid");
            return result;
        }

        if (id.Equals(EdgeDefaults.ServerEdgeEntity.Id))
        {
            result.WithStatusCode(HttpStatusCode.BadRequest);
            result.WithError("EDGE_SERVER_NOT_DELETABLE", "Edge server is not deletable");
            return result;
        }

        var store = _edgeStoreService.GetStore();

        if (!result.Success(await store.Contains(id, cancellationToken: cancellationToken), out bool contains))
        {
            _logger.LogError("Error edge Delete: {Error}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (!contains)
        {
            result.WithStatusCode(HttpStatusCode.NotFound);
            result.WithError("EDGE_ID_NOT_FOUND", "Edge ID not found");
            return result;
        }

        if (!result.Success(await store.Delete(id, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error edge Delete: {Error}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Edge id {EdgeId} was deleted", id);

        return result;
    }

    internal async Task<HttpResult<EdgeConnection>> Create(EdgeTokenEntity newEdge, CancellationToken cancellationToken = default)
    {
        using var _ = _logger.BeginScopeMap(new()
        {
            ["Service"] = nameof(EdgeService),
            ["ServiceAction"] = nameof(Create),
            ["EdgeId"] = id
        });

        HttpResult<EdgeConnection> result = new();

        var store = _edgeStoreService.GetStore();

        if (!result.Success(await store.Set(newEdge.Id, newEdge, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error edge Create: {Error}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        var newEdgeConnectionEntity = EdgeEntityHelpers.Encode(newEdge);

        result.WithValue(newEdgeConnectionEntity);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Edge id {EdgeId} was created with handshake-token {Error}", newEdge.Id, newEdgeConnectionEntity.HandshakeToken);

        return result;
    }
}
