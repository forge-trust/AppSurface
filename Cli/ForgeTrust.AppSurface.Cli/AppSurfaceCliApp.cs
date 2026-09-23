using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CliFx;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Console;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.RazorWire.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> CoverageTestArgumentValues = new();

    /// <summary>
    /// The published command name for the AppSurface CLI .NET tool.
    /// </summary>
    /// <remarks>
    /// This value is passed to <see cref="CommandService"/> so help output, error messages, and usage text display the
    /// command users type (<c>appsurface</c>) instead of the underlying assembly invocation. It must stay aligned with
    /// the project file <c>ToolCommandName</c> property and the package index <c>tool_command_name</c> manifest value.
    /// Package index validation, publish plan validation, and post-publish smoke tests detect drift across those layers.
    /// </remarks>
    internal const string ToolCommandName = "appsurface";

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
        var normalizedArguments = CoverageRunArgumentBinding.Normalize(args);
        var options = ConsoleOptions.Default with
        {
            OutputMode = ConsoleOutputMode.CommandFirst
        };
        configureOptions?.Invoke(options);

        var module = new AppSurfaceCliModule();
        var context = new StartupContext(normalizedArguments.Arguments, module)
        {
            ConsoleOutputMode = options.OutputMode
        };

        var commandTypes = GetCommandTypes(context.EntryPointAssembly).ToArray();
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton(context.EnvironmentProvider);
        services.AddSingleton<IOptionSuggester, LevenshteinOptionSuggester>();
        services.AddSingleton<IAppSurfaceDocsHostRunner, AppSurfaceDocsStandaloneHostRunner>();
        services.AddSingleton<IAppSurfaceDocsBrowserLauncher, SystemAppSurfaceDocsBrowserLauncher>();
        services.AddSingleton<IAppSurfaceDocsExportRunner, AppSurfaceDocsInProcessExportRunner>();
        services.AddSingleton<IAppSurfaceDocsHealthVerifyRunner, AppSurfaceDocsInProcessHealthVerifyRunner>();
        services.AddSingleton<IRazorWireStaticExporter, RazorWireExportEngineAdapter>();
        services.TryAddSingleton<IAppSurfaceGoogleSecretTransferClient, GoogleSecretManagerTransferClientAdapter>();
        services.TryAddSingleton<ISecretPromotionGoogleClientFactory, DefaultSecretPromotionGoogleClientFactory>();
        services.TryAddSingleton<LocalSecretsTransferCoordinator>();
        services.AddTransient<SecretPromotionWorkflow>();
        AddPwaVerifierServices(services);
        services.AddTransient<PwaVerifier>();
        services.AddSingleton(TimeProvider.System);
        AddCanaryPollingServices(services);
        services.AddTransient<CanaryPollWorkflow>();
        AddCoverageServices(services);
        services.AddSingleton<EvidencePlanner>();
        services.AddTransient<EvidenceCliWorkflow>();
        services.AddTransient<TestResultsCleanupWorkflow>();
        services.AddSingleton<IDurableSchemaCommandService, DurableSchemaCommandService>();
        AddExportEngineServices(services);
        services.AddHttpClient<IAppSurfaceDocsHealthHttpClient, AppSurfaceDocsHealthHttpClient>(
            client => { client.Timeout = TimeSpan.FromSeconds(60); });
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
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
        var displayVersion = AppSurfaceCliVersion.ResolveDisplayVersion(typeof(AppSurfaceCliApp).Assembly);
        var commandService = new CommandService(commands, context, suggester, ToolCommandName, displayVersion);
        var previousServiceProvider = CommandService.PrimaryServiceProvider;
        var previousTestArgumentValues = CoverageTestArgumentValues.Value;

        try
        {
            CoverageTestArgumentValues.Value = normalizedArguments.RestoreValues;
            CommandService.PrimaryServiceProvider = serviceProvider;
            await commandService.RunInternalAsync(CancellationToken.None);
        }
        finally
        {
            CommandService.PrimaryServiceProvider = previousServiceProvider;
            CoverageTestArgumentValues.Value = previousTestArgumentValues;
        }
    }

    /// <summary>Restores the literal test tokens bound through this invocation's opaque placeholders.</summary>
    /// <param name="arguments">CliFx-bound test argument tokens in order.</param>
    /// <returns>The original literal tokens, or the input when no normalization map is active.</returns>
    internal static string[] RestoreCoverageTestArguments(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = CoverageTestArgumentValues.Value;
        var restored = values is null
            ? arguments
            : arguments.Select(argument => values.TryGetValue(argument, out var value) ? value : argument).ToArray();
        if (restored.Any(static argument => argument.Length == 0))
        {
            throw new CommandException(
                "ASCOV101 --test-argument requires one nonempty literal token. "
                + "Fix: Add a value immediately after the option or use --test-argument=VALUE. "
                + "Docs: Cli/ForgeTrust.AppSurface.Cli/README.md#coverage-run-hang-diagnostics");
        }

        return restored;
    }

    /// <summary>
    /// Registers the RazorWire export engine dependencies used by AppSurface-owned export commands.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <remarks>
    /// The named <c>ExportEngine</c> HTTP client disables automatic redirects so artifact-producing requests are
    /// surfaced to <see cref="ExportEngine"/>. The engine then applies its explicit redirect boundary checks before
    /// reading or writing redirected response bodies, while source and readiness probes continue to treat surfaced HTTP
    /// redirect responses as evidence that an app endpoint is reachable.
    /// </remarks>
    internal static void AddExportEngineServices(IServiceCollection services)
    {
        services.AddSingleton<ExportEngine>();
        services.AddSingleton<ExportSourceRequestFactory>();
        services.AddSingleton<ExportSourceResolver>();
        services.AddSingleton<ITargetAppProcessFactory, TargetAppProcessFactory>();
        services
            .AddHttpClient("ExportEngine", client => { client.Timeout = TimeSpan.FromSeconds(60); })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }

    /// <summary>
    /// Registers the HTTP client used by <c>appsurface pwa verify</c>.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <remarks>
    /// Automatic redirects are disabled so the verifier observes the requested manifest, icon, diagnostics, and offline
    /// fallback URLs directly. That keeps the verifier's same-origin and base-path checks authoritative instead of letting
    /// <see cref="HttpClient"/> silently follow a redirect outside the app being verified.
    /// </remarks>
    internal static void AddPwaVerifierServices(IServiceCollection services)
    {
        services
            .AddHttpClient<IPwaVerificationHttpClient, PwaVerificationHttpClient>(
                client => { client.Timeout = TimeSpan.FromSeconds(30); })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }

    /// <summary>
    /// Registers the shared coverage core and its CLI and Evidence adapters.
    /// </summary>
    /// <param name="services">Service collection receiving the coverage command and Evidence registrations.</param>
    /// <remarks>
    /// The conditionally linked coverage sources compile only into the private core. Public coverage commands use
    /// CLI-only presentation adapters at their boundary, while the first-party Evidence producer composes the same
    /// private-core services in process. Registering one shared graph preserves identical collection, merge, gate, and
    /// watchdog behavior for both entry points without duplicate service descriptors.
    /// </remarks>
    internal static void AddCoverageServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICoverageRunProcessRunner, CliWrapCoverageRunProcessRunner>();
        services.AddSingleton<IReportGeneratorPackageLocator, ReportGeneratorPackageLocator>();
        services.AddSingleton<ICoverageRunReportGenerator, CoverageRunReportGenerator>();
        services.AddTransient<CoverageRunWorkflow>();
        services.AddTransient<CoverageMergeWorkflow>();

        services.AddTransient<CoverageEvidenceExecutionWorkflow>();
        services.AddTransient<CoverageEvidenceProducer>();
    }

    /// <summary>
    /// Registers the named-canary polling HTTP and delay services.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <remarks>
    /// The polling workflow owns all visible attempt and deadline behavior, so this client disables redirects and has no
    /// client-level timeout. Each workflow dispatch receives its own linked per-attempt token instead.
    /// </remarks>
    internal static void AddCanaryPollingServices(IServiceCollection services)
    {
        services.AddSingleton<ICanaryPollDelay, TimeProviderCanaryPollDelay>();
        services
            .AddHttpClient<ICanaryPollHttpClient, CanaryPollHttpClient>(
                client => { client.Timeout = Timeout.InfiniteTimeSpan; })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
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

/// <summary>The CliFx-safe argv and immutable per-invocation map back to literal test tokens.</summary>
/// <param name="Arguments">Arguments passed to CliFx after option-value normalization.</param>
/// <param name="RestoreValues">Opaque placeholder to caller token map.</param>
internal sealed record CoverageRunNormalizedArguments(string[] Arguments, IReadOnlyDictionary<string, string> RestoreValues);

/// <summary>Normalizes only the registered <c>coverage run</c> command's literal test option values.</summary>
internal static class CoverageRunArgumentBinding
{
    private const string OptionName = "--test-argument";

    /// <summary>Replaces split and joined <c>--test-argument</c> values with opaque CliFx-safe tokens.</summary>
    /// <param name="arguments">The unmodified command-line token array.</param>
    /// <returns>CliFx input plus a map for restoration after binding.</returns>
    /// <remarks>A split option consumes exactly its next token, even if that token begins with a dash.</remarks>
    internal static CoverageRunNormalizedArguments Normalize(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length < 2
            || !string.Equals(arguments[0], "coverage", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(arguments[1], "run", StringComparison.OrdinalIgnoreCase))
        {
            return new CoverageRunNormalizedArguments(
                arguments,
                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        var normalized = new List<string>(arguments.Length);
        var restoreValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var incoming = new HashSet<string>(arguments, StringComparer.Ordinal);
        var placeholderIndex = 0;
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, OptionName, StringComparison.Ordinal))
            {
                var value = index + 1 < arguments.Length ? arguments[++index] : string.Empty;
                AddPlaceholder(value);
                continue;
            }

            if (argument.StartsWith(OptionName + "=", StringComparison.Ordinal))
            {
                var value = argument[(OptionName.Length + 1)..];
                AddPlaceholder(value);
                continue;
            }

            normalized.Add(argument);
        }

        return new CoverageRunNormalizedArguments(
            normalized.ToArray(),
            new ReadOnlyDictionary<string, string>(restoreValues));

        void AddPlaceholder(string value)
        {
            string placeholder;
            do
            {
                placeholder = $"appsurface-test-argument-{Guid.NewGuid():N}-{placeholderIndex++}";
            }
            while (incoming.Contains(placeholder) || restoreValues.ContainsKey(placeholder));

            normalized.Add(OptionName);
            normalized.Add(placeholder);
            restoreValues.Add(placeholder, value);
        }
    }
}
