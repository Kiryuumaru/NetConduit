using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Tcp;
using NetConduit.Transits;

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  NetConduit Chat Demo - Multiplexed TCP Chat with MessageTransit");
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
    Console.WriteLine("Features:");
    Console.WriteLine("  - Uses MessageTransit for type-safe JSON message passing");
    Console.WriteLine("  - Two multiplexed channels (send/receive)");
    Console.WriteLine("  - Built-in /stats and /quit commands");
}

async Task RunServerAsync(int port, string username, CancellationToken ct)
{
    Console.WriteLine($"[System] Starting server on port {port} as '{username}'...");
    
    using var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine($"[System] Listening for connections...");
    
    // Accept connection with simple extension method
    await using var connection = await listener.AcceptMuxAsync(cancellationToken: ct);
    Console.WriteLine($"[System] Client connected");
    
    var runTask = await connection.StartAsync(ct);
    
    // Server opens send channel, accepts receive channel
    var sendChannel = await connection.OpenChannelAsync(new ChannelOptions { ChannelId = "server-to-client" }, ct);
    
    Console.WriteLine("[System] Waiting for client's channel...");
    ReadChannel? receiveChannel = null;
    await foreach (var ch in connection.AcceptChannelsAsync(ct))
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
    
    await RunChatLoopAsync(connection, sendChannel, receiveChannel, username, ct);
}

async Task RunClientAsync(string host, int port, string username, CancellationToken ct)
{
    Console.WriteLine($"[System] Connecting to {host}:{port} as '{username}'...");
    
    // Connect with simple extension method
    using var client = new TcpClient();
    await using var connection = await client.ConnectMuxAsync(host, port, cancellationToken: ct);
    Console.WriteLine("[System] Connected to server");
    
    var runTask = await connection.StartAsync(ct);
    
    // Client opens send channel, accepts receive channel
    var sendChannel = await connection.OpenChannelAsync(new ChannelOptions { ChannelId = "client-to-server" }, ct);
    
    Console.WriteLine("[System] Waiting for server's channel...");
    ReadChannel? receiveChannel = null;
    await foreach (var ch in connection.AcceptChannelsAsync(ct))
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
    
    await RunChatLoopAsync(connection, sendChannel, receiveChannel, username, ct);
}

async Task RunChatLoopAsync(TcpMultiplexerConnection connection, WriteChannel sendChannel, ReadChannel receiveChannel, string username, CancellationToken ct)
{
    // Create MessageTransit for type-safe messaging
    await using var transit = new MessageTransit<ChatMessage, ChatMessage>(
        sendChannel, receiveChannel,
        ChatJsonContext.Default.ChatMessage,
        ChatJsonContext.Default.ChatMessage);
    
    // Start receive loop in background using ReceiveAllAsync
    var receiveTask = Task.Run(async () =>
    {
        try
        {
            await foreach (var message in transit.ReceiveAllAsync(ct))
            {
                // Move cursor to beginning of line, clear it, print message, then restore prompt
                Console.Write("\r\x1b[K"); // Clear current line
                Console.WriteLine($"[{message.Username}] {message.Text}");
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
                Console.WriteLine($"[Stats] Multiplexer: Sent={connection.Stats.BytesSent:N0}B, Received={connection.Stats.BytesReceived:N0}B");
                Console.WriteLine($"[Stats] Send Channel: Sent={sendChannel.Stats.BytesSent:N0}B");
                Console.WriteLine($"[Stats] Recv Channel: Received={receiveChannel.Stats.BytesReceived:N0}B");
                continue;
            }
            
            // Send message using MessageTransit
            await transit.SendAsync(new ChatMessage(username, input, DateTime.UtcNow), ct);
        }
    }
    catch (OperationCanceledException) { }
    
    await sendChannel.CloseAsync(ct);
}

// Chat message record for type-safe serialization
public record ChatMessage(string Username, string Text, DateTime Timestamp);

// AOT-compatible JSON serialization context
[JsonSerializable(typeof(ChatMessage))]
internal partial class ChatJsonContext : JsonSerializerContext { }
