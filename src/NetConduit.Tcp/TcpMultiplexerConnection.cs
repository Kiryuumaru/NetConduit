using System.Net.Sockets;

namespace NetConduit.Tcp;

/// <summary>
/// Represents a multiplexer connection over TCP, managing both the multiplexer and underlying TCP client.
/// </summary>
public sealed class TcpMultiplexerConnection : IAsyncDisposable, IDisposable
{
    private readonly TcpClient _client;
    private bool _disposed;

    internal TcpMultiplexerConnection(StreamMultiplexer multiplexer, TcpClient client)
    {
        Multiplexer = multiplexer;
        _client = client;
    }

    /// <summary>
    /// The stream multiplexer.
    /// </summary>
    public StreamMultiplexer Multiplexer { get; }

    /// <summary>
    /// The underlying TCP client.
    /// </summary>
    public TcpClient Client => _client;

    /// <summary>
    /// Whether the TCP connection is connected.
    /// </summary>
    public bool Connected => _client.Connected;

    /// <summary>
    /// The local endpoint of the TCP connection.
    /// </summary>
    public System.Net.EndPoint? LocalEndPoint => _client.Client?.LocalEndPoint;

    /// <summary>
    /// The remote endpoint of the TCP connection.
    /// </summary>
    public System.Net.EndPoint? RemoteEndPoint => _client.Client?.RemoteEndPoint;

    /// <summary>
    /// Starts the multiplexer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal Task RunAsync(CancellationToken cancellationToken = default)
        => Multiplexer.RunAsync(cancellationToken);

    /// <summary>
    /// Starts the multiplexer and waits for handshake to complete.
    /// After this method returns, the multiplexer is ready to open and accept channels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the background processing. This task completes when the multiplexer shuts down.</returns>
    /// <example>
    /// <code>
    /// var connection = await TcpMultiplexer.ConnectAsync("localhost", 5000);
    /// var runTask = await connection.StartAsync(cancellationToken);
    /// 
    /// // Connection is ready - open channels
    /// var channel = await connection.Multiplexer.OpenChannelAsync(new ChannelOptions { ChannelId = "data" });
    /// </code>
    /// </example>
    public Task<Task> StartAsync(CancellationToken cancellationToken = default)
        => Multiplexer.StartAsync(cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await Multiplexer.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Multiplexer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.Dispose();
    }
}
