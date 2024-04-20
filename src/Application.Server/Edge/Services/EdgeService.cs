using Application.Common;
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
using System.Threading;
using System.Threading.Tasks;
using TransactionHelpers;
using TransactionHelpers.Interface;

namespace Application.Server.Edge.Services;

public class EdgeService(ILogger<EdgeService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<EdgeService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private const string _edgeGroupStore = "edge_group_store";

    private LocalStoreService? _localStoreService = null;

    private LocalStoreService GetStore()
    {
        _localStoreService ??= _serviceProvider.GetRequiredService<LocalStoreService>();
        return _localStoreService;
    }

    private static string GenerateToken()
    {
        return StringEncoder.Random(50);
    }

    public async Task<HttpResult<EdgeEntity[]>> GetAll(CancellationToken cancellationToken = default)
    {
        HttpResult<EdgeEntity[]> result = new();

        var store = GetStore();

        if (!result.SuccessAndHasValue(await store.GetIds(group: _edgeGroupStore, cancellationToken: cancellationToken), out string[]? edgeIds))
        {
            _logger.LogError("Error edge GetAll: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        List<EdgeEntity> edgeEntities = [];

        foreach (var id in edgeIds)
        {
            if (!result.SuccessAndHasValue(await store.Get<EdgeEntity>(id, group: _edgeGroupStore, cancellationToken: cancellationToken), out EdgeEntity? edge))
            {
                _logger.LogError("Error edge GetAll: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }
            edgeEntities.Add(edge);
        }

        result.WithValue(edgeEntities.ToArray());
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<EdgeEntity>> Get(string id, CancellationToken cancellationToken = default)
    {
        HttpResult<EdgeEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithError(new ArgumentException("Id is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        var store = GetStore();

        if (!result.Success(await store.Get<EdgeEntity>(id, group: _edgeGroupStore, cancellationToken: cancellationToken), out EdgeEntity? edge))
        {
            _logger.LogError("Error edge Get: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (edge == null)
        {
            result.WithError(new Exception("Edge not found"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        result.WithValue(edge);
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<EdgeEntity>> Create(EdgeAddDto edgeAddDto, CancellationToken cancellationToken = default)
    {
        HttpResult<EdgeEntity> result = new();

        if (string.IsNullOrEmpty(edgeAddDto.Name))
        {
            result.WithError(new ArgumentException("Edge field name is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        var store = GetStore();

        var newId = Guid.NewGuid().Encode();

        EdgeEntity newEdge = new()
        {
            Id = newId,
            Name = edgeAddDto.Name,
            Token = GenerateToken()
        };

        if (!result.Success(await store.Set(newId, newEdge, group: _edgeGroupStore, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error edge Create: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(newEdge);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Edge id {} was created", newEdge.Id);

        return result;
    }

    public async Task<HttpResult<EdgeEntity>> Edit(string id, EdgeEditDto edgeEditDto, CancellationToken cancellationToken = default)
    {
        HttpResult<EdgeEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithError(new ArgumentException("Id is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        if (string.IsNullOrEmpty(edgeEditDto.NewName) && !edgeEditDto.RenewToken)
        {
            result.WithError(new ArgumentException("No edge field to edit"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        var store = GetStore();

        if (!result.Success(await store.Get<EdgeEntity>(id, group: _edgeGroupStore, cancellationToken: cancellationToken), out EdgeEntity? edge))
        {
            _logger.LogError("Error edge Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (edge == null)
        {
            result.WithError(new Exception("Edge not found"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        if ((string.IsNullOrEmpty(edgeEditDto.NewName) || edgeEditDto.NewName == edge.Name) &&
            !edgeEditDto.RenewToken)
        {
            result.WithError(new ArgumentException("No edge field changes"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        EdgeEntity newEdge = new()
        {
            Id = edge.Id,
            Name = string.IsNullOrEmpty(edgeEditDto.NewName) ? edge.Name : edgeEditDto.NewName,
            Token = edgeEditDto.RenewToken ? GenerateToken() : edge.Token
        };

        if (!result.Success(await store.Set(id, newEdge, group: _edgeGroupStore, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error edge Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(newEdge);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Edge id {} was edited", newEdge.Id);

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

        var store = GetStore();

        if (!result.Success(await store.Contains(id, group: _edgeGroupStore, cancellationToken: cancellationToken), out bool contains))
        {
            _logger.LogError("Error edge Delete: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (!contains)
        {
            result.WithError(new ArgumentException("Edge does not exists"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        if (!result.Success(await store.Delete(id, group: _edgeGroupStore, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error edge Delete: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Edge id {} was deleted", id);

        return result;
    }
}
