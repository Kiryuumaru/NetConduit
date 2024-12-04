namespace Application.Common.Features;

public class GateKeeper
{
    private readonly ManualResetEventSlim _waiterEvent = new(false);

    public bool IsOpen { get; private set; }

    public GateKeeper(bool initialOpenState = false)
    {
        SetOpen(initialOpenState);
    }

    public void SetOpen(bool isOpen = true)
    {
        IsOpen = isOpen;

        if (isOpen)
        {
            _waiterEvent.Set();
        }
        else
        {
            _waiterEvent.Reset();
        }
    }

    public Task<bool> WaitForOpen(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                _waiterEvent.Wait(cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }

        }, cancellationToken);
    }

    public Task WaitForOpenOrThrow(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            _waiterEvent.Wait(cancellationToken);

        }, cancellationToken);
    }
}
