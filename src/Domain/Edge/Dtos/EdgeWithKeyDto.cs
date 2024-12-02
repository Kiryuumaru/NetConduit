using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Edge.Dtos;

public class EdgeWithKeyDto : EdgeInfoDto
{
    public required byte[] Key { get; init; }
}
