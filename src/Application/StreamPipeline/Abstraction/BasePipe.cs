using Application.StreamPipeline.Common;
using DisposableHelpers.Attributes;

namespace Application.StreamPipeline.Abstraction;

[Disposable]
public abstract partial class BasePipe
{
    private TranceiverStream? _tranceiverStream = null;

    protected TranceiverStream GetStream()
    {
        return _tranceiverStream ?? throw new Exception($"{GetType().Name} not started");
    }

    public Task Start(TranceiverStream tranceiverStream, CancellationToken stoppingToken)
    {
        _tranceiverStream = tranceiverStream;
        return Execute(tranceiverStream, stoppingToken);
    }

    protected abstract Task Execute(TranceiverStream tranceiverStream, CancellationToken stoppingToken);
}
