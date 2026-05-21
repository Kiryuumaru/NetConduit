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
    internal const int InitialPayloadLength = 16;
    internal const int ReconnectPayloadLength = 17;

    /// <summary>
    /// Result of the initial handshake: the remote peer's session id and the
    /// index-parity selection (higher session id gets odd indices).
    /// </summary>
    internal readonly record struct InitialResult(Guid RemoteSessionId, bool UseOddIndices);

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
        CancellationToken ct)
    {
        byte[] handshake = new byte[FrameHeader.Size + InitialPayloadLength];
        FrameHeader.WriteTo(handshake, ChannelConstants.ControlChannel, FrameFlags.Ctrl, InitialPayloadLength);
        localSessionId.TryWriteBytes(handshake.AsSpan(FrameHeader.Size));

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
        if (IsInitialFrame(remoteHeader))
        {
            remoteSessionId = new Guid(remotePayload.AsSpan(0, InitialPayloadLength));
        }
        else if (IsReconnectFrame(remoteHeader, remotePayload))
        {
            // A peer can complete the first handshake and lose the route before this side
            // receives its response. The next route is reconnect for that peer and initial
            // for this peer, so both handshake forms must converge on the same session.
            remoteSessionId = new Guid(remotePayload.AsSpan(1, InitialPayloadLength));
        }
        else
        {
            throw new MultiplexerException(ErrorCode.ProtocolError, "Invalid handshake from remote.");
        }

        // Determine odd/even index allocation based on session ID comparison
        // Higher session ID gets odd indices
        bool useOdd = localSessionId.CompareTo(remoteSessionId) > 0;
        return new InitialResult(remoteSessionId, useOdd);
    }

    /// <summary>
    /// Sends a reconnect handshake on <paramref name="transport"/>, awaits the
    /// remote reconnect frame, and verifies the carried session id matches
    /// <paramref name="expectedRemoteSessionId"/>. Cross-acceptance: if the
    /// remote responds with an initial-handshake frame, it is accepted only
    /// when its session id matches the established peer (i.e. the remote did
    /// not observe the prior handshake's completion before the route failed).
    /// </summary>
    internal static async Task PerformReconnectAsync(
        IStreamPair transport,
        Guid localSessionId,
        Guid expectedRemoteSessionId,
        CancellationToken ct)
    {
        // Symmetric reconnect: both sides send Reconnect, both read Reconnect.
        // Same pattern as initial handshake (send session ID, read session ID).

        // Send reconnect frame: [CtrlSubtype.Reconnect][sessionId:16B]
        byte[] reconnectPayload = new byte[ReconnectPayloadLength];
        reconnectPayload[0] = CtrlSubtype.Reconnect;
        localSessionId.TryWriteBytes(reconnectPayload.AsSpan(1));

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
        if (IsReconnectFrame(remoteHeader, remotePayload))
        {
            remoteSession = new Guid(remotePayload.AsSpan(1, InitialPayloadLength));
        }
        else if (IsInitialFrame(remoteHeader))
        {
            // The remote peer may not have observed the first handshake completion before
            // the route failed, while this side did. Treat the duplicate initial handshake
            // as reconnect only when the session matches the established peer.
            remoteSession = new Guid(remotePayload.AsSpan(0, InitialPayloadLength));
        }
        else
        {
            throw new MultiplexerException(ErrorCode.ProtocolError, "Invalid reconnect frame.");
        }

        if (remoteSession != expectedRemoteSessionId)
            throw new MultiplexerException(ErrorCode.SessionMismatch, "Remote session ID mismatch on reconnect.");
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
