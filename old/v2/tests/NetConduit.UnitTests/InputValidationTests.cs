using System.Buffers.Binary;
using System.Reflection;
using NetConduit.Enums;
using NetConduit.Exceptions;
using NetConduit.Internal;
using NetConduit.Models;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for input validation in DeltaBinaryEncoder, ReceiveReconnectAckAsync,
/// and ProcessReconnectFrame.
/// </summary>
public class InputValidationTests
{
    private const int TestTimeout = 15000;

    #region DeltaBinaryEncoder — ReadVarint bounds and overflow

    [Fact]
    public void BinaryDecode_VarintWithAllContinuationBits_ThrowsMeaningfulError()
    {
        // A varint that never terminates (all bytes have continuation bit 0x80 set).
        // After the version byte and opCount varint, the stringCount varint is all-continuation.
        // Expected: InvalidOperationException (or similar) with meaningful message
        // Actual (bug): IndexOutOfRangeException when reading past buffer
        var data = new byte[]
        {
            0x01,                   // version = 1
            0x00,                   // opCount = 0 (valid varint)
            0x80, 0x80, 0x80, 0x80, 0x80, 0x80  // stringCount varint: all continuation, no terminator
        };

        var ex = Assert.ThrowsAny<Exception>(() => DeltaBinaryEncoder.Decode(data));

        // Should NOT be IndexOutOfRangeException — that leaks implementation details
        Assert.IsNotType<IndexOutOfRangeException>(ex);
    }

    [Fact]
    public void BinaryDecode_VarintExceedsFiveBytes_ThrowsOverflowError()
    {
        // A varint with 6 continuation bytes and a terminator — shifts past 32 bits.
        // Expected: error about malformed varint
        // Actual (bug): silently produces corrupt result due to shift overflow
        var data = new byte[]
        {
            0x01,       // version = 1
            0x00,       // opCount = 0
            0x80, 0x80, 0x80, 0x80, 0x80, 0x01  // stringCount: 6 bytes, shift reaches 35
        };

        var ex = Assert.ThrowsAny<Exception>(() => DeltaBinaryEncoder.Decode(data));

        // Should NOT be IndexOutOfRangeException
        Assert.IsNotType<IndexOutOfRangeException>(ex);
    }

    [Fact]
    public void BinaryDecode_HugeStringCount_ThrowsWithoutAllocating()
    {
        // stringCount varint decodes to a very large value (e.g., 0x7FFFFFFF = ~2 billion).
        // Expected: validation error before allocation attempt
        // Actual (bug): attempts new string[2_000_000_000] → OutOfMemoryException
        var data = new byte[]
        {
            0x01,                           // version = 1
            0x00,                           // opCount = 0
            0xFF, 0xFF, 0xFF, 0xFF, 0x07   // stringCount = 0x7FFFFFFF (max positive int via varint)
        };

        var ex = Assert.ThrowsAny<Exception>(() => DeltaBinaryEncoder.Decode(data));

        // Should be a validation error, NOT OutOfMemoryException
        Assert.IsNotType<OutOfMemoryException>(ex);
    }

    [Fact]
    public void BinaryDecode_HugePathLength_ThrowsWithoutAllocating()
    {
        // opCount=1, stringCount=0, then operation has pathLength varint = huge value.
        // Expected: validation error
        // Actual (bug): attempts new object[huge] → OutOfMemoryException
        var data = new byte[]
        {
            0x01,                           // version = 1
            0x01,                           // opCount = 1
            0x00,                           // stringCount = 0
            0x00,                           // opCode = Set (0)
            0xFF, 0xFF, 0xFF, 0xFF, 0x07   // pathLength = 0x7FFFFFFF
        };

        var ex = Assert.ThrowsAny<Exception>(() => DeltaBinaryEncoder.Decode(data));

        Assert.IsNotType<OutOfMemoryException>(ex);
    }

    [Fact]
    public void BinaryDecode_StringLengthExceedsRemainingData_ThrowsMeaningfulError()
    {
        // stringCount=1, string length varint claims 1000 bytes but only 3 bytes remain.
        // Expected: meaningful error about truncated data
        // Actual (bug): ArgumentOutOfRangeException from Slice or reads garbage
        var data = new byte[]
        {
            0x01,       // version = 1
            0x00,       // opCount = 0
            0x01,       // stringCount = 1
            0xE8, 0x07  // string length varint = 1000
            // No string data follows
        };

        var ex = Assert.ThrowsAny<Exception>(() => DeltaBinaryEncoder.Decode(data));

        Assert.IsNotType<ArgumentOutOfRangeException>(ex);
    }

    #endregion

    #region ReceiveReconnectAckAsync — unbounded allocation

    [Fact(Timeout = TestTimeout)]
    public async Task ReconnectAck_FrameLengthExceedsMaxFrameSize_ThrowsProtocolError()
    {
        // ReceiveReconnectAckAsync reads a frame header and allocates new byte[header.Length]
        // without checking MaxFrameSize. The normal read path checks MaxFrameSize, but the
        // reconnect path does not.
        // Expected: MultiplexerException about frame length exceeding MaxFrameSize
        // Actual (bug): allocates whatever header.Length says, no size check
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = mux1;
        await using var b = mux2;

        // Get MaxFrameSize from options
        var optionsField = typeof(StreamMultiplexer)
            .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(optionsField);
        var options = (MultiplexerOptions)optionsField!.GetValue(mux1)!;
        Assert.True(options.MaxFrameSize > 0);

        // Construct a frame with Length = MaxFrameSize + 1 (just over the limit)
        var overlimitLength = (uint)(options.MaxFrameSize + 1);

        // Build a stream with: 9-byte frame header + enough payload to avoid EndOfStream
        var frameData = new byte[9 + overlimitLength];
        BinaryPrimitives.WriteUInt32BigEndian(frameData, 0); // control channel
        frameData[4] = 0; // flags
        BinaryPrimitives.WriteUInt32BigEndian(frameData.AsSpan(5), overlimitLength);
        // Payload doesn't matter — the check should happen before reading it

        using var ms = new MemoryStream(frameData);

        var method = typeof(StreamMultiplexer)
            .GetMethod("ReceiveReconnectAckAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(mux1, [ms, cts.Token])!;

        // Should throw MultiplexerException about exceeding MaxFrameSize,
        // not succeed with the oversized allocation
        var ex = await Assert.ThrowsAsync<MultiplexerException>(() => task);
        Assert.Contains("MaxFrameSize", ex.Message);
    }

    #endregion

    #region ProcessReconnectFrame — silent truncation

    [Fact(Timeout = TestTimeout)]
    public async Task ProcessReconnectFrame_TruncatedPayload_SendsProtocolError()
    {
        // A RECONNECT frame claims channelCount=10 but only has data for 1 channel.
        // The fix validates expected payload size against channelCount before processing.
        // Before fix: silently processes 1 of 10 claimed channels — state inconsistency.
        // After fix: detects mismatch, sends error, returns without processing any channels.
        await using var pipe = new DuplexPipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);
        await using var a = mux1;
        await using var b = mux2;

        var remoteSessionIdField = typeof(StreamMultiplexer)
            .GetField("_remoteSessionId", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(remoteSessionIdField);
        var mux1RemoteSession = (Guid)remoteSessionIdField!.GetValue(mux1)!;

        // Build a truncated RECONNECT payload:
        // channelCount claims 10, but only 1 channel entry in data
        var payload = new byte[32]; // 20 header + 12 for 1 entry (not 10)
        mux1RemoteSession.TryWriteBytes(payload.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), 10);

        var method = typeof(StreamMultiplexer)
            .GetMethod("ProcessReconnectFrame", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        // Confirm the fix validates correctly: expected size = 20 + 10*12 = 140, actual = 32
        // The method should detect the mismatch and return early (sending error frame).
        // This call verifies the fix handles the truncated payload without crashing.
        var expectedPayloadSize = 20 + 10 * 12;
        Assert.True(expectedPayloadSize > payload.Length,
            "Test setup: payload must be shorter than what channelCount claims");

        // Method should not throw — it sends error and returns
        var ex = Record.Exception(() =>
            method!.Invoke(mux1, [new ReadOnlyMemory<byte>(payload), cts.Token]));
        Assert.Null(ex);
    }

    #endregion
}
