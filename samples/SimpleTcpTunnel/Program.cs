using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetConduit;
using NetConduit.Interfaces;
using NetConduit.Models;
using NetConduit.Transport.Tcp;
using NetConduit.Transit.Stream;
using NetConduit.Transit.DuplexStream;
using NetConduit.Transit.Message;
using NetConduit.Transit.DeltaMessage;
using NetConduit.Transport.WebSocket;

// ═══════════════════════════════════════════════════════════════
//   NetConduit TCP Tunnel - Port Forwarding via Relay Server
//   Similar to SSH -L tunneling or ngrok
// ═══════════════════════════════════════════════════════════════

if (args.Length < 1 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var command = args[0].ToLowerInvariant();

try
{
    return command switch
    {
        "relay" => await RunRelayAsync(args, cts.Token),
        "agent" => await RunAgentAsync(args, cts.Token),
        "forward" => await RunForwardAsync(args, cts.Token),
        "list" => await RunListAsync(args, cts.Token),
        _ => PrintUsage($"Unknown command: {command}")
    };
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nShutting down...");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// ═══════════════════════════════════════════════════════════════
//   Usage
// ═══════════════════════════════════════════════════════════════

static int PrintUsage(string? error = null)
{
    if (error != null)
        Console.Error.WriteLine($"Error: {error}\n");

    Console.WriteLine("""
        ═══════════════════════════════════════════════════════════════
          NetConduit TCP Tunnel - Port Forwarding via Relay Server
        ═══════════════════════════════════════════════════════════════

        Usage:
          relay <tcp-port> <ws-port/path> [--bind loopback|any] [--auth <token> | --auth-env NAME | --allow-no-auth] [--allow-service NAME]...
          agent <relay-host> <port[/path]> <name> <local-port> [--auth <token> | --auth-env NAME]
          forward <relay-host> <port[/path]> <name> <local-port> [--auth <token> | --auth-env NAME]
          list <relay-host> <port[/path]> [--auth <token> | --auth-env NAME]

        Flags:
          --bind loopback (default) listens on localhost only.
          --bind any listens on all interfaces. WARNING: exposes the relay to the network; token + tunnel bytes travel as cleartext (no TLS), so use loopback only or tunnel over TLS/SSH on untrusted networks.
          Relay requires --auth/--auth-env, or explicit --allow-no-auth (insecure, local demos only).
          --allow-service NAME (repeatable) restricts which service names may register.
          Clients pass --auth/--auth-env to authenticate to a relay that requires it. Prefer --auth-env: --auth exposes the token in process lists.
          Do not expose this sample to untrusted networks.

        Port Format:
          5000        TCP connection (direct)
          5001/relay  WebSocket connection (ws://host:port/path)

        Examples:
          # Start relay (TCP:5000, WS:5001/relay)
          dotnet run -- relay 5000 5001/relay --auth-env TUNNEL_TOKEN

          # Agent: expose local :8080 as "web" via TCP
          dotnet run -- agent localhost 5000 web 8080 --auth-env TUNNEL_TOKEN

          # Forward: access "web" on local :4000 via TCP
          dotnet run -- forward localhost 5000 web 4000 --auth-env TUNNEL_TOKEN

          # Agent via WebSocket (firewall-friendly)
          dotnet run -- agent relay.example.com 5001/relay myapp 3000 --auth-env TUNNEL_TOKEN

          # List available services
          dotnet run -- list localhost 5000 --auth-env TUNNEL_TOKEN
        """);

    return error != null ? 1 : 0;
}

// ═══════════════════════════════════════════════════════════════
//   Relay Server
// ═══════════════════════════════════════════════════════════════

static async Task<int> RunRelayAsync(string[] args, CancellationToken ct)
{
    if (args.Length < 3)
        return PrintUsage("relay requires: <tcp-port> <ws-port/path>");

    if (!int.TryParse(args[1], out var tcpPort))
        return PrintUsage($"Invalid TCP port: {args[1]}");

    var (wsPort, wsPath) = ParseWsPortPath(args[2]);

    var rest = args[3..];
    var loopbackOnly = true;
    var bindIdx = Array.IndexOf(rest, "--bind");
    if (bindIdx >= 0)
    {
        if (bindIdx != Array.LastIndexOf(rest, "--bind"))
            return PrintUsage("Duplicate --bind flag specified.");
        if (bindIdx + 1 >= rest.Length)
            return PrintUsage("Missing value for --bind (expected 'loopback' or 'any').");
        var mode = rest[bindIdx + 1].ToLowerInvariant();
        if (mode == "loopback") loopbackOnly = true;
        else if (mode == "any") loopbackOnly = false;
        else return PrintUsage("Invalid --bind value (expected 'loopback' or 'any').");
    }

    if (!TryResolveRelayAuth(rest, out var authToken, out var authError))
        return PrintUsage(authError);

    var allowedServices = new HashSet<string>(StringComparer.Ordinal);
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--allow-service")
        {
            if (i + 1 >= rest.Length)
                return PrintUsage("Missing value for --allow-service.");
            allowedServices.Add(rest[i + 1]);
            i++;
        }
    }

    var agents = new ConcurrentDictionary<string, AgentConnection>();
    var forwards = new ConcurrentDictionary<string, ForwardConnection>();

    Console.WriteLine($"╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine($"║  NetConduit Relay Server                                     ║");
    Console.WriteLine($"╠══════════════════════════════════════════════════════════════╣");
    Console.WriteLine($"║  TCP:       port {tcpPort,-43} ║");
    Console.WriteLine($"║  WebSocket: port {wsPort} path {wsPath,-32} ║");
    Console.WriteLine($"╚══════════════════════════════════════════════════════════════╝");
    Console.WriteLine();

    if (!loopbackOnly)
    {
        Console.WriteLine("WARNING: --bind any listens on all network interfaces.");
        Console.WriteLine("WARNING: anyone who can reach these ports and knows the token can register/request tunnels. Token + tunnel bytes are cleartext (no TLS). Do not expose to untrusted networks.");
        Console.WriteLine();
    }
    if (authToken == null)
    {
        Console.WriteLine("WARNING: running with --allow-no-auth: anyone can register services and open tunnels. Local demos only.");
        Console.WriteLine();
    }
    if (allowedServices.Count > 0)
        Console.WriteLine($"[Relay] Service allowlist: {string.Join(", ", allowedServices)}");

    // Start TCP listener
    var tcpListener = new TcpListener(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, tcpPort);
    tcpListener.Start();
    Console.WriteLine($"[Relay] TCP listening on {(loopbackOnly ? "127.0.0.1" : "0.0.0.0")}:{tcpPort}");

    // Start WebSocket listener
    var wsListener = new HttpListener();
    wsListener.Prefixes.Add(loopbackOnly
        ? $"http://localhost:{wsPort}{wsPath}/"
        : $"http://+:{wsPort}{wsPath}/");
    wsListener.Start();
    Console.WriteLine($"[Relay] WebSocket listening on port {wsPort}{wsPath}");

    // Accept tasks
    var tcpAcceptTask = AcceptTcpConnectionsAsync(tcpListener, agents, forwards, authToken, allowedServices, ct);
    var wsAcceptTask = AcceptWebSocketConnectionsAsync(wsListener, wsPath, agents, forwards, authToken, allowedServices, ct);

    try
    {
        await Task.WhenAll(tcpAcceptTask, wsAcceptTask);
    }
    finally
    {
        tcpListener.Stop();
        wsListener.Stop();
    }

    return 0;
}

static async Task AcceptTcpConnectionsAsync(
    TcpListener listener,
    ConcurrentDictionary<string, AgentConnection> agents,
    ConcurrentDictionary<string, ForwardConnection> forwards,
    string? authToken,
    HashSet<string> allowedServices,
    CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var tcpClient = await listener.AcceptTcpClientAsync(ct);
            _ = HandleRelayConnectionAsync(
                () => Task.FromResult<IStreamPair>(new StreamPair(tcpClient.GetStream(), tcpClient)),
                "TCP",
                agents,
                forwards,
                authToken,
                allowedServices,
                ct);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] TCP accept error: {ex.Message}");
        }
    }
}

static async Task AcceptWebSocketConnectionsAsync(
    HttpListener listener,
    string expectedPath,
    ConcurrentDictionary<string, AgentConnection> agents,
    ConcurrentDictionary<string, ForwardConnection> forwards,
    string? authToken,
    HashSet<string> allowedServices,
    CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var context = await listener.GetContextAsync().WaitAsync(ct);

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var wsContext = await context.AcceptWebSocketAsync(null);
            var ws = wsContext.WebSocket;

            _ = HandleRelayConnectionAsync(
                () => Task.FromResult<IStreamPair>(new WebSocketStreamPair(ws)),
                "WebSocket",
                agents,
                forwards,
                authToken,
                allowedServices,
                ct);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Console.WriteLine($"[Relay] WebSocket accept error: {ex.Message}");
        }
    }
}

static async Task HandleRelayConnectionAsync(
    Func<Task<IStreamPair>> streamFactory,
    string transport,
    ConcurrentDictionary<string, AgentConnection> agents,
    ConcurrentDictionary<string, ForwardConnection> forwards,
    string? authToken,
    HashSet<string> allowedServices,
    CancellationToken ct)
{
    string? registeredService = null;
    string? connectionId = null;
    var authed = authToken == null; // no-auth mode: everything already permitted

    try
    {
        var options = new MultiplexerOptions
        {
            StreamFactory = _ => streamFactory()
        };

        await using var mux = StreamMultiplexer.Create(options);
        mux.Start();
        // Bounded handshake (~5s) so a peer that never completes setup
        // cannot hold this handler open indefinitely.
        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readyCts.CancelAfter(TimeSpan.FromSeconds(5));
        try { await mux.WaitForReadyAsync(readyCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }

        connectionId = Guid.NewGuid().ToString()[..8];
        Console.WriteLine($"[Relay] {transport} connection {connectionId} established");

        // Open control channels (bounded: a peer that never opens its
        // control channel cannot hold this handler open indefinitely).
        var ctrlSend = mux.OpenChannel("ctrl<<");
        IReadChannel? ctrlRecv = null;

        using var ctrlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctrlCts.CancelAfter(TimeSpan.FromSeconds(5));

        await foreach (var ch in mux.AcceptChannelsAsync(ct: ctrlCts.Token))
        {
            if (ch.ChannelId == "ctrl>>")
            {
                ctrlRecv = ch;
                break;
            }
        }

        if (ctrlRecv == null)
        {
            Console.WriteLine($"[Relay] {connectionId} failed to establish control channel");
            return;
        }

        var transit = new MessageTransit<TunnelMessage, TunnelMessage>(
            ctrlSend, ctrlRecv,
            TunnelJsonContext.Default.TunnelMessage,
            TunnelJsonContext.Default.TunnelMessage);

        // Bounded auth grace period: an unauthenticated connection that does
        // not prove its token within ~5s is closed. The timer is disabled
        // once this connection authenticates.
        using var authGraceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (authToken != null)
            authGraceCts.CancelAfter(TimeSpan.FromSeconds(5));

        await foreach (var msg in transit.ReceiveAllAsync(authGraceCts.Token))
        {
            switch (msg)
            {
                case Authenticate auth:
                    if (authToken != null && FixedTimeEquals(auth.Token ?? "", authToken))
                    {
                        authed = true;
                        authGraceCts.CancelAfter(Timeout.InfiniteTimeSpan);
                    }
                    else if (authToken != null)
                        Console.WriteLine($"[Relay] {connectionId} auth failed");
                    break;

                case RegisterService reg:
                    if (!authed)
                    {
                        Console.WriteLine($"[Relay] {connectionId} rejected unauthenticated register of '{reg.Name}'");
                        await transit.SendAsync(new RegisterAck(false, "Not authenticated"), ct);
                        break;
                    }
                    if (allowedServices.Count > 0 && !allowedServices.Contains(reg.Name))
                    {
                        Console.WriteLine($"[Relay] {connectionId} rejected non-allowlisted service '{reg.Name}'");
                        await transit.SendAsync(new RegisterAck(false, $"Service '{reg.Name}' is not allowlisted"), ct);
                        break;
                    }
                    Console.WriteLine($"[Relay] {connectionId} registering service '{reg.Name}' (device: {reg.DeviceId}, port: {reg.LocalPort})");

                    var agentConn = new AgentConnection(mux, transit, reg.DeviceId, reg.LocalPort);

                    if (agents.TryAdd(reg.Name, agentConn))
                    {
                        registeredService = reg.Name;
                        await transit.SendAsync(new RegisterAck(true, null), ct);
                        Console.WriteLine($"[Relay] Service '{reg.Name}' registered");
                    }
                    else
                    {
                        await transit.SendAsync(new RegisterAck(false, $"Service '{reg.Name}' already exists"), ct);
                    }
                    break;

                case TunnelRequest req:
                    if (!authed)
                    {
                        Console.WriteLine($"[Relay] {connectionId} rejected unauthenticated tunnel to '{req.ServiceName}' (id: {req.TunnelId})");
                        await transit.SendAsync(new TunnelReject(req.TunnelId, "Not authenticated"), ct);
                        break;
                    }
                    Console.WriteLine($"[Relay] {connectionId} requesting tunnel to '{req.ServiceName}' (id: {req.TunnelId})");

                    if (!agents.TryGetValue(req.ServiceName, out var agent))
                    {
                        await transit.SendAsync(new TunnelReject(req.TunnelId, $"Service '{req.ServiceName}' not found"), ct);
                        continue;
                    }

                    // Create tunnel channels
                    var tunnelId = req.TunnelId;

                    try
                    {
                        // Open bidirectional duplex streams to both sides
                        var agentDuplex = await agent.Mux.OpenDuplexStreamAsync($"tunnel:{tunnelId}", ct);
                        var forwardDuplex = await mux.OpenDuplexStreamAsync($"tunnel:{tunnelId}", ct);

                        await transit.SendAsync(new TunnelAccept(tunnelId), ct);
                        Console.WriteLine($"[Relay] Tunnel {tunnelId} established: forward ↔ {req.ServiceName}");

                        // Bridge the duplex streams
                        _ = BridgeDuplexStreamsAsync(forwardDuplex, agentDuplex, tunnelId, ct);
                    }
                    catch (Exception ex)
                    {
                        await transit.SendAsync(new TunnelReject(tunnelId, ex.Message), ct);
                    }
                    break;

                case ListRequest:
                    if (!authed)
                    {
                        Console.WriteLine($"[Relay] {connectionId} rejected unauthenticated list request");
                        return; // close without enumeration
                    }
                    var services = agents.Select(kv => new ServiceInfo(
                        kv.Key, kv.Value.DeviceId, kv.Value.LocalPort)).ToArray();
                    await transit.SendAsync(new ServiceList(services), ct);
                    break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        if (!ct.IsCancellationRequested && authToken != null && !authed)
            Console.WriteLine($"[Relay] Connection {connectionId} closed: auth timeout");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Relay] Connection {connectionId} error: {ex.Message}");
    }
    finally
    {
        if (registeredService != null)
        {
            agents.TryRemove(registeredService, out _);
            Console.WriteLine($"[Relay] Service '{registeredService}' unregistered");
        }
        Console.WriteLine($"[Relay] Connection {connectionId} closed");
    }
}

static async Task BridgeDuplexStreamsAsync(DuplexStreamTransit a, DuplexStreamTransit b, string tunnelId, CancellationToken ct)
{
    try
    {
        var aToB = a.CopyToAsync(b, ct);
        var bToA = b.CopyToAsync(a, ct);
        await Task.WhenAny(aToB, bToA);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Relay] Tunnel {tunnelId} bridge error: {ex.Message}");
    }
    finally
    {
        await a.DisposeAsync();
        await b.DisposeAsync();
        Console.WriteLine($"[Relay] Tunnel {tunnelId} closed");
    }
}

// ═══════════════════════════════════════════════════════════════
//   Agent
// ═══════════════════════════════════════════════════════════════

static async Task<int> RunAgentAsync(string[] args, CancellationToken ct)
{
    if (args.Length < 5)
        return PrintUsage("agent requires: <relay-host> <port[/path]> <name> <local-port>");

    var host = args[1];
    var portPath = args[2];
    var serviceName = args[3];

    if (!int.TryParse(args[4], out var localPort))
        return PrintUsage($"Invalid local port: {args[4]}");

    if (!TryResolveClientAuth(args[5..], out var agentAuthToken, out var agentAuthError))
        return PrintUsage(agentAuthError);

    var deviceId = Environment.MachineName;
    var (options, isWs) = CreateClientOptions(host, portPath);
    var transport = isWs ? "WebSocket" : "TCP";

    Console.WriteLine($"╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine($"║  NetConduit Agent                                            ║");
    Console.WriteLine($"╠══════════════════════════════════════════════════════════════╣");
    Console.WriteLine($"║  Service:   {serviceName,-47} ║");
    Console.WriteLine($"║  Device:    {deviceId,-47} ║");
    Console.WriteLine($"║  Local:     localhost:{localPort,-38} ║");
    Console.WriteLine($"║  Relay:     {host}:{portPath,-40} ║");
    Console.WriteLine($"║  Transport: {transport,-47} ║");
    Console.WriteLine($"╚══════════════════════════════════════════════════════════════╝");
    Console.WriteLine();

    // MaxAutoReconnectAttempts defaults to -1 (unlimited reconnect with replay)
    await using var mux = StreamMultiplexer.Create(options);

    mux.Reconnecting += (_, e) =>
    {
        if (e.Attempt > 1)
            Console.WriteLine($"[Agent] Reconnecting... (attempt {e.Attempt})");
    };

    mux.Connected += (_, _) => Console.WriteLine("[Agent] Reconnected to relay");

    mux.Start();
    await mux.WaitForReadyAsync(ct);
    Console.WriteLine($"[Agent] Connected to relay via {transport}");

    // Open control channel
    var ctrlSend = mux.OpenChannel("ctrl>>");

    IReadChannel? ctrlRecv = null;
    await foreach (var ch in mux.AcceptChannelsAsync(ct: ct))
    {
        if (ch.ChannelId == "ctrl<<")
        {
            ctrlRecv = ch;
            break;
        }
    }

    if (ctrlRecv == null)
    {
        Console.WriteLine("[Agent] Failed to establish control channel");
        return 1;
    }

    var transit = new MessageTransit<TunnelMessage, TunnelMessage>(
        ctrlSend, ctrlRecv,
        TunnelJsonContext.Default.TunnelMessage,
        TunnelJsonContext.Default.TunnelMessage);

    // Register service (auth first when the relay requires it)
    if (agentAuthToken != null)
        await transit.SendAsync(new Authenticate(agentAuthToken), ct);
    await transit.SendAsync(new RegisterService(serviceName, deviceId, localPort), ct);

    var ackMsg = await transit.ReceiveAsync(ct);
    if (ackMsg is not RegisterAck ack || !ack.Success)
    {
        var error = (ackMsg as RegisterAck)?.Error ?? "Unknown error";
        Console.WriteLine($"[Agent] Registration failed: {error}");
        return 1;
    }

    Console.WriteLine($"[Agent] Service '{serviceName}' registered successfully");
    Console.WriteLine($"[Agent] Waiting for tunnel requests... (Ctrl+C to stop)");

    // Handle tunnel requests
    _ = Task.Run(async () =>
    {
        await foreach (var readChannel in mux.AcceptChannelsAsync(ct: ct))
        {
            // Look for tunnel channels with >> suffix (outbound from relay)
            if (readChannel.ChannelId.StartsWith("tunnel:") && readChannel.ChannelId.EndsWith(">>"))
            {
                var tunnelId = readChannel.ChannelId["tunnel:".Length..^">>".Length];
                Console.WriteLine($"[Agent] Tunnel {tunnelId} incoming");
                
                // Complete the duplex by opening the response channel
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var writeChannel = mux.OpenChannel(
                            $"tunnel:{tunnelId}<<");
                        var duplex = new DuplexStreamTransit(writeChannel, readChannel);
                        await HandleAgentTunnelAsync(duplex, localPort, tunnelId, ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Agent] Tunnel {tunnelId} setup error: {ex.Message}");
                    }
                }, ct);
            }
        }
    }, ct);

    // Keep alive
    await Task.Delay(Timeout.Infinite, ct);
    return 0;
}

static async Task HandleAgentTunnelAsync(DuplexStreamTransit tunnelStream, int localPort, string tunnelId, CancellationToken ct)
{
    TcpClient? tcpClient = null;

    try
    {
        // Connect to local service
        tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, localPort, ct);
        var stream = tcpClient.GetStream();

        Console.WriteLine($"[Agent] Tunnel {tunnelId} → localhost:{localPort}");

        // Bridge tunnel stream to local TCP bidirectionally
        var tunnelToTcp = tunnelStream.CopyToAsync(stream, ct);
        var tcpToTunnel = stream.CopyToAsync(tunnelStream, ct);

        await Task.WhenAny(tunnelToTcp, tcpToTunnel);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Agent] Tunnel {tunnelId} error: {ex.Message}");
    }
    finally
    {
        tcpClient?.Dispose();
        await tunnelStream.DisposeAsync();
        Console.WriteLine($"[Agent] Tunnel {tunnelId} closed");
    }
}

// ═══════════════════════════════════════════════════════════════
//   Forward
// ═══════════════════════════════════════════════════════════════

static async Task<int> RunForwardAsync(string[] args, CancellationToken ct)
{
    if (args.Length < 5)
        return PrintUsage("forward requires: <relay-host> <port[/path]> <name> <local-port>");

    var host = args[1];
    var portPath = args[2];
    var serviceName = args[3];

    if (!int.TryParse(args[4], out var localPort))
        return PrintUsage($"Invalid local port: {args[4]}");

    if (!TryResolveClientAuth(args[5..], out var forwardAuthToken, out var forwardAuthError))
        return PrintUsage(forwardAuthError);

    var (options, isWs) = CreateClientOptions(host, portPath);
    var transport = isWs ? "WebSocket" : "TCP";

    Console.WriteLine($"╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine($"║  NetConduit Forward                                          ║");
    Console.WriteLine($"╠══════════════════════════════════════════════════════════════╣");
    Console.WriteLine($"║  Service:   {serviceName,-47} ║");
    Console.WriteLine($"║  Local:     localhost:{localPort,-38} ║");
    Console.WriteLine($"║  Relay:     {host}:{portPath,-40} ║");
    Console.WriteLine($"║  Transport: {transport,-47} ║");
    Console.WriteLine($"╚══════════════════════════════════════════════════════════════╝");
    Console.WriteLine();

    var pendingTunnels = new ConcurrentDictionary<string, TaskCompletionSource<DuplexStreamTransit>>();

    // MaxAutoReconnectAttempts defaults to -1 (unlimited reconnect with replay)
    await using var mux = StreamMultiplexer.Create(options);

    mux.Reconnecting += (_, e) =>
    {
        if (e.Attempt > 1)
            Console.WriteLine($"[Forward] Reconnecting... (attempt {e.Attempt})");
    };

    mux.Connected += (_, _) => Console.WriteLine("[Forward] Reconnected to relay");

    mux.Start();
    await mux.WaitForReadyAsync(ct);
    Console.WriteLine($"[Forward] Connected to relay via {transport}");

    // Open control channel
    var ctrlSend = mux.OpenChannel("ctrl>>");

    IReadChannel? ctrlRecv = null;
    var ctrlReceivedTcs = new TaskCompletionSource();

    // Accept task runs continuously to handle both control and tunnel channels
    _ = Task.Run(async () =>
    {
        await foreach (var readChannel in mux.AcceptChannelsAsync(ct: ct))
        {
            if (readChannel.ChannelId == "ctrl<<")
            {
                ctrlRecv = readChannel;
                ctrlReceivedTcs.TrySetResult();
            }
            else if (readChannel.ChannelId.StartsWith("tunnel:") && readChannel.ChannelId.EndsWith(">>"))
            {
                // Handle tunnel channel - complete the duplex by opening response channel
                var tunnelId = readChannel.ChannelId["tunnel:".Length..^">>".Length];
                if (pendingTunnels.TryGetValue(tunnelId, out var tcs))
                {
                    try
                    {
                        var writeChannel = mux.OpenChannel(
                            $"tunnel:{tunnelId}<<");
                        var duplex = new DuplexStreamTransit(writeChannel, readChannel);
                        tcs.TrySetResult(duplex);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }
            }
        }
    }, ct);

    // Wait for control channel
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
    try
    {
        await ctrlReceivedTcs.Task.WaitAsync(timeoutCts.Token);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        Console.WriteLine("[Forward] Failed to establish control channel (timeout)");
        return 1;
    }

    var transit = new MessageTransit<TunnelMessage, TunnelMessage>(
        ctrlSend, ctrlRecv,
        TunnelJsonContext.Default.TunnelMessage,
        TunnelJsonContext.Default.TunnelMessage);

    // Auth first when the relay requires it; tunnel requests follow per connection.
    if (forwardAuthToken != null)
        await transit.SendAsync(new Authenticate(forwardAuthToken), ct);

    // Start local listener
    var listener = new TcpListener(IPAddress.Loopback, localPort);
    listener.Start();
    Console.WriteLine($"[Forward] Listening on localhost:{localPort}");
    Console.WriteLine($"[Forward] Forwarding to service '{serviceName}'");
    Console.WriteLine($"[Forward] Ready! (Ctrl+C to stop)");

    // Handle incoming local connections
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var tcpClient = await listener.AcceptTcpClientAsync(ct);
            _ = HandleForwardConnectionAsync(tcpClient, transit, mux, serviceName, pendingTunnels, ct);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Console.WriteLine($"[Forward] Accept error: {ex.Message}");
        }
    }

    listener.Stop();
    return 0;
}

static async Task HandleForwardConnectionAsync(
    TcpClient tcpClient,
    MessageTransit<TunnelMessage, TunnelMessage> transit,
    IStreamMultiplexer mux,
    string serviceName,
    ConcurrentDictionary<string, TaskCompletionSource<DuplexStreamTransit>> pendingTunnels,
    CancellationToken ct)
{
    var tunnelId = Guid.NewGuid().ToString()[..8];

    try
    {
        Console.WriteLine($"[Forward] Local connection → requesting tunnel {tunnelId}");

        // Request tunnel
        var tcs = new TaskCompletionSource<DuplexStreamTransit>();
        pendingTunnels[tunnelId] = tcs;

        await transit.SendAsync(new TunnelRequest(serviceName, tunnelId), ct);

        // Wait for accept/reject
        var responseTask = transit.ReceiveAsync(ct).AsTask();
        var response = await responseTask;

        if (response is TunnelReject reject)
        {
            Console.WriteLine($"[Forward] Tunnel {tunnelId} rejected: {reject.Reason}");
            pendingTunnels.TryRemove(tunnelId, out _);
            tcpClient.Dispose();
            return;
        }

        if (response is not TunnelAccept)
        {
            Console.WriteLine($"[Forward] Tunnel {tunnelId} unexpected response: {response}");
            pendingTunnels.TryRemove(tunnelId, out _);
            tcpClient.Dispose();
            return;
        }

        // Wait for tunnel duplex stream with timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        DuplexStreamTransit tunnelStream;
        try
        {
            tunnelStream = await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"[Forward] Tunnel {tunnelId} channel timeout");
            pendingTunnels.TryRemove(tunnelId, out _);
            tcpClient.Dispose();
            return;
        }

        pendingTunnels.TryRemove(tunnelId, out _);
        Console.WriteLine($"[Forward] Tunnel {tunnelId} established");

        // Bridge local TCP to tunnel stream bidirectionally
        var stream = tcpClient.GetStream();
        var tcpToTunnel = stream.CopyToAsync(tunnelStream, ct);
        var tunnelToTcp = tunnelStream.CopyToAsync(stream, ct);

        await Task.WhenAny(tcpToTunnel, tunnelToTcp);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Forward] Tunnel {tunnelId} error: {ex.Message}");
    }
    finally
    {
        tcpClient.Dispose();
        Console.WriteLine($"[Forward] Tunnel {tunnelId} closed");
    }
}

// ═══════════════════════════════════════════════════════════════
//   List
// ═══════════════════════════════════════════════════════════════

static async Task<int> RunListAsync(string[] args, CancellationToken ct)
{
    if (args.Length < 3)
        return PrintUsage("list requires: <relay-host> <port[/path]>");

    var host = args[1];
    var portPath = args[2];

    if (!TryResolveClientAuth(args[3..], out var listAuthToken, out var listAuthError))
        return PrintUsage(listAuthError);

    var (options, isWs) = CreateClientOptions(host, portPath);
    var transport = isWs ? "WebSocket" : "TCP";

    Console.WriteLine($"Connecting to relay at {host}:{portPath} via {transport}...");

    await using var mux = StreamMultiplexer.Create(options);
    mux.Start();
    await mux.WaitForReadyAsync(ct);

    // Open control channel
    var ctrlSend = mux.OpenChannel("ctrl>>");

    IReadChannel? ctrlRecv = null;
    using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    acceptCts.CancelAfter(TimeSpan.FromSeconds(5));

    await foreach (var ch in mux.AcceptChannelsAsync(ct: acceptCts.Token))
    {
        if (ch.ChannelId == "ctrl<<")
        {
            ctrlRecv = ch;
            break;
        }
    }

    if (ctrlRecv == null)
    {
        Console.WriteLine("Failed to establish control channel");
        return 1;
    }

    var transit = new MessageTransit<TunnelMessage, TunnelMessage>(
        ctrlSend, ctrlRecv,
        TunnelJsonContext.Default.TunnelMessage,
        TunnelJsonContext.Default.TunnelMessage);

    // Auth first so an auth-gated relay permits the listing.
    if (listAuthToken != null)
        await transit.SendAsync(new Authenticate(listAuthToken), ct);
    await transit.SendAsync(new ListRequest(), ct);
    var response = await transit.ReceiveAsync(ct);

    if (response is ServiceList list)
    {
        Console.WriteLine();
        if (list.Services.Length == 0)
        {
            Console.WriteLine("No services registered.");
        }
        else
        {
            Console.WriteLine("Available services:");
            Console.WriteLine("─────────────────────────────────────────────────────");
            foreach (var svc in list.Services)
            {
                Console.WriteLine($"  {svc.Name,-20} ({svc.DeviceId,-15}) → :{svc.Port}");
            }
            Console.WriteLine("─────────────────────────────────────────────────────");
            Console.WriteLine($"Total: {list.Services.Length} service(s)");
        }
    }
    else
    {
        Console.WriteLine($"Unexpected response: {response}");
        return 1;
    }

    return 0;
}

// ═══════════════════════════════════════════════════════════════
//   Helpers
// ═══════════════════════════════════════════════════════════════

// Fail-closed relay auth: requires --auth/--auth-env or explicit
// --allow-no-auth. Never logs the token itself.
static bool TryResolveRelayAuth(string[] rest, out string? token, out string? error)
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

    if (token == null && !allowNoAuth)
    {
        error = "No auth configured. Pass --auth <token>, --auth-env NAME, or --allow-no-auth (insecure, local demos only).";
        return false;
    }

    return true;
}

// Clients pass a token through when given; no fail-closed requirement.
static bool TryResolveClientAuth(string[] rest, out string? token, out string? error)
{
    token = null;
    error = null;

    var authIdx = Array.IndexOf(rest, "--auth");
    var envIdx = Array.IndexOf(rest, "--auth-env");

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

    if (authIdx >= 0)
    {
        if (authIdx + 1 >= rest.Length)
        {
            error = "Missing value for --auth.";
            return false;
        }
        token = rest[authIdx + 1];
        if (string.IsNullOrWhiteSpace(token))
        {
            token = null;
            error = "--auth value must not be empty.";
            return false;
        }
    }

    if (envIdx >= 0)
    {
        if (envIdx + 1 >= rest.Length)
        {
            error = "Missing value for --auth-env.";
            return false;
        }
        if (token != null)
        {
            error = "Specify only one of --auth or --auth-env.";
            return false;
        }
        token = Environment.GetEnvironmentVariable(rest[envIdx + 1]);
        if (string.IsNullOrEmpty(token))
        {
            token = null;
            error = $"Environment variable '{rest[envIdx + 1]}' is not set or empty.";
            return false;
        }
    }

    return true;
}

// Constant-time token comparison over content so auth failures don't leak
// prefix length via timing. Lengths still differ observably (short-circuits
// on length mismatch); only equal-length content compares in constant time.
static bool FixedTimeEquals(string a, string b)
{
    var ab = Encoding.UTF8.GetBytes(a);
    var bb = Encoding.UTF8.GetBytes(b);
    return CryptographicOperations.FixedTimeEquals(ab, bb);
}

static (int port, string path) ParseWsPortPath(string portPath)
{
    var parts = portPath.Split('/', 2);
    var port = int.Parse(parts[0]);
    var path = parts.Length > 1 ? "/" + parts[1] : "/ws";
    return (port, path);
}

static (MultiplexerOptions options, bool isWebSocket) CreateClientOptions(string host, string portPath)
{
    if (portPath.Contains('/'))
    {
        var (port, path) = ParseWsPortPath(portPath);
        var uri = $"ws://{host}:{port}{path}";
        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var ws = new System.Net.WebSockets.ClientWebSocket();
                await ws.ConnectAsync(new Uri(uri), ct);
                return new WebSocketStreamPair(ws);
            }
        };
        return (options, true);
    }
    else
    {
        var port = int.Parse(portPath);
        var options = new MultiplexerOptions
        {
            StreamFactory = async ct =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(host, port, ct);
                return new StreamPair(client.GetStream(), client);
            }
        };
        return (options, false);
    }
}

// ═══════════════════════════════════════════════════════════════
//   Types
// ═══════════════════════════════════════════════════════════════

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Authenticate), "auth")]
[JsonDerivedType(typeof(RegisterService), "register")]
[JsonDerivedType(typeof(RegisterAck), "register-ack")]
[JsonDerivedType(typeof(TunnelRequest), "tunnel-req")]
[JsonDerivedType(typeof(TunnelAccept), "tunnel-accept")]
[JsonDerivedType(typeof(TunnelReject), "tunnel-reject")]
[JsonDerivedType(typeof(ListRequest), "list-req")]
[JsonDerivedType(typeof(ServiceList), "list")]
public abstract record TunnelMessage;

public record Authenticate(string? Token) : TunnelMessage;
public record RegisterService(string Name, string DeviceId, int LocalPort) : TunnelMessage;
public record RegisterAck(bool Success, string? Error) : TunnelMessage;
public record TunnelRequest(string ServiceName, string TunnelId) : TunnelMessage;
public record TunnelAccept(string TunnelId) : TunnelMessage;
public record TunnelReject(string TunnelId, string Reason) : TunnelMessage;
public record ListRequest() : TunnelMessage;
public record ServiceList(ServiceInfo[] Services) : TunnelMessage;
public record ServiceInfo(string Name, string DeviceId, int Port);

record AgentConnection(IStreamMultiplexer Mux, MessageTransit<TunnelMessage, TunnelMessage> Transit, string DeviceId, int LocalPort);
record ForwardConnection(IStreamMultiplexer Mux, MessageTransit<TunnelMessage, TunnelMessage> Transit);

[JsonSerializable(typeof(TunnelMessage))]
[JsonSerializable(typeof(Authenticate))]
[JsonSerializable(typeof(RegisterService))]
[JsonSerializable(typeof(RegisterAck))]
[JsonSerializable(typeof(TunnelRequest))]
[JsonSerializable(typeof(TunnelAccept))]
[JsonSerializable(typeof(TunnelReject))]
[JsonSerializable(typeof(ListRequest))]
[JsonSerializable(typeof(ServiceList))]
[JsonSerializable(typeof(ServiceInfo))]
[JsonSerializable(typeof(ServiceInfo[]))]
internal partial class TunnelJsonContext : JsonSerializerContext { }

// WebSocket stream wrapper
sealed class WebSocketStreamPair(System.Net.WebSockets.WebSocket webSocket) : IStreamPair
{
    public Stream ReadStream { get; } = new WebSocketStream(webSocket);
    public Stream WriteStream { get; } = new WebSocketStream(webSocket);
    
    public async ValueTask DisposeAsync()
    {
        if (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            try
            {
                await webSocket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch { }
        }
        webSocket.Dispose();
    }
}

sealed class WebSocketStream(System.Net.WebSockets.WebSocket webSocket) : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, count), ct);
        return result.Count;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var result = await webSocket.ReceiveAsync(buffer, ct);
        return result.Count;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await webSocket.SendAsync(new ArraySegment<byte>(buffer, offset, count),
            System.Net.WebSockets.WebSocketMessageType.Binary, true, ct);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await webSocket.SendAsync(buffer, System.Net.WebSockets.WebSocketMessageType.Binary, true, ct);
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

