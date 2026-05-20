using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;
using ForgeTrust.AppSurface.Docs.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Harvests intentionally public JavaScript API doclets from configured plain <c>.js</c> source files.
/// </summary>
/// <remarks>
/// The harvester is disabled by default through <see cref="AppSurfaceDocsJavaScriptHarvestOptions.Enabled"/> and scans only
/// repository-relative paths matched by <see cref="AppSurfaceDocsJavaScriptHarvestOptions.IncludeGlobs"/>. V1 is deliberately
/// strict: it reads JSDoc-shaped block comments, requires <c>@public</c> by default, treats <c>@internal</c>,
/// <c>@private</c>, and <c>@ignore</c> as hard exclusions, and renders only functions, constants, globals, standalone
/// events, and standalone typedefs. Unsupported public shapes become harvest diagnostics instead of partial docs.
/// </remarks>
public sealed class JavaScriptDocHarvester : IDocHarvester, IDocHarvesterDiagnosticProvider, IDocHarvesterActivation
{
    private const string HarvesterType = nameof(JavaScriptDocHarvester);
    private static readonly Regex UnsafeSlugCharacterRegex = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly string[] AlwaysPrunedDirectoryNames = [".git"];

    private readonly AppSurfaceDocsOptions _options;
    private readonly ILogger<JavaScriptDocHarvester> _logger;
    private readonly AppSurfaceDocsHarvestPathPolicy _pathPolicy;
    private IReadOnlyList<DocHarvestDiagnostic> _lastDiagnostics = [];

    /// <summary>
    /// Initializes a new instance of <see cref="JavaScriptDocHarvester"/>.
    /// </summary>
    /// <param name="options">Normalized AppSurface Docs options that contain JavaScript harvest settings.</param>
    /// <param name="logger">Logger used for non-fatal JavaScript harvest diagnostics.</param>
    public JavaScriptDocHarvester(AppSurfaceDocsOptions options, ILogger<JavaScriptDocHarvester> logger)
        : this(options, logger, new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance))
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="JavaScriptDocHarvester"/> with a shared harvest path policy.
    /// </summary>
    /// <param name="options">Normalized AppSurface Docs options that contain JavaScript harvest settings.</param>
    /// <param name="logger">Logger used for non-fatal JavaScript harvest diagnostics.</param>
    /// <param name="pathPolicy">Shared harvest path policy used to decide which JavaScript candidates publish.</param>
    internal JavaScriptDocHarvester(
        AppSurfaceDocsOptions options,
        ILogger<JavaScriptDocHarvester> logger,
        AppSurfaceDocsHarvestPathPolicy pathPolicy)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(pathPolicy);

        _options = options;
        _logger = logger;
        _pathPolicy = pathPolicy;
    }

    /// <summary>
    /// Scans configured JavaScript files under the repository root and returns generated AppSurface Docs API nodes.
    /// </summary>
    /// <param name="rootPath">The repository root used to resolve include and exclude globs.</param>
    /// <param name="cancellationToken">An optional token to observe while reading and parsing files.</param>
    /// <returns>Generated group pages plus fragment-addressable stub nodes for harvested JavaScript API items.</returns>
    public async Task<IReadOnlyList<DocNode>> HarvestAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var diagnostics = new List<DocHarvestDiagnostic>();
        try
        {
            var javaScriptOptions = _options.Harvest?.JavaScript ?? new AppSurfaceDocsJavaScriptHarvestOptions();
            if (!javaScriptOptions.Enabled)
            {
                return [];
            }

            if (!HasUsableIncludeGlobs(javaScriptOptions.IncludeGlobs))
            {
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.JavaScriptMissingInclude,
                    DocHarvestDiagnosticSeverity.Warning,
                    "JavaScript harvesting is enabled without include globs.",
                    "AppSurface Docs does not perform implicit repository-wide JavaScript crawling, so no JavaScript files were scanned.",
                    "Configure at least one AppSurfaceDocs:Harvest:JavaScript:IncludeGlobs glob for the public runtime files that should be documented."));
                return [];
            }

            var harvestedItems = new List<JavaScriptApiItem>();
            foreach (var filePath in EnumerateJavaScriptFiles(rootPath, javaScriptOptions, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath));
                if (!_pathPolicy.ShouldIncludeFilePath(relativePath, AppSurfaceDocsHarvestSourceKind.JavaScript))
                {
                    continue;
                }

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length > javaScriptOptions.MaxFileSizeBytes)
                    {
                        diagnostics.Add(CreateDiagnostic(
                            DocHarvestDiagnosticCodes.JavaScriptFileTooLarge,
                            DocHarvestDiagnosticSeverity.Warning,
                            $"Skipped JavaScript file '{relativePath}' because it is larger than the configured limit.",
                            $"The file is {fileInfo.Length.ToString(CultureInfo.InvariantCulture)} bytes and AppSurfaceDocs:Harvest:JavaScript:MaxFileSizeBytes is {javaScriptOptions.MaxFileSizeBytes.ToString(CultureInfo.InvariantCulture)}.",
                            "Raise the JavaScript max-file-size limit only for authored source files that should be parsed, or keep generated bundles excluded."));
                        continue;
                    }

                    var source = await File.ReadAllTextAsync(filePath, cancellationToken);
                    harvestedItems.AddRange(ParseFile(source, relativePath, javaScriptOptions, diagnostics));
                }
                catch (ParseErrorException ex)
                {
                    diagnostics.Add(CreateDiagnostic(
                        DocHarvestDiagnosticCodes.JavaScriptParseFailed,
                        DocHarvestDiagnosticSeverity.Warning,
                        $"Skipped JavaScript file '{relativePath}' because the parser rejected it.",
                        $"Acornima reported {ex.Message} at line {ex.LineNumber.ToString(CultureInfo.InvariantCulture)}, column {ex.Column.ToString(CultureInfo.InvariantCulture)}.",
                        "Fix the JavaScript syntax, remove the file from the JavaScript include set, or exclude generated syntax that the v1 harvester should not parse."));
                }
                catch (Exception ex) when (IsFileReadException(ex))
                {
                    diagnostics.Add(CreateDiagnostic(
                        DocHarvestDiagnosticCodes.JavaScriptParseFailed,
                        DocHarvestDiagnosticSeverity.Warning,
                        $"Skipped JavaScript file '{relativePath}' because it could not be read.",
                        ex.Message,
                        "Fix file permissions or locks, or exclude this file from JavaScript harvesting."));
                }
            }

            AssignStableAnchors(harvestedItems, diagnostics);
            return BuildDocNodes(harvestedItems);
        }
        finally
        {
            _lastDiagnostics = diagnostics.ToArray();
            foreach (var diagnostic in diagnostics)
            {
                _logger.Log(
                    diagnostic.Severity >= DocHarvestDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning,
                    "AppSurface Docs JavaScript harvest diagnostic {DiagnosticCode}: {Problem}",
                    diagnostic.Code,
                    diagnostic.Problem);
            }
        }
    }

    bool IDocHarvesterActivation.IsEnabled => _options.Harvest?.JavaScript?.Enabled == true;

    IReadOnlyList<DocHarvestDiagnostic> IDocHarvesterDiagnosticProvider.GetHarvestDiagnostics()
    {
        return _lastDiagnostics;
    }

    private static IReadOnlyList<JavaScriptApiItem> ParseFile(
        string source,
        string relativePath,
        AppSurfaceDocsJavaScriptHarvestOptions options,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var comments = new List<Comment>();
        var parser = new Parser(new ParserOptions
        {
            EcmaVersion = EcmaVersion.Latest,
            OnComment = (in Comment comment) => comments.Add(comment)
        });

        var script = parser.ParseScript(source);
        var attachedCommentStarts = new HashSet<int>();
        var items = new List<JavaScriptApiItem>();

        foreach (var statement in EnumerateNodes(script))
        {
            switch (statement)
            {
                case FunctionDeclaration functionDeclaration:
                    AddAttachedItem(
                        comments,
                        attachedCommentStarts,
                        items,
                        source,
                        relativePath,
                        functionDeclaration,
                        functionDeclaration,
                        functionDeclaration.Id?.Name,
                        JavaScriptApiKind.Function,
                        options,
                        diagnostics);
                    break;

                case VariableDeclaration variableDeclaration:
                    if (variableDeclaration.Declarations.Count != 1)
                    {
                        AddUnsupportedAttachedItem(
                            comments,
                            attachedCommentStarts,
                            source,
                            relativePath,
                            variableDeclaration,
                            "Multiple JavaScript declarators cannot share one public doclet in v1.",
                            diagnostics);
                        break;
                    }

                    var declarator = variableDeclaration.Declarations[0];
                    var name = declarator.Id is Identifier identifier ? identifier.Name : null;
                    var kind = declarator.Init is ArrowFunctionExpression or FunctionExpression
                        ? JavaScriptApiKind.Function
                        : JavaScriptApiKind.Constant;
                    AddAttachedItem(
                        comments,
                        attachedCommentStarts,
                        items,
                        source,
                        relativePath,
                        variableDeclaration,
                        variableDeclaration,
                        name,
                        kind,
                        options,
                        diagnostics);

                    break;

                case ExpressionStatement expressionStatement
                    when expressionStatement.Expression is AssignmentExpression assignment
                         && TryGetMemberPath(assignment.Left) is { } memberPath:
                    if (memberPath.StartsWith("window.", StringComparison.Ordinal))
                    {
                        AddAttachedItem(
                            comments,
                            attachedCommentStarts,
                            items,
                            source,
                            relativePath,
                            expressionStatement,
                            expressionStatement,
                            memberPath,
                            JavaScriptApiKind.Global,
                            options,
                            diagnostics);
                    }
                    else if (memberPath.StartsWith("module.exports", StringComparison.Ordinal))
                    {
                        AddUnsupportedAttachedItem(
                            comments,
                            attachedCommentStarts,
                            source,
                            relativePath,
                            expressionStatement,
                            "CommonJS exports are deferred from JavaScript API harvesting v1.",
                            diagnostics);
                    }

                    break;

                case ClassDeclaration classDeclaration:
                    AddUnsupportedAttachedItem(
                        comments,
                        attachedCommentStarts,
                        source,
                        relativePath,
                        classDeclaration,
                        $"JavaScript class harvesting is deferred from v1 for '{classDeclaration.Id?.Name ?? "anonymous class"}'.",
                        diagnostics);
                    break;
            }
        }

        foreach (var comment in comments)
        {
            if (attachedCommentStarts.Contains(comment.Start))
            {
                continue;
            }

            AddStandaloneItem(comment, items, source, relativePath, options, diagnostics);
        }

        foreach (var item in items.Where(static item => item.Kind == JavaScriptApiKind.Event))
        {
            AddEventCompletenessDiagnostics(item, diagnostics);
        }

        return items;
    }

    private static IEnumerable<Node> EnumerateNodes(Node root)
    {
        yield return root;

        foreach (var child in root.ChildNodes)
        {
            foreach (var descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static void AddAttachedItem(
        IReadOnlyList<Comment> comments,
        ISet<int> attachedCommentStarts,
        ICollection<JavaScriptApiItem> items,
        string source,
        string relativePath,
        Node attachmentNode,
        Node provenanceNode,
        string? name,
        JavaScriptApiKind kind,
        AppSurfaceDocsJavaScriptHarvestOptions options,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var comment = FindLeadingBlockComment(comments, attachmentNode, source);
        if (comment is null)
        {
            return;
        }

        if (attachedCommentStarts.Contains(comment.Value.Start))
        {
            return;
        }

        var doclet = ParseDoclet(GetCommentText(source, comment.Value));
        if (!ShouldIncludeDoclet(doclet, options))
        {
            return;
        }

        attachedCommentStarts.Add(comment.Value.Start);
        if (string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add(MalformedDoclet(relativePath, comment.Value.Location.Start.Line, "A public JavaScript doclet was attached to an unnamed declaration."));
            return;
        }

        items.Add(CreateApiItem(name, kind, doclet, relativePath, provenanceNode.Location.Start.Line));
    }

    private static void AddUnsupportedAttachedItem(
        IReadOnlyList<Comment> comments,
        ISet<int> attachedCommentStarts,
        string source,
        string relativePath,
        Node attachmentNode,
        string reason,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var comment = FindLeadingBlockComment(comments, attachmentNode, source);
        if (comment is null)
        {
            return;
        }

        var doclet = ParseDoclet(GetCommentText(source, comment.Value));
        if (!HasPublicSignal(doclet) || IsHardExcluded(doclet))
        {
            return;
        }

        if (doclet.HasTag("event") || doclet.HasTag("typedef"))
        {
            return;
        }

        attachedCommentStarts.Add(comment.Value.Start);
        diagnostics.Add(CreateDiagnostic(
            DocHarvestDiagnosticCodes.JavaScriptUnsupportedPublicShape,
            DocHarvestDiagnosticSeverity.Warning,
            $"Skipped unsupported public JavaScript doclet in '{relativePath}'.",
            reason,
            "Remove @public from this doclet for now, or implement the deferred JavaScript harvesting issue that covers this API shape."));
    }

    private static void AddStandaloneItem(
        Comment comment,
        ICollection<JavaScriptApiItem> items,
        string source,
        string relativePath,
        AppSurfaceDocsJavaScriptHarvestOptions options,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        if (comment.Kind != CommentKind.Block)
        {
            return;
        }

        var doclet = ParseDoclet(GetCommentText(source, comment));
        if (!ShouldIncludeDoclet(doclet, options))
        {
            return;
        }

        if (doclet.TryGetTagValue("event") is { } eventName && !string.IsNullOrWhiteSpace(eventName))
        {
            items.Add(CreateApiItem(eventName, JavaScriptApiKind.Event, doclet, relativePath, comment.Location.Start.Line));
            return;
        }

        if (doclet.HasTag("event"))
        {
            diagnostics.Add(MalformedDoclet(relativePath, comment.Location.Start.Line, "A public JavaScript event doclet is missing an event name."));
            return;
        }

        if (TryGetTypedefName(doclet) is { } typedefName)
        {
            items.Add(CreateApiItem(typedefName, JavaScriptApiKind.Typedef, doclet, relativePath, comment.Location.Start.Line));
            return;
        }

        if (doclet.HasTag("typedef"))
        {
            diagnostics.Add(MalformedDoclet(relativePath, comment.Location.Start.Line, "A public JavaScript typedef doclet is missing a typedef name."));
            return;
        }

        if (HasPublicSignal(doclet))
        {
            diagnostics.Add(MalformedDoclet(
                relativePath,
                comment.Location.Start.Line,
                "A standalone public JavaScript doclet must use a supported v1 kind such as @event or @typedef."));
        }
    }

    private static JavaScriptApiItem CreateApiItem(
        string name,
        JavaScriptApiKind kind,
        JavaScriptDoclet doclet,
        string relativePath,
        int startLine)
    {
        var group = ResolveGroupName(doclet, name, relativePath);
        var summary = doclet.Summary.Length == 0 ? name : doclet.Summary;
        var properties = doclet.GetTagValues("property")
            .Select(ParseTypedMember)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .ToArray();

        return new JavaScriptApiItem(
            name.Trim(),
            kind,
            group,
            summary,
            doclet.Description,
            doclet.GetTagValues("param").Select(ParseTypedMember).Where(static value => value is not null).Select(static value => value!).ToArray(),
            properties,
            doclet.TryGetTagValue("returns") ?? doclet.TryGetTagValue("return"),
            doclet.TryGetTagValue("target"),
            doclet.TryGetTagValue("firesWhen"),
            ParseBooleanTag(doclet.TryGetTagValue("bubbles")),
            ParseBooleanTag(doclet.TryGetTagValue("cancelable")),
            IsDetailNone(doclet),
            doclet.TryGetTagValue("example"),
            doclet.TryGetTagValue("deprecated"),
            relativePath,
            startLine);
    }

    private static void AssignStableAnchors(
        IReadOnlyList<JavaScriptApiItem> items,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        foreach (var group in items.GroupBy(item => item.Group, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var anchorGroup in group.GroupBy(item => BuildAnchorPrefix(item.Kind) + "-" + Slugify(item.Name), StringComparer.Ordinal))
            {
                var ordered = anchorGroup
                    .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.StartLine)
                    .ThenBy(item => item.Name, StringComparer.Ordinal)
                    .ToArray();

                if (ordered.Length > 1)
                {
                    diagnostics.Add(CreateDiagnostic(
                        DocHarvestDiagnosticCodes.JavaScriptDuplicateAnchor,
                        DocHarvestDiagnosticSeverity.Warning,
                        $"JavaScript API group '{group.Key}' has duplicate anchor '{anchorGroup.Key}'.",
                        "Multiple public JavaScript API items normalized to the same anchor, so AppSurface Docs appended deterministic suffixes.",
                        "Rename one of the public JavaScript items or set clearer doclet names if the suffixes are not reader-friendly."));
                }

                for (var index = 0; index < ordered.Length; index++)
                {
                    ordered[index].AnchorId = index == 0
                        ? anchorGroup.Key
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"{anchorGroup.Key}-{index + 1}");
                }
            }
        }
    }

    private static void AddEventCompletenessDiagnostics(
        JavaScriptApiItem item,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(item.Target))
        {
            missing.Add("@target");
        }

        if (string.IsNullOrWhiteSpace(item.FiresWhen))
        {
            missing.Add("@firesWhen");
        }

        if (string.IsNullOrWhiteSpace(item.Example))
        {
            missing.Add("@example");
        }

        if (!item.DetailNone && item.Properties.Count == 0)
        {
            missing.Add("@property detail.* or @detail none");
        }

        if (missing.Count == 0)
        {
            return;
        }

        diagnostics.Add(CreateDiagnostic(
            DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet,
            DocHarvestDiagnosticSeverity.Warning,
            $"JavaScript event '{item.Name}' is missing recommended public contract fields.",
            "The event will render, but readers may not know where it fires, when it fires, what payload it carries, or how to consume it.",
            "Add " + string.Join(", ", missing) + " to the public event doclet."));
    }

    private static IReadOnlyList<DocNode> BuildDocNodes(IReadOnlyList<JavaScriptApiItem> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var reservedGroupSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return items
            .GroupBy(item => item.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => CreateGroupDocNodes(group, reservedGroupSlugs))
            .ToArray();
    }

    private static IReadOnlyList<DocNode> CreateGroupDocNodes(
        IGrouping<string, JavaScriptApiItem> group,
        ISet<string> reservedGroupSlugs)
    {
        var orderedItems = group
            .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StartLine)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var groupSlug = ReserveGroupSlug(group.Key, reservedGroupSlugs);
        var groupPath = $"api/javascript/{groupSlug}";
        var groupTitle = $"{group.Key} JavaScript API";
        var content = new StringBuilder();
        var outline = new List<DocOutlineItem>();
        var provenance = new List<DocSymbolSourceProvenance>();

        content.Append($@"<section class=""doc-type doc-javascript-api"">
<header class=""doc-type-header"">
<span class=""doc-kind"">JavaScript API</span>
<h2>{WebUtility.HtmlEncode(groupTitle)}</h2>
</header>
<div class=""doc-body""><p>Public JavaScript contracts harvested from documented source comments.</p></div>");

        foreach (var item in orderedItems)
        {
            outline.Add(
                new DocOutlineItem
                {
                    Id = item.AnchorId,
                    Title = item.Name,
                    Level = 2
                });
            provenance.Add(
                new DocSymbolSourceProvenance
                {
                    AnchorId = item.AnchorId,
                    SourcePath = item.SourcePath,
                    StartLine = item.StartLine
                });
            content.Append(RenderItem(item, includeSourcePlaceholder: true));
        }

        content.Append("</section>");

        var nodes = new List<DocNode>();
        var groupMetadata = CreateJavaScriptMetadata(
            groupTitle,
            "javascript-api",
            group.Key,
            groupSlug,
            order: 250);
        nodes.Add(
            new DocNode(
                groupTitle,
                groupPath,
                content.ToString(),
                Metadata: groupMetadata,
                Outline: outline,
                SymbolSourceProvenance: provenance));

        foreach (var item in orderedItems)
        {
            var itemPath = $"{groupPath}#{item.AnchorId}";
            nodes.Add(
                new DocNode(
                    item.Name,
                    itemPath,
                    RenderItem(item, includeSourcePlaceholder: false),
                    groupPath,
                    Metadata: CreateJavaScriptMetadata(
                        item.Name,
                        GetPageType(item.Kind),
                        group.Key,
                        groupSlug,
                        order: 251)));
        }

        return nodes;
    }

    private static string ReserveGroupSlug(string groupName, ISet<string> reservedGroupSlugs)
    {
        var baseSlug = Slugify(groupName);
        if (reservedGroupSlugs.Add(baseSlug))
        {
            return baseSlug;
        }

        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(groupName)))[..8].ToLowerInvariant();
        var candidate = $"{baseSlug}-{suffix}";
        var sequence = 2;
        while (!reservedGroupSlugs.Add(candidate))
        {
            candidate = $"{baseSlug}-{suffix}-{sequence.ToString(CultureInfo.InvariantCulture)}";
            sequence++;
        }

        return candidate;
    }

    private static DocMetadata CreateJavaScriptMetadata(
        string title,
        string pageType,
        string groupName,
        string groupSlug,
        int order)
    {
        var baseMetadata = DocMetadataFactory.CreateApiReferenceMetadata(title, groupName);
        return baseMetadata with
        {
            PageType = pageType,
            Component = groupName,
            CanonicalSlug = $"api/javascript/{groupSlug}",
            Order = order,
            Keywords =
            [
                "JavaScript",
                "browser API",
                groupName
            ],
            Aliases =
            [
                $"{groupName} JavaScript",
                $"{groupName} browser API"
            ],
            Breadcrumbs =
            [
                "API Reference",
                "JavaScript",
                groupName
            ],
            BreadcrumbsMatchPathTargets = true
        };
    }

    private static string RenderItem(JavaScriptApiItem item, bool includeSourcePlaceholder)
    {
        var kindLabel = GetKindLabel(item.Kind);
        var builder = new StringBuilder();
        builder.Append(
            $@"<section id=""{WebUtility.HtmlEncode(item.AnchorId)}"" class=""doc-method-group doc-javascript-item doc-javascript-{Slugify(kindLabel)}"">
<header class=""doc-method-group-header"">
<span class=""doc-kind"">{WebUtility.HtmlEncode(kindLabel)}</span>
<h3>{WebUtility.HtmlEncode(item.Name)}</h3>");
        if (includeSourcePlaceholder)
        {
            builder.Append(CreateSymbolSourcePlaceholder(item.AnchorId));
        }

        builder.Append("</header>");
        builder.Append("<div class=\"doc-body\">");
        AppendParagraph(builder, item.Summary);

        if (!string.IsNullOrWhiteSpace(item.Description) && !string.Equals(item.Description, item.Summary, StringComparison.Ordinal))
        {
            AppendParagraph(builder, item.Description);
        }

        if (item.Deprecated is not null)
        {
            AppendParagraph(builder, "Deprecated. " + item.Deprecated);
        }

        AppendSignature(builder, item);
        AppendEventMetadata(builder, item);
        AppendMembers(builder, "Parameters", item.Parameters);
        AppendMembers(builder, item.Kind == JavaScriptApiKind.Event ? "Detail fields" : "Properties", item.Properties);
        if (!string.IsNullOrWhiteSpace(item.Returns))
        {
            AppendParagraph(builder, "Returns: " + item.Returns);
        }

        if (!string.IsNullOrWhiteSpace(item.Example))
        {
            builder.Append("<h4>Example</h4><pre><code class=\"language-js\">");
            builder.Append(WebUtility.HtmlEncode(item.Example.Trim()));
            builder.Append("</code></pre>");
        }

        builder.Append("</div></section>");
        return builder.ToString();
    }

    private static void AppendSignature(StringBuilder builder, JavaScriptApiItem item)
    {
        var signature = item.Kind == JavaScriptApiKind.Function
            ? $"{item.Name}({string.Join(", ", item.Parameters.Select(parameter => parameter.Name))})"
            : item.Name;

        builder.Append("<pre><code class=\"language-js\">");
        builder.Append(WebUtility.HtmlEncode(signature));
        builder.Append("</code></pre>");
    }

    private static void AppendEventMetadata(StringBuilder builder, JavaScriptApiItem item)
    {
        if (item.Kind != JavaScriptApiKind.Event)
        {
            return;
        }

        builder.Append("<ul>");
        AppendListItem(builder, "Target", item.Target);
        AppendListItem(builder, "Fires when", item.FiresWhen);
        AppendListItem(builder, "Bubbles", item.Bubbles?.ToString().ToLowerInvariant());
        AppendListItem(builder, "Cancelable", item.Cancelable?.ToString().ToLowerInvariant());
        if (item.DetailNone)
        {
            AppendListItem(builder, "Detail", "none");
        }

        builder.Append("</ul>");
    }

    private static void AppendMembers(StringBuilder builder, string heading, IReadOnlyList<JavaScriptMember> members)
    {
        if (members.Count == 0)
        {
            return;
        }

        builder.Append("<h4>");
        builder.Append(WebUtility.HtmlEncode(heading));
        builder.Append("</h4><ul>");
        foreach (var member in members)
        {
            builder.Append("<li><code>");
            builder.Append(WebUtility.HtmlEncode(member.Name));
            builder.Append("</code>");
            if (!string.IsNullOrWhiteSpace(member.Type))
            {
                builder.Append(" <span class=\"doc-kind\">");
                builder.Append(WebUtility.HtmlEncode(member.Type));
                builder.Append("</span>");
            }

            if (!string.IsNullOrWhiteSpace(member.Description))
            {
                builder.Append(" - ");
                builder.Append(WebUtility.HtmlEncode(member.Description));
            }

            builder.Append("</li>");
        }

        builder.Append("</ul>");
    }

    private static void AppendParagraph(StringBuilder builder, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        builder.Append("<p>");
        builder.Append(WebUtility.HtmlEncode(text.Trim()));
        builder.Append("</p>");
    }

    private static void AppendListItem(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append("<li><strong>");
        builder.Append(WebUtility.HtmlEncode(label));
        builder.Append(":</strong> ");
        builder.Append(WebUtility.HtmlEncode(value));
        builder.Append("</li>");
    }

    private static string CreateSymbolSourcePlaceholder(string anchorId)
    {
        return $@"<span data-appsurfacedocs-symbol-source=""{WebUtility.HtmlEncode(anchorId)}""></span>";
    }

    private IEnumerable<string> EnumerateJavaScriptFiles(
        string rootPath,
        AppSurfaceDocsJavaScriptHarvestOptions options,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var includePatterns = NormalizeIncludePatterns(options.IncludeGlobs ?? []).ToArray();
        var includeMatcher = new AppSurfaceDocsHarvestPathMatcher(includePatterns);
        var yielded = new HashSet<string>(PathComparer);
        foreach (var includeRoot in ResolveIncludeRoots(fullRoot, includePatterns))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(includeRoot))
            {
                var relativeFile = NormalizeRelativePath(Path.GetRelativePath(fullRoot, includeRoot));
                if (IsJavaScriptFile(includeRoot) && includeMatcher.MatchFirst(relativeFile) is not null && yielded.Add(includeRoot))
                {
                    yield return includeRoot;
                }

                continue;
            }

            if (!Directory.Exists(includeRoot))
            {
                var relativeFile = NormalizeRelativePath(Path.GetRelativePath(fullRoot, includeRoot));
                if (IsJavaScriptFile(includeRoot) && includeMatcher.MatchFirst(relativeFile) is not null && yielded.Add(includeRoot))
                {
                    yield return includeRoot;
                }

                continue;
            }

            foreach (var file in EnumerateJavaScriptFilesUnderRoot(fullRoot, includeRoot, includeMatcher, yielded, cancellationToken))
            {
                yield return file;
            }
        }
    }

    private IEnumerable<string> EnumerateJavaScriptFilesUnderRoot(
        string repositoryRoot,
        string traversalRoot,
        AppSurfaceDocsHarvestPathMatcher includeMatcher,
        HashSet<string> yielded,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(traversalRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var file in EnumerateFilesSafely(current).Where(yielded.Add))
            {
                var relativeFile = NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, file));
                if (includeMatcher.MatchFirst(relativeFile) is null)
                {
                    continue;
                }

                yield return file;
            }

            foreach (var directory in EnumerateDirectoriesSafely(current))
            {
                var relativeDirectory = NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, directory));
                if (ShouldPruneDirectory(relativeDirectory, directory)
                    || includeMatcher.MatchFileInDirectoryOrDescendant(relativeDirectory) is null)
                {
                    continue;
                }

                pending.Push(directory);
            }
        }
    }

    private static IEnumerable<string> NormalizeIncludePatterns(IEnumerable<string> includePatterns)
    {
        foreach (var pattern in includePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var normalizedPattern = AppSurfaceDocsHarvestPathPatternValidator.NormalizeSlashes(pattern.Trim());
            if (!AppSurfaceDocsHarvestPathPatternValidator.IsValidConfiguredGlobPattern(normalizedPattern))
            {
                continue;
            }

            yield return normalizedPattern;
        }
    }

    private static IEnumerable<string> ResolveIncludeRoots(string rootPath, IEnumerable<string> includePatterns)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var yielded = new HashSet<string>(PathComparer);
        foreach (var pattern in includePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var trimmedPattern = pattern.Trim();
            if (Path.IsPathRooted(trimmedPattern))
            {
                continue;
            }

            var staticRoot = GetStaticIncludeRoot(NormalizeRelativePath(trimmedPattern));
            var localStaticRoot = staticRoot.Replace('/', Path.DirectorySeparatorChar);
            var candidate = localStaticRoot.Length == 0
                ? fullRoot
                : Path.GetFullPath(Path.Join(fullRoot, localStaticRoot));
            if (!IsUnderRoot(fullRoot, candidate) || !yielded.Add(candidate))
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static string GetStaticIncludeRoot(string pattern)
    {
        var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var staticSegments = new List<string>();
        foreach (var segment in segments)
        {
            if (ContainsGlobToken(segment))
            {
                break;
            }

            staticSegments.Add(segment);
        }

        return string.Join('/', staticSegments);
    }

    private bool ShouldPruneDirectory(
        string relativeDirectory,
        string directory)
    {
        var directoryInfo = new DirectoryInfo(directory);
        if (AlwaysPrunedDirectoryNames.Contains(directoryInfo.Name, StringComparer.OrdinalIgnoreCase)
            || directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        return _pathPolicy.ShouldPruneDirectory(relativeDirectory, AppSurfaceDocsHarvestSourceKind.JavaScript);
    }

    private static IReadOnlyList<string> EnumerateFilesSafely(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*.js", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (IsFileReadException(ex))
        {
            return [];
        }
    }

    private static IReadOnlyList<string> EnumerateDirectoriesSafely(string directory)
    {
        try
        {
            return Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (IsFileReadException(ex))
        {
            return [];
        }
    }

    private static bool HasUsableIncludeGlobs(IEnumerable<string>? includePatterns)
    {
        return includePatterns?.Any(static pattern => !string.IsNullOrWhiteSpace(pattern)) == true;
    }

    private static bool ContainsGlobToken(string value)
    {
        return value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal);
    }

    private static bool IsJavaScriptFile(string path)
    {
        return string.Equals(Path.GetExtension(path), ".js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRoot(string rootPath, string candidatePath)
    {
        var fullRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        var fullCandidate = Path.GetFullPath(candidatePath);
        return fullCandidate.StartsWith(fullRoot, PathComparison)
               || string.Equals(
                   fullCandidate.TrimEnd(Path.DirectorySeparatorChar),
                   fullRoot.TrimEnd(Path.DirectorySeparatorChar),
                   PathComparison);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static bool IsFileReadException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static Comment? FindLeadingBlockComment(IReadOnlyList<Comment> comments, Node node, string source)
    {
        for (var index = comments.Count - 1; index >= 0; index--)
        {
            var comment = comments[index];
            if (comment.Kind != CommentKind.Block || comment.End > node.Start)
            {
                continue;
            }

            return IsOnlyWhitespace(source, comment.End, node.Start) ? comment : null;
        }

        return null;
    }

    private static bool ShouldIncludeDoclet(JavaScriptDoclet doclet, AppSurfaceDocsJavaScriptHarvestOptions options)
    {
        if (IsHardExcluded(doclet))
        {
            return false;
        }

        return options.RequirePublicTag ? HasPublicSignal(doclet) : doclet.HasAnyTag;
    }

    private static bool HasPublicSignal(JavaScriptDoclet doclet)
    {
        return doclet.HasTag("public");
    }

    private static bool IsHardExcluded(JavaScriptDoclet doclet)
    {
        return doclet.HasTag("internal") || doclet.HasTag("private") || doclet.HasTag("ignore");
    }

    private static JavaScriptDoclet ParseDoclet(string commentText)
    {
        var descriptionLines = new List<string>();
        var tags = new List<JavaScriptDocletTag>();
        JavaScriptDocletTagBuilder? currentTag = null;

        foreach (var line in commentText
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n')
                     .Select(static rawLine => rawLine.Trim().TrimStart('*').Trim()))
        {
            if (line.StartsWith("@", StringComparison.Ordinal))
            {
                if (currentTag is not null)
                {
                    tags.Add(currentTag.Build());
                }

                var separator = line.IndexOfAny([' ', '\t']);
                var tagName = separator < 0 ? line[1..] : line[1..separator];
                var value = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
                currentTag = new JavaScriptDocletTagBuilder(tagName, value);
                continue;
            }

            if (currentTag is not null)
            {
                currentTag.AppendContinuation(line);
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                descriptionLines.Add(line);
            }
        }

        if (currentTag is not null)
        {
            tags.Add(currentTag.Build());
        }

        var description = NormalizeDocText(descriptionLines);
        var summary = descriptionLines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
        return new JavaScriptDoclet(summary, description, tags);
    }

    private static JavaScriptMember? ParseTypedMember(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var remaining = value.Trim();
        string? type = null;
        if (remaining.StartsWith("{", StringComparison.Ordinal))
        {
            var typeEnd = remaining.IndexOf('}', StringComparison.Ordinal);
            if (typeEnd > 0)
            {
                type = remaining[1..typeEnd].Trim();
                remaining = remaining[(typeEnd + 1)..].Trim();
            }
        }

        if (remaining.Length == 0)
        {
            return null;
        }

        var separator = remaining.IndexOfAny([' ', '\t']);
        var name = separator < 0 ? remaining : remaining[..separator];
        var description = separator < 0 ? string.Empty : remaining[(separator + 1)..].Trim();
        if (description.StartsWith("-", StringComparison.Ordinal))
        {
            description = description[1..].Trim();
        }

        return string.IsNullOrWhiteSpace(name)
            ? null
            : new JavaScriptMember(name, type, description);
    }

    private static string? TryGetTypedefName(JavaScriptDoclet doclet)
    {
        var value = doclet.TryGetTagValue("typedef");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var member = ParseTypedMember(value);
        return member?.Name ?? value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }

    private static string ResolveGroupName(JavaScriptDoclet doclet, string itemName, string relativePath)
    {
        var explicitGroup = doclet.TryGetTagValue("namespace") ?? doclet.TryGetTagValue("module");
        if (!string.IsNullOrWhiteSpace(explicitGroup))
        {
            return explicitGroup.Trim();
        }

        if (itemName.StartsWith("window.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = itemName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                return parts[1];
            }
        }

        return Path.GetFileNameWithoutExtension(relativePath);
    }

    private static string? ParseBooleanTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
            ? "true"
            : value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
                ? "false"
                : value.Trim();
    }

    private static bool IsDetailNone(JavaScriptDoclet doclet)
    {
        return doclet.TryGetTagValue("detail")?.Equals("none", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? TryGetMemberPath(Node node)
    {
        return node switch
        {
            Identifier identifier => identifier.Name,
            MemberExpression memberExpression => TryGetMemberPath(memberExpression),
            _ => null
        };
    }

    private static string? TryGetMemberPath(MemberExpression memberExpression)
    {
        var objectPath = TryGetMemberPath(memberExpression.Object);
        if (objectPath is null)
        {
            return null;
        }

        var propertyName = memberExpression.Property switch
        {
            Identifier identifier when !memberExpression.Computed => identifier.Name,
            StringLiteral stringLiteral when memberExpression.Computed => stringLiteral.Value,
            _ => null
        };

        return propertyName is null ? null : objectPath + "." + propertyName;
    }

    private static string GetCommentText(string source, Comment comment)
    {
        return source[comment.ContentRange.Start..comment.ContentRange.End].Trim();
    }

    private static bool IsOnlyWhitespace(string source, int start, int end)
    {
        if (start > end)
        {
            return false;
        }

        for (var index = start; index < end; index++)
        {
            if (!char.IsWhiteSpace(source[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string NormalizeDocText(IEnumerable<string> lines)
    {
        return string.Join(
            " ",
            lines
                .Select(static line => line.Trim())
                .Where(static line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string Slugify(string value)
    {
        var normalized = UnsafeSlugCharacterRegex
            .Replace(value.Trim().ToLowerInvariant(), "-")
            .Trim('-');

        return normalized.Length == 0 ? "unnamed" : normalized;
    }

    private static string BuildAnchorPrefix(JavaScriptApiKind kind)
    {
        return kind.ToString().ToLowerInvariant();
    }

    private static string GetKindLabel(JavaScriptApiKind kind)
    {
        return $"JavaScript {kind}";
    }

    private static string GetPageType(JavaScriptApiKind kind)
    {
        return $"javascript-{BuildAnchorPrefix(kind)}";
    }

    private static DocHarvestDiagnostic MalformedDoclet(string relativePath, int line, string cause)
    {
        return CreateDiagnostic(
            DocHarvestDiagnosticCodes.JavaScriptMalformedPublicDoclet,
            DocHarvestDiagnosticSeverity.Warning,
            $"Skipped malformed public JavaScript doclet in '{relativePath}' at line {line.ToString(CultureInfo.InvariantCulture)}.",
            cause,
            "Update the JSDoc block to use a supported v1 public shape, or remove @public until the contract is ready to publish.");
    }

    private static DocHarvestDiagnostic CreateDiagnostic(
        string code,
        DocHarvestDiagnosticSeverity severity,
        string problem,
        string cause,
        string fix)
    {
        return new DocHarvestDiagnostic(code, severity, HarvesterType, problem, cause, fix);
    }

    private enum JavaScriptApiKind
    {
        Function,
        Constant,
        Global,
        Event,
        Typedef
    }

    private sealed record JavaScriptDoclet(
        string Summary,
        string Description,
        IReadOnlyList<JavaScriptDocletTag> Tags)
    {
        public bool HasAnyTag => Tags.Count > 0;

        public bool HasTag(string name)
        {
            return Tags.Any(tag => tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public string? TryGetTagValue(string name)
        {
            return Tags.FirstOrDefault(tag => tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        public IEnumerable<string> GetTagValues(string name)
        {
            return Tags
                .Where(tag => tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(tag => tag.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value));
        }
    }

    private sealed record JavaScriptDocletTag(string Name, string Value);

    private sealed class JavaScriptDocletTagBuilder
    {
        private readonly StringBuilder _value = new();

        public JavaScriptDocletTagBuilder(string name, string initialValue)
        {
            Name = name;
            if (!string.IsNullOrWhiteSpace(initialValue))
            {
                _value.Append(initialValue.Trim());
            }
        }

        private string Name { get; }

        public void AppendContinuation(string line)
        {
            if (_value.Length > 0)
            {
                _value.Append('\n');
            }

            _value.Append(line);
        }

        public JavaScriptDocletTag Build()
        {
            return new JavaScriptDocletTag(Name, _value.ToString().Trim());
        }
    }

    private sealed record JavaScriptMember(string Name, string? Type, string Description);

    private sealed record JavaScriptApiItem(
        string Name,
        JavaScriptApiKind Kind,
        string Group,
        string Summary,
        string Description,
        IReadOnlyList<JavaScriptMember> Parameters,
        IReadOnlyList<JavaScriptMember> Properties,
        string? Returns,
        string? Target,
        string? FiresWhen,
        string? Bubbles,
        string? Cancelable,
        bool DetailNone,
        string? Example,
        string? Deprecated,
        string SourcePath,
        int StartLine)
    {
        public string AnchorId { get; set; } = string.Empty;
    }

}
