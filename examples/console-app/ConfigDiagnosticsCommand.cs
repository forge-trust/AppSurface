using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;

namespace ConsoleAppExample;

/// <summary>
/// Renders the active AppSurface configuration audit report to the console.
/// </summary>
/// <param name="runner">The console-agnostic diagnostics runner registered by the config module.</param>
/// <remarks>
/// This app-local CliFx command keeps command discovery, console output, and command-framework failure mapping in the
/// consuming app while reusing Config's redacted audit renderer. The class is partial so CliFx 3 can generate the
/// descriptor AppSurface Console discovers at startup.
/// </remarks>
[Command("config diagnostics", Description = "Prints the active AppSurface configuration audit report.")]
public sealed partial class ConfigDiagnosticsCommand(ConfigDiagnosticsCommandRunner runner) : ICommand
{
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
        var result = runner.Run(console.Output);
        if (!result.Succeeded)
        {
            throw new CommandException(result.Failure?.ToDisplayString() ?? "Configuration diagnostics failed.");
        }

        return default;
    }
}
