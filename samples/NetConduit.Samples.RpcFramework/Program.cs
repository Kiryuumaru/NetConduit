// RPC Framework Sample
// Demonstrates a simple request/response pattern over NetConduit multiplexed channels
// using MessageTransit for clean, type-safe JSON messaging.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Tcp;
using NetConduit.Transits;

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
        // Use simple extension method to accept multiplexed connection
        await using var mux = await listener.AcceptMuxAsync(null, cancellationToken);
        Console.WriteLine($"[RPC Server] Client connected");
        
        _ = HandleClientAsync(mux, cancellationToken);
    }
}

async Task HandleClientAsync(IStreamMultiplexer mux, CancellationToken cancellationToken)
{
    try
    {
        var runTask = await mux.StartAsync(cancellationToken);
        
        // Open a MessageTransit for sending RPC responses
        // The server opens "responses" channel and accepts "requests" channel
        await using var rpcTransit = await mux.OpenMessageTransitAsync(
            writeChannelId: "rpc-responses",
            readChannelId: "rpc-requests",
            RpcJsonContext.Default.RpcResponse,
            RpcJsonContext.Default.RpcRequest,
            cancellationToken: cancellationToken);
        
        Console.WriteLine("[RPC Server] MessageTransit ready for RPC");
        
        // Process incoming RPC requests
        await foreach (var request in rpcTransit.ReceiveAllAsync(cancellationToken))
        {
            if (request == null) continue;
            
            Console.WriteLine($"[RPC Server] Received: {request.Method} (ReqId: {request.RequestId})");
            
            // Process and respond
            var response = await ProcessRpcAsync(request, cancellationToken);
            response.RequestId = request.RequestId;
            
            await rpcTransit.SendAsync(response, cancellationToken);
            Console.WriteLine($"[RPC Server] Sent response for '{request.Method}' (success={response.Success})");
        }
    }
    catch (OperationCanceledException)
    {
        // Expected
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[RPC Server] Error: {ex.Message}");
    }
}

async Task<RpcResponse> ProcessRpcAsync(RpcRequest request, CancellationToken cancellationToken)
{
    // Simulate processing delay
    await Task.Delay(Random.Shared.Next(10, 100), cancellationToken);
    
    return request.Method switch
    {
        "Add" => HandleAdd(request),
        "Multiply" => HandleMultiply(request),
        "Concat" => HandleConcat(request),
        "GetTime" => HandleGetTime(),
        "Echo" => HandleEcho(request),
        "SlowOperation" => await HandleSlowOperationAsync(request, cancellationToken),
        _ => new RpcResponse { Success = false, Error = $"Unknown method: {request.Method}" }
    };
}

RpcResponse HandleAdd(RpcRequest request)
{
    if (request.Parameters?.TryGetValue("a", out var aVal) == true &&
        request.Parameters.TryGetValue("b", out var bVal) == true)
    {
        var a = ((JsonElement)aVal).GetInt32();
        var b = ((JsonElement)bVal).GetInt32();
        return new RpcResponse { Success = true, Result = JsonSerializer.SerializeToElement(a + b) };
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
        return new RpcResponse { Success = true, Result = JsonSerializer.SerializeToElement(a * b) };
    }
    return new RpcResponse { Success = false, Error = "Missing parameters 'a' and 'b'" };
}

RpcResponse HandleConcat(RpcRequest request)
{
    if (request.Parameters?.TryGetValue("strings", out var stringsVal) == true)
    {
        var strings = stringsVal.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        return new RpcResponse { Success = true, Result = JsonSerializer.SerializeToElement(string.Join("", strings)) };
    }
    return new RpcResponse { Success = false, Error = "Missing parameter 'strings'" };
}

RpcResponse HandleGetTime()
{
    var timeResult = new
    {
        Utc = DateTime.UtcNow.ToString("O"),
        Local = DateTime.Now.ToString("O"),
        UnixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };
    return new RpcResponse { Success = true, Result = JsonSerializer.SerializeToElement(timeResult) };
}

RpcResponse HandleEcho(RpcRequest request)
{
    return new RpcResponse { Success = true, Result = JsonSerializer.SerializeToElement(request.Parameters) };
}

async Task<RpcResponse> HandleSlowOperationAsync(RpcRequest request, CancellationToken cancellationToken)
{
    var delayMs = 1000;
    if (request.Parameters?.TryGetValue("delayMs", out var delayVal) == true)
    {
        delayMs = delayVal.GetInt32();
    }
    
    await Task.Delay(delayMs, cancellationToken);
    return new RpcResponse { Success = true, Result = JsonSerializer.SerializeToElement($"Completed after {delayMs}ms") };
}

async Task RunClientAsync(string clientHost, int clientPort, CancellationToken cancellationToken)
{
    Console.WriteLine($"[RPC Client] Connecting to {clientHost}:{clientPort}");
    
    try
    {
        // Use simple extension method to connect multiplexed
        using var tcpClient = new TcpClient();
        await using var mux = await tcpClient.ConnectMuxAsync(clientHost, clientPort, null, cancellationToken);
        Console.WriteLine("[RPC Client] Connected!");
        
        var runTask = await mux.StartAsync(cancellationToken);
        
        // Open MessageTransit for RPC (client sends requests, accepts responses)
        await using var rpcTransit = await mux.OpenMessageTransitAsync(
            writeChannelId: "rpc-requests",
            readChannelId: "rpc-responses",
            RpcJsonContext.Default.RpcRequest,
            RpcJsonContext.Default.RpcResponse,
            cancellationToken: cancellationToken);
        
        var client = new SimpleRpcClient(rpcTransit);
        
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
        
        // Concurrent calls - all use the same MessageTransit
        Console.WriteLine("\n--- Making 10 Sequential Calls ---\n");
        for (int i = 0; i < 10; i++)
        {
            var result = await client.CallAsync<int>("Add", 
                new { a = i, b = i * 10 }, cancellationToken);
            Console.WriteLine($"  Call {i}: Add({i}, {i * 10}) = {result}");
        }
        
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

// RPC Types with AOT-safe JSON serialization

record RpcRequest
{
    public string Method { get; set; } = "";
    public string? RequestId { get; set; }
    public Dictionary<string, JsonElement>? Parameters { get; set; }
}

record RpcResponse
{
    public bool Success { get; set; }
    public string? RequestId { get; set; }
    public JsonElement? Result { get; set; }
    public string? Error { get; set; }
}

class RpcException(string message) : Exception(message);

// AOT-safe JSON serialization context
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
partial class RpcJsonContext : JsonSerializerContext;

/// <summary>
/// Simple RPC client using MessageTransit for request/response pattern.
/// </summary>
class SimpleRpcClient
{
    private readonly MessageTransit<RpcRequest, RpcResponse> _transit;
    private readonly SemaphoreSlim _lock = new(1);
    private int _requestCounter;
    
    public SimpleRpcClient(MessageTransit<RpcRequest, RpcResponse> transit)
    {
        _transit = transit;
    }
    
    public async Task<T?> CallAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        // Serialize parameters to Dictionary<string, JsonElement> if needed
        Dictionary<string, JsonElement>? paramsDict = null;
        if (parameters != null)
        {
            var json = JsonSerializer.Serialize(parameters);
            paramsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        
        var requestId = Interlocked.Increment(ref _requestCounter).ToString();
        var request = new RpcRequest
        {
            Method = method,
            RequestId = requestId,
            Parameters = paramsDict
        };
        
        // Send request and wait for matching response
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await _transit.SendAsync(request, cancellationToken);
            
            // Wait for response (with timeout)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            
            await foreach (var response in _transit.ReceiveAllAsync(timeoutCts.Token))
            {
                if (response?.RequestId == requestId)
                {
                    if (!response.Success)
                        throw new RpcException(response.Error ?? "Unknown error");
                    
                    if (response.Result == null)
                        return default;
                    
                    return response.Result.Value.Deserialize<T>();
                }
            }
            
            throw new RpcException("No response received");
        }
        finally
        {
            _lock.Release();
        }
    }
}
