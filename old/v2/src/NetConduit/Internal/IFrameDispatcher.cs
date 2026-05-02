namespace NetConduit.Internal;

internal interface IFrameDispatcher
{
    ReadChannel? LookupReadChannel(uint channelIndex);
    void ProcessFrame(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken ct);
    void OnReadLoopComplete();
    void OnReadLoopError(Exception ex);
}
