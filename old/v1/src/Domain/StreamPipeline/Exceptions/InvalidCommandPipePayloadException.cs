namespace Domain.StreamPipeline.Exceptions;

public class InvalidCommandPipePayloadException(string nameOfType) : Exception($"Command is not {nameOfType}")
{
}
