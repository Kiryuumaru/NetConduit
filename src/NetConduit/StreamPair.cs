namespace NetConduit;

/// <summary>
/// Default implementation of <see cref="IStreamPair"/> that properly disposes all resources.
/// </summary>
public sealed class StreamPair : IStreamPair
{
    private readonly object[]? _owners;
    private bool _disposed;

    /// <inheritdoc/>
    public Stream ReadStream { get; }

    /// <inheritdoc/>
    public Stream WriteStream { get; }

    /// <summary>
    /// Creates a StreamPair with the given streams.
    /// </summary>
    /// <param name="readStream">The stream for reading.</param>
    /// <param name="writeStream">The stream for writing.</param>
    /// <param name="owner">Optional owner to dispose (e.g., TcpClient). If null, streams are disposed directly.</param>
    public StreamPair(Stream readStream, Stream writeStream, IAsyncDisposable? owner = null)
    {
        ReadStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
        WriteStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
        _owners = owner != null ? [owner] : null;
    }

    /// <summary>
    /// Creates a StreamPair with a single stream used for both read and write.
    /// </summary>
    /// <param name="stream">The bidirectional stream.</param>
    /// <param name="owner">Optional owner to dispose. If null, stream is disposed directly.</param>
    public StreamPair(Stream stream, IAsyncDisposable? owner = null)
        : this(stream, stream, owner)
    {
    }

    /// <summary>
    /// Creates a StreamPair with the given streams and a synchronous disposable owner.
    /// </summary>
    /// <param name="readStream">The stream for reading.</param>
    /// <param name="writeStream">The stream for writing.</param>
    /// <param name="owner">Owner to dispose (e.g., TcpClient).</param>
    public StreamPair(Stream readStream, Stream writeStream, IDisposable owner)
    {
        ReadStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
        WriteStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
        _owners = [owner ?? throw new ArgumentNullException(nameof(owner))];
    }

    /// <summary>
    /// Creates a StreamPair with a single stream used for both read and write and a synchronous disposable owner.
    /// </summary>
    /// <param name="stream">The bidirectional stream.</param>
    /// <param name="owner">Owner to dispose.</param>
    public StreamPair(Stream stream, IDisposable owner)
        : this(stream, stream, owner)
    {
    }

    /// <summary>
    /// Creates a StreamPair with the given streams and multiple owners to dispose.
    /// Owners are disposed in the order provided.
    /// </summary>
    /// <param name="readStream">The stream for reading.</param>
    /// <param name="writeStream">The stream for writing.</param>
    /// <param name="owners">Owners to dispose (IAsyncDisposable or IDisposable).</param>
    public StreamPair(Stream readStream, Stream writeStream, params object[] owners)
    {
        ReadStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
        WriteStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
        _owners = owners.Length > 0 ? owners : null;
    }

    /// <summary>
    /// Creates a StreamPair with a single stream and multiple owners to dispose.
    /// </summary>
    /// <param name="stream">The bidirectional stream.</param>
    /// <param name="owners">Owners to dispose (IAsyncDisposable or IDisposable).</param>
    public StreamPair(Stream stream, params object[] owners)
        : this(stream, stream, owners)
    {
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        
        if (_owners != null)
        {
            foreach (var owner in _owners)
            {
                if (owner is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (owner is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        else
        {
            await ReadStream.DisposeAsync().ConfigureAwait(false);
            if (!ReferenceEquals(ReadStream, WriteStream))
            {
                await WriteStream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
