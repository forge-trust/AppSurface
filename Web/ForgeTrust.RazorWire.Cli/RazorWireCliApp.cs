using CliFx;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Provides the RazorWire CLI entry surface with the command-first console behavior required for public tool flows.
/// </summary>
internal static class RazorWireCliApp
{
    /// <summary>
    /// Runs the RazorWire CLI directly through the command service while still allowing targeted startup customization.
    /// </summary>
    /// <param name="args">Command-line arguments supplied to the CLI.</param>
    /// <param name="configureOptions">Optional console startup customization applied after RazorWire's defaults.</param>
    /// <returns>A task that completes when the CLI command finishes running.</returns>
    /// <remarks>
    /// Public command-line tool entry points need predictable command output for help, validation errors, and export
    /// progress. Running the command service directly keeps those flows independent from the Generic Host lifecycle while
    /// preserving AppSurface's command registration, dependency injection, and unknown-option suggestions.
    /// </remarks>
    internal static async Task RunAsync(string[] args, Action<ConsoleOptions>? configureOptions = null)
    {
        var options = ConsoleOptions.Default with
        {
            OutputMode = ConsoleOutputMode.CommandFirst
        };
        configureOptions?.Invoke(options);

        var module = new RazorWireCliModule();
        var context = new StartupContext(args, module)
        {
            ConsoleOutputMode = options.OutputMode
        };

        var commandTypes = GetCommandTypes(context.EntryPointAssembly).ToArray();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton(context.EnvironmentProvider);
        services.AddSingleton<IOptionSuggester, LevenshteinOptionSuggester>();
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

    private static IEnumerable<Type> GetCommandTypes(System.Reflection.Assembly assembly) =>
        assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ICommand).IsAssignableFrom(type));
}
