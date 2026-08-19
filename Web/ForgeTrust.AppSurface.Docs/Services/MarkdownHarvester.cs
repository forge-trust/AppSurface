using System.Globalization;
using System.Text;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.AppSurface.ReleaseContracts;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Core;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Harvester implementation that scans Markdown source files and converts them into documentation nodes.
/// </summary>
public class MarkdownHarvester : IDocHarvester, IDocHarvesterDiagnosticProvider
{
    private const string HarvesterType = nameof(MarkdownHarvester);
    private const string UnsafeTrustMigrationHrefMetadataDiagnosticCode = "unsafe-trust-migration-href";
    private const string ComposedUnreleasedDownloadUnavailableDiagnosticCode = "unreleased-entry-composed-download-unavailable";
    private static readonly string[] SidecarExtensions = [".yml", ".yaml"];
    private const int MinOutlineHeadingLevel = 2;
    private const int MaxOutlineHeadingLevel = 3;
    private readonly MarkdownPipeline _pipeline;
    private readonly ILogger<MarkdownHarvester> _logger;
    private readonly Func<string, CancellationToken, Task<string>> _readAllTextAsync;
    private readonly Func<string, CancellationToken, Task<byte[]>> _readAllBytesAsync;
    private readonly AppSurfaceDocsHarvestPathPolicy _pathPolicy;
    private readonly AppSurfaceDocsOptions _options;
    private IReadOnlyList<DocHarvestDiagnostic> _lastDiagnostics = [];

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> with the specified logger and configures the Markdown pipeline.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    public MarkdownHarvester(ILogger<MarkdownHarvester> logger)
        : this(logger, File.ReadAllTextAsync, AppSurfaceDocsHarvestPathPolicy.CreateDefault())
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> with observable diagnostics for the default code highlighter.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    /// <param name="loggerFactory">Logger factory used to create the TextMate grammar-load and highlighting fallback logger.</param>
    public MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        ILoggerFactory loggerFactory)
        : this(
            logger,
            File.ReadAllTextAsync,
            CreateDefaultHighlighter(loggerFactory),
            new AppSurfaceDocsOptions(),
            AppSurfaceDocsHarvestPathPolicy.CreateDefault())
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> with configurable harvest path policy and
    /// observable diagnostics for the default code highlighter.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    /// <param name="loggerFactory">Logger factory used to create the TextMate grammar-load and highlighting fallback logger.</param>
    /// <param name="pathPolicy">Shared harvest path policy used to decide which Markdown candidates publish.</param>
    internal MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        ILoggerFactory loggerFactory,
        AppSurfaceDocsHarvestPathPolicy pathPolicy)
        : this(
            logger,
            loggerFactory,
            new AppSurfaceDocsOptions(),
            pathPolicy)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> with configurable harvest options, path policy, and
    /// observable diagnostics for the default code highlighter.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    /// <param name="loggerFactory">Logger factory used to create the TextMate grammar-load and highlighting fallback logger.</param>
    /// <param name="options">AppSurface Docs options that provide Markdown resource limits.</param>
    /// <param name="pathPolicy">Shared harvest path policy used to decide which Markdown candidates publish.</param>
    internal MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        ILoggerFactory loggerFactory,
        AppSurfaceDocsOptions options,
        AppSurfaceDocsHarvestPathPolicy pathPolicy)
        : this(
            logger,
            File.ReadAllTextAsync,
            CreateDefaultHighlighter(loggerFactory),
            options,
            pathPolicy)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> for testing or internal use with a custom file reader.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    /// <param name="readAllTextAsync">Delegate used to asynchronously read file contents.</param>
    internal MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        Func<string, CancellationToken, Task<string>> readAllTextAsync)
        : this(
            logger,
            readAllTextAsync,
            AppSurfaceDocsCodeBlockMarkdownExtension.CreateDefaultHighlighter(
                NullLogger<TextMateSharpAppSurfaceDocsCodeHighlighter>.Instance),
            new AppSurfaceDocsOptions(),
            AppSurfaceDocsHarvestPathPolicy.CreateDefault())
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> for tests or internal composition with a custom
    /// file reader and configured AppSurface Docs options.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    /// <param name="readAllTextAsync">Delegate used to asynchronously read metadata sidecars and Markdown when source capture is disabled.</param>
    /// <param name="options">AppSurface Docs options that provide Markdown resource limits.</param>
    /// <remarks>
    /// This overload supplies the default TextMate-based code highlighter and default harvest path policy. Prefer it when
    /// a test needs custom options but does not need to observe code-highlighting behavior or path-policy decisions.
    /// Use the overload that accepts <see cref="IAppSurfaceDocsCodeHighlighter"/> and
    /// <see cref="AppSurfaceDocsHarvestPathPolicy"/> when either dependency must be controlled explicitly.
    /// </remarks>
    internal MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        Func<string, CancellationToken, Task<string>> readAllTextAsync,
        AppSurfaceDocsOptions options)
        : this(
            logger,
            readAllTextAsync,
            AppSurfaceDocsCodeBlockMarkdownExtension.CreateDefaultHighlighter(
                NullLogger<TextMateSharpAppSurfaceDocsCodeHighlighter>.Instance),
            options,
            AppSurfaceDocsHarvestPathPolicy.CreateDefault())
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> for tests that need to control both text and
    /// byte reads while preserving configured AppSurface Docs options.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    /// <param name="readAllTextAsync">Delegate used for metadata sidecars and Markdown when source capture is disabled.</param>
    /// <param name="options">AppSurface Docs options that provide Markdown resource limits.</param>
    /// <param name="readAllBytesAsync">Delegate used to read original Markdown bytes when source capture is enabled.</param>
    internal MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        Func<string, CancellationToken, Task<string>> readAllTextAsync,
        AppSurfaceDocsOptions options,
        Func<string, CancellationToken, Task<byte[]>> readAllBytesAsync)
        : this(
            logger,
            readAllTextAsync,
            AppSurfaceDocsCodeBlockMarkdownExtension.CreateDefaultHighlighter(
                NullLogger<TextMateSharpAppSurfaceDocsCodeHighlighter>.Instance),
            options,
            AppSurfaceDocsHarvestPathPolicy.CreateDefault(),
            readAllBytesAsync ?? throw new ArgumentNullException(nameof(readAllBytesAsync)))
    {
    }

    internal MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        Func<string, CancellationToken, Task<string>> readAllTextAsync,
        AppSurfaceDocsHarvestPathPolicy pathPolicy)
        : this(
            logger,
            readAllTextAsync,
            AppSurfaceDocsCodeBlockMarkdownExtension.CreateDefaultHighlighter(
                NullLogger<TextMateSharpAppSurfaceDocsCodeHighlighter>.Instance),
            new AppSurfaceDocsOptions(),
            pathPolicy)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> for testing or internal use with a custom file reader and code highlighter.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    /// <param name="readAllTextAsync">Delegate used to asynchronously read file contents.</param>
    /// <param name="codeHighlighter">Highlighter used when Markdown fenced code blocks are rendered to HTML.</param>
    internal MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        Func<string, CancellationToken, Task<string>> readAllTextAsync,
        IAppSurfaceDocsCodeHighlighter codeHighlighter)
        : this(
            logger,
            readAllTextAsync,
            codeHighlighter,
            new AppSurfaceDocsOptions(),
            AppSurfaceDocsHarvestPathPolicy.CreateDefault())
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> for tests or internal composition with custom file
    /// reading, code highlighting, and harvest path policy.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    /// <param name="readAllTextAsync">Delegate used to asynchronously read metadata sidecars and Markdown when source capture is disabled.</param>
    /// <param name="codeHighlighter">Highlighter used when Markdown fenced code blocks are rendered to HTML.</param>
    /// <param name="pathPolicy">Shared harvest path policy used to decide which Markdown candidates publish.</param>
    /// <remarks>
    /// This overload supplies default AppSurface Docs options, including default Markdown byte limits. Prefer it when a
    /// test needs explicit highlighting or path-policy behavior but should keep production defaults for resource guards.
    /// Use the most explicit overload when custom resource limits and custom collaborators are both required.
    /// </remarks>
    internal MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        Func<string, CancellationToken, Task<string>> readAllTextAsync,
        IAppSurfaceDocsCodeHighlighter codeHighlighter,
        AppSurfaceDocsHarvestPathPolicy pathPolicy)
        : this(logger, readAllTextAsync, codeHighlighter, new AppSurfaceDocsOptions(), pathPolicy)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> for tests or internal composition with all core
    /// collaborators supplied explicitly.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    /// <param name="readAllTextAsync">Delegate used to asynchronously read Markdown bodies and metadata sidecars.</param>
    /// <param name="codeHighlighter">Highlighter used when Markdown fenced code blocks are rendered to HTML.</param>
    /// <param name="options">AppSurface Docs options that provide Markdown resource limits.</param>
    /// <param name="pathPolicy">Shared harvest path policy used to decide which Markdown candidates publish.</param>
    /// <param name="readAllBytesAsync">Optional delegate used to read original Markdown bytes when source capture is enabled.</param>
    /// <remarks>
    /// This overload is the internal test seam for combining custom readers, highlighters, options, and path policy.
    /// Simpler overloads should be preferred when their defaults match the scenario because they keep tests focused on
    /// one dependency at a time.
    /// </remarks>
    internal MarkdownHarvester(
        ILogger<MarkdownHarvester> logger,
        Func<string, CancellationToken, Task<string>> readAllTextAsync,
        IAppSurfaceDocsCodeHighlighter codeHighlighter,
        AppSurfaceDocsOptions options,
        AppSurfaceDocsHarvestPathPolicy pathPolicy,
        Func<string, CancellationToken, Task<byte[]>>? readAllBytesAsync = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(readAllTextAsync);
        ArgumentNullException.ThrowIfNull(codeHighlighter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pathPolicy);

        _logger = logger;
        _readAllTextAsync = readAllTextAsync;
        _readAllBytesAsync = readAllBytesAsync ?? File.ReadAllBytesAsync;
        _pathPolicy = pathPolicy;
        _options = options;
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Use(new AppSurfaceDocsCodeBlockMarkdownExtension(codeHighlighter))
            .Use(new AppSurfaceDocsRichAuthoringMarkdownExtension())
            .Build();
    }

    private static IAppSurfaceDocsCodeHighlighter CreateDefaultHighlighter(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return AppSurfaceDocsCodeBlockMarkdownExtension.CreateDefaultHighlighter(
            loggerFactory.CreateLogger<TextMateSharpAppSurfaceDocsCodeHighlighter>());
    }

    /// <summary>
    /// Harvests Markdown files under the specified root directory and converts each into a DocNode containing a display title, relative path, generated HTML, metadata, and page outline.
    /// </summary>
    /// <param name="rootPath">The root directory to search recursively for `.md` files and an optional root `LICENSE` file.</param>
    /// <param name="cancellationToken">An optional token to observe for cancellation requests.</param>
    /// <returns>A collection of DocNode objects representing each processed Markdown source file, including the display title, path relative to <paramref name="rootPath"/>, generated HTML, metadata, and <see cref="DocNode.Outline"/> entries when outline headings are available.</returns>
    /// <remarks>
    /// Skips files in excluded directories (for example "node_modules", "bin", "obj", and "Tests") and hidden dot-prefixed directories unless explicitly allowlisted. Dot-prefixed files are included. File and directory reparse points are skipped so symlinks and junctions cannot point the built-in harvester outside <paramref name="rootPath"/>. The root <c>LICENSE</c> file is also included when present and not a reparse point so repository-relative license links can resolve in static exports. The special <c>releases/unreleased.md</c> path loads and composes validated append-only entries from <c>releases/unreleased.entries</c> before parsing and rendering. Because that rendered note is not byte-for-byte checked-in source, it is never retained for protected Markdown download. If a file's name is "README" (case-insensitive), its title is set to the parent directory name or "Home" for a repository root README. The Markdown body is parsed once with <c>Markdown.Parse(markdownBody, _pipeline)</c>; HTML is rendered from that AST and <see cref="DocNode.Outline"/> is populated from the same AST with <see cref="ExtractOutline"/>, then filtered through the resolved Markdown outline policy so callers can rely on display outline data being present when eligible headings are available. Files that fail to process are skipped and an error is logged.
    /// </remarks>
    public async Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        return (await HarvestWithSourceAsync(rootPath, _pathPolicy, cancellationToken)).Nodes;
    }

    /// <summary>
    /// Harvests Markdown files with the repository-scoped path policy captured for the current aggregation pass.
    /// </summary>
    /// <param name="context">The harvest context containing the repository root and active path policy snapshot.</param>
    /// <param name="cancellationToken">An optional token to observe for cancellation requests.</param>
    /// <returns>Markdown documentation nodes generated from eligible repository files.</returns>
    /// <remarks>
    /// This overload is used by the aggregator so VCS ignore exclusions are applied consistently across traversal and
    /// file inclusion checks. Custom harvesters continue to use the public <see cref="HarvestAsync(string, CancellationToken)"/>
    /// contract.
    /// </remarks>
    internal async Task<IReadOnlyList<DocNode>> HarvestAsync(DocHarvestContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (GetType() != typeof(MarkdownHarvester))
        {
            return await ((IDocHarvester)this).HarvestAsync(context.RepositoryRoot, cancellationToken);
        }

        return (await HarvestWithSourceAsync(context.RepositoryRoot, context.PathPolicy, cancellationToken)).Nodes;
    }

    /// <summary>
    /// Harvests the built-in Markdown surface and, when enabled, retains eligible valid-UTF-8 source bytes for the
    /// aggregator's private download sidecar.
    /// </summary>
    /// <remarks>
    /// This internal result is intentionally separate from <see cref="DocNode"/> so raw source never enters rendered
    /// HTML, search indexes, or the public harvester contract. The special <c>releases/unreleased.md</c> source is
    /// composed with validated append-only entries before parsing and rendering, so it cannot expose a source-faithful
    /// download even when protected source capture is enabled. The byte-reader seam is used only for Markdown source
    /// capture; metadata sidecars and disabled downloads continue through the configured text reader.
    /// </remarks>
    internal async Task<MarkdownHarvestResult> HarvestWithSourceAsync(
        DocHarvestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (GetType() != typeof(MarkdownHarvester))
        {
            return new MarkdownHarvestResult(
                await ((IDocHarvester)this).HarvestAsync(context.RepositoryRoot, cancellationToken),
                new Dictionary<string, byte[]>(StringComparer.Ordinal));
        }

        return await HarvestWithSourceAsync(context.RepositoryRoot, context.PathPolicy, cancellationToken);
    }

    private async Task<MarkdownHarvestResult> HarvestWithSourceAsync(
        string rootPath,
        IHarvestPathPolicy pathPolicy,
        CancellationToken cancellationToken)
    {
        var nodes = new List<DocNode>();
        var sourceByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var diagnostics = new List<DocHarvestDiagnostic>();
        long eligibleSourceBytes = 0;
        var sourceCaptureExceededBudget = false;
        try
        {
            var markdownOptions = _options.Harvest?.Markdown ?? new AppSurfaceDocsMarkdownHarvestOptions();
            var markdownDownloadOptions = _options.MarkdownDownload ?? new AppSurfaceDocsMarkdownDownloadOptions();
            foreach (var file in EnumerateMarkdownSourceFiles(rootPath, pathPolicy, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                    if (!pathPolicy.ShouldIncludeFilePath(relativePath, AppSurfaceDocsHarvestSourceKind.Markdown))
                    {
                        continue;
                    }

                    if (ShouldSkipOversizedMarkdownFile(file, relativePath, markdownOptions, diagnostics))
                    {
                        continue;
                    }

                    byte[]? sourceBytes = null;
                    string content;
                    if (markdownDownloadOptions.Enabled)
                    {
                        var bytes = await _readAllBytesAsync(file, cancellationToken);
                        if (TryDecodeValidUtf8(bytes, out var decodedContent))
                        {
                            sourceBytes = bytes;
                            content = decodedContent;
                        }
                        else
                        {
                            content = decodedContent;
                            diagnostics.Add(new DocHarvestDiagnostic(
                                DocHarvestDiagnosticCodes.MarkdownDownloadInvalidEncoding,
                                DocHarvestDiagnosticSeverity.Warning,
                                HarvesterType,
                                $"Markdown download was unavailable for '{relativePath}' because the source is not valid UTF-8.",
                                "The rendered Docs page uses replacement-decoded content and can remain available, but byte-faithful download accepts only valid UTF-8 source.",
                                "Save the source as UTF-8, then rebuild the Docs snapshot before retrying the protected download."));
                        }
                    }
                    else
                    {
                        content = await _readAllTextAsync(file, cancellationToken);
                    }

                    var isComposedUnreleasedNote = relativePath.Equals("releases/unreleased.md", StringComparison.Ordinal);
                    if (isComposedUnreleasedNote)
                    {
                        var entries = await UnreleasedEntryComposer.LoadAsync(
                            Path.Combine(rootPath, "releases", UnreleasedEntryComposer.EntriesDirectoryName),
                            cancellationToken);
                        content = UnreleasedEntryComposer.Compose(content, entries.Entries);
                    }

                    var (markdownBody, frontMatterResult) = MarkdownFrontMatterParser.ExtractWithDiagnostics(content);
                    ReportMetadataDiagnostics(relativePath, frontMatterResult.Diagnostics, diagnostics);
                    var sidecarMetadata = await ReadMetadataSidecarAsync(file, relativePath, cancellationToken, diagnostics);
                    var explicitMetadata = DocMetadata.Merge(frontMatterResult.Metadata, sidecarMetadata);
                    var title = Path.GetFileNameWithoutExtension(file);

                    if (title.Equals("README", StringComparison.OrdinalIgnoreCase))
                    {
                        var parentDir = Path.GetDirectoryName(relativePath);
                        title = string.IsNullOrEmpty(parentDir) ? "Home" : Path.GetFileName(parentDir);
                    }

                    var richTabs = AppSurfaceDocsRichAuthoringSyntax.RenderValidTabs(
                        markdownBody,
                        relativePath,
                        panelMarkdown => Markdown.ToHtml(
                            Markdown.Parse(
                                AppSurfaceDocsRichAuthoringSyntax.NormalizeDirectiveFences(panelMarkdown),
                                _pipeline),
                            _pipeline));
                    var renderedMarkdownBody = richTabs.ReplacePlaceholders(
                        AppSurfaceDocsRichAuthoringSyntax.NormalizeDirectiveFences(richTabs.Markdown));
                    var document = Markdown.Parse(renderedMarkdownBody, _pipeline);
                    foreach (var diagnostic in AppSurfaceDocsRichAuthoringSyntax.CollectDiagnostics(document, relativePath))
                    {
                        diagnostics.Add(diagnostic);
                    }
                    var resolvedTitle = string.IsNullOrWhiteSpace(explicitMetadata?.Title)
                        ? ResolveImplicitTitle(relativePath, document, title)
                        : explicitMetadata!.Title!.Trim();
                    var html = Markdown.ToHtml(document, _pipeline);
                    var metadata = DocMetadataFactory.CreateMarkdownMetadata(
                        relativePath,
                        resolvedTitle,
                        explicitMetadata,
                        ExtractSummary(markdownBody),
                        _logger);
                    var outline = DocOutlinePolicy.Apply(ExtractOutline(document), metadata);

                    nodes.Add(
                        new DocNode(
                            resolvedTitle,
                            relativePath,
                            html,
                            Metadata: metadata,
                            Outline: outline)
                        {
                            RichAuthoringTabsTokens = richTabs.Tokens
                        });

                    if (sourceBytes is not null)
                    {
                        var eligibility = frontMatterResult.DownloadEligibility;
                        if (eligibility == MarkdownDownloadEligibility.Eligible && isComposedUnreleasedNote)
                        {
                            sourceBytes = null;
                            diagnostics.Add(new DocHarvestDiagnostic(
                                ComposedUnreleasedDownloadUnavailableDiagnosticCode,
                                DocHarvestDiagnosticSeverity.Warning,
                                HarvesterType,
                                $"Markdown download was unavailable for '{relativePath}' because the rendered note is composed from append-only unreleased entries.",
                                "Protected Markdown download serves exact checked-in source bytes, while the living release note is assembled at harvest time.",
                                "Remove download_markdown: true from the living unreleased note; use the rendered page or archive the composed tagged release note instead."));
                        }
                        else if (eligibility == MarkdownDownloadEligibility.Eligible)
                        {
                            eligibleSourceBytes = checked(eligibleSourceBytes + sourceBytes.LongLength);
                            if (!sourceCaptureExceededBudget
                                && eligibleSourceBytes <= markdownDownloadOptions.MaxSnapshotBytes)
                            {
                                sourceByPath[relativePath] = sourceBytes;
                            }
                            else
                            {
                                // The download sidecar is deliberately all-or-nothing. Clear retained bytes as soon as
                                // the cap is crossed, then keep rendering the remaining documents without retaining raw
                                // source so a large repository cannot build an unbounded transient source map.
                                sourceByPath.Clear();
                                sourceCaptureExceededBudget = true;
                            }
                        }
                        else if (eligibility == MarkdownDownloadEligibility.Invalid)
                        {
                            diagnostics.Add(new DocHarvestDiagnostic(
                                DocHarvestDiagnosticCodes.MarkdownDownloadInvalidEligibility,
                                DocHarvestDiagnosticSeverity.Warning,
                                HarvesterType,
                                $"Markdown download was unavailable for '{relativePath}' because download_markdown must be the exact top-level inline declaration 'download_markdown: true'.",
                                "Raw source download is a security-sensitive opt-in and the declaration was quoted, nested, duplicated, malformed, or otherwise not the strict v1 shape.",
                                "Use one plain top-level download_markdown: true value in inline front matter; paired sidecar metadata never enables source download."));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process markdown file: {File}", file);
                }
            }

            return new MarkdownHarvestResult(
                nodes,
                sourceByPath,
                eligibleSourceBytes,
                sourceCaptureExceededBudget);
        }
        finally
        {
            _lastDiagnostics = diagnostics.ToArray();
        }
    }

    /// <summary>
    /// Attempts a strict UTF-8 decode and falls back to replacement decoding.
    /// </summary>
    /// <param name="bytes">The raw Markdown source bytes.</param>
    /// <param name="content">
    /// The decoded text with a leading byte-order mark removed. This is populated on both outcomes; when the return
    /// value is <see langword="false"/>, it contains the replacement-decoded content.
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="bytes"/> is valid UTF-8; otherwise <see langword="false"/>.</returns>
    private static bool TryDecodeValidUtf8(byte[] bytes, out string content)
    {
        try
        {
            content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content[1..];
            }

            return true;
        }
        catch (DecoderFallbackException)
        {
            content = DecodeUtf8WithReplacement(bytes);
            return false;
        }
    }

    private static string DecodeUtf8WithReplacement(byte[] bytes)
    {
        var content = Encoding.UTF8.GetString(bytes);
        return content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
    }

    private IEnumerable<string> EnumerateMarkdownSourceFiles(
        string rootPath,
        IHarvestPathPolicy pathPolicy,
        CancellationToken cancellationToken)
    {
        foreach (var file in pathPolicy.EnumerateCandidateFiles(
                     rootPath,
                     AppSurfaceDocsHarvestSourceKind.Markdown,
                     "*.md",
                     cancellationToken))
        {
            yield return file;
        }

        var rootLicensePath = Path.Combine(rootPath, "LICENSE");
        if (AppSurfaceDocsHarvestFileSystem.IsNonReparsePointFile(rootLicensePath))
        {
            yield return rootLicensePath;
        }
    }

    private static string ResolveImplicitTitle(string relativePath, MarkdownDocument document, string fallbackTitle)
    {
        return IsRootLicensePath(relativePath)
            ? fallbackTitle
            : ExtractLeadingTitle(document) ?? fallbackTitle;
    }

    private static bool IsRootLicensePath(string relativePath)
    {
        return relativePath.Equals("LICENSE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads an optional paired sidecar metadata file for a Markdown source document.
    /// </summary>
    /// <param name="markdownFilePath">The absolute Markdown file path.</param>
    /// <param name="relativeMarkdownPath">The Markdown file path relative to the harvest root.</param>
    /// <param name="cancellationToken">A token that can cancel sidecar discovery or file reads.</param>
    /// <param name="harvestDiagnostics">Optional harvest diagnostic collection that receives sidecar metadata warnings.</param>
    /// <returns>The parsed sidecar metadata, or <c>null</c> when no valid sidecar applies.</returns>
    /// <remarks>
    /// AppSurface Docs supports paired metadata files named <c>{file}.yml</c> and <c>{file}.yaml</c> such as
    /// <c>README.md.yml</c>. Reparse-point sidecars are ignored so metadata cannot be imported through a symlink or
    /// junction outside the harvest root. When both non-reparse extensions exist for the same Markdown file, AppSurface
    /// Docs logs a warning and ignores both sidecars until the ambiguity is removed. Inline front matter remains the
    /// primary metadata source and overrides any overlapping sidecar fields.
    /// </remarks>
    internal async Task<DocMetadata?> ReadMetadataSidecarAsync(
        string markdownFilePath,
        string relativeMarkdownPath,
        CancellationToken cancellationToken,
        ICollection<DocHarvestDiagnostic>? harvestDiagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeMarkdownPath);

        var existingSidecars = SidecarExtensions
            .Select(extension => markdownFilePath + extension)
            .Where(AppSurfaceDocsHarvestFileSystem.IsNonReparsePointFile)
            .ToArray();

        if (existingSidecars.Length == 0)
        {
            return null;
        }

        if (existingSidecars.Length > 1)
        {
            _logger.LogWarning(
                "Ignoring metadata sidecars for {MarkdownPath} because both {FirstSidecar} and {SecondSidecar} exist. Keep only one sidecar extension per Markdown file.",
                relativeMarkdownPath,
                Path.GetFileName(existingSidecars[0]),
                Path.GetFileName(existingSidecars[1]));
            return null;
        }

        var sidecarPath = existingSidecars[0];
        var relativeSidecarPath = $"{relativeMarkdownPath}{Path.GetExtension(sidecarPath)}";

        try
        {
            var markdownOptions = _options.Harvest?.Markdown ?? new AppSurfaceDocsMarkdownHarvestOptions();
            if (ShouldIgnoreOversizedMetadataSidecar(
                    sidecarPath,
                    relativeMarkdownPath,
                    relativeSidecarPath,
                    markdownOptions,
                    harvestDiagnostics))
            {
                return null;
            }

            var yaml = await _readAllTextAsync(sidecarPath, cancellationToken);
            var result = MarkdownFrontMatterParser.ParseMetadataYamlWithDiagnostics(yaml);
            ReportMetadataDiagnostics(
                relativeSidecarPath,
                result.Diagnostics,
                harvestDiagnostics);
            return result.Metadata;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (YamlException ex)
        {
            _logger.LogWarning(
                ex,
                "Ignoring metadata sidecar {SidecarPath} for {MarkdownPath} because the YAML could not be parsed.",
                Path.GetFileName(sidecarPath),
                relativeMarkdownPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Ignoring metadata sidecar {SidecarPath} for {MarkdownPath} because it could not be read.",
                Path.GetFileName(sidecarPath),
                relativeMarkdownPath);
            return null;
        }
    }

    private bool ShouldSkipOversizedMarkdownFile(
        string filePath,
        string relativePath,
        AppSurfaceDocsMarkdownHarvestOptions markdownOptions,
        ICollection<DocHarvestDiagnostic> harvestDiagnostics)
    {
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length <= markdownOptions.MaxFileSizeBytes)
        {
            return false;
        }

        var actualBytes = fileInfo.Length.ToString(CultureInfo.InvariantCulture);
        var configuredLimit = markdownOptions.MaxFileSizeBytes.ToString(CultureInfo.InvariantCulture);
        harvestDiagnostics.Add(new DocHarvestDiagnostic(
            DocHarvestDiagnosticCodes.MarkdownFileTooLarge,
            DocHarvestDiagnosticSeverity.Warning,
            HarvesterType,
            $"Skipped Markdown file '{relativePath}' because it is {actualBytes} bytes and exceeds AppSurfaceDocs:Harvest:Markdown:MaxFileSizeBytes ({configuredLimit} bytes).",
            "The file matched Markdown harvest policy but was not read, front matter was not parsed, and Markdig did not receive the Markdown body.",
            $"Exclude generated or accidental large docs with AppSurfaceDocs:Harvest:Markdown:ExcludeGlobs or AppSurfaceDocs:Harvest:Paths:ExcludeGlobs, or raise AppSurfaceDocs:Harvest:Markdown:MaxFileSizeBytes above {actualBytes} bytes only for intentional authored Markdown pages."));
        _logger.LogWarning(
            "Skipped Markdown file {MarkdownPath} because it is {ActualBytes} bytes and AppSurfaceDocs:Harvest:Markdown:MaxFileSizeBytes is {ConfiguredLimit} bytes.",
            relativePath,
            fileInfo.Length,
            markdownOptions.MaxFileSizeBytes);
        return true;
    }

    private bool ShouldIgnoreOversizedMetadataSidecar(
        string sidecarPath,
        string relativeMarkdownPath,
        string relativeSidecarPath,
        AppSurfaceDocsMarkdownHarvestOptions markdownOptions,
        ICollection<DocHarvestDiagnostic>? harvestDiagnostics)
    {
        var fileInfo = new FileInfo(sidecarPath);
        if (fileInfo.Length <= markdownOptions.MaxMetadataFileSizeBytes)
        {
            return false;
        }

        var actualBytes = fileInfo.Length.ToString(CultureInfo.InvariantCulture);
        var configuredLimit = markdownOptions.MaxMetadataFileSizeBytes.ToString(CultureInfo.InvariantCulture);
        harvestDiagnostics?.Add(new DocHarvestDiagnostic(
            DocHarvestDiagnosticCodes.MarkdownMetadataFileTooLarge,
            DocHarvestDiagnosticSeverity.Warning,
            HarvesterType,
            $"Ignored Markdown metadata sidecar '{relativeSidecarPath}' for '{relativeMarkdownPath}' because it is {actualBytes} bytes and exceeds AppSurfaceDocs:Harvest:Markdown:MaxMetadataFileSizeBytes ({configuredLimit} bytes).",
            "The sidecar matched the paired metadata naming contract but was not read or parsed as YAML. The Markdown body can still publish when it is within AppSurfaceDocs:Harvest:Markdown:MaxFileSizeBytes.",
            $"Move large prose into the Markdown body, exclude generated sidecars, or raise AppSurfaceDocs:Harvest:Markdown:MaxMetadataFileSizeBytes above {actualBytes} bytes only for intentional authored metadata."));
        _logger.LogWarning(
            "Ignored Markdown metadata sidecar {SidecarPath} for {MarkdownPath} because it is {ActualBytes} bytes and AppSurfaceDocs:Harvest:Markdown:MaxMetadataFileSizeBytes is {ConfiguredLimit} bytes.",
            relativeSidecarPath,
            relativeMarkdownPath,
            fileInfo.Length,
            markdownOptions.MaxMetadataFileSizeBytes);
        return true;
    }

    IReadOnlyList<DocHarvestDiagnostic> IDocHarvesterDiagnosticProvider.GetHarvestDiagnostics()
    {
        return GetType() == typeof(MarkdownHarvester) ? _lastDiagnostics : [];
    }

    private void ReportMetadataDiagnostics(
        string sourcePath,
        IReadOnlyList<AppSurfaceDocsMetadataDiagnostic> diagnostics,
        ICollection<DocHarvestDiagnostic>? harvestDiagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (ShouldExposeMetadataDiagnosticToHarvestHealth(diagnostic))
            {
                harvestDiagnostics?.Add(CreateMetadataHarvestDiagnostic(sourcePath, diagnostic));
            }

            _logger.LogWarning(
                "AppSurface Docs metadata warning {Code} in {SourcePath} at {FieldPath}: {Problem} Cause: {Cause} Fix: {Fix}",
                diagnostic.Code,
                sourcePath,
                diagnostic.FieldPath,
                diagnostic.Problem,
                diagnostic.Cause,
                diagnostic.Fix);
        }
    }

    private static DocHarvestDiagnostic CreateMetadataHarvestDiagnostic(
        string sourcePath,
        AppSurfaceDocsMetadataDiagnostic diagnostic)
    {
        return new DocHarvestDiagnostic(
            DocHarvestDiagnosticCodes.MetadataUnsafeTrustMigrationHref,
            DocHarvestDiagnosticSeverity.Warning,
            HarvesterType,
            $"Metadata warning in {sourcePath} at {diagnostic.FieldPath}: {diagnostic.Problem}",
            diagnostic.Cause,
            diagnostic.Fix);
    }

    private static bool ShouldExposeMetadataDiagnosticToHarvestHealth(AppSurfaceDocsMetadataDiagnostic diagnostic)
    {
        return diagnostic.Code.Equals(UnsafeTrustMigrationHrefMetadataDiagnosticCode, StringComparison.Ordinal);
    }

    internal static string? ExtractSummary(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var summaryLines = new List<string>();
        var inCodeFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence || string.IsNullOrWhiteSpace(trimmed))
            {
                if (summaryLines.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal)
                || trimmed.StartsWith("- ", StringComparison.Ordinal)
                || trimmed.StartsWith("* ", StringComparison.Ordinal)
                || StartsWithNumberedListMarker(trimmed)
                || trimmed.StartsWith("> ", StringComparison.Ordinal)
                || trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                if (summaryLines.Count > 0)
                {
                    break;
                }

                continue;
            }

            summaryLines.Add(trimmed);
        }

        return summaryLines.Count == 0 ? null : string.Join(" ", summaryLines);
    }

    private static bool StartsWithNumberedListMarker(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !char.IsDigit(value[0]))
        {
            return false;
        }

        var index = 0;
        while (index < value.Length && char.IsDigit(value[index]))
        {
            index++;
        }

        return index + 1 < value.Length
               && value[index] == '.'
               && value[index + 1] == ' ';
    }

    /// <summary>
    /// Extracts page-local outline entries from Markdown heading blocks.
    /// </summary>
    /// <param name="document">The parsed Markdown document whose heading blocks should be inspected.</param>
    /// <returns>
    /// A source-ordered list of <see cref="DocOutlineItem"/> values. Each item contains the rendered fragment <see cref="DocOutlineItem.Id"/>,
    /// normalized reader-facing <see cref="DocOutlineItem.Title"/>, and original heading <see cref="DocOutlineItem.Level"/>.
    /// </returns>
    /// <remarks>
    /// Only <see cref="HeadingBlock"/> descendants with levels between <c>MinOutlineHeadingLevel</c> and <c>MaxOutlineHeadingLevel</c> are included,
    /// which means the built-in Markdown harvester emits H2-H3 headings by default. Fragment IDs come from
    /// <c>HtmlAttributesExtensions.GetAttributes(heading).Id</c> and titles are produced by
    /// <c>NormalizeHeadingText(ExtractInlineText(heading.Inline))</c>. Headings without a non-empty fragment ID or normalized title are silently
    /// omitted; consumers and tests should account for those drops and for whitespace normalization when comparing outline titles.
    /// </remarks>
    internal static IReadOnlyList<DocOutlineItem> ExtractOutline(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document
            .Descendants<HeadingBlock>()
            .Where(heading => heading.Level >= MinOutlineHeadingLevel && heading.Level <= MaxOutlineHeadingLevel)
                .Select(
                    heading =>
                    {
                        var id = HtmlAttributesExtensions.GetAttributes(heading).Id;
                        var title = NormalizeHeadingText(ExtractInlineText(heading.Inline));

                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                        {
                            return null;
                        }

                        return new DocOutlineItem
                        {
                            Id = id,
                            Title = title,
                            Level = heading.Level
                        };
                    })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
    }

    /// <summary>
    /// Extracts the document title from a leading Markdown H1 when one exists.
    /// </summary>
    /// <param name="document">The parsed Markdown document whose first block may be a page-title H1.</param>
    /// <returns>
    /// The normalized heading text from the leading H1, or <c>null</c> when the document starts with another block or
    /// the H1 has no readable text.
    /// </returns>
    /// <remarks>
    /// This mirrors details-page H1 suppression: only the leading H1 can become package-owned page chrome. Later H1
    /// elements remain body structure and do not replace filename or metadata title fallback behavior.
    /// </remarks>
    internal static string? ExtractLeadingTitle(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var firstReaderFacingBlock = document.FirstOrDefault(block => !IsLeadingHtmlCommentBlock(block));
        if (firstReaderFacingBlock is not HeadingBlock { Level: 1 } heading)
        {
            return null;
        }

        var title = NormalizeHeadingText(ExtractInlineText(heading.Inline));
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static bool IsLeadingHtmlCommentBlock(Block block)
    {
        if (block is not HtmlBlock htmlBlock)
        {
            return false;
        }

        var html = htmlBlock.Lines.ToString();
        return html.TrimStart().StartsWith("<!--", StringComparison.Ordinal)
               && html.TrimEnd().EndsWith("-->", StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts plain reader-facing text from a Markdig inline container for outline display.
    /// </summary>
    /// <param name="inline">The inline container to flatten.</param>
    /// <returns>The extracted text, or an empty string when no inline content exists.</returns>
    internal static string ExtractInlineText(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        AppendInlineText(builder, inline.FirstChild);
        return builder.ToString();
    }

    private static void AppendInlineText(System.Text.StringBuilder builder, Inline? inline)
    {
        while (inline is not null)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline container:
                    AppendInlineText(builder, container.FirstChild);
                    break;
            }

            inline = inline.NextSibling;
        }
    }

    /// <summary>
    /// Normalizes heading text by collapsing whitespace without introducing leading spaces.
    /// </summary>
    /// <param name="value">The raw heading text.</param>
    /// <returns>The normalized heading text.</returns>
    internal static string NormalizeHeadingText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Private built-in Markdown harvest output used to retain source bytes beside the rendered Docs graph.
/// </summary>
/// <remarks>
/// This type intentionally stays internal. Raw Markdown must not become part of <see cref="DocNode"/>, the search
/// index, or the public custom-harvester contract.
/// </remarks>
internal sealed record MarkdownHarvestResult(
    IReadOnlyList<DocNode> Nodes,
    IReadOnlyDictionary<string, byte[]> SourceByPath,
    long EligibleSourceBytes = 0,
    bool SourceCaptureExceededBudget = false);
