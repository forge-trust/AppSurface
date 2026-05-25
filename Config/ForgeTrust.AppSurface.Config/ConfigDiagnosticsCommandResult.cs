namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes the outcome of a configuration diagnostics command runner invocation.
/// </summary>
/// <remarks>
/// A successful result means the audit report was generated and written. It does not mean every audited entry resolved
/// successfully: entries with <see cref="ConfigAuditEntryState.Missing"/> or
/// <see cref="ConfigAuditEntryState.Invalid"/> are still part of a successful inspection run. Command wrappers should
/// treat <see cref="ExitCode"/> as the process result for this v1 inspect-only surface.
/// </remarks>
public sealed class ConfigDiagnosticsCommandResult
{
    private ConfigDiagnosticsCommandResult(string? environment, ConfigDiagnosticsCommandFailure? failure)
    {
        Environment = environment;
        Failure = failure;
    }

    /// <summary>
    /// Gets the active AppSurface environment that was audited, when it was available.
    /// </summary>
    public string? Environment { get; }

    /// <summary>
    /// Gets the display-safe failure details when diagnostics could not run.
    /// </summary>
    public ConfigDiagnosticsCommandFailure? Failure { get; }

    /// <summary>
    /// Gets a value indicating whether the report was generated and written.
    /// </summary>
    public bool Succeeded => Failure == null;

    /// <summary>
    /// Gets the recommended process exit code for a command wrapper.
    /// </summary>
    public int ExitCode => Succeeded ? 0 : 1;

    internal static ConfigDiagnosticsCommandResult Success(string environment) => new(environment, null);

    internal static ConfigDiagnosticsCommandResult Failed(
        string? environment,
        string problem,
        string cause,
        string fix,
        Exception? exception = null)
    {
        return new ConfigDiagnosticsCommandResult(
            environment,
            new ConfigDiagnosticsCommandFailure(
                problem,
                cause,
                fix,
                exception?.GetType().Name));
    }
}
