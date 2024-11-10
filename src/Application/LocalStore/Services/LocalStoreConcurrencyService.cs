using Application.Common;
using Application.Edge.Services;
using Application.LocalStore.Common;
using Application.LocalStore.Interfaces;
using DisposableHelpers;
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

public class LocalStoreConcurrencyService()
{
    private readonly SemaphoreSlim semaphoreSlim = new(1);

    public async Task<IDisposable> Aquire(CancellationToken cancellationToken)
    {
        await semaphoreSlim.WaitAsync(cancellationToken);
        return new Disposable(disposing =>
        {
            if (disposing)
            {
                semaphoreSlim.Release();
            }
        });
    }
}
