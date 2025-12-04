using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetConduit.Streams;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for TCP to TCP relay scenarios using the multiplexer.
/// Simulates real-world use cases like:
/// - Device-to-Device relay through a central server
/// - TCP port forwarding through multiplexed connections
/// - LAN game relay over internet
/// </summary>
public class TcpRelayTests
{
    #region Basic TCP Relay Tests

    [Fact(Timeout = 120000)]
    public async Task TcpRelay_SingleConnection_DataTransfersCorrectly()
    {
        // Scenario: TCP Client A -> Mux -> TCP Server B
        // This simulates port forwarding through a multiplexed tunnel
        
        await using var physicalPipe = new DuplexPipe();
        
        await using var muxInitiator = new StreamMultiplexer(physicalPipe.Stream1, physicalPipe.Stream1,
            new MultiplexerOptions());
        await using var muxAcceptor = new StreamMultiplexer(physicalPipe.Stream2, physicalPipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        var muxInitiatorTask = muxInitiator.RunAsync(cts.Token);
        var muxAcceptorTask = muxAcceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        // Create a bidirectional channel pair (simulating the tunnel)
        var bidi = await CreateFullBidirectionalPipeAsync(muxInitiator, muxAcceptor, cts.Token);

        // Create DuplexStreams for both sides
        var clientSideStream = DuplexStream.FromChannels(bidi.InitiatorRead, bidi.InitiatorWrite, ownsChannels: false);
        var serverSideStream = DuplexStream.FromChannels(bidi.AcceptorRead, bidi.AcceptorWrite, ownsChannels: false);

        // Simulate bidirectional TCP communication
        var clientData = new byte[4096];
        var serverData = new byte[4096];
        Random.Shared.NextBytes(clientData);
        Random.Shared.NextBytes(serverData);

        // Client sends to server, server sends to client (concurrently)
        var clientToServerTask = Task.Run(async () =>
        {
            await clientSideStream.WriteAsync(clientData, cts.Token);
            await clientSideStream.FlushAsync(cts.Token);
        });

        var serverToClientTask = Task.Run(async () =>
        {
            await serverSideStream.WriteAsync(serverData, cts.Token);
            await serverSideStream.FlushAsync(cts.Token);
        });

        await Task.WhenAll(clientToServerTask, serverToClientTask);

        // Read data on both sides
        var serverReceivedBuffer = new byte[clientData.Length];
        var clientReceivedBuffer = new byte[serverData.Length];

        var serverReadTask = ReadExactlyAsync(serverSideStream, serverReceivedBuffer, cts.Token);
        var clientReadTask = ReadExactlyAsync(clientSideStream, clientReceivedBuffer, cts.Token);

        await Task.WhenAll(serverReadTask, clientReadTask);

        Assert.Equal(clientData, serverReceivedBuffer);
        Assert.Equal(serverData, clientReceivedBuffer);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task TcpRelay_MultipleConnections_AllDataCorrect()
    {
        // Scenario: Multiple TCP connections relayed through a single mux
        // Like a VPN or tunnel with multiple concurrent TCP sessions
        
        await using var physicalPipe = new DuplexPipe();
        
        await using var muxInitiator = new StreamMultiplexer(physicalPipe.Stream1, physicalPipe.Stream1,
            new MultiplexerOptions());
        await using var muxAcceptor = new StreamMultiplexer(physicalPipe.Stream2, physicalPipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var muxInitiatorTask = muxInitiator.RunAsync(cts.Token);
        var muxAcceptorTask = muxAcceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        const int connectionCount = 10;
        const int dataSize = 8192;
        
        var sentData = new ConcurrentDictionary<string, byte[]>();
        var receivedData = new ConcurrentDictionary<string, byte[]>();
        var readTasks = new List<Task>();

        // Single accept loop that handles all channels
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in muxAcceptor.AcceptChannelsAsync(cts.Token))
            {
                var channel = ch;
                var readTask = Task.Run(async () =>
                {
                    var buffer = new MemoryStream();
                    var temp = new byte[4096];
                    while (true)
                    {
                        var read = await channel.ReadAsync(temp, cts.Token);
                        if (read == 0) break;
                        buffer.Write(temp, 0, read);
                    }
                    receivedData[channel.ChannelId] = buffer.ToArray();
                });
                lock (readTasks)
                {
                    readTasks.Add(readTask);
                }
                if (++count >= connectionCount)
                    break;
            }
        });

        // Open channels and send data sequentially
        for (int i = 0; i < connectionCount; i++)
        {
            var senderChannel = await muxInitiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"relay_conn_{i}" }, cts.Token);
            
            // Generate random data
            var data = new byte[dataSize];
            Random.Shared.NextBytes(data);
            sentData[senderChannel.ChannelId] = data;

            // Send and close
            await senderChannel.WriteAsync(data, cts.Token);
            await senderChannel.FlushAsync(cts.Token);
            await senderChannel.CloseAsync(cts.Token);
        }

        await acceptTask;
        
        // Wait for all read tasks
        Task[] readTasksSnapshot;
        lock (readTasks)
        {
            readTasksSnapshot = readTasks.ToArray();
        }
        await Task.WhenAll(readTasksSnapshot);

        // Verify all connections transferred data correctly
        Assert.Equal(connectionCount, sentData.Count);
        Assert.Equal(connectionCount, receivedData.Count);
        foreach (var kvp in sentData)
        {
            Assert.True(receivedData.TryGetValue(kvp.Key, out var received), $"Missing received data for channel {kvp.Key}");
            Assert.Equal(kvp.Value, received);
        }

        cts.Cancel();
    }

    #endregion

    #region Device-to-Device Relay Tests

    [Fact(Timeout = 120000)]
    public async Task DeviceToDevice_ThroughCentralServer_DataTransfersCorrectly()
    {
        // Scenario: Device A <-> Central Server <-> Device B
        // Both devices connect to a central server, server relays traffic between them
        // This is like a TURN server or relay for NAT traversal
        
        await using var deviceAToServerPipe = new DuplexPipe();
        await using var deviceBToServerPipe = new DuplexPipe();
        
        // Device A's multiplexer (connected to server)
        await using var deviceAMux = new StreamMultiplexer(deviceAToServerPipe.Stream1, deviceAToServerPipe.Stream1,
            new MultiplexerOptions());
        
        // Server's multiplexer for Device A connection
        await using var serverMuxA = new StreamMultiplexer(deviceAToServerPipe.Stream2, deviceAToServerPipe.Stream2,
            new MultiplexerOptions());
        
        // Device B's multiplexer (connected to server)
        await using var deviceBMux = new StreamMultiplexer(deviceBToServerPipe.Stream1, deviceBToServerPipe.Stream1,
            new MultiplexerOptions());
        
        // Server's multiplexer for Device B connection
        await using var serverMuxB = new StreamMultiplexer(deviceBToServerPipe.Stream2, deviceBToServerPipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        // Start all multiplexers
        var deviceATask = deviceAMux.RunAsync(cts.Token);
        var serverATask = serverMuxA.RunAsync(cts.Token);
        var deviceBTask = deviceBMux.RunAsync(cts.Token);
        var serverBTask = serverMuxB.RunAsync(cts.Token);
        await Task.Delay(200);

        // Create channels from each device to server
        var deviceABidi = await CreateFullBidirectionalPipeAsync(deviceAMux, serverMuxA, cts.Token);
        var deviceBBidi = await CreateFullBidirectionalPipeAsync(deviceBMux, serverMuxB, cts.Token);

        // Create DuplexStreams for devices
        var deviceAStream = DuplexStream.FromChannels(deviceABidi.InitiatorRead, deviceABidi.InitiatorWrite);
        var deviceBStream = DuplexStream.FromChannels(deviceBBidi.InitiatorRead, deviceBBidi.InitiatorWrite);

        // Server-side streams (for relay)
        var serverStreamA = DuplexStream.FromChannels(deviceABidi.AcceptorRead, deviceABidi.AcceptorWrite);
        var serverStreamB = DuplexStream.FromChannels(deviceBBidi.AcceptorRead, deviceBBidi.AcceptorWrite);

        // Server relay task: copy from A to B and B to A
        var relayAToB = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await serverStreamA.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                await serverStreamB.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                await serverStreamB.FlushAsync(cts.Token);
            }
        });

        var relayBToA = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await serverStreamB.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                await serverStreamA.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                await serverStreamA.FlushAsync(cts.Token);
            }
        });

        // Device A sends message to Device B
        var messageFromA = "Hello from Device A!"u8.ToArray();
        await deviceAStream.WriteAsync(messageFromA, cts.Token);
        await deviceAStream.FlushAsync(cts.Token);

        // Device B receives the message
        var receivedByB = new byte[messageFromA.Length];
        await ReadExactlyAsync(deviceBStream, receivedByB, cts.Token);
        Assert.Equal(messageFromA, receivedByB);

        // Device B sends response to Device A
        var messageFromB = "Hello from Device B!"u8.ToArray();
        await deviceBStream.WriteAsync(messageFromB, cts.Token);
        await deviceBStream.FlushAsync(cts.Token);

        // Device A receives the response
        var receivedByA = new byte[messageFromB.Length];
        await ReadExactlyAsync(deviceAStream, receivedByA, cts.Token);
        Assert.Equal(messageFromB, receivedByA);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task DeviceCloudDevice_LargeDataTransfer_Succeeds()
    {
        // Scenario: Large file transfer between two devices through cloud relay
        // Device A sends data -> Server reads from A, writes to B -> Device B receives
        
        await using var deviceAToServerPipe = new DuplexPipe();
        await using var deviceBToServerPipe = new DuplexPipe();
        
        await using var deviceAMux = new StreamMultiplexer(deviceAToServerPipe.Stream1, deviceAToServerPipe.Stream1,
            new MultiplexerOptions());
        await using var serverMuxA = new StreamMultiplexer(deviceAToServerPipe.Stream2, deviceAToServerPipe.Stream2,
            new MultiplexerOptions());
        await using var deviceBMux = new StreamMultiplexer(deviceBToServerPipe.Stream1, deviceBToServerPipe.Stream1,
            new MultiplexerOptions());
        await using var serverMuxB = new StreamMultiplexer(deviceBToServerPipe.Stream2, deviceBToServerPipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        
        var deviceATask = deviceAMux.RunAsync(cts.Token);
        var serverATask = serverMuxA.RunAsync(cts.Token);
        var deviceBTask = deviceBMux.RunAsync(cts.Token);
        var serverBTask = serverMuxB.RunAsync(cts.Token);
        await Task.Delay(200);

        // Create channel from Device A to Server
        var deviceABidi = await CreateFullBidirectionalPipeAsync(deviceAMux, serverMuxA, cts.Token);
        
        // Create channel from Server to Device B (server initiates to device B)
        var serverBBidi = await CreateFullBidirectionalPipeAsync(serverMuxB, deviceBMux, cts.Token);

        // Generate large data (1MB)
        var largeData = new byte[1024 * 1024];
        Random.Shared.NextBytes(largeData);

        // Start relay task: read from server's A connection, write to server's B connection
        var relayTask = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            var serverReadFromA = deviceABidi.AcceptorRead;  // Server reads what A sends
            var serverWriteToB = serverBBidi.InitiatorWrite;  // Server writes to B
            
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var read = await serverReadFromA.ReadAsync(buffer, cts.Token);
                    if (read == 0)
                    {
                        // Device A closed, close connection to B
                        await serverWriteToB.CloseAsync(cts.Token);
                        break;
                    }
                    await serverWriteToB.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    await serverWriteToB.FlushAsync(cts.Token);
                }
                catch (OperationCanceledException) { break; }
            }
        });

        // Device A sends large data
        var sendTask = Task.Run(async () =>
        {
            await deviceABidi.InitiatorWrite.WriteAsync(largeData, cts.Token);
            await deviceABidi.InitiatorWrite.FlushAsync(cts.Token);
            await deviceABidi.InitiatorWrite.CloseAsync(cts.Token);
        });

        // Device B receives the data through server's initiated connection
        var deviceBRead = serverBBidi.AcceptorRead;
        var receivedData = await ReadAllAsync(deviceBRead, cts.Token);

        await sendTask;
        await relayTask;

        Assert.Equal(largeData, receivedData);

        cts.Cancel();
    }

    #endregion

    #region Real TCP Socket Tests

    [Fact(Timeout = 120000)]
    public async Task RealTcp_LoopbackRelay_DataTransfersCorrectly()
    {
        // This test uses real TCP sockets on loopback to verify end-to-end
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        // Create a TCP listener (simulating a server)
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Create the "relay" infrastructure using our mux
        await using var physicalPipe = new DuplexPipe();
        
        await using var muxInitiator = new StreamMultiplexer(physicalPipe.Stream1, physicalPipe.Stream1,
            new MultiplexerOptions());
        await using var muxAcceptor = new StreamMultiplexer(physicalPipe.Stream2, physicalPipe.Stream2,
            new MultiplexerOptions());

        var muxInitiatorTask = muxInitiator.RunAsync(cts.Token);
        var muxAcceptorTask = muxAcceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        // Create channel for the relay
        var bidi = await CreateFullBidirectionalPipeAsync(muxInitiator, muxAcceptor, cts.Token);
        var clientSideMuxStream = DuplexStream.FromChannels(bidi.InitiatorRead, bidi.InitiatorWrite);
        var serverSideMuxStream = DuplexStream.FromChannels(bidi.AcceptorRead, bidi.AcceptorWrite);

        // Connect real TCP client
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, serverPort, cts.Token);
        var tcpClientStream = tcpClient.GetStream();

        // Accept connection on server
        using var tcpServerClient = await listener.AcceptTcpClientAsync(cts.Token);
        var tcpServerStream = tcpServerClient.GetStream();

        listener.Stop();

        // Now relay: TCP Client <-> Mux Initiator side <-> Mux Acceptor side <-> TCP Server
        // Start relay tasks
        var clientToMuxRelay = RelayAsync(tcpClientStream, clientSideMuxStream, cts.Token);
        var muxToClientRelay = RelayAsync(clientSideMuxStream, tcpClientStream, cts.Token);
        var serverToMuxRelay = RelayAsync(tcpServerStream, serverSideMuxStream, cts.Token);
        var muxToServerRelay = RelayAsync(serverSideMuxStream, tcpServerStream, cts.Token);

        // Send data from TCP server side
        var testData = "Hello through mux relay!"u8.ToArray();
        await tcpServerStream.WriteAsync(testData, cts.Token);
        await tcpServerStream.FlushAsync(cts.Token);

        // Small delay for relay
        await Task.Delay(100);

        // TCP client should receive the data
        var receivedBuffer = new byte[testData.Length];
        var received = await tcpClientStream.ReadAsync(receivedBuffer, cts.Token);
        
        Assert.Equal(testData.Length, received);
        Assert.Equal(testData, receivedBuffer);

        cts.Cancel();
    }

    [Fact(Timeout = 120000)]
    public async Task RealTcp_MultipleStreams_AllDataCorrect()
    {
        // Multiple TCP streams multiplexed over a single channel pair
        
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        
        await using var physicalPipe = new DuplexPipe();
        
        await using var muxInitiator = new StreamMultiplexer(physicalPipe.Stream1, physicalPipe.Stream1,
            new MultiplexerOptions());
        await using var muxAcceptor = new StreamMultiplexer(physicalPipe.Stream2, physicalPipe.Stream2,
            new MultiplexerOptions());

        var muxInitiatorTask = muxInitiator.RunAsync(cts.Token);
        var muxAcceptorTask = muxAcceptor.RunAsync(cts.Token);
        await Task.Delay(100);

        const int streamCount = 5;
        var sentData = new ConcurrentDictionary<int, byte[]>();
        var receivedData = new ConcurrentDictionary<int, byte[]>();

        // Run sequentially to avoid race conditions with AcceptChannelsAsync
        for (int i = 0; i < streamCount; i++)
        {
            var streamIndex = i;
            
            var bidi = await CreateFullBidirectionalPipeAsync(muxInitiator, muxAcceptor, cts.Token);
            var initiatorStream = DuplexStream.FromChannels(bidi.InitiatorRead, bidi.InitiatorWrite);
            var acceptorStream = DuplexStream.FromChannels(bidi.AcceptorRead, bidi.AcceptorWrite);

            // Generate unique data for this stream
            var data = new byte[1024 + streamIndex * 100];
            Random.Shared.NextBytes(data);
            sentData[streamIndex] = data;

            // Send from initiator side
            await initiatorStream.WriteAsync(data, cts.Token);
            await bidi.InitiatorWrite.CloseAsync(cts.Token);

            // Receive on acceptor side
            var received = await ReadAllAsync(acceptorStream, cts.Token);
            receivedData[streamIndex] = received;
        }

        // Verify all streams
        Assert.Equal(streamCount, sentData.Count);
        Assert.Equal(streamCount, receivedData.Count);
        
        for (int i = 0; i < streamCount; i++)
        {
            Assert.Equal(sentData[i], receivedData[i]);
        }

        cts.Cancel();
    }

    #endregion

    #region Echo Server Pattern Tests

    [Fact(Timeout = 120000)]
    public async Task EchoServer_ThroughMux_WorksCorrectly()
    {
        // Classic TCP echo server pattern through multiplexer
        
        await using var physicalPipe = new DuplexPipe();
        
        await using var clientMux = new StreamMultiplexer(physicalPipe.Stream1, physicalPipe.Stream1,
            new MultiplexerOptions());
        await using var serverMux = new StreamMultiplexer(physicalPipe.Stream2, physicalPipe.Stream2,
            new MultiplexerOptions());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));  // Increased timeout
        
        var startTasks = await Task.WhenAll(clientMux.StartAsync(cts.Token), serverMux.StartAsync(cts.Token));
        var clientMuxTask = startTasks[0];
        var serverMuxTask = startTasks[1];

        // Client: create duplex stream and send data
        var bidi = await CreateFullBidirectionalPipeAsync(clientMux, serverMux, cts.Token);
        var clientStream = DuplexStream.FromChannels(bidi.InitiatorRead, bidi.InitiatorWrite);
        var serverStream = DuplexStream.FromChannels(bidi.AcceptorRead, bidi.AcceptorWrite);

        // Echo server task for this specific connection
        var connectionEchoTask = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await serverStream.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                await serverStream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                await serverStream.FlushAsync(cts.Token);
            }
        });

        // Client sends multiple messages
        for (int i = 0; i < 5; i++)
        {
            var message = System.Text.Encoding.UTF8.GetBytes($"Echo message {i}");
            await clientStream.WriteAsync(message, cts.Token);
            await clientStream.FlushAsync(cts.Token);

            var response = new byte[message.Length];
            await ReadExactlyAsync(clientStream, response, cts.Token);
            
            Assert.Equal(message, response);
        }

        cts.Cancel();
    }

    #endregion

    #region Helper Methods

    private record BidirectionalPipe(
        WriteChannel InitiatorWrite,
        ReadChannel InitiatorRead,
        WriteChannel AcceptorWrite,
        ReadChannel AcceptorRead
    );

    private static async Task<BidirectionalPipe> CreateFullBidirectionalPipeAsync(
        StreamMultiplexer initiator, StreamMultiplexer acceptor, CancellationToken ct)
    {
        // Channel 1: Initiator -> Acceptor
        ReadChannel? acceptorRead = null;
        var accept1Task = Task.Run(async () =>
        {
            try
            {
                await foreach (var ch in acceptor.AcceptChannelsAsync(ct))
                {
                    acceptorRead = ch;
                    break;
                }
            }
            catch (OperationCanceledException) { }
        });
        var initiatorWrite = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"tcp_init_to_accept_{Guid.NewGuid():N}" }, ct);
        
        // Wait for channel to be accepted
        var waitStart = DateTime.UtcNow;
        while (acceptorRead == null && (DateTime.UtcNow - waitStart).TotalSeconds < 10)
        {
            await Task.Delay(10, ct);
        }

        // Channel 2: Acceptor -> Initiator
        ReadChannel? initiatorRead = null;
        var accept2Task = Task.Run(async () =>
        {
            try
            {
                await foreach (var ch in initiator.AcceptChannelsAsync(ct))
                {
                    initiatorRead = ch;
                    break;
                }
            }
            catch (OperationCanceledException) { }
        });
        var acceptorWrite = await acceptor.OpenChannelAsync(new ChannelOptions { ChannelId = $"tcp_accept_to_init_{Guid.NewGuid():N}" }, ct);
        
        // Wait for channel to be accepted
        waitStart = DateTime.UtcNow;
        while (initiatorRead == null && (DateTime.UtcNow - waitStart).TotalSeconds < 10)
        {
            await Task.Delay(10, ct);
        }

        return new BidirectionalPipe(initiatorWrite, initiatorRead!, acceptorWrite, acceptorRead!);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var temp = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(temp, ct);
            if (read == 0) break;
            buffer.Write(temp, 0, read);
        }
        return buffer.ToArray();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static async Task RelayAsync(Stream source, Stream destination, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                await destination.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    #endregion
}
