namespace NetConduit.Exceptions;

/// <summary>
/// Thrown when a server-side one-shot <c>StreamFactory</c> is invoked while
/// another accept is already in flight. Concurrent duplicate acceptance is a
/// structural contract violation, not a transient transport failure, so the
/// connect-retry loop treats it as permanently fatal and fails fast instead
/// of consuming the reconnect budget. Subclasses <see cref="InvalidOperationException"/>
/// so existing <c>catch (InvalidOperationException)</c> sites keep working.
/// </summary>
public sealed class ServerAcceptConflictException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
