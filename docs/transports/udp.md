# UDP transport

Package: [`NetConduit.Transport.Udp`](https://www.nuget.org/packages/NetConduit.Transport.Udp).

Wraps `System.Net.Sockets.UdpClient`. Because UDP itself is unreliable, the transport includes a small **reliable shim** (`ReliableUdpStream`) that adds sequence numbers, acknowledgments, and retransmission. The multiplexer's own framing then rides on top.

This is **not** a full TCP replacement (no congestion control, no fast-retransmit, no SACK). Use it where TCP isn't an option.

## API

```csharp
public static class UdpMultiplexer
{
    public static MultiplexerOptions CreateOptions(
        string host,
        int port,
        ReliableUdpOptions? udpOptions = null);

    public static MultiplexerOptions CreateServerOptions(
        int listenPort,
        ReliableUdpOptions? udpOptions = null,
        UdpAcceptOptions? acceptOptions = null);
}

public sealed record UdpAcceptOptions
{
    public TimeSpan VerificationWindow { get; init; } = TimeSpan.FromMilliseconds(300);
    public int MaxCandidates { get; init; } = 64;
    public int MaxHellosPerEndpointPerSecond { get; init; } = 20;
    public int MaxHellosGlobalPerSecond { get; init; } = 1000;
    public UdpChallengeMode ChallengeMode { get; init; } = UdpChallengeMode.Disabled;
}

public enum UdpChallengeMode { Disabled, OptIn, Required }

public sealed class ReliableUdpOptions
{
    public int Mtu { get; init; } = 1200;
    public TimeSpan RetransmitTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxRetransmits { get; init; } = 5;
}
```

### `ReliableUdpOptions`

| Property | Default | Meaning |
| --- | --- | --- |
| `Mtu` | 1200 | Maximum datagram size including the 7-byte header. Valid range: 8 to 65,507. |
| `RetransmitTimeout` | 1 s | Time to wait for an ACK before retransmitting a datagram. Must be non-negative and no greater than 2,147,483,647 milliseconds. |
| `MaxRetransmits` | 5 | Maximum retransmissions after the initial send before considering the link dead (total sends = `MaxRetransmits` + 1; default 5 means up to 6 sends). Must be non-negative — negatives throw `ArgumentOutOfRangeException`. `0` is legal and means send-once with no retries. No upper bound. |

## Client

```csharp
using NetConduit;
using NetConduit.Transport.Udp;

await using var mux = StreamMultiplexer.Create(UdpMultiplexer.CreateOptions("127.0.0.1", 5000));
mux.Start();
await mux.WaitForReadyAsync();
```

## Server

```csharp
await using var mux = StreamMultiplexer.Create(UdpMultiplexer.CreateServerOptions(5000));
mux.Start();
await mux.WaitForReadyAsync();
```

## Handshake

The first exchange is a small `NC_HELLO` / `NC_HELLO_ACK` to bind the server to the remote endpoint and verify the protocol version. After that, the multiplexer's normal handshake runs.

### Accept hardening (Issue #616)

The server accept path stays unconnected through a short bounded `VerificationWindow` (default 300 ms, ~1.5 client retry intervals) and sends `NC_HELLO_ACK` to **every** admitted claimant instead of latching the first datagram. A data-proven claimant (real stream bytes observed) wins immediately; a competing first-seen tentative is held through a bounded grace window so a live (retransmitting) competitor can migrate it while the socket is still unconnected. Rate caps (per-endpoint + global HELLO budgets, bounded LRU candidate table, default 64) apply after shape validation and before ACK-send. There is no global accept timeout: an idle server still waits on the caller's cancellation token; only per-attempt verification deadlines bound the competition window.

Residual: a lone idle HELLO still commits as a singleton at its verification deadline (indistinguishable from the HELLO-alone retransmit the existing contract requires), and a rogue that stays live past the grace window still wins the race. Data-proof requires a real `FlagData` stream frame — pure ACK/FIN frames never count (an ACK-all reflector or FIN-only prober cannot win the commit). Defeating the blind-spoof shape requires proof-of-receipt, scaffolded behind `UdpAcceptOptions.ChallengeMode` (`Disabled` default = current wire bytes, old clients connect; `OptIn` accepts v1; `Required` rejects v1 `NC_HELLO` loudly with `InvalidOperationException`, tearing down that accept attempt rather than dropping-and-continuing — dropping would park the one-shot on a silent listener with no logging seam, while the loud throw keeps the rejection observable and the pinned Required tests green).

Remaining HIGH residuals (no wire change in this cut): lone-HELLO-commits (singleton at deadline), after-grace rogue-wins (live past grace), ACK-all reflection (server still ACKs every admitted HELLO via unconnected `SendTo`, so a reflector can elicit ACK traffic — rate caps bound it, they do not remove it).

## Reconnectable server

UDP's reliable shim is bound to one remote peer per session. Surviving a peer change requires disposing the multiplexer and creating a new one — there is no copy-paste re-accepting factory equivalent to TCP. See [Reconnection → UDP](../concepts/reconnection.md#udp) for the recommended pattern.

## Tuning

- **High-latency or lossy networks** — raise `RetransmitTimeout` and `MaxRetransmits`. Otherwise the shim will give up too quickly.
- **Constrained MTU paths** — set `Mtu` below your path MTU (1200 is a safe default; LAN can use 1400+).

## Platform

Cross-platform (IPv6 dual-mode sockets).
