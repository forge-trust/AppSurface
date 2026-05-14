using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Console;

/// <summary>
/// Represents configurable startup options for AppSurface console applications.
/// </summary>
public record ConsoleOptions
{
    /// <summary>
    /// Gets a default instance of <see cref="ConsoleOptions"/>.
    /// </summary>
    public static ConsoleOptions Default => new();

    /// <summary>
    /// Gets or sets how the console app should balance command-owned output against ambient host lifecycle diagnostics.
    /// </summary>
    /// <remarks>
    /// The default value is <see cref="ConsoleOutputMode.Default"/>, which preserves the standard Generic Host behavior.
    /// Set this to <see cref="ConsoleOutputMode.CommandFirst"/> for public CLI tools whose help, validation, and progress
    /// output should stay command-centric.
    /// </remarks>
    public ConsoleOutputMode OutputMode { get; set; } = ConsoleOutputMode.Default;

    /// <summary>
    /// Gets custom service registrations that should be applied after AppSurface's built-in console services.
    /// </summary>
    /// <remarks>
    /// Use this for advanced console hosting scenarios, such as injecting a custom <c>CliFx.Infrastructure.IConsole</c>
    /// implementation or additional logging providers for tests and embedded hosts.
    /// </remarks>
    public List<Action<IServiceCollection>> CustomRegistrations { get; } = [];
}
