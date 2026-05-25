using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.AppSurface.Config;

/// <summary>
/// Runs the app-owned configuration diagnostics command workflow for the active AppSurface environment.
/// </summary>
/// <remarks>
/// This runner belongs to the config package and intentionally has no dependency on CliFx, AppSurface Console, or any
/// command-specific abstractions. Console applications should expose their own app-local command, pass that command's
/// output writer to <see cref="Run(TextWriter)"/>, and translate unsuccessful results into their command framework's
/// failure type.
///
/// V1 audits only <see cref="IEnvironmentProvider.Environment"/> from the already-built app host. It does not provide a
/// command-level environment override, does not enumerate raw unknown environment variables, and cannot rescue apps that
/// fail before the host and command service can run.
/// </remarks>
public sealed class ConfigDiagnosticsCommandRunner
{
    private const string RenderFailureProblem =
        "Configuration diagnostics could not render the active AppSurface configuration report.";

    private readonly IConfigAuditReporter _reporter;
    private readonly ConfigAuditTextRenderer _renderer;
    private readonly IEnvironmentProvider _environmentProvider;

    /// <summary>
    /// Creates a configuration diagnostics runner.
    /// </summary>
    /// <param name="reporter">The source-aware audit reporter for known AppSurface configuration entries.</param>
    /// <param name="renderer">The deterministic text renderer used for operator-facing output.</param>
    /// <param name="environmentProvider">The active AppSurface environment provider.</param>
    public ConfigDiagnosticsCommandRunner(
        IConfigAuditReporter reporter,
        ConfigAuditTextRenderer renderer,
        IEnvironmentProvider environmentProvider)
    {
        _reporter = reporter;
        _renderer = renderer;
        _environmentProvider = environmentProvider;
    }

    /// <summary>
    /// Writes the active environment's configuration audit report to <paramref name="output"/>.
    /// </summary>
    /// <param name="output">The writer that receives the rendered report.</param>
    /// <returns>
    /// A result whose <see cref="ConfigDiagnosticsCommandResult.ExitCode"/> is zero when the report was generated.
    /// Missing or invalid configuration entries remain successful inspection results.
    /// </returns>
    /// <remarks>
    /// Runtime failures are converted to display-safe failure details. Raw exception messages are not exposed because
    /// provider and validation exceptions can contain attempted secret values or environment-specific paths.
    /// </remarks>
    public ConfigDiagnosticsCommandResult Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string? environment = null;

        try
        {
            environment = _environmentProvider.Environment;
            if (string.IsNullOrWhiteSpace(environment))
            {
                return ConfigDiagnosticsCommandResult.Failed(
                    environment,
                    "Configuration diagnostics could not determine the active AppSurface environment.",
                    "The active environment provider returned an empty environment name.",
                    "Set the AppSurface host environment before running diagnostics.");
            }

            var report = _reporter.GetReport(environment);
            var rendered = _renderer.Render(report);
            output.Write(rendered);

            return ConfigDiagnosticsCommandResult.Success(environment);
        }
        catch (InvalidOperationException ex)
        {
            return ConfigDiagnosticsCommandResult.Failed(
                environment,
                RenderFailureProblem,
                $"The {ex.GetType().Name} path failed while building or rendering the report.",
                "Run diagnostics only after the app can build enough host and DI state to execute commands, then inspect application logs for the underlying failure.",
                ex);
        }
        catch (ArgumentException ex)
        {
            return ConfigDiagnosticsCommandResult.Failed(
                environment,
                RenderFailureProblem,
                $"The {ex.GetType().Name} path failed while building or rendering the report.",
                "Run diagnostics only after the app can build enough host and DI state to execute commands, then inspect application logs for the underlying failure.",
                ex);
        }
        catch (FormatException ex)
        {
            return ConfigDiagnosticsCommandResult.Failed(
                environment,
                RenderFailureProblem,
                $"The {ex.GetType().Name} path failed while building or rendering the report.",
                "Run diagnostics only after the app can build enough host and DI state to execute commands, then inspect application logs for the underlying failure.",
                ex);
        }
        catch (IOException ex)
        {
            return ConfigDiagnosticsCommandResult.Failed(
                environment,
                RenderFailureProblem,
                $"The {ex.GetType().Name} path failed while building or rendering the report.",
                "Run diagnostics only after the app can build enough host and DI state to execute commands, then inspect application logs for the underlying failure.",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ConfigDiagnosticsCommandResult.Failed(
                environment,
                RenderFailureProblem,
                $"The {ex.GetType().Name} path failed while building or rendering the report.",
                "Run diagnostics only after the app can build enough host and DI state to execute commands, then inspect application logs for the underlying failure.",
                ex);
        }
    }
}
