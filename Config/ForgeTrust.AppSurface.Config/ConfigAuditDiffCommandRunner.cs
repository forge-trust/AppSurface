using System.Text.Json;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Runs command-framework-agnostic config audit diff workflows.
/// </summary>
/// <remarks>
/// The runner belongs to the config package and intentionally has no dependency on CliFx, AppSurface Console, or any
/// command-specific abstractions. Apps can expose a command that either compares two named environments from the
/// already-built host or compares consumer-owned captured JSON snapshots. Non-fatal workflow failures are converted to
/// display-safe <see cref="ConfigAuditDiffCommandFailure"/> objects with problem, cause, fix, and documentation fields.
/// </remarks>
public sealed class ConfigAuditDiffCommandRunner
{
    private const string DocsLink = "Config/ForgeTrust.AppSurface.Config/README.md#config-diff-in-10-minutes";

    private readonly IConfigAuditReporter _reporter;
    private readonly ConfigAuditReportDiffer _differ;
    private readonly ConfigAuditDiffTextRenderer _renderer;

    /// <summary>
    /// Creates a config audit diff runner.
    /// </summary>
    /// <param name="reporter">The reporter used for same-host named-environment comparisons.</param>
    /// <param name="differ">The pure typed report differ.</param>
    /// <param name="renderer">The deterministic text renderer for diff output.</param>
    public ConfigAuditDiffCommandRunner(
        IConfigAuditReporter reporter,
        ConfigAuditReportDiffer differ,
        ConfigAuditDiffTextRenderer renderer)
    {
        _reporter = reporter;
        _differ = differ;
        _renderer = renderer;
    }

    /// <summary>
    /// Compares two named environments by asking the current host's reporter for both reports.
    /// </summary>
    /// <param name="baselineEnvironment">The baseline environment name.</param>
    /// <param name="targetEnvironment">The target environment name.</param>
    /// <param name="output">The writer that receives rendered diff text.</param>
    /// <param name="options">Diff options. The default evidence mode warns that this is same-host evidence.</param>
    /// <returns>A command-runner result describing success or display-safe failure details.</returns>
    /// <remarks>
    /// This workflow is convenient for operator triage, but it reuses the already-built host and should not be treated
    /// as proof that two deployed hosts have identical provider inputs. Prefer <see cref="RunCapturedSnapshots"/> for
    /// support evidence collected from each environment.
    /// </remarks>
    public ConfigAuditDiffCommandResult Run(
        string baselineEnvironment,
        string targetEnvironment,
        TextWriter output,
        ConfigAuditDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (string.IsNullOrWhiteSpace(baselineEnvironment))
        {
            return CreateInputFailure(
                null,
                targetEnvironment,
                ConfigAuditDiffFailureStage.Baseline,
                "The baseline environment name was empty.",
                "Pass a non-empty baseline environment name.");
        }

        if (string.IsNullOrWhiteSpace(targetEnvironment))
        {
            return CreateInputFailure(
                baselineEnvironment,
                null,
                ConfigAuditDiffFailureStage.Target,
                "The target environment name was empty.",
                "Pass a non-empty target environment name.");
        }

        ConfigAuditReport baseline;
        try
        {
            baseline = _reporter.GetReport(baselineEnvironment);
        }
        catch (Exception ex) when (IsNonFatalDiffFailure(ex))
        {
            return CreateFailure(
                baselineEnvironment,
                targetEnvironment,
                ConfigAuditDiffFailureStage.Baseline,
                "Config audit diff could not build the baseline report.",
                "The baseline report path failed before sanitized comparison evidence was available.",
                "Run the baseline environment's config diagnostics and inspect application logs for the underlying provider or host failure.",
                ex);
        }

        ConfigAuditReport target;
        try
        {
            target = _reporter.GetReport(targetEnvironment);
        }
        catch (Exception ex) when (IsNonFatalDiffFailure(ex))
        {
            return CreateFailure(
                baselineEnvironment,
                targetEnvironment,
                ConfigAuditDiffFailureStage.Target,
                "Config audit diff could not build the target report.",
                "The target report path failed before sanitized comparison evidence was available.",
                "Run the target environment's config diagnostics and inspect application logs for the underlying provider or host failure.",
                ex);
        }

        return WriteDiff(baseline, target, output, options);
    }

    /// <summary>
    /// Compares two captured JSON audit report snapshots.
    /// </summary>
    /// <param name="baselineSnapshotJson">The baseline JSON snapshot.</param>
    /// <param name="targetSnapshotJson">The target JSON snapshot.</param>
    /// <param name="output">The writer that receives rendered diff text.</param>
    /// <param name="options">Diff options. Defaults are used when this value is <see langword="null"/>.</param>
    /// <param name="jsonOptions">Optional JSON serializer options for consumers that captured reports with custom settings.</param>
    /// <returns>A command-runner result describing success or display-safe failure details.</returns>
    /// <remarks>
    /// Captured snapshots are a consumer-owned v1 workflow. Capture each sanitized <see cref="ConfigAuditReport"/> from
    /// the host it describes, store it according to your support-bundle policy, and pass the JSON snapshots here when a
    /// command wrapper wants stronger evidence than same-host named-environment comparison.
    /// </remarks>
    public ConfigAuditDiffCommandResult RunCapturedSnapshots(
        string baselineSnapshotJson,
        string targetSnapshotJson,
        TextWriter output,
        ConfigAuditDiffOptions? options = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        ConfigAuditReport baseline;
        try
        {
            baseline = ParseSnapshot(baselineSnapshotJson, "baseline", jsonOptions);
        }
        catch (Exception ex) when (IsNonFatalDiffFailure(ex))
        {
            return CreateFailure(
                null,
                null,
                ConfigAuditDiffFailureStage.SnapshotParse,
                "Config audit diff could not parse the baseline snapshot.",
                $"The baseline snapshot parse path failed with {ex.GetType().Name}.",
                "Verify the snapshot is a sanitized ConfigAuditReport JSON document captured from IConfigAuditReporter.",
                ex);
        }

        ConfigAuditReport target;
        try
        {
            target = ParseSnapshot(targetSnapshotJson, "target", jsonOptions);
        }
        catch (Exception ex) when (IsNonFatalDiffFailure(ex))
        {
            return CreateFailure(
                baseline.Environment,
                null,
                ConfigAuditDiffFailureStage.SnapshotParse,
                "Config audit diff could not parse the target snapshot.",
                $"The target snapshot parse path failed with {ex.GetType().Name}.",
                "Verify the snapshot is a sanitized ConfigAuditReport JSON document captured from IConfigAuditReporter.",
                ex);
        }

        var diffOptions = new ConfigAuditDiffOptions
        {
            EvidenceMode = ConfigAuditDiffEvidenceMode.CapturedSnapshot,
            IncludeUnchangedItems = options?.IncludeUnchangedItems ?? false,
            SourceDetail = options?.SourceDetail ?? ConfigAuditDiffSourceDetail.Summarized
        };

        return WriteDiff(baseline, target, output, diffOptions);
    }

    private ConfigAuditDiffCommandResult WriteDiff(
        ConfigAuditReport baseline,
        ConfigAuditReport target,
        TextWriter output,
        ConfigAuditDiffOptions? options)
    {
        ConfigAuditDiffReport diff;
        try
        {
            diff = _differ.Compare(baseline, target, options);
        }
        catch (Exception ex) when (IsNonFatalDiffFailure(ex))
        {
            return CreateFailure(
                baseline.Environment,
                target.Environment,
                ConfigAuditDiffFailureStage.Compare,
                "Config audit diff could not compare the two reports.",
                $"The typed comparison path failed with {ex.GetType().Name}.",
                "Verify both inputs are sanitized ConfigAuditReport instances with stable report shape, then retry.",
                ex);
        }

        try
        {
            output.Write(_renderer.Render(diff));
        }
        catch (Exception ex) when (IsNonFatalDiffFailure(ex))
        {
            return CreateFailure(
                baseline.Environment,
                target.Environment,
                ConfigAuditDiffFailureStage.Render,
                "Config audit diff could not render the comparison.",
                $"The renderer path failed with {ex.GetType().Name}.",
                "Inspect the typed ConfigAuditDiffReport directly or retry with summarized source detail.",
                ex);
        }

        return ConfigAuditDiffCommandResult.Success(baseline.Environment, target.Environment);
    }

    private static ConfigAuditReport ParseSnapshot(
        string snapshotJson,
        string role,
        JsonSerializerOptions? jsonOptions)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            throw new ArgumentException($"The {role} snapshot JSON was empty.", nameof(snapshotJson));
        }

        var report = JsonSerializer.Deserialize<ConfigAuditReport>(snapshotJson, jsonOptions);
        return report ?? throw new JsonException($"The {role} snapshot did not contain a report object.");
    }

    private static ConfigAuditDiffCommandResult CreateInputFailure(
        string? baselineEnvironment,
        string? targetEnvironment,
        ConfigAuditDiffFailureStage stage,
        string cause,
        string fix) =>
        ConfigAuditDiffCommandResult.Failed(
            baselineEnvironment,
            targetEnvironment,
            stage,
            "Config audit diff could not start.",
            cause,
            fix,
            DocsLink);

    private static ConfigAuditDiffCommandResult CreateFailure(
        string? baselineEnvironment,
        string? targetEnvironment,
        ConfigAuditDiffFailureStage stage,
        string problem,
        string cause,
        string fix,
        Exception exception) =>
        ConfigAuditDiffCommandResult.Failed(
            baselineEnvironment,
            targetEnvironment,
            stage,
            problem,
            cause,
            fix,
            DocsLink,
            exception);

    private static bool IsNonFatalDiffFailure(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException;
}
