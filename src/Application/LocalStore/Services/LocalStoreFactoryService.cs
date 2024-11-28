using Application.Common;
using Application.Edge.Services;
using Application.LocalStore.Common;
using Application.LocalStore.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TransactionHelpers;
using TransactionHelpers.Exceptions;

namespace Application.LocalStore.Services;

public class LocalStoreFactoryService(IServiceProvider serviceProvider)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    public async Task<ConcurrentLocalStore> GetStore(string group = "common_group", CancellationToken cancellationToken = default)
    {
        var localStoreConcurrencyService = _serviceProvider.GetRequiredService<LocalStoreConcurrencyService>();
        var ticket = await localStoreConcurrencyService.Aquire(cancellationToken);
        var localStoreService = _serviceProvider.GetRequiredService<ILocalStoreService>();
        var localStore = new ConcurrentLocalStore(localStoreService)
        {
            Group = group
        };
        localStore.CancelWhenDisposing().Register(ticket.Dispose);
        return localStore;
    }
}
