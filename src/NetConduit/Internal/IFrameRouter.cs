namespace NetConduit.Internal;

internal interface IFrameRouter
{
    void NotifyReady(WriteChannel channel);
}
