using Application.Common.Extensions;

namespace Application.StreamPipeline.Extensions;

public static class StreamHelpers
{
    public static async Task ForwardStream(Stream source, Stream destination, int bufferSize, Action<Exception> onError, CancellationToken stoppingToken)
    {
        Memory<byte> buffer = new byte[bufferSize];

        while (!stoppingToken.IsCancellationRequested && source.CanRead && destination.CanWrite)
        {
            try
            {
                int bytesRead = await source.ReadAsync(buffer, stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                if (bytesRead == 0)
                {
                    await stoppingToken.WithTimeout(TimeSpan.FromMilliseconds(100)).WhenCanceled();
                    continue;
                }
                if (!destination.CanWrite)
                {
                    break;
                }
                destination.Write(buffer[..bytesRead].Span);
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException ||
                    ex is ObjectDisposedException)
                {
                    break;
                }
                onError(ex);
            }
        }
    }
}
