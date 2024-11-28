using Domain.Common.Models;
using Domain.Edge.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Entities;

public class EdgeEntity : BaseAuditableEntity
{
    public required string Name { get; set; }

    public required EdgeType EdgeType { get; set; }

    public required byte[] Key { get; set; }
}
