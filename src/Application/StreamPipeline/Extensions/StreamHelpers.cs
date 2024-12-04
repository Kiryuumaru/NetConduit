namespace Application.StreamPipeline.Extensions;

public static class StreamHelpers
{
    public static Task ForwardStream(Stream source, Stream destination, int bufferSize, Action<Exception> onError, CancellationToken stoppingToken)
    {
        return Task.Run(() =>
        {
            Span<byte> buffer = stackalloc byte[bufferSize];

            while (!stoppingToken.IsCancellationRequested && source.CanRead && destination.CanWrite)
            {
                try
                {
                    int bytesRead = source.Read(buffer);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    if (bytesRead == 0)
                    {
                        stoppingToken.WaitHandle.WaitOne(100);
                        continue;
                    }
                    if (!destination.CanWrite)
                    {
                        break;
                    }
                    destination.Write(buffer[..bytesRead]);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    onError(ex);
                }
            }

        }, stoppingToken);
    }
}
