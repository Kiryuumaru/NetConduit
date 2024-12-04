namespace Application.Configuration.Exceptions;

public class NoConfigValueException(string configName) : Exception($"{configName} config is empty")
{
}
