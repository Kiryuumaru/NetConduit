using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Common;

public static class StreamPipelineDefaults
{
    //public const int StreamMultiplexerChunkSize = 256;
    //public const int StreamMultiplexerChunkSize = 512;
    public const int StreamMultiplexerChunkSize = 1024;
    //public const int StreamMultiplexerChunkSize = 2048;
    //public const int StreamMultiplexerChunkSize = 4096;
    //public const int StreamMultiplexerChunkSize = 8196;
    //public const int StreamMultiplexerChunkSize = 16384;
    //public const int StreamMultiplexerChunkSize = 32768;

    public const int StreamMultiplexerMaxPacketSize = 16777216;

    public const int MessagingPipeChunkSize = 1024;
    //public const int MessagingPipeChunkSize = 4096;

    public const int EdgeCommsBufferSize = 16384;
}
