namespace Domain.StreamPipeline.Models;

public class CommandPipePayload<TCommand, TResponse>
{
    public required bool HasCommand { get; init; }

    public required bool HasResponse { get; init; }

    public required TCommand? Command { get; init; }

    public required TResponse? Response { get; init; }
}
