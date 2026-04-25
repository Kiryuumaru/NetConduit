using System.Buffers;
using System.IO.Pipelines;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Exceptions;
using NetConduit.Models;

namespace NetConduit.Internal;

internal sealed class FrameReader(MultiplexerOptions options, MultiplexerStats stats)
{
    private PipeReader? _pipeReader;

    internal void SetPipeReader(PipeReader pipeReader) => _pipeReader = pipeReader;

    internal async Task RunReadLoopAsync(IFrameDispatcher dispatcher, CancellationToken ct)
    {
        var pipeReader = _pipeReader ?? throw new InvalidOperationException("Not connected.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var readResult = await pipeReader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = readResult.Buffer;

                while (TryParseFrame(ref buffer, dispatcher, ct))
                {
                }

                pipeReader.AdvanceTo(buffer.Start, buffer.End);

                if (readResult.IsCompleted)
                    throw new EndOfStreamException("Connection closed.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                throw;
            }
            catch (Exception ex)
            {
                dispatcher.OnReadLoopError(ex);
                throw;
            }
        }

        dispatcher.OnReadLoopComplete();
    }

    internal async Task ReadSingleFrameAsync(IFrameDispatcher dispatcher, CancellationToken ct)
    {
        var pipeReader = _pipeReader ?? throw new InvalidOperationException("Not connected.");

        while (true)
        {
            var readResult = await pipeReader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = readResult.Buffer;

            if (TryParseFrame(ref buffer, dispatcher, ct))
            {
                pipeReader.AdvanceTo(buffer.Start, buffer.End);
                return;
            }

            pipeReader.AdvanceTo(buffer.Start, buffer.End);

            if (readResult.IsCompleted)
                throw new EndOfStreamException("Connection closed.");
        }
    }

    internal async ValueTask CompletePipeReaderAsync()
    {
        if (_pipeReader != null)
        {
            await _pipeReader.CompleteAsync().ConfigureAwait(false);
            _pipeReader = null;
        }
    }

    private bool TryParseFrame(ref ReadOnlySequence<byte> buffer, IFrameDispatcher dispatcher, CancellationToken ct)
    {
        if (buffer.Length < FrameHeader.Size)
            return false;

        var profiling = HotPathProfiler.IsEnabled;
        long frameStart = profiling ? HotPathProfiler.Timestamp() : 0;
        long t0;

        t0 = profiling ? HotPathProfiler.Timestamp() : 0;
        FrameHeader header;
        if (buffer.FirstSpan.Length >= FrameHeader.Size)
        {
            header = FrameHeader.Read(buffer.FirstSpan);
        }
        else
        {
            Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
            buffer.Slice(0, FrameHeader.Size).CopyTo(headerBytes);
            header = FrameHeader.Read(headerBytes);
        }
        if (profiling) HotPathProfiler.RecordHeaderParse(HotPathProfiler.Timestamp() - t0);

        if (header.Length > options.MaxFrameSize)
        {
            throw new MultiplexerException(ErrorCode.ProtocolError,
                $"Frame size {header.Length} exceeds maximum {options.MaxFrameSize}");
        }

        var totalFrameSize = FrameHeader.Size + (long)header.Length;
        if (buffer.Length < totalFrameSize)
            return false;

        stats.AddBytesReceived(FrameHeader.Size + header.Length);

        int payloadLength = (int)header.Length;

        // Data frame fast path: look up channel via dispatcher, enqueue directly
        if (header.ChannelId != ChannelIndexLimits.ControlChannel
            && header.Flags == FrameFlags.Data
            && payloadLength > 0)
        {
            t0 = profiling ? HotPathProfiler.Timestamp() : 0;
            var channel = dispatcher.LookupReadChannel(header.ChannelId);
            if (profiling) HotPathProfiler.RecordChannelLookup(HotPathProfiler.Timestamp() - t0);

            if (channel != null)
            {
                var payloadSlice = buffer.Slice(FrameHeader.Size, payloadLength);

                t0 = profiling ? HotPathProfiler.Timestamp() : 0;
                channel.EnqueueData(payloadSlice);
                if (profiling) HotPathProfiler.RecordEnqueueData(HotPathProfiler.Timestamp() - t0);
            }

            if (profiling) HotPathProfiler.RecordParseFrame(frameStart);
            buffer = buffer.Slice(totalFrameSize);
            return true;
        }

        // Control/other frames: copy payload and dispatch
        if (payloadLength > 0)
        {
            var payload = System.Buffers.ArrayPool<byte>.Shared.Rent(payloadLength);
            try
            {
                buffer.Slice(FrameHeader.Size, payloadLength).CopyTo(payload);
                dispatcher.ProcessFrame(header, payload.AsMemory(0, payloadLength), ct);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(payload);
            }
        }
        else
        {
            dispatcher.ProcessFrame(header, ReadOnlyMemory<byte>.Empty, ct);
        }

        buffer = buffer.Slice(totalFrameSize);
        return true;
    }
}
