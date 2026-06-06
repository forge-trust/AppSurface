using System.Diagnostics.CodeAnalysis;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Provides the discoverable root for AppSurface coverage commands.
/// </summary>
/// <remarks>
/// The root command keeps the coverage workflow visible in <c>appsurface --help</c>. The stable
/// v1 contract lives on <c>coverage gate</c>, which evaluates existing Cobertura coverage without
/// requiring a hosted coverage service. Execution orchestration commands stay out of the public
/// CLI surface until their package dependency and discovery boundaries are proven outside AppSurface.
/// </remarks>
[Command("coverage", Description = "Inspect AppSurface coverage commands for private CI quality gates.")]
internal sealed partial class CoverageCommand : ICommand
{
    /// <summary>
    /// Prints the coverage command family summary.
    /// </summary>
    /// <param name="console">CliFx console used for command output.</param>
    /// <returns>A completed task.</returns>
    [ExcludeFromCodeCoverage(Justification = "CliFx command discovery covers the root help path; subcommands carry behavior tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await console.Output.WriteLineAsync("Use 'appsurface coverage gate --help' to enforce a Cobertura coverage gate.");
    }
}
