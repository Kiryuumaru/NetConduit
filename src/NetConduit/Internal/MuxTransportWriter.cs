using NetConduit.Models;

namespace NetConduit.Internal;

/// <summary>
/// Outbound transport-write subsystem for <see cref="StreamMultiplexer"/>.
/// Owns the writer + flusher loops which run on dedicated long-running
/// threads. The writer drains ready channels into the transport's
/// <see cref="System.IO.Stream"/>, then signals the flusher; the flusher
/// performs the actual <c>Flush()</c> syscall, decoupling batching from
/// kernel-flush latency.
/// </summary>
internal sealed class MuxTransportWriter(
    MuxConnection conn,
    CoalescingSignal readySignal,
    CoalescingSignal flushSignal,
    List<WriteChannel> readyChannels,
    object readyLock,
    MultiplexerStats stats)
{
    /// <summary>
    /// Writer loop body — drains ready channels by priority, writes their
    /// pending frames synchronously to the transport, and signals the
    /// flusher when at least one channel produced bytes. Synchronous I/O
    /// is intentional: avoids Task continuation allocations and ThreadPool
    /// hops on the hot send path.
    /// </summary>
    internal void RunWriterLoop(CancellationToken ct)
    {
        var transport = conn.Transport ?? throw new InvalidOperationException("Transport not initialized.");
        var writeStream = transport.WriteStream;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                readySignal.Wait(ct);

                WriteChannel[] snapshot;
                lock (readyLock)
                {
                    if (readyChannels.Count == 0) continue;
                    readyChannels.Sort(static (a, b) => b.Priority.CompareTo(a.Priority));
                    snapshot = readyChannels.ToArray();
                    readyChannels.Clear();
                }

                bool anyWritten = false;
                foreach (var channel in snapshot)
                {
                    Memory<byte> frames = channel.TakeReady();
                    if (frames.IsEmpty) continue;

                    writeStream.Write(frames.Span);
                    channel.MarkSent(frames.Length);
                    Interlocked.Add(ref stats._bytesSent, frames.Length);
                    anyWritten = true;
                }

                if (anyWritten)
                    flushSignal.Signal();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Flusher loop body — waits for write activity, then flushes the
    /// transport stream. On shutdown performs one final best-effort flush
    /// to push any remaining data.
    /// </summary>
    internal void RunFlusherLoop(CancellationToken ct)
    {
        var transport = conn.Transport ?? throw new InvalidOperationException("Transport not initialized.");
        var writeStream = transport.WriteStream;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                flushSignal.Wait(ct);
                writeStream.Flush();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { writeStream.Flush(); }
            catch { /* transport may already be closed */ }
        }
    }
}
