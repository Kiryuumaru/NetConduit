using System.Buffers.Binary;
using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

/// <summary>
/// Regression for #369: <see cref="DeltaMessageTransit{T}.ReadMessageAsync"/>'s
/// deferred-drain path (added for #298) threw <see cref="EndOfStreamException"/>
/// when the peer closed mid-drain, leaking past <c>ReceiveAllAsync</c>'s
/// <see cref="OperationCanceledException"/>/<see cref="ObjectDisposedException"/>
/// catches and misrepresenting a normal peer-side disconnect as a transit
/// failure. The fix latches <c>_receiveEof</c> and lets the standard
/// <c>(null, 0)</c> return signal EOF — matching every other EOF path in
/// the file.
/// </summary>
public sealed class Issue369OversizedDrainCleanEofTests
{
    private const int MaxSize = 1024;

    [Fact(Timeout = 30000)]
    public async Task ReceiveAsync_PeerClosesMidOversizedDrain_ReturnsCleanEof()
    {
        var read = new EofCapableReadChannel();
        await using var transit = new DeltaMessageTransit<JsonObject>(
            writeChannel: null,
            readChannel: read,
            JsonContext.Default.JsonObject,
            maxMessageSize: MaxSize);

        // Step 1: feed the length prefix of an oversized frame plus only a
        // partial chunk of payload, then close the channel. Mirrors the
        // scenario in #369: peer started streaming a frame whose declared size
        // exceeds our cap, then gracefully shut down its write side.
        const int OversizedLen = MaxSize * 4;
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)OversizedLen);
        read.Append(prefix);

        var partialPayload = new byte[MaxSize]; // less than OversizedLen
        Array.Fill(partialPayload, (byte)0xAA);
        read.Append(partialPayload);

        read.SignalEof();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // First ReceiveAsync rejects the oversized prefix and schedules the
        // drain — must throw InvalidOperationException per the #298 contract.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transit.ReceiveAsync(cts.Token));

        // Second ReceiveAsync enters DrainPendingAsync. Pre-fix: after the
        // partial payload is consumed, the next ReadAsync returns 0 and
        // DrainPendingAsync throws EndOfStreamException. Post-fix: the EOF is
        // latched and ReceiveAsync returns default(T?) cleanly.
        var result = await transit.ReceiveAsync(cts.Token);
        Assert.Null(result);

        // ReceiveAllAsync MUST terminate cleanly (yield break) instead of
        // leaking EndOfStreamException to the caller.
        var collected = new List<JsonObject>();
        await foreach (var item in transit.ReceiveAllAsync(cts.Token))
        {
            collected.Add(item);
        }
        Assert.Empty(collected);
    }

    /// <summary>
    /// IReadChannel test double that supports a real EOF signal: once
    /// <see cref="SignalEof"/> is called and the buffer is drained, subsequent
    /// <see cref="ReadAsync"/> calls return 0 instead of blocking.
    /// </summary>
    private sealed class EofCapableReadChannel : IReadChannel
    {
        private readonly object _lock = new();
        private readonly List<byte> _buffer = new();
        private int _pos;
        private bool _eof;
        private TaskCompletionSource _dataAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Append(byte[] data)
        {
            lock (_lock)
            {
                _buffer.AddRange(data);
                _dataAvailable.TrySetResult();
            }
        }

        public void SignalEof()
        {
            lock (_lock)
            {
                _eof = true;
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
                    if (_eof) return 0;
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
