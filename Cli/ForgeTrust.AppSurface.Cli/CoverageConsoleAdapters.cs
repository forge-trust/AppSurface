using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Adapts the CliFx console to the private coverage-core writer boundary.
/// </summary>
/// <remarks>
/// Command handlers use the same writer pair directly. These overloads retain the intentional internal test seam
/// for existing coverage workflow tests while ensuring the core itself has no dependency on CliFx.
/// </remarks>
internal static class CoverageConsoleAdapters
{
    /// <summary>
    /// Runs coverage using the output and error writers from a CliFx console.
    /// </summary>
    public static Task<CoverageRunResult> RunAsync(
        this CoverageRunWorkflow workflow,
        CoverageRunRequest request,
        IConsole console,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(console);
        return workflow.RunAsync(request, CoverageTextWriters.Create(console.Output, console.Error), cancellationToken);
    }

    /// <summary>
    /// Merges coverage using the output and error writers from a CliFx console.
    /// </summary>
    public static Task<CoverageMergeResult> MergeAsync(
        this CoverageMergeWorkflow workflow,
        CoverageMergeRequest request,
        IConsole console,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(console);
        return workflow.MergeAsync(request, CoverageTextWriters.Create(console.Output, console.Error), cancellationToken);
    }
}
