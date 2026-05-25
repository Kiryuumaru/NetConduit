using System.Buffers.Binary;
using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

/// <summary>
/// Regression for: <see cref="DeltaMessageTransit{T}.ReadMessageAsync"/> threw on an
/// oversized length prefix without draining the payload, leaving the read channel
/// permanently misaligned. Subsequent <c>ReceiveAsync</c> calls would parse payload bytes
/// as the next length prefix and corrupt the stream silently. The fix mirrors
/// MessageTransit's <c>_pendingDrainRemaining</c> approach.
/// </summary>
public sealed class DeltaMessageTransitOvermaxDrainTests
{
    private const int MaxSize = 1024;

    [Fact(Timeout = 30000)]
    public async Task ReceiveAsync_OversizedFrame_NextFrameStillAligned()
    {
        var read = new BufferedReadChannel();
        await using var transit = new DeltaMessageTransit<JsonObject>(
            writeChannel: null,
            readChannel: read,
            JsonContext.Default.JsonObject,
            maxMessageSize: MaxSize);

        // Frame 1: oversized payload (>MaxSize).
        var oversizedPayload = new byte[MaxSize * 4];
        Array.Fill(oversizedPayload, (byte)0xAA);
        read.Append(BuildFrameRaw(oversizedPayload));

        // Frame 2: legitimate full-state payload that follows immediately on the wire.
        var validFrame = BuildFullStateFrame(new JsonObject { ["v"] = 1 });
        read.Append(validFrame);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // First ReceiveAsync MUST throw because the frame exceeds the cap.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transit.ReceiveAsync(cts.Token));

        // Pre-fix: the next ReceiveAsync reads the first 4 bytes of the orphaned
        // payload (0xAAAAAAAA) as a length prefix, parses garbage, and throws or
        // silently corrupts state — the read channel is permanently misaligned.
        // Post-fix: the oversized payload is drained before the next prefix read,
        // so frame 2 deserializes cleanly.
        var received = await transit.ReceiveAsync(cts.Token);
        Assert.NotNull(received);
        Assert.Equal(1, received!["v"]!.GetValue<int>());
    }

    private static byte[] BuildFullStateFrame(JsonObject state)
    {
        var json = System.Text.Encoding.UTF8.GetBytes(state.ToJsonString());
        var payload = new byte[1 + json.Length];
        payload[0] = 0x00;
        Buffer.BlockCopy(json, 0, payload, 1, json.Length);
        return BuildFrameRaw(payload);
    }

    private static byte[] BuildFrameRaw(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        return frame;
    }

    /// <summary>
    /// IReadChannel test double backed by a contiguous byte buffer. ReadAsync returns
    /// up to the requested number of bytes from the current position; once exhausted it
    /// blocks (waiting for Append) so the transit's reads behave like a real stream.
    /// </summary>
    private sealed class BufferedReadChannel : IReadChannel
    {
        private readonly object _lock = new();
        private readonly List<byte> _buffer = new();
        private int _pos;
        private TaskCompletionSource _dataAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Append(byte[] data)
        {
            lock (_lock)
            {
                _buffer.AddRange(data);
                _dataAvailable.TrySetResult();
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (true)
            {
                Task wait;
                lock (_lock)
                {
                    var available = _buffer.Count - _pos;
                    if (available > 0)
                    {
                        var take = Math.Min(buffer.Length, available);
                        for (var i = 0; i < take; i++)
                            buffer.Span[i] = _buffer[_pos + i];
                        _pos += take;
                        return take;
                    }
                    if (_dataAvailable.Task.IsCompleted)
                        _dataAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _dataAvailable.Task;
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public string ChannelId => "test";
        public ChannelState State => ChannelState.Open;
        public bool IsReady => true;
        public bool IsConnected => true;
        public ChannelPriority Priority => ChannelPriority.Normal;
        public ChannelStats Stats { get; } = new();
        public ChannelCloseReason? CloseReason => null;
        public Exception? CloseException => null;

        public event EventHandler? Ready { add { } remove { } }
        public event EventHandler? Connected { add { } remove { } }
        public event EventHandler<DisconnectedEventArgs>? Disconnected { add { } remove { } }
        public event EventHandler<ChannelCloseEventArgs>? Closed { add { } remove { } }

        public Task WaitForReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask CloseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public Stream AsStream() => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
