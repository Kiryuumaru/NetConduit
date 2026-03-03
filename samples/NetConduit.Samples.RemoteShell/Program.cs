using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Tcp;
using NetConduit.Transits;

// ═══════════════════════════════════════════════════════════════════════════════
// NetConduit Remote Shell - SSH-like CLI
// ═══════════════════════════════════════════════════════════════════════════════

if (args.Length < 2)
{
    PrintUsage();
    return;
}

if (args[0] == "server" && int.TryParse(args[1], out var port))
    await RunServerAsync(port);
else if (args[0] == "client" && int.TryParse(args[1], out var cport) && args.Length >= 3)
    await RunClientAsync(args[2], cport);
else
    PrintUsage();

return;

void PrintUsage()
{
    Console.WriteLine("NetConduit Remote Shell");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  server <port>        Start shell server");
    Console.WriteLine("  client <port> <host> Connect to server");
}

// ═══════════════════════════════════════════════════════════════════════════════
// SERVER
// ═══════════════════════════════════════════════════════════════════════════════

async Task RunServerAsync(int port)
{
    var cts = new CancellationTokenSource();
    var listener = new TcpListener(IPAddress.Any, port);

    // Treat Ctrl+C as input so we can handle it manually
    Console.TreatControlCAsInput = true;

    listener.Start();
    WriteColored("● ", ConsoleColor.Green);
    Console.WriteLine($"Remote Shell Server listening on port {port}");
    WriteColored("  Press Ctrl+C to stop", ConsoleColor.DarkGray);
    Console.WriteLine();
    Console.WriteLine();

    // Background task to listen for Ctrl+C
    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    Console.WriteLine();
                    WriteColored("Server shutting down...", ConsoleColor.Yellow);
                    Console.WriteLine();
                    cts.Cancel();
                    listener.Stop();
                    break;
                }
            }
            await Task.Delay(50);
        }
    });

    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(cts.Token);
            }
            catch { break; }

            var endpoint = tcp.Client.RemoteEndPoint?.ToString() ?? "unknown";
            WriteColored($"+ ", ConsoleColor.Cyan);
            Console.WriteLine($"Client connected: {endpoint}");

            _ = HandleClientAsync(tcp, endpoint, cts.Token);
        }
    }
    catch { }

    Console.WriteLine("Server stopped.");
}

async Task HandleClientAsync(TcpClient tcp, string endpoint, CancellationToken serverCt)
{
    Process? currentProcess = null;
    var processLock = new object();

    try
    {
        var accepted = false;
        var options = new MultiplexerOptions
        {
            EnableReconnection = false,
            StreamFactory = _ =>
            {
                if (accepted) throw new InvalidOperationException();
                accepted = true;
                return Task.FromResult<IStreamPair>(new StreamPair(tcp.GetStream(), tcp));
            }
        };

        await using var mux = StreamMultiplexer.Create(options);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        _ = mux.Start(cts.Token);
        await mux.WaitForReadyAsync(cts.Token);

        // Accept channels from client
        ReadChannel? cmdCh = null;
        ReadChannel? ctrlCh = null;
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        acceptCts.CancelAfter(5000);

        await foreach (var ch in mux.AcceptChannelsAsync(acceptCts.Token))
        {
            if (ch.ChannelId == "cmd") cmdCh = ch;
            else if (ch.ChannelId == "ctrl") ctrlCh = ch;
            if (cmdCh != null && ctrlCh != null) break;
        }
        if (cmdCh == null || ctrlCh == null) return;

        // Open output channels
        var outCh = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "out" }, cts.Token);
        var doneCh = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "done" }, cts.Token);

        var cmdTransit = new MessageTransit<Msg, Msg>(null, cmdCh, Ctx.Default.Msg, Ctx.Default.Msg);
        var ctrlTransit = new MessageTransit<Msg, Msg>(null, ctrlCh, Ctx.Default.Msg, Ctx.Default.Msg);
        var doneTransit = new MessageTransit<Msg, Msg>(doneCh, null, Ctx.Default.Msg, Ctx.Default.Msg);

        // Listen for Ctrl+C signals from client
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in ctrlTransit.ReceiveAllAsync(cts.Token))
                {
                    if (msg.T == "kill")
                    {
                        lock (processLock)
                        {
                            if (currentProcess != null && !currentProcess.HasExited)
                            {
                                try { currentProcess.Kill(entireProcessTree: true); }
                                catch { try { currentProcess.Kill(); } catch { } }
                            }
                        }
                    }
                }
            }
            catch { }
        }, cts.Token);

        // Process commands
        await foreach (var msg in cmdTransit.ReceiveAllAsync(cts.Token))
        {
            if (msg.T != "exec" || string.IsNullOrEmpty(msg.D)) continue;

            WriteColored($"  → ", ConsoleColor.DarkGray);
            Console.WriteLine($"[{endpoint}] {msg.D}");

            var exitCode = await ExecuteCommandAsync(msg.D, outCh, p =>
            {
                lock (processLock) currentProcess = p;
            }, () =>
            {
                lock (processLock) currentProcess = null;
            }, cts.Token);

            await doneTransit.SendAsync(new Msg { T = "done", D = exitCode.ToString() }, cts.Token);
        }
    }
    catch { }
    finally
    {
        WriteColored($"- ", ConsoleColor.Red);
        Console.WriteLine($"Client disconnected: {endpoint}");
    }
}

async Task<int> ExecuteCommandAsync(string cmd, WriteChannel outCh, Action<Process> onStart, Action onEnd, CancellationToken ct)
{
    var isWin = OperatingSystem.IsWindows();
    var psi = new ProcessStartInfo
    {
        FileName = isWin ? "cmd.exe" : "/bin/bash",
        Arguments = isWin ? $"/c {cmd}" : $"-c \"{cmd}\"",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    try
    {
        using var p = Process.Start(psi);
        if (p == null) return -1;

        onStart(p);

        // Stream stdout in real-time
        var outTask = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                int n;
                while ((n = await p.StandardOutput.BaseStream.ReadAsync(buf, ct)) > 0)
                    await outCh.WriteAsync(buf.AsMemory(0, n), ct);
            }
            catch { }
        }, ct);

        // Stream stderr in real-time (with red color)
        var errTask = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                int n;
                while ((n = await p.StandardError.BaseStream.ReadAsync(buf, ct)) > 0)
                {
                    await outCh.WriteAsync("\x1b[31m"u8.ToArray(), ct);
                    await outCh.WriteAsync(buf.AsMemory(0, n), ct);
                    await outCh.WriteAsync("\x1b[0m"u8.ToArray(), ct);
                }
            }
            catch { }
        }, ct);

        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!p.HasExited)
                try { p.Kill(entireProcessTree: true); } catch { }
        }

        await Task.WhenAll(outTask, errTask);
        onEnd();
        return p.HasExited ? p.ExitCode : -1;
    }
    catch (Exception ex)
    {
        onEnd();
        try { await outCh.WriteAsync(Encoding.UTF8.GetBytes($"\x1b[31mError: {ex.Message}\x1b[0m\n"), ct); }
        catch { }
        return -1;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// CLIENT
// ═══════════════════════════════════════════════════════════════════════════════

async Task RunClientAsync(string host, int port)
{
    WriteColored("Connecting to ", ConsoleColor.DarkGray);
    WriteColored($"{host}:{port}", ConsoleColor.Cyan);
    Console.WriteLine("...");

    var options = TcpMultiplexer.CreateOptions(host, port);
    await using var mux = StreamMultiplexer.Create(options);
    using var mainCts = new CancellationTokenSource();
    _ = mux.Start(mainCts.Token);

    try { await mux.WaitForReadyAsync(mainCts.Token); }
    catch
    {
        WriteColored("✗ ", ConsoleColor.Red);
        Console.WriteLine("Connection failed");
        return;
    }

    // Open channels
    var cmdCh = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "cmd" }, mainCts.Token);
    var ctrlCh = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "ctrl" }, mainCts.Token);

    // Accept output channels
    ReadChannel? outCh = null;
    ReadChannel? doneCh = null;
    using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);
    acceptCts.CancelAfter(5000);

    await foreach (var ch in mux.AcceptChannelsAsync(acceptCts.Token))
    {
        if (ch.ChannelId == "out") outCh = ch;
        else if (ch.ChannelId == "done") doneCh = ch;
        if (outCh != null && doneCh != null) break;
    }

    if (outCh == null || doneCh == null)
    {
        WriteColored("✗ ", ConsoleColor.Red);
        Console.WriteLine("Failed to establish channels");
        return;
    }

    var cmdTransit = new MessageTransit<Msg, Msg>(cmdCh, null, Ctx.Default.Msg, Ctx.Default.Msg);
    var ctrlTransit = new MessageTransit<Msg, Msg>(ctrlCh, null, Ctx.Default.Msg, Ctx.Default.Msg);
    var doneTransit = new MessageTransit<Msg, Msg>(null, doneCh, Ctx.Default.Msg, Ctx.Default.Msg);

    // State
    var commandRunning = false;
    CancellationTokenSource? cmdCts = null;
    var history = new List<string>();

    // Treat Ctrl+C as input so we can handle it in ReadKey
    Console.TreatControlCAsInput = true;

    // Print banner
    Console.WriteLine();
    WriteColored("✓ ", ConsoleColor.Green);
    Console.Write("Connected to ");
    WriteColored(host, ConsoleColor.Cyan);
    Console.WriteLine();
    WriteColored("  Type ", ConsoleColor.DarkGray);
    WriteColored("exit", ConsoleColor.White);
    WriteColored(" to disconnect, ", ConsoleColor.DarkGray);
    WriteColored("Ctrl+C", ConsoleColor.White);
    WriteColored(" to cancel/exit", ConsoleColor.DarkGray);
    Console.WriteLine();
    Console.WriteLine();

    // Input loop
    while (!mainCts.Token.IsCancellationRequested)
    {
        // Print prompt
        WriteColored(Environment.UserName, ConsoleColor.Green);
        WriteColored("@", ConsoleColor.DarkGray);
        WriteColored(host, ConsoleColor.Cyan);
        WriteColored(":", ConsoleColor.DarkGray);
        WriteColored("~", ConsoleColor.Blue);
        WriteColored("$ ", ConsoleColor.DarkGray);

        // Read command with history support
        var (cmd, wasCtrlC) = await ReadLineWithHistoryAsync(history, mainCts.Token);
        
        if (wasCtrlC)
        {
            // Ctrl+C while not running command = exit
            Console.WriteLine();
            break;
        }

        if (cmd == null || mainCts.Token.IsCancellationRequested) break;

        cmd = cmd.Trim();
        if (cmd.Length == 0) continue;

        // Add to history (avoid duplicates)
        if (history.Count == 0 || history[^1] != cmd)
            history.Add(cmd);

        if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("quit", StringComparison.OrdinalIgnoreCase))
            break;

        // Send command
        await cmdTransit.SendAsync(new Msg { T = "exec", D = cmd }, mainCts.Token);

        // Mark command running
        commandRunning = true;
        cmdCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);

        // Read output in background
        var outputTask = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                while (!cmdCts.Token.IsCancellationRequested)
                {
                    var n = await outCh.ReadAsync(buf, cmdCts.Token);
                    if (n == 0) break;
                    Console.Write(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
            catch { }
        }, cmdCts.Token);

        // Wait for completion or Ctrl+C
        var doneTask = doneTransit.ReceiveAsync(mainCts.Token).AsTask();
        var ctrlCTask = WaitForCtrlCAsync(cmdCts.Token);

        var completed = await Task.WhenAny(doneTask, ctrlCTask);

        if (completed == ctrlCTask && await ctrlCTask)
        {
            // User pressed Ctrl+C during command
            Console.WriteLine("^C");
            await ctrlTransit.SendAsync(new Msg { T = "kill" }, mainCts.Token);
            
            // Still wait for done signal
            try { await doneTransit.ReceiveAsync(mainCts.Token); }
            catch { }
        }

        await Task.Delay(50); // Flush output
        await cmdCts.CancelAsync();
        try { await outputTask; } catch { }

        commandRunning = false;
        cmdCts.Dispose();
        cmdCts = null;

        // Show exit code if non-zero
        Msg? done = null;
        if (completed == doneTask)
        {
            try { done = await doneTask; } catch { }
        }
        
        if (done != null && int.TryParse(done.D, out var exitCode) && exitCode != 0)
        {
            WriteColored($"[exit {exitCode}]", ConsoleColor.Yellow);
            Console.WriteLine();
        }
    }

    Console.WriteLine();
    WriteColored("Connection closed.", ConsoleColor.DarkGray);
    Console.WriteLine();
}

async Task<bool> WaitForCtrlCAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                return true;
        }
        await Task.Delay(10, ct);
    }
    return false;
}

async Task<(string? Line, bool WasCtrlC)> ReadLineWithHistoryAsync(List<string> history, CancellationToken ct)
{
    if (Console.IsInputRedirected)
        return (Console.ReadLine(), false);

    var line = new StringBuilder();
    var historyIndex = history.Count;
    var cursorPos = 0;

    while (!ct.IsCancellationRequested)
    {
        if (!Console.KeyAvailable)
        {
            await Task.Delay(10);
            continue;
        }

        var key = Console.ReadKey(intercept: true);

        // Check for Ctrl+C
        if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            return (null, true);
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Console.WriteLine();
                return (line.ToString(), false);

            case ConsoleKey.Backspace:
                if (cursorPos > 0)
                {
                    line.Remove(cursorPos - 1, 1);
                    cursorPos--;
                    RedrawLine(line.ToString(), cursorPos);
                }
                break;

            case ConsoleKey.Delete:
                if (cursorPos < line.Length)
                {
                    line.Remove(cursorPos, 1);
                    RedrawLine(line.ToString(), cursorPos);
                }
                break;

            case ConsoleKey.LeftArrow:
                if (cursorPos > 0)
                {
                    cursorPos--;
                    Console.Write("\b");
                }
                break;

            case ConsoleKey.RightArrow:
                if (cursorPos < line.Length)
                {
                    Console.Write(line[cursorPos]);
                    cursorPos++;
                }
                break;

            case ConsoleKey.Home:
                while (cursorPos > 0) { Console.Write("\b"); cursorPos--; }
                break;

            case ConsoleKey.End:
                while (cursorPos < line.Length)
                {
                    Console.Write(line[cursorPos]);
                    cursorPos++;
                }
                break;

            case ConsoleKey.UpArrow:
                if (historyIndex > 0)
                {
                    historyIndex--;
                    ClearLine(line.Length, cursorPos);
                    line.Clear();
                    line.Append(history[historyIndex]);
                    Console.Write(line);
                    cursorPos = line.Length;
                }
                break;

            case ConsoleKey.DownArrow:
                if (historyIndex < history.Count - 1)
                {
                    historyIndex++;
                    ClearLine(line.Length, cursorPos);
                    line.Clear();
                    line.Append(history[historyIndex]);
                    Console.Write(line);
                    cursorPos = line.Length;
                }
                else if (historyIndex == history.Count - 1)
                {
                    historyIndex = history.Count;
                    ClearLine(line.Length, cursorPos);
                    line.Clear();
                    cursorPos = 0;
                }
                break;

            case ConsoleKey.Escape:
                ClearLine(line.Length, cursorPos);
                line.Clear();
                cursorPos = 0;
                break;

            default:
                if (!char.IsControl(key.KeyChar))
                {
                    line.Insert(cursorPos, key.KeyChar);
                    cursorPos++;
                    if (cursorPos == line.Length)
                        Console.Write(key.KeyChar);
                    else
                        RedrawLine(line.ToString(), cursorPos);
                }
                break;
        }
    }

    return (null, false);
}

void ClearLine(int len, int pos)
{
    for (var i = 0; i < pos; i++) Console.Write("\b");
    for (var i = 0; i < len; i++) Console.Write(" ");
    for (var i = 0; i < len; i++) Console.Write("\b");
}

void RedrawLine(string line, int pos)
{
    for (var i = 0; i < pos; i++) Console.Write("\b");
    Console.Write(line);
    Console.Write(" \b");
    for (var i = line.Length; i > pos; i--) Console.Write("\b");
}

void WriteColored(string text, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ForegroundColor = prev;
}

// ═══════════════════════════════════════════════════════════════════════════════
// Messages
// ═══════════════════════════════════════════════════════════════════════════════

record Msg
{
    public string? T { get; init; } // Type: exec, kill, done
    public string? D { get; init; } // Data
}

[JsonSerializable(typeof(Msg))]
partial class Ctx : JsonSerializerContext { }
