using System.IO.Pipes;
using System.Net.Sockets;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transport.Ipc;

/// <summary>
/// IPC transport helper (named pipes on Windows, Unix domain sockets elsewhere).
/// The endpoint string is used verbatim as the addressing key on both platforms,
/// so two distinct endpoint names always resolve to two distinct OS objects.
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
                    var pipe = new NamedPipeClientStream(
                        serverName: ".",
                        pipeName: endpoint,
                        direction: PipeDirection.InOut,
                        options: PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(ct).ConfigureAwait(false);
                        return new StreamPair(pipe);
                    }
                    catch
                    {
                        pipe.Dispose();
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
                        // maxNumberOfServerInstances: 1 — the endpoint name is owned by this
                        // server instance for its lifetime. A second server with the same name
                        // fails at construction with IOException("All pipe instances are busy"),
                        // which mirrors the EADDRINUSE behaviour the Unix path gets from bind(2).
                        //
                        // inBufferSize / outBufferSize: 64 KiB. The default (0) makes WriteAsync
                        // block until the peer reads, which deadlocks the multiplexer's symmetric
                        // write-then-read handshake (both sides write before either side reads).
                        // 64 KiB matches the multiplexer's MaxFrameSize so a single frame never
                        // stalls inside the kernel pipe buffer.
                        const int PipeBufferSize = 64 * 1024;
                        var pipe = new NamedPipeServerStream(
                            pipeName: endpoint,
                            direction: PipeDirection.InOut,
                            maxNumberOfServerInstances: 1,
                            transmissionMode: PipeTransmissionMode.Byte,
                            options: PipeOptions.Asynchronous,
                            inBufferSize: PipeBufferSize,
                            outBufferSize: PipeBufferSize);
                        try
                        {
                            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                            pair = new StreamPair(pipe);
                        }
                        catch
                        {
                            pipe.Dispose();
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
