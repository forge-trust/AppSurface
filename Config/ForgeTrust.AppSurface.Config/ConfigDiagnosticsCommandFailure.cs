namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes a display-safe failure from <see cref="ConfigDiagnosticsCommandRunner"/>.
/// </summary>
/// <remarks>
/// Failures intentionally omit raw exception messages because provider and reporter exceptions can contain attempted
/// configuration values, file paths, or other support-sensitive details. Command wrappers should display these fields
/// instead of printing the original exception.
/// </remarks>
public sealed class ConfigDiagnosticsCommandFailure
{
    internal ConfigDiagnosticsCommandFailure(
        string problem,
        string cause,
        string fix,
        string? exceptionType)
    {
        Problem = problem;
        Cause = cause;
        Fix = fix;
        ExceptionType = exceptionType;
    }

    /// <summary>
    /// Gets the operator-facing problem summary.
    /// </summary>
    public string Problem { get; }

    /// <summary>
    /// Gets the display-safe cause summary.
    /// </summary>
    public string Cause { get; }

    /// <summary>
    /// Gets the suggested next action.
    /// </summary>
    public string Fix { get; }

    /// <summary>
    /// Gets the exception type that caused the failure, when available.
    /// </summary>
    /// <remarks>
    /// The type name is retained for support triage while the raw exception message is intentionally omitted.
    /// </remarks>
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
            $"Fix: {Fix}"
        };

        if (!string.IsNullOrWhiteSpace(ExceptionType))
        {
            lines.Add($"Exception type: {ExceptionType}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
