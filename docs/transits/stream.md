# StreamTransit

Simple one-way stream wrapper for [channels](../concepts/channels.md). Write to one side, read from the other. See [Transit Overview](index.md) for alternatives.

## Basic Usage

```csharp
using NetConduit.Transits;

// Sender opens write stream
var writeStream = await mux.OpenStreamAsync("upload");

// Receiver accepts read stream
var readStream = await mux.AcceptStreamAsync("upload");

// Write data
await writeStream.WriteAsync(Encoding.UTF8.GetBytes("Hello!"));

// Read data
var buffer = new byte[1024];
var bytesRead = await readStream.ReadAsync(buffer);
Console.WriteLine(Encoding.UTF8.GetString(buffer, 0, bytesRead));

// Cleanup
await writeStream.DisposeAsync();
await readStream.DisposeAsync();
```

## When to Use

Use StreamTransit when you need:
- **Simple one-way data flow**
- **Stream API compatibility**
- **File uploads/downloads**

Use [raw channels](../concepts/channels.md) directly when you want more control.

## Open vs Accept

| Operation | Channel Type | Direction |
|-----------|--------------|-----------|
| `OpenStreamAsync("data")` | WriteChannel | You write |
| `AcceptStreamAsync("data")` | ReadChannel | You read |

Side A opens, Side B accepts:

```csharp
// A sends to B
var sendStream = await muxA.OpenStreamAsync("a-to-b");
var receiveStream = await muxB.AcceptStreamAsync("a-to-b");

// B sends to A
var sendStream2 = await muxB.OpenStreamAsync("b-to-a");
var receiveStream2 = await muxA.AcceptStreamAsync("b-to-a");
```

## Stream API

StreamTransit inherits from `Stream`:

```csharp
// Write stream
var write = await mux.OpenStreamAsync("data");
await write.WriteAsync(data);
await write.FlushAsync();
write.CanWrite;  // true
write.CanRead;   // false

// Read stream
var read = await mux.AcceptStreamAsync("data");
var n = await read.ReadAsync(buffer);
read.CanRead;   // true
read.CanWrite;  // false
```

## File Transfer

```csharp
// Upload file
var uploadStream = await mux.OpenStreamAsync("file-upload");
await using var file = File.OpenRead("document.pdf");
await file.CopyToAsync(uploadStream);
await uploadStream.DisposeAsync();  // Signal EOF

// Download file
var downloadStream = await mux.AcceptStreamAsync("file-upload");
await using var output = File.Create("received.pdf");
await downloadStream.CopyToAsync(output);
```

## Multiple Streams

Create multiple concurrent streams:

```csharp
// Multiple parallel uploads
var upload1 = await mux.OpenStreamAsync("file-1");
var upload2 = await mux.OpenStreamAsync("file-2");
var upload3 = await mux.OpenStreamAsync("file-3");

// Send in parallel
await Task.WhenAll(
    file1.CopyToAsync(upload1),
    file2.CopyToAsync(upload2),
    file3.CopyToAsync(upload3)
);
```

## Properties

```csharp
// Write stream
var write = await mux.OpenStreamAsync("data");
string channelId = write.ChannelId;  // "data"
bool isOpen = write.IsOpen;

// Read stream
var read = await mux.AcceptStreamAsync("data");
string channelId = read.ChannelId;  // "data"
bool isOpen = read.IsOpen;
```

## EOF Detection

Read returns 0 bytes when sender closes:

```csharp
while (true)
{
    var bytesRead = await readStream.ReadAsync(buffer);
    if (bytesRead == 0)
    {
        Console.WriteLine("Sender closed stream");
        break;
    }
    ProcessData(buffer, bytesRead);
}
```

## Timeouts

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

try
{
    await writeStream.WriteAsync(data, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Write timed out");
}
```

## Error Handling

```csharp
try
{
    await writeStream.WriteAsync(data);
}
catch (ChannelClosedException ex)
{
    Console.WriteLine($"Channel closed: {ex.CloseReason}");
}
catch (IOException ex)
{
    Console.WriteLine($"I/O error: {ex.Message}");
}
```

## Use Cases

**Log streaming:**
```csharp
var logStream = await mux.OpenStreamAsync("logs");
await logStream.WriteAsync(Encoding.UTF8.GetBytes($"[{DateTime.Now}] Event occurred\n"));
```

**Sensor data:**
```csharp
var sensorStream = await mux.OpenStreamAsync("sensor-1");
while (running)
{
    var reading = await sensor.ReadAsync();
    await sensorStream.WriteAsync(BitConverter.GetBytes(reading));
}
```

**Media streaming:**
```csharp
var audioStream = await mux.OpenStreamAsync("audio");
await audioEncoder.OutputPipe.CopyToAsync(audioStream);
```

## Tips

**Dispose to signal EOF:**
```csharp
await writeStream.DisposeAsync();  // Receiver sees 0 bytes on next read
```

**Use larger buffers for bulk data:**
```csharp
var buffer = new byte[64 * 1024];  // 64KB
```

**Raw channel alternative:**
```csharp
// StreamTransit is just a thin wrapper
// For maximum control, use channels directly
var channel = await mux.OpenChannelAsync(new() { ChannelId = "data" });
await channel.WriteAsync(data);
```
