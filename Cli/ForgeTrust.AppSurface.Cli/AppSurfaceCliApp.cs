using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CliFx;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Provides the DI-backed execution runtime for AppSurface CLI commands.
/// </summary>
/// <remarks>
/// This internal runtime exists so the top-level tool entry point stays thin while command discovery, dependency
/// registration, logging defaults, and CliFx execution stay testable. It discovers commands from the AppSurface CLI
/// entry assembly, applies module and caller-provided service registrations, temporarily assigns
/// <see cref="CommandService.PrimaryServiceProvider"/> for constructor-injected command dependencies, and always
/// restores the previous provider after command execution.
/// </remarks>
internal static class AppSurfaceCliApp
{
    /// <summary>
    /// Runs the AppSurface CLI with the provided command-line arguments.
    /// </summary>
    /// <param name="args">Command-line arguments to parse and execute.</param>
    /// <param name="configureOptions">
    /// Optional callback that customizes <see cref="ConsoleOptions"/> before execution. Defaults start from
    /// <see cref="ConsoleOptions.Default"/> with <see cref="ConsoleOutputMode.CommandFirst"/> so command output remains
    /// the primary console experience.
    /// </param>
    /// <returns>A task that completes when the selected command finishes.</returns>
    /// <remarks>
    /// Use this seam from process entry points and tests that need the real command pipeline without shelling out. The
    /// method builds and disposes a fresh service provider for each run, registers AppSurface CLI defaults, then applies
    /// custom registrations last so tests or future host integrations can replace defaults intentionally.
    /// </remarks>
    internal static async Task RunAsync(string[] args, Action<ConsoleOptions>? configureOptions = null)
    {
        var options = ConsoleOptions.Default with
        {
            OutputMode = ConsoleOutputMode.CommandFirst
        };
        configureOptions?.Invoke(options);

        var module = new AppSurfaceCliModule();
        var context = new StartupContext(args, module)
        {
            ConsoleOutputMode = options.OutputMode
        };

        var commandTypes = GetCommandTypes(context.EntryPointAssembly).ToArray();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton(context.EnvironmentProvider);
        services.AddSingleton<IOptionSuggester, LevenshteinOptionSuggester>();
        services.AddSingleton<IRazorDocsHostRunner, RazorDocsStandaloneHostRunner>();
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        foreach (var commandType in commandTypes)
        {
            services.AddTransient(typeof(ICommand), commandType);
            services.AddTransient(commandType);
        }

        module.ConfigureServices(context, services);
        foreach (var customRegistration in options.CustomRegistrations)
        {
            customRegistration(services);
        }

        await using var serviceProvider = services.BuildServiceProvider();
        var commands = commandTypes
            .Select(commandType => (ICommand)serviceProvider.GetRequiredService(commandType))
            .ToArray();
        var suggester = serviceProvider.GetRequiredService<IOptionSuggester>();
        var commandService = new CommandService(commands, context, suggester);
        var previousServiceProvider = CommandService.PrimaryServiceProvider;

        try
        {
            CommandService.PrimaryServiceProvider = serviceProvider;
            await commandService.RunInternalAsync(CancellationToken.None);
        }
        finally
        {
            CommandService.PrimaryServiceProvider = previousServiceProvider;
        }
    }

    [ExcludeFromCodeCoverage(
        Justification = "Defensive ReflectionTypeLoadException fallback requires a broken assembly load graph; CLI tests cover command discovery through RunAsync.")]
    private static IEnumerable<Type> GetCommandTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.OfType<Type>().ToArray();
        }

        return types.Where(type => !type.IsAbstract && typeof(ICommand).IsAssignableFrom(type));
    }
}
