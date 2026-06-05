using System.Diagnostics;

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
    /// <returns>Captured command result.</returns>
    Task<CommandResult> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Captured command output and exit code.
/// </summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="Output">Captured standard output and standard error.</param>
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
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to start command '{command}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new CommandResult(
            process.ExitCode,
            string.Concat(await standardOutput, await standardError));
    }
}
