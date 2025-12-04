namespace Domain.StreamPipeline.Exceptions;

public class CorruptedHeaderBytesException : Exception
{
    public static CorruptedHeaderBytesException Instance { get; } = new();

    public CorruptedHeaderBytesException()
        : base("Corrupted header bytes received")
    {

    }
}
