namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes a display-safe configuration provider condition that must stop lower-priority resolution.
/// </summary>
/// <remarks>
/// Providers use terminal diagnostics when returning <see langword="null"/> would be ambiguous. For example, a
/// provider can distinguish a true missing key, which may fall through, from an unavailable secret store, which should
/// not allow lower-priority files to mask the failure. Diagnostic text must not contain raw configuration values.
/// </remarks>
public sealed class ConfigProviderTerminalDiagnostic
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigProviderTerminalDiagnostic"/> class.
    /// </summary>
    /// <param name="code">A stable machine-readable diagnostic code.</param>
    /// <param name="problem">The operator-facing problem summary.</param>
    /// <param name="cause">The display-safe cause summary.</param>
    /// <param name="fix">The suggested next action.</param>
    /// <param name="docs">An optional documentation hint or URL.</param>
    /// <param name="retryable">A value indicating whether retrying later may resolve the problem.</param>
    public ConfigProviderTerminalDiagnostic(
        string code,
        string problem,
        string cause,
        string fix,
        string? docs,
        bool retryable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        ArgumentException.ThrowIfNullOrWhiteSpace(cause);
        ArgumentException.ThrowIfNullOrWhiteSpace(fix);

        Code = code;
        Problem = problem;
        Cause = cause;
        Fix = fix;
        Docs = string.IsNullOrWhiteSpace(docs) ? null : docs;
        Retryable = retryable;
    }

    /// <summary>
    /// Gets the stable machine-readable diagnostic code.
    /// </summary>
    public string Code { get; }

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
    /// Gets the optional documentation hint or URL.
    /// </summary>
    public string? Docs { get; }

    /// <summary>
    /// Gets a value indicating whether retrying the same lookup later may resolve the condition.
    /// </summary>
    public bool Retryable { get; }

    /// <summary>
    /// Formats this diagnostic for display without exposing raw configuration values.
    /// </summary>
    /// <returns>A multiline display-safe diagnostic string.</returns>
    public string ToDisplayString()
    {
        var lines = new List<string>
        {
            $"Code: {Code}",
            $"Problem: {Problem}",
            $"Cause: {Cause}",
            $"Fix: {Fix}"
        };

        if (!string.IsNullOrWhiteSpace(Docs))
        {
            lines.Add($"Docs: {Docs}");
        }

        lines.Add($"Retryable: {Retryable.ToString().ToLowerInvariant()}");

        return string.Join(Environment.NewLine, lines);
    }

    /// <inheritdoc />
    public override string ToString() => ToDisplayString();
}
