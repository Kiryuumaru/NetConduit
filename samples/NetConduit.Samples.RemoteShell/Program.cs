using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Tcp;
using NetConduit.Transits;

// ═══════════════════════════════════════════════════════════════════════════════
// NetConduit Remote Shell - SSH-like CLI with Persistent Shell
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

    void Shutdown()
    {
        Console.WriteLine();
        WriteColored("Server shutting down...", ConsoleColor.Yellow);
        Console.WriteLine();
        cts.Cancel();
        listener.Stop();
    }

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Shutdown();
        Process.GetCurrentProcess().Kill();
    };

    // Background key monitor - press Q to quit
    _ = Task.Run(() =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Q || (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.C))
                {
                    Shutdown();
                    Process.GetCurrentProcess().Kill();
                    return;
                }
            }
            Thread.Sleep(100);
        }
    });

    listener.Start();
    WriteColored("● ", ConsoleColor.Green);
    Console.WriteLine($"Remote Shell Server listening on port {port}");
    WriteColored("  Press Q or Ctrl+C to stop", ConsoleColor.DarkGray);
    Console.WriteLine();
    Console.WriteLine();

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
    Process? shellProcess = null;

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

        // Open output channel
        var outCh = await mux.OpenChannelAsync(new ChannelOptions { ChannelId = "out" }, cts.Token);

        var cmdTransit = new MessageTransit<Msg, Msg>(null, cmdCh, Ctx.Default.Msg, Ctx.Default.Msg);
        var ctrlTransit = new MessageTransit<Msg, Msg>(null, ctrlCh, Ctx.Default.Msg, Ctx.Default.Msg);

        // Start persistent shell
        var isWin = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWin ? "cmd.exe" : "/bin/bash",
            Arguments = isWin ? "" : "-i", // Interactive mode for bash to show prompts
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        // Set TERM for proper prompt display on Linux
        if (!isWin)
            psi.Environment["TERM"] = "dumb";

        shellProcess = Process.Start(psi);
        if (shellProcess == null)
        {
            await outCh.WriteAsync(Encoding.UTF8.GetBytes("Failed to start shell\n"), cts.Token);
            return;
        }

        // Stream stdout to client
        _ = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                int n;
                while ((n = await shellProcess.StandardOutput.BaseStream.ReadAsync(buf, cts.Token)) > 0)
                    await outCh.WriteAsync(buf.AsMemory(0, n), cts.Token);
            }
            catch { }
        }, cts.Token);

        // Stream stderr to client (with red ANSI color)
        _ = Task.Run(async () =>
        {
            var buf = new byte[1024];
            try
            {
                int n;
                while ((n = await shellProcess.StandardError.BaseStream.ReadAsync(buf, cts.Token)) > 0)
                {
                    await outCh.WriteAsync("\x1b[31m"u8.ToArray(), cts.Token);
                    await outCh.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    await outCh.WriteAsync("\x1b[0m"u8.ToArray(), cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // Listen for Ctrl+C signals from client
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in ctrlTransit.ReceiveAllAsync(cts.Token))
                {
                    if (msg.T == "int" && shellProcess != null && !shellProcess.HasExited)
                    {
                        try
                        {
                            if (isWin)
                            {
                                // Windows: Kill child processes of cmd.exe (like ping.exe)
                                // We keep cmd.exe alive but kill any running subprocess
                                foreach (var child in Process.GetProcesses())
                                {
                                    try
                                    {
                                        // Check if this process's parent is our shell
                                        if (NativeWindows.GetParentProcessId(child.Id) == shellProcess.Id)
                                        {
                                            child.Kill(entireProcessTree: true);
                                        }
                                    }
                                    catch { }
                                }
                            }
                            else
                            {
                                // Unix: Send SIGINT to the process group
                                Process.Start("kill", $"-INT -{shellProcess.Id}");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }, cts.Token);

        // Process commands - send them to the shell
        await foreach (var msg in cmdTransit.ReceiveAllAsync(cts.Token))
        {
            if (msg.T != "cmd" || msg.D == null) continue;

            WriteColored($"  → ", ConsoleColor.DarkGray);
            Console.WriteLine($"[{endpoint}] {msg.D}");

            // Write command to shell stdin
            await shellProcess.StandardInput.WriteLineAsync(msg.D);
            await shellProcess.StandardInput.FlushAsync();
        }
    }
    catch { }
    finally
    {
        if (shellProcess != null && !shellProcess.HasExited)
        {
            try { shellProcess.Kill(entireProcessTree: true); }
            catch { try { shellProcess.Kill(); } catch { } }
        }

        WriteColored($"- ", ConsoleColor.Red);
        Console.WriteLine($"Client disconnected: {endpoint}");
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

    // Accept output channel
    ReadChannel? outCh = null;
    using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);
    acceptCts.CancelAfter(5000);

    await foreach (var ch in mux.AcceptChannelsAsync(acceptCts.Token))
    {
        if (ch.ChannelId == "out") { outCh = ch; break; }
    }

    if (outCh == null)
    {
        WriteColored("✗ ", ConsoleColor.Red);
        Console.WriteLine("Failed to establish channels");
        return;
    }

    var cmdTransit = new MessageTransit<Msg, Msg>(cmdCh, null, Ctx.Default.Msg, Ctx.Default.Msg);
    var ctrlTransit = new MessageTransit<Msg, Msg>(ctrlCh, null, Ctx.Default.Msg, Ctx.Default.Msg);

    var currentUser = Environment.UserName;
    var history = new List<string>();

    WriteColored("✓ ", ConsoleColor.Green);
    Console.WriteLine("Connected! Type 'exit' to quit.");
    Console.WriteLine();

    // Read output from server in background
    var outputCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);
    _ = Task.Run(async () =>
    {
        var buf = new byte[1024];
        try
        {
            while (!outputCts.Token.IsCancellationRequested)
            {
                var n = await outCh.ReadAsync(buf, outputCts.Token);
                if (n == 0) break;
                Console.Write(Encoding.UTF8.GetString(buf, 0, n));
            }
        }
        catch { }
    }, outputCts.Token);

    // Short delay to receive initial shell prompt
    await Task.Delay(200);

    // Main input loop
    while (!mainCts.Token.IsCancellationRequested)
    {
        var (line, wasCtrlC) = await ReadLineWithHistoryAsync(history, mainCts.Token);

        if (wasCtrlC)
        {
            // Send interrupt signal to server
            Console.WriteLine("^C");
            try { await ctrlTransit.SendAsync(new Msg { T = "int" }, mainCts.Token); }
            catch { }
            continue;
        }

        if (line == null) break;

        var trimmed = line.Trim();
        
        if (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase))
            break;

        // Add non-empty commands to history
        if (!string.IsNullOrEmpty(trimmed) && (history.Count == 0 || history[^1] != trimmed))
            history.Add(trimmed);

        // Send command to server
        await cmdTransit.SendAsync(new Msg { T = "cmd", D = line }, mainCts.Token);

        // Small delay to see output
        await Task.Delay(50);
    }

    await outputCts.CancelAsync();

    Console.WriteLine();
    WriteColored("Connection closed.", ConsoleColor.DarkGray);
    Console.WriteLine();
}

async Task<(string? Line, bool WasCtrlC)> ReadLineWithHistoryAsync(List<string> history, CancellationToken ct)
{
    if (Console.IsInputRedirected)
        return (Console.ReadLine(), false);

    // Enable Ctrl+C as input only during our input reading
    Console.TreatControlCAsInput = true;

    try
    {
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
                        var oldLen = line.Length;
                        var oldPos = cursorPos;
                        line.Remove(cursorPos - 1, 1);
                        cursorPos--;
                        RedrawLine(line.ToString(), cursorPos, oldPos, oldLen);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursorPos < line.Length)
                    {
                        var oldLen = line.Length;
                        line.Remove(cursorPos, 1);
                        RedrawLine(line.ToString(), cursorPos, cursorPos, oldLen);
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
                        var oldLen = line.Length;
                        var oldPos = cursorPos;
                        line.Insert(cursorPos, key.KeyChar);
                        cursorPos++;
                        if (cursorPos == line.Length)
                            Console.Write(key.KeyChar);
                        else
                            RedrawLine(line.ToString(), cursorPos, oldPos, oldLen);
                    }
                    break;
            }
        }

        return (null, false);
    }
    finally
    {
        // Restore normal Ctrl+C behavior
        Console.TreatControlCAsInput = false;
    }
}

void ClearLine(int len, int pos)
{
    for (var i = 0; i < pos; i++) Console.Write("\b");
    for (var i = 0; i < len; i++) Console.Write(" ");
    for (var i = 0; i < len; i++) Console.Write("\b");
}

void RedrawLine(string line, int newPos, int oldPos, int oldLen)
{
    // Move cursor to start of line
    for (var i = 0; i < oldPos; i++) Console.Write("\b");
    // Write new line
    Console.Write(line);
    // Clear any trailing characters from old line
    var extraChars = oldLen - line.Length;
    for (var i = 0; i < extraChars; i++) Console.Write(' ');
    // Move cursor back to target position
    var currentPos = line.Length + extraChars;
    for (var i = currentPos; i > newPos; i--) Console.Write("\b");
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
    public string? T { get; init; } // Type: cmd, int (interrupt)
    public string? D { get; init; } // Data
}

[JsonSerializable(typeof(Msg))]
partial class Ctx : JsonSerializerContext { }

// ═══════════════════════════════════════════════════════════════════════════════
// Windows Interop for Parent Process ID
// ═══════════════════════════════════════════════════════════════════════════════

static partial class NativeWindows
{
    public static int GetParentProcessId(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return 0;

        try
        {
            var handle = OpenProcess(0x0400 | 0x0010, false, processId);
            if (handle == IntPtr.Zero) return 0;

            try
            {
                var pbi = new PROCESS_BASIC_INFORMATION();
                if (NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out _) == 0)
                    return (int)pbi.InheritedFromUniqueProcessId;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch { }
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
