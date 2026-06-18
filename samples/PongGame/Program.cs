using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Interfaces;
using NetConduit.Models;
using NetConduit.Transport.Tcp;
using NetConduit.Transit.Stream;
using NetConduit.Transit.DuplexStream;
using NetConduit.Transit.Message;
using NetConduit.Transit.DeltaMessage;

// ═══════════════════════════════════════════════════════════════
//   NetConduit Pong - Real-time Multiplayer using DeltaTransit
//   Demonstrates bandwidth savings: only changed values are sent
// ═══════════════════════════════════════════════════════════════

if (args.Length < 1 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return;
}

var mode = args[0].ToLowerInvariant();

if (mode == "server")
{
    if (args.Length < 2 || !int.TryParse(args[1], out var port))
    {
        Console.WriteLine("Usage: server <port>");
        return;
    }
    await RunServer(port);
}
else if (mode == "client")
{
    if (args.Length < 3 || !int.TryParse(args[1], out var port))
    {
        Console.WriteLine("Usage: client <port> <host>");
        return;
    }
    var host = args[2];
    await RunClient(host, port);
}
else
{
    Console.WriteLine($"Unknown mode: {mode}. Use 'server' or 'client'.");
}

return;

// ═══════════════════════════════════════════════════════════════
//   Usage
// ═══════════════════════════════════════════════════════════════

static void PrintUsage()
{
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine("  NetConduit Pong - Real-time Multiplayer with DeltaTransit");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine();
    Console.WriteLine("This demo showcases DeltaTransit's bandwidth efficiency:");
    Console.WriteLine("  - Full GameState JSON: ~150 bytes");
    Console.WriteLine("  - Typical delta (positions only): ~40 bytes (73% savings)");
    Console.WriteLine("  - Score change delta: ~20 bytes (87% savings)");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  Server: dotnet run -- server <port>");
    Console.WriteLine("  Client: dotnet run -- client <port> <host>");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -- server 5000");
    Console.WriteLine("  dotnet run -- client 5000 127.0.0.1");
    Console.WriteLine();
    Console.WriteLine("Controls:");
    Console.WriteLine("  W/S or Up/Down - Move paddle");
    Console.WriteLine("  Q or Escape    - Quit");
}

// ═══════════════════════════════════════════════════════════════
//   Console Input Helper
// ═══════════════════════════════════════════════════════════════

static ConsoleKey? ReadKeyIfAvailable()
{
    if (Console.KeyAvailable)
    {
        var key = Console.ReadKey(intercept: true);
        return key.Key;
    }
    return null;
}

// ═══════════════════════════════════════════════════════════════
//   Server
// ═══════════════════════════════════════════════════════════════

async Task RunServer(int port)
{
    Console.CursorVisible = false;
    Console.Clear();
    Console.WriteLine($"NetConduit Pong Server - Port {port} (Q to quit)");
    Console.WriteLine("Waiting for 2 players to connect...");

    var cts = new CancellationTokenSource();
    var players = new ConcurrentDictionary<int, PlayerConnection>();
    var game = new GameState();
    var acceptedCount = 0;

    var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();

    // Accept connections task
    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested && acceptedCount < 2)
        {
            try
            {
                var tcpClient = await listener.AcceptTcpClientAsync(cts.Token);
                var connNum = Interlocked.Increment(ref acceptedCount);

                if (connNum > 2)
                {
                    tcpClient.Close();
                    continue;
                }

                var playerNum = connNum;
                _ = Task.Run(async () =>
                {
                    var setupComplete = false;
                    try
                    {
                        var accepted = false;
                        var options = new MultiplexerOptions
                        {
                            StreamFactory = _ =>
                            {
                                if (accepted)
                                    throw new InvalidOperationException("No reconnection");
                                accepted = true;
                                return Task.FromResult<IStreamPair>(new StreamPair(tcpClient.GetStream(), tcpClient));
                            }
                        };
                        var mux = StreamMultiplexer.Create(options);
                        mux.Start();
                        await mux.WaitForReadyAsync(cts.Token);

                        IReadChannel? controlChannel = null;
                        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        acceptCts.CancelAfter(TimeSpan.FromSeconds(10));

                        await foreach (var ch in mux.AcceptChannelsAsync(ct: acceptCts.Token))
                        {
                            if (ch.ChannelId == "control")
                            {
                                controlChannel = ch;
                                break;
                            }
                        }

                        if (controlChannel == null)
                        {
                            await mux.DisposeAsync();
                            return;
                        }

                        var stateChannel = mux.OpenChannel("state");

                        var stateTransit = new DeltaMessageTransit<GameState>(
                            stateChannel, null,
                            PongJsonContext.Default.GameState);

                        var inputTransit = new MessageTransit<object?, PlayerInput>(
                            null, controlChannel,
                            null,
                            PongJsonContext.Default.PlayerInput);

                        setupComplete = true;
                        var conn = new PlayerConnection(playerNum, mux, stateTransit, inputTransit);
                        players[playerNum] = conn;

                        Console.WriteLine($"Player {playerNum} connected! ({players.Count}/2)");

                        await foreach (var input in inputTransit.ReceiveAllAsync(cts.Token))
                        {
                            game.SetPaddleDirection(playerNum, input.Direction);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                    finally
                    {
                        if (setupComplete)
                        {
                            players.TryRemove(playerNum, out _);
                        }
                    }
                }, cts.Token);
            }
            catch (OperationCanceledException) { break; }
        }
    }, cts.Token);

    // Game loop - wait for players
    while (players.Count < 2 && !cts.Token.IsCancellationRequested)
    {
        var key = ReadKeyIfAvailable();
        if (key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            cts.Cancel();
            break;
        }
        await Task.Delay(100, cts.Token);
    }

    if (cts.Token.IsCancellationRequested)
    {
        listener.Stop();
        Console.CursorVisible = true;
        return;
    }

    Console.Clear();
    Console.WriteLine("Both players connected! Starting in 2 seconds...");
    await Task.Delay(2000, cts.Token);

    game.Reset();
    game.Status = GameStatus.Playing;
    Console.Clear();

    while (!cts.Token.IsCancellationRequested && game.Status == GameStatus.Playing)
    {
        var key = ReadKeyIfAvailable();
        if (key is ConsoleKey.Q or ConsoleKey.Escape)
        {
            cts.Cancel();
            break;
        }

        game.Update();

        if (game.Score1 >= GameConfig.WinScore)
            game.Status = GameStatus.Player1Wins;
        else if (game.Score2 >= GameConfig.WinScore)
            game.Status = GameStatus.Player2Wins;

        // Broadcast state using DeltaTransit (only changed values sent)
        foreach (var (_, conn) in players)
        {
            try { await conn.StateTransit.SendAsync(game, cts.Token); }
            catch { }
        }

        var statusMsg = game.Status switch
        {
            GameStatus.Playing => "W/S to move | Q to quit",
            GameStatus.Player1Wins => "Player 1 WINS! Press Q to exit.",
            GameStatus.Player2Wins => "Player 2 WINS! Press Q to exit.",
            _ => ""
        };

        Console.SetCursorPosition(0, 0);
        Console.Write(RenderGameToString(game, statusMsg));

        await Task.Delay(GameConfig.TickMs, cts.Token);
    }

    // Final state
    foreach (var (_, conn) in players)
    {
        try { await conn.StateTransit.SendAsync(game, cts.Token); }
        catch { }
    }

    listener.Stop();
    Console.CursorVisible = true;
}

// ═══════════════════════════════════════════════════════════════
//   Client
// ═══════════════════════════════════════════════════════════════

async Task RunClient(string host, int port)
{
    Console.CursorVisible = false;
    Console.Clear();
    Console.WriteLine($"Connecting to {host}:{port}...");

    var cts = new CancellationTokenSource();
    MessageTransit<PlayerInput, object?>? inputTransit = null;
    var currentDirection = 0;

    void SendDirection(int dir)
    {
        if (dir != currentDirection && inputTransit != null)
        {
            currentDirection = dir;
            var transit = inputTransit;
            _ = Task.Run(async () =>
            {
                try { await transit.SendAsync(new PlayerInput { Direction = dir }, cts.Token); }
                catch { }
            });
        }
    }

    try
    {
        var options = TcpMultiplexer.CreateOptions(host, port);
        await using var mux = StreamMultiplexer.Create(options);
        mux.Start();
        await mux.WaitForReadyAsync(cts.Token);

        var controlChannel = mux.OpenChannel("control");

        IReadChannel? stateChannel = null;
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        acceptCts.CancelAfter(TimeSpan.FromSeconds(10));

        await foreach (var ch in mux.AcceptChannelsAsync(ct: acceptCts.Token))
        {
            if (ch.ChannelId == "state")
            {
                stateChannel = ch;
                break;
            }
        }

        if (stateChannel == null)
        {
            Console.WriteLine("Failed to connect. Server did not open state channel.");
            Console.CursorVisible = true;
            return;
        }

        inputTransit = new MessageTransit<PlayerInput, object?>(
            controlChannel, null,
            PongJsonContext.Default.PlayerInput,
            null);

        var stateTransit = new DeltaMessageTransit<GameState>(
            null, stateChannel,
            PongJsonContext.Default.GameState);

        Console.Clear();
        Console.WriteLine("Connected! Waiting for game to start...");
        Console.WriteLine("Controls: W/S or Up/Down to move | Q to quit");

        var gameStarted = false;

        await foreach (var state in stateTransit.ReceiveAllAsync(cts.Token))
        {
            // Process input
            var key = ReadKeyIfAvailable();
            if (key is ConsoleKey.Q or ConsoleKey.Escape)
            {
                cts.Cancel();
                break;
            }
            else if (key is ConsoleKey.W or ConsoleKey.UpArrow)
            {
                SendDirection(currentDirection == -1 ? 0 : -1);
            }
            else if (key is ConsoleKey.S or ConsoleKey.DownArrow)
            {
                SendDirection(currentDirection == 1 ? 0 : 1);
            }
            else if (key is ConsoleKey.Spacebar or ConsoleKey.X)
            {
                SendDirection(0);
            }

            if (!gameStarted && state.Status == GameStatus.Playing)
            {
                gameStarted = true;
                Console.Clear();
            }

            if (gameStarted)
            {
                var statusMsg = state.Status switch
                {
                    GameStatus.Playing => "W/S or Up/Down to move | Q to quit",
                    GameStatus.Player1Wins => "Player 1 WINS! Press Q to exit.",
                    GameStatus.Player2Wins => "Player 2 WINS! Press Q to exit.",
                    _ => ""
                };

                Console.SetCursorPosition(0, 0);
                Console.Write(RenderGameToString(state, statusMsg));
            }

            if (state.Status != GameStatus.Playing && state.Status != GameStatus.WaitingForPlayers)
            {
                // Wait for quit key after game ends
                while (true)
                {
                    var endKey = ReadKeyIfAvailable();
                    if (endKey is ConsoleKey.Q or ConsoleKey.Escape) break;
                    await Task.Delay(100);
                }
                break;
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
    finally
    {
        Console.CursorVisible = true;
    }
}

// ═══════════════════════════════════════════════════════════════
//   Render Game State to String
// ═══════════════════════════════════════════════════════════════

static string RenderGameToString(GameState state, string statusMessage)
{
    var sb = new StringBuilder();
    sb.AppendLine($"\u2554{"".PadRight(GameConfig.Width, '\u2550')}\u2557");

    for (var y = 0; y < GameConfig.Height; y++)
    {
        sb.Append('\u2551');
        for (var x = 0; x < GameConfig.Width; x++)
        {
            var ch = ' ';

            // Ball
            if ((int)state.BallX == x && (int)state.BallY == y)
            {
                ch = '\u25cf';
            }
            // Left paddle
            else if (x == 2 && y >= (int)state.Paddle1Y && y < (int)state.Paddle1Y + GameConfig.PaddleHeight)
            {
                ch = '\u2588';
            }
            // Right paddle
            else if (x == GameConfig.Width - 3 && y >= (int)state.Paddle2Y && y < (int)state.Paddle2Y + GameConfig.PaddleHeight)
            {
                ch = '\u2588';
            }
            // Center line
            else if (x == GameConfig.Width / 2 && y % 2 == 0)
            {
                ch = '\u2502';
            }

            sb.Append(ch);
        }
        sb.Append('\u2551');
        sb.AppendLine();
    }

    sb.AppendLine($"\u255a{"".PadRight(GameConfig.Width, '\u2550')}\u255d");
    sb.AppendLine($"  Player 1: {state.Score1}    Player 2: {state.Score2}");
    sb.AppendLine($"  {statusMessage}");

    return sb.ToString();
}

// ═══════════════════════════════════════════════════════════════
//   Types
// ═══════════════════════════════════════════════════════════════

public enum GameStatus
{
    WaitingForPlayers,
    Playing,
    Player1Wins,
    Player2Wins
}

public sealed class GameState
{
    public GameStatus Status { get; set; } = GameStatus.WaitingForPlayers;
    public float BallX { get; set; }
    public float BallY { get; set; }
    public float BallVX { get; set; }
    public float BallVY { get; set; }
    public float Paddle1Y { get; set; }
    public float Paddle2Y { get; set; }
    public int Score1 { get; set; }
    public int Score2 { get; set; }

    [JsonIgnore]
    private int _paddle1Dir;
    [JsonIgnore]
    private int _paddle2Dir;

    public void Reset()
    {
        BallX = GameConfig.Width / 2f;
        BallY = GameConfig.Height / 2f;
        BallVX = GameConfig.BallSpeed * (Random.Shared.Next(2) == 0 ? 1 : -1);
        BallVY = GameConfig.BallSpeed * (Random.Shared.NextSingle() - 0.5f) * 2;
        Paddle1Y = (GameConfig.Height - GameConfig.PaddleHeight) / 2f;
        Paddle2Y = (GameConfig.Height - GameConfig.PaddleHeight) / 2f;
        Score1 = 0;
        Score2 = 0;
    }

    public void ResetBall()
    {
        BallX = GameConfig.Width / 2f;
        BallY = GameConfig.Height / 2f;
        BallVX = GameConfig.BallSpeed * (BallVX > 0 ? -1 : 1);
        BallVY = GameConfig.BallSpeed * (Random.Shared.NextSingle() - 0.5f) * 2;
    }

    public void SetPaddleDirection(int player, int direction)
    {
        if (player == 1) _paddle1Dir = direction;
        else _paddle2Dir = direction;
    }

    public void Update()
    {
        Paddle1Y += _paddle1Dir * GameConfig.PaddleSpeed;
        Paddle2Y += _paddle2Dir * GameConfig.PaddleSpeed;

        Paddle1Y = Math.Clamp(Paddle1Y, 0, GameConfig.Height - GameConfig.PaddleHeight);
        Paddle2Y = Math.Clamp(Paddle2Y, 0, GameConfig.Height - GameConfig.PaddleHeight);

        BallX += BallVX;
        BallY += BallVY;

        if (BallY <= 0 || BallY >= GameConfig.Height - 1)
        {
            BallVY = -BallVY;
            BallY = Math.Clamp(BallY, 0, GameConfig.Height - 1);
        }

        if (BallX <= 4 && BallX >= 2)
        {
            if (BallY >= Paddle1Y && BallY <= Paddle1Y + GameConfig.PaddleHeight)
            {
                BallVX = Math.Abs(BallVX) * 1.05f;
                BallVY += (BallY - Paddle1Y - GameConfig.PaddleHeight / 2f) * 0.05f;
                BallX = 4;
            }
        }

        if (BallX >= GameConfig.Width - 5 && BallX <= GameConfig.Width - 3)
        {
            if (BallY >= Paddle2Y && BallY <= Paddle2Y + GameConfig.PaddleHeight)
            {
                BallVX = -Math.Abs(BallVX) * 1.05f;
                BallVY += (BallY - Paddle2Y - GameConfig.PaddleHeight / 2f) * 0.05f;
                BallX = GameConfig.Width - 5;
            }
        }

        BallVX = Math.Clamp(BallVX, -1.2f, 1.2f);
        BallVY = Math.Clamp(BallVY, -0.8f, 0.8f);

        if (BallX <= 0)
        {
            Score2++;
            ResetBall();
        }
        else if (BallX >= GameConfig.Width - 1)
        {
            Score1++;
            ResetBall();
        }
    }
}

public sealed class PlayerInput
{
    public int Direction { get; init; }
}

sealed class PlayerConnection(
    int playerNum,
    IStreamMultiplexer mux,
    DeltaMessageTransit<GameState> stateTransit,
    MessageTransit<object?, PlayerInput> inputTransit)
{
    public int PlayerNum => playerNum;
    public IStreamMultiplexer Mux => mux;
    public DeltaMessageTransit<GameState> StateTransit => stateTransit;
    public MessageTransit<object?, PlayerInput> InputTransit => inputTransit;
}

[JsonSerializable(typeof(GameState))]
[JsonSerializable(typeof(PlayerInput))]
internal partial class PongJsonContext : JsonSerializerContext { }

static class GameConfig
{
    public const int Width = 60;
    public const int Height = 20;
    public const int PaddleHeight = 4;
    public const float BallSpeed = 0.5f;
    public const float PaddleSpeed = 0.6f;
    public const int WinScore = 5;
    public const int TickMs = 33;
}

