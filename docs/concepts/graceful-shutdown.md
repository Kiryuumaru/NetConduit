# Graceful shutdown

Two operations terminate a multiplexer. Choose based on whether you want to drain channels first.

## `GoAwayAsync(ct)` — drain then close

```csharp
await mux.GoAwayAsync(ct);
```

1. Sets `IsShuttingDown = true`.
2. Sends a `Ctrl/GoAway` frame to the remote.
3. Stops accepting new outbound opens.
4. Waits up to `GoAwayTimeout` (default 30 s) for all open channels to finish their work and close.
5. Tears down the transport. The next `await using` or `DisposeAsync()` completes immediately.

If a channel hasn't closed by the timeout, it's forcibly aborted with `ChannelCloseReason.MuxDisposed`.

## `DisposeAsync()` — immediate

```csharp
await mux.DisposeAsync();
```

1. Cancels every internal loop.
2. Aborts all channels with `ChannelCloseReason.MuxDisposed`.
3. Disposes the transport.

In-flight writes may be lost. Use this for forced shutdown, error recovery, or `await using` after a previous `GoAwayAsync`.

## Typical pattern

```csharp
await using var mux = StreamMultiplexer.Create(opts);
mux.Start();
await mux.WaitForReadyAsync(ct);

// ... use mux ...

await mux.GoAwayAsync(ct);   // best-effort drain
// mux disposes at end of using block — fast because GoAway already tore everything down
```

## What channels do during GoAway

While `IsShuttingDown` is `true`:

- Existing channels accept the last writes you queued, finish flushing, and close.
- `mux.OpenChannel(...)` and `mux.AcceptChannel(...)` throw `InvalidOperationException("Cannot open new channels after GoAwayAsync.")`. Queue any final responses on channels you opened *before* calling `GoAwayAsync`. The regression guard is `tests/NetConduit.UnitTests/GoAwayTests.cs::GoAway_RejectsNewOpenChannel`.
- Inbound channels (`AcceptChannelsAsync`) stop yielding new channels once the shutdown drain completes.

The remote sees a `Ctrl/GoAway`, raises its own `Disconnected` once the close completes, and learns the reason as `DisconnectReason.GoAwayReceived`.

## Cancelling a GoAway

The `CancellationToken` you pass to `GoAwayAsync` cancels the **drain wait**, not the shutdown — once started, shutdown always completes. If you cancel, channels still drain in the background; you just stop waiting.
