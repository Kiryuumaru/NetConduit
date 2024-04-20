using Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Application.Edge.StreamLine.Workers;

internal class TcpServerWorker(ILogger<TcpServerWorker> logger, IServiceProvider serviceProvider) : BackgroundService
{
    private readonly ILogger<TcpServerWorker> _logger = logger;
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
            {
                // Set the IP address and port for the server
                IPAddress ipAddress = IPAddress.Parse("127.0.0.1");
                int port = 23456;

                // Create a TCP listener
                TcpListener listener = new TcpListener(ipAddress, port);

                try
                {
                    // Start listening for incoming connections
                    listener.Start();
                    Console.WriteLine("Server started. Waiting for connections...");

                    while (true)
                    {
                        // Accept incoming connection
                        TcpClient client = listener.AcceptTcpClient();
                        Console.WriteLine("Edge connected.");

                        // Get the network stream
                        NetworkStream stream = client.GetStream();

                        // Receive data from client
                        byte[] buffer = new byte[1024];
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        string dataReceived = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        Console.WriteLine("Received: " + dataReceived);

                        // Echo the data back to client
                        byte[] sendData = Encoding.ASCII.GetBytes(dataReceived);
                        stream.Write(sendData, 0, sendData.Length);
                        Console.WriteLine("Sent: " + dataReceived);

                        // Close the connection
                        client.Close();
                        Console.WriteLine("Edge disconnected.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                }
                finally
                {
                    // Stop listening for incoming connections
                    listener.Stop();
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
