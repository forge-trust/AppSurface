namespace ForgeTrust.AppSurface.Aspire;

/// <summary>
/// Classifies the process-fatal exception families used by Aspire profile activation and testing cleanup paths.
/// </summary>
/// <remarks>
/// This deliberately narrow helper recognizes <see cref="OutOfMemoryException"/>, <see cref="StackOverflowException"/>,
/// and <see cref="AccessViolationException"/>. It is not a general exception-handling policy for AppSurface callers.
/// </remarks>
internal static class AspireExceptionUtilities
{
    /// <summary>
    /// Determines whether an exception is an <see cref="OutOfMemoryException"/>, <see cref="StackOverflowException"/>,
    /// or <see cref="AccessViolationException"/> that should propagate immediately.
    /// </summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns><see langword="true"/> for process-fatal exception types; otherwise <see langword="false"/>.</returns>
    internal static bool IsProcessFatal(Exception exception)
    {
        return exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
    }
}
