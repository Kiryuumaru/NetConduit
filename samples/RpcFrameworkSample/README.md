# RpcFrameworkSample

A small RPC framework built on `MessageTransit<RpcRequest, RpcResponse>`. Demonstrates that NetConduit is sufficient on its own to build typed request/response APIs — no extra protocol library needed.

## What it shows

- Asymmetric `MessageTransit<TSend, TReceive>` where the server's send/receive types are the **client's reverse**.
- A `SimpleRpcClient.CallAsync<T>` helper that correlates requests and responses by `RequestId`.
- AOT-safe JSON via `JsonSerializerContext`.
- Error propagation: the server returns `RpcResponse { Success = false, Error = "..." }` for unknown methods.

## Topology

```
   client                                  server
     |   { "Method": "Add", "Args": [...] }   |
     | -----------------------------------> |
     |   { "RequestId": ..., "Result": 7 }    |
     | <----------------------------------- |
```

Built-in methods in the sample server: `Add`, `Multiply`, `Concat`, `GetTime`, `Echo`. Unknown methods return an error.

## Run

```powershell
# Server
dotnet run --project samples/RpcFrameworkSample -- server 127.0.0.1 5000

# Client
dotnet run --project samples/RpcFrameworkSample -- client 127.0.0.1 5000
```

| Arg | Meaning |
| --- | --- |
| 1 | `server` or `client` |
| 2 | host (default `127.0.0.1`) |
| 3 | port (default `5000`) |

## Key code shape

```csharp
// Client side
var transit = await mux.OpenMessageTransitAsync<RpcRequest, RpcResponse>(
    "rpc", RpcJson.Default.RpcRequest, RpcJson.Default.RpcResponse);

var client = new SimpleRpcClient(transit);
int sum = await client.CallAsync<int>("Add", new[] { 2, 3 });
```

```csharp
// Server side
var transit = await mux.AcceptMessageTransitAsync<RpcResponse, RpcRequest>(
    "rpc", RpcJson.Default.RpcResponse, RpcJson.Default.RpcRequest);

await foreach (var req in transit.ReceiveAllAsync())
{
    var resp = HandleRpc(req);
    await transit.SendAsync(resp);
}
```

Notice that the server's `MessageTransit` type arguments are **swapped** versus the client.
