using ForgeTrust.AppSurface.Config;

namespace ForgeTrust.AppSurface.Config.LocalSecrets;

/// <summary>
/// Describes a display-safe local secret diagnostic.
/// </summary>
/// <remarks>
/// Diagnostics are safe to render in command output, audit reports, and exception messages. They must never carry raw
/// secret values. Use <see cref="ToTerminalDiagnostic"/> when the diagnostic should stop lower-priority configuration
/// provider resolution.
/// </remarks>
public sealed class AppSurfaceLocalSecretDiagnostic
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceLocalSecretDiagnostic"/> class.
    /// </summary>
    /// <param name="code">Stable machine-readable diagnostic code.</param>
    /// <param name="problem">Display-safe problem summary.</param>
    /// <param name="cause">Display-safe cause summary.</param>
    /// <param name="fix">Suggested recovery action.</param>
    /// <param name="docs">Optional documentation hint or URL.</param>
    /// <param name="retryable">Whether retrying later may resolve the condition.</param>
    public AppSurfaceLocalSecretDiagnostic(
        string code,
        string problem,
        string cause,
        string fix,
        string? docs = null,
        bool retryable = false)
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
    /// Gets the display-safe problem summary.
    /// </summary>
    public string Problem { get; }

    /// <summary>
    /// Gets the display-safe cause summary.
    /// </summary>
    public string Cause { get; }

    /// <summary>
    /// Gets the suggested recovery action.
    /// </summary>
    public string Fix { get; }

    /// <summary>
    /// Gets an optional documentation hint or URL.
    /// </summary>
    public string? Docs { get; }

    /// <summary>
    /// Gets a value indicating whether retrying later may resolve the condition.
    /// </summary>
    public bool Retryable { get; }

    /// <summary>
    /// Converts this local secret diagnostic into a Config terminal diagnostic.
    /// </summary>
    /// <returns>A terminal diagnostic safe for runtime configuration resolution errors.</returns>
    public ConfigProviderTerminalDiagnostic ToTerminalDiagnostic() =>
        new(Code, Problem, Cause, Fix, Docs, Retryable);

    /// <summary>
    /// Formats this diagnostic for display without exposing secret values.
    /// </summary>
    /// <returns>A multiline display-safe diagnostic string.</returns>
    public string ToDisplayString() => ToTerminalDiagnostic().ToDisplayString();

    /// <inheritdoc />
    public override string ToString() => ToDisplayString();
}
