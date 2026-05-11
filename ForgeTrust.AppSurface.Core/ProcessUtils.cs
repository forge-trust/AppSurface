using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Core;

/// <summary>
/// Represents the captured result of a completed process execution.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Stdout" /> and <see cref="Stderr" /> are captured independently per stream. They are not a
/// merged chronological timeline, so callers that care about exact interleaving between standard output and
/// standard error must preserve that ordering separately.
/// </para>
/// <para>
/// <see cref="ExitCode" /> reflects the process exit value and may be non-zero without an exception being
/// thrown. Startup failures and cancellation are surfaced as exceptions instead of a <see cref="CommandResult" />.
/// </para>
/// </remarks>
/// <param name="ExitCode">
/// The exit code returned by the process. A non-zero value indicates command failure, but it is still returned
/// normally to the caller rather than being promoted to an exception.
/// </param>
/// <param name="Stdout">
/// The exact text captured from standard output. This contains only the stdout stream and may be empty even when
/// the process wrote diagnostics to standard error.
/// </param>
/// <param name="Stderr">
/// The exact text captured from standard error. This contains only the stderr stream and may be empty even when
/// the process produced standard output.
/// </param>
public record CommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Provides utility methods for executing external processes.
/// </summary>
public static class ProcessUtils
{
    /// <summary>
    /// Test hooks that allow deterministic verification of process lifecycle edge cases.
    /// </summary>
    /// <remarks>
    /// Use these hooks only from tests that need to simulate rare runtime conditions such as
    /// <see cref="Process.Start()"/> returning <see langword="false"/> or process termination throwing during cleanup.
    /// Production code should continue to use the public <see cref="ExecuteProcessAsync(string, IReadOnlyList{string}, string, ILogger, CancellationToken, bool, Func{string, LogLevel}?)"/>
    /// overload, which executes real process operations without overrides.
    /// </remarks>
    internal sealed record ProcessExecutionHooks
    {
        /// <summary>
        /// Gets or sets an optional process-start override.
        /// </summary>
        internal Func<Process, bool>? StartProcessOverride { get; init; }

        /// <summary>
        /// Gets or sets an optional process-state override for <see cref="Process.HasExited"/>.
        /// </summary>
        internal Func<Process, bool>? HasExitedOverride { get; init; }

        /// <summary>
        /// Gets or sets an optional process-kill override used during cleanup.
        /// </summary>
        internal Action<Process>? KillProcessOverride { get; init; }
    }

    /// <summary>
    /// Executes a process asynchronously and captures its output.
    /// </summary>
    /// <param name="fileName">The path to the executable file to launch.</param>
    /// <param name="args">The ordered list of command-line arguments to pass to the process.</param>
    /// <param name="workingDirectory">The working directory used when starting the process.</param>
    /// <param name="logger">
    /// The logger that receives streamed output when <paramref name="streamOutput"/> is <see langword="true" />.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <param name="streamOutput">
    /// If <see langword="true" />, standard output and standard error are logged in real time and also
    /// captured in the returned <see cref="CommandResult" />.
    /// </param>
    /// <param name="stderrLogLevelSelector">
    /// Optional selector that can remap the log level used for each standard error line when
    /// <paramref name="streamOutput"/> is enabled. When omitted, standard error lines are logged
    /// at <see cref="LogLevel.Error"/>.
    /// </param>
    /// <returns>
    /// A <see cref="CommandResult"/> containing the process exit code and captured standard output/error.
    /// Non-zero exit codes are returned to the caller and are not treated as exceptions.
    /// </returns>
    /// <remarks>
    /// When <paramref name="streamOutput"/> is enabled, output is drained concurrently from both pipes,
    /// logged as lines arrive, and still buffered into the returned <see cref="CommandResult" />. Any
    /// exception thrown by the logger or <paramref name="stderrLogLevelSelector" /> is allowed to
    /// propagate so callers do not receive a partial success result with truncated output. The returned
    /// <see cref="CommandResult" /> preserves the exact standard output and standard error characters
    /// emitted by the process in both streaming and non-streaming modes.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the process fails to start.</exception>
    public static async Task<CommandResult> ExecuteProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        ILogger logger,
        CancellationToken cancellationToken,
        bool streamOutput = false,
        Func<string, LogLevel>? stderrLogLevelSelector = null)
    {
        return await ExecuteProcessAsync(
            fileName,
            args,
            workingDirectory,
            logger,
            cancellationToken,
            streamOutput,
            stderrLogLevelSelector,
            hooks: null);
    }

    /// <summary>
    /// Executes a process asynchronously with optional lifecycle hooks for deterministic testing.
    /// </summary>
    /// <param name="fileName">The path to the executable file to launch.</param>
    /// <param name="args">The ordered list of command-line arguments to pass to the process.</param>
    /// <param name="workingDirectory">The working directory used when starting the process.</param>
    /// <param name="logger">The logger that receives streamed output when <paramref name="streamOutput"/> is enabled.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <param name="streamOutput">
    /// If <see langword="true" />, standard output and standard error are logged in real time and also
    /// captured in the returned <see cref="CommandResult" />.
    /// </param>
    /// <param name="stderrLogLevelSelector">
    /// Optional selector that can remap the log level used for each standard error line when
    /// <paramref name="streamOutput"/> is enabled.
    /// </param>
    /// <param name="hooks">
    /// Optional lifecycle hooks used by tests to simulate rare process runtime behaviors that are otherwise difficult
    /// to reproduce deterministically.
    /// </param>
    /// <returns>A <see cref="CommandResult"/> containing the process exit code and captured standard output/error.</returns>
    internal static async Task<CommandResult> ExecuteProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        ILogger logger,
        CancellationToken cancellationToken,
        bool streamOutput,
        Func<string, LogLevel>? stderrLogLevelSelector,
        ProcessExecutionHooks? hooks)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process();
        process.StartInfo = startInfo;
        var started = false;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        var outputObserved = false;
        try
        {
            try
            {
                if (!StartProcess(process, hooks))
                {
                    var exception = new InvalidOperationException($"Failed to start process: {fileName}");
                    logger.LogError(exception, "Failed to start process {FileName}", fileName);
                    throw exception;
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start process {FileName}", fileName);
                throw new InvalidOperationException($"Failed to start process: {fileName}", ex);
            }

            started = true;

            if (streamOutput)
            {
                stdoutTask = StreamToLoggerAsync(process.StandardOutput, logger, LogLevel.Information, fileName, cancellationToken);
                stderrTask = StreamToLoggerAsync(process.StandardError, logger, LogLevel.Error, fileName, cancellationToken, stderrLogLevelSelector);
            }
            else
            {
                stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            }

            await WaitForExitOrStreamingFailureAsync(process, streamOutput, stdoutTask, stderrTask, cancellationToken);

            var stdout = await GetResultAsync(stdoutTask);
            var stderr = await GetResultAsync(stderrTask);
            outputObserved = true;

            return new CommandResult(process.ExitCode, stdout, stderr);
        }
        finally
        {
            TryKillProcess(process, started, logger, hooks);

            if (!outputObserved)
            {
                // Ensure tasks are observed if an exception occurred before the normal result path.
                await ObserveTaskAsync(stdoutTask, "stdout", fileName, logger);
                await ObserveTaskAsync(stderrTask, "stderr", fileName, logger);
            }
        }
    }

    private static bool StartProcess(Process process, ProcessExecutionHooks? hooks)
    {
        return hooks?.StartProcessOverride?.Invoke(process) ?? process.Start();
    }

    /// <summary>
    /// Drains a text reader into a captured string while logging completed lines as they arrive.
    /// </summary>
    /// <param name="reader">The reader to drain.</param>
    /// <param name="logger">The logger that receives completed output lines.</param>
    /// <param name="level">The default log level used for each completed line.</param>
    /// <param name="fileName">The process name included in structured log messages.</param>
    /// <param name="cancellationToken">The token used to cancel the drain operation.</param>
    /// <param name="levelSelector">
    /// Optional selector that can remap the log level used for each completed line.
    /// </param>
    /// <returns>
    /// The exact text captured from <paramref name="reader" />, including original line terminators.
    /// </returns>
    /// <remarks>
    /// This method is internal so tests can deterministically verify the stream-drain and cancellation
    /// behavior without depending on timing-sensitive child-process races.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken" /> is canceled before the reader is fully drained.
    /// Any unterminated trailing line is intentionally not logged in that case.
    /// </exception>
    internal static async Task<string> StreamToLoggerAsync(
        TextReader reader,
        ILogger logger,
        LogLevel level,
        string fileName,
        CancellationToken cancellationToken,
        Func<string, LogLevel>? levelSelector = null)
    {
        var output = new StringBuilder();
        var currentLine = new StringBuilder();
        var buffer = new char[1024];
        var previousWasCarriageReturn = false;

        while (true)
        {
            var charsRead = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (charsRead == 0)
            {
                break;
            }

            for (var i = 0; i < charsRead; i++)
            {
                var current = buffer[i];
                output.Append(current);

                if (current == '\r')
                {
                    LogCapturedLine(logger, level, fileName, currentLine.ToString(), levelSelector);
                    currentLine.Clear();
                    previousWasCarriageReturn = true;
                    continue;
                }

                if (current == '\n')
                {
                    if (previousWasCarriageReturn)
                    {
                        previousWasCarriageReturn = false;
                        continue;
                    }

                    LogCapturedLine(logger, level, fileName, currentLine.ToString(), levelSelector);
                    currentLine.Clear();
                    continue;
                }

                previousWasCarriageReturn = false;
                currentLine.Append(current);
            }
        }

        if (!previousWasCarriageReturn && currentLine.Length > 0)
        {
            LogCapturedLine(logger, level, fileName, currentLine.ToString(), levelSelector);
        }

        return output.ToString();
    }

    private static void LogCapturedLine(
        ILogger logger,
        LogLevel defaultLevel,
        string fileName,
        string line,
        Func<string, LogLevel>? levelSelector)
    {
        var effectiveLevel = levelSelector?.Invoke(line) ?? defaultLevel;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            logger.Log(effectiveLevel, "{Output}", line);
        }
        else
        {
            logger.Log(effectiveLevel, "{FileName}: {Output}", fileName, line);
        }
    }

    private static void TryKillProcess(Process process, bool started, ILogger logger, ProcessExecutionHooks? hooks)
    {
        if (!started) return;
        try
        {
            if (!(hooks?.HasExitedOverride?.Invoke(process) ?? process.HasExited))
            {
                if (hooks?.KillProcessOverride is { } killProcessOverride)
                {
                    killProcessOverride(process);
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to kill process {ProcessId}", process.Id);
        }
    }

    private static async Task WaitForExitOrStreamingFailureAsync(
        Process process,
        bool streamOutput,
        Task<string>? stdoutTask,
        Task<string>? stderrTask,
        CancellationToken cancellationToken)
    {
        var waitForExitTask = process.WaitForExitAsync(cancellationToken);

        if (!streamOutput)
        {
            await waitForExitTask;
            return;
        }

        var pendingTasks = new List<Task>(2);
        if (stdoutTask != null)
        {
            pendingTasks.Add(stdoutTask);
        }

        if (stderrTask != null)
        {
            pendingTasks.Add(stderrTask);
        }

        while (!waitForExitTask.IsCompleted && pendingTasks.Count > 0)
        {
            var waitCandidates = new Task[pendingTasks.Count + 1];
            waitCandidates[0] = waitForExitTask;
            pendingTasks.CopyTo(waitCandidates, 1);

            var completedTask = await Task.WhenAny(waitCandidates);
            if (completedTask == waitForExitTask)
            {
                break;
            }

            if (completedTask.IsFaulted || completedTask.IsCanceled)
            {
                await completedTask;
            }

            pendingTasks.Remove(completedTask);
        }

        await waitForExitTask;
    }

    /// <summary>
    /// Gets the result of a task if it is a Task&lt;string&gt;.
    /// </summary>
    /// <param name="task">The task to observe.</param>
    /// <returns>The string result if available, otherwise an empty string.</returns>
    private static async Task<string> GetResultAsync(Task<string>? task)
    {
        if (task == null) return string.Empty;
        return await task;
    }

    /// <summary>
    /// Observes a task during cleanup and logs any exceptions without surfacing them.
    /// </summary>
    /// <param name="task">The task to observe.</param>
    /// <param name="streamName">The name of the stream (e.g., "stdout").</param>
    /// <param name="fileName">The file name of the process being executed.</param>
    /// <param name="logger">The logger for debugging.</param>
    private static async Task ObserveTaskAsync(Task<string>? task, string streamName, string fileName, ILogger logger)
    {
        if (task == null) return;
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to complete reading {StreamName} for {FileName}", streamName, fileName);
        }
    }
}
