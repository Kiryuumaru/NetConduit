using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Exceptions;

public class CorruptedHeaderBytesException : Exception
{
    public static CorruptedHeaderBytesException Instance { get; } = new();

    public CorruptedHeaderBytesException()
        : base("Corrupted header bytes received")
    {
        
    }
}
