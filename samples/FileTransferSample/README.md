# FileTransferSample

Parallel multi-file transfer over a single TCP connection. One NetConduit channel per file lets transfers proceed concurrently and independently — slow files don't block fast ones.

## What it shows

- One `TcpMultiplexer` connection, **N concurrent channels**.
- `MessageTransit` for file metadata (name + size) on a control channel.
- `StreamTransit` for raw file bytes on a data channel per file.
- Backpressure: large files don't starve small ones.

## Topology

```
+-------------+        TCP        +-------------+
|   sender    |<----------------->|   server    |
|             |  meta   (msg)     |             |
|             |  file1  (stream)  |             |
|             |  file2  (stream)  |             |
|             |  ...              |             |
+-------------+                   +-------------+
```

## Run

Server:

```powershell
dotnet run --project samples/FileTransferSample -- server 5001 ./downloads
```

Sender:

```powershell
dotnet run --project samples/FileTransferSample -- send 5001 127.0.0.1 file1.bin file2.zip
```

Arguments:

| Position | Server | Sender |
| --- | --- | --- |
| 1 | mode `server` | mode `send` |
| 2 | port | port |
| 3 | output directory | host |
| 4..N | — | one or more file paths |

## Key code shape

```csharp
// Sender side, per file
var meta = await mux.OpenMessageTransitAsync(...);
await meta.SendAsync(new FileMeta(name, size));

var s = mux.OpenStream($"file:{name}");
await s.WaitForReadyAsync();
await using var fs = File.OpenRead(path);
await fs.CopyToAsync(s);
```

```csharp
// Server side, per inbound metadata
var s = await mux.AcceptStreamAsync($"file:{meta.Name}");
await using var fs = File.Create(Path.Combine(outputDir, meta.Name));
await s.CopyToAsync(fs);
```
