using Application.Common.Extensions;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Domain.Edge.Enums;
using System.Text.Json;

namespace Application.Edge.Extensions;

public static class EdgeEntityHelpers
{
    public static EdgeEntity Create(string name, EdgeType edgeType)
    {
        return new()
        {
            EdgeType = edgeType,
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
