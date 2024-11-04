using Application.Common;
using Application.Edge.Common;
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

namespace Application.Edge.Interfaces;

public interface IEdgeService
{
    Task<HttpResult<bool>> Contains(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeInfoDto[]>> GetAll(CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeInfoDto>> Get(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeWithTokenDto>> GetToken(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeWithTokenDto>> Create(AddEdgeDto edgeAddDto, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeWithTokenDto>> Edit(string id, EditEdgeDto edgeEditDto, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeInfoDto>> Delete(string id, CancellationToken cancellationToken = default);
}
