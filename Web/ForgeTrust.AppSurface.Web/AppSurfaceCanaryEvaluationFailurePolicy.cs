namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Classifies evaluator failures that package-owned HTTP and snapshot adapters can convert into safe failures.
/// </summary>
internal static class AppSurfaceCanaryEvaluationFailurePolicy
{
    /// <summary>Determines whether an evaluator failure can be converted into a safe package-owned failure response.</summary>
    /// <param name="exception">The exception raised during evaluator activation or execution.</param>
    /// <returns><see langword="false"/> for fatal process/runtime exceptions; otherwise <see langword="true"/>.</returns>
    internal static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException;
}
