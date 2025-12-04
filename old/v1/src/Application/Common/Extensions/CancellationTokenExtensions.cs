namespace Application.Common.Extensions;

public static class CancellationTokenExtensions
{
    public static CancellationToken WithTimeout(this CancellationToken cancellationToken, TimeSpan timeout)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, new CancellationTokenSource(timeout).Token).Token;
    }

    public static Task WhenCanceled(this CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        CancellationTokenRegistration? reg = null;
        reg = cancellationToken.Register(s =>
        {
            tcs.TrySetResult(true);
            reg?.Unregister();
        }, tcs);
        return tcs.Task;
    }
}
