using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Tcp;
using NetConduit.Transits;
using NetConduit.WebSocket;

// ═══════════════════════════════════════════════════════════════
//   NetConduit Group Chat - Multi-client TCP/WebSocket Demo
// ═══════════════════════════════════════════════════════════════

if (args.Length < 2 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return;
}

var mode = args[0].ToLowerInvariant();
var transport = args[1].ToLowerInvariant();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    if (mode == "server")
    {
        if (transport == "tcp")
        {
            if (args.Length < 3 || !int.TryParse(args[2], out var port))
            {
                Console.WriteLine("Usage: server tcp <port>");
                return;
            }
            await RunTcpServerAsync(port, cts.Token);
        }
        else if (transport == "ws")
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: server ws <port>/<path>");
                return;
            }
            var (port, path) = ParsePortPath(args[2]);
            await RunWebSocketServerAsync(port, path, cts.Token);
        }
        else
        {
            Console.WriteLine($"Unknown transport: {transport}. Use 'tcp' or 'ws'.");
        }
    }
    else if (mode == "client")
    {
        if (args.Length < 5)
        {
            Console.WriteLine("Usage: client <tcp|ws> <port>[/path] <host> <username>");
            return;
        }
        var host = args[3];
        var username = args[4];

        if (transport == "tcp")
        {
            if (!int.TryParse(args[2], out var port))
            {
                Console.WriteLine("Invalid port");
                return;
            }
            await RunTcpClientAsync(host, port, username, cts.Token);
        }
        else if (transport == "ws")
        {
            var (port, path) = ParsePortPath(args[2]);
            await RunWebSocketClientAsync(host, port, path, username, cts.Token);
        }
        else
        {
            Console.WriteLine($"Unknown transport: {transport}. Use 'tcp' or 'ws'.");
        }
    }
    else
    {
        Console.WriteLine($"Unknown mode: {mode}. Use 'server' or 'client'.");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[System] Shutting down...");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[Error] {ex.Message}");
}

return;

// ═══════════════════════════════════════════════════════════════
//   Helper Functions
// ═══════════════════════════════════════════════════════════════

static (int port, string path) ParsePortPath(string input)
{
    var slashIndex = input.IndexOf('/');
    if (slashIndex == -1)
        return (int.Parse(input), "/");
    return (int.Parse(input[..slashIndex]), "/" + input[(slashIndex + 1)..]);
}

static void PrintUsage()
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine("  NetConduit Group Chat");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  Server (TCP):       dotnet run -- server tcp <port>");
    Console.WriteLine("  Server (WebSocket): dotnet run -- server ws <port>/<path>");
    Console.WriteLine("  Client (TCP):       dotnet run -- client tcp <port> <host> <username>");
    Console.WriteLine("  Client (WebSocket): dotnet run -- client ws <port>/<path> <host> <username>");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -- server tcp 5000");
    Console.WriteLine("  dotnet run -- client tcp 5000 127.0.0.1 Alice");
    Console.WriteLine();
    Console.WriteLine("  dotnet run -- server ws 5000/chat");
    Console.WriteLine("  dotnet run -- client ws 5000/chat 127.0.0.1 Bob");
    Console.WriteLine();
    Console.WriteLine("Commands (in chat):");
    Console.WriteLine("  /list   - Show online users");
    Console.WriteLine("  /stats  - Show connection statistics");
    Console.WriteLine("  /quit   - Disconnect and exit");
    Console.WriteLine("  /kick <user> - (Server only) Kick a user");
}

// ═══════════════════════════════════════════════════════════════
//   TCP Server
// ═══════════════════════════════════════════════════════════════

async Task RunTcpServerAsync(int port, CancellationToken ct)
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine("  NetConduit Group Chat Server (TCP)");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine();

    var server = new ChatServer();
    var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();

    Console.WriteLine($"[Server] Listening on port {port}...");
    Console.WriteLine("[Server] Commands: /list, /stats, /kick <user>, /quit");
    Console.WriteLine();

    var acceptTask = AcceptTcpClientsAsync(listener, server, ct);
    var commandTask = RunServerCommandLoopAsync(server, ct);

    try
    {
        await Task.WhenAny(acceptTask, commandTask);
    }
    finally
    {
        listener.Stop();
    }
}

async Task AcceptTcpClientsAsync(TcpListener listener, ChatServer server, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var tcpClient = await listener.AcceptTcpClientAsync(ct);
            _ = HandleNewTcpClientAsync(tcpClient, server, ct);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server] Accept error: {ex.Message}");
        }
    }
}

async Task HandleNewTcpClientAsync(TcpClient tcpClient, ChatServer server, CancellationToken ct)
{
    try
    {
        var accepted = false;
        var options = new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                if (accepted)
                    throw new InvalidOperationException("Server-side multiplexer does not support reconnection.");
                accepted = true;
                var stream = tcpClient.GetStream();
                return Task.FromResult<IStreamPair>(new StreamPair(stream, tcpClient));
            }
        };
        var mux = StreamMultiplexer.Create(options);
        _ = mux.Start(ct);
        await mux.WaitForReadyAsync(ct);

        ReadChannel? chatChannel = null;
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        acceptCts.CancelAfter(TimeSpan.FromSeconds(10));

        await foreach (var ch in mux.AcceptChannelsAsync(acceptCts.Token))
        {
            if (ch.ChannelId == "chat")
            {
                chatChannel = ch;
                break;
            }
        }

        if (chatChannel == null)
        {
            await mux.DisposeAsync();
            return;
        }

        var buffer = new byte[1024];
        var bytesRead = await chatChannel.ReadAsync(buffer, ct);
        var joinEvent = JsonSerializer.Deserialize(buffer.AsSpan(0, bytesRead), ChatJsonContext.Default.ChatEvent);

        if (joinEvent?.Type != ChatEventType.UserJoined || string.IsNullOrEmpty(joinEvent.Username))
        {
            await mux.DisposeAsync();
            return;
        }

        var username = joinEvent.Username;

        if (server.Clients.ContainsKey(username))
        {
            await mux.DisposeAsync();
            Console.WriteLine($"[Server] Rejected duplicate username: {username}");
            return;
        }

        var broadcastChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "broadcast" }, ct);
        var clientState = new ClientState(username, mux, broadcastChannel, chatChannel);

        if (!server.TryAddClient(username, clientState))
        {
            await clientState.DisposeAsync();
            return;
        }

        Console.WriteLine($"[Server] {username} connected ({server.Clients.Count} online)");

        await server.BroadcastAsync(new ChatEvent
        {
            Type = ChatEventType.UserJoined,
            Username = username,
            OnlineUsers = server.Clients.Keys.ToArray()
        });

        await HandleClientLoopAsync(clientState, server, ct);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.WriteLine($"[Server] Client handling error: {ex.Message}");
    }
}

// ═══════════════════════════════════════════════════════════════
//   WebSocket Server
// ═══════════════════════════════════════════════════════════════

async Task RunWebSocketServerAsync(int port, string path, CancellationToken ct)
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine("  NetConduit Group Chat Server (WebSocket)");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine();

    var server = new ChatServer();

    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
    builder.Logging.ClearProviders();

    var app = builder.Build();
    app.UseWebSockets();

    app.Map(path, async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await HandleNewWebSocketClientAsync(webSocket, server, ct);
    });

    Console.WriteLine($"[Server] Listening on ws://0.0.0.0:{port}{path}");
    Console.WriteLine("[Server] Commands: /list, /stats, /kick <user>, /quit");
    Console.WriteLine();

    var webTask = app.RunAsync(ct);
    var commandTask = RunServerCommandLoopAsync(server, ct);

    await Task.WhenAny(webTask, commandTask);
}

async Task HandleNewWebSocketClientAsync(System.Net.WebSockets.WebSocket webSocket, ChatServer server, CancellationToken ct)
{
    try
    {
        var options = WebSocketMultiplexer.CreateServerOptions(webSocket);
        var mux = StreamMultiplexer.Create(options);
        _ = mux.Start(ct);
        await mux.WaitForReadyAsync(ct);

        ReadChannel? chatChannel = null;
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        acceptCts.CancelAfter(TimeSpan.FromSeconds(10));

        await foreach (var ch in mux.AcceptChannelsAsync(acceptCts.Token))
        {
            if (ch.ChannelId == "chat")
            {
                chatChannel = ch;
                break;
            }
        }

        if (chatChannel == null)
        {
            await mux.DisposeAsync();
            return;
        }

        var buffer = new byte[1024];
        var bytesRead = await chatChannel.ReadAsync(buffer, ct);
        var joinEvent = JsonSerializer.Deserialize(buffer.AsSpan(0, bytesRead), ChatJsonContext.Default.ChatEvent);

        if (joinEvent?.Type != ChatEventType.UserJoined || string.IsNullOrEmpty(joinEvent.Username))
        {
            await mux.DisposeAsync();
            return;
        }

        var username = joinEvent.Username;

        if (server.Clients.ContainsKey(username))
        {
            await mux.DisposeAsync();
            Console.WriteLine($"[Server] Rejected duplicate username: {username}");
            return;
        }

        var broadcastChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "broadcast" }, ct);
        var clientState = new ClientState(username, mux, broadcastChannel, chatChannel);

        if (!server.TryAddClient(username, clientState))
        {
            await clientState.DisposeAsync();
            return;
        }

        Console.WriteLine($"[Server] {username} connected ({server.Clients.Count} online)");

        await server.BroadcastAsync(new ChatEvent
        {
            Type = ChatEventType.UserJoined,
            Username = username,
            OnlineUsers = server.Clients.Keys.ToArray()
        });

        await HandleClientLoopAsync(clientState, server, ct);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.WriteLine($"[Server] WebSocket client error: {ex.Message}");
    }
}

// ═══════════════════════════════════════════════════════════════
//   Shared Server Logic
// ═══════════════════════════════════════════════════════════════

async Task HandleClientLoopAsync(ClientState client, ChatServer server, CancellationToken ct)
{
    try
    {
        await foreach (var evt in client.Transit.ReceiveAllAsync(ct))
        {
            if (evt.Type == ChatEventType.Message)
            {
                server.RecordMessage();
                await server.BroadcastAsync(new ChatEvent
                {
                    Type = ChatEventType.Message,
                    Username = client.Username,
                    Text = evt.Text
                });
            }
        }
    }
    catch (OperationCanceledException) { }
    catch { }
    finally
    {
        await server.RemoveClientAsync(client.Username);
    }
}

async Task RunServerCommandLoopAsync(ChatServer server, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        Console.Write("Server> ");
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
            continue;

        if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            break;

        if (input.Equals("/list", StringComparison.OrdinalIgnoreCase))
        {
            var users = server.Clients.Keys.ToArray();
            Console.WriteLine(users.Length > 0
                ? $"  Online users: {string.Join(", ", users)}"
                : "  No users online");
            continue;
        }

        if (input.Equals("/stats", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Connections: {server.Clients.Count} active, {server.TotalConnections} total");
            Console.WriteLine($"  Messages: {server.MessagesRelayed} relayed");
            continue;
        }

        if (input.StartsWith("/kick ", StringComparison.OrdinalIgnoreCase))
        {
            var username = input[6..].Trim();
            if (server.Clients.ContainsKey(username))
            {
                await server.RemoveClientAsync(username);
                Console.WriteLine($"  Kicked {username}");
            }
            else
            {
                Console.WriteLine($"  User '{username}' not found");
            }
            continue;
        }

        Console.WriteLine("  Unknown command. Use /list, /stats, /kick <user>, or /quit");
    }
}

// ═══════════════════════════════════════════════════════════════
//   TCP Client
// ═══════════════════════════════════════════════════════════════

async Task RunTcpClientAsync(string host, int port, string username, CancellationToken ct)
{
    PrintClientHeader("TCP");

    Console.WriteLine($"[System] Connecting to {host}:{port}...");

    var options = TcpMultiplexer.CreateOptions(host, port);
    await using var mux = StreamMultiplexer.Create(options);
    _ = mux.Start(ct);
    await mux.WaitForReadyAsync(ct);

    await RunClientChatAsync(mux, username, ct);
}

// ═══════════════════════════════════════════════════════════════
//   WebSocket Client
// ═══════════════════════════════════════════════════════════════

async Task RunWebSocketClientAsync(string host, int port, string path, string username, CancellationToken ct)
{
    PrintClientHeader("WebSocket");

    Console.WriteLine($"[System] Connecting to ws://{host}:{port}{path}...");

    var options = WebSocketMultiplexer.CreateOptions($"ws://{host}:{port}{path}");
    await using var mux = StreamMultiplexer.Create(options);
    _ = mux.Start(ct);
    await mux.WaitForReadyAsync(ct);

    await RunClientChatAsync(mux, username, ct);
}

// ═══════════════════════════════════════════════════════════════
//   Shared Client Logic
// ═══════════════════════════════════════════════════════════════

void PrintClientHeader(string transport)
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine($"  NetConduit Group Chat ({transport})");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine();
}

async Task RunClientChatAsync(IStreamMultiplexer mux, string username, CancellationToken ct)
{
    var chatChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "chat" }, ct);

    var joinBytes = JsonSerializer.SerializeToUtf8Bytes(
        new ChatEvent { Type = ChatEventType.UserJoined, Username = username },
        ChatJsonContext.Default.ChatEvent);
    await chatChannel.WriteAsync(joinBytes, ct);

    ReadChannel? broadcastChannel = null;
    using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    acceptCts.CancelAfter(TimeSpan.FromSeconds(10));

    await foreach (var ch in mux.AcceptChannelsAsync(acceptCts.Token))
    {
        if (ch.ChannelId == "broadcast")
        {
            broadcastChannel = ch;
            break;
        }
    }

    if (broadcastChannel == null)
    {
        Console.WriteLine("[Error] Failed to establish broadcast channel");
        return;
    }

    await using var transit = new MessageTransit<ChatEvent, ChatEvent>(
        chatChannel, broadcastChannel,
        ChatJsonContext.Default.ChatEvent,
        ChatJsonContext.Default.ChatEvent);

    Console.WriteLine($"[System] Connected as '{username}'");
    Console.WriteLine("───────────────────────────────────────────────────────────────");

    var consoleLock = new object();
    var inputBuffer = new System.Text.StringBuilder();
    var promptLength = username.Length + 2;
    var promptShown = false;

    void ClearCurrentLine()
    {
        var totalLength = promptLength + inputBuffer.Length;
        Console.Write("\r" + new string(' ', totalLength) + "\r");
    }

    void RedrawPromptAndInput()
    {
        Console.Write($"{username}> {inputBuffer}");
        promptShown = true;
    }

    void PrintMessage(string message)
    {
        lock (consoleLock)
        {
            ClearCurrentLine();
            Console.WriteLine(message);
            RedrawPromptAndInput();
        }
    }

    // Background receive loop
    _ = Task.Run(async () =>
    {
        try
        {
            await foreach (var evt in transit.ReceiveAllAsync(ct))
            {
                switch (evt.Type)
                {
                    case ChatEventType.Message:
                        // Skip own messages - already printed locally
                        if (evt.Username != username)
                            PrintMessage($"[{evt.Username}] {evt.Text}");
                        break;
                    case ChatEventType.UserJoined:
                        PrintMessage($"[System] {evt.Username} joined ({evt.OnlineUsers?.Length ?? 0} online)");
                        break;
                    case ChatEventType.UserLeft:
                        PrintMessage($"[System] {evt.Username} left ({evt.OnlineUsers?.Length ?? 0} online)");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }, ct);

    // Brief wait to let initial join message arrive (which prints prompt via PrintMessage)
    await Task.Delay(100, ct);

    // Send loop with character-by-character input
    // Only show initial prompt if PrintMessage hasn't already shown one
    lock (consoleLock)
    {
        if (!promptShown)
        {
            RedrawPromptAndInput();
        }
    }
    
    while (!ct.IsCancellationRequested)
    {
        if (!Console.KeyAvailable)
        {
            await Task.Delay(10, ct);
            continue;
        }

        ConsoleKeyInfo key;
        lock (consoleLock)
        {
            key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                var input = inputBuffer.ToString();
                inputBuffer.Clear();
                Console.WriteLine();

                if (string.IsNullOrWhiteSpace(input))
                {
                    Console.Write($"{username}> ");
                    continue;
                }

                if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                    break;

                if (input.Equals("/list", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("  (Online user list received via UserJoined/UserLeft events)");
                    Console.Write($"{username}> ");
                    continue;
                }

                if (input.Equals("/stats", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  Sent: {mux.Stats.BytesSent:N0} bytes | Received: {mux.Stats.BytesReceived:N0} bytes | Uptime: {mux.Stats.Uptime:hh\\:mm\\:ss}");
                    Console.Write($"{username}> ");
                    continue;
                }

                // Rewrite as formatted message
                Console.Write("\x1b[1A\r");
                Console.Write(new string(' ', promptLength + input.Length + 5));
                Console.Write('\r');
                Console.WriteLine($"[{username}] {input}");
                Console.Write($"{username}> ");

                // Send in background to not block input
                _ = transit.SendAsync(new ChatEvent
                {
                    Type = ChatEventType.Message,
                    Username = username,
                    Text = input
                }, ct);
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (inputBuffer.Length > 0)
                {
                    inputBuffer.Length--;
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                inputBuffer.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }

    Console.WriteLine("[System] Disconnected.");
}

// ═══════════════════════════════════════════════════════════════
//   Types (must be at end for top-level statements)
// ═══════════════════════════════════════════════════════════════

public enum ChatEventType
{
    Message,
    UserJoined,
    UserLeft
}

public sealed class ChatEvent
{
    public ChatEventType Type { get; init; }
    public string Username { get; init; } = "";
    public string? Text { get; init; }
    public string[]? OnlineUsers { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

[JsonSerializable(typeof(ChatEvent))]
internal partial class ChatJsonContext : JsonSerializerContext { }

sealed class ClientState : IAsyncDisposable
{
    public string Username { get; }
    public IStreamMultiplexer Mux { get; }
    public WriteChannel BroadcastChannel { get; }
    public MessageTransit<ChatEvent, ChatEvent> Transit { get; }

    public ClientState(string username, IStreamMultiplexer mux, WriteChannel broadcastChannel, ReadChannel chatChannel)
    {
        Username = username;
        Mux = mux;
        BroadcastChannel = broadcastChannel;
        Transit = new MessageTransit<ChatEvent, ChatEvent>(
            broadcastChannel, chatChannel,
            ChatJsonContext.Default.ChatEvent,
            ChatJsonContext.Default.ChatEvent);
    }

    public async ValueTask DisposeAsync()
    {
        await Transit.DisposeAsync();
        await Mux.DisposeAsync();
    }
}

sealed class ChatServer
{
    private readonly ConcurrentDictionary<string, ClientState> _clients = new();
    private int _totalConnections;
    private long _messagesRelayed;

    public IReadOnlyDictionary<string, ClientState> Clients => _clients;
    public int TotalConnections => _totalConnections;
    public long MessagesRelayed => Interlocked.Read(ref _messagesRelayed);

    public bool TryAddClient(string username, ClientState client)
    {
        if (_clients.TryAdd(username, client))
        {
            Interlocked.Increment(ref _totalConnections);
            return true;
        }
        return false;
    }

    public void RecordMessage() => Interlocked.Increment(ref _messagesRelayed);

    public async Task RemoveClientAsync(string username)
    {
        if (_clients.TryRemove(username, out var client))
        {
            Console.WriteLine($"[Server] {username} disconnected ({_clients.Count} online)");

            await client.DisposeAsync();

            await BroadcastAsync(new ChatEvent
            {
                Type = ChatEventType.UserLeft,
                Username = username,
                OnlineUsers = _clients.Keys.ToArray()
            });
        }
    }

    public async Task BroadcastAsync(ChatEvent evt)
    {
        var clients = _clients.Values.ToArray();
        var tasks = new List<Task>();

        foreach (var client in clients)
        {
            tasks.Add(SafeSendAsync(client, evt));
        }

        await Task.WhenAll(tasks);
    }

    private async Task SafeSendAsync(ClientState client, ChatEvent evt)
    {
        try
        {
            await client.Transit.SendAsync(evt);
        }
        catch
        {
            // Client disconnected, will be cleaned up
        }
    }
}
