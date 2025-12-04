using System.Net;
using System.Net.Sockets;
using System.Text;
using NetConduit;
using NetConduit.Tcp;

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  NetConduit Chat Demo - Multiplexed TCP Chat");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();

if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
{
    PrintUsage();
    return;
}

var mode = args[0].ToLowerInvariant();
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5000;
var host = args.Length > 2 ? args[2] : "127.0.0.1";
var username = args.Length > 3 ? args[3] : $"User{Random.Shared.Next(1000, 9999)}";

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    if (mode == "server" || mode == "s")
    {
        await RunServerAsync(port, username, cts.Token);
    }
    else if (mode == "client" || mode == "c")
    {
        await RunClientAsync(host, port, username, cts.Token);
    }
    else
    {
        Console.WriteLine($"Unknown mode: {mode}");
        PrintUsage();
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[System] Chat ended.");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[Error] {ex.Message}");
}

void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Server: dotnet run -- server [port] [username]");
    Console.WriteLine("  Client: dotnet run -- client [port] [host] [username]");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -- server 5000 Alice");
    Console.WriteLine("  dotnet run -- client 5000 127.0.0.1 Bob");
    Console.WriteLine();
    Console.WriteLine("Chat uses two multiplexed channels:");
    Console.WriteLine("  - One for sending messages (you -> peer)");
    Console.WriteLine("  - One for receiving messages (peer -> you)");
}

async Task RunServerAsync(int port, string username, CancellationToken ct)
{
    Console.WriteLine($"[System] Starting server on port {port} as '{username}'...");
    
    using var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine($"[System] Listening for connections...");
    
    var options = new MultiplexerOptions { EnableReconnection = true };
    await using var mux = await listener.AcceptMuxAsync(options, ct);
    Console.WriteLine($"[System] Client connected");
    
    var runTask = await mux.StartAsync(ct);
    
    // Server opens send channel, accepts receive channel
    await using var sendChannel = await mux.OpenChannelAsync(
        new ChannelOptions { ChannelId = "server-to-client" }, ct);
    
    Console.WriteLine("[System] Waiting for client's channel...");
    ReadChannel? receiveChannel = null;
    await foreach (var ch in mux.AcceptChannelsAsync(ct))
    {
        receiveChannel = ch;
        break;
    }
    
    if (receiveChannel == null)
    {
        Console.WriteLine("[Error] Failed to accept client channel");
        return;
    }
    
    Console.WriteLine("[System] Chat ready! Type messages and press Enter. Ctrl+C to exit.");
    Console.WriteLine("───────────────────────────────────────────────────────────────");
    
    await RunChatLoopAsync(mux, sendChannel, receiveChannel, username, ct);
}

async Task RunClientAsync(string host, int port, string username, CancellationToken ct)
{
    Console.WriteLine($"[System] Connecting to {host}:{port} as '{username}'...");
    
    using var client = new TcpClient();
    var options = new MultiplexerOptions { EnableReconnection = true };
    await using var mux = await client.ConnectMuxAsync(host, port, options, ct);
    Console.WriteLine("[System] Connected to server");
    
    var runTask = await mux.StartAsync(ct);
    
    // Client opens send channel, accepts receive channel
    await using var sendChannel = await mux.OpenChannelAsync(
        new ChannelOptions { ChannelId = "client-to-server" }, ct);
    
    Console.WriteLine("[System] Waiting for server's channel...");
    ReadChannel? receiveChannel = null;
    await foreach (var ch in mux.AcceptChannelsAsync(ct))
    {
        receiveChannel = ch;
        break;
    }
    
    if (receiveChannel == null)
    {
        Console.WriteLine("[Error] Failed to accept server channel");
        return;
    }
    
    Console.WriteLine("[System] Chat ready! Type messages and press Enter. Ctrl+C to exit.");
    Console.WriteLine("───────────────────────────────────────────────────────────────");
    
    await RunChatLoopAsync(mux, sendChannel, receiveChannel, username, ct);
}

async Task RunChatLoopAsync(TcpMultiplexerConnection mux, WriteChannel sendChannel, ReadChannel receiveChannel, string username, CancellationToken ct)
{
    // Start receive loop in background
    var receiveTask = Task.Run(async () =>
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await receiveChannel.ReadAsync(buffer, ct);
                if (read == 0) break;
                
                var message = Encoding.UTF8.GetString(buffer, 0, read);
                
                // Move cursor to beginning of line, clear it, print message, then restore prompt
                Console.Write("\r\x1b[K"); // Clear current line
                Console.WriteLine(message);
                Console.Write($"{username}> ");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[Error receiving] {ex.Message}");
        }
    }, ct);
    
    // Send loop on main thread
    try
    {
        while (!ct.IsCancellationRequested)
        {
            Console.Write($"{username}> ");
            var input = Console.ReadLine();
            
            if (string.IsNullOrEmpty(input))
                continue;
            
            if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            
            if (input.Equals("/stats", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Stats] Multiplexer: Sent={mux.Stats.BytesSent:N0}B, Received={mux.Stats.BytesReceived:N0}B");
                Console.WriteLine($"[Stats] Send Channel: Sent={sendChannel.Stats.BytesSent:N0}B");
                Console.WriteLine($"[Stats] Recv Channel: Received={receiveChannel.Stats.BytesReceived:N0}B");
                continue;
            }
            
            var formatted = $"[{username}] {input}";
            var bytes = Encoding.UTF8.GetBytes(formatted);
            await sendChannel.WriteAsync(bytes, ct);
        }
    }
    catch (OperationCanceledException) { }
    
    await sendChannel.CloseAsync(ct);
}
