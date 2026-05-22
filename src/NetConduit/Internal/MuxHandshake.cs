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
    //   Initial   : [sessionId : 16B][maxRecvPayload : 4B big-endian uint32]                              = 20B
    //   Reconnect : [CtrlSubtype.Reconnect : 1B][sessionId : 16B][maxRecvPayload : 4B big-endian uint32]  = 21B
    //
    // The 4-byte max-recv-payload tail (#180) advertises the largest single
    // frame payload this peer will accept on any inbound channel — equal to
    // its DefaultChannelOptions.SlabSize. The remote clamps every WriteAsync
    // against this so a heterogeneous slab configuration cannot send a frame
    // the receiver's slab cannot buffer (which previously crashed the reader
    // loop with MultiplexerException(ProtocolError) and burned reconnect
    // attempts replaying the same oversize frame).
    internal const int InitialPayloadLength = 20;
    internal const int ReconnectPayloadLength = 21;
    private const int MaxRecvPayloadFieldSize = 4;

    /// <summary>
    /// Result of the initial handshake: the remote peer's session id, the
    /// index-parity selection (higher session id gets odd indices), and the
    /// remote peer's advertised maximum receive payload.
    /// </summary>
    internal readonly record struct InitialResult(Guid RemoteSessionId, bool UseOddIndices, int PeerMaxRecvPayload);

    /// <summary>
    /// Result of the reconnect handshake: confirmation that the remote
    /// session id matched, plus the remote peer's currently advertised
    /// maximum receive payload (re-negotiated on every reconnect since the
    /// peer may have restarted with different options).
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
    /// </summary>
    internal static async Task<ReconnectResult> PerformReconnectAsync(
        IStreamPair transport,
        Guid localSessionId,
        Guid expectedRemoteSessionId,
        int localMaxRecvPayload,
        CancellationToken ct)
    {
        ValidateLocalMaxRecvPayload(localMaxRecvPayload);

        // Symmetric reconnect: both sides send Reconnect, both read Reconnect.
        // Same pattern as initial handshake (send session ID, read session ID).

        // Send reconnect frame: [CtrlSubtype.Reconnect][sessionId:16B][maxRecvPayload:4B]
        byte[] reconnectPayload = new byte[ReconnectPayloadLength];
        reconnectPayload[0] = CtrlSubtype.Reconnect;
        localSessionId.TryWriteBytes(reconnectPayload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(
            reconnectPayload.AsSpan(17, MaxRecvPayloadFieldSize),
            (uint)localMaxRecvPayload);

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
        if (IsReconnectFrame(remoteHeader, remotePayload))
        {
            remoteSession = new Guid(remotePayload.AsSpan(1, 16));
            peerMaxRecvPayload = ReadPeerMaxRecvPayload(remotePayload.AsSpan(17, MaxRecvPayloadFieldSize));
        }
        else if (IsInitialFrame(remoteHeader))
        {
            // The remote peer may not have observed the first handshake completion before
            // the route failed, while this side did. Treat the duplicate initial handshake
            // as reconnect only when the session matches the established peer.
            remoteSession = new Guid(remotePayload.AsSpan(0, 16));
            peerMaxRecvPayload = ReadPeerMaxRecvPayload(remotePayload.AsSpan(16, MaxRecvPayloadFieldSize));
        }
        else
        {
            throw new MultiplexerException(ErrorCode.ProtocolError, "Invalid reconnect frame.");
        }

        if (remoteSession != expectedRemoteSessionId)
            throw new MultiplexerException(ErrorCode.SessionMismatch, "Remote session ID mismatch on reconnect.");

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

    private static bool IsInitialFrame(FrameHeader header)
    {
        return header.ChannelIndex == ChannelConstants.ControlChannel
            && header.Flags == FrameFlags.Ctrl
            && header.PayloadLength == InitialPayloadLength;
    }

    private static bool IsReconnectFrame(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        return header.ChannelIndex == ChannelConstants.ControlChannel
            && header.Flags == FrameFlags.Ctrl
            && header.PayloadLength == ReconnectPayloadLength
            && payload.Length == ReconnectPayloadLength
            && payload[0] == CtrlSubtype.Reconnect;
    }

    private static async Task<(FrameHeader Header, byte[] Payload)> ReadHandshakeFrameAsync(Stream stream, CancellationToken ct)
    {
        byte[] headerBuffer = new byte[FrameHeader.Size];
        await ReadExactAsync(stream, headerBuffer, ct);
        var header = FrameHeader.Parse(headerBuffer);
        if (header.PayloadLength is not (InitialPayloadLength or ReconnectPayloadLength))
            return (header, []);

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
