using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Domain.Client.Entities;

public class ClientEntity
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Token { get; init; }
}
