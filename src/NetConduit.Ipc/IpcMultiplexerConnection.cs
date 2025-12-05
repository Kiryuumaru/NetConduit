using System.IO.Pipes;
using System.Net.Sockets;
using NetConduit;

namespace NetConduit.Ipc;

/// <summary>
/// Multiplexer connection for local IPC (loopback TCP on Windows, Unix domain sockets elsewhere).
/// </summary>
public sealed class IpcMultiplexerConnection : IStreamMultiplexerConnection, IDisposable
{
    private readonly StreamMultiplexer _multiplexer;
    private readonly Stream _stream;
    private readonly Socket? _socket;
    private readonly NamedPipeServerStream? _pipeServer;
    private bool _disposed;

    internal IpcMultiplexerConnection(StreamMultiplexer multiplexer, Stream stream, Socket? socket = null, NamedPipeServerStream? pipeServer = null)
    {
        _multiplexer = multiplexer;
        _stream = stream;
        _socket = socket;
        _pipeServer = pipeServer;
    }

    /// <summary>
    /// Underlying multiplexer managing channels and framing.
    /// </summary>
    public StreamMultiplexer Multiplexer => _multiplexer;

    /// <summary>Multiplexer options.</summary>
    public MultiplexerOptions Options => Multiplexer.Options;

    /// <summary>
    /// Raw transport stream used by the multiplexer.
    /// </summary>
    public Stream Stream => _stream;

    internal Task RunAsync(CancellationToken cancellationToken = default)
        => _multiplexer.RunAsync(cancellationToken);

    /// <summary>
    /// Starts the multiplexer processing loop.
    /// </summary>
    /// <returns>Task that completes when the multiplexer loop exits.</returns>
    public Task<Task> StartAsync(CancellationToken cancellationToken = default)
        => _multiplexer.StartAsync(cancellationToken);

    /// <summary>
    /// Opens a write channel with the provided options.
    /// </summary>
    public ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken cancellationToken = default)
        => _multiplexer.OpenChannelAsync(options, cancellationToken);

    /// <summary>
    /// Accepts a specific inbound channel by identifier.
    /// </summary>
    public ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken cancellationToken = default)
        => _multiplexer.AcceptChannelAsync(channelId, cancellationToken);

    /// <summary>
    /// Accepts all inbound channels as an async stream.
    /// </summary>
    public IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken cancellationToken = default)
        => _multiplexer.AcceptChannelsAsync(cancellationToken);

    /// <summary>
    /// Gracefully stops new channels and drains existing ones.
    /// </summary>
    public ValueTask GoAwayAsync(CancellationToken cancellationToken = default)
        => _multiplexer.GoAwayAsync(cancellationToken);

    /// <summary>
    /// Snapshot of current multiplexer statistics.
    /// </summary>
    public MultiplexerStats Stats => _multiplexer.Stats;

    /// <summary>
    /// Asynchronously disposes the multiplexer and underlying transport.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _multiplexer.DisposeAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _socket?.Dispose();
        _pipeServer?.Dispose();
    }

    /// <summary>
    /// Synchronously disposes the multiplexer and underlying transport.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _multiplexer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _socket?.Dispose();
        _pipeServer?.Dispose();
    }
}
