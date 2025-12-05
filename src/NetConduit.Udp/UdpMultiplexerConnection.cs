using System.Net.Sockets;
using NetConduit;

namespace NetConduit.Udp;

/// <summary>
/// Multiplexer connection over UDP using a minimal reliable stream shim.
/// </summary>
public sealed class UdpMultiplexerConnection : IStreamMultiplexerConnection, IDisposable
{
    private readonly StreamMultiplexer _multiplexer;
    private readonly UdpClient _udpClient;
    private readonly ReliableUdpStream _stream;
    private bool _disposed;

    internal UdpMultiplexerConnection(StreamMultiplexer multiplexer, UdpClient udpClient, ReliableUdpStream stream)
    {
        _multiplexer = multiplexer;
        _udpClient = udpClient;
        _stream = stream;
    }

    /// <summary>The multiplexer.</summary>
    public StreamMultiplexer Multiplexer => _multiplexer;

    /// <summary>Multiplexer options.</summary>
    public MultiplexerOptions Options => Multiplexer.Options;

    /// <summary>The underlying UDP client.</summary>
    public UdpClient Client => _udpClient;

    /// <summary>Starts the multiplexer run loop.</summary>
    internal Task RunAsync(CancellationToken cancellationToken = default)
        => _multiplexer.RunAsync(cancellationToken);

    /// <summary>Starts the multiplexer and waits for handshake to complete.</summary>
    public Task<Task> StartAsync(CancellationToken cancellationToken = default)
        => _multiplexer.StartAsync(cancellationToken);

    /// <summary>Open a write channel.</summary>
    public ValueTask<WriteChannel> OpenChannelAsync(ChannelOptions options, CancellationToken cancellationToken = default)
        => _multiplexer.OpenChannelAsync(options, cancellationToken);

    /// <summary>Accept a specific channel.</summary>
    public ValueTask<ReadChannel> AcceptChannelAsync(string channelId, CancellationToken cancellationToken = default)
        => _multiplexer.AcceptChannelAsync(channelId, cancellationToken);

    /// <summary>Enumerate incoming channels.</summary>
    public IAsyncEnumerable<ReadChannel> AcceptChannelsAsync(CancellationToken cancellationToken = default)
        => _multiplexer.AcceptChannelsAsync(cancellationToken);

    /// <summary>Graceful shutdown.</summary>
    public ValueTask GoAwayAsync(CancellationToken cancellationToken = default)
        => _multiplexer.GoAwayAsync(cancellationToken);

    /// <summary>Multiplexer statistics.</summary>
    public MultiplexerStats Stats => Multiplexer.Stats;

    /// <summary>
    /// Asynchronously disposes the multiplexer, stream wrapper, and UDP client.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Multiplexer.DisposeAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _udpClient.Dispose();
    }

    /// <summary>
    /// Synchronously disposes the multiplexer, stream wrapper, and UDP client.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _multiplexer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stream.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _udpClient.Dispose();
    }
}
