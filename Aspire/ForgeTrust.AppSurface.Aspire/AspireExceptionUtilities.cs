namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Classifies exceptions that indicate process-level failure and must not be intercepted by recovery or cleanup paths.
/// </summary>
internal static class AspireExceptionUtilities
{
    /// <summary>
    /// Determines whether an exception represents a process-level failure that should propagate immediately.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns><see langword="true"/> for process-fatal exception types; otherwise <see langword="false"/>.</returns>
    internal static bool IsProcessFatal(Exception exception)
    {
        return exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
    }
}
