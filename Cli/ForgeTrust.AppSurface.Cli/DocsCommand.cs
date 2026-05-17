using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.Docs.Standalone;
using ForgeTrust.AppSurface.Web;
using ForgeTrust.RazorWire.Cli;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Previews RazorDocs for a local repository through the public <c>appsurface docs</c> command.
/// </summary>
/// <remarks>
/// This command starts the RazorDocs standalone host with CLI-friendly defaults and delegates option validation and
/// argument construction to <see cref="RazorDocsPreviewCommand"/>.
/// </remarks>
[Command("docs", Description = "Preview RazorDocs for a repository. Related: docs preview, docs export.")]
internal sealed class DocsCommand : RazorDocsPreviewCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DocsCommand"/> class.
    /// </summary>
    /// <param name="logger">Logger used for command diagnostics.</param>
    /// <param name="hostRunner">Runner that starts the RazorDocs host.</param>
    public DocsCommand(ILogger<DocsCommand> logger, IRazorDocsHostRunner hostRunner)
        : base(logger, hostRunner)
    {
    }
}

/// <summary>
/// Previews RazorDocs for a local repository through the <c>appsurface docs preview</c> alias.
/// </summary>
/// <remarks>
/// Use this alias when a command hierarchy reads better in scripts. It has the same options and behavior as
/// <see cref="DocsCommand"/>.
/// </remarks>
[Command("docs preview", Description = "Preview RazorDocs for a repository.")]
internal sealed class DocsPreviewCommand : RazorDocsPreviewCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DocsPreviewCommand"/> class.
    /// </summary>
    /// <param name="logger">Logger used for command diagnostics.</param>
    /// <param name="hostRunner">Runner that starts the RazorDocs host.</param>
    public DocsPreviewCommand(ILogger<DocsPreviewCommand> logger, IRazorDocsHostRunner hostRunner)
        : base(logger, hostRunner)
    {
    }
}

/// <summary>
/// Exports RazorDocs for a local repository through the <c>appsurface docs export</c> command.
/// </summary>
/// <remarks>
/// This command owns the AppSurface Docs source-host lifecycle and delegates static crawling, URL rewriting, CDN
/// validation, and materialization to the RazorWire export engine.
/// </remarks>
[Command("docs export", Description = "Export RazorDocs for a repository to static files.")]
internal sealed class DocsExportCommand : RazorDocsRepositoryCommand, ICommand
{
    private const string DefaultExportUrl = "http://127.0.0.1:0";
    private const string DefaultOutputPath = "dist/docs";

    private readonly ILogger<DocsExportCommand> _logger;
    private readonly IRazorDocsExportRunner _exportRunner;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocsExportCommand"/> class.
    /// </summary>
    /// <param name="logger">Logger used for command diagnostics.</param>
    /// <param name="exportRunner">Runner that starts the docs host and performs static export.</param>
    public DocsExportCommand(ILogger<DocsExportCommand> logger, IRazorDocsExportRunner exportRunner)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exportRunner);

        _logger = logger;
        _exportRunner = exportRunner;
    }

    /// <summary>
    /// Gets the directory where static docs files will be written.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>dist/docs</c> for local use. CI should pass an explicit output path so upload artifacts and export
    /// output stay tied together.
    /// </remarks>
    [CommandOption("output", 'o', Description = "Output directory for exported static docs (default: dist/docs).")]
    public string OutputPath { get; init; } = DefaultOutputPath;

    /// <summary>
    /// Gets the export mode used by the underlying RazorWire exporter.
    /// </summary>
    /// <remarks>
    /// <see cref="ExportMode.Cdn"/> validates and rewrites output for static CDN hosting. <see cref="ExportMode.Hybrid"/>
    /// preserves application-style internal URLs for server-backed deployments.
    /// </remarks>
    [CommandOption("mode", 'm', Description = "Export mode: cdn (default) or hybrid.")]
    public ExportMode Mode { get; init; } = ExportMode.Cdn;

    /// <summary>
    /// Gets an optional path to a seed-route file.
    /// </summary>
    /// <remarks>
    /// This option is long-only because <c>-r</c> is reserved for <c>--repo</c> across AppSurface docs commands. When
    /// omitted, export derives default seeds from the configured docs routing surface.
    /// </remarks>
    [CommandOption("seeds", Description = "Path to a file containing seed routes. Defaults to / and the configured docs root.")]
    public string? SeedRoutesPath { get; init; }

    /// <summary>
    /// Executes the command through the CliFx console integration.
    /// </summary>
    /// <param name="console">Console abstraction used to register cancellation handling.</param>
    /// <returns>A value task that completes when export finishes or command validation fails.</returns>
    [ExcludeFromCodeCoverage]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        var cancellationToken = console.RegisterCancellationHandler();
        await ExecuteAsync(cancellationToken);
    }

    /// <summary>
    /// Executes the command using an explicit cancellation token.
    /// </summary>
    /// <param name="cancellationToken">Token observed while starting the host and exporting static output.</param>
    /// <returns>A value task that completes when export finishes.</returns>
    internal async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var exportArgs = BuildExportArgs();
        _logger.LogInformation(
            "Exporting RazorDocs for {RepositoryRoot} to {OutputPath}.",
            exportArgs.HostArgs.RepositoryRoot,
            exportArgs.OutputPath);

        try
        {
            await _exportRunner.ExportAsync(exportArgs, cancellationToken);
        }
        catch (ExportValidationException ex)
        {
            throw new CommandException(ex.Message);
        }
        catch (TimeoutException ex)
        {
            throw new CommandException(ex.Message);
        }
    }

    /// <summary>
    /// Translates CLI options into an AppSurface Docs export invocation.
    /// </summary>
    /// <returns>The export runner arguments.</returns>
    /// <remarks>
    /// Export defaults to Production and binds a loopback ephemeral port internally. It does not expose preview's
    /// <c>--urls</c> or <c>--port</c> options because humans and CI should not manage export listener ports.
    /// </remarks>
    internal RazorDocsExportArgs BuildExportArgs()
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new CommandException("The --output value must point to an export directory.");
        }

        var hostArgs = BuildHostArgs(defaultEnvironmentName: Environments.Production);
        var seedRoutesPath = string.IsNullOrWhiteSpace(SeedRoutesPath)
            ? null
            : Path.GetFullPath(SeedRoutesPath);
        var initialSeedRoutes = seedRoutesPath is null
            ? BuildDefaultSeedRoutes()
            : null;

        return new RazorDocsExportArgs(
            hostArgs,
            Path.GetFullPath(OutputPath),
            seedRoutesPath,
            initialSeedRoutes,
            Mode,
            DefaultExportUrl);
    }

    private IReadOnlyList<string> BuildDefaultSeedRoutes()
    {
        var docsUrlBuilder = new DocsUrlBuilder(new RazorDocsOptions
        {
            Routing = new RazorDocsRoutingOptions
            {
                RouteRootPath = RouteRootPath,
                DocsRootPath = DocsRootPath
            }
        });

        return new[] { "/", docsUrlBuilder.CurrentDocsRootPath }
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>
/// Shared implementation for RazorDocs preview commands.
/// </summary>
/// <remarks>
/// The base command translates CLI options into RazorDocs standalone host arguments. It keeps command parsing separate
/// from process hosting so tests can verify validation and argument forwarding without starting Kestrel.
/// </remarks>
internal abstract class RazorDocsPreviewCommand : RazorDocsRepositoryCommand, ICommand
{
    private readonly ILogger _logger;
    private readonly IRazorDocsHostRunner _hostRunner;

    /// <summary>
    /// Initializes shared RazorDocs preview command state.
    /// </summary>
    /// <param name="logger">Logger used for command diagnostics.</param>
    /// <param name="hostRunner">Runner that starts the translated RazorDocs host invocation.</param>
    /// <remarks>
    /// Derived command aliases share the same implementation so validation and host argument translation cannot drift.
    /// </remarks>
    protected RazorDocsPreviewCommand(ILogger logger, IRazorDocsHostRunner hostRunner)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hostRunner);

        _logger = logger;
        _hostRunner = hostRunner;
    }

    /// <summary>
    /// Gets the explicit URL binding forwarded to the RazorDocs host.
    /// </summary>
    /// <remarks>
    /// Use this for a full Kestrel binding such as <c>http://127.0.0.1:5189</c>. Prefer <see cref="Port"/> when only the
    /// port needs to change.
    /// </remarks>
    [CommandOption("urls", 'u', Description = "URL binding forwarded to the RazorDocs host, for example http://127.0.0.1:5189.")]
    public string? Urls { get; init; }

    /// <summary>
    /// Gets the port shortcut forwarded to the RazorDocs host.
    /// </summary>
    /// <remarks>
    /// Use this for local preview scripts that only need a port override. Use <see cref="Urls"/> for explicit host,
    /// scheme, or multi-binding scenarios.
    /// </remarks>
    [CommandOption("port", 'p', Description = "Port shortcut forwarded to the RazorDocs host.")]
    public int? Port { get; init; }

    /// <summary>
    /// Executes the command through the CliFx console integration.
    /// </summary>
    /// <param name="console">Console abstraction used to register cancellation handling.</param>
    /// <returns>A value task that completes when the preview host exits or command validation fails.</returns>
    [ExcludeFromCodeCoverage]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        var cancellationToken = console.RegisterCancellationHandler();
        await ExecuteAsync(cancellationToken);
    }

    /// <summary>
    /// Executes the command using an explicit cancellation token.
    /// </summary>
    /// <param name="cancellationToken">Token observed before the host runner starts.</param>
    /// <returns>A value task that completes when the preview host exits.</returns>
    /// <remarks>
    /// This overload exists for tests and shared command execution paths that already own cancellation registration.
    /// </remarks>
    internal async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var hostArgs = BuildHostArgs(Urls, Port, defaultEnvironmentName: Environments.Development);
        _logger.LogInformation("Starting RazorDocs preview for {RepositoryRoot}.", hostArgs.RepositoryRoot);
        using var currentDirectory = CurrentDirectoryScope.ChangeTo(hostArgs.RepositoryRoot);
        await _hostRunner.RunAsync(hostArgs.Args, hostArgs.StartupTimeout, cancellationToken);
    }
}

/// <summary>
/// Shared repository, routing, strict-harvest, environment, and startup-timeout options for AppSurface docs commands.
/// </summary>
/// <remarks>
/// Preview and export share these options so route and source configuration cannot drift. Preview adds listener binding
/// options, while export keeps listener management internal and adds output/export options instead.
/// </remarks>
internal abstract class RazorDocsRepositoryCommand
{
    /// <summary>
    /// Gets the repository root to harvest.
    /// </summary>
    /// <remarks>
    /// Defaults to the current directory. Use this when running the CLI from a parent directory, script workspace, or
    /// package output folder. The value must resolve to an existing directory.
    /// </remarks>
    [CommandOption("repo", 'r', Description = "Repository root to harvest (default: current directory).")]
    public string RepositoryRoot { get; init; } = ".";

    /// <summary>
    /// Gets a value indicating whether startup should fail when every configured RazorDocs harvester fails.
    /// </summary>
    /// <remarks>
    /// This is a source-harvest fail-closed gate. Static artifact validation is controlled separately by
    /// <c>docs export --mode cdn</c>.
    /// </remarks>
    [CommandOption("strict", Description = "Fail startup when every configured RazorDocs harvester fails.")]
    public bool StrictHarvest { get; init; }

    /// <summary>
    /// Gets the route-family root for RazorDocs version and archive routes.
    /// </summary>
    /// <remarks>
    /// Use this when the docs route family is mounted somewhere other than <c>/docs</c>, for example
    /// <c>--route-root /reference</c>. Pair it with <see cref="DocsRootPath"/> when the live docs path should differ from
    /// archive/version routes.
    /// </remarks>
    [CommandOption("route-root", Description = "Route-family root for RazorDocs version and archive routes.")]
    public string? RouteRootPath { get; init; }

    /// <summary>
    /// Gets the live docs root path.
    /// </summary>
    /// <remarks>
    /// Use this to serve current docs under a nested route, for example <c>--route-root /reference --docs-root
    /// /reference/next</c>. Leave unset to use RazorDocs defaults.
    /// </remarks>
    [CommandOption("docs-root", Description = "Live docs root path.")]
    public string? DocsRootPath { get; init; }

    /// <summary>
    /// Gets the host environment forwarded to the RazorDocs standalone host.
    /// </summary>
    /// <remarks>
    /// Preview defaults to <c>Development</c> so the host can use deterministic per-workspace local endpoints. Export
    /// defaults to <c>Production</c> before starting the in-process host.
    /// </remarks>
    [CommandOption("environment", 'e', Description = "Host environment forwarded to the RazorDocs host.")]
    public string? EnvironmentName { get; init; }

    /// <summary>
    /// Gets the number of seconds to wait for the web host to start before failing fast.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds. Set to <c>0</c> to disable the startup watchdog. Negative, infinite, and NaN values are
    /// rejected before the host starts.
    /// </remarks>
    [CommandOption("startup-timeout-seconds", Description = "Seconds to wait for the RazorDocs web host to start before failing fast. Use 0 to disable.")]
    public double StartupTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Translates shared CLI options into standalone RazorDocs host arguments.
    /// </summary>
    /// <param name="defaultEnvironmentName">Environment to use when <see cref="EnvironmentName"/> is blank.</param>
    /// <returns>The repository root, forwarded host arguments, startup timeout, and resolved environment.</returns>
    internal RazorDocsHostArgs BuildHostArgs(string? defaultEnvironmentName)
    {
        return BuildHostArgs(urls: null, port: null, defaultEnvironmentName);
    }

    /// <summary>
    /// Translates shared and preview-only CLI options into standalone RazorDocs host arguments.
    /// </summary>
    /// <param name="urls">Optional explicit preview URL binding.</param>
    /// <param name="port">Optional preview port shortcut.</param>
    /// <param name="defaultEnvironmentName">Environment to use when <see cref="EnvironmentName"/> is blank.</param>
    /// <returns>The repository root, forwarded host arguments, startup timeout, and resolved environment.</returns>
    internal RazorDocsHostArgs BuildHostArgs(string? urls, int? port, string? defaultEnvironmentName)
    {
        if (string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            throw new CommandException("The --repo value must point to a repository directory.");
        }

        var repositoryRoot = Path.GetFullPath(RepositoryRoot);
        if (!Directory.Exists(repositoryRoot))
        {
            throw new CommandException($"The RazorDocs repository root does not exist: {repositoryRoot}");
        }

        var environmentName = ResolveEnvironmentName(defaultEnvironmentName);
        var args = new List<string>
        {
            "--RazorDocs:Source:RepositoryRoot",
            repositoryRoot
        };

        AddOptional(args, "--urls", urls);
        if (port is not null)
        {
            if (port is < 1 or > 65535)
            {
                throw new CommandException("The --port value must be between 1 and 65535.");
            }

            args.Add("--port");
            args.Add(port.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (StrictHarvest)
        {
            args.Add("--RazorDocs:Harvest:FailOnFailure");
            args.Add("true");
        }

        AddOptional(args, "--RazorDocs:Routing:RouteRootPath", RouteRootPath);
        AddOptional(args, "--RazorDocs:Routing:DocsRootPath", DocsRootPath);
        AddOptional(args, "--environment", environmentName);

        return new RazorDocsHostArgs(repositoryRoot, args.ToArray(), ResolveStartupTimeout(), environmentName);
    }

    private static void AddOptional(List<string> args, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        args.Add(name);
        args.Add(value);
    }

    private string? ResolveEnvironmentName(string? defaultEnvironmentName)
    {
        if (!string.IsNullOrWhiteSpace(EnvironmentName))
        {
            return EnvironmentName.Trim();
        }

        return string.IsNullOrWhiteSpace(defaultEnvironmentName)
            ? null
            : defaultEnvironmentName.Trim();
    }

    private TimeSpan? ResolveStartupTimeout()
    {
        if (double.IsNaN(StartupTimeoutSeconds) || double.IsInfinity(StartupTimeoutSeconds) || StartupTimeoutSeconds < 0)
        {
            throw new CommandException("The --startup-timeout-seconds value must be a finite number greater than or equal to 0.");
        }

        if (StartupTimeoutSeconds > TimeSpan.MaxValue.TotalSeconds)
        {
            throw new CommandException(
                $"The --startup-timeout-seconds value must be less than or equal to {TimeSpan.MaxValue.TotalSeconds.ToString(CultureInfo.InvariantCulture)}.");
        }

        return StartupTimeoutSeconds == 0
            ? null
            : TimeSpan.FromSeconds(StartupTimeoutSeconds);
    }

    protected sealed class CurrentDirectoryScope : IDisposable
    {
        private readonly string _previousDirectory;

        private CurrentDirectoryScope(string previousDirectory)
        {
            _previousDirectory = previousDirectory;
        }

        public static CurrentDirectoryScope ChangeTo(string directory)
        {
            var previousDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(directory);
            return new CurrentDirectoryScope(previousDirectory);
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_previousDirectory);
        }
    }
}

/// <summary>
/// Describes the RazorDocs host invocation produced by the CLI option translator.
/// </summary>
/// <param name="RepositoryRoot">Absolute repository root that the RazorDocs host should harvest.</param>
/// <param name="Args">Command-line arguments forwarded to the standalone RazorDocs host.</param>
/// <param name="StartupTimeout">Startup watchdog timeout, or <see langword="null"/> when disabled.</param>
/// <param name="EnvironmentName">Resolved host environment, or <see langword="null"/> when the host should use its default.</param>
internal readonly record struct RazorDocsHostArgs(
    string RepositoryRoot,
    string[] Args,
    TimeSpan? StartupTimeout,
    string? EnvironmentName);

/// <summary>
/// Describes a one-shot AppSurface Docs static export request.
/// </summary>
/// <param name="HostArgs">Standalone RazorDocs host arguments.</param>
/// <param name="OutputPath">Absolute output directory for exported files.</param>
/// <param name="SeedRoutesPath">Optional absolute seed-route file path.</param>
/// <param name="InitialSeedRoutes">Optional in-memory seed routes used when <paramref name="SeedRoutesPath"/> is null.</param>
/// <param name="Mode">RazorWire static export mode.</param>
/// <param name="RequestedBaseUrl">Loopback URL passed to Kestrel. The default uses port 0 so the OS chooses a free port.</param>
internal readonly record struct RazorDocsExportArgs(
    RazorDocsHostArgs HostArgs,
    string OutputPath,
    string? SeedRoutesPath,
    IReadOnlyList<string>? InitialSeedRoutes,
    ExportMode Mode,
    string RequestedBaseUrl);

/// <summary>
/// Applies shared host options required by packaged AppSurface docs tooling.
/// </summary>
internal static class RazorDocsCliHost
{
    /// <summary>
    /// Configures the standalone RazorDocs host shape used by packaged preview and export commands.
    /// </summary>
    /// <param name="options">Web startup options to mutate.</param>
    /// <param name="startupTimeout">Startup watchdog timeout, or <see langword="null"/> when disabled.</param>
    public static void ConfigurePackagedToolHost(WebOptions options, TimeSpan? startupTimeout)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Packaged .NET tools often lack static web asset manifests; RazorWireWebModule and RazorDocs endpoint
        // fallbacks serve embedded assets instead so global/local tool distributions remain self-contained.
        options.StaticFiles.EnableStaticWebAssets = false;
        options.StartupTimeout = startupTimeout;
    }
}

/// <summary>
/// Starts a RazorDocs host for CLI preview commands.
/// </summary>
/// <remarks>
/// This seam keeps command parsing and validation testable without starting a real web host. Production implementations
/// should honor cancellation before delegating into long-running host lifetimes.
/// </remarks>
internal interface IRazorDocsHostRunner
{
    /// <summary>
    /// Runs the RazorDocs host with translated command-line arguments.
    /// </summary>
    /// <param name="args">Arguments forwarded to the standalone RazorDocs host.</param>
    /// <param name="startupTimeout">Startup watchdog timeout, or <see langword="null"/> to disable it.</param>
    /// <param name="cancellationToken">Token that cancels before the host is started.</param>
    /// <returns>A task that completes when the host exits.</returns>
    Task RunAsync(string[] args, TimeSpan? startupTimeout, CancellationToken cancellationToken);
}

/// <summary>
/// Starts the AppSurface Docs host and exports it to static files.
/// </summary>
internal interface IRazorDocsExportRunner
{
    /// <summary>
    /// Starts the docs host, runs static export, and stops the host.
    /// </summary>
    /// <param name="args">Resolved export arguments.</param>
    /// <param name="cancellationToken">Token observed during host startup and export.</param>
    /// <returns>A task that completes when export finishes.</returns>
    Task ExportAsync(RazorDocsExportArgs args, CancellationToken cancellationToken);
}

/// <summary>
/// Adapts the RazorWire static exporter behind a small AppSurface CLI test seam.
/// </summary>
internal interface IRazorWireStaticExporter
{
    /// <summary>
    /// Exports the started docs host described by <paramref name="context"/>.
    /// </summary>
    /// <param name="context">RazorWire export context.</param>
    /// <param name="cancellationToken">Token observed by the export operation.</param>
    /// <returns>A task that completes when export finishes.</returns>
    Task ExportAsync(ExportContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IRazorDocsHostRunner"/> that delegates to the standalone RazorDocs web host.
/// </summary>
/// <remarks>
/// Use this adapter for the packaged AppSurface CLI path. Tests should prefer fake runners so they can verify argument
/// translation without starting Kestrel. The type is internal and sealed because callers should depend on
/// <see cref="IRazorDocsHostRunner"/> rather than subclassing host lifetime behavior.
/// </remarks>
[ExcludeFromCodeCoverage(
    Justification = "Production adapter delegates into the long-running standalone web host; command tests cover argument and option construction before this boundary.")]
internal sealed class RazorDocsStandaloneHostRunner : IRazorDocsHostRunner
{
    /// <inheritdoc />
    public Task RunAsync(string[] args, TimeSpan? startupTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RazorDocsStandaloneHost.RunAsync(args, options => RazorDocsCliHost.ConfigurePackagedToolHost(options, startupTimeout));
    }
}

/// <summary>
/// Production export runner that starts the standalone RazorDocs host in-process and exports it over real loopback HTTP.
/// </summary>
internal sealed class RazorDocsInProcessExportRunner : IRazorDocsExportRunner
{
    private readonly ILogger<RazorDocsInProcessExportRunner> _logger;
    private readonly IRazorWireStaticExporter _exporter;
    private readonly IRazorDocsExportHostStarter _hostStarter;

    /// <summary>
    /// Initializes a new instance of the <see cref="RazorDocsInProcessExportRunner"/> class.
    /// </summary>
    /// <param name="logger">Logger used for host lifecycle diagnostics.</param>
    /// <param name="exporter">Static exporter invoked after the in-process host starts.</param>
    public RazorDocsInProcessExportRunner(
        ILogger<RazorDocsInProcessExportRunner> logger,
        IRazorWireStaticExporter exporter)
        : this(logger, exporter, new RazorDocsStandaloneExportHostStarter())
    {
    }

    internal RazorDocsInProcessExportRunner(
        ILogger<RazorDocsInProcessExportRunner> logger,
        IRazorWireStaticExporter exporter,
        IRazorDocsExportHostStarter hostStarter)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(hostStarter);

        _logger = logger;
        _exporter = exporter;
        _hostStarter = hostStarter;
    }

    /// <inheritdoc />
    public async Task ExportAsync(RazorDocsExportArgs args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var environmentName = args.HostArgs.EnvironmentName ?? Environments.Production;
        IHost? host = null;

        try
        {
            host = await BuildAndStartHostWithTimeoutAsync(args, environmentName, cancellationToken);

            var baseUrl = ResolveBoundBaseUrl(host);
            _logger.LogInformation("RazorDocs export host started at {BaseUrl}.", baseUrl);

            var context = new ExportContext(
                args.OutputPath,
                args.SeedRoutesPath,
                args.InitialSeedRoutes,
                baseUrl,
                args.Mode);

            await _exporter.ExportAsync(context, cancellationToken);
        }
        finally
        {
            if (host is not null)
            {
                await StopAndDisposeHostAsync(host);
            }
        }
    }

    internal static string ResolveBoundBaseUrl(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var addresses = host.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;

        return ResolveBoundBaseUrl(addresses);
    }

    internal static string ResolveBoundBaseUrl(ICollection<string>? addresses)
    {
        if (addresses is null || addresses.Count == 0)
        {
            throw new InvalidOperationException("RazorDocs export host did not publish a listening URL. No addresses were published.");
        }

        if (addresses.Count != 1)
        {
            throw new InvalidOperationException($"RazorDocs export host published {addresses.Count} listening URLs; expected exactly one. Values: '{string.Join("', '", addresses)}'.");
        }

        var baseAddress = addresses.Single();
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"RazorDocs export host did not publish a valid listening URL. Value: '{baseAddress}'.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private async Task<IHost> BuildAndStartHostWithTimeoutAsync(
        RazorDocsExportArgs args,
        string environmentName,
        CancellationToken cancellationToken)
    {
        var startupTimeout = args.HostArgs.StartupTimeout;
        if (startupTimeout is null)
        {
            return await _hostStarter.BuildAndStartAsync(args, environmentName, cancellationToken);
        }

        var startCts = CreateStartupTimeout(startupTimeout.Value, cancellationToken);
        CancellationTokenSource? startupTimeoutCts = startCts;
        var startTask = Task.Run(
            () => _hostStarter.BuildAndStartAsync(args, environmentName, startCts.Token),
            CancellationToken.None);

        try
        {
            var completedTask = await Task.WhenAny(startTask, Task.Delay(startupTimeout.Value, cancellationToken));
            if (completedTask != startTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (startTask.IsCompleted)
                {
                    return await startTask;
                }

                await startCts.CancelAsync();
                ObserveTimedOutStartupTask(startTask, startCts);
                startupTimeoutCts = null;
                throw CreateStartupTimeoutException(startupTimeout.Value, innerException: null);
            }

            return await startTask;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateStartupTimeoutException(startupTimeout.Value, ex);
        }
        catch (OperationCanceledException)
        {
            ObserveTimedOutStartupTask(startTask, startCts);
            startupTimeoutCts = null;
            throw;
        }
        finally
        {
            startupTimeoutCts?.Dispose();
        }
    }

    private static CancellationTokenSource CreateStartupTimeout(TimeSpan startupTimeout, CancellationToken cancellationToken)
    {
        var startupTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeoutCts.CancelAfter(startupTimeout);
        return startupTimeoutCts;
    }

    private void ObserveTimedOutStartupTask(Task<IHost> startTask, CancellationTokenSource startupTimeoutCts)
    {
        _ = ObserveTimedOutStartupTaskAsync(startTask, startupTimeoutCts);
    }

    private async Task ObserveTimedOutStartupTaskAsync(Task<IHost> startTask, CancellationTokenSource startupTimeoutCts)
    {
        try
        {
            var startedHost = await startTask.ConfigureAwait(false);
            await StopAndDisposeHostAsync(startedHost).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNonFatalException(ex))
        {
            _logger.LogDebug(ex, "RazorDocs export host startup task completed after the startup timeout.");
        }
        finally
        {
            startupTimeoutCts.Dispose();
        }
    }

    private static TimeoutException CreateStartupTimeoutException(TimeSpan startupTimeout, Exception? innerException)
    {
        var seconds = startupTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return new TimeoutException($"RazorDocs export host did not start within {seconds} seconds.", innerException);
    }

    private async Task StopAndDisposeHostAsync(IHost host)
    {
        using var disposableHost = host;

        try
        {
            await host.StopAsync(CancellationToken.None);
        }
        catch (Exception ex) when (IsNonFatalException(ex))
        {
            _logger.LogWarning(ex, "RazorDocs export host failed during shutdown.");
        }
    }

    private static bool IsNonFatalException(Exception ex)
    {
        return ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException
            and not BadImageFormatException
            and not CannotUnloadAppDomainException
            and not InvalidProgramException
            and not ThreadAbortException;
    }

}

/// <summary>
/// Builds and starts the in-process AppSurface Docs export host.
/// </summary>
internal interface IRazorDocsExportHostStarter
{
    /// <summary>
    /// Builds the standalone docs host, starts Kestrel, and returns the started host for export.
    /// </summary>
    /// <param name="args">Resolved export arguments.</param>
    /// <param name="environmentName">Resolved host environment.</param>
    /// <param name="cancellationToken">Token observed while starting the host.</param>
    /// <returns>The started host.</returns>
    Task<IHost> BuildAndStartAsync(
        RazorDocsExportArgs args,
        string environmentName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IRazorDocsExportHostStarter"/> that uses the RazorDocs standalone host builder.
/// </summary>
[ExcludeFromCodeCoverage(
    Justification = "Production adapter delegates into the real standalone host builder and Kestrel; command and runner tests cover behavior before this boundary.")]
internal sealed class RazorDocsStandaloneExportHostStarter : IRazorDocsExportHostStarter
{
    /// <inheritdoc />
    public async Task<IHost> BuildAndStartAsync(
        RazorDocsExportArgs args,
        string environmentName,
        CancellationToken cancellationToken)
    {
        var builder = RazorDocsStandaloneHost.CreateBuilder(
            args.HostArgs.Args,
            new FixedEnvironmentProvider(environmentName),
            options => RazorDocsCliHost.ConfigurePackagedToolHost(options, args.HostArgs.StartupTimeout));

        builder.UseContentRoot(args.HostArgs.RepositoryRoot);
        builder.ConfigureWebHost(webHost =>
        {
            webHost.UseEnvironment(environmentName);
            webHost.UseUrls(args.RequestedBaseUrl);
        });

        var host = builder.Build();
        try
        {
            await host.StartAsync(cancellationToken);
            return host;
        }
        catch (Exception ex) when (IsNonFatalException(ex))
        {
            host.Dispose();
            throw;
        }
    }

    private static bool IsNonFatalException(Exception ex)
    {
        return ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException
            and not BadImageFormatException
            and not CannotUnloadAppDomainException
            and not InvalidProgramException
            and not ThreadAbortException;
    }

    private sealed class FixedEnvironmentProvider : IEnvironmentProvider
    {
        private readonly string _environmentName;

        public FixedEnvironmentProvider(string environmentName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
            _environmentName = environmentName;
        }

        public string Environment => _environmentName;

        public bool IsDevelopment => string.Equals(_environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);

        public string? GetEnvironmentVariable(string name, string? defaultValue = null)
        {
            if (string.Equals(name, "ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
            {
                return _environmentName;
            }

            var value = System.Environment.GetEnvironmentVariable(name);
            return value ?? defaultValue;
        }
    }
}

/// <summary>
/// Production <see cref="IRazorWireStaticExporter"/> that delegates to RazorWire's <see cref="ExportEngine"/>.
/// </summary>
internal sealed class RazorWireExportEngineAdapter : IRazorWireStaticExporter
{
    private readonly ExportEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="RazorWireExportEngineAdapter"/> class.
    /// </summary>
    /// <param name="engine">RazorWire export engine.</param>
    public RazorWireExportEngineAdapter(ExportEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc />
    public Task ExportAsync(ExportContext context, CancellationToken cancellationToken)
    {
        return _engine.RunAsync(context, cancellationToken);
    }
}
