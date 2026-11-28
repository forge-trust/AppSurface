namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Provides the shared fatal-exception classification used by Evidence execution adapters.</summary>
internal static class EvidenceExceptionPolicy
{
    /// <summary>Determines whether an exception may be handled as an ordinary execution failure.</summary>
    /// <param name="exception">The exception being classified.</param>
    /// <returns>False for out-of-memory, stack-overflow, access-violation, and unloaded-domain failures; true otherwise.</returns>
    /// <remarks>Fatal failures must propagate unchanged. Classification does not perform cleanup or handle corrupted process state.</remarks>
    internal static bool IsNonFatalException(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;
}
