using System.Buffers.Binary;
using System.Reflection;
using NetConduit.Models;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests that reconnection protocol payloads are validated for correct size and bounds.
/// Ensures truncated or malformed payloads don't cause out-of-bounds reads.
/// </summary>
public class ReconnectAckPayloadValidationTests
{
    private const int TestTimeout = 15000;

    [Fact(Timeout = TestTimeout)]
    public async Task ReconnectAckParsing_TruncatedPayload_DoesNotThrowOutOfRange()
    {
        // Build a valid-looking RECONNECT_ACK with channelCount=5 but only 1 entry (truncated)
        // Expected: graceful handling (protocol error or empty positions)
        // Actual (bug): ArgumentOutOfRangeException from BinaryPrimitives.ReadUInt32BigEndian
        
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = mux1;
        await using var b = mux2;

        // Get mux1's _remoteSessionId (set during handshake)
        var remoteSessionIdField = typeof(StreamMultiplexer)
            .GetField("_remoteSessionId", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(remoteSessionIdField);
        var remoteSessionId = (Guid)remoteSessionIdField!.GetValue(mux1)!;

        // Build a RECONNECT_ACK payload:
        // [0] = 0x08 (ReconnectAck subtype)
        // [1..16] = session ID (16 bytes)
        // [17..20] = channelCount = 5 (claims 5 entries)
        // [21..32] = one channel entry (12 bytes) → only 1 of 5 claimed
        var payload = new byte[33]; // 21 + 12 = exactly 1 entry
        payload[0] = 0x08; // ReconnectAck
        remoteSessionId.TryWriteBytes(payload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17), 5); // claims 5 entries
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(21), 1); // channel index 1
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(25), 100); // bytes received

        // Build a frame header: ChannelId=0 (control), Flags=Data(0), Length=payload size
        var frame = new byte[9 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0), 0); // control channel
        frame[4] = 0x00; // Data flag
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(9));

        // Feed the crafted frame via a MemoryStream to ReceiveReconnectAckAsync
        using var ms = new MemoryStream(frame);

        var method = typeof(StreamMultiplexer)
            .GetMethod("ReceiveReconnectAckAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Should NOT throw ArgumentOutOfRangeException — should handle gracefully
        var task = (Task<Dictionary<uint, long>>)method!.Invoke(mux1, [ms, cts.Token])!;
        var positions = await task;

        // At most 1 valid entry should be parsed (the others are truncated)
        Assert.True(positions.Count <= 1, 
            $"Expected at most 1 position parsed from truncated payload, got {positions.Count}");

        cts.Cancel();
    }
}
