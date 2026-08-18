using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Previews or explicitly removes stale AppSurface coverage artifacts.
/// </summary>
/// <remarks>
/// Without <c>--all</c>, this command uses the AppSurface coverage ownership marker to remove only known artifacts
/// from the selected coverage output directory. <c>--all</c> is an intentional broader maintenance operation that
/// scans a worktree for every directory named <c>TestResults</c>. Both modes preview by default and require
/// <c>--apply</c> before changing the filesystem.
/// </remarks>
[Command("coverage clean", Description = "Preview or explicitly clean AppSurface coverage artifacts; use --all for every TestResults directory.")]
internal sealed partial class CoverageCleanCommand(
    TestResultsCleanupWorkflow testResultsWorkflow,
    Func<string>? getCurrentDirectory = null) : ICommand
{
    private static readonly string DefaultOutputDirectory = Path.Join("TestResults", "coverage-merged");
    private readonly TestResultsCleanupWorkflow _testResultsWorkflow = testResultsWorkflow ?? throw new ArgumentNullException(nameof(testResultsWorkflow));
    private readonly Func<string> _getCurrentDirectory = getCurrentDirectory ?? Directory.GetCurrentDirectory;

    /// <summary>
    /// Gets or sets the AppSurface coverage output directory cleaned by the default mode.
    /// </summary>
    [CommandOption("output", Description = "AppSurface coverage output directory. Defaults to TestResults/coverage-merged; cannot be used with --all.")]
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every descendant <c>TestResults</c> directory should be included.
    /// </summary>
    /// <remarks>
    /// This is broader than the default ownership-marker mode. It is intended for reclaiming disk space from a
    /// bounded local worktree and honors <see cref="RootDirectory"/>.
    /// </remarks>
    [CommandOption("all", Description = "Scan for every TestResults directory below --root instead of only AppSurface-owned coverage artifacts.")]
    public bool All { get; set; }

    /// <summary>
    /// Gets or sets the scan root used only by <c>--all</c>.
    /// </summary>
    [CommandOption("root", Description = "Existing worktree directory scanned by --all. Defaults to the current directory and is never deleted.")]
    public string? RootDirectory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the previewed artifacts may be deleted.
    /// </summary>
    [CommandOption("apply", Description = "Delete the selected coverage artifacts or TestResults directories. Omit to preview without changing files.")]
    public bool Apply { get; set; }

    /// <inheritdoc />
    [ExcludeFromCodeCoverage(Justification = "The cancellation-registration adapter delegates to the token-aware overload covered by tests.")]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await ExecuteAsync(console, console.RegisterCancellationHandler());
    }

    /// <summary>
    /// Executes the cleanup with an explicit cancellation token.
    /// </summary>
    /// <param name="console">Console used for user-visible output.</param>
    /// <param name="cancellationToken">Cancellation token observed during filesystem traversal.</param>
    /// <returns>A task that completes after the preview or deletion summary is written.</returns>
    internal async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (All)
        {
            if (OutputDirectory is not null)
            {
                throw new CommandException("--output cannot be used with --all. Omit --output to scan TestResults directories below --root.");
            }

            await _testResultsWorkflow.CleanAsync(
                new TestResultsCleanupRequest(RootDirectory, Apply),
                console,
                cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(RootDirectory))
        {
            throw new CommandException("--root is available only with --all. Omit --root to clean AppSurface-owned coverage artifacts, or pass --all to scan a worktree.");
        }

        var outputDirectory = OutputDirectory ?? Path.Join(_getCurrentDirectory(), DefaultOutputDirectory);
        var result = CoverageRunOutputGuard.CleanExistingOwnedOutput(outputDirectory, Apply);
        await WriteOwnedCoverageResultAsync(console, result, cancellationToken);
    }

    private static async Task WriteOwnedCoverageResultAsync(
        IConsole console,
        CoverageOwnedCleanupResult result,
        CancellationToken cancellationToken)
    {
        await console.Output.WriteLineAsync($"Coverage output: {result.OutputDirectory}");
        if (!result.OutputExists)
        {
            await console.Output.WriteLineAsync("No coverage output directory exists. Nothing to clean.");
            return;
        }

        if (!result.IsOwned)
        {
            await console.Output.WriteLineAsync("No AppSurface-owned coverage artifacts were found. Nothing to clean.");
            return;
        }

        await console.Output.WriteLineAsync(
            $"Found {result.Artifacts.Count.ToString(CultureInfo.InvariantCulture)} AppSurface coverage {Pluralize("artifact", result.Artifacts.Count)}.");
        foreach (var artifact in result.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await console.Output.WriteLineAsync($"  {artifact}");
        }

        await console.Output.WriteLineAsync(
            result.Applied
                ? $"Removed {result.Artifacts.Count.ToString(CultureInfo.InvariantCulture)} AppSurface coverage {Pluralize("artifact", result.Artifacts.Count)}."
                : "Preview only. Re-run with --apply to delete the listed artifacts.");
    }

    private static string Pluralize(string singular, int count) => count == 1 ? singular : singular + "s";
}
