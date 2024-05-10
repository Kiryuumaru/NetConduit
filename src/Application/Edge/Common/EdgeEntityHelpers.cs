using Application.Common;
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
    public static EdgeConnectionEntity Encode(EdgeEntity edgeEntity)
    {
        if (string.IsNullOrEmpty(edgeEntity.Token))
        {
            return new EdgeConnectionEntity()
            {
                Id = edgeEntity.Id,
                Name = edgeEntity.Name,
                Token = edgeEntity.Token,
                HandshakeToken = ""
            };
        }

        return new EdgeConnectionEntity()
        {
            Id = edgeEntity.Id,
            Name = edgeEntity.Name,
            Token = edgeEntity.Token,
            HandshakeToken = JsonSerializer.Serialize(edgeEntity, JsonSerializerExtension.CamelCaseOption).Encode()
        };
    }

    public static EdgeConnectionEntity Decode(string handshakeToken)
    {
        var decoded = handshakeToken.Decode();

        EdgeEntity edgeEntity = JsonSerializer.Deserialize<EdgeEntity>(decoded, JsonSerializerExtension.CamelCaseOption)
            ?? throw new Exception("Invalid handshakeToken");

        return new EdgeConnectionEntity()
        {
            Id = edgeEntity.Id,
            Name = edgeEntity.Name,
            Token = edgeEntity.Token,
            HandshakeToken = handshakeToken
        };
    }
}
