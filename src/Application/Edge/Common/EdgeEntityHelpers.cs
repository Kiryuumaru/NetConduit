using Application.Common;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Application.Edge.Common;

public static class EdgeEntityHelpers
{
    public static EdgeEntity Create(string name)
    {
        return new()
        {
            Name = name,
            Key = RandomHelpers.ByteArray(1024)
        };
    }

    public static string Encode(GetEdgeWithKeyDto edgeEntity)
    {
        return JsonSerializer.Serialize(edgeEntity, JsonSerializerExtension.CamelCaseNoIndentOption).Encode();
    }

    public static GetEdgeWithKeyDto Decode(string token)
    {
        var decoded = token.Decode();

        GetEdgeWithKeyDto edgeEntity;
        try
        {
            edgeEntity = JsonSerializer.Deserialize<GetEdgeWithKeyDto>(decoded, JsonSerializerExtension.CamelCaseNoIndentOption) ?? throw new Exception();
        }
        catch
        {
            throw new Exception("Invalid edge token");
        }

        return edgeEntity;
    }
}
