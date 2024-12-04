using Application.Common.Extensions;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.Edge.Enums;
using System.Text;
using System.Text.Json;

namespace Application.Edge.Extensions;

public static class EdgeEntityHelpers
{
    public static EdgeEntity Create(string name, EdgeType edgeType, Guid? id = null, byte[]? key = null)
    {
        return new()
        {
            Id = id ?? Guid.NewGuid(),
            EdgeType = edgeType,
            Name = name,
            Key = key ?? Encoding.ASCII.GetBytes(RandomHelpers.Alphanumeric(EdgeDefaults.EdgeKeySize))
        };
    }
}
