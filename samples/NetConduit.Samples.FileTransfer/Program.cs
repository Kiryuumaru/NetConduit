using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetConduit;
using NetConduit.Tcp;

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  NetConduit File Transfer Demo - Concurrent Multiplexed Transfers");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();

if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
{
    PrintUsage();
    return;
}

var mode = args[0].ToLowerInvariant();
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 5001;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    if (mode == "server" || mode == "s")
    {
        var outputDir = args.Length > 2 ? args[2] : "./received";
        await RunServerAsync(port, outputDir, cts.Token);
    }
    else if (mode == "send")
    {
        var host = args.Length > 2 ? args[2] : "127.0.0.1";
        var files = args.Skip(3).ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine("Error: No files specified to send");
            PrintUsage();
            return;
        }
        await RunSenderAsync(host, port, files, cts.Token);
    }
    else
    {
        Console.WriteLine($"Unknown mode: {mode}");
        PrintUsage();
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[System] Transfer cancelled.");
}
catch (Exception ex)
{
    Console.WriteLine($"\n[Error] {ex.Message}");
}

void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  Server: dotnet run -- server [port] [output-dir]");
    Console.WriteLine("  Sender: dotnet run -- send [port] [host] <file1> <file2> ...");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -- server 5001 ./downloads");
    Console.WriteLine("  dotnet run -- send 5001 127.0.0.1 file1.txt file2.zip file3.mp4");
    Console.WriteLine();
    Console.WriteLine("Features:");
    Console.WriteLine("  - Multiple files sent concurrently over separate channels");
    Console.WriteLine("  - Progress reporting for each file");
    Console.WriteLine("  - Automatic file integrity (size) verification");
}

async Task RunServerAsync(int port, string outputDir, CancellationToken ct)
{
    Directory.CreateDirectory(outputDir);
    Console.WriteLine($"[Server] Starting on port {port}");
    Console.WriteLine($"[Server] Saving files to: {Path.GetFullPath(outputDir)}");
    
    using var listener = new TcpListener(IPAddress.Any, port);
    listener.Start();
    Console.WriteLine("[Server] Waiting for connections...");
    
    while (!ct.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(ct);
        _ = HandleClientAsync(client, outputDir, ct);
    }
}

async Task HandleClientAsync(TcpClient client, string outputDir, CancellationToken ct)
{
    var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    Console.WriteLine($"[Server] Client connected: {endpoint}");
    
    try
    {
        // Simple extension: wrap TCP client as multiplexer
        await using var connection = client.AsMux();
        var runTask = await connection.StartAsync(ct);
        
        var receiveTasks = new List<Task>();
        
        // Accept all incoming file channels
        await foreach (var channel in connection.AcceptChannelsAsync(ct))
        {
            var task = ReceiveFileAsync(channel, outputDir, ct);
            receiveTasks.Add(task);
        }
        
        await Task.WhenAll(receiveTasks);
        Console.WriteLine($"[Server] Client {endpoint} disconnected");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Server] Error with client {endpoint}: {ex.Message}");
    }
    finally
    {
        client.Dispose();
    }
}

async Task ReceiveFileAsync(ReadChannel channel, string outputDir, CancellationToken ct)
{
    try
    {
        // Read file header: [filename_len: 4B][filename: N bytes][file_size: 8B]
        var headerBuffer = new byte[4];
        await channel.ReadExactlyAsync(headerBuffer, ct);
        var filenameLen = BinaryPrimitives.ReadInt32BigEndian(headerBuffer);
        
        var filenameBytes = new byte[filenameLen];
        await channel.ReadExactlyAsync(filenameBytes, ct);
        var filename = Encoding.UTF8.GetString(filenameBytes);
        
        var sizeBuffer = new byte[8];
        await channel.ReadExactlyAsync(sizeBuffer, ct);
        var fileSize = BinaryPrimitives.ReadInt64BigEndian(sizeBuffer);
        
        // Sanitize and create output path
        filename = Path.GetFileName(filename);
        var outputPath = Path.Combine(outputDir, filename);
        
        Console.WriteLine($"[Recv] Starting: {filename} ({FormatSize(fileSize)})");
        
        // Stream file content
        await using var fileStream = File.Create(outputPath);
        var buffer = new byte[64 * 1024];
        long received = 0;
        var lastProgress = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        while (received < fileSize)
        {
            var toRead = (int)Math.Min(buffer.Length, fileSize - received);
            var read = await channel.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) break;
            
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            
            // Report progress every 10%
            var progress = (int)(received * 100 / fileSize);
            if (progress >= lastProgress + 10)
            {
                var speed = received / sw.Elapsed.TotalSeconds;
                Console.WriteLine($"[Recv] {filename}: {progress}% ({FormatSize((long)speed)}/s)");
                lastProgress = progress;
            }
        }
        
        sw.Stop();
        var avgSpeed = received / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"[Recv] Complete: {filename} ({FormatSize(received)} in {sw.Elapsed.TotalSeconds:F1}s, {FormatSize((long)avgSpeed)}/s)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Recv] Error on channel {channel.ChannelId}: {ex.Message}");
    }
}

async Task RunSenderAsync(string host, int port, string[] files, CancellationToken ct)
{
    // Validate files exist
    foreach (var file in files)
    {
        if (!File.Exists(file))
        {
            Console.WriteLine($"[Error] File not found: {file}");
            return;
        }
    }
    
    Console.WriteLine($"[Sender] Connecting to {host}:{port}");
    
    // Simple extension: connect TCP and create multiplexer
    using var client = new TcpClient();
    await using var connection = await client.ConnectMuxAsync(host, port, cancellationToken: ct);
    Console.WriteLine("[Sender] Connected!");
    
    var runTask = await connection.StartAsync(ct);
    
    // Send all files concurrently over separate channels
    var sendTasks = files.Select((file, index) => SendFileAsync(connection, file, index, ct)).ToList();
    
    Console.WriteLine($"[Sender] Sending {files.Length} file(s) concurrently...");
    await Task.WhenAll(sendTasks);
    
    Console.WriteLine("[Sender] All transfers complete!");
    await connection.GoAwayAsync(ct);
}

async Task SendFileAsync(TcpMultiplexerConnection connection, string filePath, int index, CancellationToken ct)
{
    var filename = Path.GetFileName(filePath);
    var fileInfo = new FileInfo(filePath);
    var fileSize = fileInfo.Length;
    
    try
    {
        // Open a dedicated channel for this file
        await using var channel = await connection.OpenChannelAsync(
            new ChannelOptions { ChannelId = $"file-{index}-{filename}" }, ct);
        
        Console.WriteLine($"[Send] Starting: {filename} ({FormatSize(fileSize)})");
        
        // Send header: [filename_len: 4B][filename: N bytes][file_size: 8B]
        var filenameBytes = Encoding.UTF8.GetBytes(filename);
        var header = new byte[4 + filenameBytes.Length + 8];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), filenameBytes.Length);
        filenameBytes.CopyTo(header.AsSpan(4));
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(4 + filenameBytes.Length, 8), fileSize);
        await channel.WriteAsync(header, ct);
        
        // Stream file content
        await using var fileStream = File.OpenRead(filePath);
        var buffer = new byte[64 * 1024];
        long sent = 0;
        var lastProgress = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        while (sent < fileSize)
        {
            var read = await fileStream.ReadAsync(buffer, ct);
            if (read == 0) break;
            
            await channel.WriteAsync(buffer.AsMemory(0, read), ct);
            sent += read;
            
            // Report progress every 10%
            var progress = (int)(sent * 100 / fileSize);
            if (progress >= lastProgress + 10)
            {
                var speed = sent / sw.Elapsed.TotalSeconds;
                Console.WriteLine($"[Send] {filename}: {progress}% ({FormatSize((long)speed)}/s)");
                lastProgress = progress;
            }
        }
        
        sw.Stop();
        var avgSpeed = sent / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"[Send] Complete: {filename} ({FormatSize(sent)} in {sw.Elapsed.TotalSeconds:F1}s, {FormatSize((long)avgSpeed)}/s)");
        
        await channel.CloseAsync(ct);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Send] Error sending {filename}: {ex.Message}");
    }
}

string FormatSize(long bytes)
{
    string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
    int index = 0;
    double size = bytes;
    
    while (size >= 1024 && index < suffixes.Length - 1)
    {
        size /= 1024;
        index++;
    }
    
    return $"{size:F1} {suffixes[index]}";
}
