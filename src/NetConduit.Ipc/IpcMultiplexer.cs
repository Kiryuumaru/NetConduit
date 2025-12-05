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
    /// Connects to an IPC endpoint and creates a multiplexer.
    /// </summary>
    public static async Task<IpcMultiplexerConnection> ConnectAsync(
        string endpoint,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (OperatingSystem.IsWindows())
        {
            var port = GetDeterministicPort(endpoint);
            var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            var mux = new StreamMultiplexer(stream, stream, options);
            mux.OnError += static ex => throw ex;
            return new IpcMultiplexerConnection(mux, stream, client.Client);
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endPoint = new UnixDomainSocketEndPoint(endpoint);
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            var mux = new StreamMultiplexer(stream, stream, options);
            mux.OnError += static ex => throw ex;
            return new IpcMultiplexerConnection(mux, stream, socket);
        }
    }

    /// <summary>
    /// Accepts an IPC connection and creates a multiplexer.
    /// </summary>
    public static async Task<IpcMultiplexerConnection> AcceptAsync(
        string endpoint,
        MultiplexerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (OperatingSystem.IsWindows())
        {
            var port = GetDeterministicPort(endpoint);
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            TcpClient accepted;
            try
            {
                accepted = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                listener.Stop();
            }

            var stream = accepted.GetStream();
            var mux = new StreamMultiplexer(stream, stream, options);
            mux.OnError += static ex => throw ex;
            return new IpcMultiplexerConnection(mux, stream, accepted.Client);
        }
        else
        {
            // Unix domain socket listener
            if (File.Exists(endpoint))
                File.Delete(endpoint);

            var listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endPoint = new UnixDomainSocketEndPoint(endpoint);
            listenSocket.Bind(endPoint);
            listenSocket.Listen(backlog: 1);
            var accepted = await listenSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            listenSocket.Dispose();

            var stream = new NetworkStream(accepted, ownsSocket: true);
            var mux = new StreamMultiplexer(stream, stream, options);
            mux.OnError += static ex => throw ex;
            return new IpcMultiplexerConnection(mux, stream, accepted);
        }
    }

    private static int GetDeterministicPort(string endpoint)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(endpoint);
        var hash = SHA256.HashData(bytes);
        // Keep within dynamic/private port range 49152-65535
        var value = (ushort)(hash[0] << 8 | hash[1]);
        return 49152 + (value % (65535 - 49152));
    }
}
