namespace Application.Logger.Interfaces;

public interface ILoggerReader
{
    Task Start(int tail, bool follow, Dictionary<string, string> scope, CancellationToken cancellationToken = default);
}
