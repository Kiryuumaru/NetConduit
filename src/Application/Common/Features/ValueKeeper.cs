namespace Application.Common.Features;

public class ValueKeeper<T>
{
    private readonly ManualResetEventSlim _waiterEvent = new(false);

    public T? Value { get; private set; }

    public void SetValue(T? value)
    {
        Value = value;

        if (value != null)
        {
            _waiterEvent.Set();
        }
        else
        {
            _waiterEvent.Reset();
        }
    }

    public Task<T?> WaitForValue(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                _waiterEvent.Wait(cancellationToken);
                return Value;
            }
            catch
            {
                return default;
            }

        }, cancellationToken);
    }

    public Task<T> WaitForValueOrThrow(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                _waiterEvent.Wait(cancellationToken);
                if (Value == null)
                {
                    throw new OperationCanceledException();
                }
                return Value;
            }
            catch
            {
                throw;
            }

        }, cancellationToken);
    }
}
