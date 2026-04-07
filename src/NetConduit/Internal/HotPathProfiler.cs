using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NetConduit.Internal;

/// <summary>
/// Lightweight hot-path profiler. Disabled by default (zero overhead).
/// Enable with HotPathProfiler.Enable() before starting multiplexer.
/// All counters are lock-free using Interlocked operations.
/// </summary>
public static class HotPathProfiler
{
    private static volatile bool _enabled;

    // TryParseFrame breakdown
    private static long _parseFrameCount;
    private static long _parseFrameTotalTicks;
    private static long _headerParseTotalTicks;
    private static long _rentTotalTicks;
    private static long _payloadCopyTotalTicks;
    private static long _enqueueDataTotalTicks;
    private static long _channelLookupTotalTicks;

    // ReadAsync breakdown
    private static long _readAsyncCount;
    private static long _readAsyncFastPathCount;
    private static long _readAsyncSlowPathCount;
    private static long _consumeBufferTotalTicks;
    private static long _consumeBufferCopyTotalTicks;
    private static long _consumeBufferDisposeTotalTicks;
    private static long _creditGrantTotalTicks;
    private static long _creditGrantCount;

    // FlushLoop breakdown
    private static long _flushCycleCount;
    private static long _flushCycleTotalTicks;
    private static long _hasPendingGrantsScanTotalTicks;
    private static long _writePendingGrantsTotalTicks;
    private static long _commitPipeWriterTotalTicks;
    private static long _drainPipeTotalTicks;
    private static long _streamWriteTotalTicks;
    private static long _streamFlushTotalTicks;
    private static long _multiSegmentCount;
    private static long _singleSegmentCount;
    private static long _drainBytesTotal;

    // EnqueueData
    private static long _enqueueDataLockTotalTicks;

    // ArrayPool
    private static long _rentCount;
    private static long _returnCount;

    // Linked CTS
    private static long _linkedCtsCount;

    public static bool IsEnabled
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _enabled;
    }

    public static void Enable() => _enabled = true;
    public static void Disable() => _enabled = false;

    public static void Reset()
    {
        _parseFrameCount = 0;
        _parseFrameTotalTicks = 0;
        _headerParseTotalTicks = 0;
        _rentTotalTicks = 0;
        _payloadCopyTotalTicks = 0;
        _enqueueDataTotalTicks = 0;
        _channelLookupTotalTicks = 0;
        _readAsyncCount = 0;
        _readAsyncFastPathCount = 0;
        _readAsyncSlowPathCount = 0;
        _consumeBufferTotalTicks = 0;
        _consumeBufferCopyTotalTicks = 0;
        _consumeBufferDisposeTotalTicks = 0;
        _creditGrantTotalTicks = 0;
        _creditGrantCount = 0;
        _flushCycleCount = 0;
        _flushCycleTotalTicks = 0;
        _hasPendingGrantsScanTotalTicks = 0;
        _writePendingGrantsTotalTicks = 0;
        _commitPipeWriterTotalTicks = 0;
        _drainPipeTotalTicks = 0;
        _streamWriteTotalTicks = 0;
        _streamFlushTotalTicks = 0;
        _multiSegmentCount = 0;
        _singleSegmentCount = 0;
        _drainBytesTotal = 0;
        _enqueueDataLockTotalTicks = 0;
        _rentCount = 0;
        _returnCount = 0;
        _linkedCtsCount = 0;
    }

    // --- TryParseFrame probes ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Timestamp() => Stopwatch.GetTimestamp();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordParseFrame(long startTicks)
    {
        Interlocked.Increment(ref _parseFrameCount);
        Interlocked.Add(ref _parseFrameTotalTicks, Stopwatch.GetTimestamp() - startTicks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordHeaderParse(long ticks) =>
        Interlocked.Add(ref _headerParseTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordRent(long ticks)
    {
        Interlocked.Add(ref _rentTotalTicks, ticks);
        Interlocked.Increment(ref _rentCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordPayloadCopy(long ticks) =>
        Interlocked.Add(ref _payloadCopyTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordChannelLookup(long ticks) =>
        Interlocked.Add(ref _channelLookupTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordEnqueueData(long ticks) =>
        Interlocked.Add(ref _enqueueDataTotalTicks, ticks);

    // --- ReadAsync probes ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordReadAsyncFastPath() =>
        Interlocked.Increment(ref _readAsyncFastPathCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordReadAsyncSlowPath()
    {
        Interlocked.Increment(ref _readAsyncSlowPathCount);
        Interlocked.Increment(ref _linkedCtsCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordReadAsync() =>
        Interlocked.Increment(ref _readAsyncCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordConsumeBuffer(long ticks) =>
        Interlocked.Add(ref _consumeBufferTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordConsumeBufferCopy(long ticks) =>
        Interlocked.Add(ref _consumeBufferCopyTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordConsumeBufferDispose(long ticks) =>
        Interlocked.Add(ref _consumeBufferDisposeTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordCreditGrant(long ticks)
    {
        Interlocked.Add(ref _creditGrantTotalTicks, ticks);
        Interlocked.Increment(ref _creditGrantCount);
    }

    // --- FlushLoop probes ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordFlushCycle(long ticks)
    {
        Interlocked.Increment(ref _flushCycleCount);
        Interlocked.Add(ref _flushCycleTotalTicks, ticks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordHasPendingGrantsScan(long ticks) =>
        Interlocked.Add(ref _hasPendingGrantsScanTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordWritePendingGrants(long ticks) =>
        Interlocked.Add(ref _writePendingGrantsTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordCommitPipeWriter(long ticks) =>
        Interlocked.Add(ref _commitPipeWriterTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordDrainPipe(long ticks) =>
        Interlocked.Add(ref _drainPipeTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordStreamWrite(long ticks) =>
        Interlocked.Add(ref _streamWriteTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordStreamFlush(long ticks) =>
        Interlocked.Add(ref _streamFlushTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordDrainSegment(bool isSingleSegment, long bytes)
    {
        if (isSingleSegment)
            Interlocked.Increment(ref _singleSegmentCount);
        else
            Interlocked.Increment(ref _multiSegmentCount);
        Interlocked.Add(ref _drainBytesTotal, bytes);
    }

    // --- EnqueueData probes ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordEnqueueDataLock(long ticks) =>
        Interlocked.Add(ref _enqueueDataLockTotalTicks, ticks);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordReturn() =>
        Interlocked.Increment(ref _returnCount);

    // --- Report ---

    public static void PrintReport(double wallTimeSec)
    {
        static double TicksToMs(long ticks) => ticks * 1_000.0 / Stopwatch.Frequency;
        static string AvgUs(long totalTicks, long count) =>
            count > 0 ? $"{totalTicks * 1_000_000.0 / Stopwatch.Frequency / count:F1} us" : "N/A";

        Console.Error.WriteLine();
        Console.Error.WriteLine("============================================================");
        Console.Error.WriteLine("  HOT PATH PROFILER RESULTS");
        Console.Error.WriteLine("============================================================");
        Console.Error.WriteLine($"  Wall time: {wallTimeSec:F2}s");
        Console.Error.WriteLine();

        // TryParseFrame breakdown
        Console.Error.WriteLine("--- TryParseFrame (server read loop, per data frame) ---");
        Console.Error.WriteLine($"  Frames parsed:       {_parseFrameCount:N0}");
        Console.Error.WriteLine($"  Total time:          {TicksToMs(_parseFrameTotalTicks):F1} ms");
        Console.Error.WriteLine($"  Avg per frame:       {AvgUs(_parseFrameTotalTicks, _parseFrameCount)}");
        Console.Error.WriteLine($"  Breakdown:");
        Console.Error.WriteLine($"    Header parse:      {AvgUs(_headerParseTotalTicks, _parseFrameCount),10}  ({Pct(_headerParseTotalTicks, _parseFrameTotalTicks),5})");
        Console.Error.WriteLine($"    Channel lookup:    {AvgUs(_channelLookupTotalTicks, _parseFrameCount),10}  ({Pct(_channelLookupTotalTicks, _parseFrameTotalTicks),5})");
        Console.Error.WriteLine($"    ArrayPool.Rent:    {AvgUs(_rentTotalTicks, _rentCount),10}  ({Pct(_rentTotalTicks, _parseFrameTotalTicks),5})  [{_rentCount:N0} calls]");
        Console.Error.WriteLine($"    Payload copy:      {AvgUs(_payloadCopyTotalTicks, _parseFrameCount),10}  ({Pct(_payloadCopyTotalTicks, _parseFrameTotalTicks),5})");
        Console.Error.WriteLine($"    EnqueueData:       {AvgUs(_enqueueDataTotalTicks, _parseFrameCount),10}  ({Pct(_enqueueDataTotalTicks, _parseFrameTotalTicks),5})");
        Console.Error.WriteLine();

        // EnqueueData lock detail
        Console.Error.WriteLine("--- EnqueueData Lock ---");
        Console.Error.WriteLine($"  Avg lock hold time:  {AvgUs(_enqueueDataLockTotalTicks, _parseFrameCount)}");
        Console.Error.WriteLine();

        // ReadAsync breakdown
        Console.Error.WriteLine("--- ReadAsync (application read, per call) ---");
        Console.Error.WriteLine($"  Total calls:         {_readAsyncCount:N0}");
        Console.Error.WriteLine($"  Fast path (buffered):{_readAsyncFastPathCount:N0}  ({Pct(_readAsyncFastPathCount, _readAsyncCount)})");
        Console.Error.WriteLine($"  Slow path (await):   {_readAsyncSlowPathCount:N0}  ({Pct(_readAsyncSlowPathCount, _readAsyncCount)})");
        Console.Error.WriteLine($"  Linked CTS allocs:   {_linkedCtsCount:N0}");
        Console.Error.WriteLine();

        // ConsumeBuffer breakdown
        Console.Error.WriteLine("--- ConsumeBuffer (per read return) ---");
        Console.Error.WriteLine($"  Total time:          {TicksToMs(_consumeBufferTotalTicks):F1} ms");
        Console.Error.WriteLine($"  Avg per call:        {AvgUs(_consumeBufferTotalTicks, _readAsyncCount)}");
        Console.Error.WriteLine($"  Breakdown:");
        Console.Error.WriteLine($"    Data copy:         {AvgUs(_consumeBufferCopyTotalTicks, _readAsyncCount),10}  ({Pct(_consumeBufferCopyTotalTicks, _consumeBufferTotalTicks),5})");
        Console.Error.WriteLine($"    Dispose (return):  {AvgUs(_consumeBufferDisposeTotalTicks, _returnCount),10}  ({Pct(_consumeBufferDisposeTotalTicks, _consumeBufferTotalTicks),5})  [{_returnCount:N0} calls]");
        Console.Error.WriteLine($"    Credit grant:      {AvgUs(_creditGrantTotalTicks, _creditGrantCount),10}  ({Pct(_creditGrantTotalTicks, _consumeBufferTotalTicks),5})  [{_creditGrantCount:N0} grants]");
        Console.Error.WriteLine();

        // FlushLoop breakdown
        Console.Error.WriteLine("--- FlushLoop (per cycle) ---");
        Console.Error.WriteLine($"  Cycles:              {_flushCycleCount:N0}");
        Console.Error.WriteLine($"  Total time:          {TicksToMs(_flushCycleTotalTicks):F1} ms");
        Console.Error.WriteLine($"  Avg per cycle:       {AvgUs(_flushCycleTotalTicks, _flushCycleCount)}");
        Console.Error.WriteLine($"  Breakdown:");
        Console.Error.WriteLine($"    HasPendingGrants:  {AvgUs(_hasPendingGrantsScanTotalTicks, _flushCycleCount),10}  ({Pct(_hasPendingGrantsScanTotalTicks, _flushCycleTotalTicks),5})");
        Console.Error.WriteLine($"    WritePendGrants:   {AvgUs(_writePendingGrantsTotalTicks, _flushCycleCount),10}  ({Pct(_writePendingGrantsTotalTicks, _flushCycleTotalTicks),5})");
        Console.Error.WriteLine($"    CommitPipeWriter:  {AvgUs(_commitPipeWriterTotalTicks, _flushCycleCount),10}  ({Pct(_commitPipeWriterTotalTicks, _flushCycleTotalTicks),5})");
        Console.Error.WriteLine($"    DrainPipe total:   {AvgUs(_drainPipeTotalTicks, _flushCycleCount),10}  ({Pct(_drainPipeTotalTicks, _flushCycleTotalTicks),5})");
        Console.Error.WriteLine($"    Stream.WriteAsync: {AvgUs(_streamWriteTotalTicks, _flushCycleCount),10}  ({Pct(_streamWriteTotalTicks, _flushCycleTotalTicks),5})");
        Console.Error.WriteLine($"    Stream.FlushAsync: {AvgUs(_streamFlushTotalTicks, _flushCycleCount),10}  ({Pct(_streamFlushTotalTicks, _flushCycleTotalTicks),5})");
        Console.Error.WriteLine($"  Drain segments:      {_singleSegmentCount:N0} single, {_multiSegmentCount:N0} multi ({Pct(_multiSegmentCount, _singleSegmentCount + _multiSegmentCount)} multi)");
        Console.Error.WriteLine($"  Drain bytes total:   {_drainBytesTotal:N0} ({_drainBytesTotal / 1_048_576.0:F1} MB)");
        if (_flushCycleCount > 0)
            Console.Error.WriteLine($"  Avg batch size:      {_drainBytesTotal / _flushCycleCount:N0} bytes/cycle");
        Console.Error.WriteLine();

        // ArrayPool summary
        Console.Error.WriteLine("--- ArrayPool ---");
        Console.Error.WriteLine($"  Rent calls:          {_rentCount:N0}");
        Console.Error.WriteLine($"  Return calls:        {_returnCount:N0}");
        Console.Error.WriteLine($"  Avg Rent time:       {AvgUs(_rentTotalTicks, _rentCount)}");
        Console.Error.WriteLine($"  Avg Return time:     {AvgUs(_consumeBufferDisposeTotalTicks, _returnCount)}");
        Console.Error.WriteLine();
    }

    private static string Pct(long part, long total) =>
        total > 0 ? $"{100.0 * part / total:F1}%" : "N/A";
}
