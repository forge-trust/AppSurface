using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Executes child processes for the export pipeline while preserving a structured
/// <see cref="ProcessResult"/> contract for callers.
/// </summary>
/// <remarks>
/// This abstraction exists so resolver logic can verify command composition
/// without launching real processes in tests. Callers should prefer it whenever
/// they need stdout, stderr, exit code, and cancellation behavior surfaced in a
/// consistent shape.
/// </remarks>
internal interface ICommandExecutor
{
    /// <summary>
    /// Executes a command and captures its exit code, standard output, and
    /// standard error.
    /// </summary>
    /// <param name="fileName">The executable to start.</param>
    /// <param name="args">The ordered command-line arguments passed to <paramref name="fileName"/>.</param>
    /// <param name="workingDirectory">The working directory used for process start.</param>
    /// <param name="cancellationToken">Cancels the launched process and any in-flight output reads.</param>
    /// <returns>
    /// A <see cref="ProcessResult"/> whose <c>ExitCode</c>, <c>Stdout</c>, and
    /// <c>Stderr</c> describe the completed command or a start-up failure.
    /// </returns>
    /// <remarks>
    /// Implementations should avoid throwing for ordinary process start failures
    /// so higher-level callers can decide whether to surface an exception,
    /// retry, or fall back. Cancellation should still propagate via
    /// <see cref="OperationCanceledException"/>.
    /// </remarks>
    Task<ProcessResult> ExecuteCommandAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken);
}
