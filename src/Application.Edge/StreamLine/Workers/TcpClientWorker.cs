using Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.StreamLine.Workers;

internal class TcpClientWorker(ILogger<TcpClientWorker> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private readonly ILogger<TcpClientWorker> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Start(stoppingToken);
        return Task.CompletedTask;
    }

    private async void Start(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {// Set the IP address and port for the server
                string serverIP = "127.0.0.1";
                int port = 23456;

                try
                {
                    // Create a TCP client
                    TcpClient client = new TcpClient();

                    // Connect to the server
                    client.Connect(serverIP, port);
                    Console.WriteLine("Connected to server.");

                    // Get the network stream
                    NetworkStream stream = client.GetStream();

                    // Send data to server
                    string dataToSend = "Hello from client!";
                    byte[] sendData = Encoding.ASCII.GetBytes(dataToSend);
                    stream.Write(sendData, 0, sendData.Length);
                    Console.WriteLine("Sent: " + dataToSend);

                    // Receive data from server
                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    string dataReceived = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                    Console.WriteLine("Received: " + dataReceived);

                    // Close the connection
                    client.Close();
                    Console.WriteLine("Disconnected from server.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error on TCP client: {}", ex);
            }
            await TaskUtils.DelaySafe(1000, stoppingToken);
        }
    }
}
