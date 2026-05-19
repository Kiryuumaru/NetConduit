namespace NetConduit.Internal;

internal sealed class HandshakeTransportException(string message, Exception innerException)
    : IOException(message, innerException);
