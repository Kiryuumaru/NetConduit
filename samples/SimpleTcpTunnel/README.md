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
# Relay listens on TCP :5000 AND WebSocket :5001/relay simultaneously (loopback-only by default, auth required)
$env:TUNNEL_TOKEN = "correct horse battery staple"
dotnet run --project samples/SimpleTcpTunnel -- relay 5000 5001/relay --auth-env TUNNEL_TOKEN

# Agent: connect to relay over TCP, expose tunnel name "web" pointing at local port 8080
dotnet run --project samples/SimpleTcpTunnel -- agent localhost 5000 web 8080 --auth-env TUNNEL_TOKEN

# Forwarder: connect to relay over TCP, request tunnel "web", listen locally on port 4000
dotnet run --project samples/SimpleTcpTunnel -- forward localhost 5000 web 4000 --auth-env TUNNEL_TOKEN

# Mixed: agent talking to the same relay via its WebSocket port
dotnet run --project samples/SimpleTcpTunnel -- agent relay.example.com 5001/relay myapp 3000 --auth-env TUNNEL_TOKEN

# List tunnels registered on a relay
dotnet run --project samples/SimpleTcpTunnel -- list localhost 5000 --auth-env TUNNEL_TOKEN
```

Once running, a TCP client connecting to `localhost:4000` (the forwarder) reaches the agent's `localhost:8080` (the target service) through the relay.

### Hardening flags

| Flag | Relay | agent / forward / list |
| --- | --- | --- |
| `--bind loopback` (default) | TCP binds `Loopback`, WS prefix uses `localhost` | — |
| `--bind any` | TCP binds `Any`, WS prefix uses `+`. **WARNING:** exposes the relay to the network. Token + tunnel bytes travel as cleartext (no TLS): use loopback only, or tunnel over TLS/SSH on untrusted networks | — |
| `--auth <token>` / `--auth-env NAME` | Required to start (or `--allow-no-auth`). Clients must send `Authenticate` first; Register/Tunnel/List are gated per message until authed. Prefer `--auth-env`: `--auth` exposes the token in process lists | Passthrough: sends `Authenticate` before Register/Tunnel/List |
| `--allow-no-auth` | **Insecure, local demos only.** Relay starts without a token | — |
| `--allow-service NAME` (repeatable) | Only these service names may register (`RegisterAck(false, "not allowlisted")` otherwise). Empty allowlist (flag absent) means any authenticated name may register | — |

Fail-closed behavior: the relay refuses to start without `--auth`/`--auth-env` or explicit `--allow-no-auth`;
unauthenticated Register/Tunnel/List attempts get `RegisterAck(false)` / `TunnelReject` / connection close
with no duplex opened and no enumeration; control-channel setup and the auth grace are each bounded (~5s per stage, ~10s worst-case).
Tokens are compared in constant time over content (lengths visible) and never logged.

Token + tunnel bytes travel as cleartext with no TLS: use loopback only, or tunnel over TLS/SSH on untrusted networks.

> Do not expose this sample to untrusted networks.

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
