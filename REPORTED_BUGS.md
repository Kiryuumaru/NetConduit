---

## Critical

**1. DeltaTransit: Silent Drop of Identical Messages**
When two consecutive states are identical, `SendCoreAsync()` returns without sending anything (`if (ops.Count == 0) return;`). This causes **infinite hangs** for request-response patterns since the receiver never gets a message. We hit this directly — it was the root cause of the FullSync hang in EdgeConduit where repeated `FullSyncRequest` messages were silently dropped.

**2. MessageTransit: Zero-Length Message = EOF**
A message with length 0 returns `default` (treated as connection-closed), indistinguishable from a legitimate empty payload. Protocols using empty messages will misinterpret them as stream end.

**3. DeltaTransit: Double ArrayPool Return**
If `ReadExactAsync()` fails mid-read and returns the buffer, `ReceiveCoreAsync`'s finally block returns it again — corrupting the pool. Subsequent rentals can return poisoned buffers.

---

## High

**4. WriteChannel: Credit Underflow Under Concurrency**
The CompareExchange loop in `WriteAsync` allows two concurrent threads to both read `oldCredits=1000`, both calculate `toSend=900`, and both succeed — consuming 1800 credits from a 1000 budget. Flow control breaks.

**5. StreamMultiplexer: Channel Index Space Never Reclaimed**
Channel indices are allocated monotonically and never freed. Long-lived multiplexers creating/closing many channels eventually exhaust the ~4 billion index space permanently. We worked around this in EdgeConduit with `_tunnelCounter` for unique IDs.

**6. Channel ID Reuse Corruption**
After a channel closes, the same ID can be reused while the remote side still has a stale read channel. New messages corrupt the old handler's data stream.

**7. ReadChannel: Dispose Race with Active Read**
`EnqueueData` holds `_disposeLock` but `ReadAsync` accesses `_currentOwnedBuffer` without it. A dispose during an active read causes use-after-free.

**8. Symmetric Handshake Deadlock**
If both sides of a connection use identical logic (both wait for the other's handshake first), they deadlock until the full `HandshakeTimeout` (default 10s) expires.

**9. DeltaTransit: Silent State Divergence on Malformed Deltas**
Malformed delta JSON returns an empty ops list instead of throwing. Corrupted deltas cause silent state divergence between peers with no detection mechanism.

**10. Reconnection Buffer Silently Drops Data**
Default reconnect buffer is 1MB. Bursts exceeding this silently drop excess data with no warning or callback.

---

## Medium

**11. Credit Starvation Event Spam** — `OnCreditStarvation` fires on every credit-wait cycle (potentially thousands/sec), overwhelming event handlers.

**12. Transit Suffix Collision** — Hardcoded `>>` / `<<` suffixes for duplex channels collide if user channel IDs contain those characters.

**13. Adaptive Flow Control Shrink Race** — Window can shrink to `MinCredits` while a grant is in flight, causing sender starvation.

**14. ReadChannel ReceiveAllAsync Hides Closure Reason** — Cannot distinguish graceful close from error close from dispose.

**15. Incomplete FlushMode** — Only `Batched` mode is fully implemented; other enum values are untested.

---

## Summary

| Severity | Count | Key Theme |
|----------|-------|-----------|
| Critical | 3 | Silent message drops, buffer corruption |
| High | 7 | Concurrency bugs, resource exhaustion, data corruption |
| Medium | 5 | Event spam, ID collisions, incomplete features |
