using Application.StreamPipeline.Common;

namespace Application.StreamPipeline.Interfaces;

public interface ISecureStreamFactory
{
    TranceiverStream CreateSecureTranceiverStream(int capacity);
}
