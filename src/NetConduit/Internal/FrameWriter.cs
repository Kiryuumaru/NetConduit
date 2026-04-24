using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Models;

namespace NetConduit.Internal;

internal sealed class FrameWriter(MultiplexerOptions options, MultiplexerStats stats)
{
    private Pipe? _pipe;
    private readonly object _writeLock = new();
    private readonly SemaphoreSlim _streamLock = new(1, 1);
    private readonly SemaphoreSlim _flushSignal = new(0, 1);
    private readonly ConcurrentQueue<ReadChannel> _pendingCreditChannels = new();
    private volatile bool _pendingFlush;
    internal long UnflushedDataBytes;
    private volatile Exception? _writeError;
    private Stream? _writeStream;

    internal void SetStream(Stream writeStream) => _writeStream = writeStream;

    internal void EnsurePipeCreated()
    {
        _pipe ??= new Pipe(new PipeOptions(
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            minimumSegmentSize: 65536));
    }

    internal void ClearWriteError() => _writeError = null;

    internal void WriteFrame(FrameHeader header, ReadOnlyMemory<byte> payload, bool forceFlush, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var combinedLength = FrameHeader.Size + payload.Length;

        lock (_writeLock)
        {
            if (_writeError != null) throw new IOException("Write pipe failed.", _writeError);
            var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");
            var span = writer.GetSpan(combinedLength);
            header.Write(span);
            payload.Span.CopyTo(span[FrameHeader.Size..]);
            writer.Advance(combinedLength);
            UnflushedDataBytes += combinedLength;

            _pendingFlush = true;
            if (options.FlushMode == FlushMode.Immediate || forceFlush)
                SignalFlush();
        }

        stats.AddBytesSent(FrameHeader.Size + payload.Length);
    }

    internal void WriteFrameDirect(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var combinedLength = FrameHeader.Size + payload.Length;

        lock (_writeLock)
        {
            var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");
            var span = writer.GetSpan(combinedLength);
            header.Write(span);
            if (!payload.IsEmpty)
                payload.Span.CopyTo(span[FrameHeader.Size..]);
            writer.Advance(combinedLength);

            _pendingFlush = true;
            if (options.FlushMode == FlushMode.Immediate)
                SignalFlush();
        }

        stats.AddBytesSent(FrameHeader.Size + payload.Length);
    }

    internal void SignalFlush()
    {
        try { _flushSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    internal void SetPendingFlush()
    {
        _pendingFlush = true;
    }

    internal void EnqueuePendingCredit(ReadChannel channel)
    {
        _pendingCreditChannels.Enqueue(channel);
    }

    internal async Task RunFlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _flushSignal.WaitAsync(options.FlushInterval, ct).ConfigureAwait(false);

                var profiling = HotPathProfiler.IsEnabled;
                long cycleStart = profiling ? HotPathProfiler.Timestamp() : 0;
                long t0;

                t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                var hasPendingGrants = !_pendingCreditChannels.IsEmpty;
                if (profiling) HotPathProfiler.RecordHasPendingGrantsScan(HotPathProfiler.Timestamp() - t0);

                if (_pendingFlush || hasPendingGrants)
                {
                    lock (_writeLock)
                    {
                        var writer = _pipe?.Writer ?? throw new InvalidOperationException("Not connected.");

                        if (hasPendingGrants)
                        {
                            t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                            WritePendingCreditGrants(writer);
                            if (profiling) HotPathProfiler.RecordWritePendingGrants(HotPathProfiler.Timestamp() - t0);
                        }

                        _pendingFlush = false;
                        UnflushedDataBytes = 0;
                        t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                        CommitPipeWriter(writer);
                        if (profiling) HotPathProfiler.RecordCommitPipeWriter(HotPathProfiler.Timestamp() - t0);
                    }

                    t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                    await DrainPipeToStreamAsync(ct).ConfigureAwait(false);
                    if (profiling) HotPathProfiler.RecordDrainPipe(HotPathProfiler.Timestamp() - t0);

                    if (profiling) HotPathProfiler.RecordFlushCycle(HotPathProfiler.Timestamp() - cycleStart);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _writeError = ex;
            }
        }

        // Final drain: flush any remaining buffered data before shutdown
        try
        {
            await ForceFlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — stream may already be broken
        }
    }

    internal async ValueTask ForceFlushAsync(CancellationToken ct)
    {
        lock (_writeLock)
        {
            var writer = _pipe?.Writer;
            if (writer == null) return;
            _pendingFlush = false;
            UnflushedDataBytes = 0;
            CommitPipeWriter(writer);
        }

        await DrainPipeToStreamAsync(ct).ConfigureAwait(false);
    }

    internal async ValueTask TryCommitAndDrainAsync(CancellationToken ct)
    {
        if (!Monitor.TryEnter(_writeLock))
            return;
        try
        {
            var writer = _pipe?.Writer;
            if (writer == null) return;
            CommitPipeWriter(writer);
        }
        finally
        {
            Monitor.Exit(_writeLock);
        }

        if (!_streamLock.Wait(0))
            return;
        try
        {
            var pipeReader = _pipe?.Reader;
            var writeStream = _writeStream;
            if (pipeReader != null && writeStream != null && pipeReader.TryRead(out var readResult))
            {
                bool consumed = false;
                try
                {
                    if (readResult.Buffer.Length > 0)
                    {
                        await WriteBufferToStreamAsync(readResult.Buffer, writeStream, ct).ConfigureAwait(false);
                    }
                    consumed = true;
                }
                finally
                {
                    if (consumed)
                        pipeReader.AdvanceTo(readResult.Buffer.End);
                    else
                        pipeReader.AdvanceTo(readResult.Buffer.Start);
                }
            }
        }
        finally
        {
            _streamLock.Release();
        }
    }

    internal async ValueTask CompletePipeAsync()
    {
        if (_pipe != null)
        {
            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
            _pipe = null;
        }
    }

    internal void Dispose()
    {
        _streamLock.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CommitPipeWriter(PipeWriter writer)
    {
        var flushTask = writer.FlushAsync(CancellationToken.None);
        flushTask.GetAwaiter().GetResult();
    }

    private async ValueTask DrainPipeToStreamAsync(CancellationToken ct)
    {
        await _streamLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pipeReader = _pipe?.Reader;
            var writeStream = _writeStream;
            if (pipeReader != null && writeStream != null && pipeReader.TryRead(out var readResult))
            {
                bool consumed = false;
                try
                {
                    if (readResult.Buffer.Length > 0)
                    {
                        await WriteBufferToStreamAsync(readResult.Buffer, writeStream, ct).ConfigureAwait(false);
                    }
                    consumed = true;
                }
                finally
                {
                    if (consumed)
                        pipeReader.AdvanceTo(readResult.Buffer.End);
                    else
                        pipeReader.AdvanceTo(readResult.Buffer.Start);
                }
            }
        }
        finally
        {
            _streamLock.Release();
        }
    }

    private static async ValueTask WriteBufferToStreamAsync(ReadOnlySequence<byte> buffer, Stream writeStream, CancellationToken ct)
    {
        var profiling = HotPathProfiler.IsEnabled;
        if (profiling) HotPathProfiler.RecordDrainSegment(buffer.IsSingleSegment, buffer.Length);

        long t0 = profiling ? HotPathProfiler.Timestamp() : 0;
        if (buffer.IsSingleSegment)
        {
            await writeStream.WriteAsync(buffer.First, ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var segment in buffer)
            {
                await writeStream.WriteAsync(segment, ct).ConfigureAwait(false);
            }
        }
        if (profiling) HotPathProfiler.RecordStreamWrite(HotPathProfiler.Timestamp() - t0);

        t0 = profiling ? HotPathProfiler.Timestamp() : 0;
        await writeStream.FlushAsync(ct).ConfigureAwait(false);
        if (profiling) HotPathProfiler.RecordStreamFlush(HotPathProfiler.Timestamp() - t0);
    }

    private void WritePendingCreditGrants(PipeWriter writer)
    {
        while (_pendingCreditChannels.TryDequeue(out var channel))
        {
            var credits = channel.DrainPendingCredits();
            if (credits == 0) continue;

            const int frameLen = FrameHeader.Size + 9;
            var span = writer.GetSpan(frameLen);
            var header = new FrameHeader(ChannelIndexLimits.ControlChannel, FrameFlags.Data, 9);
            header.Write(span);
            span[FrameHeader.Size] = (byte)ControlSubtype.CreditGrant;
            BinaryPrimitives.WriteUInt32BigEndian(span[(FrameHeader.Size + 1)..], channel.ChannelIndex);
            BinaryPrimitives.WriteUInt32BigEndian(span[(FrameHeader.Size + 5)..], credits);
            writer.Advance(frameLen);

            stats.AddBytesSent(frameLen);
        }
    }
}
