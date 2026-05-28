using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// CLI entry point for AppSurface release preparation and publishing validation.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Launches the release CLI with process IO streams.
    /// </summary>
    /// <param name="args">Command-line arguments supplied to the process.</param>
    /// <returns>Process exit code where <c>0</c> indicates success.</returns>
    [ExcludeFromCodeCoverage(Justification = "Process entry-point wiring delegates to RunAsync, which is covered by CLI tests.")]
    internal static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, System.Console.Out, System.Console.Error, Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// Runs the release CLI against supplied IO streams and an explicit working directory.
    /// </summary>
    /// <param name="args">Command-line arguments, including command and options.</param>
    /// <param name="standardOut">Writer that receives reports, structured output, and help.</param>
    /// <param name="standardError">Writer that receives diagnostic envelopes and invalid usage.</param>
    /// <param name="currentDirectory">Directory used to resolve repository-relative defaults.</param>
    /// <param name="cancellationToken">Cancellation token for file and process work.</param>
    /// <param name="commandRunner">Optional command runner seam used by tests.</param>
    /// <param name="clock">Optional clock seam used by tests.</param>
    /// <returns><c>0</c> for success; otherwise a non-zero exit code.</returns>
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOut,
        TextWriter standardError,
        string currentDirectory,
        CancellationToken cancellationToken = default,
        ICommandRunner? commandRunner = null,
        IReleaseClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOut);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var previousExitCode = Environment.ExitCode;
        using var console = new FakeInMemoryConsole();
        try
        {
            Environment.ExitCode = 0;

            await ReleaseCliApp.RunAsync(
                args,
                options =>
                {
                    options.OutputMode = ConsoleOutputMode.CommandFirst;
                    options.CustomRegistrations.Add(services =>
                    {
                        services.AddSingleton(new ReleaseExecutionContext(Path.GetFullPath(currentDirectory)));
                        services.AddSingleton(commandRunner ?? new ProcessCommandRunner());
                        services.AddSingleton(clock ?? new SystemReleaseClock());
                        services.AddSingleton<IConsole>(console);
                    });
                });

            await standardOut.WriteAsync(console.ReadOutputString());
            await standardError.WriteAsync(console.ReadErrorString());
            return Environment.ExitCode;
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
        }
    }
}
