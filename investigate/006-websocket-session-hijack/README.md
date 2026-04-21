# BUG: WebSocket Session Routing Has No Authentication — Session Hijacking

## Severity: HIGH (Security)

## Summary

`WebSocketMuxListener.HandleAsync()` routes incoming WebSocket connections to existing
multiplexer sessions based solely on a client-provided `sessionId` (GUID). Any attacker
who knows or guesses a valid session GUID can hijack an existing session by connecting
with that session ID. There is NO authentication, token verification, or origin checking.

## Evidence

### Code Location

[src/NetConduit.WebSocket/WebSocketMuxListener.cs](../../src/NetConduit.WebSocket/WebSocketMuxListener.cs):

```csharp
public async Task HandleAsync(
    System.Net.WebSockets.WebSocket webSocket,
    Guid? sessionId = null,       // Client-provided, untrusted input!
    CancellationToken cancellationToken = default)
{
    // ...
    if (sessionId.HasValue && _sessions.TryGetValue(sessionId.Value, out var entry))
    {
        // DIRECT routing — no authentication check!
        await entry.ConnectionChannel.Writer.WriteAsync(pair, cancellationToken);
    }
}
```

### Attack Scenario

1. Client A connects to server → gets session GUID `abc-123`
2. Client A disconnects (network failure)
3. Attacker connects with `?session=abc-123` (brute-force or intercepted)
4. `HandleAsync` finds the session in `_sessions` dictionary
5. Attacker's WebSocket is piped directly into Client A's multiplexer
6. Attacker can now:
   - Read all of Client A's channel data
   - Send data as Client A
   - Close Client A's channels

### GUID Predictability

GUIDs (even v4) are not cryptographic tokens. Their entropy is 122 bits from
`Guid.NewGuid()`, but:
- They may be logged, transmitted in URLs, or visible in browser dev tools
- The query string `?session=...` is visible in HTTP logs, proxies, and CDN caches
- Server-side GUIDs are returned via `IStreamMultiplexer.SessionId` and may be exposed

## CWE Reference

- [CWE-287: Improper Authentication](https://cwe.mitre.org/data/definitions/287.html)
- [CWE-384: Session Fixation](https://cwe.mitre.org/data/definitions/384.html)
- [CWE-330: Use of Insufficiently Random Values](https://cwe.mitre.org/data/definitions/330.html)

## Recommended Fix

Replace the bare GUID with a cryptographic session token and verify it on reconnection:

```csharp
using System.Security.Cryptography;

public class WebSocketSession
{
    public Guid SessionId { get; } = Guid.NewGuid();
    public byte[] Token { get; } = RandomNumberGenerator.GetBytes(32);
}
```

```csharp
public async Task HandleAsync(
    WebSocket webSocket,
    Guid? sessionId = null,
    string? token = null,
    CancellationToken cancellationToken = default)
{
    if (sessionId.HasValue && _sessions.TryGetValue(sessionId.Value, out var entry))
    {
        // Verify the token with constant-time comparison
        if (token is null || !CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(token), entry.Session.Token))
        {
            throw new UnauthorizedAccessException("Invalid session token.");
        }

        await entry.ConnectionChannel.Writer.WriteAsync(pair, cancellationToken);
    }
}
```

Key points:
- 256-bit cryptographic token makes brute-force infeasible (~2^256 attempts)
- `CryptographicOperations.FixedTimeEquals` prevents timing side-channel attacks
- Token should be transmitted in a header or handshake payload, NOT in the URL query string
  (URLs are logged by proxies, CDNs, and browsers)
- Consider adding token expiry and rotation for long-lived sessions

## Reproduction

See `WebSocketSessionHijackTest.cs` — proves sessions are accessible with only a GUID.
