using Domain.StreamPipeline.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.StreamPipeline.Models;

public class CommandPipePayload
{
    public required Guid CommandGuid { get; init; }

    public required CommandPipePayloadType PayloadType { get; init; }

    public required string RawPayload { get; init; }
}
