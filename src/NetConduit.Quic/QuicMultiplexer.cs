using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using NetConduit;

namespace NetConduit.Quic;

/// <summary>
/// QUIC transport helper for StreamMultiplexer.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public static class QuicMultiplexer
{
    private const string DefaultAlpn = "netconduit";

    /// <summary>
    /// Connects to a QUIC endpoint and creates a multiplexer.
    /// </summary>
    public static async Task<QuicMultiplexerConnection> ConnectAsync(
        string host,
        int port,
        MultiplexerOptions? options = null,
        string? alpn = null,
        bool allowInsecure = true,
        CancellationToken cancellationToken = default)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QUIC is not supported on this platform.");

        ArgumentNullException.ThrowIfNull(host);
        var applicationProtocol = new SslApplicationProtocol(alpn ?? DefaultAlpn);
        var endpoints = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var remote = new IPEndPoint(endpoints[0], port);

        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = remote,
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            MaxInboundBidirectionalStreams = 100,
            MaxInboundUnidirectionalStreams = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ApplicationProtocols = new List<SslApplicationProtocol> { applicationProtocol },
                EnabledSslProtocols = SslProtocols.Tls13,
            }
        };

        if (allowInsecure)
        {
            clientOptions.ClientAuthenticationOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        var connection = await QuicConnection.ConnectAsync(clientOptions, cancellationToken).ConfigureAwait(false);
        var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);

        // Send a small preface so the server observes the stream immediately
        await stream.WriteAsync(new byte[] { 0x01 }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var mux = new StreamMultiplexer(stream, stream, options);
        return new QuicMultiplexerConnection(mux, connection, stream);
    }

    /// <summary>
    /// Starts a QUIC listener on the given endpoint.
    /// </summary>
    public static async Task<QuicListener> ListenAsync(
        IPEndPoint endPoint,
        X509Certificate2 certificate,
        string? alpn = null,
        CancellationToken cancellationToken = default)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QUIC is not supported on this platform.");

        ArgumentNullException.ThrowIfNull(endPoint);
        ArgumentNullException.ThrowIfNull(certificate);

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = endPoint,
            ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol(alpn ?? DefaultAlpn) },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                MaxInboundBidirectionalStreams = 100,
                MaxInboundUnidirectionalStreams = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol(alpn ?? DefaultAlpn) },
                    EnabledSslProtocols = SslProtocols.Tls13,
                }
            })
        };

        var listener = await QuicListener.ListenAsync(listenerOptions, cancellationToken).ConfigureAwait(false);
        return listener;
    }

    /// <summary>
    /// Accepts a QUIC connection on an existing listener and creates a multiplexer.
    /// </summary>
    public static async Task<QuicMultiplexerConnection> AcceptAsync(
        QuicListener listener,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QUIC is not supported on this platform.");

        ArgumentNullException.ThrowIfNull(listener);

        var connection = await listener.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
        var stream = await connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);

        // Consume the preface byte sent by the client
        var preface = new byte[1];
        _ = await stream.ReadAsync(preface, cancellationToken).ConfigureAwait(false);

        var mux = new StreamMultiplexer(stream, stream, options);
        return new QuicMultiplexerConnection(mux, connection, stream);
    }
}
