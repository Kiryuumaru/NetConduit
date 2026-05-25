using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transport.Ipc;

/// <summary>
/// IPC transport helper. Uses TCP loopback with an endpoint-keyed port registry
/// on Windows and Unix domain sockets elsewhere.
/// </summary>
public static class IpcMultiplexer
{
    // Per-user registry directory. %LOCALAPPDATA% scopes the registry to the current
    // user account so unrelated user sessions on the same machine cannot collide.
    private static readonly string RegistryDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetConduit",
        "ipc-endpoints");

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
                    var port = ReadEndpointPort(endpoint);
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
                        // Bind to an OS-assigned ephemeral port (port=0), then advertise the
                        // actual port through an endpoint-keyed registry file. This eliminates
                        // both the 16-bit truncated SHA-256 port hash collision (~150 names
                        // for 50% collision) AND the conflict with the kernel's own ephemeral
                        // port range, which would otherwise allow an unrelated process to
                        // grab the deterministic port first and cause a NetConduit client to
                        // silently land on it (#233).
                        //
                        // The registry file is opened FileShare.Read|DeleteOnClose with a
                        // SHA-256-hashed filename derived from the endpoint string, so:
                        //   * concurrent server bind on the same endpoint fails at file-open
                        //     (the first holder has exclusive write access)
                        //   * the file is deleted automatically when the server process exits
                        //     or disposes the registration handle
                        //   * a client cannot construct a colliding registry path because the
                        //     endpoint string IS the file identity
                        var listener = new TcpListener(IPAddress.Loopback, port: 0);
                        listener.Start();
                        var boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;

                        FileStream registry;
                        try
                        {
                            registry = OpenExclusiveRegistry(endpoint);
                        }
                        catch
                        {
                            listener.Stop();
                            throw;
                        }

                        try
                        {
                            await WritePortAsync(registry, boundPort, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            registry.Dispose();
                            listener.Stop();
                            throw;
                        }

                        TcpClient client;
                        try
                        {
                            client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            // #403: AcceptTcpClientAsync cancellation/failure
                            // leaves the registry FileStream open and the
                            // endpoint exclusively locked until process exit
                            // (DeleteOnClose only fires on dispose, not GC).
                            // A second server bind on the same endpoint then
                            // fails until the original process terminates,
                            // because the registry file is held under
                            // FileShare.Read | DeleteOnClose. Explicit
                            // dispose releases the lock immediately.
                            registry.Dispose();
                            throw;
                        }
                        finally
                        {
                            listener.Stop();
                        }

                        try
                        {
                            var stream = client.GetStream();
                            pair = new StreamPair(stream, new RegistryHandle(client, registry));
                        }
                        catch
                        {
                            client.Dispose();
                            registry.Dispose();
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

    private static FileStream OpenExclusiveRegistry(string endpoint)
    {
        Directory.CreateDirectory(RegistryDirectory);
        var path = GetRegistryPath(endpoint);
        // FileShare.Read lets a client read the port concurrently; the server holds the
        // only writable handle. FileOptions.DeleteOnClose removes the file when this
        // handle closes, so a crash or normal dispose both clean up the advertisement.
        return new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64,
            FileOptions.DeleteOnClose);
    }

    private static async Task WritePortAsync(FileStream registry, int port, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await registry.WriteAsync(bytes, ct).ConfigureAwait(false);
        await registry.FlushAsync(ct).ConfigureAwait(false);
    }

    private static int ReadEndpointPort(string endpoint)
    {
        var path = GetRegistryPath(endpoint);
        string content;
        try
        {
            // FileShare.Read|Write|Delete matches the share intent of the server's
            // FileShare.Read-with-DeleteOnClose handle and tolerates the file being
            // pending-delete at the moment we read (the server may have just shut
            // down). Read-only access keeps us off the writer's exclusive lock.
            using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.ASCII);
            content = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            throw new SocketException((int)SocketError.ConnectionRefused);
        }
        catch (DirectoryNotFoundException)
        {
            throw new SocketException((int)SocketError.ConnectionRefused);
        }

        if (!int.TryParse(content, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port is <= 0 or > 65535)
        {
            throw new InvalidDataException(
                $"IPC endpoint registry file for '{endpoint}' is malformed or truncated.");
        }

        return port;
    }

    private static string GetRegistryPath(string endpoint)
    {
        // SHA-256 hex of the endpoint string. This makes the filename a deterministic
        // function of the endpoint identity (collision-free in practice) while staying
        // within OS filename-length and character-set limits regardless of the input.
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(endpoint));
        var hex = Convert.ToHexString(hashBytes);
        return Path.Combine(RegistryDirectory, hex + ".port");
    }

    // Owns the TcpClient and the registry FileStream so the StreamPair disposes both
    // atomically. Closing the registry handle triggers DeleteOnClose, removing the
    // port advertisement at exactly the moment the server stops servicing the channel.
    private sealed class RegistryHandle(TcpClient client, FileStream registry) : IDisposable
    {
        public void Dispose()
        {
            try { client.Dispose(); } catch { }
            try { registry.Dispose(); } catch { }
        }
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
