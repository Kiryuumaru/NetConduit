using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

// ═══════════════════════════════════════════════════════════════════════════════
// NetConduit Remote Shell - SSH-like CLI with Persistent Shell
// ═══════════════════════════════════════════════════════════════════════════════

if (args.Length < 2)
{
    PrintUsage();
    return;
}

if (args[0] == "server" && int.TryParse(args[1], out var port))
{
    var rest = args[2..];
    var bind = ParseBind(rest);
    if (bind == null) { PrintUsage("Invalid --bind value (expected 'loopback' or 'any')."); return; }
    if (!TryResolveAuth(rest, out var token, out var authError)) { PrintUsage(authError); return; }
    if (bind.Equals(IPAddress.Any))
        WriteBindAnyWarning();
    if (token == null)
        WriteNoAuthWarning();
    await RunServerAsync(port, bind, token);
}
else if (args[0] == "client" && int.TryParse(args[1], out var cport) && args.Length >= 3)
{
    var rest = args[3..];
    if (!TryResolveClientAuth(rest, out var ctoken, out var cauthError)) { PrintUsage(cauthError); return; }
    await RunClientAsync(args[2], cport, ctoken);
}
else
    PrintUsage();

return;

// "any" binds all interfaces (convenient but exposed); default is loopback.
IPAddress? ParseBind(string[] rest)
{
    var idx = Array.IndexOf(rest, "--bind");
    if (idx < 0) return IPAddress.Loopback;
    if (idx != Array.LastIndexOf(rest, "--bind")) return null;
    if (idx + 1 >= rest.Length) return null;
    return rest[idx + 1].ToLowerInvariant() switch
    {
        "loopback" => IPAddress.Loopback,
        "any" => IPAddress.Any,
        _ => null,
    };
}

// Fail-closed: a server must have either a token (--auth/--auth-env) or an
// explicit --allow-no-auth escape hatch. Returns (ok, token-or-null, error).
bool TryResolveAuth(string[] rest, out string? token, out string? error)
{
    return TryResolveAuthCommon(rest, out token, out error);
}

// Clients just pass a token through if given; no fail-closed requirement.
bool TryResolveClientAuth(string[] rest, out string? token, out string? error)
{
    return TryResolveAuthCommon(rest, out token, out error, clientMode: true);
}

bool TryResolveAuthCommon(string[] rest, out string? token, out string? error, bool clientMode = false)
{
    token = null;
    error = null;

    var authIdx = Array.IndexOf(rest, "--auth");
    var envIdx = Array.IndexOf(rest, "--auth-env");
    var allowNoAuth = rest.Contains("--allow-no-auth");

    if (authIdx >= 0 && authIdx != Array.LastIndexOf(rest, "--auth"))
    {
        error = "Duplicate --auth flag specified.";
        return false;
    }
    if (envIdx >= 0 && envIdx != Array.LastIndexOf(rest, "--auth-env"))
    {
        error = "Duplicate --auth-env flag specified.";
        return false;
    }

    string? flagToken = authIdx >= 0
        ? (authIdx + 1 < rest.Length ? rest[authIdx + 1] : null)
        : null;
    if (authIdx >= 0 && flagToken == null)
    {
        error = "Missing value for --auth.";
        return false;
    }
    if (flagToken != null && string.IsNullOrWhiteSpace(flagToken))
    {
        error = "--auth value must not be empty.";
        return false;
    }

    string? envToken = null;
    if (envIdx >= 0)
    {
        if (envIdx + 1 >= rest.Length)
        {
            error = "Missing value for --auth-env.";
            return false;
        }
        envToken = Environment.GetEnvironmentVariable(rest[envIdx + 1]);
        if (string.IsNullOrEmpty(envToken))
        {
            error = $"Environment variable '{rest[envIdx + 1]}' is not set or empty.";
            return false;
        }
    }

    if (flagToken != null && envToken != null)
    {
        error = "Specify only one of --auth or --auth-env.";
        return false;
    }

    token = flagToken ?? envToken;

    if (token == null && !allowNoAuth && !clientMode)
    {
        error = "No auth configured. Pass --auth <token>, --auth-env NAME, or --allow-no-auth (insecure, local demos only).";
        return false;
    }

    return true;
}

void PrintUsage(string? error = null)
{
    if (error != null)
        Console.Error.WriteLine($"Error: {error}\n");
    Console.WriteLine("NetConduit Remote Shell");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  server <port> [--bind loopback|any] [--auth <token> | --auth-env NAME | --allow-no-auth]");
    Console.WriteLine("  client <port> <host> [--auth <token> | --auth-env NAME]");
    Console.WriteLine();
    Console.WriteLine("  --bind loopback (default) listens on localhost only.");
    Console.WriteLine("  --bind any listens on all interfaces. WARNING: exposes a remote shell to the network.");
    Console.WriteLine("  Token + shell I/O travel as cleartext (no TLS): use loopback only, or tunnel over TLS/SSH on untrusted networks.");
    Console.WriteLine("  A server requires --auth/--auth-env, or explicit --allow-no-auth (insecure, local demos only).");
    Console.WriteLine("  Do not expose this sample to untrusted networks.");
}

void WriteBindAnyWarning()
{
    WriteColored("WARNING: ", ConsoleColor.Yellow);
    Console.WriteLine("--bind any listens on all network interfaces.");
    WriteColored("WARNING: ", ConsoleColor.Yellow);
    Console.WriteLine("Anyone who can reach this port and knows the token gets a shell. Token + shell I/O are cleartext (no TLS). Do not expose to untrusted networks.");
}

void WriteNoAuthWarning()
{
    WriteColored("WARNING: ", ConsoleColor.Yellow);
    Console.WriteLine("Running with --allow-no-auth: any client can execute commands. Local demos only.");
}

// ═══════════════════════════════════════════════════════════════════════════════
// SERVER
// ═══════════════════════════════════════════════════════════════════════════════

async Task RunServerAsync(int port, IPAddress bind, string? authToken)
{
    var cts = new CancellationTokenSource();
    var listener = new TcpListener(bind, port);

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

    // Shutdown via Ctrl+C only — key monitoring removed to avoid terminal compatibility issues

    listener.Start();
    WriteColored("● ", ConsoleColor.Green);
    Console.WriteLine($"Remote Shell Server listening on {bind}:{port}");
    WriteColored("  Press Ctrl+C to stop", ConsoleColor.DarkGray);
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

            _ = HandleClientAsync(tcp, endpoint, authToken, cts.Token);
        }
    }
    catch { }

    Console.WriteLine("Server stopped.");
}

async Task HandleClientAsync(TcpClient tcp, string endpoint, string? authToken, CancellationToken serverCt)
{
    Process? shellProcess = null;

    // Fail-closed auth gate: no shell starts until the client proves the token.
    // The auth channel ("auth") is accepted first with a ~5s timeout; on any
    // failure the connection is disposed without Process.Start.
    async Task<bool> AuthenticateAsync(IStreamMultiplexer mux, CancellationToken ct)
    {
        if (authToken == null) return true;

        // The client opens its "auth>>" send channel; accept it here (~5s).
        // Prefix-filtered so cmd/ctrl channels arriving early stay queued
        // for the later generic accept instead of being swallowed here.
        IReadChannel? recv = null;
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(5000);

        try
        {
            await foreach (var ch in mux.AcceptChannelsAsync("auth>>", ct: authCts.Token))
            {
                recv = ch;
                break;
            }
        }
        catch (OperationCanceledException) { }

        if (recv == null)
        {
            WriteColored($"  ✗ ", ConsoleColor.Red);
            Console.WriteLine($"[{endpoint}] auth failed: no auth channel (timeout)");
            return false;
        }

        var transit = new MessageTransit<Msg, Msg>(null, recv, Ctx.Default.Msg, Ctx.Default.Msg);
        Msg? msg = null;
        try
        {
            using var msgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            msgCts.CancelAfter(5000);
            await foreach (var m in transit.ReceiveAllAsync(msgCts.Token))
            {
                msg = m;
                break;
            }
        }
        catch (OperationCanceledException) { }

        if (msg?.T != "auth" || msg.D == null || !FixedTimeEquals(msg.D, authToken))
        {
            WriteColored($"  ✗ ", ConsoleColor.Red);
            Console.WriteLine($"[{endpoint}] auth failed: bad or missing token");
            return false;
        }

        return true;
    }

    try
    {
        var accepted = false;
        var options = new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                if (accepted) throw new InvalidOperationException();
                accepted = true;
                return Task.FromResult<IStreamPair>(new StreamPair(tcp.GetStream(), tcp));
            }
        };

        await using var mux = StreamMultiplexer.Create(options);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        mux.Start();
        // Bound the handshake so an unauthenticated connection cannot hold
        // this handler (and the auth gate below) open indefinitely.
        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        readyCts.CancelAfter(5000);
        try { await mux.WaitForReadyAsync(readyCts.Token); }
        catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested) { return; }

        // Auth gate runs before any cmd/ctrl channel is accepted and before
        // any shell process starts. Failure disposes the mux without a shell.
        if (!await AuthenticateAsync(mux, cts.Token)) return;

        // Accept channels from client
        IReadChannel? cmdCh = null;
        IReadChannel? ctrlCh = null;
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        acceptCts.CancelAfter(5000);

        await foreach (var ch in mux.AcceptChannelsAsync(ct: acceptCts.Token))
        {
            if (ch.ChannelId == "cmd") cmdCh = ch;
            else if (ch.ChannelId == "ctrl") ctrlCh = ch;
            if (cmdCh != null && ctrlCh != null) break;
        }
        if (cmdCh == null || ctrlCh == null) return;

        // Open output channel
        var outCh = mux.OpenChannel("out");

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

async Task RunClientAsync(string host, int port, string? authToken)
{
    WriteColored("Connecting to ", ConsoleColor.DarkGray);
    WriteColored($"{host}:{port}", ConsoleColor.Cyan);
    Console.WriteLine("...");

    var options = TcpMultiplexer.CreateOptions(host, port);
    await using var mux = StreamMultiplexer.Create(options);
    using var mainCts = new CancellationTokenSource();
    mux.Start();

    try { await mux.WaitForReadyAsync(mainCts.Token); }
    catch
    {
        WriteColored("✗ ", ConsoleColor.Red);
        Console.WriteLine("Connection failed");
        return;
    }

    // Open channels (auth first, then cmd/ctrl — mirrors the server gate).
    // When the server requires auth but this client has no token, no auth
    // channel is opened and the server rejects us after its auth timeout.
    if (authToken != null)
    {
        var authCh = mux.OpenChannel("auth>>");
        var authTransit = new MessageTransit<Msg, Msg>(authCh, null, Ctx.Default.Msg, Ctx.Default.Msg);
        try { await authTransit.SendAsync(new Msg { T = "auth", D = authToken }, mainCts.Token); }
        catch
        {
            WriteColored("✗ ", ConsoleColor.Red);
            Console.WriteLine("Failed to send auth token");
            return;
        }
    }

    var cmdCh = mux.OpenChannel("cmd");
    var ctrlCh = mux.OpenChannel("ctrl");

    // Accept output channel
    IReadChannel? outCh = null;
    using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(mainCts.Token);
    acceptCts.CancelAfter(5000);

    await foreach (var ch in mux.AcceptChannelsAsync(ct: acceptCts.Token))
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

// Constant-time token comparison over content so auth failures don't leak
// prefix length via timing. Lengths still differ observably (short-circuits
// on length mismatch); only equal-length content compares in constant time.
bool FixedTimeEquals(string a, string b)
{
    var ab = Encoding.UTF8.GetBytes(a);
    var bb = Encoding.UTF8.GetBytes(b);
    return CryptographicOperations.FixedTimeEquals(ab, bb);
}

// ═══════════════════════════════════════════════════════════════════════════════
// Messages
// ═══════════════════════════════════════════════════════════════════════════════

record Msg
{
    public string? T { get; init; } // Type: auth, cmd, int (interrupt)
    public string? D { get; init; } // Data (auth token for T=auth, command for T=cmd)
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

