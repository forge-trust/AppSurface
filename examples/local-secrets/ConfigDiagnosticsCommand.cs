using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;

namespace LocalSecretsExample;

[Command("config diagnostics", Description = "Prints the active AppSurface configuration audit report.")]
public sealed partial class ConfigDiagnosticsCommand(ConfigDiagnosticsCommandRunner runner) : ICommand
{
    /// <summary>
    /// Gets or sets whether diagnostics expand bounded known-entry collections while preserving redaction.
    /// </summary>
    [CommandOption("debug")] public bool Debug { get; set; }

    /// <summary>
    /// Executes the diagnostics command against the already-selected AppSurface host environment.
    /// </summary>
    /// <param name="console">The CliFx console whose output writer receives the rendered audit report.</param>
    /// <returns>A completed value task when the report renders successfully.</returns>
    /// <exception cref="CommandException">
    /// Thrown with sanitized diagnostics text when the runner cannot produce a report.
    /// </exception>
    public ValueTask ExecuteAsync(IConsole console)
    {
        var result = runner.Run(
            console.Output,
            Debug
                ? ConfigAuditReportMode.ExpandKnownEntryCollections
                : ConfigAuditReportMode.Default);
        if (!result.Succeeded)
        {
            throw new CommandException(result.Failure?.ToDisplayString() ?? "Configuration diagnostics failed.");
        }

        return default;
    }
}
