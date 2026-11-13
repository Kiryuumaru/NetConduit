namespace NetConduit.Mesh.Internal;

/// <summary>
/// Validation helpers for mesh identifiers (node IDs, pool IDs, multiplexer IDs).
/// </summary>
internal static class Identifiers
{
    /// <summary>Maximum allowed length for any mesh identifier (node, pool, multiplexer).</summary>
    internal const int MaxLength = 256;

    /// <summary>Validate a node identifier. Throws <see cref="ArgumentException"/> if invalid.</summary>
    internal static void ValidateNodeId(string value, string paramName)
    {
        Validate(value, paramName, "Node ID");
    }

    /// <summary>Validate a pool identifier.</summary>
    internal static void ValidatePoolId(string value, string paramName)
    {
        Validate(value, paramName, "Pool ID");
    }

    /// <summary>Validate a multiplexer identifier opened on the mesh.</summary>
    internal static void ValidateMultiplexerId(string value, string paramName)
    {
        Validate(value, paramName, "Multiplexer ID");
    }

    private static void Validate(string value, string paramName, string label)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);

        if (value.Length == 0)
        {
            throw new ArgumentException($"{label} must not be empty.", paramName);
        }

        if (value.Length > MaxLength)
        {
            throw new ArgumentException($"{label} must not exceed {MaxLength} characters.", paramName);
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c < 0x20 || c == 0x7F)
            {
                throw new ArgumentException($"{label} must not contain control characters.", paramName);
            }

            switch (c)
            {
                case ':':
                case '/':
                case '<':
                case '>':
                    throw new ArgumentException($"{label} must not contain ':', '/', '<' or '>'.", paramName);
            }
        }
    }
}
