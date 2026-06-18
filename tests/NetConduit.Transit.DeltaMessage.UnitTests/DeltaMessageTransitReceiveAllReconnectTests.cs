using System.Buffers.Binary;
using System.Text.Json.Nodes;
using NetConduit.Enums;
using NetConduit.Events;
using NetConduit.Interfaces;
using NetConduit.Models;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

/// <summary>
/// Regression for: <see cref="DeltaMessageTransit{T}.ReceiveAllAsync"/>'s loop
/// condition was <c>IsConnected</c>, which flips false on every transient transport
/// disconnect — terminating the enumerable mid-session even when auto-reconnect was
/// configured to recover. The fix mirrors <c>MessageTransit.ReceiveAllAsync</c> by
/// using a <c>_receiveEof</c> flag that is set only on real end-of-stream.
/// </summary>
public sealed class DeltaMessageTransitReceiveAllReconnectTests
{
    [Fact(Timeout = 30000)]
    public async Task ReceiveAllAsync_TransportDisconnectMidStream_ContinuesAfterReconnect()
    {
        var read = new ScriptedReadChannel();
        await using var transit = new DeltaMessageTransit<JsonObject>(
            writeChannel: null,
            readChannel: read,
            JsonContext.Default.JsonObject);

        // Frame 1: full state.
        read.EnqueueFrame(BuildFullStateFrame(new JsonObject { ["v"] = 1 }));

        var collected = new List<JsonObject>();
        // Gate the foreach body so we can deterministically toggle IsConnected
        // BEFORE the iterator re-evaluates its while condition for iteration 2.
        var releaseAfterFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var consumer = Task.Run(async () =>
        {
            await foreach (var s in transit.ReceiveAllAsync(cts.Token))
            {
                collected.Add(s);
                if (collected.Count == 1)
                {
                    await releaseAfterFirst.Task.WaitAsync(cts.Token);
                }
                if (collected.Count >= 2) break;
            }
        }, cts.Token);

        await WaitUntilAsync(() => collected.Count == 1, cts.Token);

        // Iterator is parked inside the foreach body (awaiting releaseAfterFirst).
        // The iterator's next MoveNextAsync (and thus its while-check) has NOT
        // run yet. Toggle the channel offline to simulate a transient transport
        // disconnect, then release the body so the iterator re-evaluates `while`.
        read.SetConnected(false);
        releaseAfterFirst.SetResult();

        // Allow the iterator to wake, re-enter the while check, and (pre-fix)
        // bail out. Post-fix, it parks in ReadAsync waiting for more data.
        await Task.Delay(100, cts.Token);

        // Simulate transport reconnect: channel back online and a new frame
        // arrives. Pre-fix this is never delivered because the enumerable
        // already terminated. Post-fix the iterator is still parked and yields it.
        read.SetConnected(true);
        read.EnqueueFrame(BuildFullStateFrame(new JsonObject { ["v"] = 2 }));

        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, collected.Count);
        Assert.Equal(1, collected[0]["v"]!.GetValue<int>());
        Assert.Equal(2, collected[1]["v"]!.GetValue<int>());
    }

    [Fact(Timeout = 30000)]
    public async Task ReceiveAllAsync_ReadReturnsZero_TerminatesViaEof()
    {
        var read = new ScriptedReadChannel();
        await using var transit = new DeltaMessageTransit<JsonObject>(
            writeChannel: null,
            readChannel: read,
            JsonContext.Default.JsonObject);

        read.EnqueueFrame(BuildFullStateFrame(new JsonObject { ["v"] = 1 }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var collected = new List<JsonObject>();

        var consumer = Task.Run(async () =>
        {
            await foreach (var s in transit.ReceiveAllAsync(cts.Token))
            {
                collected.Add(s);
            }
        }, cts.Token);

        await WaitUntilAsync(() => collected.Count == 1, cts.Token);

        // Real EOF (read returns 0) — the enumerable MUST terminate.
        read.SignalEof();
        await consumer;

        Assert.Single(collected);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

    private static byte[] BuildFullStateFrame(JsonObject state)
    {
        var json = System.Text.Encoding.UTF8.GetBytes(state.ToJsonString());
        var payload = new byte[1 + json.Length];
        payload[0] = 0x00;
        Buffer.BlockCopy(json, 0, payload, 1, json.Length);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        return frame;
    }

    /// <summary>
    /// IReadChannel test double whose IsConnected can be toggled and whose ReadAsync
    /// parks on a TaskCompletionSource until either a frame is enqueued or EOF is signalled.
    /// </summary>
    private sealed class ScriptedReadChannel : IReadChannel
    {
        private readonly object _lock = new();
        private readonly Queue<byte> _buffer = new();
        private TaskCompletionSource _dataAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _eof;
        private bool _connected = true;

        public void EnqueueFrame(byte[] frame)
        {
            lock (_lock)
            {
                foreach (var b in frame) _buffer.Enqueue(b);
                _dataAvailable.TrySetResult();
            }
        }

        public void SetConnected(bool value)
        {
            lock (_lock) { _connected = value; }
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
                    if (_buffer.Count > 0)
                    {
                        var take = Math.Min(buffer.Length, _buffer.Count);
                        for (var i = 0; i < take; i++)
                            buffer.Span[i] = _buffer.Dequeue();
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
        public bool IsConnected
        {
            get { lock (_lock) { return _connected; } }
        }
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
