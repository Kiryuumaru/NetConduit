# Concepts

Core concepts for understanding how NetConduit works.

## Overview

| Concept | Description |
|---------|-------------|
| [Channels](channels.md) | Virtual one-way streams over a single connection |
| [Backpressure](backpressure.md) | Credit-based flow control |
| [Priority](priority.md) | Channel priority levels |
| [Reconnection](reconnection.md) | Auto-reconnection with state recovery |
| [Graceful Shutdown](graceful-shutdown.md) | GoAway protocol |
| [Heartbeat](heartbeat.md) | Keep-alive ping/pong |
| [Events](events.md) | Multiplexer lifecycle events |

## How It Works

NetConduit takes a single bidirectional stream and multiplexes it into many virtual channels:

```
Application:    Channel A    Channel B    Channel C
                    │            │            │
                    ▼            ▼            ▼
NetConduit:    ┌─────────────────────────────────────┐
               │  Frame Router (mux/demux)           │
               │  - 8-byte frame headers             │
               │  - Credit-based flow control        │
               │  - Priority scheduling              │
               └─────────────────────────────────────┘
                              │
                              ▼
Transport:          Single Stream (TCP, WS, etc.)
```

Each channel is independent — data on one channel doesn't block others (no head-of-line blocking at the mux layer).
