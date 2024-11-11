using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Common;

public static class StreamHelpers
{
    public static async Task ForwardStream(Stream source, Stream destination, int bufferSize, Action<Exception> onError, CancellationToken stoppingToken)
    {
        byte[] buffer = new byte[bufferSize];

        while (!stoppingToken.IsCancellationRequested && source.CanRead && destination.CanWrite)
        {
            try
            {
                int bytesread = await source.ReadAsync(buffer, stoppingToken);
                if (!destination.CanWrite)
                {
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, bytesread), stoppingToken);
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
    }
}
