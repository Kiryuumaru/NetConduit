using Domain.Common.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Entities;

public class EdgeEntity : BaseEntity
{
    public required string Name { get; init; }

    public required byte[] Key { get; init; }
}
