// RPC Framework Sample
// Demonstrates a simple request/response pattern over NetConduit multiplexed channels.
// Each RPC method call uses a dedicated channel pair for isolation.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NetConduit;
using NetConduit.Tcp;

var mode = args.Length > 0 ? args[0] : "server";
var host = args.Length > 1 ? args[1] : "127.0.0.1";
var port = args.Length > 2 && int.TryParse(args[2], out var p) ? p : 9002;

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
    Console.WriteLine("Usage: NetConduit.Samples.RpcFramework [server|client] [host] [port]");
}

async Task RunServerAsync(string serverHost, int serverPort, CancellationToken cancellationToken)
{
    Console.WriteLine($"[RPC Server] Starting on {serverHost}:{serverPort}");
    
    using var listener = new TcpListener(IPAddress.Parse(serverHost), serverPort);
    listener.Start();
    Console.WriteLine("[RPC Server] Listening for connections...");
    
    while (!cancellationToken.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cancellationToken);
        Console.WriteLine($"[RPC Server] Client connected from {client.Client.RemoteEndPoint}");
        _ = HandleClientAsync(client, cancellationToken);
    }
}

async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
{
    try
    {
        await using var multiplexer = client.AsMux();
        var runTask = await multiplexer.StartAsync(cancellationToken);
        
        // Create response channel for sending responses back
        await using var responseChannel = await multiplexer.OpenChannelAsync(
            new ChannelOptions { ChannelId = "RpcResponses" }, cancellationToken);
        
        var requestCount = 0;
        await foreach (var requestChannel in multiplexer.AcceptChannelsAsync(cancellationToken))
        {
            requestCount++;
            _ = HandleRpcCallAsync(requestChannel, responseChannel, requestCount, cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
        // Expected
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[RPC Server] Error handling client: {ex.Message}");
    }
    finally
    {
        client.Dispose();
    }
}

async Task HandleRpcCallAsync(ReadChannel requestChannel, WriteChannel responseChannel, int requestNum, CancellationToken cancellationToken)
{
    try
    {
        // Read the RPC request
        var request = await ReadRpcMessageAsync(requestChannel, cancellationToken);
        if (request == null)
        {
            Console.WriteLine($"[RPC Server] Request #{requestNum}: Invalid request");
            return;
        }
        
        Console.WriteLine($"[RPC Server] Request #{requestNum}: Received call to '{request.Method}' (ReqId: {request.RequestId})");
        
        // Process the RPC call
        var response = await ProcessRpcAsync(request, cancellationToken);
        response.RequestId = request.RequestId;
        
        // Send the response
        await WriteRpcMessageAsync(responseChannel, response, cancellationToken);
        
        Console.WriteLine($"[RPC Server] Sent response for '{request.Method}' (success={response.Success})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[RPC Server] Error processing RPC: {ex.Message}");
    }
}

async Task<RpcResponse> ProcessRpcAsync(RpcRequest request, CancellationToken cancellationToken)
{
    // Simulate RPC method processing
    await Task.Delay(Random.Shared.Next(10, 100), cancellationToken);
    
    return request.Method switch
    {
        "Add" => HandleAdd(request),
        "Multiply" => HandleMultiply(request),
        "Concat" => HandleConcat(request),
        "GetTime" => HandleGetTime(),
        "Echo" => HandleEcho(request),
        "SlowOperation" => await HandleSlowOperationAsync(request, cancellationToken),
        _ => new RpcResponse 
        { 
            Success = false, 
            Error = $"Unknown method: {request.Method}" 
        }
    };
}

RpcResponse HandleAdd(RpcRequest request)
{
    if (request.Parameters?.TryGetValue("a", out var aVal) == true &&
        request.Parameters.TryGetValue("b", out var bVal) == true)
    {
        var a = ((JsonElement)aVal).GetInt32();
        var b = ((JsonElement)bVal).GetInt32();
        return new RpcResponse { Success = true, Result = a + b };
    }
    return new RpcResponse { Success = false, Error = "Missing parameters 'a' and 'b'" };
}

RpcResponse HandleMultiply(RpcRequest request)
{
    if (request.Parameters?.TryGetValue("a", out var aVal) == true &&
        request.Parameters.TryGetValue("b", out var bVal) == true)
    {
        var a = ((JsonElement)aVal).GetDouble();
        var b = ((JsonElement)bVal).GetDouble();
        return new RpcResponse { Success = true, Result = a * b };
    }
    return new RpcResponse { Success = false, Error = "Missing parameters 'a' and 'b'" };
}

RpcResponse HandleConcat(RpcRequest request)
{
    if (request.Parameters?.TryGetValue("strings", out var stringsVal) == true)
    {
        var element = (JsonElement)stringsVal;
        var strings = element.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        return new RpcResponse { Success = true, Result = string.Join("", strings) };
    }
    return new RpcResponse { Success = false, Error = "Missing parameter 'strings'" };
}

RpcResponse HandleGetTime()
{
    return new RpcResponse 
    { 
        Success = true, 
        Result = new 
        { 
            Utc = DateTime.UtcNow.ToString("O"),
            Local = DateTime.Now.ToString("O"),
            UnixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }
    };
}

RpcResponse HandleEcho(RpcRequest request)
{
    return new RpcResponse { Success = true, Result = request.Parameters };
}

async Task<RpcResponse> HandleSlowOperationAsync(RpcRequest request, CancellationToken cancellationToken)
{
    var delayMs = 1000;
    if (request.Parameters?.TryGetValue("delayMs", out var delayVal) == true)
    {
        delayMs = ((JsonElement)delayVal).GetInt32();
    }
    
    await Task.Delay(delayMs, cancellationToken);
    return new RpcResponse { Success = true, Result = $"Completed after {delayMs}ms" };
}

async Task RunClientAsync(string clientHost, int clientPort, CancellationToken cancellationToken)
{
    Console.WriteLine($"[RPC Client] Connecting to {clientHost}:{clientPort}");
    
    try
    {
        using var tcpClient = new TcpClient();
        await using var multiplexer = await tcpClient.ConnectMuxAsync(clientHost, clientPort, null, cancellationToken);
        Console.WriteLine("[RPC Client] Connected!");
        
        var runTask = await multiplexer.StartAsync(cancellationToken);
        
        var client = new RpcClient(multiplexer, cancellationToken);
        
        // Start accepting response channel from server
        await client.StartAsync();
        
        // Demo: Make several RPC calls
        Console.WriteLine("\n--- Making RPC Calls ---\n");
        
        // Simple addition
        var addResult = await client.CallAsync<int>("Add", 
            new { a = 10, b = 32 }, cancellationToken);
        Console.WriteLine($"Add(10, 32) = {addResult}");
        
        // Multiplication
        var mulResult = await client.CallAsync<double>("Multiply", 
            new { a = 3.14, b = 2.0 }, cancellationToken);
        Console.WriteLine($"Multiply(3.14, 2.0) = {mulResult}");
        
        // String concatenation
        var concatResult = await client.CallAsync<string>("Concat", 
            new { strings = new[] { "Hello", " ", "World", "!" } }, cancellationToken);
        Console.WriteLine($"Concat([...]) = \"{concatResult}\"");
        
        // Get server time
        var timeResult = await client.CallAsync<JsonElement>("GetTime", null, cancellationToken);
        Console.WriteLine($"GetTime() = {timeResult}");
        
        // Echo
        var echoResult = await client.CallAsync<JsonElement>("Echo", 
            new { message = "test", numbers = new[] { 1, 2, 3 } }, cancellationToken);
        Console.WriteLine($"Echo(...) = {echoResult}");
        
        // Concurrent calls - all run in parallel over separate channels
        Console.WriteLine("\n--- Making 10 Concurrent Calls ---\n");
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var result = await client.CallAsync<int>("Add", 
                    new { a = index, b = index * 10 }, cancellationToken);
                Console.WriteLine($"  Concurrent call {index}: Add({index}, {index * 10}) = {result}");
            }, cancellationToken));
        }
        await Task.WhenAll(tasks);
        
        // Error handling
        Console.WriteLine("\n--- Testing Error Handling ---\n");
        try
        {
            await client.CallAsync<object>("UnknownMethod", null, cancellationToken);
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"Expected error: {ex.Message}");
        }
        
        Console.WriteLine("\n[RPC Client] All calls completed!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[RPC Client] Error: {ex.Message}");
    }
}

async Task WriteRpcMessageAsync(WriteChannel channel, object message, CancellationToken cancellationToken)
{
    var json = JsonSerializer.Serialize(message);
    var jsonBytes = Encoding.UTF8.GetBytes(json);
    
    // Write length prefix (4 bytes) + JSON data
    var lengthBytes = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(lengthBytes, jsonBytes.Length);
    
    await channel.WriteAsync(lengthBytes, cancellationToken);
    await channel.WriteAsync(jsonBytes, cancellationToken);
}

async Task<RpcRequest?> ReadRpcMessageAsync(ReadChannel channel, CancellationToken cancellationToken)
{
    // Read length prefix
    var lengthBytes = new byte[4];
    var read = await channel.ReadAsync(lengthBytes, cancellationToken);
    if (read != 4) return default;
    
    var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
    if (length <= 0 || length > 10 * 1024 * 1024) return default; // Max 10MB
    
    // Read JSON data
    var jsonBytes = new byte[length];
    var totalRead = 0;
    while (totalRead < length)
    {
        read = await channel.ReadAsync(jsonBytes.AsMemory(totalRead), cancellationToken);
        if (read == 0) return default;
        totalRead += read;
    }
    
    var json = Encoding.UTF8.GetString(jsonBytes);
    return JsonSerializer.Deserialize<RpcRequest>(json);
}

// RPC Types

class RpcRequest
{
    public string Method { get; set; } = "";
    public string? RequestId { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
}

class RpcResponse
{
    public bool Success { get; set; }
    public string? RequestId { get; set; }
    public object? Result { get; set; }
    public string? Error { get; set; }
}

class RpcException : Exception
{
    public RpcException(string message) : base(message) { }
}

// Simple RPC Client wrapper
class RpcClient
{
    private readonly TcpMultiplexerConnection _multiplexer;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<string, TaskCompletionSource<RpcResponse>> _pendingRequests = new();
    private readonly SemaphoreSlim _lock = new(1);
    private int _requestCounter;
    
    public RpcClient(TcpMultiplexerConnection multiplexer, CancellationToken cancellationToken)
    {
        _multiplexer = multiplexer;
        _cancellationToken = cancellationToken;
    }
    
    public async Task StartAsync()
    {
        // Accept the response channel from server
        await foreach (var channel in _multiplexer.AcceptChannelsAsync(_cancellationToken))
        {
            if (channel.ChannelId == "RpcResponses")
            {
                _ = ReceiveResponsesAsync(channel);
                break;
            }
        }
    }
    
    private async Task ReceiveResponsesAsync(ReadChannel channel)
    {
        try
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                // Read length prefix
                var lengthBytes = new byte[4];
                var read = await channel.ReadAsync(lengthBytes, _cancellationToken);
                if (read != 4) break;
                
                var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
                if (length <= 0 || length > 10 * 1024 * 1024) continue;
                
                // Read JSON data
                var jsonBytes = new byte[length];
                var totalRead = 0;
                while (totalRead < length)
                {
                    read = await channel.ReadAsync(jsonBytes.AsMemory(totalRead), _cancellationToken);
                    if (read == 0) break;
                    totalRead += read;
                }
                
                var json = Encoding.UTF8.GetString(jsonBytes);
                var response = JsonSerializer.Deserialize<RpcResponse>(json);
                
                if (response?.RequestId != null)
                {
                    await _lock.WaitAsync(_cancellationToken);
                    try
                    {
                        if (_pendingRequests.TryGetValue(response.RequestId, out var tcs))
                        {
                            _pendingRequests.Remove(response.RequestId);
                            tcs.TrySetResult(response);
                        }
                    }
                    finally
                    {
                        _lock.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }
    
    public async Task<T?> CallAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref _requestCounter).ToString();
        var tcs = new TaskCompletionSource<RpcResponse>();
        
        // Register pending request
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _pendingRequests[requestId] = tcs;
        }
        finally
        {
            _lock.Release();
        }
        
        try
        {
            // Create a new channel for this RPC call
            await using var requestChannel = await _multiplexer.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"RpcRequest-{requestId}" }, cancellationToken);
            
            // Build request
            var request = new RpcRequest
            {
                Method = method,
                RequestId = requestId,
                Parameters = parameters != null 
                    ? JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(parameters))
                    : null
            };
            
            // Send request
            var json = JsonSerializer.Serialize(request);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var lengthBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lengthBytes, jsonBytes.Length);
            
            await requestChannel.WriteAsync(lengthBytes, cancellationToken);
            await requestChannel.WriteAsync(jsonBytes, cancellationToken);
            
            // Wait for response with timeout
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(30));
            
            var response = await tcs.Task.WaitAsync(linkedCts.Token);
            
            if (!response.Success)
            {
                throw new RpcException(response.Error ?? "Unknown error");
            }
            
            if (response.Result == null)
            {
                return default;
            }
            
            // Convert result to requested type
            var resultJson = JsonSerializer.Serialize(response.Result);
            return JsonSerializer.Deserialize<T>(resultJson);
        }
        finally
        {
            // Clean up if still pending
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                _pendingRequests.Remove(requestId);
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
