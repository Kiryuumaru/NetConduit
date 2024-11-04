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

namespace Application.Edge.Services;

public class EdgeStoreService(ILogger<EdgeStoreService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<EdgeStoreService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private const string _edgeGroupStore = "edge_group_store";

    private LocalStoreService? _localStoreService = null;

    public LocalStoreService GetStore()
    {
        using var _ = _logger.BeginScopeMap(new()
        {
            ["Service"] = nameof(EdgeStoreService),
            ["ServiceAction"] = nameof(GetStore)
        });

        _localStoreService ??= _serviceProvider.GetRequiredService<LocalStoreService>();
        _localStoreService.CommonGroup = _edgeGroupStore;
        return _localStoreService;
    }
}
