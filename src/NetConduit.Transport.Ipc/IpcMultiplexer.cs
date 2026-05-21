using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transport.Ipc;

/// <summary>
/// IPC transport helper (TCP loopback on Windows, Unix domain sockets elsewhere).
/// </summary>
public static class IpcMultiplexer
{
    /// <summary>
    /// Creates multiplexer options that connect to the specified IPC endpoint.
    /// Supports reconnection — each call to StreamFactory creates a new IPC connection.
    /// </summary>
    /// <param name="endpoint">The IPC endpoint name.</param>
    /// <returns>MultiplexerOptions configured for IPC client connection.</returns>
    public static MultiplexerOptions CreateOptions(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                if (OperatingSystem.IsWindows())
                {
                    var port = GetDeterministicPort(endpoint);
                    var client = new TcpClient(AddressFamily.InterNetwork);
                    try
                    {
                        await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                        var stream = client.GetStream();
                        return new StreamPair(stream, client);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                }
                else
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        var endPoint = new UnixDomainSocketEndPoint(endpoint);
                        await socket.ConnectAsync(endPoint, ct).ConfigureAwait(false);
                        var stream = new NetworkStream(socket, ownsSocket: true);
                        return new StreamPair(stream);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            }
        };
    }

    /// <summary>
    /// Creates multiplexer options that accept an IPC connection at the specified endpoint.
    /// Reconnection is not supported for server-side connections.
    /// </summary>
    /// <param name="endpoint">The IPC endpoint name.</param>
    /// <returns>MultiplexerOptions configured for IPC server acceptance.</returns>
    public static MultiplexerOptions CreateServerOptions(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

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
                        "Server-side IPC multiplexer does not support reconnection. " +
                        "Create a new multiplexer instance to accept another connection.");
                }
                if (prev == 1)
                {
                    throw new InvalidOperationException(
                        "Server-side IPC multiplexer is already accepting a connection.");
                }

                try
                {
                    StreamPair pair;
                    if (OperatingSystem.IsWindows())
                    {
                        var port = GetDeterministicPort(endpoint);
                        var listener = new TcpListener(IPAddress.Loopback, port);
                        listener.Start();

                        TcpClient client;
                        try
                        {
                            client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            listener.Stop();
                        }

                        try
                        {
                            var stream = client.GetStream();
                            pair = new StreamPair(stream, client);
                        }
                        catch
                        {
                            client.Dispose();
                            throw;
                        }
                    }
                    else
                    {
                        EnsureUnixEndpointWritable(endpoint);

                        var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        try
                        {
                            var endPoint = new UnixDomainSocketEndPoint(endpoint);
                            listenSocket.Bind(endPoint);
                            listenSocket.Listen(backlog: 1);
                            var clientSocket = await listenSocket.AcceptAsync(ct).ConfigureAwait(false);

                            var stream = new NetworkStream(clientSocket, ownsSocket: true);
                            pair = new StreamPair(stream);
                        }
                        finally
                        {
                            listenSocket.Dispose();
                            try { File.Delete(endpoint); } catch { }
                        }
                    }

                    Interlocked.Exchange(ref state, 2);
                    return pair;
                }
                catch
                {
                    Interlocked.CompareExchange(ref state, 0, 1);
                    throw;
                }
            }
        };
    }

    private static int GetDeterministicPort(string endpoint)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(endpoint);
        var hash = SHA256.HashData(bytes);
        var value = (ushort)(hash[0] << 8 | hash[1]);
        return 49152 + (value % (65535 - 49152));
    }

    // Refuses to delete the endpoint path when it points at something the user did
    // not put there as a Unix domain socket. A bare File.Delete here would silently
    // destroy arbitrary user files when the endpoint is misconfigured.
    //
    // File.Exists on Unix explicitly checks S_IFREG, so it returns true only for
    // regular files (and symlinks resolving to them) — Unix domain sockets, FIFOs,
    // and devices all report false. That gives us the discriminator we need: if
    // the path "exists" by File.Exists, it is provably not a socket and must not
    // be deleted. For actual stale sockets, File.Exists is false and we fall
    // through; Socket.Bind will surface EADDRINUSE which the caller can act on.
    //
    // Note: a connect(2) probe cannot be used as a type discriminator here.
    // On Linux, connect(AF_UNIX, SOCK_STREAM) to a regular file returns
    // ECONNREFUSED — the same error returned for a stale socket with no live
    // listener — so the two cases are indistinguishable from the socket API.
    private static void EnsureUnixEndpointWritable(string endpoint)
    {
        if (!File.Exists(endpoint))
            return;

        throw new IOException(
            $"IPC endpoint path '{endpoint}' exists and is not a Unix domain socket; refusing to overwrite.");
    }
}
