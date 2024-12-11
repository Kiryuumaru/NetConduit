using Application.Common.Extensions;

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

    public async Task<bool> WaitForOpen(CancellationToken cancellationToken)
    {
        try
        {
            await _waiterEvent.WaitHandle.WaitHandleTask(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task WaitForOpenOrThrow(CancellationToken cancellationToken)
    {
        return _waiterEvent.WaitHandle.WaitHandleTask(cancellationToken);
    }
}
