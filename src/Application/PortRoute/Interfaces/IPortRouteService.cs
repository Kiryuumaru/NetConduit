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

namespace Application.PortRoute.Interfaces;

public interface IPortRouteService
{
    Task<HttpResult<PortRouteEntity[]>> GetAll(string? sourceEdgeId = null, string? destinationEdgeId = null, CancellationToken cancellationToken = default);

    Task<HttpResult<PortRouteEntity>> Get(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<PortRouteEntity>> Create(PortRouteAddDto portRouteAddDto, CancellationToken cancellationToken = default);

    Task<HttpResult<PortRouteEntity>> Edit(string id, PortRouteEditDto portRouteEditDto, CancellationToken cancellationToken = default);

    Task<HttpResult> Delete(string id, CancellationToken cancellationToken = default);
}
