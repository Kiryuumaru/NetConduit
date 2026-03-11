using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NetConduit;

namespace NetConduit.Ipc;

/// <summary>
/// IPC transport helper for StreamMultiplexer (named pipes on Windows, Unix domain sockets elsewhere).
/// </summary>
public static class IpcMultiplexer
{
    /// <summary>
    /// Creates multiplexer options with a StreamFactory that connects to the specified IPC endpoint.
    /// Supports reconnection - each call to StreamFactory creates a new IPC connection.
    /// </summary>
    /// <param name="endpoint">The IPC endpoint name.</param>
    /// <param name="configure">Optional action to configure additional multiplexer options.</param>
    /// <returns>MultiplexerOptions configured for IPC client connection.</returns>
    public static MultiplexerOptions CreateOptions(
        string endpoint,
        Action<MultiplexerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                if (OperatingSystem.IsWindows())
                {
                    var port = GetDeterministicPort(endpoint);
                    var client = new TcpClient(AddressFamily.InterNetwork);
                    await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                    var stream = client.GetStream();
                    return new StreamPair(stream, client);
                }
                else
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var endPoint = new UnixDomainSocketEndPoint(endpoint);
                    await socket.ConnectAsync(endPoint, ct).ConfigureAwait(false);
                    var stream = new NetworkStream(socket, ownsSocket: true);
                    return new StreamPair(stream);
                }
            }
        };

        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Creates multiplexer options with a StreamFactory that accepts IPC connections at the specified endpoint.
    /// Reconnection is disabled by default for server-side connections.
    /// </summary>
    /// <param name="endpoint">The IPC endpoint name.</param>
    /// <param name="configure">Optional action to configure additional multiplexer options.</param>
    /// <returns>MultiplexerOptions configured for IPC server acceptance.</returns>
    public static MultiplexerOptions CreateServerOptions(
        string endpoint,
        Action<MultiplexerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var accepted = false;
        var options = new MultiplexerOptions
        {
            EnableReconnection = false,
            StreamFactory = async ct =>
            {
                if (accepted)
                {
                    throw new InvalidOperationException(
                        "Server-side IPC multiplexer does not support reconnection. " +
                        "Create a new multiplexer instance to accept another connection.");
                }

                accepted = true;

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

                    var stream = client.GetStream();
                    return new StreamPair(stream, client);
                }
                else
                {
                    if (File.Exists(endpoint))
                        File.Delete(endpoint);

                    var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var endPoint = new UnixDomainSocketEndPoint(endpoint);
                    listenSocket.Bind(endPoint);
                    listenSocket.Listen(backlog: 1);
                    var clientSocket = await listenSocket.AcceptAsync(ct).ConfigureAwait(false);
                    listenSocket.Dispose();

                    var stream = new NetworkStream(clientSocket, ownsSocket: true);
                    return new StreamPair(stream);
                }
            }
        };

        configure?.Invoke(options);
        return options;
    }

    private static int GetDeterministicPort(string endpoint)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(endpoint);
        var hash = SHA256.HashData(bytes);
        var value = (ushort)(hash[0] << 8 | hash[1]);
        return 49152 + (value % (65535 - 49152));
    }
}
