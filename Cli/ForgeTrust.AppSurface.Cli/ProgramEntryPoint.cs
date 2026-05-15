using System.Threading;
using ForgeTrust.AppSurface.Console;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Internal CLI entry point that adds a scoped test seam around <see cref="AppSurfaceCliApp"/>.
/// </summary>
/// <remarks>
/// Production code uses <see cref="RunAsync"/> directly. Tests can use
/// <see cref="PushConfigureOptionsOverrideForTests"/> to add temporary console or DI overrides without changing the
/// top-level statement in <c>Program.cs</c>.
/// </remarks>
internal static class ProgramEntryPoint
{
    /// <summary>
    /// Stores a test-only console-options override for the current async context.
    /// </summary>
    /// <remarks>
    /// <see cref="AsyncLocal{T}"/> keeps nested and parallel async test flows isolated, but the returned scope from
    /// <see cref="PushConfigureOptionsOverrideForTests"/> still must be disposed to restore the previous value.
    /// </remarks>
    private static readonly AsyncLocal<Action<ConsoleOptions>?> _configureOptionsOverrideForTests = new();

    /// <summary>
    /// Runs the AppSurface CLI with the specified arguments and optional console configuration.
    /// </summary>
    /// <param name="args">Command-line arguments to parse and execute.</param>
    /// <param name="configureOptions">Optional primary console-options callback for the current invocation.</param>
    /// <returns>A task that represents the CLI execution.</returns>
    internal static Task RunAsync(string[] args, Action<ConsoleOptions>? configureOptions = null) =>
        AppSurfaceCliApp.RunAsync(args, CombineConfigureOptions(configureOptions, _configureOptionsOverrideForTests.Value));

    /// <summary>
    /// Pushes a test-only console-options override for the current async context.
    /// </summary>
    /// <param name="configureOptions">Override callback to apply after any direct invocation callback.</param>
    /// <returns>A disposable scope that restores the previous override when disposed.</returns>
    /// <remarks>
    /// Always dispose the returned scope, typically with <c>using var</c>. Overrides compose with
    /// <see cref="RunAsync"/> callbacks in direct-then-override order so tests can replace services after production
    /// defaults are configured.
    /// </remarks>
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

    /// <summary>
    /// Restores the previously active test override when disposed.
    /// </summary>
    /// <param name="previous">Override that was active before the current scope was pushed.</param>
    private sealed class RestoreOverrideScope(Action<ConsoleOptions>? previous) : IDisposable
    {
        public void Dispose()
        {
            _configureOptionsOverrideForTests.Value = previous;
        }
    }
}
