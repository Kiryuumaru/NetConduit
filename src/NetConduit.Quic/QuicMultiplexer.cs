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
    /// Creates multiplexer options with a StreamFactory that connects to the specified QUIC endpoint.
    /// Supports reconnection - each call to StreamFactory creates a new QUIC connection.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="alpn">Optional application protocol name.</param>
    /// <param name="allowInsecure">Whether to allow insecure certificates (for development).</param>
    /// <param name="configure">Optional action to configure additional multiplexer options.</param>
    /// <returns>MultiplexerOptions configured for QUIC client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        string host,
        int port,
        string? alpn = null,
        bool allowInsecure = true,
        Action<MultiplexerOptions>? configure = null)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QUIC is not supported on this platform.");

        ArgumentNullException.ThrowIfNull(host);

        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var applicationProtocol = new SslApplicationProtocol(alpn ?? DefaultAlpn);
                var endpoints = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
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

                var connection = await QuicConnection.ConnectAsync(clientOptions, ct).ConfigureAwait(false);
                var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);

                await stream.WriteAsync(new byte[] { 0x01 }, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                return new StreamPair(stream, stream, connection);
            }
        };

        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Starts a QUIC listener on the given endpoint.
    /// </summary>
    /// <param name="endPoint">The endpoint to listen on.</param>
    /// <param name="certificate">The TLS certificate for the server.</param>
    /// <param name="alpn">Optional application protocol name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A QUIC listener that can be used with CreateServerOptions.</returns>
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
    /// Creates multiplexer options with a StreamFactory that accepts a connection from the specified QUIC listener.
    /// Reconnection is disabled by default for server-side connections.
    /// </summary>
    /// <param name="listener">The QUIC listener to accept from.</param>
    /// <param name="configure">Optional action to configure additional multiplexer options.</param>
    /// <returns>MultiplexerOptions configured for QUIC server acceptance.</returns>
    public static MultiplexerOptions CreateServerOptions(
        QuicListener listener,
        Action<MultiplexerOptions>? configure = null)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QUIC is not supported on this platform.");

        ArgumentNullException.ThrowIfNull(listener);

        var accepted = false;
        var options = new MultiplexerOptions
        {
            EnableReconnection = false,
            StreamFactory = async ct =>
            {
                if (accepted)
                {
                    throw new InvalidOperationException(
                        "Server-side QUIC multiplexer does not support reconnection. " +
                        "Accept another connection from the listener to create a new multiplexer.");
                }

                accepted = true;

                var connection = await listener.AcceptConnectionAsync(ct).ConfigureAwait(false);
                var stream = await connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);

                var preface = new byte[1];
                _ = await stream.ReadAsync(preface, ct).ConfigureAwait(false);

                return new StreamPair(stream, stream, connection);
            }
        };

        configure?.Invoke(options);
        return options;
    }
}
