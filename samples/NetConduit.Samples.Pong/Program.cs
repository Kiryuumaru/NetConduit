using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Tcp;
using NetConduit.Transits;
using Terminal.Gui;

// ═══════════════════════════════════════════════════════════════
//   NetConduit Pong - Real-time Multiplayer using Terminal.Gui
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
    RunServer(port);
}
else if (mode == "client")
{
    if (args.Length < 3 || !int.TryParse(args[1], out var port))
    {
        Console.WriteLine("Usage: client <port> <host>");
        return;
    }
    var host = args[2];
    RunClient(host, port);
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
    Console.WriteLine("  NetConduit Pong - Real-time Multiplayer Demo");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
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
//   Server
// ═══════════════════════════════════════════════════════════════

void RunServer(int port)
{
    Application.Init();
    
    try
    {
        var cts = new CancellationTokenSource();
        var players = new ConcurrentDictionary<int, PlayerConnection>();
        var game = new GameState();
        var acceptedCount = 0;

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        // Create main window
        var win = new Window
        {
            Title = $"NetConduit Pong Server - Port {port} (Q to quit)",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true
        };

        // Create game display using FrameView for border
        var gameFrame = new FrameView
        {
            Title = "Game",
            X = Pos.Center(),
            Y = 1,
            Width = GameConfig.Width + 4,
            Height = GameConfig.Height + 6
        };

        var gameLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "Waiting for 2 players to connect..."
        };

        gameFrame.Add(gameLabel);
        win.Add(gameFrame);

        // Handle quit key at Application level
        Application.KeyDown += (s, e) =>
        {
            var isQuit = e.KeyCode == KeyCode.Esc || 
                         e.KeyCode == KeyCode.Q ||
                         (e.KeyCode & KeyCode.CharMask) == (KeyCode)'q' ||
                         (e.KeyCode & KeyCode.CharMask) == (KeyCode)'Q';
            if (isQuit)
            {
                cts.Cancel();
                Application.RequestStop();
                e.Handled = true;
            }
        };

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
                                EnableReconnection = false,
                                StreamFactory = _ =>
                                {
                                    if (accepted)
                                        throw new InvalidOperationException("No reconnection");
                                    accepted = true;
                                    return Task.FromResult<IStreamPair>(new StreamPair(tcpClient.GetStream(), tcpClient));
                                }
                            };
                            var mux = StreamMultiplexer.Create(options);
                            _ = mux.Start(cts.Token);
                            await mux.WaitForReadyAsync(cts.Token);

                            ReadChannel? controlChannel = null;
                            using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                            acceptCts.CancelAfter(TimeSpan.FromSeconds(10));

                            await foreach (var ch in mux.AcceptChannelsAsync(acceptCts.Token))
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

                            var stateChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "state" }, cts.Token);

                            var transit = new MessageTransit<GameState, PlayerInput>(
                                stateChannel, controlChannel,
                                PongJsonContext.Default.GameState,
                                PongJsonContext.Default.PlayerInput);

                            setupComplete = true;
                            var conn = new PlayerConnection(playerNum, mux, transit);
                            players[playerNum] = conn;

                            Application.Invoke(() =>
                            {
                                gameLabel.Text = $"Player {playerNum} connected! ({players.Count}/2)";
                            });

                            await foreach (var input in transit.ReceiveAllAsync(cts.Token))
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

        // Game loop task
        _ = Task.Run(async () =>
        {
            // Wait for both players
            while (players.Count < 2 && !cts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, cts.Token);
            }

            if (cts.Token.IsCancellationRequested) return;

            Application.Invoke(() =>
            {
                gameLabel.Text = "Both players connected! Starting in 2 seconds...";
            });
            await Task.Delay(2000, cts.Token);

            game.Reset();
            game.Status = GameStatus.Playing;

            while (!cts.Token.IsCancellationRequested && game.Status == GameStatus.Playing)
            {
                game.Update();

                if (game.Score1 >= GameConfig.WinScore)
                    game.Status = GameStatus.Player1Wins;
                else if (game.Score2 >= GameConfig.WinScore)
                    game.Status = GameStatus.Player2Wins;

                // Broadcast state
                foreach (var (_, conn) in players)
                {
                    try { await conn.Transit.SendAsync(game, cts.Token); }
                    catch { }
                }

                // Update display
                var statusMsg = game.Status switch
                {
                    GameStatus.Playing => "W/S to move | Q to quit",
                    GameStatus.Player1Wins => "Player 1 WINS! Press Q to exit.",
                    GameStatus.Player2Wins => "Player 2 WINS! Press Q to exit.",
                    _ => ""
                };

                Application.Invoke(() =>
                {
                    gameLabel.Text = RenderGameToString(game, statusMsg);
                });

                await Task.Delay(GameConfig.TickMs, cts.Token);
            }

            // Final state
            foreach (var (_, conn) in players)
            {
                try { await conn.Transit.SendAsync(game, cts.Token); }
                catch { }
            }

            listener.Stop();
        }, cts.Token);

        Application.Run(win);
    }
    finally
    {
        Application.Shutdown();
    }
}

// ═══════════════════════════════════════════════════════════════
//   Client
// ═══════════════════════════════════════════════════════════════

void RunClient(string host, int port)
{
    Application.Init();
    
    try
    {
        var cts = new CancellationTokenSource();
        MessageTransit<PlayerInput, GameState>? transit = null;
        var currentDirection = 0;
        var gameStarted = false;

        // Create main window
        var win = new Window
        {
            Title = $"NetConduit Pong - {host}:{port}",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true
        };

        // Create game display
        var gameFrame = new FrameView
        {
            Title = "Game",
            X = Pos.Center(),
            Y = 1,
            Width = GameConfig.Width + 4,
            Height = GameConfig.Height + 6
        };

        var gameLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = $"Connecting to {host}:{port}..."
        };

        gameFrame.Add(gameLabel);
        win.Add(gameFrame);

        // Helper to send paddle input
        void SendDirection(int dir)
        {
            if (dir != currentDirection && transit != null)
            {
                currentDirection = dir;
                _ = Task.Run(async () =>
                {
                    try { await transit.SendAsync(new PlayerInput { Direction = currentDirection }, cts.Token); }
                    catch { }
                });
            }
        }

        // Clear default arrow key bindings that interfere with game controls
        win.KeyBindings.Clear();
        gameFrame.KeyBindings.Clear();
        gameLabel.KeyBindings.Clear();

        // Handle key events - TOGGLE mode since consoles don't have real key release
        // W/Up = toggle up movement, S/Down = toggle down movement, Space/X = stop
        Application.KeyDown += (s, e) =>
        {
            var isUp = e.KeyCode == KeyCode.CursorUp || 
                       e.KeyCode == KeyCode.W || 
                       (e.KeyCode & KeyCode.CharMask) == (KeyCode)'w' ||
                       (e.KeyCode & KeyCode.CharMask) == (KeyCode)'W';
                       
            var isDown = e.KeyCode == KeyCode.CursorDown || 
                         e.KeyCode == KeyCode.S ||
                         (e.KeyCode & KeyCode.CharMask) == (KeyCode)'s' ||
                         (e.KeyCode & KeyCode.CharMask) == (KeyCode)'S';

            var isStop = e.KeyCode == KeyCode.Space ||
                         e.KeyCode == KeyCode.X ||
                         (e.KeyCode & KeyCode.CharMask) == (KeyCode)'x' ||
                         (e.KeyCode & KeyCode.CharMask) == (KeyCode)'X';

            var isQuit = e.KeyCode == KeyCode.Esc || 
                         e.KeyCode == KeyCode.Q ||
                         (e.KeyCode & KeyCode.CharMask) == (KeyCode)'q' ||
                         (e.KeyCode & KeyCode.CharMask) == (KeyCode)'Q';

            if (isUp)
            {
                // Toggle: if already moving up, stop; otherwise move up
                SendDirection(currentDirection == -1 ? 0 : -1);
                e.Handled = true;
            }
            else if (isDown)
            {
                // Toggle: if already moving down, stop; otherwise move down
                SendDirection(currentDirection == 1 ? 0 : 1);
                e.Handled = true;
            }
            else if (isStop)
            {
                SendDirection(0);
                e.Handled = true;
            }
            else if (isQuit)
            {
                cts.Cancel();
                Application.RequestStop();
                e.Handled = true;
            }
        };

        // Network connection task
        _ = Task.Run(async () =>
        {
            try
            {
                var options = TcpMultiplexer.CreateOptions(host, port);
                await using var mux = StreamMultiplexer.Create(options);
                _ = mux.Start(cts.Token);
                await mux.WaitForReadyAsync(cts.Token);

                var controlChannel = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "control" }, cts.Token);

                ReadChannel? stateChannel = null;
                using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                acceptCts.CancelAfter(TimeSpan.FromSeconds(10));

                await foreach (var ch in mux.AcceptChannelsAsync(acceptCts.Token))
                {
                    if (ch.ChannelId == "state")
                    {
                        stateChannel = ch;
                        break;
                    }
                }

                if (stateChannel == null)
                {
                    Application.Invoke(() =>
                    {
                        gameLabel.Text = "Failed to connect. Press Q to exit.";
                    });
                    return;
                }

                transit = new MessageTransit<PlayerInput, GameState>(
                    controlChannel, stateChannel,
                    PongJsonContext.Default.PlayerInput,
                    PongJsonContext.Default.GameState);

                Application.Invoke(() =>
                {
                    gameLabel.Text = "Connected! Waiting for game to start...\nControls: W/S or Up/Down to move";
                });

                await foreach (var state in transit.ReceiveAllAsync(cts.Token))
                {
                    if (!gameStarted && state.Status == GameStatus.Playing)
                    {
                        gameStarted = true;
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

                        Application.Invoke(() =>
                        {
                            gameLabel.Text = RenderGameToString(state, statusMsg);
                        });
                    }

                    if (state.Status != GameStatus.Playing && state.Status != GameStatus.WaitingForPlayers)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Application.Invoke(() =>
                {
                    gameLabel.Text = $"Error: {ex.Message}\nPress Q to exit.";
                });
            }
        }, cts.Token);

        Application.Run(win);
    }
    finally
    {
        Application.Shutdown();
    }
}

// ═══════════════════════════════════════════════════════════════
//   Render Game State to String
// ═══════════════════════════════════════════════════════════════

static string RenderGameToString(GameState state, string statusMessage)
{
    var sb = new StringBuilder();

    for (var y = 0; y < GameConfig.Height; y++)
    {
        for (var x = 0; x < GameConfig.Width; x++)
        {
            var ch = ' ';

            // Ball
            if ((int)state.BallX == x && (int)state.BallY == y)
            {
                ch = '●';
            }
            // Left paddle
            else if (x == 2 && y >= (int)state.Paddle1Y && y < (int)state.Paddle1Y + GameConfig.PaddleHeight)
            {
                ch = '█';
            }
            // Right paddle
            else if (x == GameConfig.Width - 3 && y >= (int)state.Paddle2Y && y < (int)state.Paddle2Y + GameConfig.PaddleHeight)
            {
                ch = '█';
            }
            // Center line
            else if (x == GameConfig.Width / 2 && y % 2 == 0)
            {
                ch = '│';
            }

            sb.Append(ch);
        }
        sb.AppendLine();
    }

    // Score and status
    sb.AppendLine($"Player 1: {state.Score1}    Player 2: {state.Score2}");
    sb.AppendLine(statusMessage);

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

sealed class PlayerConnection(int playerNum, IStreamMultiplexer mux, MessageTransit<GameState, PlayerInput> transit)
{
    public int PlayerNum => playerNum;
    public IStreamMultiplexer Mux => mux;
    public MessageTransit<GameState, PlayerInput> Transit => transit;
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
