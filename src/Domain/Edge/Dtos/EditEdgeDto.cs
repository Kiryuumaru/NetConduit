using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Dtos;

public class EditEdgeDto
{
    public string? NewName { get; init; }

    public bool RenewToken { get; init; }
}
