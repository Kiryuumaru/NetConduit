# RemoteShellSample

An SSH-like CLI tool. The client opens a persistent shell session on the server (a long-lived child process such as `pwsh` or `bash`) and streams stdin/stdout/stderr over NetConduit channels.

## What it shows

- A **persistent** remote process tied to a session, kept across multiple client commands.
- `MessageTransit` for control (open shell, resize, signal).
- `DuplexStreamTransit` for the live tty stream (stdin one way, stdout/stderr the other).
- Graceful close: client `Ctrl+C` sends a signal; server tears down the child process cleanly.

## Topology

```
   +----------+   ctrl (msg)    +----------+
   |          |---------------->|          |  fork child
   |  client  |                 |  server  |--- pwsh / bash
   |  (your   |   tty (duplex   |          |
   |  shell)  |    stream)      |          |
   |          |<--------------->|          |
   +----------+                 +----------+
```

## Run

Server:

```powershell
dotnet run --project samples/RemoteShellSample -- server 5000
```

Client:

```powershell
dotnet run --project samples/RemoteShellSample -- client 5000 127.0.0.1
```

| Arg | Server | Client |
| --- | --- | --- |
| 1 | `server` | `client` |
| 2 | port | port |
| 3 | — | host |

## Key code shape

```csharp
// Server
var ctrl = await mux.AcceptMessageTransitAsync("ctrl", ShellJson.Default.ShellMessage);
var tty  = await mux.AcceptDuplexStreamAsync("tty");

var proc = Process.Start(new ProcessStartInfo("pwsh") {
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    UseShellExecute = false,
});

// Pipe child stdout -> tty
_ = proc.StandardOutput.BaseStream.CopyToAsync(tty);
// Pipe tty -> child stdin
_ = tty.CopyToAsync(proc.StandardInput.BaseStream);
```

```csharp
// Client
var ctrl = await mux.OpenMessageTransitAsync("ctrl", ShellJson.Default.ShellMessage);
var tty  = await mux.OpenDuplexStreamAsync("tty");

_ = tty.CopyToAsync(Console.OpenStandardOutput());
_ = Console.OpenStandardInput().CopyToAsync(tty);
```
