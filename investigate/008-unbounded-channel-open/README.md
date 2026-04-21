# BUG: No Channel Count Limit — Unbounded Resource Consumption

## Severity: HIGH (Security)

## Summary

There is no limit on the number of channels a remote peer can open. A malicious
peer can send thousands of INIT frames, each creating a `ReadChannel` with its own
`Pipe`, `PipeReader`, `PipeWriter`, `SemaphoreSlim`, `CancellationTokenSource`,
`AdaptiveFlowControl`, and `ChannelSyncState`. There is NO throttling, rate limiting,
or maximum channel count.

## Evidence

### Code Location

[src/NetConduit/StreamMultiplexer.cs](../../src/NetConduit/StreamMultiplexer.cs) — `ProcessInitFrame`:
```csharp
private void ProcessInitFrame(uint channelIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
{
    // Only checks for GOAWAY, payload validity, and duplicate ChannelId
    // NO check for maximum channel count!

    var channel = new ReadChannel(this, channelIndex, channelId, priority, options);

    if (!_readChannelsByIndex.TryAdd(channelIndex, channel)) { ... }
    if (!_readChannelsById.TryAdd(channelId, channel)) { ... }

    // Channel is created and ACKed unconditionally
    SendAck(channelIndex, channel.GetInitialCredits(), ct);
}
```

### Resource Cost Per Channel

Each `ReadChannel` allocates:
- 1x `Pipe` (with PipeReader + PipeWriter + internal buffer segments)
- 1x `CancellationTokenSource` (with internal registration list)
- 1x `ChannelSyncState` (with potential ring buffer)
- 1x `AdaptiveFlowControl` (with window tracking state)
- 1x `ChannelStats` (with multiple counters)
- ConcurrentDictionary entries (index + string lookups)
- Accept channel queue entry

Estimated ~2-4 KB per channel in overhead. An attacker opening 1M channels
would consume **2-4 GB** of memory.

### No Throttling

- `ProcessInitFrame` processes every INIT synchronously in the read loop
- Each INIT creates a channel and sends an ACK
- There is no delay, rate limit, or backpressure on channel creation
- The accept queue is `Channel.CreateUnbounded` — no bound

## CWE Reference

- [CWE-770: Allocation of Resources Without Limits or Throttling](https://cwe.mitre.org/data/definitions/770.html)
- [CWE-400: Uncontrolled Resource Consumption](https://cwe.mitre.org/data/definitions/400.html)

## Recommended Fix

Add a configurable maximum channel count with a sensible default:

**1. Add option to `MultiplexerOptions`:**

```csharp
public class MultiplexerOptions
{
    public int MaxConcurrentChannels { get; init; } = 1000;
}
```

**2. Enforce in `ProcessInitFrame`:**

```csharp
private void ProcessInitFrame(uint channelIndex, ReadOnlyMemory<byte> payload, CancellationToken ct)
{
    if (_readChannelsByIndex.Count >= _options.MaxConcurrentChannels)
    {
        SendError(channelIndex, ErrorCode.ResourceExhausted,
            "Maximum channel count reached.", ct);
        return;
    }

    // ... existing channel creation logic
}
```

**3. (Optional) Add rate limiting:**

For additional protection against burst attacks, add a simple token-bucket or
sliding-window rate limiter on INIT frame processing:

```csharp
private readonly RateLimiter _initRateLimiter =
    new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
    {
        PermitLimit = 100,
        Window = TimeSpan.FromSeconds(1),
        SegmentsPerWindow = 4,
    });
```

Key points:
- `MaxConcurrentChannels` caps the total resource footprint per connection
- Returning an error frame (not silently dropping) signals the peer to back off
- Rate limiting provides defense against burst attacks even below the cap
- The accept queue should also be bounded (`Channel.CreateBounded<T>`) to prevent
  memory growth when the application is slow to accept channels

## Reproduction

See `UnboundedChannelOpenTest.cs` — test opens many channels to demonstrate no limit.
