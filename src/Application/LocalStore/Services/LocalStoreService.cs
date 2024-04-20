using Application.Common;
using Application.LocalStore.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TransactionHelpers;

namespace Application.LocalStore.Services;

public class LocalStoreService(ILocalStore localStore)
{
    private readonly ILocalStore _localStore = localStore;

    private const string _CommonGroup = "common_store";

    public async Task<Result<bool>> Contains(string id, string group = _CommonGroup, CancellationToken cancellationToken = default)
    {
        Result<bool> result = new();

        if (!result.Success(await _localStore.Get(group, id, cancellationToken), out string? value))
        {
            return result;
        }

        result.WithValue(!string.IsNullOrEmpty(value));

        return result;
    }

    public async Task<Result<T>> Get<T>(string id, string group = _CommonGroup, CancellationToken cancellationToken = default)
        where T : class
    {
        Result<T> result = new();

        if (!result.Success(await _localStore.Get(group, id, cancellationToken), out string? value))
        {
            return result;
        }

        try
        {
            if (!string.IsNullOrEmpty(value))
            {
                T? obj = JsonSerializer.Deserialize<T>(value, JsonSerializerExtension.CamelCaseOption);
                result.WithValue(obj);
            }
            else
            {
                result.WithValue((T?)default);
            }
        }
        catch (Exception ex)
        {
            result.WithError(ex);
        }

        return result;
    }

    public async Task<Result<string[]>> GetIds(string group = _CommonGroup, CancellationToken cancellationToken = default)
    {
        Result<string[]> result = new();

        if (!result.Success(await _localStore.GetIds(group, cancellationToken), out string[]? ids))
        {
            return result;
        }

        result.WithValue(ids);

        return result;
    }

    public async Task<Result> Set<T>(string id, T? obj, string group = _CommonGroup, CancellationToken cancellationToken = default)
        where T : class
    {
        Result result = new();

        string data;
        try
        {
            data = JsonSerializer.Serialize(obj, JsonSerializerExtension.CamelCaseOption);
        }
        catch (Exception ex)
        {
            result.WithError(ex);
            return result;
        }

        if (!result.Success(await _localStore.Set(group, id, data, cancellationToken)))
        {
            return result;
        }

        return result;
    }

    public async Task<Result<bool>> Delete(string id, string group = _CommonGroup, CancellationToken cancellationToken = default)
    {
        Result<bool> result = new();

        if (!result.Success(await _localStore.Get(group, id, cancellationToken), out string? value))
        {
            return result;
        }

        if (!result.Success(await _localStore.Set(group, id, null, cancellationToken)))
        {
            return result;
        }

        result.WithValue(!string.IsNullOrEmpty(value));

        return result;
    }
}
