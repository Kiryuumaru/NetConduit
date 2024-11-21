using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application.StreamPipeline.Models;

public class MessagingPipePayload<T>
{
    public required Guid MessageGuid { get; init; }

    public required T Message { get; init; }
}
