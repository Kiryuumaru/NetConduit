namespace NetConduit;

/// <summary>
/// Exception thrown when a multiplexer protocol error occurs.
/// </summary>
public class MultiplexerException : Exception
{
    /// <summary>
    /// The error code associated with this exception.
    /// </summary>
    public ErrorCode ErrorCode { get; }

    /// <summary>
    /// Creates a new MultiplexerException with the specified error code and message.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="message">The error message.</param>
    public MultiplexerException(ErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Creates a new MultiplexerException with the specified error code, message, and inner exception.
    /// </summary>
    /// <param name="errorCode">The error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public MultiplexerException(ErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
