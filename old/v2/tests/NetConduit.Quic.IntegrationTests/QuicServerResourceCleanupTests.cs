using System.Net;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetConduit.Models;
using NetConduit.Quic;

namespace NetConduit.Quic.IntegrationTests;

[Collection("QuicTests")]
public class QuicServerResourceCleanupTests
{
    [Fact(Timeout = 30000)]
    public async Task ServerStreamFactory_CleansUpConnectionOnReadCancellation()
    {
        if (!QuicListener.IsSupported)
            return;

        var port = GetFreePort();
        using var cert = CreateSelfSigned("CN=NetConduit-Quic-Cleanup-Test");
        using var cts = new CancellationTokenSource();

        await using var listener = await QuicMultiplexer.ListenAsync(
            new IPEndPoint(IPAddress.Loopback, port), cert, cancellationToken: CancellationToken.None);

        var serverOptions = QuicMultiplexer.CreateServerOptions(listener);

        // Connect a QUIC client that opens a stream but never sends the preface byte.
        var clientConnection = await QuicConnection.ConnectAsync(
            new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                MaxInboundBidirectionalStreams = 100,
                MaxInboundUnidirectionalStreams = 0,
                ClientAuthenticationOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = [new System.Net.Security.SslApplicationProtocol("netconduit")],
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                }
            });

        var clientStream = await clientConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
        // Deliberately do NOT send preface byte — server factory will block on ReadAsync.

        // Start the server factory; it will block waiting for the preface.
        var factoryTask = serverOptions.StreamFactory(cts.Token);

        // Give the factory time to accept connection and stream, then cancel during ReadAsync.
        await Task.Delay(500);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factoryTask);

        // If the server properly disposed the QuicConnection, the client should see a disconnect.
        // Attempt to read from the client stream — a clean server close propagates quickly.
        var readBuf = new byte[1];
        var readTask = clientStream.ReadAsync(readBuf).AsTask();
        var completed = await Task.WhenAny(readTask, Task.Delay(3000));

        Assert.True(completed == readTask,
            "Server QUIC connection was not cleaned up after factory cancellation — resource leak detected");

        await clientConnection.DisposeAsync();
    }

    private static X509Certificate2 CreateSelfSigned(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
