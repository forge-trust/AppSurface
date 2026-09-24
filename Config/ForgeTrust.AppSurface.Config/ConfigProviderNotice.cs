namespace ForgeTrust.AppSurface.Config;

/// <summary>A display-safe, non-terminal explanation attached to a successfully resolved value.</summary>
/// <remarks>Automatic logging treats third-party prose as untrusted and emits only bounded identifiers and codes.</remarks>
public sealed class ConfigProviderNotice
{
    /// <summary>Creates a notice with complete operator guidance and no configuration values.</summary>
    /// <param name="code">The stable machine-readable code.</param>
    /// <param name="problem">The problem summary.</param>
    /// <param name="cause">The safe cause summary.</param>
    /// <param name="fix">The next action.</param>
    /// <param name="docs">The required documentation destination.</param>
    /// <param name="retryable">Whether retrying later can resolve the condition.</param>
    /// <exception cref="ArgumentException">A required text field is null, empty, or whitespace.</exception>
    public ConfigProviderNotice(string code, string problem, string cause, string fix, string docs, bool retryable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        ArgumentException.ThrowIfNullOrWhiteSpace(cause);
        ArgumentException.ThrowIfNullOrWhiteSpace(fix);
        ArgumentException.ThrowIfNullOrWhiteSpace(docs);
        Code = code;
        Problem = problem;
        Cause = cause;
        Fix = fix;
        Docs = docs;
        Retryable = retryable;
    }

    /// <summary>Gets the stable diagnostic code.</summary>
    public string Code { get; }
    /// <summary>Gets the safe problem summary.</summary>
    public string Problem { get; }
    /// <summary>Gets the safe cause summary.</summary>
    public string Cause { get; }
    /// <summary>Gets the repair action.</summary>
    public string Fix { get; }
    /// <summary>Gets the required documentation destination.</summary>
    public string Docs { get; }
    /// <summary>Gets whether retrying later can help.</summary>
    public bool Retryable { get; }

    /// <summary>Gets an approved native locator for built-in notice deduplication, never a value.</summary>
    internal string? SafeSourceIdentifier { get; init; }
}
