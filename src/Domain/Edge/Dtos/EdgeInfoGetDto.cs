using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Dtos;

public class EdgeInfoGetDto
{
    public required string Id { get; init; }

    public required string Name { get; init; }
}
