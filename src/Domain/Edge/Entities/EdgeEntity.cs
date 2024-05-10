using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Entities;

public class EdgeEntity
{
    public required string Id { get; init; }

    public required string Name { get; init; }
}
