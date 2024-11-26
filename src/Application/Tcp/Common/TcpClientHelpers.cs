using Application.Tcp.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Application.StreamPipeline.Common;

namespace Application.Tcp.Common;

internal static class TcpClientHelpers
{
    public static Task WatchLiveliness(TcpClient tcpClient, NetworkStream networkStream, TranceiverStream tranceiverStream, CancellationTokenSource cts, TimeSpan livelinessSpan)
    {
        return Task.Run(() =>
        {
            Span<byte> buffer = stackalloc byte[1];

            while (tcpClient.Connected && !tranceiverStream.IsDisposedOrDisposing && !cts.IsCancellationRequested)
            {
                try
                {
                    if (tcpClient.Client.Poll(0, SelectMode.SelectRead) &&
                        tcpClient.Client.Receive(buffer, SocketFlags.Peek) == 0)
                    {
                        break;
                    }

                    cts.Token.WaitHandle.WaitOne(livelinessSpan);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch { }
            }

        }, cts.Token);
    }
}
