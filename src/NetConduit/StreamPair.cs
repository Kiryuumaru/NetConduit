namespace NetConduit;

/// <summary>
/// Default implementation of <see cref="IStreamPair"/>.
/// </summary>
public sealed class StreamPair : IStreamPair
{
    private readonly IAsyncDisposable? _owner;

    /// <inheritdoc />
    public Stream ReadStream { get; }

    /// <inheritdoc />
    public Stream WriteStream { get; }

    /// <summary>
    /// Creates a stream pair from separate read and write streams.
    /// </summary>
    public StreamPair(Stream readStream, Stream writeStream, IAsyncDisposable? owner = null)
    {
        ReadStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
        WriteStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
        _owner = owner;
    }

    /// <summary>
    /// Creates a stream pair from a single bidirectional stream.
    /// </summary>
    public StreamPair(Stream stream, IAsyncDisposable? owner = null)
        : this(stream, stream, owner)
    {
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (ReadStream != WriteStream)
        {
            await ReadStream.DisposeAsync();
        }
        await WriteStream.DisposeAsync();

        if (_owner is not null)
        {
            await _owner.DisposeAsync();
        }
    }
}
