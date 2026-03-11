# TcpTunnel Sample - Port Forwarding via NetConduit

**Status: ✅ Implemented** - See [samples/NetConduit.Samples.TcpTunnel](samples/NetConduit.Samples.TcpTunnel)

## Overview

TCP port forwarding through a relay server, enabling access to services behind NAT/firewall. Similar to SSH `-L` tunneling or ngrok.

**Transport Options:**
- **TCP** - Direct TCP connection to relay (best latency)
- **WebSocket** - WebSocket connection to relay (firewall-friendly, works through HTTP proxies)

---

## Use Cases

- Access home server from work (behind NAT)
- Debug remote IoT device's web interface
- Expose development server without public IP
- Game server behind firewall
- Remote database access

---

## Architecture

```
┌─────────────────┐         ┌──────────────┐         ┌─────────────────┐
│  Your Machine   │         │ Relay Server │         │  Remote Device  │
│   (Country A)   │         │   (Cloud)    │         │   (Country B)   │
│                 │         │              │         │                 │
│ localhost:4000  │◄───────►│   Routes     │◄───────►│ localhost:8080  │
│   (forward)     │ TCP/WS  │   traffic    │ TCP/WS  │    (agent)      │
└─────────────────┘         └──────────────┘         └─────────────────┘

Transport layer (client ↔ relay):
  ┌──────────────────────────────────────────────────┐
  │  TCP:  TcpProvider        (port 5000)                │
  │   WS:  WebSocketProvider  (ws://host:5001/relay)     │
  └──────────────────────────────────────────────────┘

Data flow:
  curl localhost:4000
       │
       ▼
  forward listens on 4000
       │
       ▼
  forward → relay (NetConduit channel)
       │
       ▼
  relay routes to agent "myservice"
       │
       ▼
  agent → localhost:8080 (TCP)
       │
       ▼
  response flows back through same path
```

---

## Components

### 1. Relay Server

Central hub that both agent and forward connect to.

**Responsibilities:**
- Listen on both TCP and WebSocket endpoints
- Accept connections from agents (service providers)
- Accept connections from forwards (service consumers)
- Route tunnel requests to registered agents
- Manage service registry (name → agent mapping)

**State:**
```csharp
ConcurrentDictionary<string, AgentConnection> _agents;

record AgentConnection(
    StreamMultiplexer Mux,
    MessageTransit<TunnelMessage, TunnelMessage> Control);
```

### 2. Agent

Runs on the remote device with the service to expose.

**Responsibilities:**
- Connect to relay server
- Register service name (e.g., "myservice")
- Accept tunnel requests from relay
- Bridge tunnel channel to local TCP port

**Flow:**
```
agent starts
    │
    ├─► Connect to relay
    │
    ├─► Send RegisterService { Name = "myservice", LocalPort = 8080 }
    │
    └─► Loop: Accept tunnel channels
             │
             ├─► Open TCP to localhost:8080
             │
             └─► Bridge: channel ↔ TCP socket
```

### 3. Forward

Runs on your local machine.

**Responsibilities:**
- Connect to relay server
- Listen on local TCP port
- Request tunnels to named service
- Bridge local TCP to tunnel channel

**Flow:**
```
forward starts
    │
    ├─► Connect to relay
    │
    ├─► Listen on local port (e.g., 4000)
    │
    └─► Loop: Accept local TCP connections
             │
             ├─► Send TunnelRequest { ServiceName = "myservice" }
             │
             ├─► Relay creates channel to agent
             │
             └─► Bridge: local TCP ↔ channel
```

---

## Protocol

### Messages (JSON over MessageTransit)

```csharp
[JsonDerivedType(typeof(RegisterService), "register")]
[JsonDerivedType(typeof(RegisterAck), "register-ack")]
[JsonDerivedType(typeof(TunnelRequest), "tunnel-req")]
[JsonDerivedType(typeof(TunnelAccept), "tunnel-accept")]
[JsonDerivedType(typeof(TunnelReject), "tunnel-reject")]
[JsonDerivedType(typeof(ListRequest), "list-req")]
[JsonDerivedType(typeof(ServiceList), "list")]
public abstract record TunnelMessage;

// Agent → Relay: Register a service
public record RegisterService(string Name, string DeviceId, int LocalPort) : TunnelMessage;

// Relay → Agent: Registration confirmed
public record RegisterAck(bool Success, string? Error) : TunnelMessage;

// Forward → Relay: Request tunnel to service
public record TunnelRequest(string ServiceName, string TunnelId) : TunnelMessage;

// Relay → Forward: Tunnel established
public record TunnelAccept(string TunnelId) : TunnelMessage;

// Relay → Forward: Tunnel failed
public record TunnelReject(string TunnelId, string Reason) : TunnelMessage;

// Anyone → Relay: Request list of services
public record ListRequest() : TunnelMessage;

// Relay → Anyone: List of available services on all devices
public record ServiceList(ServiceInfo[] Services) : TunnelMessage;

public record ServiceInfo(string Name, string DeviceId, int Port);
```

### Channel Naming

| Channel | Direction | Purpose |
|---------|-----------|---------|
| `ctrl>>` | Agent/Forward → Relay | Control messages (send) |
| `ctrl<<` | Relay → Agent/Forward | Control messages (receive) |
| `tunnel:{id}>>` | Forward → Agent | Tunnel data (via relay) |
| `tunnel:{id}<<` | Agent → Forward | Tunnel data (via relay) |

---

## Relay Server Flow

```
Start TCP listener (port 5000)
Start WebSocket listener (port 5001, path /relay)
    │
    ▼
Accept connection (from either)
    │
    ├─► Create multiplexer
    │
    ├─► Accept control channel
    │
    └─► Handle messages:
        │
        ├─► RegisterService:
        │    └─► Add to _agents[name] = mux
        │
        ├─► TunnelRequest { ServiceName, TunnelId }:
        │    │
        │    ├─► Lookup agent = _agents[ServiceName]
        │    │
        │    ├─► If not found: Send TunnelReject
        │    │
        │    ├─► Open channel to agent: tunnel:{TunnelId}
        │    │
        │    ├─► Open channel to forward: tunnel:{TunnelId}
        │    │
        │    ├─► Bridge: forwardChannel ↔ agentChannel
        │    │
        │    └─► Send TunnelAccept to forward
        │
        └─► Disconnect:
             └─► Remove from _agents if agent
```

---

## Data Flow Detail

### Tunnel Establishment

```
Forward                     Relay                      Agent
   │                          │                          │
   │──TunnelRequest──────────►│                          │
   │   {myservice, tunnel-1}  │                          │
   │                          │                          │
   │                          │◄─────(lookup agent)──────│
   │                          │                          │
   │                          │──OpenChannel─────────────►│
   │                          │  tunnel:tunnel-1>>       │
   │                          │                          │
   │                          │◄─AcceptChannel───────────│
   │                          │  tunnel:tunnel-1<<       │
   │                          │                          │
   │◄─TunnelAccept────────────│                          │
   │   {tunnel-1}             │                          │
   │                          │                          │
   │══tunnel:tunnel-1>>═══════│═══════════════════════════►│ localhost:8080
   │◄═tunnel:tunnel-1<<═══════│◄══════════════════════════│
```

### Bridge Implementation

```csharp
async Task BridgeAsync(Stream a, Stream b, CancellationToken ct)
{
    var aToB = a.CopyToAsync(b, ct);
    var bToA = b.CopyToAsync(a, ct);
    await Task.WhenAny(aToB, bToA);
}
```

### Transport Provider Selection

```csharp
// Parse port format: "5000" = TCP, "5001/relay" = WebSocket
(INetConduitProvider provider, bool isWebSocket) ParseEndpoint(string host, string portPath)
{
    if (portPath.Contains('/'))
    {
        var parts = portPath.Split('/', 2);
        var port = int.Parse(parts[0]);
        var path = "/" + parts[1];
        return (new WebSocketProvider($"ws://{host}:{port}{path}"), true);
    }
    else
    {
        var port = int.Parse(portPath);
        return (new TcpProvider(host, port), false);
    }
}

// Relay server listens on both
async Task RunRelay(int tcpPort, string wsPortPath)
{
    var tcpListener = new TcpListener(tcpPort);
    
    var wsParts = wsPortPath.Split('/', 2);
    var wsPort = int.Parse(wsParts[0]);
    var wsPath = "/" + wsParts[1];
    var wsListener = new WebSocketListener(wsPort, wsPath);
    
    // Accept from either transport
    await Task.WhenAll(
        AcceptLoop(tcpListener),
        AcceptLoop(wsListener)
    );
}
```

---

## CLI Design

```
NetConduit TCP Tunnel

Usage:
  relay <tcp-port> <ws-port/path>         Start relay server (TCP + WebSocket)
  agent <relay-host> <port[/path]> <name> <local-port>
  forward <relay-host> <port[/path]> <name> <local-port>
  list <relay-host> <port[/path]>

Port Format:
  5000        TCP connection (no path = TCP)
  5001/relay  WebSocket connection (path = WebSocket at ws://host:port/path)

Examples:
  # Start relay on cloud server (TCP:5000, WS:5001/relay)
  dotnet run -- relay 5000 5001/relay

  # --- TCP transport (no path) ---

  # Agent via TCP: expose port 8080 as "webserver"
  dotnet run -- agent relay.example.com 5000 webserver 8080

  # Forward via TCP: access webserver on local:4000
  dotnet run -- forward relay.example.com 5000 webserver 4000

  # List via TCP
  dotnet run -- list relay.example.com 5000

  # --- WebSocket transport (with path) ---

  # Agent via WebSocket: expose port 3306 as "mysql"
  dotnet run -- agent relay.example.com 5001/relay mysql 3306

  # Forward via WebSocket: access mysql on local:3307
  dotnet run -- forward relay.example.com 5001/relay mysql 3307

  # List via WebSocket
  dotnet run -- list relay.example.com 5001/relay

  # --- Mixed transport (agent on TCP, forward on WS) ---

  # Agent registers via TCP (behind minimal firewall)
  dotnet run -- agent relay.example.com 5000 myapp 8080

  # Forward via WebSocket (through corporate proxy)
  dotnet run -- forward relay.example.com 5001/relay myapp 4000

Output:
  # list command output:
  # Available services:
  #   webserver    (device-abc)    → :8080
  #   mysql        (device-xyz)    → :3306

  # Now: curl localhost:4000 → hits remote device's :8080
```

---

## Implementation Phases

### Phase 1: Core Protocol
- [ ] Define TunnelMessage types with JSON serialization
- [ ] Implement relay server with dual listeners (TCP + WebSocket)
- [ ] Implement agent with service registration
- [ ] Port format detection: `port` = TCP, `port/path` = WebSocket

### Phase 2: Tunnel Establishment
- [ ] Implement TunnelRequest handling in relay
- [ ] Implement channel bridging in relay
- [ ] Implement tunnel acceptance in agent

### Phase 3: Forward Client
- [ ] Implement local TCP listener
- [ ] Implement tunnel request on connection
- [ ] Implement local-to-channel bridging

### Phase 4: Polish
- [ ] Add service listing command
- [ ] Add connection status display
- [ ] Add graceful disconnect handling
- [ ] Add reconnection support

---

## Error Handling

| Scenario | Handling |
|----------|----------|
| Agent disconnects | Remove from registry, reject pending tunnels |
| Forward disconnects | Close tunnel channels |
| Service not found | Send TunnelReject with reason |
| Local port unavailable | Agent logs error, continues |
| Relay unavailable | Agent/Forward retry with backoff |

---

## Security Considerations

> **Note:** This sample is for demonstration. Production use requires:

- TLS for relay connections
- Authentication (API keys, tokens)
- Authorization (who can register/access services)
- Rate limiting
- Connection limits per client

---

## Testing Scenarios

### Basic Flow
1. Start relay on port 5000
2. Start agent exposing port 8080 as "web"
3. Start forward mapping "web" to local 4000
4. `curl localhost:4000` → should reach agent's 8080

### Multiple Services
1. Agent A registers "web" (8080)
2. Agent B registers "db" (5432)
3. Forward to both simultaneously

### Reconnection
1. Establish tunnel
2. Kill relay
3. Restart relay
4. Verify agents reconnect and re-register

### Concurrent Tunnels
1. Multiple forwards to same service
2. Each gets independent tunnel
3. Traffic isolated

---

## Future Enhancements

- **UDP tunneling** - For game servers, VoIP
- **Multi-hop** - Chain multiple relays
- **Compression** - Reduce bandwidth
- **Bandwidth limiting** - Per-tunnel QoS
- **Web dashboard** - Monitor tunnels via HTTP
