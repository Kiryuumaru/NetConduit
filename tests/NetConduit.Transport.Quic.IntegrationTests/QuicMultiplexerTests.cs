using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetConduit.Transport.Quic;

namespace NetConduit.Transport.Quic.IntegrationTests;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class QuicMultiplexerTests
{
    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    [Fact(Timeout = 30000)]
    public async Task ConnectAndAccept_EstablishesConnection()
    {
        if (!QuicListener.IsSupported)
            return;

        using var cert = CreateSelfSignedCert();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        await using var listener = await QuicMultiplexer.ListenAsync(endpoint, cert);
        var actualPort = listener.LocalEndPoint.Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = QuicMultiplexer.CreateServerOptions(listener);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = QuicMultiplexer.CreateOptions("localhost", actualPort, allowInsecure: true);
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
        if (!QuicListener.IsSupported)
            return;

        using var cert = CreateSelfSignedCert();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        await using var listener = await QuicMultiplexer.ListenAsync(endpoint, cert);
        var actualPort = listener.LocalEndPoint.Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverOptions = QuicMultiplexer.CreateServerOptions(listener);
        await using var server = StreamMultiplexer.Create(serverOptions);

        var clientOptions = QuicMultiplexer.CreateOptions("localhost", actualPort, allowInsecure: true);
        await using var client = StreamMultiplexer.Create(clientOptions);

        server.Start();
        client.Start();
        await Task.WhenAll(client.WaitForReadyAsync(cts.Token), server.WaitForReadyAsync(cts.Token));

        var writeChannel = client.OpenChannel("test");
        var readChannel = await server.AcceptChannelAsync("test", cts.Token);

        var testData = "Hello, QUIC Multiplexer!"u8.ToArray();
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
}
