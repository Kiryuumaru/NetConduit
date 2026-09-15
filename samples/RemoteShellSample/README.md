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

Server (loopback-only by default; auth required unless you pass the insecure escape hatch):

```powershell
$env:SHELL_TOKEN = "correct horse battery staple"
dotnet run --project samples/RemoteShellSample -- server 5000 --auth-env SHELL_TOKEN
```

Client (arg order `client <port> <host>` is intentional):

```powershell
dotnet run --project samples/RemoteShellSample -- client 5000 127.0.0.1 --auth-env SHELL_TOKEN
```

### Flags

| Flag | Effect |
| --- | --- |
| `--bind loopback` (default) | Listen on localhost only. |
| `--bind any` | Listen on all interfaces. **WARNING:** exposes a remote shell to the network. Token + shell I/O travel as cleartext (no TLS): use loopback only, or tunnel over TLS/SSH on untrusted networks. |
| `--auth <token>` | Shared token the client must prove before any shell starts. |
| `--auth-env NAME` | Read the token from environment variable `NAME` (preferred over `--auth`, which leaks via process lists). |
| `--allow-no-auth` | **Insecure, local demos only.** Skips the auth gate; anyone who can connect gets a shell. |

The server refuses to start without `--auth`/`--auth-env` or explicit `--allow-no-auth` (fail-closed).
Connection setup and the auth gate are each bounded (~5s per stage, so an unauthenticated peer is dropped after ~10s worst-case); failures are logged with endpoint + reason and the
connection is closed with no shell process started. Tokens are compared in constant time over content (lengths visible) and never logged.

The token and all shell I/O travel as cleartext with no TLS: use loopback only, or tunnel over TLS/SSH on untrusted networks.

> Do not expose this sample to untrusted networks.

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
