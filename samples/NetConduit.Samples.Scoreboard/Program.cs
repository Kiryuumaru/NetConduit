// Scoreboard Sample
// Demonstrates a score tracker using MessageTransit for score submissions,
// DeltaTransit for efficient leaderboard synchronization, and reconnection
// support. Both sides use custom StreamFactories that support re-connection:
// the server re-accepts from the listener, and the client redials.
// The leaderboard persists across player sessions.

using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Enums;
using NetConduit.Transits;

var mode = args.Length > 0 ? args[0] : "help";
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5000;
var host = args.Length > 2 ? args[2] : "localhost";
var playerName = args.Length > 3 ? args[3] : $"Player{Random.Shared.Next(100, 999)}";

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

switch (mode)
{
    case "server":
        await RunServerAsync(port, cts.Token);
        break;
    case "player":
        await RunPlayerAsync(host, port, playerName, cts.Token);
        break;
    default:
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- server <port>");
        Console.WriteLine("  dotnet run -- player <port> <host> <name>");
        break;
}

// --- Server ---

async Task RunServerAsync(int serverPort, CancellationToken ct)
{
    Console.WriteLine($"[Server] Starting scoreboard on port {serverPort}");

    using var listener = new TcpListener(IPAddress.Any, serverPort);
    listener.Start();
    Console.WriteLine("[Server] Waiting for players...");

    // Leaderboard persists across player sessions
    var scores = new Dictionary<string, int>();

    while (!ct.IsCancellationRequested)
    {
        try
        {
            // Custom StreamFactory that re-accepts from the listener on reconnection.
            // CreateServerOptions rejects reconnection by design (single-client accept).
            // Building options directly allows the mux to re-accept on disconnect.
            var options = new MultiplexerOptions
            {
                StreamFactory = async ct =>
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    client.NoDelay = true;
                    return new StreamPair(client.GetStream(), client);
                },
                MaxAutoReconnectAttempts = 5,
                AutoReconnectDelay = TimeSpan.FromSeconds(2),
                ConnectionTimeout = TimeSpan.FromSeconds(10),
                PingInterval = TimeSpan.FromSeconds(10),
                PingTimeout = TimeSpan.FromSeconds(5)
            };

            await using var mux = StreamMultiplexer.Create(options);

            mux.Disconnected += (_, e) =>
                Console.WriteLine($"[Server] Player disconnected: {e.Reason}");
            mux.Reconnecting += (_, e) =>
                Console.WriteLine($"[Server] Awaiting reconnection (attempt {e.Attempt})...");
            mux.Connected += (_, _) =>
                Console.WriteLine("[Server] Player reconnected — session restored");

            mux.Start();
            await mux.WaitForReadyAsync(ct);
            Console.WriteLine("[Server] Player connected");

            await HandlePlayerSessionAsync(mux, scores, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server] Session error: {ex.Message}");
        }

        Console.WriteLine("[Server] Waiting for next player...");
    }
}

async Task HandlePlayerSessionAsync(
    IStreamMultiplexer mux,
    Dictionary<string, int> scores,
    CancellationToken ct)
{
    // Accept score submissions via MessageTransit (player -> server)
    await using var scoreTransit = await mux.AcceptMessageTransitAsync(
        "scores",
        ScoreJsonContext.Default.ScoreEvent,
        cancellationToken: ct);

    // Open DeltaTransit to push leaderboard state (server -> player)
    // JsonObject works without JsonTypeInfo, delta encoding sends only changed fields
    var leaderboardWriteChannel = mux.OpenChannel(new ChannelOptions
    {
        ChannelId = "leaderboard",
        Priority = ChannelPriority.Normal
    });

    var leaderboardTransit = new DeltaTransit<JsonObject>(leaderboardWriteChannel, null);

    // Send current leaderboard immediately
    await leaderboardTransit.SendAsync(ScoresToJson(scores), ct);

    string? playerName = null;

    // Process incoming score events — survives reconnections
    await foreach (var evt in scoreTransit.ReceiveAllAsync(ct))
    {
        if (evt is null) continue;

        playerName ??= evt.Player;
        Console.WriteLine($"[Server] {evt.Player} scored {evt.Points} points ({evt.Reason})");

        scores[evt.Player] = scores.GetValueOrDefault(evt.Player) + evt.Points;
        await leaderboardTransit.SendAsync(ScoresToJson(scores), ct);
    }

    Console.WriteLine($"[Server] {playerName ?? "Unknown"} session ended");
}

JsonObject ScoresToJson(Dictionary<string, int> scores)
{
    var sorted = scores.OrderByDescending(kvp => kvp.Value).ToList();

    var obj = new JsonObject();
    for (var i = 0; i < sorted.Count; i++)
    {
        obj[$"rank{i + 1}"] = $"{sorted[i].Key}: {sorted[i].Value}";
    }
    obj["totalPlayers"] = sorted.Count;
    return obj;
}

// --- Player ---

async Task RunPlayerAsync(string playerHost, int playerPort, string name, CancellationToken ct)
{
    Console.WriteLine($"[{name}] Connecting to {playerHost}:{playerPort}...");

    // Custom StreamFactory that creates a new TCP connection on each reconnection attempt.
    // Equivalent to: TcpMultiplexer.CreateOptions(host, port) with { MaxAutoReconnectAttempts = 10, ... }
    // Built directly here to show the StreamFactory pattern.
    var options = new MultiplexerOptions
    {
        StreamFactory = async ct =>
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(playerHost, playerPort, ct);
            return new StreamPair(client.GetStream(), client);
        },
        MaxAutoReconnectAttempts = 10,
        AutoReconnectDelay = TimeSpan.FromSeconds(1),
        ConnectionTimeout = TimeSpan.FromSeconds(5),
        PingInterval = TimeSpan.FromSeconds(10),
        PingTimeout = TimeSpan.FromSeconds(5)
    };

    await using var mux = StreamMultiplexer.Create(options);

    mux.Disconnected += (_, e) =>
        Console.WriteLine($"\n[{name}] Disconnected: {e.Reason}");
    mux.Reconnecting += (_, e) =>
        Console.WriteLine($"[{name}] Reconnecting (attempt {e.Attempt})...");
    mux.Connected += (_, _) =>
        Console.WriteLine($"[{name}] Reconnected — resuming session");

    mux.Start();
    await mux.WaitForReadyAsync(ct);

    Console.WriteLine($"[{name}] Connected! Submitting scores and watching leaderboard.");

    // Open MessageTransit to send score events (player -> server)
    await using var scoreTransit = await mux.OpenMessageTransitAsync(
        "scores",
        ScoreJsonContext.Default.ScoreEvent,
        cancellationToken: ct);

    // Accept DeltaTransit to receive leaderboard updates (server -> player)
    var leaderboardReadChannel = await mux.AcceptChannelAsync("leaderboard", ct);
    var leaderboardTransit = new DeltaTransit<JsonObject>(null, leaderboardReadChannel);

    // Background task: watch leaderboard updates
    var watchTask = Task.Run(async () =>
    {
        try
        {
            await foreach (var board in leaderboardTransit.ReceiveAllAsync(ct))
            {
                if (board is null) continue;
                Console.WriteLine($"\n[{name}] === LEADERBOARD ===");
                foreach (var prop in board)
                {
                    if (prop.Key.StartsWith("rank"))
                        Console.WriteLine($"  {prop.Key}: {prop.Value}");
                }
                Console.WriteLine($"  Players: {board["totalPlayers"]}");
                Console.WriteLine();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }, ct);

    // Send score events periodically
    var reasons = new[] { "headshot", "assist", "objective", "streak", "combo", "bonus" };

    for (var round = 1; !ct.IsCancellationRequested; round++)
    {
        var points = Random.Shared.Next(10, 100);
        var reason = reasons[Random.Shared.Next(reasons.Length)];

        await scoreTransit.SendAsync(new ScoreEvent(name, points, reason), ct);
        Console.WriteLine($"[{name}] Round {round}: +{points} ({reason})");

        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(1, 4)), ct);
    }

    await watchTask;
}

// --- Types ---

record ScoreEvent(string Player, int Points, string Reason);

[JsonSerializable(typeof(ScoreEvent))]
partial class ScoreJsonContext : JsonSerializerContext;
