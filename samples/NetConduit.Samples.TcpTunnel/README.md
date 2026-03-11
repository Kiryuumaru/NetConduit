# NetConduit TcpTunnel Sample

TCP port forwarding through a relay server - similar to SSH `-L` tunneling or ngrok.

## Overview

```
┌─────────────┐       ┌─────────────┐       ┌─────────────┐       ┌─────────────┐
│   Client    │──────▶│   Forward   │──────▶│    Relay    │──────▶│   Agent     │
│ (e.g. curl) │       │ (port 4000) │       │ (port 6000) │       │ (port 8080) │
└─────────────┘       └─────────────┘       └─────────────┘       └─────────────┘
                                                                        │
                                                                        ▼
                                                                  ┌─────────────┐
                                                                  │ Local       │
                                                                  │ Service     │
                                                                  └─────────────┘
```

## Components

| Component | Description |
|-----------|-------------|
| **Relay** | Central hub that routes traffic between agents and forwards |
| **Agent** | Registers a local service with the relay, making it accessible |
| **Forward** | Creates a local port that tunnels traffic to a remote service |
| **List** | Shows all services registered with the relay |

## Usage

### 1. Start the Relay Server

```bash
# TCP only
dotnet run -- relay 6000

# TCP + WebSocket
dotnet run -- relay 6000 6001/ws
```

### 2. Register a Service (Agent)

On a machine with a local service (e.g., web server on port 8080):

```bash
dotnet run -- agent localhost 6000 mywebapp 8080
```

This registers `mywebapp` with the relay, exposing `localhost:8080`.

### 3. Access the Service (Forward)

From any machine that can reach the relay:

```bash
dotnet run -- forward localhost 6000 mywebapp 4000
```

Now `localhost:4000` tunnels to the agent's `localhost:8080`.

### 4. List Available Services

```bash
dotnet run -- list localhost 6000
```

Output:
```
Available services:
─────────────────────────────────────────────────────
  mywebapp            (MACHINE-NAME  ) → :8080
─────────────────────────────────────────────────────
Total: 1 service(s)
```

## Examples

### Expose a Local Web Server

```bash
# Terminal 1: Start relay
dotnet run -- relay 6000

# Terminal 2: Start agent (expose local web server)
dotnet run -- agent localhost 6000 webapp 3000

# Terminal 3: Forward to access it
dotnet run -- forward localhost 6000 webapp 8080

# Now http://localhost:8080 reaches the web server on port 3000
```

### WebSocket Transport

Use `/path` suffix for WebSocket connections:

```bash
# Relay with WebSocket endpoint
dotnet run -- relay 6000 6001/ws

# Agent connecting via WebSocket
dotnet run -- agent localhost 6001/ws myservice 8080

# Forward via WebSocket
dotnet run -- forward localhost 6001/ws myservice 4000
```

## Features

- **Multiple concurrent tunnels** - Each connection creates a new tunnel
- **Auto-reconnection** - Agent and Forward reconnect if connection drops
- **TCP and WebSocket** - Choose your transport
- **Service discovery** - List command shows all registered services

## Architecture

The sample demonstrates several NetConduit features:

| Feature | Usage |
|---------|-------|
| `DuplexStreamTransit` | Bidirectional data streams for tunnel channels |
| `MessageTransit` | JSON control messages (register, tunnel request, etc.) |
| `OpenDuplexStreamAsync` | Opens paired write/read channels for full duplex |
| `AcceptChannelsAsync` | Accepts incoming channels from remote side |
| Auto-reconnection | Transparent recovery from connection drops |

## Protocol

Control channel messages (JSON with `$type` discriminator):

| Message | Direction | Description |
|---------|-----------|-------------|
| `RegisterService` | Agent → Relay | Register a service |
| `RegisterAck` | Relay → Agent | Acknowledge registration |
| `TunnelRequest` | Forward → Relay | Request tunnel to service |
| `TunnelAccept` | Relay → Forward | Tunnel established |
| `TunnelReject` | Relay → Forward | Tunnel failed |
| `ListRequest` | Client → Relay | List services |
| `ServiceList` | Relay → Client | Available services |

## Command Reference

```
Usage: TcpTunnel <command> [options]

Commands:
  relay    <tcp-port> [ws-port/path]    Start relay server
  agent    <host> <port[/path]> <name> <local-port>   Register service
  forward  <host> <port[/path]> <name> <local-port>   Forward to service
  list     <host> <port[/path]>         List services
```
