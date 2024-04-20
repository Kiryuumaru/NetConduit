using Application.Common;
using Application.LocalStore.Services;
using Domain.Client.Dtos;
using Domain.Client.Entities;
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

namespace Application.Server.Client.Services;

public class ClientService(ILogger<ClientService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<ClientService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private const string _clientGroupStore = "client_group_store";

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

    public async Task<HttpResult<ClientEntity[]>> GetAll(CancellationToken cancellationToken = default)
    {
        HttpResult<ClientEntity[]> result = new();

        var store = GetStore();

        if (!result.SuccessAndHasValue(await store.GetIds(group: _clientGroupStore, cancellationToken: cancellationToken), out string[]? clientIds))
        {
            _logger.LogError("Error client GetAll: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        List<ClientEntity> clientEntities = [];

        foreach (var id in clientIds)
        {
            if (!result.SuccessAndHasValue(await store.Get<ClientEntity>(id, group: _clientGroupStore, cancellationToken: cancellationToken), out ClientEntity? client))
            {
                _logger.LogError("Error client GetAll: {}", result.Error);
                result.WithStatusCode(HttpStatusCode.InternalServerError);
                return result;
            }
            clientEntities.Add(client);
        }

        result.WithValue(clientEntities.ToArray());
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<ClientEntity>> Get(string id, CancellationToken cancellationToken = default)
    {
        HttpResult<ClientEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithError(new ArgumentException("Id is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        var store = GetStore();

        if (!result.Success(await store.Get<ClientEntity>(id, group: _clientGroupStore, cancellationToken: cancellationToken), out ClientEntity? client))
        {
            _logger.LogError("Error client Get: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (client == null)
        {
            result.WithError(new Exception("Client not found"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        result.WithValue(client);
        result.WithStatusCode(HttpStatusCode.OK);

        return result;
    }

    public async Task<HttpResult<ClientEntity>> Create(ClientAddDto clientAddDto, CancellationToken cancellationToken = default)
    {
        HttpResult<ClientEntity> result = new();

        if (string.IsNullOrEmpty(clientAddDto.Name))
        {
            result.WithError(new ArgumentException("Client field name is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        var store = GetStore();

        var newId = Guid.NewGuid().Encode();

        ClientEntity newClient = new()
        {
            Id = newId,
            Name = clientAddDto.Name,
            Token = GenerateToken()
        };

        if (!result.Success(await store.Set(newId, newClient, group: _clientGroupStore, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error client Create: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(newClient);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Client id {} was created", newClient.Id);

        return result;
    }

    public async Task<HttpResult<ClientEntity>> Edit(string id, ClientEditDto clientEditDto, CancellationToken cancellationToken = default)
    {
        HttpResult<ClientEntity> result = new();

        if (string.IsNullOrEmpty(id))
        {
            result.WithError(new ArgumentException("Id is empty"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        if (string.IsNullOrEmpty(clientEditDto.NewName) && !clientEditDto.RenewToken)
        {
            result.WithError(new ArgumentException("No client field to edit"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        var store = GetStore();

        if (!result.Success(await store.Get<ClientEntity>(id, group: _clientGroupStore, cancellationToken: cancellationToken), out ClientEntity? client))
        {
            _logger.LogError("Error client Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (client == null)
        {
            result.WithError(new Exception("Client not found"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        if ((string.IsNullOrEmpty(clientEditDto.NewName) || clientEditDto.NewName == client.Name) &&
            !clientEditDto.RenewToken)
        {
            result.WithError(new ArgumentException("No client field changes"));
            result.WithStatusCode(HttpStatusCode.BadRequest);
            return result;
        }

        ClientEntity newClient = new()
        {
            Id = client.Id,
            Name = string.IsNullOrEmpty(clientEditDto.NewName) ? client.Name : clientEditDto.NewName,
            Token = clientEditDto.RenewToken ? GenerateToken() : client.Token
        };

        if (!result.Success(await store.Set(id, newClient, group: _clientGroupStore, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error client Edit: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithValue(newClient);
        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Client id {} was edited", newClient.Id);

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

        if (!result.Success(await store.Contains(id, group: _clientGroupStore, cancellationToken: cancellationToken), out bool contains))
        {
            _logger.LogError("Error client Delete: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        if (!contains)
        {
            result.WithError(new ArgumentException("Client does not exists"));
            result.WithStatusCode(HttpStatusCode.NotFound);
            return result;
        }

        if (!result.Success(await store.Delete(id, group: _clientGroupStore, cancellationToken: cancellationToken)))
        {
            _logger.LogError("Error client Delete: {}", result.Error);
            result.WithStatusCode(HttpStatusCode.InternalServerError);
            return result;
        }

        result.WithStatusCode(HttpStatusCode.OK);

        _logger.LogInformation("Client id {} was deleted", id);

        return result;
    }
}
