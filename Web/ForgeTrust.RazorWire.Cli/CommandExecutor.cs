using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Exceptions;
using Microsoft.Extensions.Logging;
using CliCommand = CliWrap.Cli;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Default <see cref="ICommandExecutor"/> implementation backed by
/// CliWrap buffered command execution.
/// </summary>
/// <remarks>
/// This implementation models launch failures as <see cref="ProcessResult"/>
/// instances instead of throwing so resolver code can treat command execution
/// as data and decide whether to fall back or raise a richer exception. If the
/// command is canceled after launch starts, cancellation is propagated. CliWrap
/// owns child-process termination for the canceled command task.
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
    /// Executes a child process, captures its output streams, and returns the
    /// resulting <see cref="ProcessResult"/>.
    /// </summary>
    /// <param name="fileName">The executable to launch.</param>
    /// <param name="args">The ordered command-line arguments passed to the executable.</param>
    /// <param name="workingDirectory">The working directory supplied to the process start info.</param>
    /// <param name="cancellationToken">Cancels the process wait and output reads.</param>
    /// <returns>
    /// A <see cref="ProcessResult"/> whose fields contain the exit code,
    /// stdout, and stderr on success, or a synthetic failure result when the
    /// process cannot be started or configured.
    /// </returns>
    /// <remarks>
    /// The method intentionally returns <see cref="ProcessResult"/> for
    /// launch/setup failures so callers can preserve command context in their
    /// own diagnostics. Arguments are passed to CliWrap as ordered tokens, not
    /// as a shell command string. Standard output and standard error remain
    /// separate unbounded buffers to preserve the previous <c>Process</c>
    /// contract for this CLI slice.
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

        try
        {
            var result = await CliCommand.Wrap(fileName)
                .WithArguments(args)
                .WithWorkingDirectory(workingDirectory)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken);

            return new ProcessResult(result.ExitCode, result.StandardOutput, result.StandardError);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CommandExecutionException ex)
        {
            return new ProcessResult(-1, string.Empty, $"Failed to start process: {ex.Message}");
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(-1, string.Empty, $"Failed to start process: {ex.Message}");
        }
        catch (FileNotFoundException ex)
        {
            return new ProcessResult(-1, string.Empty, $"Executable not found: {ex.Message}");
        }
        catch (DirectoryNotFoundException ex)
        {
            return new ProcessResult(-1, string.Empty, $"Working directory not found: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return new ProcessResult(-1, string.Empty, $"Invalid process start operation: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unexpected process start failure for command {FileName}", fileName);
            return new ProcessResult(-1, string.Empty, $"An unexpected error occurred while starting the process: {ex.Message}");
        }
    }
}
