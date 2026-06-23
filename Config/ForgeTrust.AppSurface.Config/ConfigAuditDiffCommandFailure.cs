namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Identifies the stage where <see cref="ConfigAuditDiffCommandRunner"/> could not complete.
/// </summary>
public enum ConfigAuditDiffFailureStage
{
    /// <summary>The baseline report could not be built or parsed.</summary>
    Baseline = 0,

    /// <summary>The target report could not be built or parsed.</summary>
    Target = 1,

    /// <summary>A captured JSON snapshot could not be parsed.</summary>
    SnapshotParse = 2,

    /// <summary>The typed diff comparison failed.</summary>
    Compare = 3,

    /// <summary>The diff report could not be rendered.</summary>
    Render = 4
}

/// <summary>
/// Describes a display-safe failure from <see cref="ConfigAuditDiffCommandRunner"/>.
/// </summary>
/// <remarks>
/// Failures intentionally omit raw exception messages because provider, parser, and renderer exceptions can contain
/// attempted values, file paths, or environment-specific details. Command wrappers should display these fields instead
/// of printing the original exception.
/// </remarks>
public sealed class ConfigAuditDiffCommandFailure
{
    internal ConfigAuditDiffCommandFailure(
        ConfigAuditDiffFailureStage stage,
        string problem,
        string cause,
        string fix,
        string docsLink,
        string? exceptionType)
    {
        Stage = stage;
        Problem = problem;
        Cause = cause;
        Fix = fix;
        DocsLink = docsLink;
        ExceptionType = exceptionType;
    }

    /// <summary>Gets the failure stage.</summary>
    public ConfigAuditDiffFailureStage Stage { get; }

    /// <summary>Gets the operator-facing problem summary.</summary>
    public string Problem { get; }

    /// <summary>Gets the display-safe cause summary.</summary>
    public string Cause { get; }

    /// <summary>Gets the suggested next action.</summary>
    public string Fix { get; }

    /// <summary>Gets the documentation link for this workflow.</summary>
    public string DocsLink { get; }

    /// <summary>Gets the exception type that caused the failure, when available.</summary>
    public string? ExceptionType { get; }

    /// <summary>
    /// Formats this failure for command-line display.
    /// </summary>
    /// <returns>A display-safe multiline failure message.</returns>
    public string ToDisplayString()
    {
        var lines = new List<string>
        {
            $"Problem: {Problem}",
            $"Cause: {Cause}",
            $"Fix: {Fix}",
            $"Docs: {DocsLink}",
            $"Stage: {Stage}"
        };

        if (!string.IsNullOrWhiteSpace(ExceptionType))
        {
            lines.Add($"Exception type: {ExceptionType}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
