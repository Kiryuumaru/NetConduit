# SimpleTcpTunnel

A three-role TCP tunnel: **relay** + **agent** + **forward**. Lets a TCP service behind NAT/firewall (the agent) accept connections from any TCP client (forward) by routing through a publicly reachable relay.

## What it shows

- Three coordinating processes over a single NetConduit relay.
- Per-tunnel-connection channel via `DuplexStreamTransit` — opening a new logical TCP stream is one extension method call.
- Control plane via `MessageTransit` for tunnel registration and listing.
- Optional **WebSocket relay** (`port/path`) so the agent can sit behind HTTP proxies.

## Roles

| Role | Listens / Connects | Job |
| --- | --- | --- |
| `relay` | listens for agents and forwards | switching fabric |
| `agent`  | connects out to relay, opens local target | exposes a local service through the relay |
| `forward` | listens locally, dials relay | accepts client TCP connections and pipes them to the agent |
| `list` | queries relay | prints registered tunnels |

## Topology

```
   tcp client                forward                relay                agent             target service
       |   tcp connect ----->  |                      |                    |  (e.g. localhost:8080)
       |                       |  open duplex "X" --> |                    |
       |                       |                      | open duplex "X" -> | <--- local tcp dial
       |                       |                      |                    |
       |  bytes <===================== piped duplex stream ============>  bytes
```

## Run

```powershell
# Relay on port 5000 (TCP) — listens for agents and forwarders
dotnet run --project samples/SimpleTcpTunnel -- relay 5000

# WebSocket relay on port 5001 at path "/relay"
dotnet run --project samples/SimpleTcpTunnel -- relay 5001/relay

# Agent: connect to relay at localhost:5000, expose tunnel name "web" pointing at local port 8080
dotnet run --project samples/SimpleTcpTunnel -- agent localhost 5000 web 8080

# Forwarder: connect to relay, request tunnel "web", listen locally on port 4000
dotnet run --project samples/SimpleTcpTunnel -- forward localhost 5000 web 4000

# Mixed: agent talking to a WebSocket relay
dotnet run --project samples/SimpleTcpTunnel -- agent relay.example.com 5001/relay myapp 3000

# List tunnels registered on a relay
dotnet run --project samples/SimpleTcpTunnel -- list localhost 5000
```

Once running, a TCP client connecting to `localhost:4000` (the forwarder) reaches the agent's `localhost:8080` (the target service) through the relay.

## Key code shape

```csharp
// Forwarder: per inbound tcp connection
var tcp = await listener.AcceptTcpClientAsync();
var duplex = await relayMux.OpenDuplexStreamAsync($"tunnel:{name}:{Guid.NewGuid()}");
_ = tcp.GetStream().CopyToAsync(duplex);
_ = duplex.CopyToAsync(tcp.GetStream());
```

```csharp
// Agent: accept matching channels and dial local service
await foreach (var ch in mux.AcceptChannelsAsync())
{
    if (ch.ChannelId.StartsWith($"tunnel:{tunnelName}:"))
    {
        var duplex = new DuplexStreamTransit(/* paired writer */, ch);
        var local = new TcpClient(); await local.ConnectAsync(target, targetPort);
        _ = duplex.CopyToAsync(local.GetStream());
        _ = local.GetStream().CopyToAsync(duplex);
    }
}
```
