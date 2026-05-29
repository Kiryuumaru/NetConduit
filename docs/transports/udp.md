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
        ReliableUdpOptions? udpOptions = null);
}

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
| `MaxRetransmits` | 5 | Retransmit attempts before considering the link dead. |

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

## Reconnectable server

UDP's reliable shim is bound to one remote peer per session. Surviving a peer change requires disposing the multiplexer and creating a new one — there is no copy-paste re-accepting factory equivalent to TCP. See [Reconnection → UDP](../concepts/reconnection.md#udp) for the recommended pattern.

## Tuning

- **High-latency or lossy networks** — raise `RetransmitTimeout` and `MaxRetransmits`. Otherwise the shim will give up too quickly.
- **Constrained MTU paths** — set `Mtu` below your path MTU (1200 is a safe default; LAN can use 1400+).

## Platform

Cross-platform (IPv6 dual-mode sockets).
