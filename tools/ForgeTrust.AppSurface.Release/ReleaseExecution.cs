using System.Diagnostics;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Command runner abstraction for git and GitHub CLI calls.
/// </summary>
internal interface ICommandRunner
{
    /// <summary>
    /// Runs a process and captures stdout and stderr.
    /// </summary>
    /// <param name="invocation">Command invocation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Command result.</returns>
    Task<CommandResult> RunAsync(CommandInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// Process command runner used by the default CLI.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Process execution is covered through injected command runners so tests do not depend on local git or gh state.")]
internal sealed class ProcessCommandRunner : ICommandRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    public async Task<CommandResult> RunAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(invocation.Executable)
        {
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(invocation.Timeout ?? DefaultTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            var capturedError = await ReadCompletedOutputAsync(stderr);
            var timeoutError = $"Command timed out after {(invocation.Timeout ?? DefaultTimeout).TotalSeconds:0} seconds: {invocation.Executable}";
            return new CommandResult(
                124,
                await ReadCompletedOutputAsync(stdout),
                string.IsNullOrWhiteSpace(capturedError) ? timeoutError : capturedError + Environment.NewLine + timeoutError);
        }

        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        return new CommandResult(process.ExitCode, await stdout, await stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process can exit between HasExited and Kill; timeout handling is already returning a failure result.
            return;
        }
    }

    private static async Task<string> ReadCompletedOutputAsync(Task<string> output)
    {
        try
        {
            return await output;
        }
        catch (OperationCanceledException)
        {
            return "";
        }
    }
}

/// <summary>
/// Clock abstraction used by tests to make generated release dates deterministic.
/// </summary>
internal interface IReleaseClock
{
    /// <summary>
    /// Gets today's UTC date.
    /// </summary>
    /// <returns>UTC date.</returns>
    DateOnly TodayUtc();
}

/// <summary>
/// System clock used by the default CLI.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "System clock wiring is covered through deterministic clock seams in workflow tests.")]
internal sealed class SystemReleaseClock : IReleaseClock
{
    /// <inheritdoc />
    public DateOnly TodayUtc() => DateOnly.FromDateTime(DateTime.UtcNow);
}
