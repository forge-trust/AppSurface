using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Config;

namespace LocalSecretsExample;

[Command("config diagnostics", Description = "Prints the active AppSurface configuration audit report.")]
public sealed partial class ConfigDiagnosticsCommand(ConfigDiagnosticsCommandRunner runner) : ICommand
{
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
