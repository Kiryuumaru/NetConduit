using DisposableHelpers;

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
