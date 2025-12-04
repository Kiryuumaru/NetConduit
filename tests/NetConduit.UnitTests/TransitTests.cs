using System.Text.Json;
using System.Text.Json.Serialization;
using NetConduit.Transits;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for the Transit classes (MessageTransit, StreamTransit, DuplexStreamTransit).
/// </summary>
public partial class TransitTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    #region Test Messages

    public record TestMessage(string Id, int Value, string? Text = null);

    public record ComplexMessage(
        string Id,
        List<int> Numbers,
        Dictionary<string, string> Metadata,
        DateTime Timestamp);

    [JsonSerializable(typeof(TestMessage))]
    [JsonSerializable(typeof(ComplexMessage))]
    internal partial class TestJsonContext : JsonSerializerContext { }

    #endregion

    #region MessageTransit Tests

    [Fact(Timeout = 120000)]
    public async Task MessageTransit_SendReceive_SimpleMessage_Works()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        // Create channels
        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "msg_send" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("msg_send", cts.Token);

        // Create transit with AOT-safe JsonTypeInfo
        await using var sendTransit = new MessageTransit<TestMessage, TestMessage>(
            writeChannel, null,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        await using var receiveTransit = new MessageTransit<TestMessage, TestMessage>(
            null, readChannel,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        // Act
        var expectedMessage = new TestMessage("test-123", 42, "Hello World");
        await sendTransit.SendAsync(expectedMessage, cts.Token);

        var receivedMessage = await receiveTransit.ReceiveAsync(cts.Token);

        // Assert
        Assert.NotNull(receivedMessage);
        Assert.Equal(expectedMessage.Id, receivedMessage.Id);
        Assert.Equal(expectedMessage.Value, receivedMessage.Value);
        Assert.Equal(expectedMessage.Text, receivedMessage.Text);

        // Cleanup
        await writeChannel.DisposeAsync();
        await readChannel.DisposeAsync();
        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task MessageTransit_BidirectionalChannelPair_SendReceiveBothDirections()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        // Create channel pair: A→B for requests, B→A for responses
        var requestWriteChannel = await muxA.OpenChannelAsync(new() { ChannelId = "requests" }, cts.Token);
        var requestReadChannel = await muxB.AcceptChannelAsync("requests", cts.Token);

        var responseWriteChannel = await muxB.OpenChannelAsync(new() { ChannelId = "responses" }, cts.Token);
        var responseReadChannel = await muxA.AcceptChannelAsync("responses", cts.Token);

        // Create bidirectional transits
        await using var transitA = new MessageTransit<TestMessage, TestMessage>(
            requestWriteChannel, responseReadChannel,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        await using var transitB = new MessageTransit<TestMessage, TestMessage>(
            responseWriteChannel, requestReadChannel,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        // Act - Send request from A
        var request = new TestMessage("req-1", 100, "Request from A");
        await transitA.SendAsync(request, cts.Token);

        // Receive request at B
        var receivedRequest = await transitB.ReceiveAsync(cts.Token);
        Assert.NotNull(receivedRequest);
        Assert.Equal(request.Id, receivedRequest.Id);

        // Send response from B
        var response = new TestMessage("resp-1", 200, "Response from B");
        await transitB.SendAsync(response, cts.Token);

        // Receive response at A
        var receivedResponse = await transitA.ReceiveAsync(cts.Token);
        Assert.NotNull(receivedResponse);
        Assert.Equal(response.Id, receivedResponse.Id);

        // Cleanup
        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task MessageTransit_MultipleMessages_AllReceivedInOrder()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "multi_msg" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("multi_msg", cts.Token);

        await using var sendTransit = new MessageTransit<TestMessage, TestMessage>(
            writeChannel, null,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        await using var receiveTransit = new MessageTransit<TestMessage, TestMessage>(
            null, readChannel,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        // Act
        const int messageCount = 100;
        var messages = Enumerable.Range(0, messageCount)
            .Select(i => new TestMessage($"msg-{i}", i, $"Message {i}"))
            .ToList();

        foreach (var msg in messages)
        {
            await sendTransit.SendAsync(msg, cts.Token);
        }

        var received = new List<TestMessage>();
        for (int i = 0; i < messageCount; i++)
        {
            var msg = await receiveTransit.ReceiveAsync(cts.Token);
            Assert.NotNull(msg);
            received.Add(msg);
        }

        // Assert - all messages received in order
        Assert.Equal(messageCount, received.Count);
        for (int i = 0; i < messageCount; i++)
        {
            Assert.Equal(messages[i].Id, received[i].Id);
            Assert.Equal(messages[i].Value, received[i].Value);
        }

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task MessageTransit_ComplexMessage_SerializesCorrectly()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "complex" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("complex", cts.Token);

        await using var sendTransit = new MessageTransit<ComplexMessage, ComplexMessage>(
            writeChannel, null,
            TestJsonContext.Default.ComplexMessage,
            TestJsonContext.Default.ComplexMessage);

        await using var receiveTransit = new MessageTransit<ComplexMessage, ComplexMessage>(
            null, readChannel,
            TestJsonContext.Default.ComplexMessage,
            TestJsonContext.Default.ComplexMessage);

        // Act
        var complex = new ComplexMessage(
            "complex-1",
            [1, 2, 3, 4, 5],
            new Dictionary<string, string> { ["key1"] = "value1", ["key2"] = "value2" },
            DateTime.UtcNow);

        await sendTransit.SendAsync(complex, cts.Token);
        var received = await receiveTransit.ReceiveAsync(cts.Token);

        // Assert
        Assert.NotNull(received);
        Assert.Equal(complex.Id, received.Id);
        Assert.Equal(complex.Numbers.Count, received.Numbers.Count);
        Assert.Equal(complex.Metadata.Count, received.Metadata.Count);
        Assert.Equal(complex.Metadata["key1"], received.Metadata["key1"]);

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task MessageTransit_ReceiveAllAsync_EnumeratesAllMessages()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "async_enum" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("async_enum", cts.Token);

        await using var sendTransit = new MessageTransit<TestMessage, TestMessage>(
            writeChannel, null,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        await using var receiveTransit = new MessageTransit<TestMessage, TestMessage>(
            null, readChannel,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        const int messageCount = 50;

        // Send all messages
        var sendTask = Task.Run(async () =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                await sendTransit.SendAsync(new TestMessage($"enum-{i}", i), cts.Token);
            }
            // Close channel to signal end
            await writeChannel.DisposeAsync();
        }, cts.Token);

        // Act - Receive via async enumerable
        var received = new List<TestMessage>();
        await foreach (var msg in receiveTransit.ReceiveAllAsync(cts.Token))
        {
            received.Add(msg);
        }

        await sendTask;

        // Assert
        Assert.Equal(messageCount, received.Count);
        for (int i = 0; i < messageCount; i++)
        {
            Assert.Equal($"enum-{i}", received[i].Id);
        }

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task MessageTransit_IsConnected_ReflectsChannelState()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "conn_check" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("conn_check", cts.Token);

        await using var transit = new MessageTransit<TestMessage, TestMessage>(
            writeChannel, readChannel,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        // Assert - connected initially
        Assert.True(transit.IsConnected);
        Assert.Equal("conn_check", transit.WriteChannelId);
        Assert.Equal("conn_check", transit.ReadChannelId);

        await cts.CancelAsync();
    }

    #endregion

    #region StreamTransit Tests

    [Fact(Timeout = 120000)]
    public async Task StreamTransit_WriteOnly_SendsData()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "stream_write" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("stream_write", cts.Token);

        await using var writeStream = new StreamTransit(writeChannel);

        // Act
        var data = "Hello from StreamTransit!"u8.ToArray();
        await writeStream.WriteAsync(data, cts.Token);

        var buffer = new byte[100];
        var bytesRead = await readChannel.ReadAsync(buffer, cts.Token);

        // Assert
        Assert.Equal(data.Length, bytesRead);
        Assert.Equal(data, buffer[..bytesRead]);
        Assert.True(writeStream.CanWrite);
        Assert.False(writeStream.CanRead);
        Assert.False(writeStream.CanSeek);

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamTransit_ReadOnly_ReceivesData()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "stream_read" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("stream_read", cts.Token);

        await using var readStream = new StreamTransit(readChannel);

        // Act
        var data = "Hello from WriteChannel!"u8.ToArray();
        await writeChannel.WriteAsync(data, cts.Token);

        var buffer = new byte[100];
        var bytesRead = await readStream.ReadAsync(buffer, cts.Token);

        // Assert
        Assert.Equal(data.Length, bytesRead);
        Assert.Equal(data, buffer[..bytesRead]);
        Assert.False(readStream.CanWrite);
        Assert.True(readStream.CanRead);

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamTransit_ExtensionMethod_AsStream_Works()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "ext_method" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("ext_method", cts.Token);

        // Act - Use extension method
        await using var writeStream = writeChannel.AsStream();
        await using var readStream = readChannel.AsStream();

        var data = new byte[] { 1, 2, 3, 4, 5 };
        await writeStream.WriteAsync(data, cts.Token);

        var buffer = new byte[10];
        var bytesRead = await readStream.ReadAsync(buffer, cts.Token);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer[..5]);

        await cts.CancelAsync();
    }

    #endregion

    #region DuplexStreamTransit Tests

    [Fact(Timeout = 120000)]
    public async Task DuplexStreamTransit_BidirectionalCommunication_Works()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        // Create channel pair for duplex communication
        var aToB = await muxA.OpenChannelAsync(new() { ChannelId = "a_to_b" }, cts.Token);
        var aToBRead = await muxB.AcceptChannelAsync("a_to_b", cts.Token);

        var bToA = await muxB.OpenChannelAsync(new() { ChannelId = "b_to_a" }, cts.Token);
        var bToARead = await muxA.AcceptChannelAsync("b_to_a", cts.Token);

        // Create duplex streams
        await using var duplexA = new DuplexStreamTransit(aToB, bToARead);
        await using var duplexB = new DuplexStreamTransit(bToA, aToBRead);

        // Act - A sends, B receives
        var dataFromA = "From A to B"u8.ToArray();
        await duplexA.WriteAsync(dataFromA, cts.Token);

        var bufferB = new byte[100];
        var bytesReadB = await duplexB.ReadAsync(bufferB, cts.Token);

        // B sends, A receives
        var dataFromB = "From B to A"u8.ToArray();
        await duplexB.WriteAsync(dataFromB, cts.Token);

        var bufferA = new byte[100];
        var bytesReadA = await duplexA.ReadAsync(bufferA, cts.Token);

        // Assert
        Assert.Equal(dataFromA.Length, bytesReadB);
        Assert.Equal(dataFromA, bufferB[..bytesReadB]);

        Assert.Equal(dataFromB.Length, bytesReadA);
        Assert.Equal(dataFromB, bufferA[..bytesReadA]);

        Assert.True(duplexA.CanRead);
        Assert.True(duplexA.CanWrite);
        Assert.True(duplexA.IsConnected);

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task DuplexStreamTransit_ExtensionMethod_AsDuplexStream_Works()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var write = await muxA.OpenChannelAsync(new() { ChannelId = "duplex_ext_w" }, cts.Token);
        var read = await muxB.AcceptChannelAsync("duplex_ext_w", cts.Token);

        var writeBack = await muxB.OpenChannelAsync(new() { ChannelId = "duplex_ext_r" }, cts.Token);
        var readBack = await muxA.AcceptChannelAsync("duplex_ext_r", cts.Token);

        // Act - Use extension method
        await using var duplexA = write.AsDuplexStream(readBack);
        await using var duplexB = writeBack.AsDuplexStream(read);

        var data = new byte[] { 10, 20, 30, 40, 50 };
        await duplexA.WriteAsync(data, cts.Token);

        var buffer = new byte[10];
        var bytesRead = await duplexB.ReadAsync(buffer, cts.Token);

        // Assert
        Assert.Equal(5, bytesRead);
        Assert.Equal(data, buffer[..5]);
        Assert.Equal("duplex_ext_w", duplexA.WriteChannelId);
        Assert.Equal("duplex_ext_r", duplexA.ReadChannelId);

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task DuplexStreamTransit_LargeDataTransfer_WorksBidirectionally()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var aToB = await muxA.OpenChannelAsync(new() { ChannelId = "large_a_to_b" }, cts.Token);
        var aToBRead = await muxB.AcceptChannelAsync("large_a_to_b", cts.Token);

        var bToA = await muxB.OpenChannelAsync(new() { ChannelId = "large_b_to_a" }, cts.Token);
        var bToARead = await muxA.AcceptChannelAsync("large_b_to_a", cts.Token);

        await using var duplexA = new DuplexStreamTransit(aToB, bToARead);
        await using var duplexB = new DuplexStreamTransit(bToA, aToBRead);

        // Act - Transfer 1MB in each direction
        const int dataSize = 1024 * 1024;
        var dataA = new byte[dataSize];
        var dataB = new byte[dataSize];
        Random.Shared.NextBytes(dataA);
        Random.Shared.NextBytes(dataB);

        var sendFromATask = duplexA.WriteAsync(dataA, cts.Token);
        var sendFromBTask = duplexB.WriteAsync(dataB, cts.Token);

        var receivedAtB = new byte[dataSize];
        var receivedAtA = new byte[dataSize];

        var receiveAtBTask = ReadExactAsync(duplexB, receivedAtB, cts.Token);
        var receiveAtATask = ReadExactAsync(duplexA, receivedAtA, cts.Token);

        await Task.WhenAll(sendFromATask.AsTask(), sendFromBTask.AsTask(), receiveAtBTask, receiveAtATask);

        // Assert
        Assert.Equal(dataA, receivedAtB);
        Assert.Equal(dataB, receivedAtA);

        await cts.CancelAsync();
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (read == 0) throw new EndOfStreamException();
            totalRead += read;
        }
    }

    #endregion

    #region Edge Cases

    [Fact(Timeout = 120000)]
    public async Task Transit_DisposedTransit_ThrowsObjectDisposedException()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "disposed_test" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("disposed_test", cts.Token);

        var transit = new MessageTransit<TestMessage, TestMessage>(
            writeChannel, readChannel,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        // Act - Dispose
        await transit.DisposeAsync();

        // Assert - Should throw on operations
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await transit.SendAsync(new TestMessage("x", 1), cts.Token));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await transit.ReceiveAsync(cts.Token));

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task StreamTransit_UnsupportedOperations_Throw()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "unsupported" }, cts.Token);
        await using var stream = new StreamTransit(writeChannel);

        // Assert - Unsupported operations throw
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
        Assert.Throws<NotSupportedException>(() => _ = stream.Length);
        Assert.Throws<NotSupportedException>(() => _ = stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);

        // Read on write-only stream
#pragma warning disable CA2022 // Avoid inexact read - intentionally testing that read throws on write-only stream
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await stream.ReadAsync(new byte[10], cts.Token));
#pragma warning restore CA2022

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task MessageTransit_SendOnly_ReceiveThrows()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "send_only" }, cts.Token);

        await using var sendOnlyTransit = new MessageTransit<TestMessage, TestMessage>(
            writeChannel, null,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sendOnlyTransit.ReceiveAsync(cts.Token));

        await cts.CancelAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task MessageTransit_ReceiveOnly_SendThrows()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TestTimeout);

        await using var muxA = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var muxB = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var runA = muxA.RunAsync(cts.Token);
        var runB = muxB.RunAsync(cts.Token);

        var writeChannel = await muxA.OpenChannelAsync(new() { ChannelId = "recv_only" }, cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("recv_only", cts.Token);

        await using var receiveOnlyTransit = new MessageTransit<TestMessage, TestMessage>(
            null, readChannel,
            TestJsonContext.Default.TestMessage,
            TestJsonContext.Default.TestMessage);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await receiveOnlyTransit.SendAsync(new TestMessage("x", 1), cts.Token));

        await cts.CancelAsync();
    }

    #endregion
}
