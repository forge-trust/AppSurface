using System.Diagnostics.CodeAnalysis;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;
using ForgeTrust.RazorWire;
using ForgeTrust.RazorWire.Cli;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Exports an AppSurface/RazorWire application through the product-facing <c>appsurface export</c> command.
/// </summary>
/// <remarks>
/// This command delegates crawling, hybrid split-origin rewriting, validation, and materialization to the shared
/// RazorWire export engine while keeping the common AppSurface workflow discoverable from the AppSurface CLI.
/// </remarks>
[Command("export", Description = "Export an AppSurface/RazorWire application to static or hybrid files.")]
internal sealed partial class AppSurfaceExportCommand : ICommand
{
    private readonly ILogger<AppSurfaceExportCommand> _logger;
    private readonly ExportEngine _engine;
    private readonly ExportSourceRequestFactory _requestFactory;
    private readonly ExportSourceResolver _sourceResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSurfaceExportCommand"/> class.
    /// </summary>
    /// <param name="logger">Logger used to report export progress and diagnostics.</param>
    /// <param name="engine">Shared RazorWire export engine that crawls, validates, rewrites, and writes artifacts.</param>
    /// <param name="requestFactory">Factory that validates the mutually exclusive source options.</param>
    /// <param name="sourceResolver">Resolver that turns a URL, project, or DLL source into a crawlable base URL.</param>
    public AppSurfaceExportCommand(
        ILogger<AppSurfaceExportCommand> logger,
        ExportEngine engine,
        ExportSourceRequestFactory requestFactory,
        ExportSourceResolver sourceResolver)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _requestFactory = requestFactory ?? throw new ArgumentNullException(nameof(requestFactory));
        _sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
    }

    [CommandOption("output", 'o', Description = "Output directory for exported files (default: dist).")]
    public string OutputPath { get; set; } = "dist";

    [CommandOption("seeds", Description = "Path to a file containing seed routes.")]
    public string? SeedRoutesPath { get; set; }

    /// <summary>
    /// Gets or sets an optional path to a publish-root deployment extras manifest.
    /// </summary>
    /// <remarks>
    /// The manifest declares explicit deployment-owned files, such as <c>CNAME</c>, that should be copied into the
    /// publish root after export proves they do not collide with generated output. This option is distinct from
    /// <see cref="SeedRoutesPath"/>, which contains routes to crawl.
    /// </remarks>
    [CommandOption("publish-root-extras", Description = "Path to a YAML manifest for explicit deployment-owned publish-root files; not route seeds.")]
    public string? PublishRootExtrasPath { get; set; }

    [CommandOption("mode", 'm', Description = "Export mode: cdn (default) or hybrid.")]
    public ExportMode Mode { get; set; } = ExportMode.Cdn;

    [CommandOption("url", 'u', Description = "Base URL of a running application to crawl.")]
    public string? BaseUrl { get; set; }

    [CommandOption("project", 'p', Description = "Path to a .csproj to run and export.")]
    public string? ProjectPath { get; set; }

    [CommandOption("dll", 'd', Description = "Path to a .dll to run and export.")]
    public string? DllPath { get; set; }

    [CommandOption("framework", 'f', Description = "Target framework (required for multi-target projects).")]
    public string? Framework { get; set; }

    [CommandOption("app-args", Description = "Repeatable app argument token to pass through to the launched target app.")]
    public string[] AppArgs { get; set; } = [];

    [CommandOption("no-build", Description = "Project mode only: skip build before launch.")]
    public bool NoBuild { get; set; }

    [CommandOption("public-origin", Description = "Public static origin for same-origin canonical metadata, such as https://www.example.com.")]
    public string? PublicOrigin { get; set; }

    /// <summary>
    /// Gets or sets the optional live origin for RazorWire-managed split-origin hybrid interactions.
    /// </summary>
    /// <remarks>
    /// The value must be an absolute <c>http</c> or <c>https</c> origin with no path, query string, fragment, or
    /// userinfo. Use this when the exported static files are served from one origin but RazorWire-owned live streams,
    /// islands, and lazy anti-forgery forms must call a different live app origin. Leave it unset for same-origin
    /// hybrid deployments.
    /// </remarks>
    [CommandOption("live-origin", Description = "Live origin for RazorWire-managed hybrid interactions, such as https://api.example.com.")]
    public string? LiveOrigin { get; set; }

    /// <summary>
    /// Gets or sets credential behavior for RazorWire-managed live calls in hybrid output.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="RazorWireHybridCredentialsMode.Auto"/>, which includes credentials only when
    /// <see cref="LiveOrigin"/> is set. Use <see cref="RazorWireHybridCredentialsMode.Include"/> for cookie-backed
    /// cross-origin live apps and <see cref="RazorWireHybridCredentialsMode.Omit"/> for public live surfaces that do not
    /// depend on cookies or anti-forgery token refreshes.
    /// </remarks>
    [CommandOption("hybrid-credentials", Description = "Hybrid credentials mode: auto (default), include, or omit.")]
    public RazorWireHybridCredentialsMode HybridCredentials { get; set; } = RazorWireHybridCredentialsMode.Auto;

    [ExcludeFromCodeCoverage]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        await ExecuteAsync(console, console.RegisterCancellationHandler());
    }

    internal async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        try
        {
            if (!ExportHybridOptions.TryNormalizeOrigin(LiveOrigin, out var normalizedLiveOrigin))
            {
                throw new CommandException("The --live-origin value must be an absolute http or https origin, such as 'https://api.example.com', with no path, query string, fragment, or userinfo.");
            }

            if (!ExportHybridOptions.TryNormalizeOrigin(PublicOrigin, out var normalizedPublicOrigin))
            {
                throw new CommandException("The --public-origin value must be an absolute http or https origin, such as 'https://example.com', with no path, query string, fragment, or userinfo.");
            }

            IReadOnlyList<ExportDeploymentExtra> deploymentExtras = string.IsNullOrWhiteSpace(PublishRootExtrasPath)
                ? []
                : ExportDeploymentExtrasManifestReader.Read(PublishRootExtrasPath);
            var request = _requestFactory.Create(BaseUrl, ProjectPath, DllPath, Framework, AppArgs, NoBuild);
            await using var resolvedSource = await _sourceResolver.ResolveAsync(request, cancellationToken);

            _logger.LogInformation("Exporting AppSurface app to {OutputPath}.", OutputPath);
            var context = new ExportContext(
                OutputPath,
                SeedRoutesPath,
                initialSeedRoutes: null,
                resolvedSource.BaseUrl,
                Mode,
                ExportRedirectStrategy.Html,
                new ExportHybridOptions
                {
                    LiveOrigin = normalizedLiveOrigin,
                    CredentialsMode = HybridCredentials
                },
                publicOrigin: normalizedPublicOrigin);
            foreach (var extra in deploymentExtras)
            {
                context.AddDeploymentExtra(extra);
            }

            await _engine.RunAsync(context, cancellationToken);
        }
        catch (ExportValidationException ex)
        {
            throw new CommandException(ex.Message);
        }
    }
}
