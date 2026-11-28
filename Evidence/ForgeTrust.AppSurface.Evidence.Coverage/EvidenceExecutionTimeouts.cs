namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Defines the shared duration rules for cleanup and receipt acquisition.</summary>
internal static class EvidenceExecutionTimeouts
{
    /// <summary>Checks the grace period used to wait for owned producer work to settle.</summary>
    /// <param name="timeout">The cleanup grace period, which must be positive and at most one minute.</param>
    /// <returns>Whether the grace period is within bounds.</returns>
    internal static bool IsValidProducerCleanup(TimeSpan timeout)
        => timeout > TimeSpan.Zero && timeout <= TimeSpan.FromMinutes(1);

    /// <summary>Checks whether receipt acquisition has a finite, positive wait budget.</summary>
    /// <param name="timeout">The wait budget. The infinite sentinel is negative and fails the positive check.</param>
    /// <returns>Whether the wait budget is valid.</returns>
    internal static bool IsValidReceiptWait(TimeSpan timeout) => timeout > TimeSpan.Zero;
}
