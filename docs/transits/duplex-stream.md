# DuplexStreamTransit

Bidirectional stream abstraction over two simplex channels. Read and write on the same stream object.

## Basic Usage

```csharp
using NetConduit.Transits;

// Side A opens duplex stream
var duplexA = await muxA.OpenDuplexStreamAsync("data");

// Side B accepts (pairs with A's open)
var duplexB = await muxB.AcceptDuplexStreamAsync("data");

// A writes, B reads
await duplexA.WriteAsync(Encoding.UTF8.GetBytes("Hello from A!"));
var buffer = new byte[1024];
var bytesRead = await duplexB.ReadAsync(buffer);
Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, bytesRead));

// B writes, A reads
await duplexB.WriteAsync(Encoding.UTF8.GetBytes("Hello from B!"));
bytesRead = await duplexA.ReadAsync(buffer);
Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, bytesRead));

// Cleanup
await duplexA.DisposeAsync();
await duplexB.DisposeAsync();
```

## Channel Naming

Single channel ID creates paired channels:

| Method | Write Channel | Read Channel |
|--------|--------------|--------------|
| `OpenDuplexStreamAsync("data")` | `data>>` | `data<<` |
| `AcceptDuplexStreamAsync("data")` | `data<<` | `data>>` |

Or specify explicit channel names:

```csharp
// Custom channel names
var duplex = await mux.OpenDuplexStreamAsync("my-outbound", "my-inbound");
```

## Stream API

DuplexStreamTransit inherits from `Stream`:

```csharp
var duplex = await mux.OpenDuplexStreamAsync("data");

// All Stream methods work
await duplex.WriteAsync(data);
await duplex.FlushAsync();
var bytesRead = await duplex.ReadAsync(buffer);

// Synchronous operations (if needed)
duplex.Write(data);
int n = duplex.Read(buffer);

// Stream properties
bool canRead = duplex.CanRead;   // true
bool canWrite = duplex.CanWrite; // true
bool canSeek = duplex.CanSeek;   // false (not seekable)
```

## Use with Standard APIs

Works with any API expecting a Stream:

```csharp
var duplex = await mux.OpenDuplexStreamAsync("text");

// StreamReader/Writer
using var reader = new StreamReader(duplex, leaveOpen: true);
using var writer = new StreamWriter(duplex, leaveOpen: true);

await writer.WriteLineAsync("Hello!");
await writer.FlushAsync();

string line = await reader.ReadLineAsync();
```

## Binary Protocols

Perfect for custom binary protocols:

```csharp
var duplex = await mux.OpenDuplexStreamAsync("binary");

using var reader = new BinaryReader(duplex, Encoding.UTF8, leaveOpen: true);
using var writer = new BinaryWriter(duplex, Encoding.UTF8, leaveOpen: true);

// Write structured data
writer.Write(messageType);
writer.Write(payload.Length);
writer.Write(payload);
writer.Flush();

// Read structured data
int type = reader.ReadInt32();
int length = reader.ReadInt32();
byte[] data = reader.ReadBytes(length);
```

## File Transfer Example

```csharp
// Sender
var duplex = await mux.OpenDuplexStreamAsync("file");
await using var file = File.OpenRead("large-file.bin");
await file.CopyToAsync(duplex);

// Signal end of file by closing write side
await duplex.WriteChannel.DisposeAsync();

// Receiver
var duplex = await mux.AcceptDuplexStreamAsync("file");
await using var file = File.Create("received-file.bin");
await duplex.CopyToAsync(file);
```

## Properties

```csharp
var duplex = await mux.OpenDuplexStreamAsync("data");

// Access underlying channels
WriteChannel writeChannel = duplex.WriteChannel;
ReadChannel readChannel = duplex.ReadChannel;

// Channel IDs
string writeId = duplex.WriteChannelId;  // "data>>"
string readId = duplex.ReadChannelId;    // "data<<"

// Connection state
bool isConnected = duplex.IsConnected;
```

## Half-Close

Close one direction while keeping the other open:

```csharp
var duplex = await mux.OpenDuplexStreamAsync("data");

// Send all data
await duplex.WriteAsync(data);

// Close write side (signals EOF to reader)
await duplex.WriteChannel.DisposeAsync();

// Can still read responses
while (true)
{
    var bytesRead = await duplex.ReadAsync(buffer);
    if (bytesRead == 0) break;  // EOF
    ProcessData(buffer, bytesRead);
}
```

## Timeouts

```csharp
var duplex = await mux.OpenDuplexStreamAsync("data");

// Read with timeout
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    var bytesRead = await duplex.ReadAsync(buffer, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Read timed out");
}
```

## Use Cases

**TCP tunnel / port forwarding:**
```csharp
// Forward local port through multiplexer
var localClient = new TcpClient();
await localClient.ConnectAsync("localhost", 8080);

var duplex = await mux.OpenDuplexStreamAsync($"tunnel-{Guid.NewGuid()}");

// Bidirectional copy
var localStream = localClient.GetStream();
var copyToRemote = localStream.CopyToAsync(duplex);
var copyFromRemote = duplex.CopyToAsync(localStream);
await Task.WhenAny(copyToRemote, copyFromRemote);
```

**Interactive shell:**
```csharp
// stdin/stdout over multiplexer
var duplex = await mux.OpenDuplexStreamAsync("shell");

// Attach to process
process.StandardInput.BaseStream = duplex;
process.StandardOutput.BaseStream = duplex;
```

**Proxy protocols:**
```csharp
// HTTP CONNECT tunnel
var duplex = await mux.OpenDuplexStreamAsync("proxy");
// Forward bytes bidirectionally
```

## Tips

**Use `leaveOpen: true` with wrappers:**
```csharp
using var reader = new StreamReader(duplex, leaveOpen: true);
// Duplex not disposed when reader is disposed
```

**Buffer sizes matter:**
```csharp
// Larger buffer = fewer round trips for bulk data
var buffer = new byte[64 * 1024];  // 64KB
```

**Close properly:**
```csharp
// Sends FIN frame to signal EOF
await duplex.DisposeAsync();
```
