using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NetConduit.Transport.Udp;

namespace NetConduit.Transport.Udp.IntegrationTests;

public class UdpMultiplexerTests
{
    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectAndAccept_EstablishesConnection()
    {
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);
    }

    [Fact(Timeout = 30000)]
    public async Task SendsAndReceivesData()
    {
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeChannel = client.OpenChannel("test");
        var readChannel = await server.AcceptChannelAsync("test", cts.Token);

        var testData = "Hello, UDP Multiplexer!"u8.ToArray();
        await writeChannel.WriteAsync(testData, cts.Token);
        await writeChannel.CloseAsync(cts.Token);

        var buffer = new byte[testData.Length];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        Assert.Equal(testData.Length, totalRead);
        Assert.Equal(testData, buffer);
    }

    [Fact(Timeout = 30000)]
    public async Task ClientFactory_ThrowsTimeoutException_WhenNoServerAcksHello()
    {
        int port = GetAvailablePort();
        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);

        using var ctorCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await clientOptions.StreamFactory!(ctorCts.Token);
        });
    }

    [Fact(Timeout = 30000)]
    public async Task ServerFactory_CancelledAccept_DoesNotConsumeOneShot()
    {
        int port = GetAvailablePort();
        var serverOptions = UdpMultiplexer.CreateServerOptions(port);

        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await serverOptions.StreamFactory!(cancelled.Token));
        }

        using var helloClient = new UdpClient(AddressFamily.InterNetworkV6);
        helloClient.Client.DualMode = true;
        helloClient.Connect(new IPEndPoint(IPAddress.IPv6Loopback, port));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = serverOptions.StreamFactory!(cts.Token);

        var helloBytes = "NC_HELLO"u8.ToArray();
        while (!serverTask.IsCompleted && !cts.IsCancellationRequested)
        {
            await helloClient.SendAsync(helloBytes, cts.Token);
            var delayTask = Task.Delay(100, cts.Token);
            await Task.WhenAny(serverTask, delayTask);
        }

        await using var pair = await serverTask;
        Assert.NotNull(pair);
    }

    [Fact(Timeout = 30000)]
    public async Task SendsAndReceivesData_WhenAttackerInjectsTruncatedDataFrameMidStream()
    {
        // Regression for #187: a data frame whose wire-declared `len` exceeds the actual
        // datagram payload size must NOT be silently truncated-and-ACKed. The receiver
        // must drop it so the legitimate sender retransmits and the multiplexer byte
        // stream stays aligned.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = UdpMultiplexer.CreateOptions("::1", port);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeChannel = client.OpenChannel("test");
        var readChannel = await server.AcceptChannelAsync("test", cts.Token);

        // Inject a malformed data frame at the server's UDP port. seq=99 is well beyond
        // the legitimate stream's _expectedSeq cursor, so even if the frame were accepted
        // the payload would not be delivered yet — but the strict-length check should
        // reject it before that point.
        using (var attacker = new UdpClient(AddressFamily.InterNetworkV6))
        {
            attacker.Client.DualMode = true;
            attacker.Connect(new IPEndPoint(IPAddress.IPv6Loopback, port));

            var malformed = new byte[7 + 4];
            malformed[0] = 0x01; // FlagData
            BinaryPrimitives.WriteUInt32BigEndian(malformed.AsSpan(1, 4), 99u);
            BinaryPrimitives.WriteUInt16BigEndian(malformed.AsSpan(5, 2), 4096); // lying length
            await attacker.SendAsync(malformed, cts.Token);
        }

        // Legitimate traffic must still flow end-to-end after the bogus packet was dropped.
        var testData = "Hello, UDP Multiplexer!"u8.ToArray();
        await writeChannel.WriteAsync(testData, cts.Token);
        await writeChannel.CloseAsync(cts.Token);

        var buffer = new byte[testData.Length];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        Assert.Equal(testData.Length, totalRead);
        Assert.Equal(testData, buffer);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerFactory_StrayNonHelloPacket_DoesNotHijackListener()
    {
        // #303: a single non-HELLO UDP datagram arriving before NC_HELLO must
        // not latch listener.Connect() to the rogue endpoint and short-circuit
        // the handshake. The factory must discard non-HELLO bytes and keep
        // accepting until a real NC_HELLO arrives, sending NC_HELLO_ACK back
        // to the legitimate sender.
        int port = GetAvailablePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var serverOptions = UdpMultiplexer.CreateServerOptions(port);

        // Kick off the factory directly so the listener is bound before we send.
        var serverTask = serverOptions.StreamFactory!(cts.Token);

        // Send a stray non-HELLO datagram from an unrelated UDP socket. Without
        // the fix this would latch listener.Connect() to this socket's ephemeral
        // endpoint, and the legitimate HELLO below would never reach the
        // listener (delivered to a Connected UDP socket only from the locked-in
        // remote).
        using (var rogue = new UdpClient(AddressFamily.InterNetworkV6))
        {
            rogue.Client.DualMode = true;
            rogue.Connect(new IPEndPoint(IPAddress.IPv6Loopback, port));
            for (int i = 0; i < 5; i++)
            {
                await rogue.SendAsync("garbage"u8.ToArray(), cts.Token);
                await Task.Delay(20, cts.Token);
            }
        }

        // Now send the real NC_HELLO from a different UDP socket and expect
        // NC_HELLO_ACK back from the server.
        using var legit = new UdpClient(AddressFamily.InterNetworkV6);
        legit.Client.DualMode = true;
        legit.Connect(new IPEndPoint(IPAddress.IPv6Loopback, port));

        var helloBytes = "NC_HELLO"u8.ToArray();
        var ackExpected = "NC_HELLO_ACK"u8.ToArray();

        // Retransmit HELLO periodically until we either get the ACK or time out.
        var ackReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var result = await legit.ReceiveAsync(cts.Token);
                    if (result.Buffer.AsSpan().SequenceEqual(ackExpected))
                    {
                        ackReceived.TrySetResult(true);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { ackReceived.TrySetResult(false); }
        });

        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var sendTask = Task.Run(async () =>
        {
            while (!sendCts.IsCancellationRequested)
            {
                try { await legit.SendAsync(helloBytes, sendCts.Token); }
                catch (OperationCanceledException) { return; }
                try { await Task.Delay(100, sendCts.Token); }
                catch (OperationCanceledException) { return; }
            }
        });

        var got = await ackReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sendCts.Cancel();
        try { await sendTask; } catch { }

        Assert.True(got, "Legitimate client never received NC_HELLO_ACK — listener was hijacked by the stray packet.");

        // Factory task should also complete successfully (with the pair pointing
        // at the legitimate sender).
        await using var pair = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(pair);
    }
}
