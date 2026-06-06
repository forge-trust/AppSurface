using System.Buffers;
using System.ComponentModel;
using CliWrap;
using CliWrap.Buffered;
using CliWrap.Exceptions;

namespace ForgeTrust.AppSurface.CoverageRunner;

/// <summary>
/// Runs external commands for the coverage runner.
/// </summary>
internal interface ICommandRunner
{
    /// <summary>
    /// Runs a command and captures its output.
    /// </summary>
    /// <param name="command">Command name.</param>
    /// <param name="arguments">Command arguments.</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="outputFile">
    /// Optional log file that receives streamed standard output and standard error. When supplied,
    /// the returned output is empty unless process launch fails.
    /// </param>
    /// <returns>Command result.</returns>
    Task<CommandResult> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? outputFile = null);
}

/// <summary>
/// Captured command output and exit code.
/// </summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="Output">Captured standard output and standard error, or a launch-failure diagnostic.</param>
internal sealed record CommandResult(int ExitCode, string Output);

/// <summary>
/// Process-based command runner.
/// </summary>
internal sealed class ProcessCommandRunner : ICommandRunner
{
    /// <inheritdoc />
    public async Task<CommandResult> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        string? outputFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        try
        {
            return outputFile is null
                ? await RunBufferedAsync(command, arguments, workingDirectory, cancellationToken)
                : await RunStreamingAsync(command, arguments, workingDirectory, outputFile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCommandLaunchFailure(ex))
        {
            var output = $"Failed to start command '{command}': {ex.Message}{Environment.NewLine}";
            if (outputFile is not null)
            {
                output += await TryAppendFailureLogAsync(outputFile, output, cancellationToken);
            }

            return new CommandResult(1, output);
        }
    }

    private static async Task<CommandResult> RunBufferedAsync(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await Cli.Wrap(command)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return new CommandResult(
            result.ExitCode,
            JoinOutput(result.StandardOutput, result.StandardError));
    }

    private static async Task<CommandResult> RunStreamingAsync(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string outputFile,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            outputFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var writeGate = new SemaphoreSlim(1, 1);

        var result = await Cli.Wrap(command)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.Create((source, token) => CopyPipeToFileAsync(source, stream, writeGate, token)))
            .WithStandardErrorPipe(PipeTarget.Create((source, token) => CopyPipeToFileAsync(source, stream, writeGate, token)))
            .ExecuteAsync(cancellationToken);

        return new CommandResult(result.ExitCode, string.Empty);
    }

    private static async Task CopyPipeToFileAsync(
        Stream source,
        Stream target,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return;
                }

                await writeGate.WaitAsync(cancellationToken);
                try
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static string JoinOutput(string standardOutput, string standardError)
    {
        if (string.IsNullOrEmpty(standardOutput))
        {
            return standardError;
        }

        if (string.IsNullOrEmpty(standardError))
        {
            return standardOutput;
        }

        if (standardOutput.EndsWith('\n') || standardOutput.EndsWith('\r') || standardError.StartsWith('\n') || standardError.StartsWith('\r'))
        {
            return string.Concat(standardOutput, standardError);
        }

        return string.Concat(standardOutput, Environment.NewLine, standardError);
    }

    private static bool IsCommandLaunchFailure(Exception exception)
    {
        return exception is CliWrapException
            or Win32Exception
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or IOException;
    }

    /// <summary>
    /// Appends a process-launch failure diagnostic to a streamed log file.
    /// </summary>
    /// <param name="outputFile">Log file to append.</param>
    /// <param name="output">Diagnostic text to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An additional diagnostic when the log file cannot be written; otherwise an empty string.</returns>
    /// <remarks>
    /// This method is internal so tests can verify cancellation and unwritable-log behavior without
    /// forcing platform-specific process-launch failures to also race file-system failures.
    /// </remarks>
    internal static async Task<string> TryAppendFailureLogAsync(
        string outputFile,
        string output,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(outputFile, output, cancellationToken);
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return $"Failed to write command log '{outputFile}': {ex.Message}{Environment.NewLine}";
        }
    }
}
