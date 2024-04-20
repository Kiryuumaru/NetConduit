using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Client.Dtos;

public class ClientEditDto
{
    public string? NewName { get; init; }

    public bool RenewToken { get; init; }
}
