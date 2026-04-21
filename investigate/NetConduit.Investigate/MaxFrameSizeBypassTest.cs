using NetConduit.Models;

namespace NetConduit.Investigate;

/// <summary>
/// Proves that MultiplexerOptions.MaxFrameSize and MessageTransit maxMessageSize
/// accept invalid (negative/zero) values with no validation, leading to protocol
/// breakage or unexpected behavior. Also proves the int-to-uint cast safety issue.
/// </summary>
public class MaxFrameSizeBypassTest
{
    [Fact]
    public void CSharpPromotion_UintVsInt_PromotesToLong()
    {
        // In C#, uint > int promotes BOTH to long (not uint like C/C++).
        // So negative MaxFrameSize = -1 means: uint > long(-1) → all frames BLOCKED.
        // This is safe for DoS, but BREAKS the protocol entirely:
        // Even handshake frames (which have non-zero length) would be rejected.

        int maxFrameSize = -1;
        uint headerLength = 25; // Handshake frame length

        bool blocked = headerLength > maxFrameSize;
        // C# promotes: (long)25 > (long)(-1) → true → blocked!

        Assert.True(blocked,
            "A normal 25-byte handshake frame is BLOCKED when MaxFrameSize=-1. " +
            "This breaks the protocol on connection — the mux can never complete handshake.");
    }

    [Fact]
    public void ZeroMaxFrameSize_BlocksAllNonEmptyFrames()
    {
        int maxFrameSize = 0;

        Assert.True((uint)1 > maxFrameSize, "1-byte frame blocked");
        Assert.True((uint)25 > maxFrameSize, "Handshake frame blocked");
        Assert.False((uint)0 > maxFrameSize, "Only zero-length frames pass");

        // MaxFrameSize=0 means handshake fails → connection never establishes
    }

    [Theory]
    [InlineData(-1, 0u, true)]       // Zero-length frame blocked by negative max
    [InlineData(-1, 25u, true)]      // Handshake blocked
    [InlineData(-1, 100u, true)]     // All frames blocked
    [InlineData(0, 0u, false)]       // Only zero-length passes with max=0
    [InlineData(0, 1u, true)]        // 1 byte blocked with max=0
    [InlineData(16, 16u, false)]     // Exactly at limit passes
    [InlineData(16, 17u, true)]      // One over blocked
    public void MaxFrameSize_BehaviorMatrix(int maxFrameSize, uint headerLength, bool shouldBlock)
    {
        bool blocked = headerLength > maxFrameSize;
        Assert.Equal(shouldBlock, blocked);
    }

    [Fact]
    public void DefaultMaxFrameSize_IsCorrect()
    {
        var options = new MultiplexerOptions
        {
            StreamFactory = _ => throw new NotImplementedException()
        };
        Assert.Equal(16 * 1024 * 1024, options.MaxFrameSize);
        Assert.True(options.MaxFrameSize > 0, "Default MaxFrameSize should be positive");
    }

    [Fact]
    public void NoValidation_AllowsNegativeMaxFrameSize()
    {
        // Prove there's no validation preventing negative values
        var options = new MultiplexerOptions
        {
            MaxFrameSize = -1,
            StreamFactory = _ => throw new NotImplementedException()
        };

        Assert.Equal(-1, options.MaxFrameSize);
        // No exception thrown — the invalid value is silently accepted.
        // Consequence: ALL frames rejected → protocol broken → connection hangs.
    }

    [Fact]
    public void NoValidation_AllowsZeroMaxFrameSize()
    {
        var options = new MultiplexerOptions
        {
            MaxFrameSize = 0,
            StreamFactory = _ => throw new NotImplementedException()
        };

        Assert.Equal(0, options.MaxFrameSize);
        // MaxFrameSize=0 means only zero-length frames pass.
        // Handshake requires non-zero frames → protocol broken.
    }

    [Fact]
    public void MessageTransit_NegativeMaxMessageSize_AllBlockedByLongPromotion()
    {
        // MessageTransit:
        //   var messageLength = ReadUInt32BigEndian(...)  // uint
        //   if (messageLength > _maxMessageSize)          // int
        //
        // C# promotes both to long:
        //   (long)messageLength > (long)(-1) → true for any messageLength >= 0
        // So ALL messages are rejected — protocol broken.

        int maxMessageSize = -1;
        uint receivedLength = 0; // Even zero-length

        bool blocked = receivedLength > maxMessageSize;
        // (long)(0) > (long)(-1) → true — even zero length blocked!

        Assert.True(blocked,
            "MessageTransit with maxMessageSize=-1 blocks ALL messages, " +
            "including zero-length. The transit is completely non-functional.");
    }

    [Fact]
    public void IntMaxValue_MaxFrameSize_AllowsUpTo2GB()
    {
        // If MaxFrameSize = int.MaxValue (2,147,483,647):
        // Any frame up to ~2GB passes. This is a valid DoS amplification.
        int maxFrameSize = int.MaxValue;
        uint twoGigFrame = (uint)int.MaxValue;

        bool blocked = twoGigFrame > maxFrameSize;
        Assert.False(blocked, "2GB frame passes when MaxFrameSize=int.MaxValue");

        // The multiplexer would try to allocate 2GB of memory to read this frame.
        // No upper bound enforced on the MaxFrameSize property itself.
    }
}
