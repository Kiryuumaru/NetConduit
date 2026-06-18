using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
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

    [Fact]
    public void CreateOptions_EmptyAlpn_ThrowsArgumentException()
    {
        if (!QuicListener.IsSupported)
            return;

        var exception = Assert.Throws<ArgumentException>(() =>
            QuicMultiplexer.CreateOptions("localhost", 12345, alpn: string.Empty));

        Assert.Equal("alpn", exception.ParamName);
    }

    [Fact(Timeout = 30000)]
    public async Task ListenAsync_EmptyAlpn_ThrowsArgumentException()
    {
        if (!QuicListener.IsSupported)
            return;

        using var cert = CreateSelfSignedCert();
        QuicListener? listener = null;

        try
        {
            var exception = await Record.ExceptionAsync(async () =>
                listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), cert, alpn: string.Empty));

            var argumentException = Assert.IsType<ArgumentException>(exception);
            Assert.Equal("alpn", argumentException.ParamName);
        }
        finally
        {
            if (listener is not null)
                await listener.DisposeAsync();
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ListenAsync_CertificateWithoutPrivateKey_ThrowsArgumentException()
    {
        if (!QuicListener.IsSupported)
            return;

        using var withPrivateKey = CreateSelfSignedCert();
        using var publicOnly = X509CertificateLoader.LoadCertificate(withPrivateKey.Export(X509ContentType.Cert));
        QuicListener? listener = null;

        try
        {
            var exception = await Record.ExceptionAsync(async () =>
                listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), publicOnly));

            var argumentException = Assert.IsType<ArgumentException>(exception);
            Assert.Equal("certificate", argumentException.ParamName);
        }
        finally
        {
            if (listener is not null)
                await listener.DisposeAsync();
        }
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

    [Fact(Timeout = 30000)]
    public async Task ServerFactory_CancelledAccept_DoesNotConsumeOneShot()
    {
        if (!QuicListener.IsSupported)
            return;

        using var cert = CreateSelfSignedCert();
        await using var listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), cert);
        var serverOptions = QuicMultiplexer.CreateServerOptions(listener);

        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await serverOptions.StreamFactory(cancelled.Token));
        }

        using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await serverOptions.StreamFactory(retryTimeout.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task ServerFactory_RejectsPeerThatClosesStreamWithoutPreface()
    {
        if (!QuicListener.IsSupported)
            return;

        using var cert = CreateSelfSignedCert();
        await using var listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), cert);
        var port = listener.LocalEndPoint.Port;
        var serverOptions = QuicMultiplexer.CreateServerOptions(listener);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = Task.Run(async () => await serverOptions.StreamFactory(cts.Token));

        await using var rogue = await ConnectRogueClientAsync(port, cts.Token);
        var rogueStream = await rogue.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cts.Token);
        rogueStream.CompleteWrites();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await serverTask);
        Assert.IsType<InvalidOperationException>(GetBaseInvalidOperation(ex));
        Assert.Contains("preface", GetBaseInvalidOperation(ex)!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerFactory_RejectsPeerThatSendsWrongPrefaceByte()
    {
        if (!QuicListener.IsSupported)
            return;

        using var cert = CreateSelfSignedCert();
        await using var listener = await QuicMultiplexer.ListenAsync(new IPEndPoint(IPAddress.Loopback, 0), cert);
        var port = listener.LocalEndPoint.Port;
        var serverOptions = QuicMultiplexer.CreateServerOptions(listener);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = Task.Run(async () => await serverOptions.StreamFactory(cts.Token));

        await using var rogue = await ConnectRogueClientAsync(port, cts.Token);
        var rogueStream = await rogue.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cts.Token);
        await rogueStream.WriteAsync(new byte[] { 0xFF }, cts.Token);
        await rogueStream.FlushAsync(cts.Token);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await serverTask);
        var ioe = GetBaseInvalidOperation(ex);
        Assert.NotNull(ioe);
        Assert.Contains("0xFF", ioe!.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<QuicConnection> ConnectRogueClientAsync(int port, CancellationToken ct)
    {
        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            MaxInboundBidirectionalStreams = 100,
            MaxInboundUnidirectionalStreams = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = [new SslApplicationProtocol("netconduit")],
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            }
        };
        return await QuicConnection.ConnectAsync(clientOptions, ct);
    }

    private static InvalidOperationException? GetBaseInvalidOperation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is InvalidOperationException ioe) return ioe;
        }
        return null;
    }
}
