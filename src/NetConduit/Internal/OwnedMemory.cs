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
            // Use Trace instead of Debug.Fail to avoid crashing test host on timeout
            System.Diagnostics.Trace.TraceWarning($"OwnedMemory leak detected! Allocation #{_allocationId} was not disposed.");
            Interlocked.Decrement(ref _allocationCount);
        }
    }
}
#endif

/// <summary>
/// Represents owned memory with transfer semantics for zero-copy data paths.
/// The owner is responsible for calling Dispose() when done.
/// This is a struct for performance - avoid boxing!
/// Uses ArrayPool directly to avoid the IMemoryOwner heap allocation from MemoryPool.
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

    private byte[]? _array;
    private readonly int _length;
    private bool _disposed;

    /// <summary>
    /// Creates a new OwnedMemory by renting a buffer from the shared pool.
    /// </summary>
    /// <param name="minimumLength">Minimum required buffer length.</param>
    /// <returns>An OwnedMemory that must be disposed by the caller.</returns>
    public static OwnedMemory Rent(int minimumLength)
    {
        var array = ArrayPool<byte>.Shared.Rent(minimumLength);
        return new OwnedMemory(array, minimumLength);
    }

    /// <summary>
    /// Creates a new OwnedMemory wrapping a rented byte array.
    /// Ownership is transferred - do not return the array to the pool directly.
    /// </summary>
    /// <param name="array">The rented byte array to wrap.</param>
    /// <param name="length">The actual used length (may be less than array capacity).</param>
    public OwnedMemory(byte[] array, int length)
    {
        _array = array ?? throw new ArgumentNullException(nameof(array));
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
            if (_array == null)
                throw new InvalidOperationException("OwnedMemory was not properly initialized.");
            
            return _array.AsMemory(0, _length);
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
        
        var array = _array;
        _array = null;
        if (array != null)
            ArrayPool<byte>.Shared.Return(array);

#if DEBUG
        _tracker?.MarkDisposed();
        _tracker = null;
#endif
    }
}
