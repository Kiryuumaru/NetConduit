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

namespace Application.PortRoute.Services;

public class PortRouteStoreService(ILogger<PortRouteStoreService> logger, IServiceProvider serviceProvider)
{
    private readonly ILogger<PortRouteStoreService> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    private const string _portRouteGroupStore = "port_route_group_store";

    private LocalStoreService? _localStoreService = null;

    public LocalStoreService GetStore()
    {
        _localStoreService ??= _serviceProvider.GetRequiredService<LocalStoreService>();
        _localStoreService.CommonGroup = _portRouteGroupStore;
        return _localStoreService;
    }
}
