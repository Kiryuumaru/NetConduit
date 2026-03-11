# NetConduit RpcFramework Sample

Simple request/response RPC pattern using MessageTransit.

## Features

- **Type-safe RPC** - JSON-serialized request/response messages
- **Multiple operations** - Add, Multiply, Concat, GetTime, Echo, SlowOperation
- **Request correlation** - Request IDs match responses to requests
- **Native AOT compatible** - Uses source-generated JSON serialization

## Usage

### Start Server

```bash
dotnet run -- server [host] [port]
```

Default: `127.0.0.1:9002`

### Run Client

```bash
dotnet run -- client [host] [port]
```

## Examples

```bash
# Terminal 1: Start server
dotnet run -- server

# Terminal 2: Run client
dotnet run -- client

# Output:
# Add(5, 3) = 8
# Multiply(4.5, 2.0) = 9.0
# Concat(["Hello", " ", "World"]) = "Hello World"
# GetTime() = { "Utc": "...", "Local": "...", "UnixTimestamp": ... }
```

## Available RPC Methods

| Method | Parameters | Description |
|--------|------------|-------------|
| `Add` | `a`, `b` (int) | Returns a + b |
| `Multiply` | `a`, `b` (double) | Returns a × b |
| `Concat` | `strings` (array) | Joins strings |
| `GetTime` | none | Returns current time info |
| `Echo` | any | Returns parameters as-is |
| `SlowOperation` | `delayMs` (optional) | Simulates slow operation |

## Protocol

### Request

```json
{
  "requestId": "abc123",
  "method": "Add",
  "parameters": { "a": 5, "b": 3 }
}
```

### Response

```json
{
  "requestId": "abc123",
  "success": true,
  "result": 8
}
```

## Architecture

```
┌─────────────────────┐                    ┌─────────────────────┐
│       Client        │                    │       Server        │
│                     │                    │                     │
│  SimpleRpcClient    │  rpc-requests      │  ProcessRpcAsync()  │
│  ┌───────────────┐  │  ──────────────▶   │  ┌───────────────┐  │
│  │  CallAsync()  │──┼────────────────────┼──│ Method Router │  │
│  └───────────────┘  │                    │  └───────────────┘  │
│         ▲           │  rpc-responses     │         │           │
│         │           │  ◀──────────────   │         ▼           │
│  ┌───────────────┐  │                    │  ┌───────────────┐  │
│  │ ReceiveLoop   │◀─┼────────────────────┼──│ SendAsync()   │  │
│  └───────────────┘  │                    │  └───────────────┘  │
└─────────────────────┘                    └─────────────────────┘
```

## NetConduit Features Demonstrated

| Feature | Usage |
|---------|-------|
| `MessageTransit<TSend, TReceive>` | Different types for request/response |
| `OpenMessageTransitAsync` | Open bidirectional message channel |
| `ReceiveAllAsync` | Process requests as async enumerable |
| `SendAsync` | Send typed messages |
| Source-generated JSON | Native AOT compatible serialization |
