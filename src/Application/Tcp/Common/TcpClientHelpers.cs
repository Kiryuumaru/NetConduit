using Application.StreamPipeline.Models;
using Application.Tcp.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Application.Tcp.Common;

internal static class TcpClientHelpers
{
    public static async Task WatchLiveliness(TcpClient tcpClient, NetworkStream networkStream, StreamTranceiver streamTranceiver, CancellationTokenSource cts, TimeSpan livelinessSpan)
    {
        byte[] buffer = new byte[1];

        while (tcpClient.Connected && !streamTranceiver.IsDisposedOrDisposing && !cts.IsCancellationRequested)
        {
            try
            {
                if (tcpClient.Client.Poll(0, SelectMode.SelectRead) &&
                    await tcpClient.Client.ReceiveAsync(buffer, SocketFlags.Peek, cts.Token) == 0)
                {
                    break;
                }

                await Task.Delay(livelinessSpan, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch { }
        }

        cts.Cancel();
        tcpClient.Close();
        tcpClient.Dispose();
        networkStream.Close();
        networkStream.Dispose();
        streamTranceiver.Dispose();
    }
}
