using System.Diagnostics.CodeAnalysis;
using CliFx;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// A command for exporting a RazorWire site to a static directory.
/// </summary>
[Command("export", Description = "Export a RazorWire site to a static directory.")]
public class ExportCommand : ICommand
{
    /// <summary>
    /// Gets or sets the path to the directory where the exported site will be written.
    /// Defaults to <c>"dist"</c>.
    /// </summary>
    [CommandOption("output", 'o', Description = "Output directory (default: dist).")]
    public string OutputPath { get; init; } = "dist";

    /// <summary>
    /// Gets or sets an optional path to a plain-text file containing one initial seed route per line.
    /// </summary>
    /// <remarks>
    /// This property is bound from the <c>-r|--seeds</c> command option. When it is <see langword="null"/>
    /// or empty, the exporter starts from the root route (<c>/</c>). When it points to a file, the exporter
    /// reads each line, accepts root-relative routes and absolute HTTP(S) URLs, strips query strings and
    /// fragments during normalization, and skips invalid, external, hash-only, JavaScript, or mailto entries.
    /// If the file is missing or unreadable, export fails and returns a non-zero CLI exit code. If the file is
    /// readable but contains no valid routes, the exporter logs a warning and falls back to the root route.
    /// </remarks>
    [CommandOption("seeds", 'r', Description = "Path to a file containing seed routes.")]
    public string? SeedRoutesPath { get; init; }

    /// <summary>
    /// Gets or sets the export mode that controls whether output is rewritten for static CDN hosting.
    /// </summary>
    /// <remarks>
    /// <see cref="ExportMode.Cdn"/> is the default and emits fully static output for plain static hosts. Use
    /// <see cref="ExportMode.Hybrid"/> when the exported directory will still be served behind application-style
    /// routing that can resolve extensionless URLs.
    /// </remarks>
    [CommandOption("mode", 'm', Description = "Export mode: cdn (default) or hybrid.")]
    public ExportMode Mode { get; init; } = ExportMode.Cdn;

    /// <summary>
    /// Gets or sets the base URL of a running application to crawl.
    /// </summary>
    [CommandOption("url", 'u', Description = "Base URL of a running application to crawl.")]
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Gets or sets a path to a .csproj file to run and export.
    /// </summary>
    [CommandOption("project", 'p', Description = "Path to a .csproj to run and export.")]
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Gets or sets a path to a .dll file to run and export.
    /// </summary>
    [CommandOption("dll", 'd', Description = "Path to a .dll to run and export.")]
    public string? DllPath { get; init; }

    /// <summary>
    /// Gets or sets an optional target framework for project exports, required for multi-target projects.
    /// </summary>
    [CommandOption("framework", 'f', Description = "Target framework (required for multi-target projects).")]
    public string? Framework { get; init; }

    /// <summary>
    /// Gets or sets app arguments forwarded to the launched target app.
    /// Repeat this option for each token.
    /// </summary>
    [CommandOption("app-args", Description = "Repeatable app argument token to pass through to the launched target app.")]
    public string[] AppArgs { get; init; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether project mode should skip build before launch.
    /// </summary>
    [CommandOption("no-build", Description = "Project mode only: skip build before launch.")]
    public bool NoBuild { get; init; }

    private readonly ILogger<ExportCommand> _logger;
    private readonly ExportEngine _engine;
    private readonly ExportSourceRequestFactory _requestFactory;
    private readonly ExportSourceResolver _sourceResolver;

    /// <summary>
    /// Initializes a new instance of <see cref="ExportCommand"/> with the required logger and export engine.
    /// </summary>
    /// <param name="logger">The logger for reporting command status.</param>
    /// <param name="engine">The engine that performs the export operation.</param>
    /// <param name="requestFactory">The factory that validates and creates an export source request.</param>
    /// <param name="sourceResolver">The resolver that turns a source request into a crawlable base URL.</param>
    public ExportCommand(
        ILogger<ExportCommand> logger,
        ExportEngine engine,
        ExportSourceRequestFactory requestFactory,
        ExportSourceResolver sourceResolver)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(sourceResolver);

        _logger = logger;
        _engine = engine;
        _requestFactory = requestFactory;
        _sourceResolver = sourceResolver;
    }

    /// <summary>
    /// Executes the export process for the RazorWire site to the configured output directory, validating options and writing progress to the console.
    /// </summary>
    /// <param name="console">The console used to write progress and completion messages.</param>
    /// <returns>A <see cref="ValueTask"/> that completes when the export operation finishes.</returns>
    [ExcludeFromCodeCoverage]
    public async ValueTask ExecuteAsync(IConsole console)
    {
        var cancellationToken = console.RegisterCancellationHandler();
        await ExecuteAsync(console, cancellationToken);
    }

    /// <summary>
    /// Executes the export process using an explicit cancellation token.
    /// </summary>
    /// <param name="console">The console used to write progress and completion messages.</param>
    /// <param name="cancellationToken">Cancellation token for startup and export operations.</param>
    /// <returns>A <see cref="ValueTask"/> that completes when the export operation finishes.</returns>
    public async ValueTask ExecuteAsync(IConsole console, CancellationToken cancellationToken)
    {
        var request = _requestFactory.Create(BaseUrl, ProjectPath, DllPath, Framework, AppArgs, NoBuild);
        await using var resolvedSource = await _sourceResolver.ResolveAsync(request, cancellationToken);

        _logger.LogInformation("Exporting to {OutputPath}...", OutputPath);

        var context = new ExportContext(OutputPath, SeedRoutesPath, resolvedSource.BaseUrl, Mode);
        try
        {
            await _engine.RunAsync(context, cancellationToken);
        }
        catch (ExportValidationException ex)
        {
            throw new CommandException(ex.Message);
        }

        _logger.LogInformation("Export complete!");
    }
}
