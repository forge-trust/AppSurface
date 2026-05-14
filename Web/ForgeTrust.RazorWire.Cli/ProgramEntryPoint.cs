using System.Threading;
using ForgeTrust.AppSurface.Console;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Provides a testable wrapper around the RazorWire CLI top-level entrypoint.
/// </summary>
internal static class ProgramEntryPoint
{
    private static readonly AsyncLocal<Action<ConsoleOptions>?> _configureOptionsOverrideForTests = new();

    /// <summary>
    /// Runs the RazorWire CLI using the same command-first startup path as the shipped program entrypoint.
    /// </summary>
    /// <param name="args">Command-line arguments supplied to the CLI.</param>
    /// <param name="configureOptions">Optional console startup customization applied before the CLI host starts.</param>
    /// <returns>A task that completes when CLI execution finishes.</returns>
    internal static Task RunAsync(string[] args, Action<ConsoleOptions>? configureOptions = null) =>
        RazorWireCliApp.RunAsync(args, CombineConfigureOptions(configureOptions, _configureOptionsOverrideForTests.Value));

    /// <summary>
    /// Temporarily injects additional console startup configuration for the duration of a test that invokes the real assembly entrypoint.
    /// </summary>
    /// <param name="configureOptions">Test-only console startup customization applied after any explicit caller configuration.</param>
    /// <returns>
    /// A disposable scope that restores the previous override when disposed.
    /// </returns>
    internal static IDisposable PushConfigureOptionsOverrideForTests(Action<ConsoleOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        var previous = _configureOptionsOverrideForTests.Value;
        _configureOptionsOverrideForTests.Value = configureOptions;

        return new RestoreOverrideScope(previous);
    }

    private static Action<ConsoleOptions>? CombineConfigureOptions(
        Action<ConsoleOptions>? primary,
        Action<ConsoleOptions>? secondary)
    {
        if (primary is null)
        {
            return secondary;
        }

        if (secondary is null)
        {
            return primary;
        }

        return options =>
        {
            primary(options);
            secondary(options);
        };
    }

    private sealed class RestoreOverrideScope(Action<ConsoleOptions>? previous) : IDisposable
    {
        public void Dispose()
        {
            _configureOptionsOverrideForTests.Value = previous;
        }
    }
}
