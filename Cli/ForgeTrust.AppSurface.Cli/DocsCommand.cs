using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Docs.Standalone;
using ForgeTrust.AppSurface.Web;
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
[Command("docs", Description = "Preview RazorDocs for a repository.")]
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
/// Shared implementation for RazorDocs preview commands.
/// </summary>
/// <remarks>
/// The base command translates CLI options into RazorDocs standalone host arguments. It keeps command parsing separate
/// from process hosting so tests can verify validation and argument forwarding without starting Kestrel.
/// </remarks>
internal abstract class RazorDocsPreviewCommand : ICommand
{
    private static readonly string DefaultPreviewEnvironmentName = Environments.Development;

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
    /// Gets the repository root to harvest and preview.
    /// </summary>
    /// <remarks>
    /// Defaults to the current directory. Use this when running the CLI from a parent directory, script workspace, or
    /// package output folder. The value must resolve to an existing directory.
    /// </remarks>
    [CommandOption("repo", 'r', Description = "Repository root to preview (default: current directory).")]
    public string RepositoryRoot { get; init; } = ".";

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
    /// Gets a value indicating whether startup should fail when every configured RazorDocs harvester fails.
    /// </summary>
    /// <remarks>
    /// Keep this off for exploratory local preview. Enable it in CI or release checks where a completely failed harvest
    /// should stop the command before the site starts.
    /// </remarks>
    [CommandOption("strict", Description = "Fail startup when every configured RazorDocs harvester fails.")]
    public bool StrictHarvest { get; init; }

    /// <summary>
    /// Gets the route-family root for RazorDocs version and archive routes.
    /// </summary>
    /// <remarks>
    /// Use this when the docs route family is mounted somewhere other than <c>/docs</c>, for example
    /// <c>--route-root /reference</c>. Pair it with <see cref="DocsRootPath"/> when the live preview path should differ
    /// from archive/version routes.
    /// </remarks>
    [CommandOption("route-root", Description = "Route-family root for RazorDocs version and archive routes.")]
    public string? RouteRootPath { get; init; }

    /// <summary>
    /// Gets the live docs preview root path.
    /// </summary>
    /// <remarks>
    /// Use this to preview current docs under a nested route, for example <c>--route-root /reference --docs-root
    /// /reference/next</c>. Leave unset to use RazorDocs defaults.
    /// </remarks>
    [CommandOption("docs-root", Description = "Live docs preview root path.")]
    public string? DocsRootPath { get; init; }

    /// <summary>
    /// Gets the host environment forwarded to the RazorDocs standalone host.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>Development</c> so local previews receive AppSurface Web's deterministic per-workspace endpoint
    /// fallback when no URL or port is supplied. Set this explicitly when testing production or staging behavior.
    /// </remarks>
    [CommandOption("environment", 'e', Description = "Host environment forwarded to the RazorDocs host (default: Development).")]
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
        var hostArgs = BuildHostArgs();
        _logger.LogInformation("Starting RazorDocs preview for {RepositoryRoot}.", hostArgs.RepositoryRoot);
        using var currentDirectory = CurrentDirectoryScope.ChangeTo(hostArgs.RepositoryRoot);
        await _hostRunner.RunAsync(hostArgs.Args, hostArgs.StartupTimeout, cancellationToken);
    }

    /// <summary>
    /// Translates CLI options into standalone RazorDocs host arguments.
    /// </summary>
    /// <returns>The repository root, forwarded host arguments, and startup timeout for the preview run.</returns>
    /// <remarks>
    /// This method performs command-level validation so users receive <see cref="CommandException"/> errors before the
    /// web host starts. It is internal so tests can verify the translation contract without opening a listener.
    /// </remarks>
    internal RazorDocsHostArgs BuildHostArgs()
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

        var args = new List<string>
        {
            "--RazorDocs:Source:RepositoryRoot",
            repositoryRoot
        };

        AddOptional(args, "--urls", Urls);
        if (Port is not null)
        {
            if (Port is < 1 or > 65535)
            {
                throw new CommandException("The --port value must be between 1 and 65535.");
            }

            args.Add("--port");
            args.Add(Port.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (StrictHarvest)
        {
            args.Add("--RazorDocs:Harvest:FailOnFailure");
            args.Add("true");
        }

        AddOptional(args, "--RazorDocs:Routing:RouteRootPath", RouteRootPath);
        AddOptional(args, "--RazorDocs:Routing:DocsRootPath", DocsRootPath);
        AddOptional(args, "--environment", ResolveEnvironmentName());

        return new RazorDocsHostArgs(repositoryRoot, args.ToArray(), ResolveStartupTimeout());
    }

    private string ResolveEnvironmentName()
    {
        return string.IsNullOrWhiteSpace(EnvironmentName)
            ? DefaultPreviewEnvironmentName
            : EnvironmentName;
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

    private sealed class CurrentDirectoryScope : IDisposable
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
internal readonly record struct RazorDocsHostArgs(string RepositoryRoot, string[] Args, TimeSpan? StartupTimeout);

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
        return RazorDocsStandaloneHost.RunAsync(args, options => ConfigurePackagedToolHost(options, startupTimeout));
    }

    private static void ConfigurePackagedToolHost(WebOptions options, TimeSpan? startupTimeout)
    {
        // Packaged .NET tools often lack static web asset manifests; RazorWireWebModule and RazorDocs endpoint
        // fallbacks serve embedded assets instead so global/local tool distributions remain self-contained.
        options.StaticFiles.EnableStaticWebAssets = false;
        options.StartupTimeout = startupTimeout;
    }
}
