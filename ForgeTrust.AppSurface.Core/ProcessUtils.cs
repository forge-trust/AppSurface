using System.ComponentModel;
using System.Text;
using CliWrap;
using CliWrap.Exceptions;
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
        string stdout = string.Empty;
        string stderr = string.Empty;

        var command = Cli.Wrap(fileName)
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Create(async (stream, targetCancellationToken) =>
            {
                stdout = await CaptureOutputAsync(
                    stream,
                    logger,
                    LogLevel.Information,
                    fileName,
                    targetCancellationToken,
                    streamOutput);
            }))
            .WithStandardErrorPipe(PipeTarget.Create(async (stream, targetCancellationToken) =>
            {
                stderr = await CaptureOutputAsync(
                    stream,
                    logger,
                    LogLevel.Error,
                    fileName,
                    targetCancellationToken,
                    streamOutput,
                    stderrLogLevelSelector);
            }));

        try
        {
            var result = await command.ExecuteAsync(cancellationToken);
            return new CommandResult(result.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CliWrapException ex)
        {
            logger.LogError(ex, "Failed to start process {FileName}", fileName);
            throw new InvalidOperationException($"Failed to start process: {fileName}", ex);
        }
        catch (Win32Exception ex)
        {
            logger.LogError(ex, "Failed to start process {FileName}", fileName);
            throw new InvalidOperationException($"Failed to start process: {fileName}", ex);
        }
    }

    private static async Task<string> CaptureOutputAsync(
        Stream stream,
        ILogger logger,
        LogLevel logLevel,
        string fileName,
        CancellationToken cancellationToken,
        bool streamOutput,
        Func<string, LogLevel>? levelSelector = null)
    {
        using var reader = new StreamReader(
            stream,
            Console.OutputEncoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);

        return streamOutput
            ? await StreamToLoggerAsync(reader, logger, logLevel, fileName, cancellationToken, levelSelector)
            : await reader.ReadToEndAsync(cancellationToken);
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
}
