using Application.Common;
using Application.Edge.Interfaces;
using Application.Handshake.Services;
using Application.Server.Edge.Services;
using Application.Server.PortRoute.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulHelpers;
using RestfulHelpers.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Edge.Services;

public class EdgeApiService(IServiceProvider serviceProvider, IConfiguration configuration) : IEdgeService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    private Task<HttpResult> InvokeEndpoint(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var endpoint = _configuration.GetVarRefValue("SERVER_ENDPOINT").Trim('/') + "/api/edge" + path;
        return new HttpClient().Execute(method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    }

    private Task<HttpResult<TReturn>> InvokeEndpoint<TReturn>(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var endpoint = _configuration.GetVarRefValue("SERVER_ENDPOINT").Trim('/') + "/api/edge" + path;
        return new HttpClient().Execute<TReturn>(method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    }

    private Task<HttpResult<TReturn>> InvokeEndpoint<TPayload, TReturn>(HttpMethod method, TPayload payload, string path, CancellationToken cancellationToken)
    {
        var endpoint = _configuration.GetVarRefValue("SERVER_ENDPOINT").Trim('/') + "/api/edge" + path;
        return new HttpClient().ExecuteWithContent<TReturn, TPayload>(payload, method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    }

    public Task<HttpResult<EdgeConnectionEntity>> Create(EdgeAddDto edgeAddDto, CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint<EdgeAddDto, EdgeConnectionEntity>(HttpMethod.Post, edgeAddDto, "", cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<EdgeService>().Create(edgeAddDto, cancellationToken);
        }
    }

    public Task<HttpResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint(HttpMethod.Delete, "/" + id, cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<EdgeService>().Delete(id, cancellationToken);
        }
    }

    public Task<HttpResult<EdgeEntity>> Edit(string id, EdgeEditDto edgeEditDto, CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint<EdgeEditDto, EdgeEntity>(HttpMethod.Put, edgeEditDto, "/" + id, cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<EdgeService>().Edit(id, edgeEditDto, cancellationToken);
        }
    }

    public Task<HttpResult<EdgeConnectionEntity>> Get(string id, CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint<EdgeConnectionEntity>(HttpMethod.Get, "/" + id, cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<EdgeService>().Get(id, cancellationToken);
        }
    }

    public Task<HttpResult<EdgeEntity[]>> GetAll(CancellationToken cancellationToken = default)
    {
        if (_configuration.ContainsVarRefValue("SERVER_ENDPOINT"))
        {
            return InvokeEndpoint<EdgeEntity[]>(HttpMethod.Get, "", cancellationToken);
        }
        else
        {
            return _serviceProvider.GetRequiredService<EdgeService>().GetAll(cancellationToken);
        }
    }
}
