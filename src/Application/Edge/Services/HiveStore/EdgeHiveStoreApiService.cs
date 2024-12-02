using Application.Common;
using Application.Configuration.Extensions;
using Application.Edge.Interfaces;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RestfulHelpers;
using RestfulHelpers.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Edge.Services.HiveStore;

public class EdgeHiveStoreApiService(IServiceProvider serviceProvider, IConfiguration configuration) : IEdgeHiveStoreService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IConfiguration _configuration = configuration;

    public Task<HttpResult<GetEdgeWithTokenDto>> GetOrCreate(string id, Func<AddEdgeDto> onCreate, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<HttpResult<bool>> IEdgeHiveStoreService.Contains(string id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    Task<HttpResult<GetEdgeWithTokenDto>> IEdgeHiveStoreService.Create(AddEdgeDto edgeAddDto, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    Task<HttpResult<GetEdgeInfoDto>> IEdgeHiveStoreService.Delete(string id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    Task<HttpResult<GetEdgeWithTokenDto>> IEdgeHiveStoreService.Edit(string id, EditEdgeDto edgeEditDto, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    Task<HttpResult<GetEdgeInfoDto>> IEdgeHiveStoreService.Get(string id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    Task<HttpResult<GetEdgeInfoDto[]>> IEdgeHiveStoreService.GetAll(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    Task<HttpResult<GetEdgeWithTokenDto>> IEdgeHiveStoreService.GetToken(string id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }


    //private string GetServerTcpEndpoint()
    //{
    //    var endpoint = _configuration.GetServerTcpEndpoint() ?? throw new Exception("Server endpoint was not set");
    //    return endpoint;
    //}

    //private Task<HttpResult> InvokeEndpoint(HttpMethod method, string path, CancellationToken cancellationToken)
    //{
    //    var endpoint = GetServerTcpEndpoint().Trim('/') + "/api/edge" + path;
    //    return new HttpClient().Execute(method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    //}

    //private Task<HttpResult<TReturn>> InvokeEndpoint<TReturn>(HttpMethod method, string path, CancellationToken cancellationToken)
    //{
    //    var endpoint = GetServerTcpEndpoint().Trim('/') + "/api/edge" + path;
    //    return new HttpClient().Execute<TReturn>(method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    //}

    //private Task<HttpResult<TReturn>> InvokeEndpoint<TPayload, TReturn>(HttpMethod method, TPayload payload, string path, CancellationToken cancellationToken)
    //{
    //    var endpoint = GetServerTcpEndpoint().Trim('/') + "/api/edge" + path;
    //    return new HttpClient().ExecuteWithContent<TReturn, TPayload>(payload, method, endpoint, JsonSerializerExtension.CamelCaseOption, cancellationToken);
    //}

    //public Task<HttpResult<EdgeConnection>> Create(AddEdgeDto edgeAddDto, CancellationToken cancellationToken = default)
    //{
    //    if (_configuration.GetServerTcpEndpoint() != null)
    //    {
    //        return InvokeEndpoint<AddEdgeDto, EdgeConnection>(HttpMethod.Post, edgeAddDto, "", cancellationToken);
    //    }
    //    else
    //    {
    //        return _serviceProvider.GetRequiredService<EdgeHiveStoreService>().Create(edgeAddDto, cancellationToken);
    //    }
    //}

    //public Task<HttpResult> Delete(string id, CancellationToken cancellationToken = default)
    //{
    //    if (_configuration.GetServerTcpEndpoint() != null)
    //    {
    //        return InvokeEndpoint(HttpMethod.Delete, "/" + id, cancellationToken);
    //    }
    //    else
    //    {
    //        return _serviceProvider.GetRequiredService<EdgeHiveStoreService>().Delete(id, cancellationToken);
    //    }
    //}

    //public Task<HttpResult<EdgeEntity>> Edit(string id, EditEdgeDto edgeEditDto, CancellationToken cancellationToken = default)
    //{
    //    if (_configuration.GetServerTcpEndpoint() != null)
    //    {
    //        return InvokeEndpoint<EditEdgeDto, EdgeEntity>(HttpMethod.Put, edgeEditDto, "/" + id, cancellationToken);
    //    }
    //    else
    //    {
    //        return _serviceProvider.GetRequiredService<EdgeHiveStoreService>().Edit(id, edgeEditDto, cancellationToken);
    //    }
    //}

    //public Task<HttpResult<EdgeConnection>> Get(string id, CancellationToken cancellationToken = default)
    //{
    //    if (_configuration.GetServerTcpEndpoint() != null)
    //    {
    //        return InvokeEndpoint<EdgeConnection>(HttpMethod.Get, "/" + id, cancellationToken);
    //    }
    //    else
    //    {
    //        return _serviceProvider.GetRequiredService<EdgeHiveStoreService>().Get(id, cancellationToken);
    //    }
    //}

    //public Task<HttpResult<EdgeEntity[]>> GetAll(CancellationToken cancellationToken = default)
    //{
    //    if (_configuration.GetServerTcpEndpoint() != null)
    //    {
    //        return InvokeEndpoint<EdgeEntity[]>(HttpMethod.Get, "", cancellationToken);
    //    }
    //    else
    //    {
    //        return _serviceProvider.GetRequiredService<EdgeHiveStoreService>().GetAll(cancellationToken);
    //    }
    //}
}
