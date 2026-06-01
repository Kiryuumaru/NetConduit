using System.Buffers.Binary;
using NetConduit.Constants;
using NetConduit.Enums;
using NetConduit.Exceptions;
using NetConduit.Interfaces;

namespace NetConduit.Internal;

/// <summary>
/// Wire-level handshake protocol for <see cref="StreamMultiplexer"/>.
/// Owns the initial and reconnect handshake forms, their cross-acceptance
/// branches, and the I/O framing primitives used to read handshake frames
/// from a transport. Carries no shared state with the multiplexer's loops
/// or lifecycle; all dynamic input is passed as parameters.
/// </summary>
internal static class MuxHandshake
{
    // Wire format:
    //   Initial   : [sessionId : 16B][maxRecvPayload : 4B big-endian uint32]
    //               = 20B
    //   Reconnect : [CtrlSubtype.Reconnect : 1B][sessionId : 16B]
    //               [maxRecvPayload : 4B big-endian uint32]
    //               [channelCount : 4B big-endian uint32]
    //               [channelIndex : 4B BE][frameBytesReceived : 8B BE] * channelCount
    //               = 25B header + 12B per channel entry
    //
    // The 4-byte max-recv-payload tail advertises the largest single
    // frame payload this peer will accept on any inbound channel — equal to
    // its DefaultChannelOptions.SlabSize. The remote clamps every WriteAsync
    // against this so a heterogeneous slab configuration cannot send a frame
    // the receiver's slab cannot buffer (which previously crashed the reader
    // loop with MultiplexerException(ProtocolError) and burned reconnect
    // attempts replaying the same oversize frame).
    //
    // The per-channel replay position vector advertises each side's
    // FrameBytesReceived for every active inbound channel so the writer can
    // rewind its replay base to the byte the peer actually delivered. Without
    // it, a lost ACK frame causes the writer's _ackedPos to lag the reader's
    // true receive position, and the post-reconnect replay duplicate-delivers
    // bytes to ReadAsync.
    internal const int InitialPayloadLength = 20;
    private const int MaxRecvPayloadFieldSize = 4;
    // Reconnect payload header = [subtype:1][sessionId:16][maxRecvPayload:4][channelCount:uint32-BE].
    internal const int ReconnectHeaderLength = 25;
    // Per-channel position entry = [channelIndex:uint32-BE][frameBytesReceived:uint64-BE].
    internal const int ReconnectChannelEntrySize = 12;
    // Defensive upper bound for any handshake frame the read path will allocate for.
    // Bounds reconnect replay metadata so a malformed peer cannot force
    // unbounded allocation before protocol validation runs.
    internal const int MaxPayloadLength = 1 << 20;

    /// <summary>
    /// Result of the initial handshake: the remote peer's session id, the
    /// index-parity selection (higher session id gets odd indices), and the
    /// remote peer's advertised maximum receive payload.
    /// </summary>
    internal readonly record struct InitialResult(Guid RemoteSessionId, bool UseOddIndices, int PeerMaxRecvPayload);

    /// <summary>
    /// Result of the reconnect handshake: the remote peer's currently
    /// advertised maximum receive payload (re-negotiated on every reconnect
    /// since the peer may have restarted with different options).
    /// </summary>
    internal readonly record struct ReconnectResult(int PeerMaxRecvPayload);

    /// <summary>
    /// Sends the initial handshake on <paramref name="transport"/>, awaits the
    /// remote handshake frame, derives the remote session id and the
    /// index-parity selection. Cross-acceptance: if the remote responds with
    /// a reconnect frame instead (the remote may have observed the previous
    /// completion before the route failed while this side did not), the
    /// session id carried by that reconnect frame is accepted.
    /// </summary>
    internal static async Task<InitialResult> PerformInitialAsync(
        IStreamPair transport,
        Guid localSessionId,
        int localMaxRecvPayload,
        CancellationToken ct)
    {
        ValidateLocalMaxRecvPayload(localMaxRecvPayload);

        byte[] handshake = new byte[FrameHeader.Size + InitialPayloadLength];
        FrameHeader.WriteTo(handshake, ChannelConstants.ControlChannel, FrameFlags.Ctrl, InitialPayloadLength);
        localSessionId.TryWriteBytes(handshake.AsSpan(FrameHeader.Size));
        BinaryPrimitives.WriteUInt32BigEndian(
            handshake.AsSpan(FrameHeader.Size + 16, MaxRecvPayloadFieldSize),
            (uint)localMaxRecvPayload);

        FrameHeader remoteHeader;
        byte[] remotePayload;
        try
        {
            await transport.WriteStream.WriteAsync(handshake, ct);
            await transport.WriteStream.FlushAsync(ct);
            (remoteHeader, remotePayload) = await ReadHandshakeFrameAsync(transport.ReadStream, ct);
        }
        catch (HandshakeTransportException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new HandshakeTransportException("Transport failed during initial handshake.", ex);
        }

        Guid remoteSessionId;
        int peerMaxRecvPayload;
        if (IsInitialFrame(remoteHeader))
        {
            remoteSessionId = new Guid(remotePayload.AsSpan(0, 16));
            peerMaxRecvPayload = ReadPeerMaxRecvPayload(remotePayload.AsSpan(16, MaxRecvPayloadFieldSize));
        }
        else if (IsReconnectFrame(remoteHeader, remotePayload))
        {
            // A peer can complete the first handshake and lose the route before this side
            // receives its response. The next route is reconnect for that peer and initial
            // for this peer, so both handshake forms must converge on the same session.
            remoteSessionId = new Guid(remotePayload.AsSpan(1, 16));
            peerMaxRecvPayload = ReadPeerMaxRecvPayload(remotePayload.AsSpan(17, MaxRecvPayloadFieldSize));
        }
        else
        {
            throw new MultiplexerException(ErrorCode.ProtocolError, "Invalid handshake from remote.");
        }

        // Determine odd/even index allocation based on session ID comparison
        // Higher session ID gets odd indices
        bool useOdd = localSessionId.CompareTo(remoteSessionId) > 0;
        return new InitialResult(remoteSessionId, useOdd, peerMaxRecvPayload);
    }

    /// <summary>
    /// Sends a reconnect handshake on <paramref name="transport"/>, awaits the
    /// remote reconnect frame, and verifies the carried session id matches
    /// <paramref name="expectedRemoteSessionId"/>. Cross-acceptance: if the
    /// remote responds with an initial-handshake frame, it is accepted only
    /// when its session id matches the established peer (i.e. the remote did
    /// not observe the prior handshake's completion before the route failed).
    ///
    /// The reconnect payload carries a re-negotiated max-recv-payload
    /// and a per-channel position vector advertising each side's
    /// <c>FrameBytesReceived</c> for every active inbound channel;
    /// <paramref name="applyRemotePositions"/> is invoked with the remote's
    /// vector so the local writer can rewind its replay base to the byte
    /// the peer actually delivered. Initial frames carry no positions; in
    /// that branch <paramref name="applyRemotePositions"/> is invoked with
    /// an empty list.
    /// </summary>
    internal static async Task<ReconnectResult> PerformReconnectAsync(
        IStreamPair transport,
        Guid localSessionId,
        Guid expectedRemoteSessionId,
        int localMaxRecvPayload,
        IReadOnlyList<ChannelReplayPosition> localPositions,
        Action<IReadOnlyList<ChannelReplayPosition>> applyRemotePositions,
        CancellationToken ct)
    {
        ValidateLocalMaxRecvPayload(localMaxRecvPayload);

        // Symmetric reconnect: both sides send Reconnect, both read Reconnect.
        // Same pattern as initial handshake (send session ID, read session ID), plus
        // re-negotiated max-recv-payload and a per-channel position vector
        //  that lets each side rewind its writer's replay base to the peer's
        // actually-received position.
        int maxReplayPositions = (MaxPayloadLength - ReconnectHeaderLength) / ReconnectChannelEntrySize;
        if (localPositions.Count > maxReplayPositions)
            throw new MultiplexerException(ErrorCode.Internal, "Too many channels for reconnect handshake.");

        int payloadLength = ReconnectHeaderLength + localPositions.Count * ReconnectChannelEntrySize;
        byte[] reconnectPayload = new byte[payloadLength];
        reconnectPayload[0] = CtrlSubtype.Reconnect;
        localSessionId.TryWriteBytes(reconnectPayload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(
            reconnectPayload.AsSpan(17, MaxRecvPayloadFieldSize),
            (uint)localMaxRecvPayload);
        BinaryPrimitives.WriteUInt32BigEndian(reconnectPayload.AsSpan(21, 4), (uint)localPositions.Count);

        int offset = ReconnectHeaderLength;
        for (int i = 0; i < localPositions.Count; i++)
        {
            var pos = localPositions[i];
            BinaryPrimitives.WriteUInt32BigEndian(reconnectPayload.AsSpan(offset, 4), pos.ChannelIndex);
            BinaryPrimitives.WriteUInt64BigEndian(reconnectPayload.AsSpan(offset + 4, 8), (ulong)pos.FrameBytesReceived);
            offset += ReconnectChannelEntrySize;
        }

        byte[] frame = new byte[FrameHeader.Size + reconnectPayload.Length];
        FrameHeader.WriteTo(frame, ChannelConstants.ControlChannel, FrameFlags.Ctrl, reconnectPayload.Length);
        reconnectPayload.CopyTo(frame.AsSpan(FrameHeader.Size));

        FrameHeader remoteHeader;
        byte[] remotePayload;
        try
        {
            await transport.WriteStream.WriteAsync(frame, ct);
            await transport.WriteStream.FlushAsync(ct);
            (remoteHeader, remotePayload) = await ReadHandshakeFrameAsync(transport.ReadStream, ct);
        }
        catch (HandshakeTransportException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new HandshakeTransportException("Transport failed during reconnect handshake.", ex);
        }

        Guid remoteSession;
        int peerMaxRecvPayload;
        IReadOnlyList<ChannelReplayPosition> remotePositions;
        if (IsReconnectFrame(remoteHeader, remotePayload))
        {
            remoteSession = new Guid(remotePayload.AsSpan(1, 16));
            peerMaxRecvPayload = ReadPeerMaxRecvPayload(remotePayload.AsSpan(17, MaxRecvPayloadFieldSize));
            remotePositions = ParseReplayPositions(remotePayload);
        }
        else if (IsInitialFrame(remoteHeader))
        {
            // The remote peer may not have observed the first handshake completion before
            // the route failed, while this side did. Treat the duplicate initial handshake
            // as reconnect only when the session matches the established peer. No replay
            // positions are present in an initial handshake — peer's writer starts fresh.
            remoteSession = new Guid(remotePayload.AsSpan(0, 16));
            peerMaxRecvPayload = ReadPeerMaxRecvPayload(remotePayload.AsSpan(16, MaxRecvPayloadFieldSize));
            remotePositions = Array.Empty<ChannelReplayPosition>();
        }
        else
        {
            throw new MultiplexerException(ErrorCode.ProtocolError, "Invalid reconnect frame.");
        }

        if (remoteSession != expectedRemoteSessionId)
            throw new MultiplexerException(ErrorCode.SessionMismatch, "Remote session ID mismatch on reconnect.");

        applyRemotePositions(remotePositions);
        return new ReconnectResult(peerMaxRecvPayload);
    }

    private static void ValidateLocalMaxRecvPayload(int localMaxRecvPayload)
    {
        if (localMaxRecvPayload < FrameConstants.MinSlabSize || localMaxRecvPayload > FrameConstants.MaxSlabSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localMaxRecvPayload),
                localMaxRecvPayload,
                $"Local max-recv-payload must be between {FrameConstants.MinSlabSize} and {FrameConstants.MaxSlabSize} bytes.");
        }
    }

    private static int ReadPeerMaxRecvPayload(ReadOnlySpan<byte> field)
    {
        uint raw = BinaryPrimitives.ReadUInt32BigEndian(field);
        // Reject pathological values from a misbehaving peer so the local
        // WriteAsync clamp never produces a negative or unbounded budget.
        // The honest-peer trust model (#scope.md) means we treat out-of-range
        // values as a protocol error rather than a security boundary.
        if (raw < FrameConstants.MinSlabSize || raw > FrameConstants.MaxSlabSize)
        {
            throw new MultiplexerException(
                ErrorCode.ProtocolError,
                $"Peer advertised an out-of-range max-recv-payload ({raw} bytes); must be between {FrameConstants.MinSlabSize} and {FrameConstants.MaxSlabSize}.");
        }
        return (int)raw;
    }

    private static IReadOnlyList<ChannelReplayPosition> ParseReplayPositions(ReadOnlySpan<byte> reconnectPayload)
    {
        if (reconnectPayload.Length < ReconnectHeaderLength)
            return Array.Empty<ChannelReplayPosition>();

        uint rawChannelCount = BinaryPrimitives.ReadUInt32BigEndian(reconnectPayload.Slice(21, 4));
        if (rawChannelCount > int.MaxValue)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Reconnect payload channel count is too large.");

        int channelCount = (int)rawChannelCount;
        long expectedLength = ReconnectHeaderLength + (long)channelCount * ReconnectChannelEntrySize;
        if (reconnectPayload.Length != expectedLength)
            throw new MultiplexerException(ErrorCode.ProtocolError, "Reconnect payload length does not match channel count.");

        if (channelCount == 0)
            return Array.Empty<ChannelReplayPosition>();

        var positions = new ChannelReplayPosition[channelCount];
        int offset = ReconnectHeaderLength;
        for (int i = 0; i < channelCount; i++)
        {
            uint channelIndex = BinaryPrimitives.ReadUInt32BigEndian(reconnectPayload.Slice(offset, 4));
            ulong peerReceivedPosition = BinaryPrimitives.ReadUInt64BigEndian(reconnectPayload.Slice(offset + 4, 8));
            offset += ReconnectChannelEntrySize;
            positions[i] = new ChannelReplayPosition(channelIndex, (long)peerReceivedPosition);
        }
        return positions;
    }

    private static bool IsInitialFrame(FrameHeader header)
    {
        return header.ChannelIndex == ChannelConstants.ControlChannel
            && header.Flags == FrameFlags.Ctrl
            && header.PayloadLength == InitialPayloadLength;
    }

    private static bool IsReconnectFrame(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.ChannelIndex != ChannelConstants.ControlChannel
            || header.Flags != FrameFlags.Ctrl
            || header.PayloadLength != payload.Length
            || payload.Length < ReconnectHeaderLength
            || payload[0] != CtrlSubtype.Reconnect)
        {
            return false;
        }

        // Trailing bytes after the 25-byte header must be an integral number of
        // [channelIndex:4][frameBytesReceived:8] entries that match the advertised count.
        int trailing = payload.Length - ReconnectHeaderLength;
        if (trailing % ReconnectChannelEntrySize != 0)
            return false;

        uint channelCount = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(21, 4));
        return channelCount <= int.MaxValue && trailing / ReconnectChannelEntrySize == channelCount;
    }

    private static async Task<(FrameHeader Header, byte[] Payload)> ReadHandshakeFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] headerBuffer = new byte[FrameHeader.Size];
        await ReadExactAsync(stream, headerBuffer, ct);
        var header = FrameHeader.Parse(headerBuffer);
        // Initial handshake is fixed-size; reconnect handshake is variable due to the
        // per-channel position vector, bounded by MaxPayloadLength to defend against
        // a malformed/hostile peer driving an unbounded allocation.
        if (header.PayloadLength != InitialPayloadLength
            && (header.PayloadLength < ReconnectHeaderLength
                || header.PayloadLength > MaxPayloadLength))
        {
            return (header, []);
        }

        byte[] payload = new byte[header.PayloadLength];
        await ReadExactAsync(stream, payload, ct);
        return (header, payload);
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0)
                throw new HandshakeTransportException(
                    "Transport stream closed before the handshake completed.",
                    new EndOfStreamException("Transport stream closed unexpectedly."));
            totalRead += read;
        }
    }
}
