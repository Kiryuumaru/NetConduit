// Video Stream Sample
// Demonstrates simulated video streaming with multiple channels:
// - Video channel: High-bandwidth simulated video frames
// - Audio channel: Lower-bandwidth simulated audio packets  
// This shows how multiplexing allows different data types to flow independently.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetConduit;
using NetConduit.Tcp;

var mode = args.Length > 0 ? args[0] : "server";
var host = args.Length > 1 ? args[1] : "127.0.0.1";
var port = args.Length > 2 && int.TryParse(args[2], out var p) ? p : 9003;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (mode == "server")
{
    await RunServerAsync(host, port, cts.Token);
}
else if (mode == "client")
{
    await RunClientAsync(host, port, cts.Token);
}
else
{
    Console.WriteLine("Usage: NetConduit.Samples.VideoStream [server|client] [host] [port]");
}

async Task RunServerAsync(string serverHost, int serverPort, CancellationToken cancellationToken)
{
    Console.WriteLine($"[Stream Server] Starting on {serverHost}:{serverPort}");
    Console.WriteLine("[Stream Server] Will broadcast simulated video/audio to connected clients");
    
    using var listener = new TcpListener(IPAddress.Parse(serverHost), serverPort);
    listener.Start();
    
    var clients = new List<(TcpMultiplexerConnection mux, WriteChannel video, WriteChannel audio)>();
    var clientLock = new object();
    
    // Handle incoming connections
    _ = Task.Run(async () =>
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
                Console.WriteLine($"[Stream Server] Client connected from {tcpClient.Client.RemoteEndPoint}");
                
                var mux = tcpClient.AsMux();
                var runTask = await mux.StartAsync(cancellationToken);
                
                // Open video and audio channels for this client
                var videoChannel = await mux.OpenChannelAsync(
                    new ChannelOptions { ChannelId = "Video", Priority = ChannelPriority.High }, 
                    cancellationToken);
                var audioChannel = await mux.OpenChannelAsync(
                    new ChannelOptions { ChannelId = "Audio", Priority = ChannelPriority.Normal }, 
                    cancellationToken);
                
                lock (clientLock)
                {
                    clients.Add((mux, videoChannel, audioChannel));
                }
                
                Console.WriteLine($"[Stream Server] Client ready, total clients: {clients.Count}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stream Server] Accept error: {ex.Message}");
            }
        }
    }, cancellationToken);
    
    // Stream to all connected clients
    await StreamToClientsAsync(() =>
    {
        lock (clientLock)
        {
            return clients.ToArray();
        }
    }, clientLock, clients, cancellationToken);
}

async Task StreamToClientsAsync(
    Func<(TcpMultiplexerConnection mux, WriteChannel video, WriteChannel audio)[]> getClients,
    object clientLock,
    List<(TcpMultiplexerConnection mux, WriteChannel video, WriteChannel audio)> clients,
    CancellationToken cancellationToken)
{
    var frameNumber = 0u;
    var audioPacketNumber = 0u;
    
    // Simulated video: 1920x1080 @ 30 fps ~= 8MB/s raw (we'll compress to ~500KB/frame)
    // Simulated audio: 48kHz stereo ~= 384KB/s raw (we'll send ~16KB packets)
    
    const int videoFps = 30;
    const int audioPacketsPerSecond = 24; // ~16KB per packet = ~384KB/s
    const int videoFrameSize = 500_000; // 500KB simulated compressed frame
    const int audioPacketSize = 16_000; // 16KB audio packet
    
    var videoInterval = TimeSpan.FromMilliseconds(1000.0 / videoFps);
    var audioInterval = TimeSpan.FromMilliseconds(1000.0 / audioPacketsPerSecond);
    
    var nextVideoTime = DateTime.UtcNow;
    var nextAudioTime = DateTime.UtcNow;
    
    var startTime = DateTime.UtcNow;
    var totalVideoBytes = 0L;
    var totalAudioBytes = 0L;
    
    Console.WriteLine("\n[Stream Server] Starting stream broadcast...");
    Console.WriteLine($"[Stream Server] Video: {videoFps} fps, ~{videoFrameSize / 1024}KB/frame");
    Console.WriteLine($"[Stream Server] Audio: {audioPacketsPerSecond} packets/sec, ~{audioPacketSize / 1024}KB/packet");
    Console.WriteLine();
    
    while (!cancellationToken.IsCancellationRequested)
    {
        var now = DateTime.UtcNow;
        var currentClients = getClients();
        
        if (currentClients.Length == 0)
        {
            await Task.Delay(100, cancellationToken);
            continue;
        }
        
        // Send video frame
        if (now >= nextVideoTime)
        {
            frameNumber++;
            var videoFrame = CreateVideoFrame(frameNumber, videoFrameSize);
            
            foreach (var (mux, video, audio) in currentClients)
            {
                _ = SendFrameAsync(video, videoFrame, mux, clientLock, clients, cancellationToken);
            }
            
            totalVideoBytes += videoFrameSize;
            nextVideoTime = nextVideoTime.Add(videoInterval);
            
            if (frameNumber % videoFps == 0)
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                var videoBps = totalVideoBytes / elapsed / 1024 / 1024;
                var audioBps = totalAudioBytes / elapsed / 1024 / 1024;
                Console.WriteLine($"[Stream Server] Frame {frameNumber}: " +
                    $"Video {videoBps:F2} MB/s, Audio {audioBps:F2} MB/s, " +
                    $"Clients: {currentClients.Length}");
            }
        }
        
        // Send audio packet
        if (now >= nextAudioTime)
        {
            audioPacketNumber++;
            var audioPacket = CreateAudioPacket(audioPacketNumber, audioPacketSize);
            
            foreach (var (mux, video, audio) in currentClients)
            {
                _ = SendFrameAsync(audio, audioPacket, mux, clientLock, clients, cancellationToken);
            }
            
            totalAudioBytes += audioPacketSize;
            nextAudioTime = nextAudioTime.Add(audioInterval);
        }
        
        // Sleep until next event
        var sleepTime = Math.Min(
            (nextVideoTime - DateTime.UtcNow).TotalMilliseconds,
            (nextAudioTime - DateTime.UtcNow).TotalMilliseconds);
        
        if (sleepTime > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(sleepTime), cancellationToken);
        }
    }
}

byte[] CreateVideoFrame(uint frameNumber, int size)
{
    // Create a simulated video frame with header + pattern data
    var frame = new byte[size];
    
    // Header: VFRM + frame number + timestamp + resolution
    Encoding.ASCII.GetBytes("VFRM").CopyTo(frame.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), frameNumber);
    BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(8, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16, 2), 1920); // width
    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(18, 2), 1080); // height
    
    // Fill rest with pattern (simulating compressed video data)
    var pattern = (byte)(frameNumber % 256);
    for (int i = 20; i < size; i++)
    {
        frame[i] = (byte)((pattern + i) % 256);
    }
    
    return frame;
}

byte[] CreateAudioPacket(uint packetNumber, int size)
{
    // Create a simulated audio packet with header + pattern data
    var packet = new byte[size];
    
    // Header: APKT + packet number + timestamp + sample rate
    Encoding.ASCII.GetBytes("APKT").CopyTo(packet.AsSpan(0, 4));
    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), packetNumber);
    BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(8, 8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), 48000); // sample rate
    
    // Fill rest with pattern (simulating audio data)
    var pattern = (byte)(packetNumber % 256);
    for (int i = 20; i < size; i++)
    {
        packet[i] = (byte)((pattern + i * 2) % 256);
    }
    
    return packet;
}

async Task SendFrameAsync(
    WriteChannel channel, 
    byte[] data,
    TcpMultiplexerConnection mux,
    object clientLock,
    List<(TcpMultiplexerConnection mux, WriteChannel video, WriteChannel audio)> clients,
    CancellationToken cancellationToken)
{
    try
    {
        // Send length-prefixed frame
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, data.Length);
        await channel.WriteAsync(lengthBytes, cancellationToken);
        await channel.WriteAsync(data, cancellationToken);
    }
    catch (Exception)
    {
        // Client probably disconnected - remove from list
        lock (clientLock)
        {
            clients.RemoveAll(c => c.mux == mux);
        }
    }
}

async Task RunClientAsync(string clientHost, int clientPort, CancellationToken cancellationToken)
{
    Console.WriteLine($"[Stream Client] Connecting to {clientHost}:{clientPort}");
    
    try
    {
        using var tcpClient = new TcpClient();
        await using var multiplexer = await tcpClient.ConnectMuxAsync(clientHost, clientPort, null, cancellationToken);
        Console.WriteLine("[Stream Client] Connected! Receiving stream...\n");
        
        var runTask = await multiplexer.StartAsync(cancellationToken);
        
        var stats = new StreamStats();
        var statsTimer = new System.Timers.Timer(1000);
        statsTimer.Elapsed += (_, _) => stats.PrintStats();
        statsTimer.Start();
        
        // Receive incoming channels
        await foreach (var channel in multiplexer.AcceptChannelsAsync(cancellationToken))
        {
            _ = ReceiveStreamChannelAsync(channel, stats, cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("[Stream Client] Stopped.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Stream Client] Error: {ex.Message}");
    }
}

async Task ReceiveStreamChannelAsync(ReadChannel channel, StreamStats stats, CancellationToken cancellationToken)
{
    try
    {
        var channelType = channel.ChannelId;
        Console.WriteLine($"[Stream Client] Received {channelType} channel");
        
        // Read frames
        while (!cancellationToken.IsCancellationRequested)
        {
            // Read length prefix
            var lengthBytes = new byte[4];
            var read = await channel.ReadAsync(lengthBytes, cancellationToken);
            if (read == 0) break;
            if (read != 4) continue;
            
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length <= 0 || length > 10_000_000) continue;
            
            // Read frame data
            var frameData = new byte[length];
            var totalRead = 0;
            while (totalRead < length)
            {
                read = await channel.ReadAsync(frameData.AsMemory(totalRead), cancellationToken);
                if (read == 0) break;
                totalRead += read;
            }
            
            if (totalRead != length) break;
            
            // Parse and record
            if (channelType == "Video")
            {
                var timestamp = BinaryPrimitives.ReadInt64BigEndian(frameData.AsSpan(8, 8));
                var latency = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp;
                stats.RecordVideoFrame(length, latency);
            }
            else if (channelType == "Audio")
            {
                var timestamp = BinaryPrimitives.ReadInt64BigEndian(frameData.AsSpan(8, 8));
                var latency = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp;
                stats.RecordAudioPacket(length, latency);
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Expected
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Stream Client] Channel error: {ex.Message}");
    }
}

class StreamStats
{
    private long _videoFrames;
    private long _videoBytes;
    private long _videoLatencySum;
    private long _audioPackets;
    private long _audioBytes;
    private long _audioLatencySum;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private readonly object _lock = new();
    
    public void RecordVideoFrame(int bytes, long latencyMs)
    {
        lock (_lock)
        {
            _videoFrames++;
            _videoBytes += bytes;
            _videoLatencySum += latencyMs;
        }
    }
    
    public void RecordAudioPacket(int bytes, long latencyMs)
    {
        lock (_lock)
        {
            _audioPackets++;
            _audioBytes += bytes;
            _audioLatencySum += latencyMs;
        }
    }
    
    public void PrintStats()
    {
        lock (_lock)
        {
            var elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
            if (elapsed < 1) return;
            
            var videoFps = _videoFrames / elapsed;
            var videoBps = _videoBytes / elapsed / 1024 / 1024;
            var avgVideoLatency = _videoFrames > 0 ? _videoLatencySum / (double)_videoFrames : 0;
            
            var audioPps = _audioPackets / elapsed;
            var audioBps = _audioBytes / elapsed / 1024 / 1024;
            var avgAudioLatency = _audioPackets > 0 ? _audioLatencySum / (double)_audioPackets : 0;
            
            Console.WriteLine($"[Stats] Video: {videoFps:F1} fps, {videoBps:F2} MB/s, {avgVideoLatency:F0}ms latency | " +
                $"Audio: {audioPps:F1} pps, {audioBps:F2} MB/s, {avgAudioLatency:F0}ms latency");
        }
    }
}
