namespace NetConduit.Internal;

internal static class SafeEventRaiser
{
    // Invokes each target on the multicast delegate in its own try/catch so one
    // throwing handler cannot prevent the others from running. Fatal exceptions
    // (OOM, AV, StackOverflow, ThreadAbort) propagate. Non-fatal exceptions are
    // routed to onHandlerException when provided; passing null disables routing
    // and is the correct choice when raising an exception-reporting event itself
    // (preventing recursion).
    public static void Raise<T>(
        object sender,
        EventHandler<T>? handler,
        T args,
        Action<Exception>? onHandlerException)
        where T : EventArgs
    {
        if (handler is null) return;
        foreach (var target in handler.GetInvocationList())
        {
            try { ((EventHandler<T>)target).Invoke(sender, args); }
            catch (Exception ex) when (!ExceptionFilters.IsFatal(ex))
            {
                onHandlerException?.Invoke(ex);
            }
        }
    }

    public static void Raise(
        object sender,
        EventHandler? handler,
        Action<Exception>? onHandlerException)
    {
        if (handler is null) return;
        foreach (var target in handler.GetInvocationList())
        {
            try { ((EventHandler)target).Invoke(sender, EventArgs.Empty); }
            catch (Exception ex) when (!ExceptionFilters.IsFatal(ex))
            {
                onHandlerException?.Invoke(ex);
            }
        }
    }
}
