using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Common;

internal static class StreamHelpers
{
    public static async Task ForwardStream(Stream? source, Stream? destination, int bufferSize, Action<Exception> onError, CancellationToken stoppingToken)
    {
        if (source == null || !source.CanRead ||
            destination == null || !destination.CanRead)
        {
            return;
        }

        byte[] buffer = new byte[bufferSize];

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int bytesread = await source.ReadAsync(buffer, stoppingToken);
                await destination.WriteAsync(buffer.AsMemory(0, bytesread), stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                onError(ex);
            }
        }
    }
}
