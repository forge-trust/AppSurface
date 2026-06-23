namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Describes the outcome of a config audit diff command runner invocation.
/// </summary>
/// <remarks>
/// A successful result means the diff report was produced and written. It does not mean the environments are equivalent:
/// changed, added, removed, uncomparable, and warning items remain part of a successful inspection run. Command wrappers
/// should use <see cref="ExitCode"/> only for process success or failure of the workflow itself.
/// </remarks>
public sealed class ConfigAuditDiffCommandResult
{
    private ConfigAuditDiffCommandResult(
        string? baselineEnvironment,
        string? targetEnvironment,
        ConfigAuditDiffCommandFailure? failure)
    {
        BaselineEnvironment = baselineEnvironment;
        TargetEnvironment = targetEnvironment;
        Failure = failure;
    }

    /// <summary>Gets the baseline environment when available.</summary>
    public string? BaselineEnvironment { get; }

    /// <summary>Gets the target environment when available.</summary>
    public string? TargetEnvironment { get; }

    /// <summary>Gets the display-safe failure details when the diff workflow could not run.</summary>
    public ConfigAuditDiffCommandFailure? Failure { get; }

    /// <summary>Gets a value indicating whether the diff was generated and written.</summary>
    public bool Succeeded => Failure == null;

    /// <summary>Gets the recommended process exit code for a command wrapper.</summary>
    public int ExitCode => Succeeded ? 0 : 1;

    internal static ConfigAuditDiffCommandResult Success(string baselineEnvironment, string targetEnvironment) =>
        new(baselineEnvironment, targetEnvironment, null);

    internal static ConfigAuditDiffCommandResult Failed(
        string? baselineEnvironment,
        string? targetEnvironment,
        ConfigAuditDiffFailureStage stage,
        string problem,
        string cause,
        string fix,
        string docsLink,
        Exception? exception = null)
    {
        return new ConfigAuditDiffCommandResult(
            baselineEnvironment,
            targetEnvironment,
            new ConfigAuditDiffCommandFailure(
                stage,
                problem,
                cause,
                fix,
                docsLink,
                exception?.GetType().Name));
    }
}
