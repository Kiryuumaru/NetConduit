namespace NetConduit;

/// <summary>
/// Constants for frame encoding.
/// </summary>
public static class FrameConstants
{
    /// <summary>Size of the frame header in bytes.</summary>
    public const int HeaderSize = 9;
    
    /// <summary>Default maximum frame payload size (16MB).</summary>
    public const int DefaultMaxFrameSize = 16 * 1024 * 1024;
}
