# BUG: Handshake Nonce Uses Non-Cryptographic PRNG — Predictable Index Space

## Severity: MEDIUM (Security)

## Summary

The handshake nonce used for index space negotiation is generated with
`Random.Shared.NextInt64()`, which uses the **xoshiro256\*\*** PRNG — a fast,
non-cryptographic random number generator. An attacker who can observe multiple
connection attempts can predict future nonces and force the victim into a
predetermined index space, enabling targeted channel spoofing attacks.

## Evidence

### Code Location

[src/NetConduit/StreamMultiplexer.cs](../../src/NetConduit/StreamMultiplexer.cs) — `SendHandshakeAsync`:

```csharp
_localNonce = Random.Shared.NextInt64();  // Non-cryptographic PRNG!
BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(17), _localNonce);
```

### Why This Matters

The nonce determines index space assignment:
```csharp
// DetermineIndexSpace():
useOddIndices = _localNonce > _remoteNonce;
```

- Higher nonce → odd indices (1, 3, 5, ...)
- Lower nonce → even indices (2, 4, 6, ...)

If an attacker can predict the victim's nonce, they can craft their own nonce
to force the victim into a specific index space. This is a building block for:

1. **Index prediction**: know which indices the peer will allocate
2. **Session confusion**: two clients connecting to the same server could be
   forced into the same index space if nonces collide

### .NET Documentation on Random Thread Safety

Per [Microsoft docs](https://learn.microsoft.com/en-us/dotnet/api/system.random):
> "The Random class is not suitable for generating passwords, tokens, or other
> values that need to be unpredictable."

The correct approach for security-sensitive nonces:
```csharp
using System.Security.Cryptography;
var bytes = RandomNumberGenerator.GetBytes(8);
_localNonce = BinaryPrimitives.ReadInt64BigEndian(bytes);
```

## CWE Reference

- [CWE-330: Use of Insufficiently Random Values](https://cwe.mitre.org/data/definitions/330.html)
- [CWE-338: Use of Cryptographically Weak PRNG](https://cwe.mitre.org/data/definitions/338.html)

## Recommended Fix

Replace `Random.Shared.NextInt64()` with `RandomNumberGenerator`:

```csharp
using System.Security.Cryptography;

// In SendHandshakeAsync:
Span<byte> nonceBytes = stackalloc byte[8];
RandomNumberGenerator.Fill(nonceBytes);
_localNonce = BinaryPrimitives.ReadInt64BigEndian(nonceBytes);
BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(17), _localNonce);
```

Key points:
- `RandomNumberGenerator` uses the OS CSPRNG (e.g., `/dev/urandom` on Linux, BCryptGenRandom on Windows)
- No seeding required, thread-safe, no shared state to observe
- Same performance for a single 8-byte read — the overhead is negligible for a once-per-connection operation
- This is the standard .NET pattern for security-sensitive random values per
  [Microsoft guidance](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator)

## Reproduction

See `HandshakeNoncePredictabilityTest.cs`.
