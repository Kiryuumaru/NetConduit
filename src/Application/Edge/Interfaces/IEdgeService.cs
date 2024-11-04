using Application.Common;
using Application.Edge.Common;
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

namespace Application.Edge.Interfaces;

public interface IEdgeService
{
    Task<HttpResult<EdgeInfoGetDto[]>> GetAll(CancellationToken cancellationToken = default);

    Task<HttpResult<EdgeWithTokenGetDto>> Get(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<EdgeWithTokenGetDto>> Create(EdgeAddDto edgeAddDto, CancellationToken cancellationToken = default);

    Task<HttpResult<EdgeInfoGetDto>> Edit(string id, EdgeEditDto edgeEditDto, CancellationToken cancellationToken = default);

    Task<HttpResult<EdgeInfoGetDto>> Delete(string id, CancellationToken cancellationToken = default);
}
