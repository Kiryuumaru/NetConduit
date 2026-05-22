using NetConduit.Transport.Ipc;

namespace NetConduit.Transport.Ipc.IntegrationTests;

public class IpcMultiplexerTests
{
    private static string GetUniqueEndpoint()
    {
        if (OperatingSystem.IsWindows())
            return $"netconduit-test-{Guid.NewGuid():N}";
        else
            return Path.Combine(Path.GetTempPath(), $"nc-test-{Guid.NewGuid():N}.sock");
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectAndAccept_EstablishesConnection()
    {
        var endpoint = GetUniqueEndpoint();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = IpcMultiplexer.CreateServerOptions(endpoint);
        await using var server = StreamMultiplexer.Create(serverOptions);
        server.Start();

        // Allow server socket to bind before client connects
        await Task.Delay(200, cts.Token);

        var clientOptions = IpcMultiplexer.CreateOptions(endpoint);
        await using var client = StreamMultiplexer.Create(clientOptions);
        client.Start();

        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        Assert.True(client.IsConnected);
        Assert.True(server.IsConnected);
    }

    [Fact(Timeout = 30000)]
    public async Task SendsAndReceivesData()
    {
        var endpoint = GetUniqueEndpoint();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = IpcMultiplexer.CreateServerOptions(endpoint);
        await using var server = StreamMultiplexer.Create(serverOptions);
        server.Start();

        await Task.Delay(200, cts.Token);

        var clientOptions = IpcMultiplexer.CreateOptions(endpoint);
        await using var client = StreamMultiplexer.Create(clientOptions);
        client.Start();

        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeChannel = client.OpenChannel("test");
        var readChannel = await server.AcceptChannelAsync("test", cts.Token);

        var testData = "Hello, IPC Multiplexer!"u8.ToArray();
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
    public async Task MultipleChannels_TransferData()
    {
        var endpoint = GetUniqueEndpoint();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = IpcMultiplexer.CreateServerOptions(endpoint);
        await using var server = StreamMultiplexer.Create(serverOptions);
        server.Start();

        await Task.Delay(200, cts.Token);

        var clientOptions = IpcMultiplexer.CreateOptions(endpoint);
        await using var client = StreamMultiplexer.Create(clientOptions);
        client.Start();

        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        const int channelCount = 3;
        var tasks = new List<Task>();

        for (int i = 0; i < channelCount; i++)
        {
            var channelId = $"ch-{i}";
            var data = new byte[512];
            Random.Shared.NextBytes(data);

            var writeChannel = client.OpenChannel(channelId);

            tasks.Add(Task.Run(async () =>
            {
                await writeChannel.WriteAsync(data, cts.Token);
                await writeChannel.CloseAsync(cts.Token);
            }));

            tasks.Add(Task.Run(async () =>
            {
                var readChannel = await server.AcceptChannelAsync(channelId, cts.Token);
                var received = new byte[data.Length];
                int totalRead = 0;
                while (totalRead < received.Length)
                {
                    int read = await readChannel.ReadAsync(received.AsMemory(totalRead), cts.Token);
                    if (read == 0) break;
                    totalRead += read;
                }
                Assert.Equal(data.Length, totalRead);
                Assert.Equal(data, received);
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerFactory_CancelledAccept_DoesNotConsumeOneShot()
    {
        var endpoint = GetUniqueEndpoint();
        var serverOptions = IpcMultiplexer.CreateServerOptions(endpoint);

        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await serverOptions.StreamFactory(cancelled.Token));
        }

        using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await serverOptions.StreamFactory(retryTimeout.Token));

        if (!OperatingSystem.IsWindows() && File.Exists(endpoint))
            File.Delete(endpoint);
    }

    [Fact(Timeout = 30000)]
    public async Task CreateServerOptions_EndpointPathIsRegularFile_RefusesToOverwriteAndPreservesFile()
    {
        if (OperatingSystem.IsWindows())
            return; // Unix-only code path (Windows uses TCP loopback, no filesystem entry).

        var endpoint = Path.Combine(Path.GetTempPath(), $"nc-test-{Guid.NewGuid():N}.not-a-socket");
        var sentinel = "DO_NOT_DELETE_ME"u8.ToArray();
        await File.WriteAllBytesAsync(endpoint, sentinel);
        try
        {
            var options = IpcMultiplexer.CreateServerOptions(endpoint);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<IOException>(async () => await options.StreamFactory(cts.Token));

            Assert.True(File.Exists(endpoint), "Non-socket file at IPC endpoint path must not be deleted.");
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(endpoint));
        }
        finally
        {
            if (File.Exists(endpoint))
                File.Delete(endpoint);
        }
    }

    // Regression for #233: prior to the named-pipe rewrite, the Windows IPC transport
    // hashed the endpoint to a 16-bit port and could route a client targeting endpoint
    // "A" to a server bound under endpoint "B" whenever the two hashes collided.
    // Named pipes use the endpoint string verbatim, so two distinct endpoint names must
    // always resolve to two distinct servers — there is no hash, hence no collision.
    [Fact(Timeout = 30000)]
    public async Task TwoDistinctEndpoints_RouteToDistinctServers()
    {
        var endpointA = GetUniqueEndpoint();
        var endpointB = GetUniqueEndpoint();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var serverA = StreamMultiplexer.Create(IpcMultiplexer.CreateServerOptions(endpointA));
        await using var serverB = StreamMultiplexer.Create(IpcMultiplexer.CreateServerOptions(endpointB));
        serverA.Start();
        serverB.Start();

        await Task.Delay(200, cts.Token);

        await using var clientA = StreamMultiplexer.Create(IpcMultiplexer.CreateOptions(endpointA));
        await using var clientB = StreamMultiplexer.Create(IpcMultiplexer.CreateOptions(endpointB));
        clientA.Start();
        clientB.Start();

        await Task.WhenAll(
            clientA.WaitForReadyAsync(cts.Token),
            clientB.WaitForReadyAsync(cts.Token),
            serverA.WaitForReadyAsync(cts.Token),
            serverB.WaitForReadyAsync(cts.Token));

        var writeA = clientA.OpenChannel("probe");
        var writeB = clientB.OpenChannel("probe");
        var readA = await serverA.AcceptChannelAsync("probe", cts.Token);
        var readB = await serverB.AcceptChannelAsync("probe", cts.Token);

        var payloadA = "from-client-A"u8.ToArray();
        var payloadB = "from-client-B"u8.ToArray();
        await writeA.WriteAsync(payloadA, cts.Token);
        await writeB.WriteAsync(payloadB, cts.Token);
        await writeA.CloseAsync(cts.Token);
        await writeB.CloseAsync(cts.Token);

        var bufA = new byte[payloadA.Length];
        var bufB = new byte[payloadB.Length];
        int readACount = 0;
        int readBCount = 0;
        while (readACount < bufA.Length)
        {
            int n = await readA.ReadAsync(bufA.AsMemory(readACount), cts.Token);
            if (n == 0) break;
            readACount += n;
        }
        while (readBCount < bufB.Length)
        {
            int n = await readB.ReadAsync(bufB.AsMemory(readBCount), cts.Token);
            if (n == 0) break;
            readBCount += n;
        }

        Assert.Equal(payloadA, bufA);
        Assert.Equal(payloadB, bufB);
    }

    // Regression for #233 (Windows-only): two servers cannot bind the same endpoint
    // name. With the prior TCP-port-hash implementation, the second server raised
    // SocketException(EADDRINUSE). With named pipes (maxNumberOfServerInstances: 1),
    // the constructor itself throws IOException("All pipe instances are busy"). Either
    // way, the user-visible contract is "one server per endpoint name" — assert it.
    [Fact(Timeout = 30000)]
    public async Task DuplicateServerBind_OnSameEndpoint_Fails()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var endpoint = GetUniqueEndpoint();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var serverA = StreamMultiplexer.Create(IpcMultiplexer.CreateServerOptions(endpoint));
        serverA.Start();

        await Task.Delay(200, cts.Token);

        var optionsB = IpcMultiplexer.CreateServerOptions(endpoint);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await optionsB.StreamFactory(cts.Token));
    }
}
