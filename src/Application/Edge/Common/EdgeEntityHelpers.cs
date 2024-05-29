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
    public static EdgeTokenEntity GenerateToken(EdgeEntity edgeEntity)
    {
        return new()
        {
            Id = edgeEntity.Id,
            Name = edgeEntity.Name,
            Token = StringEncoder.Random(50)
        };
    }

    public static EdgeConnectionEntity Encode(EdgeTokenEntity edgeEntity)
    {
        if (string.IsNullOrEmpty(edgeEntity.Token))
        {
            return new EdgeConnectionEntity()
            {
                Id = edgeEntity.Id,
                Name = edgeEntity.Name,
                HandshakeToken = ""
            };
        }

        return new EdgeConnectionEntity()
        {
            Id = edgeEntity.Id,
            Name = edgeEntity.Name,
            HandshakeToken = JsonSerializer.Serialize(edgeEntity, JsonSerializerExtension.CamelCaseNoIndentOption).Encode()
        };
    }

    public static EdgeConnectionEntity Decode(string handshakeToken)
    {
        var decoded = handshakeToken.Decode();

        EdgeTokenEntity edgeEntity = JsonSerializer.Deserialize<EdgeTokenEntity>(decoded, JsonSerializerExtension.CamelCaseNoIndentOption)
            ?? throw new Exception("Invalid handshakeToken");

        return new EdgeConnectionEntity()
        {
            Id = edgeEntity.Id,
            Name = edgeEntity.Name,
            HandshakeToken = handshakeToken
        };
    }
}
