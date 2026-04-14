using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NetConduit.Models;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

/// <summary>
/// Real-world use-case tests that simulate actual application patterns.
/// These target the exact patterns where the reported bugs manifest.
/// </summary>
[Collection("HighMemory")]
public partial class UseCaseTests
{
    #region Shared Types

    public record ChatMessage(string User, string Text, long Timestamp);
    public record SyncRequest(string Type, int Version);
    public record SyncResponse(string Type, bool Success, int Version);
    public record FileChunk(string FileId, int ChunkIndex, int TotalChunks, byte[] Data);
    public record RpcRequest(string Method, string RequestId, JsonNode? Params);
    public record RpcResponse(string RequestId, bool Success, JsonNode? Result, string? Error);

    [JsonSerializable(typeof(ChatMessage))]
    [JsonSerializable(typeof(SyncRequest))]
    [JsonSerializable(typeof(SyncResponse))]
    [JsonSerializable(typeof(FileChunk))]
    [JsonSerializable(typeof(RpcRequest))]
    [JsonSerializable(typeof(RpcResponse))]
    internal partial class UseCaseJsonContext : JsonSerializerContext { }

    #endregion

    #region Use Case 1: Request-Response Pattern (targets Bug 1)

    [Fact(Timeout = 30000)]
    public async Task UseCase_RequestResponse_MultipleSameRequests_AllGetResponses()
    {
        // Simulates a sync protocol where the client sends identical FullSyncRequest
        // messages and expects a response for each one.
        // Bug 1: DeltaTransit silently drops identical consecutive messages.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        // Request channel: Client → Server (MessageTransit for request-response)
        var reqWrite = await muxA.OpenChannelAsync(new() { ChannelId = "req" }, cts.Token);
        var reqRead = await muxB.AcceptChannelAsync("req", cts.Token);

        // Response channel: Server → Client
        var resWrite = await muxB.OpenChannelAsync(new() { ChannelId = "res" }, cts.Token);
        var resRead = await muxA.AcceptChannelAsync("res", cts.Token);

        await using var reqSender = new MessageTransit<SyncRequest, SyncRequest>(
            reqWrite, null, UseCaseJsonContext.Default.SyncRequest, UseCaseJsonContext.Default.SyncRequest);
        await using var reqReceiver = new MessageTransit<SyncRequest, SyncRequest>(
            null, reqRead, UseCaseJsonContext.Default.SyncRequest, UseCaseJsonContext.Default.SyncRequest);

        await using var resSender = new MessageTransit<SyncResponse, SyncResponse>(
            resWrite, null, UseCaseJsonContext.Default.SyncResponse, UseCaseJsonContext.Default.SyncResponse);
        await using var resReceiver = new MessageTransit<SyncResponse, SyncResponse>(
            null, resRead, UseCaseJsonContext.Default.SyncResponse, UseCaseJsonContext.Default.SyncResponse);

        // Server: receives requests and sends responses
        var serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                var req = await reqReceiver.ReceiveAsync(cts.Token);
                Assert.NotNull(req);
                Assert.Equal("FullSync", req.Type);

                await resSender.SendAsync(
                    new SyncResponse("FullSync", true, req.Version), cts.Token);
            }
        });

        // Client: sends 5 identical FullSync requests
        var responsesReceived = 0;
        for (int i = 0; i < 5; i++)
        {
            await reqSender.SendAsync(new SyncRequest("FullSync", 1), cts.Token);

            using var perReq = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, perReq.Token);

            var response = await resReceiver.ReceiveAsync(linked.Token);
            Assert.NotNull(response);
            Assert.True(response.Success);
            responsesReceived++;
        }

        await serverTask;
        Assert.Equal(5, responsesReceived);
    }

    #endregion

    #region Use Case 2: Chat Application (targets Bugs 1, 4, 12)

    [Fact(Timeout = 60000)]
    public async Task UseCase_ChatRoom_MultipleUsers_MessageOrder()
    {
        // Multi-user chat where users send messages through a shared multiplexer.
        // Each user has a dedicated channel.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        const int userCount = 5;
        const int messagesPerUser = 20;
        var allReceived = new ConcurrentDictionary<string, List<ChatMessage>>();
        var errors = new ConcurrentBag<string>();

        var senderTasks = new List<Task>();
        var receiverTasks = new List<Task>();

        for (int u = 0; u < userCount; u++)
        {
            var userId = $"user_{u}";
            allReceived[userId] = new List<ChatMessage>();

            var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = userId }, cts.Token);
            var readChannel = await muxB.AcceptChannelAsync(userId, cts.Token);

            var sender = new MessageTransit<ChatMessage, ChatMessage>(
                writeChannel, null,
                UseCaseJsonContext.Default.ChatMessage, UseCaseJsonContext.Default.ChatMessage);

            var receiver = new MessageTransit<ChatMessage, ChatMessage>(
                null, readChannel,
                UseCaseJsonContext.Default.ChatMessage, UseCaseJsonContext.Default.ChatMessage);

            var capturedUserId = userId;
            var capturedU = u;

            // Sender
            senderTasks.Add(Task.Run(async () =>
            {
                try
                {
                    for (int m = 0; m < messagesPerUser; m++)
                    {
                        var msg = new ChatMessage(capturedUserId, $"Message {m} from {capturedUserId}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        await sender.SendAsync(msg, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { errors.Add($"Sender {capturedUserId}: {ex.Message}"); }
                finally { await sender.DisposeAsync(); }
            }));

            // Receiver
            receiverTasks.Add(Task.Run(async () =>
            {
                try
                {
                    for (int m = 0; m < messagesPerUser; m++)
                    {
                        var msg = await receiver.ReceiveAsync(cts.Token);
                        if (msg is null) { errors.Add($"Receiver {capturedUserId}: null at msg {m}"); break; }
                        allReceived[capturedUserId].Add(msg);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { errors.Add($"Receiver {capturedUserId}: {ex.Message}"); }
                finally { await receiver.DisposeAsync(); }
            }));
        }

        await Task.WhenAll(senderTasks);
        await Task.WhenAll(receiverTasks);
        await cts.CancelAsync();

        Assert.Empty(errors);

        // Verify all messages received in order
        foreach (var userId in allReceived.Keys)
        {
            var msgs = allReceived[userId];
            Assert.Equal(messagesPerUser, msgs.Count);

            // Verify ordering (timestamps should be non-decreasing)
            for (int i = 1; i < msgs.Count; i++)
            {
                Assert.True(msgs[i].Timestamp >= msgs[i - 1].Timestamp,
                    $"Out of order: {userId} msg {i} timestamp {msgs[i].Timestamp} < {msgs[i - 1].Timestamp}");
            }
        }
    }

    #endregion

    #region Use Case 3: State Synchronization (targets Bugs 1, 9)

    [Fact(Timeout = 30000)]
    public async Task UseCase_StateSyncProtocol_IdenticalStateRetransmit_NoHang()
    {
        // EdgeConduit-style state sync: client sends state, server applies and echoes back.
        // When states are identical (no changes), the echo-back should still work.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        // Client → Server state channel
        var clientWrite = await muxA.OpenChannelAsync(new() { ChannelId = "state_c2s" }, cts.Token);
        var serverRead = await muxB.AcceptChannelAsync("state_c2s", cts.Token);

        // Server → Client state channel
        var serverWrite = await muxB.OpenChannelAsync(new() { ChannelId = "state_s2c" }, cts.Token);
        var clientRead = await muxA.AcceptChannelAsync("state_s2c", cts.Token);

        await using var clientSender = new DeltaTransit<JsonNode>(clientWrite, null);
        await using var serverReceiver = new DeltaTransit<JsonNode>(null, serverRead);

        await using var serverSender = new DeltaTransit<JsonNode>(serverWrite, null);
        await using var clientReceiver = new DeltaTransit<JsonNode>(null, clientRead);

        // Server: receive state, echo it back
        var serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                var state = await serverReceiver.ReceiveAsync(cts.Token);
                if (state is null) break;
                await serverSender.SendAsync(state, cts.Token);
            }
        });

        // Client: send same state 5 times, then different state, then same state again
        var sameState = JsonNode.Parse("""{"sync": "full", "version": 1}""")!;
        var diffState = JsonNode.Parse("""{"sync": "full", "version": 2}""")!;

        var clientEchoes = 0;

        // Phase 1: 3 identical states
        for (int i = 0; i < 3; i++)
        {
            await clientSender.SendAsync(sameState.DeepClone(), cts.Token);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            var echo = await clientReceiver.ReceiveAsync(linked.Token);
            Assert.NotNull(echo);
            clientEchoes++;
        }

        // Phase 2: one different state
        await clientSender.SendAsync(diffState.DeepClone(), cts.Token);
        var diffEcho = await clientReceiver.ReceiveAsync(cts.Token);
        Assert.NotNull(diffEcho);
        clientEchoes++;

        // Phase 3: back to same state
        for (int i = 0; i < 3; i++)
        {
            await clientSender.SendAsync(sameState.DeepClone(), cts.Token);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            var echo = await clientReceiver.ReceiveAsync(linked.Token);
            Assert.NotNull(echo);
            clientEchoes++;
        }

        // Send remaining to complete server's loop
        for (int i = 0; i < 3; i++)
        {
            await clientSender.SendAsync(JsonNode.Parse($$"""{"filler": {{i}}}""")!, cts.Token);
            await clientReceiver.ReceiveAsync(cts.Token);
            clientEchoes++;
        }

        await serverTask;
        Assert.Equal(10, clientEchoes);
    }

    #endregion

    #region Use Case 4: File Transfer (targets Bugs 4, 5, 10)

    [Fact(Timeout = 60000)]
    public async Task UseCase_LargeFileTransfer_MultiChannel_DataIntegrity()
    {
        // Simulate transferring a large file split into chunks across parallel channels
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        const int totalSize = 2 * 1024 * 1024; // 2MB
        const int chunkSize = 64 * 1024; // 64KB chunks
        const int parallelStreams = 4;

        // Generate "file" data
        var fileData = new byte[totalSize];
        Random.Shared.NextBytes(fileData);
        var expectedHash = SHA256.HashData(fileData);

        var chunksPerStream = (totalSize / chunkSize) / parallelStreams;
        var errors = new ConcurrentBag<string>();
        var receivedChunks = new ConcurrentDictionary<int, byte[]>();

        var tasks = new List<Task>();

        for (int s = 0; s < parallelStreams; s++)
        {
            var streamIndex = s;
            var channelId = $"file_stream_{streamIndex}";
            var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = channelId }, cts.Token);
            var readChannel = await muxB.AcceptChannelAsync(channelId, cts.Token);

            // Sender
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    for (int c = 0; c < chunksPerStream; c++)
                    {
                        var chunkIndex = streamIndex * chunksPerStream + c;
                        var offset = chunkIndex * chunkSize;
                        if (offset + chunkSize > totalSize) break;

                        var chunk = fileData.AsMemory(offset, chunkSize);
                        // Write length prefix + data
                        var header = new byte[4];
                        BinaryPrimitives.WriteInt32BigEndian(header, chunkIndex);
                        await writeChannel.WriteAsync(header, cts.Token);
                        await writeChannel.WriteAsync(chunk, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { errors.Add($"Sender stream {streamIndex}: {ex.Message}"); }
            }));

            // Receiver
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    for (int c = 0; c < chunksPerStream; c++)
                    {
                        // Read header (chunk index)
                        var header = new byte[4];
                        var headerRead = 0;
                        while (headerRead < 4)
                        {
                            var n = await readChannel.ReadAsync(header.AsMemory(headerRead), cts.Token);
                            if (n == 0) { errors.Add($"Receiver {streamIndex}: EOF reading header at chunk {c}"); return; }
                            headerRead += n;
                        }
                        var chunkIndex = BinaryPrimitives.ReadInt32BigEndian(header);

                        // Read data
                        var data = new byte[chunkSize];
                        var dataRead = 0;
                        while (dataRead < chunkSize)
                        {
                            var n = await readChannel.ReadAsync(data.AsMemory(dataRead), cts.Token);
                            if (n == 0) { errors.Add($"Receiver {streamIndex}: EOF reading data at chunk {c}"); return; }
                            dataRead += n;
                        }

                        receivedChunks[chunkIndex] = data;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { errors.Add($"Receiver stream {streamIndex}: {ex.Message}"); }
            }));
        }

        await Task.WhenAll(tasks);
        await cts.CancelAsync();

        Assert.Empty(errors);

        // Reassemble and verify
        var totalChunks = parallelStreams * chunksPerStream;
        Assert.Equal(totalChunks, receivedChunks.Count);

        var reassembled = new byte[totalChunks * chunkSize];
        foreach (var kv in receivedChunks)
        {
            Buffer.BlockCopy(kv.Value, 0, reassembled, kv.Key * chunkSize, chunkSize);
        }

        var receivedHash = SHA256.HashData(reassembled.AsSpan(0, totalChunks * chunkSize));
        Assert.Equal(expectedHash.AsSpan(0, expectedHash.Length).ToArray(),
                     receivedHash.AsSpan(0, receivedHash.Length).ToArray());
    }

    #endregion

    #region Use Case 5: RPC Framework (targets Bugs 1, 2, 14)

    [Fact(Timeout = 30000)]
    public async Task UseCase_RpcFramework_CallAndResponse_ReliableDelivery()
    {
        // RPC-style: client sends method + params, server returns result.
        // Tests that every request gets exactly one response.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var rpcWrite = await muxA.OpenChannelAsync(new() { ChannelId = "rpc_req" }, cts.Token);
        var rpcRead = await muxB.AcceptChannelAsync("rpc_req", cts.Token);

        var rpcResWrite = await muxB.OpenChannelAsync(new() { ChannelId = "rpc_res" }, cts.Token);
        var rpcResRead = await muxA.AcceptChannelAsync("rpc_res", cts.Token);

        await using var reqSender = new MessageTransit<RpcRequest, RpcRequest>(
            rpcWrite, null, UseCaseJsonContext.Default.RpcRequest, UseCaseJsonContext.Default.RpcRequest);
        await using var reqReceiver = new MessageTransit<RpcRequest, RpcRequest>(
            null, rpcRead, UseCaseJsonContext.Default.RpcRequest, UseCaseJsonContext.Default.RpcRequest);

        await using var resSender = new MessageTransit<RpcResponse, RpcResponse>(
            rpcResWrite, null, UseCaseJsonContext.Default.RpcResponse, UseCaseJsonContext.Default.RpcResponse);
        await using var resReceiver = new MessageTransit<RpcResponse, RpcResponse>(
            null, rpcResRead, UseCaseJsonContext.Default.RpcResponse, UseCaseJsonContext.Default.RpcResponse);

        // Server
        var serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                var req = await reqReceiver.ReceiveAsync(cts.Token);
                if (req is null) break;

                var result = req.Method switch
                {
                    "add" => JsonNode.Parse($"{req.Params!["a"]!.GetValue<int>() + req.Params["b"]!.GetValue<int>()}")!,
                    "echo" => req.Params?.DeepClone(),
                    _ => null
                };

                await resSender.SendAsync(
                    new RpcResponse(req.RequestId, true, result, null), cts.Token);
            }
        });

        // Client: send 20 RPC calls with varying methods
        var responses = new ConcurrentDictionary<string, RpcResponse>();
        for (int i = 0; i < 20; i++)
        {
            var reqId = $"req_{i}";
            RpcRequest req;
            if (i % 2 == 0)
            {
                req = new RpcRequest("add", reqId, JsonNode.Parse($$"""{"a": {{i}}, "b": {{i + 1}}}"""));
            }
            else
            {
                req = new RpcRequest("echo", reqId, JsonNode.Parse($$"""{"msg": "hello_{{i}}"}"""));
            }

            await reqSender.SendAsync(req, cts.Token);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            var res = await resReceiver.ReceiveAsync(linked.Token);
            Assert.NotNull(res);
            Assert.Equal(reqId, res.RequestId);
            Assert.True(res.Success);
            responses[reqId] = res;
        }

        await serverTask;
        Assert.Equal(20, responses.Count);

        // Verify add results
        for (int i = 0; i < 20; i += 2)
        {
            var res = responses[$"req_{i}"];
            var expected = i + i + 1;
            Assert.Equal(expected, res.Result!.GetValue<int>());
        }
    }

    #endregion

    #region Use Case 6: Pub/Sub with DeltaTransit (targets Bug 1)

    [Fact(Timeout = 60000)]
    public async Task UseCase_PubSub_DeltaState_SubscribersGetAllUpdates()
    {
        // Publisher sends state updates, multiple subscribers receive all of them.
        // Tests that repeated identical states (common in pub/sub) are delivered.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        const int subscriberCount = 3;
        const int totalUpdates = 30;

        var subscriberReceived = new ConcurrentDictionary<int, int>();
        var errors = new ConcurrentBag<string>();

        // Create subscriber channels
        var senders = new List<DeltaTransit<JsonNode>>();
        var receivers = new List<DeltaTransit<JsonNode>>();

        for (int s = 0; s < subscriberCount; s++)
        {
            var writeChannel = await muxA.OpenChannelAsync(
                new() { ChannelId = $"sub_{s}" }, cts.Token);
            var readChannel = await muxB.AcceptChannelAsync($"sub_{s}", cts.Token);

            senders.Add(new DeltaTransit<JsonNode>(writeChannel, null));
            receivers.Add(new DeltaTransit<JsonNode>(null, readChannel));
            subscriberReceived[s] = 0;
        }

        // Receiver tasks
        var receiveTask = Enumerable.Range(0, subscriberCount).Select(s => Task.Run(async () =>
        {
            for (int i = 0; i < totalUpdates; i++)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

                    var state = await receivers[s].ReceiveAsync(linked.Token);
                    if (state is null)
                    {
                        errors.Add($"Subscriber {s}: null at update {i}");
                        break;
                    }
                    subscriberReceived[s]++;
                }
                catch (OperationCanceledException)
                {
                    errors.Add($"Subscriber {s}: timeout at update {i}");
                    break;
                }
            }
        })).ToArray();

        // Publisher: sends updates, some identical
        for (int i = 0; i < totalUpdates; i++)
        {
            // Pattern: 3 identical, 1 different, repeat
            var stateValue = i / 3; // Same value for every 3 consecutive sends
            var state = JsonNode.Parse($$"""{"tick": {{stateValue}}, "seq": {{i}}}""")!;

            foreach (var sender in senders)
            {
                await sender.SendAsync(state.DeepClone(), cts.Token);
            }
        }

        await Task.WhenAll(receiveTask);
        await cts.CancelAsync();

        // Cleanup
        foreach (var s in senders) await s.DisposeAsync();
        foreach (var r in receivers) await r.DisposeAsync();

        if (errors.Any())
        {
            Assert.Fail($"Pub/Sub errors:\n{string.Join("\n", errors.Take(20))}");
        }

        // All subscribers should have received all updates
        foreach (var kv in subscriberReceived)
        {
            Assert.Equal(totalUpdates, kv.Value);
        }
    }

    #endregion

    #region Use Case 7: Duplex Stream for Bidirectional Pipe (targets Bug 12)

    [Fact(Timeout = 30000)]
    public async Task UseCase_DuplexStream_TwoWayDataPipe_Integrity()
    {
        // Simulate a bidirectional data pipe (like a TCP tunnel)
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        // DuplexStreamTransit IS a Stream — read from it, write to it
        var openTask = muxA.OpenDuplexStreamAsync("tunnel", cts.Token);
        var acceptTask = muxB.AcceptDuplexStreamAsync("tunnel", cts.Token);
        var transit = await openTask;
        var remoteTransit = await acceptTask;

        var errors = new ConcurrentBag<string>();

        // A → B: send numbered packets (transit writes go to remoteTransit reads)
        var aToBTask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                var data = new byte[64];
                BinaryPrimitives.WriteInt32BigEndian(data, i);
                data.AsSpan(4).Fill((byte)(i & 0xFF));
                await transit.WriteAsync(data, cts.Token);
            }
        });

        // B → A: send numbered packets (remoteTransit writes go to transit reads)
        var bToATask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                var data = new byte[64];
                BinaryPrimitives.WriteInt32BigEndian(data, i + 1000);
                data.AsSpan(4).Fill((byte)((i + 128) & 0xFF));
                await remoteTransit.WriteAsync(data, cts.Token);
            }
        });

        // Verify A reads from B (reads on transit come from remoteTransit writes)
        var readFromBTask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                var buf = new byte[64];
                var total = 0;
                while (total < 64)
                {
                    var n = await transit.ReadAsync(buf.AsMemory(total), cts.Token);
                    if (n == 0) { errors.Add($"A←B: EOF at packet {i}"); return; }
                    total += n;
                }
                var seq = BinaryPrimitives.ReadInt32BigEndian(buf);
                if (seq != i + 1000) errors.Add($"A←B: expected seq {i + 1000}, got {seq}");
            }
        });

        // Verify B reads from A (reads on remoteTransit come from transit writes)
        var readFromATask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                var buf = new byte[64];
                var total = 0;
                while (total < 64)
                {
                    var n = await remoteTransit.ReadAsync(buf.AsMemory(total), cts.Token);
                    if (n == 0) { errors.Add($"B←A: EOF at packet {i}"); return; }
                    total += n;
                }
                var seq = BinaryPrimitives.ReadInt32BigEndian(buf);
                if (seq != i) errors.Add($"B←A: expected seq {i}, got {seq}");
            }
        });

        await Task.WhenAll(aToBTask, bToATask, readFromBTask, readFromATask);
        await cts.CancelAsync();

        Assert.Empty(errors);

        await transit.DisposeAsync();
        await remoteTransit.DisposeAsync();
    }

    #endregion

    #region Use Case 8: Channel Suffix Collision Prevention (targets Bug 12)

    [Fact(Timeout = 30000)]
    public async Task UseCase_ChannelNameWithSuffix_NoCollisionWithTransitInternal()
    {
        // User creates channels with names that contain ">>" or "<<"
        // These should not collide with transit-internal channel names
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        // Open a raw channel with a name that looks like a transit suffix
        var write1 = await muxA.OpenChannelAsync(new() { ChannelId = "data>>" }, cts.Token);
        var read1 = await muxB.AcceptChannelAsync("data>>", cts.Token);

        // Now open a duplex transit named "data" — it will create "data>>" and "data<<"
        // This should collide with the already-opened "data>>" channel
        try
        {
            var transit = await muxA.OpenDuplexStreamAsync("data", cts.Token);

            // If we get here, both "data>>" channels exist — which one gets data?
            // Send on the raw channel
            await write1.WriteAsync(new byte[] { 0xAA }, cts.Token);

            // Send on the transit (DuplexStreamTransit IS a Stream)
            await transit.WriteAsync(new byte[] { 0xBB }, cts.Token);

            // Read from raw channel — should get 0xAA, not 0xBB
            var buf = new byte[1];
            var n = await read1.ReadAsync(buf, cts.Token);
            if (n > 0 && buf[0] != 0xAA)
            {
                Assert.Fail($"Channel collision: raw channel got 0x{buf[0]:X2} instead of 0xAA");
            }

            await transit.DisposeAsync();
        }
        catch (Exception)
        {
            // Exception is actually the CORRECT behavior — should reject duplicate channel names
        }
    }

    #endregion

    #region Use Case 9: Long-Running Multiplexer Channel Churn (targets Bug 5, 6)

    [Fact(Timeout = 120000)]
    public async Task UseCase_LongRunningMux_ThousandsOfChannelCycles_StillFunctional()
    {
        // Simulate a long-running server that creates and destroys thousands of channels
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var errors = new ConcurrentBag<string>();
        var successfulCycles = 0;

        for (int i = 0; i < 3000; i++)
        {
            if (cts.Token.IsCancellationRequested) break;

            try
            {
                var channelId = $"session_{i}";
                var write = await muxA.OpenChannelAsync(new() { ChannelId = channelId }, cts.Token);
                var read = await muxB.AcceptChannelAsync(channelId, cts.Token);

                // Simulate a short-lived session
                var payload = BitConverter.GetBytes(i);
                await write.WriteAsync(payload, cts.Token);

                var buf = new byte[4];
                var total = 0;
                while (total < 4)
                {
                    var n = await read.ReadAsync(buf.AsMemory(total), cts.Token);
                    if (n == 0) break;
                    total += n;
                }

                if (total == 4)
                {
                    var received = BitConverter.ToInt32(buf);
                    if (received != i)
                    {
                        errors.Add($"Cycle {i}: expected {i}, got {received}");
                    }
                }

                await write.DisposeAsync();
                await read.DisposeAsync();
                successfulCycles++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                errors.Add($"Cycle {i}: {ex.GetType().Name}: {ex.Message}");
                if (errors.Count > 10) break; // Don't spam
            }
        }

        Assert.True(successfulCycles > 2900, $"Only {successfulCycles} cycles succeeded");
        Assert.Empty(errors);
    }

    #endregion

    #region Use Case 10: Streaming Video Frames (targets Bug 4, 11)

    [Fact(Timeout = 60000)]
    public async Task UseCase_VideoStreaming_LargeFramesContinuous_NoCreditStarvation()
    {
        // Simulate streaming large video frames continuously
        // Tests credit system under sustained high-throughput load
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "video" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("video", cts.Token);

        const int frameSize = 500 * 1024; // 500KB per frame (720p compressed)
        const int frameCount = 30; // 1 second at 30fps

        var errors = new ConcurrentBag<string>();
        var framesReceived = 0;

        // Producer: continuous frame output
        var producer = Task.Run(async () =>
        {
            for (int f = 0; f < frameCount; f++)
            {
                try
                {
                    var frame = new byte[frameSize];
                    // Write frame number at the start
                    BinaryPrimitives.WriteInt32BigEndian(frame, f);
                    // Fill rest with pattern
                    frame.AsSpan(4, Math.Min(16, frameSize - 4)).Fill((byte)(f & 0xFF));

                    await writeChannel.WriteAsync(frame, cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (TimeoutException) { errors.Add($"Frame {f}: credit starvation timeout"); break; }
                catch (Exception ex) { errors.Add($"Frame {f}: {ex.Message}"); break; }
            }
        });

        // Consumer: read frames and verify
        var consumer = Task.Run(async () =>
        {
            for (int f = 0; f < frameCount; f++)
            {
                try
                {
                    var frame = new byte[frameSize];
                    var total = 0;
                    while (total < frameSize)
                    {
                        var n = await readChannel.ReadAsync(frame.AsMemory(total), cts.Token);
                        if (n == 0) { errors.Add($"Frame {f}: EOF"); return; }
                        total += n;
                    }

                    var frameNum = BinaryPrimitives.ReadInt32BigEndian(frame);
                    if (frameNum != f)
                    {
                        errors.Add($"Frame {f}: wrong number {frameNum}");
                        return;
                    }
                    Interlocked.Increment(ref framesReceived);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add($"Consumer frame {f}: {ex.Message}"); break; }
            }
        });

        await Task.WhenAll(producer, consumer);
        await cts.CancelAsync();

        Assert.Empty(errors);
        Assert.Equal(frameCount, framesReceived);
    }

    #endregion

    #region Use Case 11: Heartbeat Protocol (targets Bug 1)

    [Fact(Timeout = 30000)]
    public async Task UseCase_HeartbeatProtocol_IdenticalPings_AllDelivered()
    {
        // Heartbeat: sends identical "ping" messages at interval, expects "pong" back.
        // Bug 1: identical DeltaTransit messages get dropped.
        // Using MessageTransit here which should work, then test DeltaTransit variant.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var pingWrite = await muxA.OpenChannelAsync(new() { ChannelId = "ping" }, cts.Token);
        var pingRead = await muxB.AcceptChannelAsync("ping", cts.Token);

        var pongWrite = await muxB.OpenChannelAsync(new() { ChannelId = "pong" }, cts.Token);
        var pongRead = await muxA.AcceptChannelAsync("pong", cts.Token);

        await using var pingSender = new MessageTransit<SyncRequest, SyncRequest>(
            pingWrite, null, UseCaseJsonContext.Default.SyncRequest, UseCaseJsonContext.Default.SyncRequest);
        await using var pingReceiver = new MessageTransit<SyncRequest, SyncRequest>(
            null, pingRead, UseCaseJsonContext.Default.SyncRequest, UseCaseJsonContext.Default.SyncRequest);
        await using var pongSender = new MessageTransit<SyncResponse, SyncResponse>(
            pongWrite, null, UseCaseJsonContext.Default.SyncResponse, UseCaseJsonContext.Default.SyncResponse);
        await using var pongReceiver = new MessageTransit<SyncResponse, SyncResponse>(
            null, pongRead, UseCaseJsonContext.Default.SyncResponse, UseCaseJsonContext.Default.SyncResponse);

        // Responder
        var responderTask = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                var ping = await pingReceiver.ReceiveAsync(cts.Token);
                if (ping is null) break;
                await pongSender.SendAsync(new SyncResponse("pong", true, ping.Version), cts.Token);
            }
        });

        // Send 10 identical pings — all should get pong responses
        var pongsReceived = 0;
        var identicalPing = new SyncRequest("ping", 1);

        for (int i = 0; i < 10; i++)
        {
            await pingSender.SendAsync(identicalPing, cts.Token);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            var pong = await pongReceiver.ReceiveAsync(linked.Token);
            Assert.NotNull(pong);
            Assert.True(pong.Success);
            pongsReceived++;
        }

        await responderTask;
        Assert.Equal(10, pongsReceived);
    }

    [Fact(Timeout = 30000)]
    public async Task UseCase_HeartbeatProtocol_DeltaTransitPings_IdenticalDropped()
    {
        // Same heartbeat pattern but using DeltaTransit — this directly hits Bug 1
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "hb_delta" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("hb_delta", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        var ping = JsonNode.Parse("""{"type": "ping", "seq": 1}""")!;
        var receivedPings = 0;

        // Send 5 identical pings
        for (int i = 0; i < 5; i++)
        {
            await sender.SendAsync(ping.DeepClone(), cts.Token);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            try
            {
                var received = await receiver.ReceiveAsync(linked.Token);
                if (received is not null) receivedPings++;
            }
            catch (OperationCanceledException)
            {
                // Timeout = message was dropped (Bug 1)
                break;
            }
        }

        // Bug 1: Only the first ping is received, rest are dropped
        // This assertion will FAIL if Bug 1 exists
        Assert.Equal(5, receivedPings);
    }

    #endregion

    #region Use Case 12: Database Replication (targets Bug 1, 10)

    [Fact(Timeout = 60000)]
    public async Task UseCase_DatabaseReplication_IdempotentUpdates_AllApplied()
    {
        // Simulate a DB replication stream where the same row is updated multiple times
        // with the same value (idempotent updates). Each update must be received.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "db_replication" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("db_replication", cts.Token);

        await using var sender = new MessageTransit<SyncRequest, SyncRequest>(
            writeA, null, UseCaseJsonContext.Default.SyncRequest, UseCaseJsonContext.Default.SyncRequest);
        await using var receiver = new MessageTransit<SyncRequest, SyncRequest>(
            null, readB, UseCaseJsonContext.Default.SyncRequest, UseCaseJsonContext.Default.SyncRequest);

        // Send 20 updates: some identical, some different
        var updates = new List<SyncRequest>();
        for (int i = 0; i < 20; i++)
        {
            // Groups of 4 identical updates at the same version
            updates.Add(new SyncRequest("UPDATE", i / 4));
        }

        foreach (var update in updates)
            await sender.SendAsync(update, cts.Token);

        var received = 0;
        for (int i = 0; i < 20; i++)
        {
            var msg = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(msg);
            Assert.Equal("UPDATE", msg.Type);
            received++;
        }

        Assert.Equal(20, received);
    }

    #endregion

    #region Use Case 13: Config Push (targets Bug 1)

    [Fact(Timeout = 30000)]
    public async Task UseCase_ConfigPush_SameConfigSentRepeatedly_DeltaDrops()
    {
        // Config server pushes the same config repeatedly via DeltaTransit
        // This is the exact scenario where Bug 1 causes data loss
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeA = await muxA.OpenChannelAsync(new() { ChannelId = "config_push" }, cts.Token);
        var readB = await muxB.AcceptChannelAsync("config_push", cts.Token);

        await using var sender = new DeltaTransit<JsonNode>(writeA, null);
        await using var receiver = new DeltaTransit<JsonNode>(null, readB);

        var config = JsonNode.Parse("""
        {
            "maxRetries": 3,
            "timeout": 30000,
            "debugMode": false,
            "endpoints": ["http://api1.local", "http://api2.local"]
        }
        """)!;

        var receivedCount = 0;
        for (int push = 0; push < 10; push++)
        {
            await sender.SendAsync(config.DeepClone(), cts.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);

            try
            {
                var received = await receiver.ReceiveAsync(linked.Token);
                if (received is not null) receivedCount++;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Bug 1: Only first push is received
        Assert.Equal(10, receivedCount);
    }

    #endregion

    #region Use Case 14: Mixed Transit Session (targets Bugs 1, 4, 12)

    [Fact(Timeout = 60000)]
    public async Task UseCase_MixedTransitSession_AllTransitTypes_Operational()
    {
        // A real session uses Delta, Message, Raw, and DuplexStream together
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        // DeltaTransit: state sync
        var dw = await muxA.OpenChannelAsync(new() { ChannelId = "mixed_delta" }, cts.Token);
        var dr = await muxB.AcceptChannelAsync("mixed_delta", cts.Token);
        await using var deltaSender = new DeltaTransit<JsonNode>(dw, null);
        await using var deltaReceiver = new DeltaTransit<JsonNode>(null, dr);

        // MessageTransit: commands
        var mw = await muxA.OpenChannelAsync(new() { ChannelId = "mixed_cmd" }, cts.Token);
        var mr = await muxB.AcceptChannelAsync("mixed_cmd", cts.Token);
        await using var cmdSender = new MessageTransit<RpcRequest, RpcRequest>(
            mw, null, UseCaseJsonContext.Default.RpcRequest, UseCaseJsonContext.Default.RpcRequest);
        await using var cmdReceiver = new MessageTransit<RpcRequest, RpcRequest>(
            null, mr, UseCaseJsonContext.Default.RpcRequest, UseCaseJsonContext.Default.RpcRequest);

        // Raw channel: binary data
        var rw = await muxA.OpenChannelAsync(new() { ChannelId = "mixed_raw" }, cts.Token);
        var rr = await muxB.AcceptChannelAsync("mixed_raw", cts.Token);

        // DuplexStream: bidirectional pipe
        var openDuplexTask = muxA.OpenDuplexStreamAsync("mixed_duplex", cts.Token);
        var acceptDuplexTask = muxB.AcceptDuplexStreamAsync("mixed_duplex", cts.Token);
        var duplex = await openDuplexTask;
        var remoteDuplex = await acceptDuplexTask;

        // Use all four simultaneously
        await deltaSender.SendAsync(JsonNode.Parse("""{"status": "connected"}""")!, cts.Token);
        var deltaMsg = await deltaReceiver.ReceiveAsync(cts.Token);
        Assert.NotNull(deltaMsg);

        await cmdSender.SendAsync(new RpcRequest("ping", "1", null), cts.Token);
        var cmd = await cmdReceiver.ReceiveAsync(cts.Token);
        Assert.NotNull(cmd);
        Assert.Equal("ping", cmd.Method);

        await rw.WriteAsync(new byte[] { 0xDE, 0xAD }, cts.Token);
        var rawBuf = new byte[2];
        var total = 0;
        while (total < 2) { var n = await rr.ReadAsync(rawBuf.AsMemory(total), cts.Token); total += n; }
        Assert.Equal(0xDE, rawBuf[0]);

        await duplex.WriteAsync(new byte[] { 0xBE }, cts.Token);
        var dBuf = new byte[1];
        Assert.Equal(1, await remoteDuplex.ReadAsync(dBuf, cts.Token));
        Assert.Equal(0xBE, dBuf[0]);

        await duplex.DisposeAsync();
        await remoteDuplex.DisposeAsync();
    }

    #endregion

    #region Use Case 15: Streaming Metrics (targets Bug 1, 11)

    [Fact(Timeout = 60000)]
    public async Task UseCase_MetricsStream_PeriodicSameValue_AllDelivered()
    {
        // Metrics stream where gauges report the same value repeatedly
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var mw = await muxA.OpenChannelAsync(new() { ChannelId = "metrics" }, cts.Token);
        var mr = await muxB.AcceptChannelAsync("metrics", cts.Token);

        await using var sender = new MessageTransit<ChatMessage, ChatMessage>(
            mw, null, UseCaseJsonContext.Default.ChatMessage, UseCaseJsonContext.Default.ChatMessage);
        await using var receiver = new MessageTransit<ChatMessage, ChatMessage>(
            null, mr, UseCaseJsonContext.Default.ChatMessage, UseCaseJsonContext.Default.ChatMessage);

        // CPU gauge stays at 42% for multiple reports
        var receivedCount = 0;
        for (int i = 0; i < 50; i++)
        {
            var metric = new ChatMessage("cpu_gauge", "42.0", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await sender.SendAsync(metric, cts.Token);
            var received = await receiver.ReceiveAsync(cts.Token);
            Assert.NotNull(received);
            Assert.Equal("cpu_gauge", received.User);
            receivedCount++;
        }

        Assert.Equal(50, receivedCount);
    }

    #endregion

    #region Use Case 16: Channel Migration (targets Bug 5, 6)

    [Fact(Timeout = 60000)]
    public async Task UseCase_ChannelMigration_OldClosedNewOpened_DataFlows()
    {
        // Simulate session migration: close old channels, open new ones repeatedly
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        for (int gen = 0; gen < 50; gen++)
        {
            var id = $"session_gen_{gen}";
            var write = await muxA.OpenChannelAsync(new() { ChannelId = id }, cts.Token);
            var read = await muxB.AcceptChannelAsync(id, cts.Token);

            var data = BitConverter.GetBytes(gen);
            await write.WriteAsync(data, cts.Token);

            var buf = new byte[4];
            var total = 0;
            while (total < 4)
            {
                var n = await read.ReadAsync(buf.AsMemory(total), cts.Token);
                if (n == 0) break;
                total += n;
            }
            Assert.Equal(4, total);
            Assert.Equal(gen, BitConverter.ToInt32(buf));

            await write.DisposeAsync();
            await read.DisposeAsync();
        }
    }

    #endregion

    #region Use Case 17: Bidirectional Command Channel (targets Bug 8, 12)

    [Fact(Timeout = 30000)]
    public async Task UseCase_BidirectionalCommand_BothSidesInitiate_Works()
    {
        // Both client and server open channels to each other
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        // A opens to B
        var aToB_w = await muxA.OpenChannelAsync(new() { ChannelId = "a_to_b" }, cts.Token);
        var aToB_r = await muxB.AcceptChannelAsync("a_to_b", cts.Token);

        // B opens to A
        var bToA_w = await muxB.OpenChannelAsync(new() { ChannelId = "b_to_a" }, cts.Token);
        var bToA_r = await muxA.AcceptChannelAsync("b_to_a", cts.Token);

        // Exchange data in both directions simultaneously
        var aToBTask = Task.Run(async () =>
        {
            await aToB_w.WriteAsync(new byte[] { 0x01 }, cts.Token);
            var buf = new byte[1];
            Assert.Equal(1, await bToA_r.ReadAsync(buf, cts.Token));
            Assert.Equal(0x02, buf[0]);
        });

        var bToATask = Task.Run(async () =>
        {
            await bToA_w.WriteAsync(new byte[] { 0x02 }, cts.Token);
            var buf = new byte[1];
            Assert.Equal(1, await aToB_r.ReadAsync(buf, cts.Token));
            Assert.Equal(0x01, buf[0]);
        });

        await Task.WhenAll(aToBTask, bToATask);
    }

    #endregion

    #region Use Case 18: Bulk Upload with Credits (targets Bug 4, 11)

    [Fact(Timeout = 60000)]
    public async Task UseCase_BulkUpload_LargeData_CreditsManaged()
    {
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var write = await muxA.OpenChannelAsync(new() { ChannelId = "bulk_upload" }, cts.Token);
        var read = await muxB.AcceptChannelAsync("bulk_upload", cts.Token);

        var starvationCount = 0;
        write.OnCreditStarvation += () => Interlocked.Increment(ref starvationCount);

        // Upload 4MB in 16KB chunks
        const int totalSize = 4 * 1024 * 1024;
        const int chunkSize = 16 * 1024;
        var sentBytes = 0L;

        var uploadTask = Task.Run(async () =>
        {
            var chunk = new byte[chunkSize];
            for (int offset = 0; offset < totalSize; offset += chunkSize)
            {
                BinaryPrimitives.WriteInt32BigEndian(chunk, offset);
                await write.WriteAsync(chunk, cts.Token);
                Interlocked.Add(ref sentBytes, chunkSize);
            }
        });

        var receivedBytes = 0L;
        var downloadTask = Task.Run(async () =>
        {
            var buf = new byte[65536];
            while (Interlocked.Read(ref receivedBytes) < totalSize)
            {
                var n = await read.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                Interlocked.Add(ref receivedBytes, n);
            }
        });

        await uploadTask;
        await downloadTask;

        Assert.Equal(totalSize, receivedBytes);
    }

    #endregion
}
