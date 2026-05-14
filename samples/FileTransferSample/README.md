# NetConduit FileTransfer Sample

Concurrent file transfers over multiplexed channels with progress reporting.

## Features

- **Concurrent transfers** - Multiple files sent in parallel over separate channels
- **Progress reporting** - Real-time transfer speed and progress
- **Size verification** - Automatic integrity checking

## Usage

### Start the Server

```bash
dotnet run -- server [port] [output-dir]
```

Default port: 5001, default output: `./received`

### Send Files

```bash
dotnet run -- send [port] [host] <file1> <file2> ...
```

## Examples

```bash
# Terminal 1: Start server
dotnet run -- server 5001 ./downloads

# Terminal 2: Send files
dotnet run -- send 5001 127.0.0.1 document.pdf image.png video.mp4
```

## Architecture

```
Sender                              Server
┌──────────────────┐               ┌──────────────────┐
│ NetConduit Mux   │               │ NetConduit Mux   │
│                  │               │                  │
│ [file1 channel]──┼──────────────▶│──AcceptChannels()│
│ [file2 channel]──┼──────────────▶│     │            │
│ [file3 channel]──┼──────────────▶│     ▼            │
│                  │               │  Concurrent      │
│                  │               │  File Writers    │
└──────────────────┘               └──────────────────┘
```

## Protocol

Each file transfer channel sends:
1. Filename length (4 bytes, big-endian)
2. Filename (UTF-8 bytes)
3. File size (8 bytes, big-endian)
4. File content (streamed)

## NetConduit Features Demonstrated

| Feature | Usage |
|---------|-------|
| `TcpMultiplexer.CreateOptions` | Client connection setup |
| `TcpMultiplexer.CreateServerOptions` | Server listener setup |
| `OpenChannelAsync` | One channel per file (parallel transfers) |
| `AcceptChannelsAsync` | Accept all incoming file channels |
| Channel as Stream | Use `WriteAsync`/`ReadAsync` for binary data |
