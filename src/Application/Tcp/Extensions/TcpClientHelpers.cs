using System.Net.Sockets;
using Application.Common.Extensions;
using Application.StreamPipeline.Common;

namespace Application.Tcp.Extensions;

internal static class TcpClientHelpers
{
    public static async Task WatchLiveliness(TcpClient tcpClient, TranceiverStream tranceiverStream, CancellationTokenSource cts, TimeSpan livelinessSpan)
    {
        Memory<byte> buffer = new byte[1];

        while (tcpClient.Connected && !tranceiverStream.IsDisposedOrDisposing && !cts.IsCancellationRequested)
        {
            try
            {
                if (tcpClient.Client.Poll(0, SelectMode.SelectRead) &&
                    await tcpClient.Client.ReceiveAsync(buffer, SocketFlags.Peek) == 0)
                {
                    break;
                }

                await cts.Token.WaitHandle.WaitAsync(livelinessSpan);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch { }
        }
    }
}
