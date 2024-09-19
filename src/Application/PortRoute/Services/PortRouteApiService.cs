using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Services;
using Application.LocalStore.Services;
using Application.PortRoute.Services;
using Application.Server.Edge.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.PortRoute.Dtos;
using Domain.PortRoute.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulHelpers;
using RestfulHelpers.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.PortRoute.Interfaces;

public class PortRouteApiService(ILogger<PortRouteApiService> logger, IServiceProvider serviceProvider, IConfiguration configuration) : IPortRouteService
{
    private readonly ILogger<PortRouteApiService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    private string GetServerEndpoint()
    {
        var endpoint = _configuration.GetServerEndpoint() ?? throw new Exception("Server endpoint was not set");
        return endpoint;
    }

    private Task<HttpResult> InvokeEndpoint(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var endpoint = GetServerEndpoint().Trim('/') + "/api/route" + path;
        return new HttpClient().Execute(method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    }

    private Task<HttpResult<TReturn>> InvokeEndpoint<TReturn>(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var endpoint = GetServerEndpoint().Trim('/') + "/api/route" + path;
        return new HttpClient().Execute<TReturn>(method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    }

    private Task<HttpResult<TReturn>> InvokeEndpoint<TPayload, TReturn>(HttpMethod method, TPayload payload, string path, CancellationToken cancellationToken)
    {
        var endpoint = GetServerEndpoint().Trim('/') + "/api/route" + path;
        return new HttpClient().ExecuteWithContent<TReturn, TPayload>(payload, method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    }

    public Task<HttpResult<PortRouteEntity>> Create(PortRouteAddDto portRouteAddDto, CancellationToken cancellationToken = default)
    {
        if (_configuration.GetServerEndpoint() != null)
        {
            return InvokeEndpoint<PortRouteAddDto, PortRouteEntity>(HttpMethod.Post, portRouteAddDto, "", cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<PortRouteService>().Create(portRouteAddDto, cancellationToken);
        }
    }

    public Task<HttpResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        if (_configuration.GetServerEndpoint() != null)
        {
            return InvokeEndpoint(HttpMethod.Delete, "/" + id, cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<PortRouteService>().Delete(id, cancellationToken);
        }
    }

    public Task<HttpResult<PortRouteEntity>> Edit(string id, PortRouteEditDto portRouteEditDto, CancellationToken cancellationToken = default)
    {
        if (_configuration.GetServerEndpoint() != null)
        {
            return InvokeEndpoint<PortRouteEditDto, PortRouteEntity>(HttpMethod.Put, portRouteEditDto, "/" + id, cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<PortRouteService>().Edit(id, portRouteEditDto, cancellationToken);
        }
    }

    public Task<HttpResult<PortRouteEntity>> Get(string id, CancellationToken cancellationToken = default)
    {
        if (_configuration.GetServerEndpoint() != null)
        {
            return InvokeEndpoint<PortRouteEntity>(HttpMethod.Get, "/" + id, cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<PortRouteService>().Get(id, cancellationToken);
        }
    }

    public Task<HttpResult<PortRouteEntity[]>> GetAll(string? sourceEdgeId = null, string? destinationEdgeId = null, CancellationToken cancellationToken = default)
    {
        if (_configuration.GetServerEndpoint() != null)
        {
            return InvokeEndpoint<PortRouteEntity[]>(HttpMethod.Get, $"?sourceEdgeId={sourceEdgeId}&destinationEdgeId{destinationEdgeId}", cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<PortRouteService>().GetAll(sourceEdgeId, destinationEdgeId, cancellationToken);
        }
    }
}
