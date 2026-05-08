using NetConduit.Interfaces;

namespace NetConduit;

/// <summary>
/// Default implementation of <see cref="IStreamPair"/>.
/// </summary>
public sealed class StreamPair : IStreamPair
{
    private readonly IAsyncDisposable? _asyncOwner;
    private readonly IDisposable? _syncOwner;

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
        _asyncOwner = owner;
    }

    /// <summary>
    /// Creates a stream pair from a single bidirectional stream.
    /// </summary>
    public StreamPair(Stream stream, IAsyncDisposable? owner = null)
        : this(stream, stream, owner)
    {
    }

    /// <summary>
    /// Creates a stream pair from separate read and write streams with a disposable owner.
    /// </summary>
    public StreamPair(Stream readStream, Stream writeStream, IDisposable owner)
    {
        ReadStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
        WriteStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
        _syncOwner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>
    /// Creates a stream pair from a single bidirectional stream with a disposable owner.
    /// </summary>
    public StreamPair(Stream stream, IDisposable owner)
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

        if (_asyncOwner is not null)
        {
            await _asyncOwner.DisposeAsync();
        }

        _syncOwner?.Dispose();
    }
}
