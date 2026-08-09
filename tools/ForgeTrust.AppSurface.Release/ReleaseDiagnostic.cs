namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Diagnostic envelope with a stable code and reader-actionable context.
/// </summary>
internal sealed record ReleaseDiagnostic(
    string Severity,
    string Code,
    string Problem,
    string Cause,
    string Fix,
    string Docs)
{
    /// <summary>
    /// Creates an error diagnostic.
    /// </summary>
    internal static ReleaseDiagnostic Error(string code, string problem, string cause, string fix, string docs)
    {
        return new ReleaseDiagnostic("error", code, problem, cause, fix, docs);
    }

    /// <summary>
    /// Creates a warning diagnostic.
    /// </summary>
    internal static ReleaseDiagnostic Warning(string code, string problem, string cause, string fix, string docs)
    {
        return new ReleaseDiagnostic("warning", code, problem, cause, fix, docs);
    }

    /// <summary>
    /// Creates the diagnostic emitted when append-only unreleased entries cannot be composed.
    /// </summary>
    /// <param name="cause">Specific entry or template validation failure.</param>
    /// <returns>Stable diagnostic for callers of release check and preparation.</returns>
    internal static ReleaseDiagnostic InvalidUnreleasedEntry(string cause) => Error(
        "release-unreleased-entry-invalid",
        "The append-only unreleased entry set cannot be composed.",
        cause,
        "Use one correctly named entry file with an exact supported section directive, and keep exactly one marker for every section in releases/unreleased.md.",
        "releases/README.md#append-only-unreleased-entries");

    /// <summary>
    /// Renders the diagnostic envelope for CLI stderr.
    /// </summary>
    /// <returns>Human-readable diagnostic envelope.</returns>
    internal string Render()
    {
        return $"""
            Severity: {Severity}
            Code: {Code}
            Problem: {Problem}
            Cause: {Cause}
            Fix: {Fix}
            Docs: {Docs}
            """;
    }
}

/// <summary>
/// Exception that carries a structured release diagnostic.
/// </summary>
internal sealed class ReleaseToolException : Exception
{
    /// <summary>
    /// Creates a release exception.
    /// </summary>
    /// <param name="diagnostic">Diagnostic to render to users.</param>
    internal ReleaseToolException(ReleaseDiagnostic diagnostic)
        : base(diagnostic.Problem)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>
    /// Gets the structured diagnostic.
    /// </summary>
    internal ReleaseDiagnostic Diagnostic { get; }
}
