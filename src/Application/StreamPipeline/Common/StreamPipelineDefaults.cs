using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Common;

public static class StreamPipelineDefaults
{
    public const int StreamMultiplexerChunkSize = 4096;
    //public const int StreamMultiplexerChunkSize = 16384;
    //public const int StreamMultiplexerChunkSize = 32768;

    public const int EdgeCommsBufferSize = 16384;

    public static readonly Guid CommandChannelKey = new("00000000-0000-0000-0000-000000000001");
}
