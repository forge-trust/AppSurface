namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>A diagnostic-only projection of a failed generated first-work test's known phase and error category.</summary>
/// <param name="Phase">A closed phase from the reviewed first-work fixture.</param>
/// <param name="Code">A known exception category, or Other; never an arbitrary exception type name.</param>
/// <param name="Cleanup">Known cleanup observations, or unknown when the marker cannot establish one.</param>
/// <remarks>
/// This projection never establishes success or changes test, command, timing or coverage requirements.
/// Raw messages, stack locations, arguments and process output remain in owned scratch and are not retained.
/// </remarks>
internal sealed record DurableTemplateFirstWorkDiagnostic(string Phase, string Code, string Cleanup)
{
    private const string Prefix = "[FirstDurableWork] failure phase=";
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        "postgresql-fixture-setup", "host-read-only-startup", "read-only-startup-catalog", "initial-readiness",
        "typed-work-acceptance", "authorization-before-wake-body", "authenticated-work-activation",
        "persisted-terminal-work", "healthy-readiness-transition", "sdk-export-flush",
    };
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "InvalidOperationException", "OperationCanceledException", "TaskCanceledException", "TimeoutException",
        "IOException", "UnauthorizedAccessException", "SocketException", "HttpRequestException",
        "PostgresException", "NpgsqlException", "EqualException", "TrueException", "FalseException",
        "IsTypeException", "NullException", "NotNullException", "SingleException", "ContainsException",
        "DoesNotContainException", "InRangeException", "EmptyException", "NotEmptyException",
        "MissingCheckpoint", "CleanupFailed",
    };

    /// <summary>Reads at most one known marker from bounded test output, withholding ambiguous or invalid markers.</summary>
    /// <param name="output">Output from a failed, identity-matched first-work test inside the bounded TRX document.</param>
    /// <returns>A closed diagnostic value, or null; absence never changes the failed test outcome.</returns>
    internal static DurableTemplateFirstWorkDiagnostic? Read(string? output)
    {
        if (string.IsNullOrEmpty(output) || output.Length > 1024 * 1024) return null;
        DurableTemplateFirstWorkDiagnostic? result = null;
        var markers = 0;
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.TrimStart(' ', '\t');
            if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (++markers != 1 || raw.Length > 4096) return null;
            var codeAt = line.IndexOf(" code=", Prefix.Length, StringComparison.Ordinal);
            var locationAt = codeAt < 0 ? -1 : line.IndexOf(" at=", codeAt + 6, StringComparison.Ordinal);
            var causeAt = locationAt < 0 ? -1 : line.IndexOf(" cause=", locationAt + 4, StringComparison.Ordinal);
            var cleanupAt = causeAt < 0 ? -1 : line.IndexOf("; cleanup=", causeAt + 7, StringComparison.Ordinal);
            var retryAt = cleanupAt < 0 ? -1 : line.IndexOf("; retry=", cleanupAt + 10, StringComparison.Ordinal);
            if (codeAt < 0 || locationAt < 0 || causeAt < 0 || cleanupAt < 0 || retryAt < 0) return null;
            var phase = line[Prefix.Length..codeAt];
            if (!Phases.Contains(phase)) return null;
            var code = line[(codeAt + 6)..locationAt];
            var cleanup = line[(cleanupAt + 10)..retryAt];
            result = new(phase, Codes.Contains(code) ? code : "Other", cleanup switch
            {
                "none" => "none",
                "owned-cleanup-unfinished" => "owned-cleanup-unfinished",
                "owned-cleanup-total-budget-exhausted" => "owned-cleanup-total-budget-exhausted",
                "owned-cleanup-unfinished,owned-cleanup-total-budget-exhausted" => "owned-cleanup-unfinished,owned-cleanup-total-budget-exhausted",
                _ => "unknown",
            });
        }
        return result;
    }
}
