using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.StreamPipeline.Models;

public class CommandPipePayload<TCommand, TResponse>
{
    public required TCommand? Command { get; init; }

    public required TResponse? Response { get; init; }
}
