using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Default <see cref="ICommandExecutor"/> implementation backed by
/// <see cref="Process"/>.
/// </summary>
/// <remarks>
/// This implementation models launch failures as <see cref="ProcessResult"/>
/// instances instead of throwing so resolver code can treat command execution
/// as data and decide whether to fall back or raise a richer exception. If the
/// command is canceled after launch starts, cancellation is propagated and the
/// finally block still attempts to stop the child process tree.
/// </remarks>
internal sealed class CommandExecutor : ICommandExecutor
{
    private readonly ILogger<CommandExecutor> _logger;

    public CommandExecutor(ILogger<CommandExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Starts a child process, captures its output streams, and returns the
    /// resulting <see cref="ProcessResult"/>.
    /// </summary>
    /// <param name="fileName">The executable to launch.</param>
    /// <param name="args">The ordered command-line arguments passed to the executable.</param>
    /// <param name="workingDirectory">The working directory supplied to the process start info.</param>
    /// <param name="cancellationToken">Cancels the process wait and output reads.</param>
    /// <returns>
    /// A <see cref="ProcessResult"/> whose fields contain the exit code,
    /// stdout, and stderr on success, or a synthetic failure result when the
    /// process cannot be started.
    /// </returns>
    /// <remarks>
    /// The method intentionally returns <see cref="ProcessResult"/> for
    /// launch/setup failures so callers can preserve command context in their
    /// own diagnostics. The finally block always attempts
    /// <c>Kill(entireProcessTree: true)</c> for started processes, so callers
    /// should assume cancellation or mid-flight failures may terminate child
    /// processes spawned by the launched command.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// Thrown when cancellation is observed after launch begins.
    /// </exception>
    public async Task<ProcessResult> ExecuteCommandAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new ProcessResult(-1, string.Empty, "Command file name is required.");
        }

        if (args is null)
        {
            return new ProcessResult(-1, string.Empty, "Command arguments are required.");
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new ProcessResult(-1, string.Empty, "Working directory is required.");
        }

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

        using var process = new Process { StartInfo = startInfo };
        var started = false;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            try
            {
                started = process.Start();
                if (!started)
                {
                    return new ProcessResult(-1, string.Empty, "Process failed to start.");
                }
            }
            catch (Win32Exception ex)
            {
                return new ProcessResult(-1, string.Empty, $"Failed to start process: {ex.Message}");
            }
            catch (FileNotFoundException ex)
            {
                return new ProcessResult(-1, string.Empty, $"Executable not found: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return new ProcessResult(-1, string.Empty, $"Invalid process start operation: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ProcessResult(-1, string.Empty, $"An unexpected error occurred while starting the process: {ex.Message}");
            }

            stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdoutTask, stderrTask);

            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            TryKillProcess(process, started);
            await ObserveReadTaskAsync(stdoutTask, "stdout", fileName);
            await ObserveReadTaskAsync(stderrTask, "stderr", fileName);
        }
    }

    private void TryKillProcess(Process process, bool started)
    {
        if (!started)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring process termination exception during cleanup.");
        }
    }

    private async Task ObserveReadTaskAsync(Task<string>? readTask, string streamName, string fileName)
    {
        if (readTask is null)
        {
            return;
        }

        try
        {
            _ = await readTask;
        }
        catch (OperationCanceledException)
        {
            // Read was canceled along with the command cancellation token.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring {StreamName} read exception for command {FileName}", streamName, fileName);
        }
    }
}
