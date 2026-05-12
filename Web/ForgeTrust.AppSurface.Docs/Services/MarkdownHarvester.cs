using ForgeTrust.AppSurface.Docs.Models;
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
public class MarkdownHarvester : IDocHarvester
{
    private static readonly string[] SidecarExtensions = [".yml", ".yaml"];
    private const int MinOutlineHeadingLevel = 2;
    private const int MaxOutlineHeadingLevel = 3;
    private readonly MarkdownPipeline _pipeline;
    private readonly ILogger<MarkdownHarvester> _logger;
    private readonly Func<string, CancellationToken, Task<string>> _readAllTextAsync;

    /// <summary>
    /// Initializes a new instance of <see cref="MarkdownHarvester"/> with the specified logger and configures the Markdown pipeline.
    /// </summary>
    /// <param name="logger">Logger used for recording harvesting events and errors.</param>
    public MarkdownHarvester(ILogger<MarkdownHarvester> logger)
        : this(logger, File.ReadAllTextAsync)
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
            CreateDefaultHighlighter(loggerFactory))
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
            RazorDocsCodeBlockMarkdownExtension.CreateDefaultHighlighter(
                NullLogger<TextMateSharpRazorDocsCodeHighlighter>.Instance))
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
        IRazorDocsCodeHighlighter codeHighlighter)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(readAllTextAsync);
        ArgumentNullException.ThrowIfNull(codeHighlighter);

        _logger = logger;
        _readAllTextAsync = readAllTextAsync;
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Use(new RazorDocsCodeBlockMarkdownExtension(codeHighlighter))
            .Build();
    }

    private static IRazorDocsCodeHighlighter CreateDefaultHighlighter(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        return RazorDocsCodeBlockMarkdownExtension.CreateDefaultHighlighter(
            loggerFactory.CreateLogger<TextMateSharpRazorDocsCodeHighlighter>());
    }

    /// <summary>
    /// Harvests Markdown files under the specified root directory and converts each into a DocNode containing a display title, relative path, generated HTML, metadata, and page outline.
    /// </summary>
    /// <param name="rootPath">The root directory to search recursively for `.md` files and an optional root `LICENSE` file.</param>
    /// <param name="cancellationToken">An optional token to observe for cancellation requests.</param>
    /// <returns>A collection of DocNode objects representing each processed Markdown source file, including the display title, path relative to <paramref name="rootPath"/>, generated HTML, metadata, and <see cref="DocNode.Outline"/> entries when outline headings are available.</returns>
    /// <remarks>
    /// Skips files in excluded directories (for example "node_modules", "bin", "obj", and "Tests") and hidden dot-prefixed directories unless explicitly allowlisted. Dot-prefixed files are included. The root <c>LICENSE</c> file is also included when present so repository-relative license links can resolve in static exports. If a file's name is "README" (case-insensitive), its title is set to the parent directory name or "Home" for a repository root README. The Markdown body is parsed once with <c>Markdown.Parse(markdownBody, _pipeline)</c>; HTML is rendered from that AST and <see cref="DocNode.Outline"/> is populated from the same AST with <see cref="ExtractOutline"/> so callers can rely on outline data being present when eligible headings are available. Files that fail to process are skipped and an error is logged.
    /// </remarks>
    public async Task<IReadOnlyList<DocNode>> HarvestAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var nodes = new List<DocNode>();
        foreach (var file in EnumerateMarkdownSourceFiles(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                if (HarvestPathExclusions.ShouldExcludeFilePath(relativePath))
                {
                    continue;
                }

                var content = await _readAllTextAsync(file, cancellationToken);
                var (markdownBody, frontMatterResult) = MarkdownFrontMatterParser.ExtractWithDiagnostics(content);
                LogMetadataDiagnostics(relativePath, frontMatterResult.Diagnostics);
                var sidecarMetadata = await ReadMetadataSidecarAsync(file, relativePath, cancellationToken);
                var explicitMetadata = DocMetadata.Merge(frontMatterResult.Metadata, sidecarMetadata);
                var title = Path.GetFileNameWithoutExtension(file);

                if (title.Equals("README", StringComparison.OrdinalIgnoreCase))
                {
                    var parentDir = Path.GetDirectoryName(relativePath);
                    title = string.IsNullOrEmpty(parentDir) ? "Home" : Path.GetFileName(parentDir);
                }

                var resolvedTitle = explicitMetadata?.Title ?? title;
                var document = Markdown.Parse(markdownBody, _pipeline);
                var html = Markdown.ToHtml(document, _pipeline);
                var metadata = DocMetadataFactory.CreateMarkdownMetadata(
                    relativePath,
                    resolvedTitle,
                    explicitMetadata,
                    ExtractSummary(markdownBody),
                    _logger);
                var outline = ExtractOutline(document);

                nodes.Add(new DocNode(resolvedTitle, relativePath, html, Metadata: metadata, Outline: outline));
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

        return nodes;
    }

    private static IEnumerable<string> EnumerateMarkdownSourceFiles(string rootPath)
    {
        foreach (var file in Directory.EnumerateFiles(rootPath, "*.md", SearchOption.AllDirectories))
        {
            yield return file;
        }

        var rootLicensePath = Path.Combine(rootPath, "LICENSE");
        if (File.Exists(rootLicensePath))
        {
            yield return rootLicensePath;
        }
    }

    /// <summary>
    /// Reads an optional paired sidecar metadata file for a Markdown source document.
    /// </summary>
    /// <param name="markdownFilePath">The absolute Markdown file path.</param>
    /// <param name="relativeMarkdownPath">The Markdown file path relative to the harvest root.</param>
    /// <param name="cancellationToken">A token that can cancel sidecar discovery or file reads.</param>
    /// <returns>The parsed sidecar metadata, or <c>null</c> when no valid sidecar applies.</returns>
    /// <remarks>
    /// RazorDocs supports paired metadata files named <c>{file}.yml</c> and <c>{file}.yaml</c> such as
    /// <c>README.md.yml</c>. When both extensions exist for the same Markdown file, RazorDocs logs a warning and ignores
    /// both sidecars until the ambiguity is removed. Inline front matter remains the primary metadata source and overrides
    /// any overlapping sidecar fields.
    /// </remarks>
    internal async Task<DocMetadata?> ReadMetadataSidecarAsync(
        string markdownFilePath,
        string relativeMarkdownPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeMarkdownPath);

        var existingSidecars = SidecarExtensions
            .Select(extension => markdownFilePath + extension)
            .Where(File.Exists)
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

        try
        {
            var yaml = await _readAllTextAsync(sidecarPath, cancellationToken);
            var result = MarkdownFrontMatterParser.ParseMetadataYamlWithDiagnostics(yaml);
            LogMetadataDiagnostics($"{relativeMarkdownPath}{Path.GetExtension(sidecarPath)}", result.Diagnostics);
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

    private void LogMetadataDiagnostics(
        string sourcePath,
        IReadOnlyList<RazorDocsMetadataDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            _logger.LogWarning(
                "RazorDocs metadata warning {Code} in {SourcePath} at {FieldPath}: {Problem} Cause: {Cause} Fix: {Fix}",
                diagnostic.Code,
                sourcePath,
                diagnostic.FieldPath,
                diagnostic.Problem,
                diagnostic.Cause,
                diagnostic.Fix);
        }
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
