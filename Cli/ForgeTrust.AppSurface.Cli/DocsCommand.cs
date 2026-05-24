using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using CliWrap;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Docs;
using ForgeTrust.AppSurface.Docs.Models;
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
using CliCommand = CliWrap.Cli;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Previews AppSurface Docs for a local repository through the public <c>appsurface docs</c> command.
/// </summary>
/// <remarks>
/// This command starts the AppSurface Docs standalone host with CLI-friendly defaults and delegates option validation and
/// argument construction to <see cref="AppSurfaceDocsPreviewCommand"/>.
/// </remarks>
[Command("docs", Description = "Preview AppSurface Docs for a repository. Related: docs preview, docs export.")]
internal sealed class DocsCommand : AppSurfaceDocsPreviewCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DocsCommand"/> class.
    /// </summary>
    /// <param name="logger">Logger used for command diagnostics.</param>
    /// <param name="hostRunner">Runner that starts the AppSurface Docs host.</param>
    public DocsCommand(ILogger<DocsCommand> logger, IAppSurfaceDocsHostRunner hostRunner)
        : base(logger, hostRunner)
    {
    }
}

/// <summary>
/// Previews AppSurface Docs for a local repository through the <c>appsurface docs preview</c> alias.
/// </summary>
/// <remarks>
/// Use this alias when a command hierarchy reads better in scripts. It has the same options and behavior as
/// <see cref="DocsCommand"/>.
/// </remarks>
[Command("docs preview", Description = "Preview AppSurface Docs for a repository.")]
internal sealed class DocsPreviewCommand : AppSurfaceDocsPreviewCommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DocsPreviewCommand"/> class.
    /// </summary>
    /// <param name="logger">Logger used for command diagnostics.</param>
    /// <param name="hostRunner">Runner that starts the AppSurface Docs host.</param>
    public DocsPreviewCommand(ILogger<DocsPreviewCommand> logger, IAppSurfaceDocsHostRunner hostRunner)
        : base(logger, hostRunner)
    {
    }
}

/// <summary>
/// Exports AppSurface Docs for a local repository through the <c>appsurface docs export</c> command.
/// </summary>
/// <remarks>
/// This command owns the AppSurface Docs source-host lifecycle and delegates static crawling, URL rewriting, CDN
/// validation, and materialization to the RazorWire export engine.
/// </remarks>
[Command("docs export", Description = "Export AppSurface Docs for a repository to static files.")]
internal sealed class DocsExportCommand : AppSurfaceDocsRepositoryCommand, ICommand
{
    private const string DefaultExportUrl = "http://127.0.0.1:0";
    private const string DefaultOutputPath = "dist/docs";

    private readonly ILogger<DocsExportCommand> _logger;
    private readonly IAppSurfaceDocsExportRunner _exportRunner;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocsExportCommand"/> class.
    /// </summary>
    /// <param name="logger">Logger used for command diagnostics.</param>
    /// <param name="exportRunner">Runner that starts the docs host and performs static export.</param>
    public DocsExportCommand(ILogger<DocsExportCommand> logger, IAppSurfaceDocsExportRunner exportRunner)
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
    /// Gets the redirect alias materialization strategy used by the underlying RazorWire exporter.
    /// </summary>
    /// <remarks>
    /// <see cref="ExportRedirectStrategy.Html"/> is the default and works on GitHub Pages and generic static hosts by
    /// writing alias HTML fallback files. <see cref="ExportRedirectStrategy.Netlify"/> writes a root <c>_redirects</c>
    /// file for Netlify-compatible CDN hosting and is valid only with <see cref="ExportMode.Cdn"/>.
    /// </remarks>
    [CommandOption("redirects", Description = "Redirect strategy: html (default) or netlify.")]
    public ExportRedirectStrategy RedirectStrategy { get; init; } = ExportRedirectStrategy.Html;

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
            "Exporting AppSurface Docs for {RepositoryRoot} to {OutputPath}.",
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
    internal AppSurfaceDocsExportArgs BuildExportArgs()
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new CommandException("The --output value must point to an export directory.");
        }

        if (Mode == ExportMode.Hybrid && RedirectStrategy == ExportRedirectStrategy.Netlify)
        {
            throw new CommandException("The --redirects netlify strategy requires --mode cdn because Netlify rules point at publish-root static routes.");
        }

        var outputPath = Path.GetFullPath(OutputPath);
        if (File.Exists(outputPath))
        {
            throw new CommandException($"The --output value must point to an export directory, but an existing file was found: {outputPath}");
        }

        if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any())
        {
            throw new CommandException($"The --output directory must be empty before export starts: {outputPath}");
        }

        var hostArgs = BuildHostArgs(defaultEnvironmentName: Environments.Production);
        var seedRoutesPath = string.IsNullOrWhiteSpace(SeedRoutesPath)
            ? null
            : Path.GetFullPath(SeedRoutesPath);
        if (seedRoutesPath is not null && !File.Exists(seedRoutesPath))
        {
            throw new CommandException($"The --seeds file does not exist: {seedRoutesPath}");
        }

        var initialSeedRoutes = seedRoutesPath is null
            ? BuildDefaultSeedRoutes()
            : null;

        return new AppSurfaceDocsExportArgs(
            hostArgs,
            outputPath,
            seedRoutesPath,
            initialSeedRoutes,
            Mode,
            RedirectStrategy,
            DefaultExportUrl);
    }

    /// <summary>
    /// Builds the default export seed routes from the resolved AppSurface Docs routing options.
    /// </summary>
    /// <returns>The root route plus the live docs root, with duplicates removed.</returns>
    private IReadOnlyList<string> BuildDefaultSeedRoutes()
    {
        var docsUrlBuilder = new DocsUrlBuilder(new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
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
/// Shared implementation for AppSurface Docs preview commands.
/// </summary>
/// <remarks>
/// The base command translates CLI options into AppSurface Docs standalone host arguments. It keeps command parsing separate
/// from process hosting so tests can verify validation and argument forwarding without starting Kestrel.
/// </remarks>
internal abstract class AppSurfaceDocsPreviewCommand : AppSurfaceDocsRepositoryCommand, ICommand
{
    private readonly ILogger _logger;
    private readonly IAppSurfaceDocsHostRunner _hostRunner;

    /// <summary>
    /// Initializes shared AppSurface Docs preview command state.
    /// </summary>
    /// <param name="logger">Logger used for command diagnostics.</param>
    /// <param name="hostRunner">Runner that starts the translated AppSurface Docs host invocation.</param>
    /// <remarks>
    /// Derived command aliases share the same implementation so validation and host argument translation cannot drift.
    /// </remarks>
    protected AppSurfaceDocsPreviewCommand(ILogger logger, IAppSurfaceDocsHostRunner hostRunner)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hostRunner);

        _logger = logger;
        _hostRunner = hostRunner;
    }

    /// <summary>
    /// Gets the explicit URL binding forwarded to the AppSurface Docs host.
    /// </summary>
    /// <remarks>
    /// Use this for a full Kestrel binding such as <c>http://127.0.0.1:5189</c>. Prefer <see cref="Port"/> when only the
    /// port needs to change.
    /// </remarks>
    [CommandOption("urls", 'u', Description = "URL binding forwarded to the AppSurface Docs host, for example http://127.0.0.1:5189.")]
    public string? Urls { get; init; }

    /// <summary>
    /// Gets the port shortcut forwarded to the AppSurface Docs host.
    /// </summary>
    /// <remarks>
    /// Use this for local preview scripts that only need a port override. Use <see cref="Urls"/> for explicit host,
    /// scheme, or multi-binding scenarios.
    /// </remarks>
    [CommandOption("port", 'p', Description = "Port shortcut forwarded to the AppSurface Docs host.")]
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
        _logger.LogInformation("Starting AppSurface Docs preview for {RepositoryRoot}.", hostArgs.RepositoryRoot);
        using var currentDirectory = CurrentDirectoryScope.ChangeTo(hostArgs.RepositoryRoot);
        try
        {
            await _hostRunner.RunAsync(hostArgs.Args, hostArgs.StartupTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new CommandException(ex.Message);
        }
    }
}

/// <summary>
/// Shared repository, routing, strict-harvest, environment, and startup-timeout options for AppSurface docs commands.
/// </summary>
/// <remarks>
/// Preview and export share these options so route and source configuration cannot drift. Preview adds listener binding
/// options, while export keeps listener management internal and adds output/export options instead.
/// </remarks>
internal abstract class AppSurfaceDocsRepositoryCommand
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
    /// Gets a value indicating whether startup should fail when every configured AppSurface Docs harvester fails.
    /// </summary>
    /// <remarks>
    /// This is a source-harvest fail-closed gate. Static artifact validation is controlled separately by
    /// <c>docs export --mode cdn</c>.
    /// </remarks>
    [CommandOption("strict", Description = "Fail startup when every configured AppSurface Docs harvester fails.")]
    public bool StrictHarvest { get; init; }

    /// <summary>
    /// Gets the route-family root for AppSurface Docs version and archive routes.
    /// </summary>
    /// <remarks>
    /// Use this when the docs route family is mounted somewhere other than <c>/docs</c>, for example
    /// <c>--route-root /reference</c>. Pair it with <see cref="DocsRootPath"/> when the live docs path should differ from
    /// archive/version routes.
    /// </remarks>
    [CommandOption("route-root", Description = "Route-family root for AppSurface Docs version and archive routes.")]
    public string? RouteRootPath { get; init; }

    /// <summary>
    /// Gets the live docs root path.
    /// </summary>
    /// <remarks>
    /// Use this to serve current docs under a nested route, for example <c>--route-root /reference --docs-root
    /// /reference/next</c>. Leave unset to use AppSurface Docs defaults.
    /// </remarks>
    [CommandOption("docs-root", Description = "Live docs root path.")]
    public string? DocsRootPath { get; init; }

    /// <summary>
    /// Gets the public origin used for absolute AppSurface Docs canonical metadata.
    /// </summary>
    /// <remarks>
    /// Use this for published docs exports when the public host is known, for example
    /// <c>--public-origin https://docs.example.com</c>. Configure only the origin; route paths such as
    /// <c>/docs</c> come from <see cref="RouteRootPath"/> and <see cref="DocsRootPath"/>.
    /// </remarks>
    [CommandOption("public-origin", Description = "Public origin used for absolute AppSurface Docs canonical metadata.")]
    public string? PublicOrigin { get; init; }

    /// <summary>
    /// Gets the host environment forwarded to the AppSurface Docs standalone host.
    /// </summary>
    /// <remarks>
    /// Preview defaults to <c>Development</c> so the host can use deterministic per-workspace local endpoints. Export
    /// defaults to <c>Production</c> before starting the in-process host.
    /// </remarks>
    [CommandOption("environment", 'e', Description = "Host environment forwarded to the AppSurface Docs host.")]
    public string? EnvironmentName { get; init; }

    /// <summary>
    /// Gets the number of seconds to wait for the web host to start before failing fast.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds. Set to <c>0</c> to disable the startup watchdog. Negative, infinite, and NaN values are
    /// rejected before the host starts.
    /// </remarks>
    [CommandOption("startup-timeout-seconds", Description = "Seconds to wait for the AppSurface Docs web host to start before failing fast. Use 0 to disable.")]
    public double StartupTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Translates shared CLI options into standalone AppSurface Docs host arguments.
    /// </summary>
    /// <param name="defaultEnvironmentName">Environment to use when <see cref="EnvironmentName"/> is blank.</param>
    /// <returns>The repository root, forwarded host arguments, startup timeout, and resolved environment.</returns>
    internal AppSurfaceDocsHostArgs BuildHostArgs(string? defaultEnvironmentName)
    {
        return BuildHostArgs(urls: null, port: null, defaultEnvironmentName);
    }

    /// <summary>
    /// Translates shared and preview-only CLI options into standalone AppSurface Docs host arguments.
    /// </summary>
    /// <param name="urls">Optional explicit preview URL binding.</param>
    /// <param name="port">Optional preview port shortcut.</param>
    /// <param name="defaultEnvironmentName">Environment to use when <see cref="EnvironmentName"/> is blank.</param>
    /// <returns>The repository root, forwarded host arguments, startup timeout, and resolved environment.</returns>
    internal AppSurfaceDocsHostArgs BuildHostArgs(string? urls, int? port, string? defaultEnvironmentName)
    {
        if (string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            throw new CommandException("The --repo value must point to a repository directory.");
        }

        var repositoryRoot = Path.GetFullPath(RepositoryRoot);
        if (!Directory.Exists(repositoryRoot))
        {
            throw new CommandException($"The AppSurface Docs repository root does not exist: {repositoryRoot}");
        }

        var environmentName = ResolveEnvironmentName(defaultEnvironmentName);
        var args = new List<string>
        {
            "--AppSurfaceDocs:Source:RepositoryRoot",
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
            args.Add("--AppSurfaceDocs:Harvest:FailOnFailure");
            args.Add("true");
        }

        AddOptional(args, "--AppSurfaceDocs:Routing:RouteRootPath", RouteRootPath);
        AddOptional(args, "--AppSurfaceDocs:Routing:DocsRootPath", DocsRootPath);
        AddOptional(args, "--AppSurfaceDocs:Routing:PublicOrigin", PublicOrigin);
        AddOptional(args, "--environment", environmentName);

        return new AppSurfaceDocsHostArgs(repositoryRoot, args.ToArray(), ResolveStartupTimeout(), environmentName);
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

    /// <summary>
    /// Restores the previous process current directory when disposed.
    /// </summary>
    /// <remarks>
    /// The standalone host resolves some relative paths from the current directory, so preview and export temporarily
    /// scope it to the repository root while the host runs.
    /// </remarks>
    internal sealed class CurrentDirectoryScope : IDisposable
    {
        private readonly string _previousDirectory;

        /// <summary>
        /// Initializes a new instance of the <see cref="CurrentDirectoryScope"/> class.
        /// </summary>
        /// <param name="previousDirectory">Directory to restore when the scope is disposed.</param>
        private CurrentDirectoryScope(string previousDirectory)
        {
            _previousDirectory = previousDirectory;
        }

        /// <summary>
        /// Changes the process current directory and returns a scope that restores the previous value.
        /// </summary>
        /// <param name="directory">Directory to make current for the scope lifetime.</param>
        /// <returns>A disposable scope that restores the previous current directory.</returns>
        public static CurrentDirectoryScope ChangeTo(string directory)
        {
            var previousDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(directory);
            return new CurrentDirectoryScope(previousDirectory);
        }

        /// <summary>
        /// Restores the current directory captured when the scope was created.
        /// </summary>
        public void Dispose()
        {
            Directory.SetCurrentDirectory(_previousDirectory);
        }
    }
}

/// <summary>
/// Describes the AppSurface Docs host invocation produced by the CLI option translator.
/// </summary>
/// <param name="RepositoryRoot">Absolute repository root that the AppSurface Docs host should harvest.</param>
/// <param name="Args">Command-line arguments forwarded to the standalone AppSurface Docs host.</param>
/// <param name="StartupTimeout">Startup watchdog timeout, or <see langword="null"/> when disabled.</param>
/// <param name="EnvironmentName">Resolved host environment, or <see langword="null"/> when the host should use its default.</param>
internal readonly record struct AppSurfaceDocsHostArgs(
    string RepositoryRoot,
    string[] Args,
    TimeSpan? StartupTimeout,
    string? EnvironmentName);

/// <summary>
/// Describes a one-shot AppSurface Docs static export request.
/// </summary>
/// <param name="HostArgs">Standalone AppSurface Docs host arguments.</param>
/// <param name="OutputPath">Absolute output directory for exported files.</param>
/// <param name="SeedRoutesPath">Optional absolute seed-route file path.</param>
/// <param name="InitialSeedRoutes">Optional in-memory seed routes used when <paramref name="SeedRoutesPath"/> is null.</param>
/// <param name="Mode">RazorWire static export mode.</param>
/// <param name="RedirectStrategy">Redirect alias materialization strategy.</param>
/// <param name="RequestedBaseUrl">Loopback URL passed to Kestrel. The default uses port 0 so the OS chooses a free port.</param>
internal readonly record struct AppSurfaceDocsExportArgs(
    AppSurfaceDocsHostArgs HostArgs,
    string OutputPath,
    string? SeedRoutesPath,
    IReadOnlyList<string>? InitialSeedRoutes,
    ExportMode Mode,
    ExportRedirectStrategy RedirectStrategy,
    string RequestedBaseUrl);

/// <summary>
/// Applies shared host options required by packaged AppSurface docs tooling.
/// </summary>
internal static class AppSurfaceDocsCliHost
{
    private const string AspNetCoreCategory = "Microsoft.AspNetCore";
    private const string HostLifetimeCategory = "Microsoft.Hosting.Lifetime";
    private const string InternalHostCategory = "Microsoft.Extensions.Hosting.Internal.Host";
    private const string DocsCategory = "ForgeTrust.AppSurface.Docs";
    private const string WebStartupCategory = "ForgeTrust.AppSurface.Web";

    /// <summary>
    /// Configures the standalone AppSurface Docs host shape used by packaged preview and export commands.
    /// </summary>
    /// <param name="options">Web startup options to mutate.</param>
    /// <param name="startupTimeout">Startup watchdog timeout, or <see langword="null"/> when disabled.</param>
    public static void ConfigurePackagedToolHost(WebOptions options, TimeSpan? startupTimeout)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Packaged .NET tools often lack static web asset manifests; RazorWireWebModule and AppSurface Docs endpoint
        // fallbacks serve embedded assets instead so global/local tool distributions remain self-contained.
        options.StaticFiles.EnableStaticWebAssets = false;
        options.StartupTimeout = startupTimeout;
    }

    /// <summary>
    /// Suppresses routine ASP.NET Core host lifecycle output for interactive AppSurface Docs preview runs.
    /// </summary>
    /// <param name="logging">Logging builder for the preview host.</param>
    /// <remarks>
    /// The CLI prints the resolved docs URL itself after Kestrel starts, so routine messages such as
    /// <c>Now listening on</c>, <c>Application started</c>, AppSurface Web's endpoint fallback note, and routine docs
    /// harvest summaries would duplicate that command-owned status. Warnings and errors remain visible so startup and
    /// request failures are not hidden.
    /// </remarks>
    public static void ConfigureQuietPreviewLogging(ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.AddFilter(AspNetCoreCategory, LogLevel.Warning);
        logging.AddFilter(HostLifetimeCategory, LogLevel.Warning);
        logging.AddFilter(InternalHostCategory, LogLevel.Warning);
        logging.AddFilter(DocsCategory, LogLevel.Warning);
        logging.AddFilter(WebStartupCategory, LogLevel.Warning);
    }
}

/// <summary>
/// Starts a AppSurface Docs host for CLI preview commands.
/// </summary>
/// <remarks>
/// This seam keeps command parsing and validation testable without starting a real web host. Production implementations
/// should honor cancellation before delegating into long-running host lifetimes.
/// </remarks>
internal interface IAppSurfaceDocsHostRunner
{
    /// <summary>
    /// Runs the AppSurface Docs host with translated command-line arguments.
    /// </summary>
    /// <param name="args">Arguments forwarded to the standalone AppSurface Docs host.</param>
    /// <param name="startupTimeout">Startup watchdog timeout, or <see langword="null"/> to disable it.</param>
    /// <param name="cancellationToken">Token that cancels before the host is started.</param>
    /// <returns>A task that completes when the host exits.</returns>
    Task RunAsync(string[] args, TimeSpan? startupTimeout, CancellationToken cancellationToken);
}

/// <summary>
/// Starts the AppSurface Docs host and exports it to static files.
/// </summary>
internal interface IAppSurfaceDocsExportRunner
{
    /// <summary>
    /// Starts the docs host, runs static export, and stops the host.
    /// </summary>
    /// <param name="args">Resolved export arguments.</param>
    /// <param name="cancellationToken">Token observed during host startup and export.</param>
    /// <returns>A task that completes when export finishes.</returns>
    Task ExportAsync(AppSurfaceDocsExportArgs args, CancellationToken cancellationToken);
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
/// Adds AppSurface Docs-specific export graph state after the host starts and before RazorWire crawls it.
/// </summary>
internal interface IAppSurfaceDocsExportContextConfigurator
{
    /// <summary>
    /// Configures the export context using services from the started docs host.
    /// </summary>
    /// <param name="host">Started AppSurface Docs host.</param>
    /// <param name="context">Export context to configure.</param>
    /// <param name="cancellationToken">Token observed while resolving host state.</param>
    /// <returns>A task that completes after context configuration.</returns>
    Task ConfigureAsync(IHost host, ExportContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IAppSurfaceDocsHostRunner"/> that delegates to the standalone AppSurface Docs web host.
/// </summary>
/// <remarks>
/// Use this adapter for the packaged AppSurface CLI path. Tests should prefer fake runners so they can verify argument
/// translation without starting Kestrel. The type is internal and sealed because callers should depend on
/// <see cref="IAppSurfaceDocsHostRunner"/> rather than subclassing host lifetime behavior.
/// </remarks>
internal sealed class AppSurfaceDocsStandaloneHostRunner : IAppSurfaceDocsHostRunner
{
    private readonly ILogger<AppSurfaceDocsStandaloneHostRunner> _logger;
    private readonly IAppSurfaceDocsBrowserLauncher _browserLauncher;
    private readonly IAppSurfaceDocsPreviewHostStarter _hostStarter;
    private readonly IAppSurfaceDocsHarvestSummaryReader _harvestSummaryReader;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsStandaloneHostRunner"/> class.
    /// </summary>
    /// <param name="logger">Logger used for preview lifecycle diagnostics.</param>
    /// <param name="browserLauncher">Browser launcher used after the docs host is listening.</param>
    public AppSurfaceDocsStandaloneHostRunner(
        ILogger<AppSurfaceDocsStandaloneHostRunner> logger,
        IAppSurfaceDocsBrowserLauncher browserLauncher)
        : this(logger, browserLauncher, new AppSurfaceDocsStandalonePreviewHostStarter(), new AppSurfaceDocsHarvestSummaryReader())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsStandaloneHostRunner"/> class with an explicit starter.
    /// </summary>
    /// <param name="logger">Logger used for preview lifecycle diagnostics.</param>
    /// <param name="browserLauncher">Browser launcher used after the docs host is listening.</param>
    /// <param name="hostStarter">Host starter used to build and start the preview application.</param>
    internal AppSurfaceDocsStandaloneHostRunner(
        ILogger<AppSurfaceDocsStandaloneHostRunner> logger,
        IAppSurfaceDocsBrowserLauncher browserLauncher,
        IAppSurfaceDocsPreviewHostStarter hostStarter)
        : this(logger, browserLauncher, hostStarter, new AppSurfaceDocsHarvestSummaryReader())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsStandaloneHostRunner"/> class with explicit test seams.
    /// </summary>
    /// <param name="logger">Logger used for preview lifecycle diagnostics.</param>
    /// <param name="browserLauncher">Browser launcher used after the docs host is listening.</param>
    /// <param name="hostStarter">Host starter used to build and start the preview application.</param>
    /// <param name="harvestSummaryReader">Reader used to produce command-owned harvest summary output.</param>
    internal AppSurfaceDocsStandaloneHostRunner(
        ILogger<AppSurfaceDocsStandaloneHostRunner> logger,
        IAppSurfaceDocsBrowserLauncher browserLauncher,
        IAppSurfaceDocsPreviewHostStarter hostStarter,
        IAppSurfaceDocsHarvestSummaryReader harvestSummaryReader)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(browserLauncher);
        ArgumentNullException.ThrowIfNull(hostStarter);
        ArgumentNullException.ThrowIfNull(harvestSummaryReader);

        _logger = logger;
        _browserLauncher = browserLauncher;
        _hostStarter = hostStarter;
        _harvestSummaryReader = harvestSummaryReader;
    }

    /// <inheritdoc />
    public async Task RunAsync(string[] args, TimeSpan? startupTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IHost? host = null;

        try
        {
            var previewArgs = new AppSurfaceDocsPreviewHostArgs(args, startupTimeout);
            host = await BuildAndStartHostWithTimeoutAsync(previewArgs, cancellationToken);
            var baseUrl = AppSurfaceDocsPreviewUrlResolver.ResolveBoundBaseUrl(host);
            var docsUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDocsUrl(baseUrl, args);

            _logger.LogInformation("AppSurface Docs is ready at {DocsUrl}.", docsUrl);
            var browserLaunch = await _browserLauncher.TryOpenAsync(docsUrl, cancellationToken);
            if (!browserLaunch.Succeeded)
            {
                _logger.LogWarning("AppSurface Docs is ready, but the browser could not be opened automatically: {Reason}", browserLaunch.FailureReason);
            }

            await LogHarvestSummaryAsync(host, cancellationToken);
            await host.WaitForShutdownAsync(cancellationToken);
        }
        finally
        {
            if (host is not null)
            {
                await StopAndDisposeHostAsync(host);
            }
        }
    }

    private async Task LogHarvestSummaryAsync(IHost host, CancellationToken cancellationToken)
    {
        AppSurfaceDocsHarvestSummary? summary;
        try
        {
            summary = await _harvestSummaryReader.ReadAsync(host, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            _logger.LogWarning(ex, "AppSurface Docs harvest summary could not be read.");
            return;
        }

        if (summary is null)
        {
            return;
        }

        if (summary.DiagnosticCount > 0)
        {
            _logger.LogInformation(
                "Harvested {DocCount} docs from {SuccessfulHarvesters}/{TotalHarvesters} active harvesters. Status: {HarvestStatus}; diagnostics: {DiagnosticCount}.",
                summary.TotalDocs,
                summary.SuccessfulHarvesters,
                summary.TotalHarvesters,
                summary.Status,
                summary.DiagnosticCount);
            return;
        }

        _logger.LogInformation(
            "Harvested {DocCount} docs from {SuccessfulHarvesters}/{TotalHarvesters} active harvesters. Status: {HarvestStatus}.",
            summary.TotalDocs,
            summary.SuccessfulHarvesters,
            summary.TotalHarvesters,
            summary.Status);
    }

    private async Task<IHost> BuildAndStartHostWithTimeoutAsync(
        AppSurfaceDocsPreviewHostArgs args,
        CancellationToken cancellationToken)
    {
        if (args.StartupTimeout is null)
        {
            return await _hostStarter.BuildAndStartAsync(args, cancellationToken);
        }

        using var startupTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startupToken = startupTimeoutCts.Token;
        var startTask = Task.Run(
            () => _hostStarter.BuildAndStartAsync(args, startupToken),
            CancellationToken.None);

        try
        {
            var completedTask = await Task.WhenAny(startTask, Task.Delay(args.StartupTimeout.Value, cancellationToken));
            if (completedTask == startTask)
            {
                return await startTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (startTask.IsCompleted)
            {
                return await startTask;
            }

            await startupTimeoutCts.CancelAsync();
            ObserveLateStartupTask(startTask, "AppSurface Docs preview host startup task completed after the startup timeout.");
            throw CreateStartupTimeoutException(args.StartupTimeout.Value, innerException: null);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateStartupTimeoutException(args.StartupTimeout.Value, ex);
        }
        catch (OperationCanceledException)
        {
            ObserveLateStartupTask(startTask, "AppSurface Docs preview host startup task completed after external cancellation.");
            throw;
        }
    }

    private void ObserveLateStartupTask(Task<IHost> startTask, string lateCompletionMessage)
    {
        _ = ObserveLateStartupTaskAsync(startTask, lateCompletionMessage);
    }

    private async Task ObserveLateStartupTaskAsync(Task<IHost> startTask, string lateCompletionMessage)
    {
        try
        {
            var startedHost = await startTask.ConfigureAwait(false);
            await StopAndDisposeHostAsync(startedHost).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            _logger.LogDebug(ex, lateCompletionMessage);
        }
    }

    private static TimeoutException CreateStartupTimeoutException(TimeSpan startupTimeout, Exception? innerException)
    {
        var seconds = startupTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return new TimeoutException($"AppSurface Docs preview host did not start within {seconds} seconds.", innerException);
    }

    private async Task StopAndDisposeHostAsync(IHost host)
    {
        using var disposableHost = host;

        try
        {
            await host.StopAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            _logger.LogWarning(ex, "AppSurface Docs preview host failed during shutdown.");
        }
    }

}

/// <summary>
/// Shared exception classification helpers used by command cleanup and best-effort diagnostics.
/// </summary>
internal static class ExceptionFilters
{
    /// <summary>
    /// Determines whether an exception is safe to catch for cleanup, fallback logging, or diagnostic reporting.
    /// </summary>
    /// <param name="ex">Exception to classify.</param>
    /// <returns><see langword="true"/> when the exception is non-fatal and can be handled locally.</returns>
    internal static bool IsNonFatal(Exception ex)
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
/// Reads a command-owned harvest summary from a started AppSurface Docs preview host.
/// </summary>
internal interface IAppSurfaceDocsHarvestSummaryReader
{
    /// <summary>
    /// Reads the current docs harvest summary if the started host exposes the AppSurface Docs aggregator.
    /// </summary>
    /// <param name="host">Started preview host.</param>
    /// <param name="cancellationToken">Token observed while waiting for the first cached docs snapshot.</param>
    /// <returns>A summary when available; otherwise <see langword="null" />.</returns>
    Task<AppSurfaceDocsHarvestSummary?> ReadAsync(IHost host, CancellationToken cancellationToken);
}

/// <summary>
/// Concise harvest summary emitted by the AppSurface Docs CLI preview command.
/// </summary>
/// <param name="Status">Aggregate harvest status.</param>
/// <param name="TotalDocs">Number of final documentation nodes in the cached snapshot.</param>
/// <param name="TotalHarvesters">Number of active harvesters that participated in the snapshot.</param>
/// <param name="SuccessfulHarvesters">Number of harvesters that completed successfully.</param>
/// <param name="DiagnosticCount">Number of structured harvest diagnostics in the snapshot.</param>
internal sealed record AppSurfaceDocsHarvestSummary(
    DocHarvestHealthStatus Status,
    int TotalDocs,
    int TotalHarvesters,
    int SuccessfulHarvesters,
    int DiagnosticCount);

/// <summary>
/// Production <see cref="IAppSurfaceDocsHarvestSummaryReader"/> that reads <see cref="DocAggregator"/> health.
/// </summary>
internal sealed class AppSurfaceDocsHarvestSummaryReader : IAppSurfaceDocsHarvestSummaryReader
{
    /// <inheritdoc />
    public async Task<AppSurfaceDocsHarvestSummary?> ReadAsync(IHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);

        var aggregator = host.Services.GetService<DocAggregator>();
        if (aggregator is null)
        {
            return null;
        }

        var health = await aggregator.GetHarvestHealthAsync(cancellationToken);
        return new AppSurfaceDocsHarvestSummary(
            health.Status,
            health.TotalDocs,
            health.TotalHarvesters,
            health.SuccessfulHarvesters,
            health.Diagnostics.Count);
    }
}

/// <summary>
/// Describes a preview AppSurface Docs host startup request.
/// </summary>
/// <param name="Args">Arguments forwarded to the standalone AppSurface Docs host.</param>
/// <param name="StartupTimeout">Startup watchdog timeout, or <see langword="null"/> when disabled.</param>
internal readonly record struct AppSurfaceDocsPreviewHostArgs(string[] Args, TimeSpan? StartupTimeout);

/// <summary>
/// Builds and starts the AppSurface Docs preview host.
/// </summary>
internal interface IAppSurfaceDocsPreviewHostStarter
{
    /// <summary>
    /// Builds the standalone docs host, starts Kestrel, and returns the started host for preview.
    /// </summary>
    /// <param name="args">Resolved preview arguments.</param>
    /// <param name="cancellationToken">Token observed while starting the host.</param>
    /// <returns>The started host.</returns>
    Task<IHost> BuildAndStartAsync(AppSurfaceDocsPreviewHostArgs args, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IAppSurfaceDocsPreviewHostStarter"/> that uses the AppSurface Docs standalone host builder.
/// </summary>
[ExcludeFromCodeCoverage(
    Justification = "Production adapter delegates into the real standalone host builder and Kestrel; runner tests cover behavior before this boundary.")]
internal sealed class AppSurfaceDocsStandalonePreviewHostStarter : IAppSurfaceDocsPreviewHostStarter
{
    /// <inheritdoc />
    public async Task<IHost> BuildAndStartAsync(AppSurfaceDocsPreviewHostArgs args, CancellationToken cancellationToken)
    {
        var builder = AppSurfaceDocsStandaloneHost.CreateBuilder(
            args.Args,
            environmentProvider: null,
            options => AppSurfaceDocsCliHost.ConfigurePackagedToolHost(options, args.StartupTimeout));

        builder.ConfigureLogging(AppSurfaceDocsCliHost.ConfigureQuietPreviewLogging);

        var repositoryRoot = AppSurfaceDocsPreviewUrlResolver.ResolveRepositoryRoot(args.Args, Directory.GetCurrentDirectory());
        var defaultUrl = AppSurfaceDocsPreviewUrlResolver.ResolveDefaultPreviewUrl(args.Args, repositoryRoot);
        if (defaultUrl is not null)
        {
            builder.ConfigureWebHost(webHost => webHost.UseUrls(defaultUrl));
        }

        var host = builder.Build();
        try
        {
            await host.StartAsync(cancellationToken);
            return host;
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            host.Dispose();
            throw;
        }
    }

}

/// <summary>
/// Attempts to open the preview docs URL in the user's browser.
/// </summary>
internal interface IAppSurfaceDocsBrowserLauncher
{
    /// <summary>
    /// Attempts to open <paramref name="url"/> in the user's browser without failing the preview command.
    /// </summary>
    /// <param name="url">Absolute docs URL to open.</param>
    /// <param name="cancellationToken">Token observed before launch.</param>
    /// <returns>The browser launch outcome.</returns>
    Task<AppSurfaceDocsBrowserLaunchResult> TryOpenAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// Describes the outcome of an attempted browser launch.
/// </summary>
/// <param name="Succeeded">Whether a platform launch command was started successfully.</param>
/// <param name="FailureReason">User-facing failure detail when <paramref name="Succeeded"/> is <see langword="false"/>.</param>
internal readonly record struct AppSurfaceDocsBrowserLaunchResult(bool Succeeded, string? FailureReason)
{
    /// <summary>
    /// Gets a successful browser-launch result.
    /// </summary>
    public static AppSurfaceDocsBrowserLaunchResult Success { get; } = new(true, null);

    /// <summary>
    /// Creates a failed browser-launch result.
    /// </summary>
    /// <param name="reason">User-facing failure detail.</param>
    /// <returns>A failed browser-launch result.</returns>
    public static AppSurfaceDocsBrowserLaunchResult Failure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new AppSurfaceDocsBrowserLaunchResult(false, reason);
    }
}

/// <summary>
/// Browser launcher that uses the current operating system's conventional URL opener.
/// </summary>
internal sealed class SystemAppSurfaceDocsBrowserLauncher : IAppSurfaceDocsBrowserLauncher
{
    private readonly IAppSurfaceDocsBrowserOpenCommandRunner _commandRunner;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemAppSurfaceDocsBrowserLauncher"/> class.
    /// </summary>
    public SystemAppSurfaceDocsBrowserLauncher()
        : this(new CliWrapAppSurfaceDocsBrowserOpenCommandRunner())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemAppSurfaceDocsBrowserLauncher"/> class with an explicit command runner.
    /// </summary>
    /// <param name="commandRunner">Runner that invokes the platform URL opener.</param>
    internal SystemAppSurfaceDocsBrowserLauncher(IAppSurfaceDocsBrowserOpenCommandRunner commandRunner)
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        _commandRunner = commandRunner;
    }

    /// <inheritdoc />
    public async Task<AppSurfaceDocsBrowserLaunchResult> TryOpenAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _commandRunner.OpenAsync(url, cancellationToken);
            return AppSurfaceDocsBrowserLaunchResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            return AppSurfaceDocsBrowserLaunchResult.Failure(ex.Message);
        }
    }
}

/// <summary>
/// Runs the platform command that asks the operating system to open a browser URL.
/// </summary>
internal interface IAppSurfaceDocsBrowserOpenCommandRunner
{
    /// <summary>
    /// Opens the given URL with the platform opener command.
    /// </summary>
    /// <param name="url">Absolute URL to open.</param>
    /// <param name="cancellationToken">Token observed while starting the opener command.</param>
    /// <returns>A task that completes when the opener command exits.</returns>
    Task OpenAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// CliWrap-backed <see cref="IAppSurfaceDocsBrowserOpenCommandRunner"/> implementation.
/// </summary>
[ExcludeFromCodeCoverage(
    Justification = "Platform URL opener commands depend on the interactive user environment; launcher tests cover the runner seam.")]
internal sealed class CliWrapAppSurfaceDocsBrowserOpenCommandRunner : IAppSurfaceDocsBrowserOpenCommandRunner
{
    /// <inheritdoc />
    public async Task OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        await CreateCommand(url).ExecuteAsync(cancellationToken);
    }

    private static Command CreateCommand(Uri url)
    {
        if (OperatingSystem.IsWindows())
        {
            return CliCommand.Wrap("cmd")
                .WithArguments(["/c", "start", string.Empty, url.AbsoluteUri]);
        }

        return CliCommand.Wrap(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
            .WithArguments([url.AbsoluteUri]);
    }
}

/// <summary>
/// Resolves browser-facing URLs for AppSurface Docs preview hosts.
/// </summary>
internal static class AppSurfaceDocsPreviewUrlResolver
{
    /// <summary>
    /// Resolves the repository root forwarded to the standalone host.
    /// </summary>
    /// <param name="args">Arguments forwarded to the standalone host.</param>
    /// <param name="fallbackRoot">Fallback root used when the forwarded arguments do not contain a repository root.</param>
    /// <returns>The forwarded repository root when present; otherwise <paramref name="fallbackRoot"/>.</returns>
    internal static string ResolveRepositoryRoot(IReadOnlyList<string> args, string fallbackRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackRoot);

        return ResolveOptionValue(args, "--AppSurfaceDocs:Source:RepositoryRoot") ?? fallbackRoot;
    }

    /// <summary>
    /// Resolves the default preview listener when the CLI invocation did not configure an endpoint explicitly.
    /// </summary>
    /// <param name="args">Arguments forwarded to the standalone host.</param>
    /// <param name="repositoryRoot">Repository root used as the deterministic-port seed.</param>
    /// <returns>A localhost URL, or <see langword="null"/> when explicit endpoint configuration should win.</returns>
    internal static string? ResolveDefaultPreviewUrl(IReadOnlyList<string> args, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var resolution = AppSurfaceWebDevelopmentPortDefaults.Resolve(
            [.. args],
            repositoryRoot,
            repositoryRoot,
            System.Environment.GetEnvironmentVariable,
            System.Environment.GetEnvironmentVariables().Keys.Cast<string>());
        if (resolution.AppliedPort is null)
        {
            return null;
        }

        return $"http://localhost:{resolution.AppliedPort.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Resolves the browser-facing base URL from Kestrel's published server addresses.
    /// </summary>
    /// <param name="host">Started host that exposes Kestrel server addresses.</param>
    /// <returns>The scheme and authority that should be opened in the browser.</returns>
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

    /// <summary>
    /// Resolves the browser-facing base URL from Kestrel's published server addresses.
    /// </summary>
    /// <param name="addresses">Published server addresses.</param>
    /// <returns>The scheme and authority that should be opened in the browser.</returns>
    internal static string ResolveBoundBaseUrl(ICollection<string>? addresses)
    {
        if (addresses is null || addresses.Count == 0)
        {
            throw new InvalidOperationException("AppSurface Docs preview host did not publish a listening URL. No addresses were published.");
        }

        var candidates = addresses
            .Select(TryCreateBrowserUri)
            .Where(candidate => candidate is not null)
            .Cast<PreviewUriCandidate>()
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException($"AppSurface Docs preview host did not publish a valid listening URL. Values: '{string.Join("', '", addresses)}'.");
        }

        var uri = candidates.FirstOrDefault(candidate => !candidate.IsWildcard && candidate.Uri.IsLoopback).Uri
                  ?? candidates.FirstOrDefault(candidate => candidate.Uri.IsLoopback).Uri
                  ?? candidates.FirstOrDefault(candidate => IsAnyHostAddress(candidate.Uri)).Uri
                  ?? candidates[0].Uri;

        var builder = new UriBuilder(uri)
        {
            Host = IsAnyHostAddress(uri) ? "localhost" : uri.Host,
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// Combines the bound host base URL with the configured AppSurface Docs root path.
    /// </summary>
    /// <param name="baseUrl">Bound host base URL.</param>
    /// <param name="args">Arguments forwarded to the standalone host.</param>
    /// <returns>The absolute docs page URL to open.</returns>
    internal static Uri ResolveDocsUrl(string baseUrl, IReadOnlyList<string> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(args);

        var routeRootPath = ResolveOptionValue(args, "--AppSurfaceDocs:Routing:RouteRootPath");
        var docsRootPath = ResolveOptionValue(args, "--AppSurfaceDocs:Routing:DocsRootPath");
        var docsUrlBuilder = new DocsUrlBuilder(new AppSurfaceDocsOptions
        {
            Routing = new AppSurfaceDocsRoutingOptions
            {
                RouteRootPath = routeRootPath,
                DocsRootPath = docsRootPath
            }
        });

        return new Uri(
            new Uri(EnsureTrailingSlash(baseUrl)),
            docsUrlBuilder.CurrentDocsRootPath.TrimStart('/'));
    }

    private static string? ResolveOptionValue(IReadOnlyList<string> args, string optionName)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            var prefix = optionName + "=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[prefix.Length..];
            }

            if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Count
                && !args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool IsAnyHostAddress(Uri uri)
    {
        return string.Equals(uri.Host, "*", StringComparison.Ordinal)
               || string.Equals(uri.Host, "+", StringComparison.Ordinal)
               || string.Equals(uri.Host, "0.0.0.0", StringComparison.Ordinal)
               || string.Equals(uri.Host, "[::]", StringComparison.Ordinal)
               || string.Equals(uri.Host, "::", StringComparison.Ordinal);
    }

    private static PreviewUriCandidate? TryCreateBrowserUri(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return new PreviewUriCandidate(uri, IsAnyHostAddress(uri));
        }

        var isWildcard = address.Contains("://*:", StringComparison.Ordinal)
                         || address.Contains("://+:", StringComparison.Ordinal);
        var normalized = address
            .Replace("://*:", "://localhost:", StringComparison.Ordinal)
            .Replace("://+:", "://localhost:", StringComparison.Ordinal);

        return Uri.TryCreate(normalized, UriKind.Absolute, out uri)
            ? new PreviewUriCandidate(uri, isWildcard)
            : null;
    }

    private readonly record struct PreviewUriCandidate(Uri Uri, bool IsWildcard);

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal)
            ? value
            : value + "/";
    }

}

/// <summary>
/// Production export runner that starts the standalone AppSurface Docs host in-process and exports it over real loopback HTTP.
/// </summary>
internal sealed class AppSurfaceDocsInProcessExportRunner : IAppSurfaceDocsExportRunner
{
    private readonly ILogger<AppSurfaceDocsInProcessExportRunner> _logger;
    private readonly IRazorWireStaticExporter _exporter;
    private readonly IAppSurfaceDocsExportHostStarter _hostStarter;
    private readonly IAppSurfaceDocsExportContextConfigurator _contextConfigurator;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsInProcessExportRunner"/> class.
    /// </summary>
    /// <param name="logger">Logger used for host lifecycle diagnostics.</param>
    /// <param name="exporter">Static exporter invoked after the in-process host starts.</param>
    public AppSurfaceDocsInProcessExportRunner(
        ILogger<AppSurfaceDocsInProcessExportRunner> logger,
        IRazorWireStaticExporter exporter)
        : this(logger, exporter, new AppSurfaceDocsStandaloneExportHostStarter(), new AppSurfaceDocsExportContextConfigurator())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsInProcessExportRunner"/> class with an explicit host starter.
    /// </summary>
    /// <param name="logger">Logger used for host lifecycle diagnostics.</param>
    /// <param name="exporter">Static exporter invoked after the in-process host starts.</param>
    /// <param name="hostStarter">Host starter used to build and start the docs application.</param>
    internal AppSurfaceDocsInProcessExportRunner(
        ILogger<AppSurfaceDocsInProcessExportRunner> logger,
        IRazorWireStaticExporter exporter,
        IAppSurfaceDocsExportHostStarter hostStarter)
        : this(logger, exporter, hostStarter, NoOpAppSurfaceDocsExportContextConfigurator.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceDocsInProcessExportRunner"/> class with explicit test seams.
    /// </summary>
    /// <param name="logger">Logger used for host lifecycle diagnostics.</param>
    /// <param name="exporter">Static exporter invoked after the in-process host starts.</param>
    /// <param name="hostStarter">Host starter used to build and start the docs application.</param>
    /// <param name="contextConfigurator">Configurator that registers docs-specific export graph state.</param>
    internal AppSurfaceDocsInProcessExportRunner(
        ILogger<AppSurfaceDocsInProcessExportRunner> logger,
        IRazorWireStaticExporter exporter,
        IAppSurfaceDocsExportHostStarter hostStarter,
        IAppSurfaceDocsExportContextConfigurator contextConfigurator)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(hostStarter);
        ArgumentNullException.ThrowIfNull(contextConfigurator);

        _logger = logger;
        _exporter = exporter;
        _hostStarter = hostStarter;
        _contextConfigurator = contextConfigurator;
    }

    /// <inheritdoc />
    public async Task ExportAsync(AppSurfaceDocsExportArgs args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var environmentName = args.HostArgs.EnvironmentName ?? Environments.Production;
        IHost? host = null;
        using var currentDirectory = AppSurfaceDocsRepositoryCommand.CurrentDirectoryScope.ChangeTo(args.HostArgs.RepositoryRoot);

        try
        {
            host = await BuildAndStartHostWithTimeoutAsync(args, environmentName, cancellationToken);

            var baseUrl = ResolveBoundBaseUrl(host);
            _logger.LogInformation("AppSurface Docs export host started at {BaseUrl}.", baseUrl);

            var context = new ExportContext(
                args.OutputPath,
                args.SeedRoutesPath,
                args.InitialSeedRoutes,
                baseUrl,
                args.Mode,
                args.RedirectStrategy);

            await _contextConfigurator.ConfigureAsync(host, context, cancellationToken);

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

    /// <summary>
    /// Resolves the single bound loopback base URL published by the started export host.
    /// </summary>
    /// <param name="host">Started host that exposes Kestrel server addresses.</param>
    /// <returns>The scheme and authority used by the export crawler.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the host does not publish exactly one valid loopback URL.</exception>
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

    /// <summary>
    /// Resolves the crawler base URL from a Kestrel address collection.
    /// </summary>
    /// <param name="addresses">Published server addresses.</param>
    /// <returns>The absolute URL authority to crawl.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no URL, multiple URLs, an invalid URL, or a non-loopback URL is published.</exception>
    internal static string ResolveBoundBaseUrl(ICollection<string>? addresses)
    {
        if (addresses is null || addresses.Count == 0)
        {
            throw new InvalidOperationException("AppSurface Docs export host did not publish a listening URL. No addresses were published.");
        }

        if (addresses.Count != 1)
        {
            throw new InvalidOperationException($"AppSurface Docs export host published {addresses.Count} listening URLs; expected exactly one. Values: '{string.Join("', '", addresses)}'.");
        }

        var baseAddress = addresses.Single();
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"AppSurface Docs export host did not publish a valid listening URL. Value: '{baseAddress}'.");
        }

        if (!uri.IsLoopback)
        {
            throw new InvalidOperationException(
                $"AppSurface Docs export host published non-loopback URL '{baseAddress}'; expected a single loopback listener.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// Builds and starts the docs host while enforcing the configured startup watchdog.
    /// </summary>
    /// <param name="args">Resolved export arguments.</param>
    /// <param name="environmentName">Environment name applied to the standalone host.</param>
    /// <param name="cancellationToken">External cancellation token for the export operation.</param>
    /// <returns>The started host.</returns>
    /// <exception cref="TimeoutException">Thrown when the host does not start before the startup timeout.</exception>
    private async Task<IHost> BuildAndStartHostWithTimeoutAsync(
        AppSurfaceDocsExportArgs args,
        string environmentName,
        CancellationToken cancellationToken)
    {
        var startupTimeout = args.HostArgs.StartupTimeout;
        if (startupTimeout is null)
        {
            return await _hostStarter.BuildAndStartAsync(args, environmentName, cancellationToken);
        }

        using var startupTimeoutCts = CreateStartupTimeout(startupTimeout.Value, cancellationToken);
        var startupToken = startupTimeoutCts.Token;
        var startTask = Task.Run(
            () => _hostStarter.BuildAndStartAsync(args, environmentName, startupToken),
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

                await startupTimeoutCts.CancelAsync();
                ObserveTimedOutStartupTask(startTask, startupTimeoutCts.Transfer());
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
            ObserveCanceledStartupTask(startTask, startupTimeoutCts.Transfer());
            throw;
        }
    }

    /// <summary>
    /// Creates the linked cancellation source used to distinguish startup timeout from external cancellation.
    /// </summary>
    /// <param name="startupTimeout">Startup timeout to enforce.</param>
    /// <param name="cancellationToken">External cancellation token to link.</param>
    /// <returns>A disposable lease for the linked startup cancellation source.</returns>
    private static StartupTimeoutCancellationLease CreateStartupTimeout(TimeSpan startupTimeout, CancellationToken cancellationToken)
    {
        var startupTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeoutCts.CancelAfter(startupTimeout);
        return new StartupTimeoutCancellationLease(startupTimeoutCts);
    }

    /// <summary>
    /// Observes a startup task that outlived the configured timeout so late completion can stop or log the host.
    /// </summary>
    /// <param name="startTask">Background startup task to observe.</param>
    /// <param name="startupTimeoutCts">Cancellation source transferred from the timeout branch.</param>
    private void ObserveTimedOutStartupTask(Task<IHost> startTask, CancellationTokenSource startupTimeoutCts)
    {
        _ = ObserveStartupTaskAsync(
            startTask,
            startupTimeoutCts,
            "AppSurface Docs export host startup task completed after the startup timeout.");
    }

    /// <summary>
    /// Observes a startup task that outlived external cancellation so late completion can stop or log the host.
    /// </summary>
    /// <param name="startTask">Background startup task to observe.</param>
    /// <param name="startupTimeoutCts">Cancellation source transferred from the external cancellation branch.</param>
    private void ObserveCanceledStartupTask(Task<IHost> startTask, CancellationTokenSource startupTimeoutCts)
    {
        _ = ObserveStartupTaskAsync(
            startTask,
            startupTimeoutCts,
            "AppSurface Docs export host startup task completed after external cancellation.");
    }

    /// <summary>
    /// Awaits a late startup task and disposes the host if startup eventually succeeds.
    /// </summary>
    /// <param name="startTask">Background startup task to observe.</param>
    /// <param name="startupTimeoutCts">Cancellation source owned by the observation task.</param>
    /// <param name="lateCompletionMessage">Debug message used when late startup faults.</param>
    /// <returns>A task that completes after the late startup task is observed.</returns>
    private async Task ObserveStartupTaskAsync(
        Task<IHost> startTask,
        CancellationTokenSource startupTimeoutCts,
        string lateCompletionMessage)
    {
        using var cts = startupTimeoutCts;

        try
        {
            var startedHost = await startTask.ConfigureAwait(false);
            await StopAndDisposeHostAsync(startedHost).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            _logger.LogDebug(ex, lateCompletionMessage);
        }
    }

    /// <summary>
    /// Creates the user-facing timeout exception for a host that failed to start in time.
    /// </summary>
    /// <param name="startupTimeout">Timeout that elapsed.</param>
    /// <param name="innerException">Optional cancellation exception that came from the startup token.</param>
    /// <returns>The timeout exception reported to command execution.</returns>
    private static TimeoutException CreateStartupTimeoutException(TimeSpan startupTimeout, Exception? innerException)
    {
        var seconds = startupTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return new TimeoutException($"AppSurface Docs export host did not start within {seconds} seconds.", innerException);
    }

    /// <summary>
    /// Stops the export host and always disposes it, logging non-fatal shutdown failures.
    /// </summary>
    /// <param name="host">Host to stop and dispose.</param>
    /// <returns>A task that completes after shutdown and disposal.</returns>
    private async Task StopAndDisposeHostAsync(IHost host)
    {
        using var disposableHost = host;

        try
        {
            await host.StopAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            _logger.LogWarning(ex, "AppSurface Docs export host failed during shutdown.");
        }
    }

    /// <summary>
    /// Owns a startup cancellation source until timeout observation needs to transfer that ownership.
    /// </summary>
    private sealed class StartupTimeoutCancellationLease : IDisposable
    {
        private CancellationTokenSource? _source;

        /// <summary>
        /// Initializes a new instance of the <see cref="StartupTimeoutCancellationLease"/> class.
        /// </summary>
        /// <param name="source">Linked cancellation source to own.</param>
        public StartupTimeoutCancellationLease(CancellationTokenSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _source = source;
        }

        /// <summary>
        /// Gets the token exposed by the owned startup cancellation source.
        /// </summary>
        public CancellationToken Token => (_source ?? throw new ObjectDisposedException(nameof(StartupTimeoutCancellationLease))).Token;

        /// <summary>
        /// Requests cancellation of the owned startup cancellation source.
        /// </summary>
        /// <returns>A task that completes after cancellation callbacks have run.</returns>
        public Task CancelAsync()
        {
            return (_source ?? throw new ObjectDisposedException(nameof(StartupTimeoutCancellationLease))).CancelAsync();
        }

        /// <summary>
        /// Transfers cancellation source ownership to a late-startup observer.
        /// </summary>
        /// <returns>The owned cancellation source.</returns>
        public CancellationTokenSource Transfer()
        {
            var source = _source ?? throw new ObjectDisposedException(nameof(StartupTimeoutCancellationLease));
            _source = null;
            return source;
        }

        /// <summary>
        /// Disposes the owned cancellation source unless ownership has been transferred.
        /// </summary>
        public void Dispose()
        {
            _source?.Dispose();
        }
    }

}

/// <summary>
/// Default AppSurface Docs export configurator that publishes docs route aliases into RazorWire's export graph.
/// </summary>
internal sealed class AppSurfaceDocsExportContextConfigurator : IAppSurfaceDocsExportContextConfigurator
{
    /// <inheritdoc />
    public async Task ConfigureAsync(IHost host, ExportContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(context);

        var routeManifest = await host.Services
            .GetRequiredService<DocAggregator>()
            .GetRouteManifestAsync(cancellationToken);

        foreach (var entry in routeManifest.Entries)
        {
            context.AddSeedRoute(entry.CanonicalLiveUrl);

            foreach (var alias in entry.RecoveryAliases.Concat(entry.DeclaredAliases))
            {
                context.AddRedirectAlias(alias.LiveUrl, entry.CanonicalLiveUrl);
            }
        }
    }
}

/// <summary>
/// Test-seam configurator used when unit tests provide a fake host that does not contain AppSurface Docs services.
/// </summary>
internal sealed class NoOpAppSurfaceDocsExportContextConfigurator : IAppSurfaceDocsExportContextConfigurator
{
    public static readonly NoOpAppSurfaceDocsExportContextConfigurator Instance = new();

    private NoOpAppSurfaceDocsExportContextConfigurator()
    {
    }

    /// <inheritdoc />
    public Task ConfigureAsync(IHost host, ExportContext context, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds and starts the in-process AppSurface Docs export host.
/// </summary>
internal interface IAppSurfaceDocsExportHostStarter
{
    /// <summary>
    /// Builds the standalone docs host, starts Kestrel, and returns the started host for export.
    /// </summary>
    /// <param name="args">Resolved export arguments.</param>
    /// <param name="environmentName">Resolved host environment.</param>
    /// <param name="cancellationToken">Token observed while starting the host.</param>
    /// <returns>The started host.</returns>
    Task<IHost> BuildAndStartAsync(
        AppSurfaceDocsExportArgs args,
        string environmentName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IAppSurfaceDocsExportHostStarter"/> that uses the AppSurface Docs standalone host builder.
/// </summary>
[ExcludeFromCodeCoverage(
    Justification = "Production adapter delegates into the real standalone host builder and Kestrel; command and runner tests cover behavior before this boundary.")]
internal sealed class AppSurfaceDocsStandaloneExportHostStarter : IAppSurfaceDocsExportHostStarter
{
    /// <inheritdoc />
    public async Task<IHost> BuildAndStartAsync(
        AppSurfaceDocsExportArgs args,
        string environmentName,
        CancellationToken cancellationToken)
    {
        var builder = AppSurfaceDocsStandaloneHost.CreateBuilder(
            args.HostArgs.Args,
            new FixedEnvironmentProvider(environmentName),
            options => AppSurfaceDocsCliHost.ConfigurePackagedToolHost(options, args.HostArgs.StartupTimeout));

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
        catch (Exception ex) when (ExceptionFilters.IsNonFatal(ex))
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Provides a fixed environment name to the standalone host builder during export startup.
    /// </summary>
    private sealed class FixedEnvironmentProvider : IEnvironmentProvider
    {
        private readonly string _environmentName;

        /// <summary>
        /// Initializes a new instance of the <see cref="FixedEnvironmentProvider"/> class.
        /// </summary>
        /// <param name="environmentName">Environment name exposed to the host builder.</param>
        public FixedEnvironmentProvider(string environmentName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
            _environmentName = environmentName;
        }

        /// <summary>
        /// Gets the fixed environment name.
        /// </summary>
        public string Environment => _environmentName;

        /// <summary>
        /// Gets a value indicating whether the fixed environment is Development.
        /// </summary>
        public bool IsDevelopment => string.Equals(_environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets environment variable values while overriding ASP.NET and .NET environment variables.
        /// </summary>
        /// <param name="name">Environment variable name.</param>
        /// <param name="defaultValue">Fallback value when the variable is not set.</param>
        /// <returns>The fixed host environment for environment-name variables, otherwise the process value or fallback.</returns>
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
