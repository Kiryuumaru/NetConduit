using System.Collections.Concurrent;

namespace NetConduit.Internal;

/// <summary>
/// Pools pinned byte arrays by size to avoid repeated POH allocations.
/// Pinned arrays are expensive to allocate and collected only during Gen2 GC.
/// Recycling them eliminates GC pressure in high-throughput channel scenarios.
/// </summary>
internal static class SlabPool
{
    private static readonly ConcurrentDictionary<int, ConcurrentStack<byte[]>> _pools = new();

    internal static byte[] Rent(int size)
    {
        if (_pools.TryGetValue(size, out var stack) && stack.TryPop(out var slab))
        {
            return slab;
        }
        return GC.AllocateArray<byte>(size, pinned: true);
    }

    internal static void Return(byte[] slab)
    {
        var stack = _pools.GetOrAdd(slab.Length, _ => new ConcurrentStack<byte[]>());
        stack.Push(slab);
    }
}
