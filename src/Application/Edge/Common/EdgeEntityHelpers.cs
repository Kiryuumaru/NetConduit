using Application.Common;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.Edge.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Application.Edge.Common;

public static class EdgeEntityHelpers
{
    public static EdgeEntity Create(string id, string name)
    {
        return new()
        {
            Id = id,
            Name = name,
            Key = RandomHelpers.ByteArray(1024)
        };
    }

    public static EdgeConnection Encode(EdgeEntity edgeEntity)
    {
        return new EdgeConnection()
        {
            Id = edgeEntity.Id,
            Name = edgeEntity.Name,
            Key = edgeEntity.Key,
            Token = JsonSerializer.Serialize(edgeEntity, JsonSerializerExtension.CamelCaseNoIndentOption).Encode()
        };
    }

    public static EdgeConnection Decode(string token)
    {
        var decoded = token.Decode();

        EdgeEntity edgeEntity = JsonSerializer.Deserialize<EdgeEntity>(decoded, JsonSerializerExtension.CamelCaseNoIndentOption)
            ?? throw new Exception("Invalid handshakeToken");

        return new EdgeConnection()
        {
            Id = edgeEntity.Id,
            Name = edgeEntity.Name,
            Key = edgeEntity.Key,
            Token = JsonSerializer.Serialize(edgeEntity, JsonSerializerExtension.CamelCaseNoIndentOption).Encode()
        };
    }
}
