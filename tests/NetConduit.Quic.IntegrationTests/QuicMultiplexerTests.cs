using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetConduit.Quic;

namespace NetConduit.Quic.IntegrationTests;

public class QuicMultiplexerTests
{
    [Fact(Timeout = 120000)]
    public async Task QuicMux_SendReceive_Works()
    {
        if (!QuicListener.IsSupported)
            return; // skip on unsupported platforms

        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cert = CreateSelfSigned("CN=NetConduit-Quic-Test");

        await using var listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, port), cert, cancellationToken: cts.Token);

        var acceptTask = QuicMultiplexer.AcceptAsync(listener, cancellationToken: cts.Token);
        var client = await QuicMultiplexer.ConnectAsync("127.0.0.1", port, allowInsecure: true, cancellationToken: cts.Token);
        await using var server = await acceptTask;
        await using (client)
        await using (server)
        {
            var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
            var clientRun = startTasks[0];
            var serverRun = startTasks[1];

            var write = await client.OpenChannelAsync(new ChannelOptions { ChannelId = "quic" }, cts.Token);
            var read = await server.AcceptChannelAsync("quic", cts.Token);

            var payload = "hello over quic"u8.ToArray();
            await write.WriteAsync(payload, cts.Token);
            await write.CloseAsync(cts.Token);

            var buffer = new byte[payload.Length];
            var readBytes = await read.ReadAsync(buffer, cts.Token);
            Assert.Equal(payload.Length, readBytes);
            Assert.Equal(payload, buffer);

            cts.Cancel();
            await Task.WhenAll(serverRun, clientRun);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task QuicMux_MultipleChannels_TransferDataConcurrently()
    {
        if (!QuicListener.IsSupported)
            return; // skip on unsupported platforms

        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cert = CreateSelfSigned("CN=NetConduit-Quic-Test");

        await using var listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, port), cert, cancellationToken: cts.Token);

        var acceptTask = QuicMultiplexer.AcceptAsync(listener, cancellationToken: cts.Token);
        var client = await QuicMultiplexer.ConnectAsync("127.0.0.1", port, allowInsecure: true, cancellationToken: cts.Token);
        await using var server = await acceptTask;
        await using (client)
        await using (server)
        {
            var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
            var clientRun = startTasks[0];
            var serverRun = startTasks[1];

            const int channelCount = 5;
            const int dataSize = 512;
            var tasks = new List<Task>();

            for (int i = 0; i < channelCount; i++)
            {
                int channelIndex = i;
                tasks.Add(Task.Run(async () =>
                {
                    var channelId = $"quic-channel-{channelIndex}";
                    var writeChannel = await client.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                    var readChannel = await server.AcceptChannelAsync(channelId, cts.Token);

                    var testData = new byte[dataSize];
                    Random.Shared.NextBytes(testData);

                    await writeChannel.WriteAsync(testData, cts.Token);
                    await writeChannel.CloseAsync(cts.Token);

                    var buffer = new byte[dataSize];
                    int totalRead = 0;
                    while (totalRead < buffer.Length)
                    {
                        int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
                        if (read == 0) break;
                        totalRead += read;
                    }

                    Assert.Equal(testData.Length, totalRead);
                    Assert.Equal(testData, buffer);
                }, cts.Token));
            }

            await Task.WhenAll(tasks);
            Assert.Equal(channelCount, client.OpenedChannelIds.Count);

            cts.Cancel();
            await Task.WhenAll(serverRun, clientRun);
        }
    }

    [Theory(Timeout = 120000)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task QuicMux_ReliableTransfer_NoCorruption(int channelCount)
    {
        if (!QuicListener.IsSupported)
            return; // skip on unsupported platforms

        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var cert = CreateSelfSigned("CN=NetConduit-Quic-Test");

        await using var listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, port), cert, cancellationToken: cts.Token);

        var acceptTask = QuicMultiplexer.AcceptAsync(listener, cancellationToken: cts.Token);
        var client = await QuicMultiplexer.ConnectAsync("127.0.0.1", port, allowInsecure: true, cancellationToken: cts.Token);
        await using var server = await acceptTask;
        await using (client)
        await using (server)
        {
            var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
            var clientRun = startTasks[0];
            var serverRun = startTasks[1];

            const int baseSize = 512;
            var tasks = new List<Task>();

            for (int i = 0; i < channelCount; i++)
            {
                int channelIndex = i;
                tasks.Add(Task.Run(async () =>
                {
                    var channelId = $"reliable-{channelIndex}";
                    var writeChannel = await client.OpenChannelAsync(new ChannelOptions { ChannelId = channelId }, cts.Token);
                    var readChannel = await server.AcceptChannelAsync(channelId, cts.Token);

                    var testData = new byte[baseSize + channelIndex];
                    Random.Shared.NextBytes(testData);

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
                }, cts.Token));
            }

            await Task.WhenAll(tasks);

            cts.Cancel();
            await Task.WhenAll(serverRun, clientRun);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task QuicMux_BidirectionalCommunication_BothSidesOpenChannels()
    {
        if (!QuicListener.IsSupported)
            return; // skip on unsupported platforms

        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cert = CreateSelfSigned("CN=NetConduit-Quic-Test");

        await using var listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, port), cert, cancellationToken: cts.Token);

        var acceptTask = QuicMultiplexer.AcceptAsync(listener, cancellationToken: cts.Token);
        var client = await QuicMultiplexer.ConnectAsync("127.0.0.1", port, allowInsecure: true, cancellationToken: cts.Token);
        await using var server = await acceptTask;
        await using (client)
        await using (server)
        {
            var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
            var clientRun = startTasks[0];
            var serverRun = startTasks[1];

            // Client opens channel to server
            var clientToServerWrite = await client.OpenChannelAsync(new ChannelOptions { ChannelId = "c2s" }, cts.Token);
            var clientToServerRead = await server.AcceptChannelAsync("c2s", cts.Token);

            // Server opens channel to client
            var serverToClientWrite = await server.OpenChannelAsync(new ChannelOptions { ChannelId = "s2c" }, cts.Token);
            var serverToClientRead = await client.AcceptChannelAsync("s2c", cts.Token);

            var clientMessage = "Hello from QUIC client"u8.ToArray();
            var serverMessage = "Hello from QUIC server"u8.ToArray();

            await clientToServerWrite.WriteAsync(clientMessage, cts.Token);
            await serverToClientWrite.WriteAsync(serverMessage, cts.Token);

            var clientBuffer = new byte[serverMessage.Length];
            var serverBuffer = new byte[clientMessage.Length];

            int clientRead = 0, serverRead = 0;
            while (clientRead < clientBuffer.Length)
            {
                int read = await serverToClientRead.ReadAsync(clientBuffer.AsMemory(clientRead), cts.Token);
                if (read == 0) break;
                clientRead += read;
            }
            while (serverRead < serverBuffer.Length)
            {
                int read = await clientToServerRead.ReadAsync(serverBuffer.AsMemory(serverRead), cts.Token);
                if (read == 0) break;
                serverRead += read;
            }

            Assert.Equal(serverMessage, clientBuffer);
            Assert.Equal(clientMessage, serverBuffer);

            cts.Cancel();
            await Task.WhenAll(serverRun, clientRun);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task QuicMux_LargeDataTransfer_TransfersCorrectly()
    {
        if (!QuicListener.IsSupported)
            return; // skip on unsupported platforms

        var port = GetFreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cert = CreateSelfSigned("CN=NetConduit-Quic-Test");

        await using var listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, port), cert, cancellationToken: cts.Token);

        var acceptTask = QuicMultiplexer.AcceptAsync(listener, cancellationToken: cts.Token);
        var client = await QuicMultiplexer.ConnectAsync("127.0.0.1", port, allowInsecure: true, cancellationToken: cts.Token);
        await using var server = await acceptTask;
        await using (client)
        await using (server)
        {
            var startTasks = await Task.WhenAll(client.StartAsync(cts.Token), server.StartAsync(cts.Token));
            var clientRun = startTasks[0];
            var serverRun = startTasks[1];

            var writeChannel = await client.OpenChannelAsync(new ChannelOptions { ChannelId = "large" }, cts.Token);
            var readChannel = await server.AcceptChannelAsync("large", cts.Token);

            // 5 MB of data
            const int dataSize = 5 * 1024 * 1024;
            var testData = new byte[dataSize];
            Random.Shared.NextBytes(testData);

            var writeTask = Task.Run(async () =>
            {
                const int chunkSize = 64 * 1024;
                for (int offset = 0; offset < dataSize; offset += chunkSize)
                {
                    int length = Math.Min(chunkSize, dataSize - offset);
                    await writeChannel.WriteAsync(testData.AsMemory(offset, length), cts.Token);
                }
                await writeChannel.CloseAsync(cts.Token);
            }, cts.Token);

            var buffer = new byte[dataSize];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await readChannel.ReadAsync(buffer.AsMemory(totalRead), cts.Token);
                if (read == 0) break;
                totalRead += read;
            }

            await writeTask;

            Assert.Equal(dataSize, totalRead);
            Assert.Equal(testData, buffer);

            cts.Cancel();
            await Task.WhenAll(serverRun, clientRun);
        }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var eku = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }; // ServerAuth + ClientAuth
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), password: null);
    }
}
