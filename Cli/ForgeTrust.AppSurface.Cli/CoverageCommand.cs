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
/// v1 contract includes <c>coverage run</c>, which executes instrumented .NET test projects,
/// <c>coverage merge</c>, which fans in existing Cobertura shards, and <c>coverage gate</c>, which
/// evaluates the merged coverage without requiring a hosted coverage service.
/// </remarks>
[Command("coverage", Description = "Inspect AppSurface coverage commands for local coverage execution and threshold enforcement.")]
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
        await console.Output.WriteLineAsync("Use 'appsurface coverage run --help' to produce Cobertura coverage, 'appsurface coverage merge --help' to fan in existing shards, then 'appsurface coverage gate --help' to enforce thresholds.");
    }
}
