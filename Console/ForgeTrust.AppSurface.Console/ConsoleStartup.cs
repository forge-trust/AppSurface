using System.Reflection;
using CliFx;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Console;

/// <summary>
/// A base class for console application startup logic, extending the module-based initialization.
/// </summary>
/// <typeparam name="TModule">The type of the root module.</typeparam>
public abstract class ConsoleStartup<TModule> : AppSurfaceStartup<TModule>
    where TModule : IAppSurfaceHostModule, new()
{
    private const string CommandServiceCategory = "ForgeTrust.AppSurface.Console.CommandService";
    private const string HostLifetimeCategory = "Microsoft.Hosting.Lifetime";
    private const string InternalHostCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    private Action<ConsoleOptions>? _configureOptions;
    private ConsoleOptions _options = new();
    private bool _optionsBuilt;

    /// <summary>
    /// Registers an optional callback to customize <see cref="ConsoleOptions"/> and enables fluent chaining.
    /// </summary>
    /// <param name="configureOptions">An optional action invoked before startup context creation to modify console startup behavior.</param>
    /// <returns>The same <see cref="ConsoleStartup{TModule}"/> instance to support fluent configuration.</returns>
    public ConsoleStartup<TModule> WithOptions(Action<ConsoleOptions>? configureOptions = null)
    {
        _configureOptions = configureOptions;
        _options = new();
        _optionsBuilt = false;

        return this;
    }

    /// <summary>
    /// Runs the console application asynchronously using a startup context derived from the supplied arguments and configured console options.
    /// </summary>
    /// <param name="args">Command-line arguments supplied to the console application.</param>
    /// <returns>A task that completes when the host run finishes.</returns>
    public new Task RunAsync(string[] args) => base.RunAsync(CreateStartupContext(args));

    /// <inheritdoc />
    protected sealed override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services)
    {
        // Allow subclasses to add their own services
        ConfigureAdditionalServices(context, services);

        // Register the command types from the assembly of TModule
        var commandTypes = GetCommandTypes(context.EntryPointAssembly);
        foreach (var commandType in commandTypes)
        {
            // We use the ICommand interface to register the command types
            // This allows us to resolve them later when the command service is started
            services.AddTransient(typeof(ICommand), commandType);
            // Register the command type itself so it can be resolved directly by CliFx
            services.AddTransient(commandType);
        }

        services.AddSingleton<IOptionSuggester, LevenshteinOptionSuggester>();
        services.AddHostedService<CommandService>();
    }

    /// <inheritdoc />
    protected override IHostBuilder ConfigureBuilderForAppType(StartupContext context, IHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            if (context.ConsoleOutputMode != ConsoleOutputMode.CommandFirst)
            {
                return;
            }

            logging.AddFilter(HostLifetimeCategory, LogLevel.Warning);
            logging.AddFilter(InternalHostCategory, LogLevel.Warning);
            logging.AddFilter(CommandServiceCategory, LogLevel.Warning);
        });

        return base.ConfigureBuilderForAppType(context, builder);
    }

    /// <summary>
    /// Allows derived classes to register additional services.
    /// </summary>
    /// <param name="context">The startup context.</param>
    /// <param name="services">The service collection.</param>
    protected virtual void ConfigureAdditionalServices(StartupContext context, IServiceCollection services)
    {
        // Default implementation does nothing, so we don't force an implementation.
    }

    private StartupContext CreateStartupContext(string[] args)
    {
        BuildOptions();

        return new StartupContext(
            args,
            CreateRootModule(),
            CustomRegistrations: [.. _options.CustomRegistrations])
        {
            ConsoleOutputMode = _options.OutputMode
        };
    }

    private void BuildOptions()
    {
        if (_optionsBuilt)
        {
            return;
        }

        _options = new();
        _configureOptions?.Invoke(_options);
        _optionsBuilt = true;
    }


    // This will ensure that we register all command types from the assembly of TModule
    private IReadOnlyList<Type> GetCommandTypes(Assembly searchAssembly)
    {
        var commands = searchAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ICommand).IsAssignableFrom(t))
            .ToList();

        return commands;
    }
}
