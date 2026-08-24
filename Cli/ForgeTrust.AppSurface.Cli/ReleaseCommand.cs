using System.Diagnostics.CodeAnalysis;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Provides the discoverable root for consumer-owned release-note workflows.
/// </summary>
/// <remarks>
/// The public release surface deliberately handles the collaborative authoring problem only: independent work can add
/// isolated Markdown entries and one release owner can compose them deterministically. It does not create tags, publish
/// packages, mutate GitHub Releases, or impose AppSurface's repository-specific release policy on a consumer project.
/// </remarks>
[Command("release", Description = "Compose isolated release-note entries without concurrent edits to a shared changelog.")]
internal sealed partial class ReleaseCommand : ICommand
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage(Justification = "CliFx command discovery covers the root help path; the compose subcommand carries behavior tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await console.Output.WriteLineAsync("Use 'appsurface release compose --help' to validate and compose isolated release-note entries.");
    }
}
