using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transport.Quic;

/// <summary>
/// QUIC transport helper for StreamMultiplexer.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public static class QuicMultiplexer
{
    private const string DefaultAlpn = "netconduit";

    // 1-byte NetConduit protocol preface. Both endpoints must agree on this
    // byte before the mux runs framing on the QUIC stream. Bumping this value
    // is the protocol-version gate.
    private const byte ExpectedPreface = 0x01;

    /// <summary>
    /// Creates multiplexer options that connect to the specified QUIC endpoint.
    /// Supports reconnection — each call to StreamFactory creates a new QUIC connection.
    /// </summary>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="alpn">Optional application protocol name.</param>
    /// <param name="allowInsecure">Whether to allow insecure certificates (for development).</param>
    /// <returns>MultiplexerOptions configured for QUIC client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        string host,
        int port,
        string? alpn = null,
        bool allowInsecure = false)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QUIC is not supported on this platform.");

        ArgumentNullException.ThrowIfNull(host);

        return new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var applicationProtocol = new SslApplicationProtocol(alpn ?? DefaultAlpn);
                var endpoints = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                return await ConnectToAnyEndpointAsync(endpoints, port, host, applicationProtocol, allowInsecure, ct)
                    .ConfigureAwait(false);
            }
        };
    }

    private static async Task<IStreamPair> ConnectToAnyEndpointAsync(
        IPAddress[] addresses,
        int port,
        string targetHost,
        SslApplicationProtocol applicationProtocol,
        bool allowInsecure,
        CancellationToken ct)
    {
        if (addresses.Length == 0)
            throw new InvalidOperationException($"Host '{targetHost}' did not resolve to any IP addresses.");

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = addresses
            .Select(address => ConnectToEndpointAsync(new IPEndPoint(address, port), targetHost, applicationProtocol, allowInsecure, attemptCts.Token))
            .ToList();
        var failures = new List<Exception>();

        while (attempts.Count > 0)
        {
            Task<IStreamPair> completed = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(completed);

            try
            {
                IStreamPair streamPair = await completed.ConfigureAwait(false);
                await attemptCts.CancelAsync().ConfigureAwait(false);
                _ = DisposeRemainingAttemptsAsync(attempts);
                return streamPair;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        throw failures.Count == 1
            ? failures[0]
            : new AggregateException($"Unable to connect to any resolved address for host '{targetHost}'.", failures);
    }

    private static async Task<IStreamPair> ConnectToEndpointAsync(
        IPEndPoint remote,
        string targetHost,
        SslApplicationProtocol applicationProtocol,
        bool allowInsecure,
        CancellationToken ct)
    {
        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = remote,
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
            MaxInboundBidirectionalStreams = 100,
            MaxInboundUnidirectionalStreams = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                ApplicationProtocols = [applicationProtocol],
                EnabledSslProtocols = SslProtocols.Tls13,
            }
        };

        if (allowInsecure)
            clientOptions.ClientAuthenticationOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

        var connection = await QuicConnection.ConnectAsync(clientOptions, ct).ConfigureAwait(false);
        try
        {
            var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(new byte[] { ExpectedPreface }, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                return new StreamPair(stream, stream, connection);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeRemainingAttemptsAsync(IEnumerable<Task<IStreamPair>> attempts)
    {
        foreach (Task<IStreamPair> attempt in attempts)
        {
            try
            {
                IStreamPair streamPair = await attempt.ConfigureAwait(false);
                await streamPair.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
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

        var applicationProtocol = new SslApplicationProtocol(alpn ?? DefaultAlpn);

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = endPoint,
            ApplicationProtocols = [applicationProtocol],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                MaxInboundBidirectionalStreams = 100,
                MaxInboundUnidirectionalStreams = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [applicationProtocol],
                    EnabledSslProtocols = SslProtocols.Tls13,
                }
            })
        };

        var listener = await QuicListener.ListenAsync(listenerOptions, cancellationToken).ConfigureAwait(false);
        return listener;
    }

    /// <summary>
    /// Creates multiplexer options that accept a connection from the specified QUIC listener.
    /// Reconnection is not supported for server-side connections.
    /// </summary>
    /// <param name="listener">The QUIC listener to accept from.</param>
    /// <returns>MultiplexerOptions configured for QUIC server acceptance.</returns>
    public static MultiplexerOptions CreateServerOptions(QuicListener listener)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QUIC is not supported on this platform.");

        ArgumentNullException.ThrowIfNull(listener);

        // 0 = idle, 1 = accepting, 2 = accepted
        var state = 0;
        return new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var prev = Interlocked.CompareExchange(ref state, 1, 0);
                if (prev == 2)
                {
                    throw new InvalidOperationException(
                        "Server-side QUIC multiplexer does not support reconnection. " +
                        "Accept another connection from the listener to create a new multiplexer.");
                }
                if (prev == 1)
                {
                    throw new InvalidOperationException(
                        "Server-side QUIC multiplexer is already accepting a connection.");
                }

                QuicConnection? connection = null;
                QuicStream? stream = null;
                try
                {
                    connection = await listener.AcceptConnectionAsync(ct).ConfigureAwait(false);
                    stream = await connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);

                    // Read the 1-byte NetConduit preface fully and validate it.
                    // QuicStream.ReadAsync may return 0 (peer closed write side without
                    // sending the preface) or a short read; both must be rejected so the
                    // mux gets a clear protocol-mismatch error instead of an EOF or
                    // garbage on the first frame-header read.
                    var preface = new byte[1];
                    int total = 0;
                    while (total < preface.Length)
                    {
                        int n = await stream.ReadAsync(preface.AsMemory(total), ct).ConfigureAwait(false);
                        if (n == 0)
                        {
                            throw new InvalidOperationException(
                                "QUIC peer closed the inbound stream before sending the NetConduit preface byte.");
                        }
                        total += n;
                    }
                    if (preface[0] != ExpectedPreface)
                    {
                        throw new InvalidOperationException(
                            $"QUIC peer sent invalid NetConduit preface byte 0x{preface[0]:X2}; expected 0x{ExpectedPreface:X2}.");
                    }

                    Interlocked.Exchange(ref state, 2);
                    return new StreamPair(stream, stream, connection);
                }
                catch
                {
                    Interlocked.CompareExchange(ref state, 0, 1);
                    if (stream is not null)
                        await stream.DisposeAsync().ConfigureAwait(false);
                    if (connection is not null)
                        await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
        };
    }
}
