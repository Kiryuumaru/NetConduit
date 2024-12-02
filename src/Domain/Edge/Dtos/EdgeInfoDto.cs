using Domain.Edge.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Dtos;

public class EdgeInfoDto
{
    public required Guid Id { get; init; }

    public required EdgeType EdgeType { get; set; }

    public required string Name { get; init; }
}
