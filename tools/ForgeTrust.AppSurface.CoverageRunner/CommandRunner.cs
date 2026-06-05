using CliWrap;
using CliWrap.Buffered;

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

        var result = await Cli.Wrap(command)
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        return new CommandResult(
            result.ExitCode,
            string.Concat(result.StandardOutput, result.StandardError));
    }
}
