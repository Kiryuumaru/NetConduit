namespace NetConduit.Enums;

/// <summary>
/// Operations for delta encoding in state synchronization.
/// </summary>
public enum DeltaOp : byte
{
    /// <summary>
    /// Set value (add or update property).
    /// </summary>
    Set = 0,

    /// <summary>
    /// Remove property from object.
    /// </summary>
    Remove = 1,

    /// <summary>
    /// Explicitly set property to null.
    /// </summary>
    SetNull = 2,

    /// <summary>
    /// Insert element at array index.
    /// </summary>
    ArrayInsert = 11,

    /// <summary>
    /// Remove element at array index.
    /// </summary>
    ArrayRemove = 12,

    /// <summary>
    /// Replace entire array (fallback when delta larger than full).
    /// </summary>
    ArrayReplace = 14
}
