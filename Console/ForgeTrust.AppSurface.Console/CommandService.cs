using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Console;

internal class CommandService : CriticalService
{
    private readonly IEnumerable<ICommand> _commands;
    private readonly StartupContext _context;
    private readonly string? _executableName;
    private readonly IOptionSuggester _suggester;

    public CommandService(
        IServiceProvider primaryServiceProvider,
        IEnumerable<ICommand> commands,
        ILogger<CommandService> logger,
        IHostApplicationLifetime applicationLifetime,
        StartupContext context,
        IOptionSuggester suggester) : base(logger, applicationLifetime)
    {
        PrimaryServiceProvider = primaryServiceProvider;
        _commands = commands;
        _context = context;
        _suggester = suggester;
    }

    /// <summary>
    /// Creates a command service that can run a supplied command set without the hosted service provider pipeline.
    /// </summary>
    /// <param name="commands">Commands to register with CliFx.</param>
    /// <param name="context">Startup context used when command instances need AppSurface runtime state.</param>
    /// <param name="suggester">Option suggester used to enrich CliFx parse errors.</param>
    /// <param name="executableName">
    /// Optional executable display name used in usage, help, and error output. Leave unset to let CliFx choose its
    /// default executable name.
    /// </param>
    internal CommandService(
        IEnumerable<ICommand> commands,
        StartupContext context,
        IOptionSuggester suggester,
        string? executableName = null) : base(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandService>.Instance,
            new DummyApplicationLifetime())
    {
        _commands = commands;
        _context = context;
        _executableName = executableName;
        _suggester = suggester;
    }

    internal static IServiceProvider? PrimaryServiceProvider { get; set; }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private sealed class DummyApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    internal Task RunInternalAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        var builder = new CommandLineApplicationBuilder();
        var serviceProvider = PrimaryServiceProvider;
        ArgumentNullException.ThrowIfNull(serviceProvider);

        foreach (var cmd in _commands)
        {
            builder.AddCommand(CommandDescriptorResolver.GetRequiredDescriptor(cmd.GetType()));
        }

        if (!string.IsNullOrWhiteSpace(_executableName))
        {
            builder.SetExecutableName(_executableName);
        }

        var consoleFromDi = serviceProvider.GetService<IConsole>();
        using var createdConsole = consoleFromDi == null ? new SystemConsole() : null;
        var console = consoleFromDi ?? createdConsole!;

        var app = builder
            .UseTypeInstantiator(serviceProvider)
            .UseConsole(console)
            .Build();

        var exitCode = await app.RunAsync(_context.Args);

        if (exitCode != 0 && _context.Args.Length > 0)
        {
            // If execution failed, check for unknown options and offer suggestions
            CheckForUnknownOptions(console);
        }

        // Only set the exit code if it hasn't been set already
        // This allows other parts of the application to set a failure exit code
        if (Environment.ExitCode == 0)
        {
            Environment.ExitCode = exitCode;
        }
    }

    /// <summary>
    /// Analyzes the current command-line arguments to detect unknown or mistyped options
    /// after a command has failed and, when possible, displays suggestions for valid
    /// alternatives.
    /// </summary>
    /// <param name="console">
    /// The console used to write diagnostic messages and option suggestions to the user.
    /// </param>
    /// <remarks>
    /// This method performs three main steps:
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///       Resolves the target command type from the parsed arguments and registered commands.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Extracts the set of valid option names for the resolved command.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Compares the provided arguments against the valid options and uses
    ///       <see cref="IOptionSuggester"/> to present suggestions for any unknown options.
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    internal void CheckForUnknownOptions(IConsole console)
    {
        var args = _context.Args;

        // 1. Identify the command and valid options
        var validOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var optionRequiresValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Allow common help flags unconditionally
        validOptions.Add("-h");
        validOptions.Add("--help");
        validOptions.Add("--version");

        CommandDescriptor? commandDescriptor = null;

        // Basic routing: Check if first arg is a command
        if (args.Length > 0)
        {
            var commandName = args[0];
            if (!commandName.StartsWith("-"))
            {
                commandDescriptor = _commands
                    .Select(c => CommandDescriptorResolver.TryGetDescriptor(c.GetType()))
                    .FirstOrDefault(d => d?.Name?.Equals(commandName, StringComparison.OrdinalIgnoreCase) == true);
            }
        }

        // Fallback to default/root command
        if (commandDescriptor == null)
        {
            commandDescriptor = _commands
                .Select(c => CommandDescriptorResolver.TryGetDescriptor(c.GetType()))
                .FirstOrDefault(d => d is { Name: null });
        }

        if (commandDescriptor != null)
        {
            foreach (var option in commandDescriptor.Inputs.OfType<CommandOptionDescriptor>())
            {
                var requiresArg = option.Property.Type != typeof(bool);

                if (!string.IsNullOrEmpty(option.Name))
                {
                    validOptions.Add("--" + option.Name);
                    if (requiresArg) optionRequiresValue.Add("--" + option.Name);
                }

                if (option.ShortName != null)
                {
                    validOptions.Add("-" + option.ShortName);
                    if (requiresArg) optionRequiresValue.Add("-" + option.ShortName);
                }
            }
        }

        // 2. Scan args for unknown options
        var optionExpectedValue = false;
        foreach (var arg in args)
        {
            if (arg == "--")
            {
                break; // Stop scanning options after standard '--' token
            }

            if (optionExpectedValue)
            {
                // This argument is a consumed value for the previous option
                optionExpectedValue = false;
                continue;
            }

            if (arg.StartsWith("-"))
            {
                // Handle --foo=bar syntax
                var token = arg.Split('=', 2)[0];

                if (!validOptions.Contains(token))
                {
                    var suggestions = _suggester.GetSuggestions(token, validOptions);
                    const int maxSuggestionsToShow = 2;
                    var shownCount = 0;

                    foreach (var suggestion in suggestions)
                    {
                        console.ForegroundColor = ConsoleColor.Yellow;
                        console.Error.WriteLine($"Did you mean '{suggestion}'?");
                        console.ResetColor();

                        shownCount++;
                        if (shownCount >= maxSuggestionsToShow)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    // If it's a known option without an inline '=', check if it requires a subsequent value
                    if (!arg.Contains('=') && optionRequiresValue.Contains(token))
                    {
                        optionExpectedValue = true;
                    }
                }
            }
        }
    }
}
