using System.Net;
using System.Net.Sockets;
using NetConduit;
using NetConduit.Mesh;
using NetConduit.Models;
using NetConduit.Transport.Tcp;

// =====================================================================
//  THREE HOSTS — MESH OWNS THE MUXES (same pattern as StreamMultiplexer)
//
//   HOST A                    HOST B                         HOST C
//   ┌─────────────┐          ┌──────────────┐              ┌──────────────┐
//   │ meshA        │          │ meshB        │              │ meshC        │
//   │ NodeId="A"   │          │ NodeId="B"   │              │ NodeId="C"   │
//   │             │          │             │              │             │
//   │ neighbor:B  │──TCP──►  │ ◄─ neighbor:A (server)    │             │
//   │  (client)   │  :19001  │             │              │             │
//   │             │          │ neighbor:C  │──TCP──►      │ ◄─ neighbor:B  │
//   │             │          │  (client)   │  :19002      │   (server)   │
//   └─────────────┘          └──────────────┘              └──────────────┘
//
//   API: mesh.AddNeighbor(name, MultiplexerOptions)
//   Mesh calls StreamMultiplexer.Create() + Start() internally.
//   Same pattern as StreamMultiplexer → MultiplexerOptions → StreamFactory.
//   User never sees the underlying mux — mesh owns its lifecycle.
// =====================================================================

// ── B & C listen; A is a leaf ────────────────────────────────────

var hostB_listener = new TcpListener(IPAddress.Loopback, 19001);
var hostC_listener = new TcpListener(IPAddress.Loopback, 19002);
hostB_listener.Start();
hostC_listener.Start();

Console.WriteLine("[INIT] B listening :19001, C listening :19002, A is leaf");

// ── Create mesh identities ────────────────────────────────────────

await using var meshA = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "A" });
await using var meshB = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "B" });
await using var meshC = MeshMultiplexer.Create(new MeshMultiplexerOptions { NodeId = "C" });

meshA.Start();
meshB.Start();
meshC.Start();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

// ── Wire neighbors — one line per link ────────────────────────────
//     mesh creates the StreamMultiplexer, starts it, owns its lifecycle.

meshA.AddNeighbor("B", TcpMultiplexer.CreateOptions("localhost", 19001));
meshB.AddNeighbor("A", TcpMultiplexer.CreateServerOptions(hostB_listener));
meshB.AddNeighbor("C", TcpMultiplexer.CreateOptions("localhost", 19002));
meshC.AddNeighbor("B", TcpMultiplexer.CreateServerOptions(hostC_listener));

// No manual mux setup. No WaitForReadyAsync on muxes — mesh handles it.

await Task.WhenAll(
    meshA.WaitForReadyAsync(cts.Token),
    meshB.WaitForReadyAsync(cts.Token),
    meshC.WaitForReadyAsync(cts.Token));

Console.WriteLine("[MESH] Topology converged");

// ── A discovers C (via B) ────────────────────────────────────────

Console.Write("[ROUTE] A waiting for C (via B)... ");
var aReachesC = await meshA.WaitForReachableAsync("C", cts.Token);
Console.WriteLine($"{aReachesC.HopCount} hops");

// ── Open routed mux A → C (B relays) ─────────────────────────────

Console.Write("[ROUTE] Opening A → C... ");
var acceptAtC = Task.Run(async () =>
{
    await foreach (var inc in meshC.AcceptMultiplexersAsync(cts.Token))
        return inc;
    throw new Exception("never accepted");
});

var subA = await meshA.OpenMultiplexerAsync("C", "chat", cts.Token);
var atC = await acceptAtC.WaitAsync(cts.Token);
Console.WriteLine($"session {subA.SessionId}");

// ── Send data A → C ──────────────────────────────────────────────

var writer = subA.OpenChannel(new ChannelOptions { ChannelId = "greeting" });
var reader = atC.Multiplexer.AcceptChannel("greeting");
await Task.WhenAll(writer.WaitForReadyAsync(cts.Token), reader.WaitForReadyAsync(cts.Token));

var msg = "Hello A→C!"u8.ToArray();
await writer.WriteAsync(msg, cts.Token);

var buf = new byte[64];
int got = 0;
while (got < msg.Length)
{
    int n = await reader.ReadAsync(buf.AsMemory(got), cts.Token);
    if (n == 0) break;
    got += n;
}

Console.WriteLine($"[DATA]  A sent {msg.Length}B → C received {got}B");
Console.WriteLine($"[DATA]  \"{System.Text.Encoding.UTF8.GetString(buf.AsSpan(0, got))}\"");
Console.WriteLine($"[STATS] B relayed {meshB.Stats.RelayBytesForwarded} bytes");

// ── Cleanup (mesh disposes muxes automatically) ────────────────────

await writer.DisposeAsync();
await reader.DisposeAsync();
await subA.DisposeAsync();
await atC.Multiplexer.DisposeAsync();

hostB_listener.Stop();
hostC_listener.Stop();

Console.WriteLine("[DONE]");
return 0;
