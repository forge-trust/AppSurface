using System.ComponentModel;
using System.Diagnostics;

namespace ForgeTrust.Runnable.PackageIndex;

/// <summary>
/// Runs external commands for repository validation workflows.
/// </summary>
internal interface ICommandRunner
{
    /// <summary>
    /// Runs the requested command and returns captured output when it exits successfully.
    /// </summary>
    /// <param name="request">Command request with process, timeout, and user-facing error context.</param>
    /// <param name="cancellationToken">Cancellation token used while waiting for command completion.</param>
    /// <returns>Captured process output.</returns>
    /// <exception cref="PackageIndexException">Thrown when the process fails to start, times out, or exits unsuccessfully.</exception>
    Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Describes one external command invocation and the language to use when it fails.
/// </summary>
/// <param name="FileName">Executable name or path.</param>
/// <param name="Arguments">Command-line arguments supplied without shell interpolation.</param>
/// <param name="WorkingDirectory">Working directory for the process.</param>
/// <param name="OperationName">Human-readable operation name, such as <c>dotnet pack</c>.</param>
/// <param name="Subject">Repository item being processed, such as a project path.</param>
/// <param name="FailureVerb">Verb phrase used in nonzero exit messages, such as <c>pack</c> or <c>evaluate</c>.</param>
/// <param name="TimeoutDescription">Gerund phrase used in timeout messages, such as <c>packing</c>.</param>
/// <param name="TimeoutMilliseconds">Timeout applied to the process wait.</param>
/// <param name="Environment">Optional environment variable overrides.</param>
internal sealed record CommandRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string OperationName,
    string Subject,
    string FailureVerb,
    string TimeoutDescription,
    int TimeoutMilliseconds,
    IReadOnlyDictionary<string, string?>? Environment = null);

/// <summary>
/// Captured stdout and stderr from a successful command.
/// </summary>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
internal sealed record CommandRunResult(string StandardOutput, string StandardError);

/// <summary>
/// Process-backed command runner with timeout and failure cleanup.
/// </summary>
internal sealed class ProcessCommandRunner : ICommandRunner
{
    /// <inheritdoc />
    public async Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FailureVerb);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TimeoutDescription);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TimeoutMilliseconds);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new PackageIndexException(
                $"Failed to start {request.OperationName} for '{request.Subject}': {ex.Message}");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(request.TimeoutMilliseconds);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateProcessAsync(process);
            var (_, timeoutError) = await AwaitProcessOutputAsync(
                standardOutputTask,
                standardErrorTask,
                swallowReadFailures: true);
            var message = $"{request.OperationName} timed out after {request.TimeoutMilliseconds} ms while {request.TimeoutDescription} '{request.Subject}'.";
            if (!string.IsNullOrWhiteSpace(timeoutError))
            {
                message = $"{message}{Environment.NewLine}{timeoutError.TrimEnd()}";
            }

            throw new PackageIndexException(message);
        }
        catch
        {
            await TerminateProcessAsync(process);
            _ = await AwaitProcessOutputAsync(
                standardOutputTask,
                standardErrorTask,
                swallowReadFailures: true);
            throw;
        }

        var (standardOutput, standardError) = await AwaitProcessOutputAsync(
            standardOutputTask,
            standardErrorTask,
            swallowReadFailures: false);
        if (process.ExitCode != 0)
        {
            var message = $"Failed to {request.FailureVerb} '{request.Subject}' with {request.OperationName}.";
            if (!string.IsNullOrWhiteSpace(standardError))
            {
                message = $"{message}{Environment.NewLine}{standardError.TrimEnd()}";
            }

            throw new PackageIndexException(message);
        }

        return new CommandRunResult(standardOutput, standardError);
    }

    private static async Task<(string StandardOutput, string StandardError)> AwaitProcessOutputAsync(
        Task<string> standardOutputTask,
        Task<string> standardErrorTask,
        bool swallowReadFailures)
    {
        if (swallowReadFailures)
        {
            try
            {
                await Task.WhenAll(standardOutputTask, standardErrorTask);
            }
            catch
            {
                // Best-effort cleanup after process termination should not hide the original failure path.
            }

            return (
                standardOutputTask.Status == TaskStatus.RanToCompletion ? standardOutputTask.Result : string.Empty,
                standardErrorTask.Status == TaskStatus.RanToCompletion ? standardErrorTask.Result : string.Empty);
        }

        await Task.WhenAll(standardOutputTask, standardErrorTask);
        return (await standardOutputTask, await standardErrorTask);
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
