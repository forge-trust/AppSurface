using Microsoft.Extensions.DependencyInjection;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// DI-backed execution runtime for release CliFx commands.
/// </summary>
internal static class ReleaseCliApp
{
    private const string ToolCommandName = "release";

    /// <summary>
    /// Runs release commands through the shared AppSurface command-service primitive.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configureOptions">Optional console configuration.</param>
    /// <returns>A task that completes when command execution finishes.</returns>
    internal static async Task RunAsync(string[] args, Action<ConsoleOptions>? configureOptions = null)
    {
        var options = ConsoleOptions.Default with
        {
            OutputMode = ConsoleOutputMode.CommandFirst
        };
        configureOptions?.Invoke(options);

        var module = new ReleaseCliModule();
        var context = new StartupContext(args, module)
        {
            ConsoleOutputMode = options.OutputMode
        };

        var commandTypes = context.EntryPointAssembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ICommand).IsAssignableFrom(type))
            .ToArray();
        var commandDescriptors = commandTypes
            .Select(CommandDescriptorResolver.GetRequiredDescriptor)
            .ToArray();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton(context.EnvironmentProvider);
        services.AddSingleton<IOptionSuggester, LevenshteinOptionSuggester>();

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
        if (TryHandleUnknownCommand(args, commandDescriptors, serviceProvider))
        {
            Environment.ExitCode = 1;
            return;
        }

        var commandService = new CommandService(commands, context, suggester, ToolCommandName);
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

    private static bool TryHandleUnknownCommand(
        IReadOnlyList<string> args,
        IReadOnlyList<CommandDescriptor> commandDescriptors,
        IServiceProvider serviceProvider)
    {
        if (args.Count == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        var commandNames = commandDescriptors
            .Select(descriptor => descriptor.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (commandNames.Contains(args[0], StringComparer.Ordinal))
        {
            return false;
        }

        var consoleFromDi = serviceProvider.GetService<IConsole>();
        using var createdConsole = consoleFromDi is null ? new SystemConsole() : null;
        var console = consoleFromDi ?? createdConsole!;
        console.Error.WriteLine($"Unrecognized command '{args[0]}'.");
        console.Error.WriteLine($"Supported commands: {string.Join(", ", commandNames)}.");
        return true;
    }
}
