using Domain.Edge.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Dtos;

public class AddEdgeDto
{
    public required EdgeType EdgeType { get; init; }

    public required string Name { get; init; }
}
