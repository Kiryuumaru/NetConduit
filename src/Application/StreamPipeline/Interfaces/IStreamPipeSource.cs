using Application.StreamPipeline.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Interfaces;

public interface IStreamPipeSource : IDisposable
{
    Task Start(IPAddress address, int port, int bufferSize, Func<TcpClient, NetworkStream, StreamPipe> tcpClientStreamFactory, CancellationToken stoppingToken);
}
