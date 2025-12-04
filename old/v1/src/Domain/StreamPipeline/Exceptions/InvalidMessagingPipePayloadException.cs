namespace Domain.StreamPipeline.Exceptions;

public class InvalidMessagingPipePayloadException(string nameOfType) : Exception($"Message is not {nameOfType}")
{
}
