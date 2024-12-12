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

    public ValueTask<bool> WaitForOpen(CancellationToken cancellationToken)
    {
        return _waiterEvent.WaitHandle.WaitAsync(cancellationToken);
    }
}
