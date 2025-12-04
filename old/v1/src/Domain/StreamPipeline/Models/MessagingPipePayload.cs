namespace Domain.StreamPipeline.Models;

public class MessagingPipePayload<T>
{
    public required Guid MessageGuid { get; init; }

    public required T Message { get; init; }
}
