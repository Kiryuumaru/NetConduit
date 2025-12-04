using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Common.Extensions;

public static class ThreadHelpers
{
    public static Task WaitThread(Action action)
    {
        SemaphoreSlim reset = new(0);
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch
            {
                throw;
            }
            finally
            {
                reset.Release();
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        return Task.Run(async () =>
        {
            await reset.WaitAsync();
            thread.Join();
        });
    }

    public static Task WaitThread(Func<Task> task)
    {
        SemaphoreSlim reset = new(0);
        var thread = new Thread(async () =>
        {
            try
            {
                await task();
            }
            catch
            {
                throw;
            }
            finally
            {
                reset.Release();
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        return Task.Run(async () =>
        {
            await reset.WaitAsync();
            thread.Join();
        });
    }

    public static async ValueTask<bool> WaitAsync(this WaitHandle waitHandle, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (waitHandle.WaitOne(0))
        {
            return true;
        }

        var tcs = new TaskCompletionSource();

        var registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            waitObject: waitHandle,
            callBack: (o, timeout) => tcs.TrySetResult(),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: true);

        bool cancelled = false;
        CancellationTokenRegistration? cancellationTokenRegistration = null;

        if (cancellationToken.CanBeCanceled)
        {
            cancellationTokenRegistration = cancellationToken.Register(() =>
            {
                cancelled = true;
                tcs.TrySetCanceled();
                registeredWaitHandle.Unregister(null);
                cancellationTokenRegistration?.Unregister();
            });
        }

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            if (cancelled)
            {
                return false;
            }
            throw;
        }
        finally
        {
            registeredWaitHandle.Unregister(null);
            cancellationTokenRegistration?.Unregister();
        }

        return true;
    }

    public static async ValueTask<bool> WaitAsync(this WaitHandle waitHandle, int millisecondsTimeout)
    {
        if (waitHandle.WaitOne(0))
        {
            return true;
        }

        if (millisecondsTimeout == 0)
        {
            return false;
        }

        var tcs = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(millisecondsTimeout);

        var registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            waitObject: waitHandle,
            callBack: (o, timeout) => tcs.TrySetResult(),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: true);

        bool cancelled = false;

        CancellationTokenRegistration? cancellationTokenRegistration = null;
        cancellationTokenRegistration = cts.Token.Register(() =>
        {
            cancelled = true;
            tcs.TrySetCanceled();
            registeredWaitHandle.Unregister(null);
            cancellationTokenRegistration?.Unregister();
        });

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            if (cancelled)
            {
                return false;
            }
            throw;
        }
        finally
        {
            registeredWaitHandle.Unregister(null);
            cancellationTokenRegistration?.Unregister();
        }

        return true;
    }

    public static ValueTask<bool> WaitAsync(this WaitHandle waitHandle, TimeSpan timeout)
    {
        return WaitAsync(waitHandle, (int)timeout.TotalMilliseconds);
    }
}