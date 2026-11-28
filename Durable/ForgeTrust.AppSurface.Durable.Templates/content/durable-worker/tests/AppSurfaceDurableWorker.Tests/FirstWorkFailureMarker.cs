namespace DurableWorkerTemplate.Tests;

/// <summary>Formats the first-work fixture's failure observation for the bounded diagnostic reader.</summary>
/// <remarks>
/// This marker describes a failure; it cannot authorize a successful proof. Callers retain the original
/// failure and cleanup checks. The repository tests link this authored formatter to verify the reader contract.
/// </remarks>
internal static class FirstWorkFailureMarker
{
    /// <summary>Formats one failure marker without changing its phase, code, cleanup or retry instructions.</summary>
    /// <param name="phase">The fixture phase that failed.</param>
    /// <param name="code">The original failure category, including the stable Docker prerequisite category.</param>
    /// <param name="location">Method-only stack context, or null when unavailable.</param>
    /// <param name="cause">A closed prerequisite cause, or null for the ordinary assertion explanation.</param>
    /// <param name="cleanup">The fixture's observed cleanup result.</param>
    /// <param name="retry">Instructions for rerunning the same proof.</param>
    /// <param name="documentation">The canonical first-proof documentation link.</param>
    /// <returns>One diagnostic marker; its reader independently validates and projects closed fields.</returns>
    internal static string Format(string phase, string code, string? location, string? cause,
        string cleanup, string retry, string documentation) =>
        $"[FirstDurableWork] failure phase={phase} code={code} at={location ?? "unknown"} cause={cause ?? "The bounded phase did not satisfy its assertion or owned cleanup contract"}; cleanup={cleanup}; retry={retry}; docs={documentation}";
}
