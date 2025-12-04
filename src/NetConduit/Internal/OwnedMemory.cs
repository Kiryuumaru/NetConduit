using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;

namespace NetConduit.Internal;

#if DEBUG
/// <summary>
/// DEBUG-only tracker for detecting OwnedMemory leaks.
/// </summary>
internal sealed class OwnedMemoryTracker
{
    private static long _allocationCount;
    private static long _allocationIdCounter;
    
    private readonly long _allocationId;
    private readonly string _allocationStackTrace;
    private bool _disposed;

    public static long AllocationCount => Interlocked.Read(ref _allocationCount);
    
    public OwnedMemoryTracker()
    {
        _allocationId = Interlocked.Increment(ref _allocationIdCounter);
        _allocationStackTrace = Environment.StackTrace;
        Interlocked.Increment(ref _allocationCount);
    }

    public void MarkDisposed()
    {
        if (!_disposed)
        {
            _disposed = true;
            Interlocked.Decrement(ref _allocationCount);
        }
    }

    ~OwnedMemoryTracker()
    {
        if (!_disposed)
        {
            Debug.Fail($"OwnedMemory leak detected! Allocation #{_allocationId} was not disposed.\n" +
                       $"Allocation stack trace:\n{_allocationStackTrace}");
            Interlocked.Decrement(ref _allocationCount);
        }
    }
}
#endif

/// <summary>
/// Represents owned memory with transfer semantics for zero-copy data paths.
/// The owner is responsible for calling Dispose() when done.
/// This is a struct for performance - avoid boxing!
/// </summary>
internal struct OwnedMemory : IDisposable
{
#if DEBUG
    /// <summary>
    /// Gets the current number of live OwnedMemory allocations.
    /// Only available in DEBUG builds.
    /// </summary>
    public static long AllocationCount => OwnedMemoryTracker.AllocationCount;
    
    private OwnedMemoryTracker? _tracker;
#endif

    private IMemoryOwner<byte>? _owner;
    private readonly int _length;
    private bool _disposed;

    /// <summary>
    /// Creates a new OwnedMemory by renting a buffer from the shared pool.
    /// </summary>
    /// <param name="minimumLength">Minimum required buffer length.</param>
    /// <returns>An OwnedMemory that must be disposed by the caller.</returns>
    public static OwnedMemory Rent(int minimumLength)
    {
        var owner = MemoryPool<byte>.Shared.Rent(minimumLength);
        return new OwnedMemory(owner, minimumLength);
    }

    /// <summary>
    /// Creates a new OwnedMemory wrapping an existing IMemoryOwner.
    /// Ownership is transferred - do not dispose the passed owner directly.
    /// </summary>
    /// <param name="owner">The memory owner to wrap.</param>
    /// <param name="length">The actual used length (may be less than buffer capacity).</param>
    public OwnedMemory(IMemoryOwner<byte> owner, int length)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _length = length;
        _disposed = false;

#if DEBUG
        _tracker = new OwnedMemoryTracker();
#endif
    }

    /// <summary>
    /// Gets the memory span. Throws if disposed.
    /// </summary>
    public readonly Memory<byte> Memory
    {
        get
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OwnedMemory));
            if (_owner == null)
                throw new InvalidOperationException("OwnedMemory was not properly initialized.");
            
            return _owner.Memory[.._length];
        }
    }

    /// <summary>
    /// Gets the readonly span for reading data.
    /// </summary>
    public readonly ReadOnlyMemory<byte> ReadOnlyMemory => Memory;

    /// <summary>
    /// Gets the span for direct access.
    /// </summary>
    public readonly Span<byte> Span => Memory.Span;

    /// <summary>
    /// Gets the readonly span for reading.
    /// </summary>
    public readonly ReadOnlySpan<byte> ReadOnlySpan => Memory.Span;

    /// <summary>
    /// Gets the actual data length.
    /// </summary>
    public readonly int Length => _length;

    /// <summary>
    /// Gets whether this memory has been disposed.
    /// </summary>
    public readonly bool IsDisposed => _disposed;

    /// <summary>
    /// Releases the underlying buffer back to the pool.
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        
        var owner = _owner;
        _owner = null;
        owner?.Dispose();

#if DEBUG
        _tracker?.MarkDisposed();
        _tracker = null;
#endif
    }
}
