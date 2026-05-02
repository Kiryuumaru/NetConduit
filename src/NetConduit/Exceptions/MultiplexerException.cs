namespace NetConduit;

/// <summary>
/// Thrown for protocol-level multiplexer errors.
/// </summary>
public sealed class MultiplexerException(ErrorCode errorCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The protocol error code.</summary>
    public ErrorCode ErrorCode { get; } = errorCode;
}
