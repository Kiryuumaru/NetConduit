namespace NetConduit.Internal;

internal static class ExceptionFilters
{
    public static bool IsFatal(Exception exception) => exception switch
    {
        OutOfMemoryException => true,
        AccessViolationException => true,
        StackOverflowException => true,
        ThreadAbortException => true,
        _ => false,
    };
}
