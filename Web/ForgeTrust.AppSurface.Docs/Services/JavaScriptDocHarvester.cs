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
/// Harvests intentionally public JavaScript API doclets from policy-approved plain <c>.js</c> source files.
/// </summary>
/// <remarks>
/// The harvester is enabled by default through <see cref="AppSurfaceDocsJavaScriptHarvestOptions.Enabled"/> and scans
/// repository-relative JavaScript candidates that pass the shared harvest path policy. V1 is deliberately strict: broad
/// discovery requires <c>@public</c>, treats <c>@internal</c>, <c>@private</c>, and <c>@ignore</c> as hard exclusions,
/// skips file and directory reparse points before reads or descent, and turns unsupported public shapes into harvest
/// diagnostics instead of partial docs.
/// </remarks>
public sealed class JavaScriptDocHarvester : IDocHarvester, IDocHarvesterDiagnosticProvider, IDocHarvesterActivation, IDocHarvesterHealthParticipation
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
    /// Scans policy-approved JavaScript files under the repository root and returns generated AppSurface Docs API nodes.
    /// </summary>
    /// <param name="rootPath">The repository root used to resolve include and exclude globs.</param>
    /// <param name="cancellationToken">An optional token to observe while reading and parsing files.</param>
    /// <returns>Generated group pages plus fragment-addressable stub nodes for harvested JavaScript API items.</returns>
    public async Task<IReadOnlyList<DocNode>> HarvestAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        return await HarvestAsync(rootPath, _pathPolicy, cancellationToken);
    }

    /// <summary>
    /// Scans JavaScript sources with the repository-scoped path policy captured for the current aggregation pass.
    /// </summary>
    /// <param name="context">The harvest context containing the repository root and active path policy snapshot.</param>
    /// <param name="cancellationToken">An optional token to observe while reading and parsing files.</param>
    /// <returns>Generated JavaScript API group pages and fragment-addressable API nodes.</returns>
    /// <remarks>
    /// This overload is used by the aggregator so VCS ignore exclusions are applied consistently across traversal and
    /// file inclusion checks. Custom harvesters continue to use the public <see cref="HarvestAsync(string, CancellationToken)"/>
    /// contract.
    /// </remarks>
    internal async Task<IReadOnlyList<DocNode>> HarvestAsync(
        DocHarvestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await HarvestAsync(context.RepositoryRoot, context.PathPolicy, cancellationToken);
    }

    private async Task<IReadOnlyList<DocNode>> HarvestAsync(
        string rootPath,
        IHarvestPathPolicy pathPolicy,
        CancellationToken cancellationToken)
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

            var includePatterns = NormalizeIncludePatterns(javaScriptOptions.IncludeGlobs ?? []).ToArray();
            var requirePublicTag = ShouldRequirePublicTag(javaScriptOptions, includePatterns);
            var harvestedItems = new List<JavaScriptApiItem>();
            foreach (var filePath in EnumerateJavaScriptFiles(rootPath, includePatterns, pathPolicy, diagnostics, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath));
                if (!pathPolicy.ShouldIncludeFilePath(relativePath, AppSurfaceDocsHarvestSourceKind.JavaScript))
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
                    if (requirePublicTag && !source.Contains("@public", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    harvestedItems.AddRange(ParseFile(source, relativePath, javaScriptOptions, requirePublicTag, diagnostics));
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
            ResolveTypedefReferences(harvestedItems, diagnostics);
            foreach (var item in harvestedItems)
            {
                AddCompletenessDiagnostics(item, javaScriptOptions.RequireCompleteEventDoclets, diagnostics);
            }

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

    bool IDocHarvesterHealthParticipation.ParticipatesInStrictHealth
    {
        get
        {
            var javaScriptOptions = _options.Harvest?.JavaScript ?? new AppSurfaceDocsJavaScriptHarvestOptions();
            var includePatterns = NormalizeIncludePatterns(javaScriptOptions.IncludeGlobs ?? []);
            return javaScriptOptions.StrictHealth || HasUsableIncludeGlobs(includePatterns);
        }
    }

    IReadOnlyList<DocHarvestDiagnostic> IDocHarvesterDiagnosticProvider.GetHarvestDiagnostics()
    {
        return _lastDiagnostics;
    }

    private static IReadOnlyList<JavaScriptApiItem> ParseFile(
        string source,
        string relativePath,
        AppSurfaceDocsJavaScriptHarvestOptions options,
        bool requirePublicTag,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var comments = new List<Comment>();
        var script = ParseJavaScriptProgram(source, comments);
        var attachedCommentStarts = new HashSet<int>();
        var items = new List<JavaScriptApiItem>();

        foreach (var statement in EnumerateNodes(script))
        {
            switch (statement)
            {
                case ExportNamedDeclaration { Declaration: FunctionDeclaration functionDeclaration } exportNamedDeclaration:
                    AddAttachedItem(
                        comments,
                        attachedCommentStarts,
                        items,
                        source,
                        relativePath,
                        exportNamedDeclaration,
                        functionDeclaration,
                        functionDeclaration.Id?.Name,
                        JavaScriptApiKind.Function,
                        options,
                        requirePublicTag,
                        diagnostics);
                    break;

                case ExportNamedDeclaration { Declaration: VariableDeclaration variableDeclaration } exportNamedDeclaration:
                    AddVariableDeclarationItems(
                        comments,
                        attachedCommentStarts,
                        items,
                        source,
                        relativePath,
                        exportNamedDeclaration,
                        variableDeclaration,
                        options,
                        requirePublicTag,
                        diagnostics);
                    break;

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
                        requirePublicTag,
                        diagnostics);
                    break;

                case VariableDeclaration variableDeclaration:
                    AddVariableDeclarationItems(
                        comments,
                        attachedCommentStarts,
                        items,
                        source,
                        relativePath,
                        variableDeclaration,
                        variableDeclaration,
                        options,
                        requirePublicTag,
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
                            requirePublicTag,
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

            AddStandaloneItem(comment, items, source, relativePath, options, requirePublicTag, diagnostics);
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

    private static Node ParseJavaScriptProgram(string source, List<Comment> comments)
    {
        try
        {
            return CreateParser(comments).ParseModule(source);
        }
        catch (ParseErrorException)
        {
            comments.Clear();
            return CreateParser(comments).ParseScript(source);
        }
    }

    private static Parser CreateParser(List<Comment> comments)
    {
        return new Parser(new ParserOptions
        {
            EcmaVersion = EcmaVersion.Latest,
            OnComment = (in Comment comment) => comments.Add(comment)
        });
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
        bool requirePublicTag,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var comment = FindLeadingBlockComment(comments, attachmentNode, source);
        if (comment is null)
        {
            return;
        }

        var doclet = ParseDoclet(GetCommentText(source, comment.Value));
        if (HasStandaloneContractTag(doclet))
        {
            return;
        }

        if (!ShouldIncludeDoclet(doclet, requirePublicTag))
        {
            return;
        }

        if (!attachedCommentStarts.Add(comment.Value.Start)) return;
        if (string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add(MalformedDoclet(relativePath, comment.Value.Location.Start.Line, "A public JavaScript doclet was attached to an unnamed declaration."));
            return;
        }

        items.Add(CreateApiItem(name, kind, doclet, relativePath, provenanceNode.Location.Start.Line, options));
    }

    private static void AddVariableDeclarationItems(
        IReadOnlyList<Comment> comments,
        ISet<int> attachedCommentStarts,
        ICollection<JavaScriptApiItem> items,
        string source,
        string relativePath,
        Node attachmentNode,
        VariableDeclaration variableDeclaration,
        AppSurfaceDocsJavaScriptHarvestOptions options,
        bool requirePublicTag,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        if (variableDeclaration.Declarations.Count != 1)
        {
            AddUnsupportedAttachedItem(
                comments,
                attachedCommentStarts,
                source,
                relativePath,
                attachmentNode,
                "Multiple JavaScript declarators cannot share one public doclet in v1.",
                diagnostics);
            return;
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
            attachmentNode,
            variableDeclaration,
            name,
            kind,
            options,
            requirePublicTag,
            diagnostics);
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

        if (HasStandaloneContractTag(doclet))
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
        bool requirePublicTag,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        if (comment.Kind != CommentKind.Block)
        {
            return;
        }

        var doclet = ParseDoclet(GetCommentText(source, comment));
        if (!ShouldIncludeDoclet(doclet, requirePublicTag))
        {
            return;
        }

        if (TryGetStandaloneContract(doclet, out var kind, out var name))
        {
            if (!ValidateStandaloneContractName(kind, name, doclet, relativePath, comment.Location.Start.Line, diagnostics))
            {
                return;
            }

            items.Add(CreateApiItem(name, kind, doclet, relativePath, comment.Location.Start.Line, options));
            return;
        }

        if (TryGetMalformedStandaloneContractCause(doclet) is { } malformedCause)
        {
            diagnostics.Add(MalformedDoclet(relativePath, comment.Location.Start.Line, malformedCause));
            return;
        }

        if (HasPublicSignal(doclet))
        {
            diagnostics.Add(MalformedDoclet(
                relativePath,
                comment.Location.Start.Line,
                "A standalone public JavaScript doclet must use a supported v1 kind such as @event, @typedef, @attribute, @config, @moduleContract, @cssCustomProperty, or @cssHook."));
        }
    }

    private static JavaScriptApiItem CreateApiItem(
        string name,
        JavaScriptApiKind kind,
        JavaScriptDoclet doclet,
        string relativePath,
        int startLine,
        AppSurfaceDocsJavaScriptHarvestOptions options)
    {
        var group = ResolveGroup(doclet, name, relativePath, options);
        var summary = doclet.Summary.Length == 0 ? name : doclet.Summary;
        var properties = doclet.GetTagValues("property")
            .Select(ParseTypedMember)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .ToArray();

        return new JavaScriptApiItem(
            name.Trim(),
            kind,
            group.Identity,
            group.DisplayName,
            group.IsPathFallback,
            summary,
            doclet.Description,
            doclet.GetTagValues("param").Select(ParseTypedMember).Where(static value => value is not null).Select(static value => value!).ToArray(),
            properties,
            ParseReturnValue(doclet.TryGetTagValue("returns") ?? doclet.TryGetTagValue("return")),
            doclet.TryGetTagValue("target"),
            doclet.TryGetTagValue("firesWhen"),
            doclet.TryGetTagValue("type"),
            TryCreateTypedefReferenceNameFromBracedExpression(doclet.TryGetTagValue("type")),
            doclet.TryGetTagValue("default"),
            doclet.TryGetTagValue("values"),
            doclet.TryGetTagValue("source"),
            doclet.TryGetTagValue("signature"),
            doclet.TryGetTagValue("syntax"),
            doclet.TryGetTagValue("inherits"),
            doclet.TryGetTagValue("hookKind"),
            doclet.TryGetTagValue("stability"),
            ParseBooleanTag(doclet.TryGetTagValue("bubbles")),
            ParseBooleanTag(doclet.TryGetTagValue("cancelable")),
            IsDetailNone(doclet),
            HasPublicSignal(doclet),
            doclet.TryGetTagValue("example"),
            doclet.TryGetTagValue("deprecated"),
            relativePath,
            startLine);
    }

    private static bool HasStandaloneContractTag(JavaScriptDoclet doclet)
    {
        return doclet.HasTag("event")
               || doclet.HasTag("typedef")
               || doclet.HasTag("attribute")
               || doclet.HasTag("config")
               || doclet.HasTag("moduleContract")
               || doclet.HasTag("cssCustomProperty")
               || doclet.HasTag("cssHook");
    }

    private static bool TryGetStandaloneContract(JavaScriptDoclet doclet, out JavaScriptApiKind kind, out string name)
    {
        if (TryGetTagName(doclet, "event") is { } eventName)
        {
            kind = JavaScriptApiKind.Event;
            name = eventName;
            return true;
        }

        if (TryGetTypedefName(doclet) is { } typedefName)
        {
            kind = JavaScriptApiKind.Typedef;
            name = typedefName;
            return true;
        }

        if (TryGetTagName(doclet, "attribute") is { } attributeName)
        {
            kind = JavaScriptApiKind.Attribute;
            name = attributeName;
            return true;
        }

        if (TryGetTagName(doclet, "config") is { } configName)
        {
            kind = JavaScriptApiKind.Config;
            name = configName;
            return true;
        }

        if (TryGetTagName(doclet, "moduleContract") is { } moduleContractName)
        {
            kind = JavaScriptApiKind.ModuleContract;
            name = moduleContractName;
            return true;
        }

        if (TryGetTagName(doclet, "cssCustomProperty") is { } cssCustomPropertyName)
        {
            kind = JavaScriptApiKind.CssCustomProperty;
            name = cssCustomPropertyName;
            return true;
        }

        if (TryGetTagName(doclet, "cssHook") is { } cssHookName)
        {
            kind = JavaScriptApiKind.CssHook;
            name = cssHookName;
            return true;
        }

        kind = default;
        name = string.Empty;
        return false;
    }

    private static string? TryGetTagName(JavaScriptDoclet doclet, string tagName)
    {
        var value = doclet.TryGetTagValue(tagName);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string? TryGetMalformedStandaloneContractCause(JavaScriptDoclet doclet)
    {
        if (doclet.HasTag("event"))
        {
            return "A public JavaScript event doclet is missing an event name.";
        }

        if (doclet.HasTag("typedef"))
        {
            return "A public JavaScript typedef doclet is missing a typedef name.";
        }

        if (doclet.HasTag("attribute"))
        {
            return "A public JavaScript attribute doclet is missing an attribute name.";
        }

        if (doclet.HasTag("config"))
        {
            return "A public JavaScript config doclet is missing a config field name.";
        }

        if (doclet.HasTag("moduleContract"))
        {
            return "A public JavaScript module contract doclet is missing a contract name.";
        }

        if (doclet.HasTag("cssCustomProperty"))
        {
            return "A public JavaScript CSS custom property doclet is missing a custom property name.";
        }

        if (doclet.HasTag("cssHook"))
        {
            return "A public JavaScript CSS hook doclet is missing a selector.";
        }

        return null;
    }

    private static bool ValidateStandaloneContractName(
        JavaScriptApiKind kind,
        string name,
        JavaScriptDoclet doclet,
        string relativePath,
        int line,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        if (kind == JavaScriptApiKind.CssCustomProperty && !name.StartsWith("--", StringComparison.Ordinal))
        {
            diagnostics.Add(MalformedDoclet(
                relativePath,
                line,
                "A public JavaScript CSS custom property doclet name must start with '--'."));
            return false;
        }

        if (kind == JavaScriptApiKind.CssHook && !IsValidCssHook(name, doclet))
        {
            diagnostics.Add(MalformedDoclet(
                relativePath,
                line,
                "A public JavaScript CSS hook doclet must use a supported @hookKind and a stable selector or CSS property name."));
            return false;
        }

        return true;
    }

    private static bool IsValidCssHook(string selector, JavaScriptDoclet doclet)
    {
        var hookKind = doclet.TryGetTagValue("hookKind")?.Trim();
        if (string.IsNullOrWhiteSpace(hookKind))
        {
            return false;
        }

        return hookKind.ToLowerInvariant() switch
        {
            "class" => Regex.IsMatch(selector, @"^\.[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant),
            "data-attribute" => Regex.IsMatch(
                selector,
                @"^\[data-[A-Za-z0-9_-]+(?:=(?:""[^""]+""|'[^']+'|[^\]\s]+))?\]$",
                RegexOptions.CultureInvariant),
            "part" => Regex.IsMatch(selector, @"^::part\([A-Za-z_][A-Za-z0-9_-]*\)$", RegexOptions.CultureInvariant),
            "state" => Regex.IsMatch(selector, @"^:state\([A-Za-z_][A-Za-z0-9_-]*\)$", RegexOptions.CultureInvariant),
            "css-property" => IsStableCssPropertyHook(selector),
            "selector" => IsStableNarrowSelector(selector, doclet),
            _ => false
        };
    }

    private static bool IsStableCssPropertyHook(string selector)
    {
        return Regex.IsMatch(selector, @"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    }

    private static bool IsStableNarrowSelector(string selector, JavaScriptDoclet doclet)
    {
        return doclet.TryGetTagValue("stability")?.Equals("stable", StringComparison.OrdinalIgnoreCase) == true
               && !selector.Contains(',', StringComparison.Ordinal)
               && !selector.Any(char.IsWhiteSpace)
               && !selector.Contains('>', StringComparison.Ordinal)
               && !selector.Contains('+', StringComparison.Ordinal)
               && !selector.Contains('~', StringComparison.Ordinal);
    }

    private static void AssignStableAnchors(
        IReadOnlyList<JavaScriptApiItem> items,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        foreach (var group in items.GroupBy(item => item.GroupIdentity, StringComparer.OrdinalIgnoreCase))
        {
            var groupItems = group.ToArray();
            var groupDisplayName = groupItems[0].GroupDisplayName;
            var groupIdentity = group.Key;
            foreach (var anchorGroup in groupItems.GroupBy(item => BuildAnchorPrefix(item.Kind) + "-" + Slugify(item.Name), StringComparer.Ordinal))
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
                        $"JavaScript API group '{groupDisplayName}' with identity '{groupIdentity}' has duplicate anchor '{anchorGroup.Key}'.",
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

    private static void ResolveTypedefReferences(
        IReadOnlyList<JavaScriptApiItem> items,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        foreach (var group in items
            .GroupBy(item => item.GroupIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var groupItems = group.ToArray();
                return new
                {
                    GroupIdentity = group.Key,
                    GroupItems = groupItems,
                    GroupDisplayName = groupItems[0].GroupDisplayName
                };
            }))
        {
            var typedefIndex = group.GroupItems
                .Where(static item => item.Kind == JavaScriptApiKind.Typedef)
                .GroupBy(static item => item.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
            var emittedDiagnostics = new HashSet<JavaScriptTypedefDiagnosticKey>();

            foreach (var item in group.GroupItems)
            {
                ResolveMemberTypedefReferences(item.Parameters, group.GroupIdentity, group.GroupDisplayName, typedefIndex, emittedDiagnostics, diagnostics);
                ResolveMemberTypedefReferences(item.Properties, group.GroupIdentity, group.GroupDisplayName, typedefIndex, emittedDiagnostics, diagnostics);

                if (item.Returns?.TypeReferenceName is { } returnsReferenceName)
                {
                    item.Returns.TypeReference = ResolveTypedefReference(
                        returnsReferenceName,
                        group.GroupIdentity,
                        group.GroupDisplayName,
                        typedefIndex,
                        emittedDiagnostics,
                        diagnostics);
                }

                if (item.TypeReferenceName is { } itemTypeReferenceName)
                {
                    item.TypeReference = ResolveTypedefReference(
                        itemTypeReferenceName,
                        group.GroupIdentity,
                        group.GroupDisplayName,
                        typedefIndex,
                        emittedDiagnostics,
                        diagnostics);
                }
            }
        }
    }

    private static void ResolveMemberTypedefReferences(
        IEnumerable<JavaScriptMember> members,
        string groupIdentity,
        string groupDisplayName,
        IReadOnlyDictionary<string, JavaScriptApiItem[]> typedefIndex,
        ISet<JavaScriptTypedefDiagnosticKey> emittedDiagnostics,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        foreach (var member in members)
        {
            if (member.TypeReferenceName is not { } referenceName)
            {
                continue;
            }

            member.TypeReference = ResolveTypedefReference(
                referenceName,
                groupIdentity,
                groupDisplayName,
                typedefIndex,
                emittedDiagnostics,
                diagnostics);
        }
    }

    private static JavaScriptTypedefReference? ResolveTypedefReference(
        string referenceName,
        string groupIdentity,
        string groupDisplayName,
        IReadOnlyDictionary<string, JavaScriptApiItem[]> typedefIndex,
        ISet<JavaScriptTypedefDiagnosticKey> emittedDiagnostics,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        if (!typedefIndex.TryGetValue(referenceName, out var matches))
        {
            AddTypedefReferenceDiagnostic(
                DocHarvestDiagnosticCodes.JavaScriptTypedefReferenceMissing,
                JavaScriptTypedefDiagnosticKind.Missing,
                referenceName,
                groupIdentity,
                groupDisplayName,
                emittedDiagnostics,
                diagnostics);
            return null;
        }

        if (matches.Length > 1)
        {
            AddTypedefReferenceDiagnostic(
                DocHarvestDiagnosticCodes.JavaScriptTypedefReferenceAmbiguous,
                JavaScriptTypedefDiagnosticKind.Ambiguous,
                referenceName,
                groupIdentity,
                groupDisplayName,
                emittedDiagnostics,
                diagnostics);
            return null;
        }

        return new JavaScriptTypedefReference(referenceName, matches[0]);
    }

    private static void AddTypedefReferenceDiagnostic(
        string code,
        JavaScriptTypedefDiagnosticKind kind,
        string referenceName,
        string groupIdentity,
        string groupDisplayName,
        ISet<JavaScriptTypedefDiagnosticKey> emittedDiagnostics,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        if (!emittedDiagnostics.Add(new JavaScriptTypedefDiagnosticKey(groupIdentity.ToUpperInvariant(), referenceName, kind)))
        {
            return;
        }

        var isMissing = kind == JavaScriptTypedefDiagnosticKind.Missing;
        diagnostics.Add(CreateDiagnostic(
            code,
            DocHarvestDiagnosticSeverity.Warning,
            isMissing
                ? $"JavaScript typedef reference '{referenceName}' in group '{groupDisplayName}' could not be resolved."
                : $"JavaScript typedef reference '{referenceName}' in group '{groupDisplayName}' is ambiguous.",
            isMissing
                ? $"A public JavaScript member uses {{{referenceName}}}, but no same-group @typedef named '{referenceName}' was harvested."
                : $"Multiple same-group @typedef doclets have the harvested name '{referenceName}'.",
            isMissing
                ? $"Add a same-group public @typedef named {referenceName}, correct the type name, or leave the type expression unsupported if it should not link."
                : "Rename one typedef or split the docs into distinct JavaScript API groups before referencing it."));
    }

    private static void AddCompletenessDiagnostics(
        JavaScriptApiItem item,
        bool requireCompleteEventDoclets,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var missing = new List<string>();
        var hasInvalidDetailProperties = false;
        var hasDetailNoneConflict = false;
        switch (item.Kind)
        {
            case JavaScriptApiKind.Event:
                AddMissing(missing, item.Target, "@target");
                AddMissing(missing, item.FiresWhen, "@firesWhen");
                if (!item.DetailNone && item.Properties.Count == 0)
                {
                    missing.Add("@property detail.*, @property {PayloadType} detail, or @detail none");
                }

                if (item.Properties.Any(static property => !IsValidEventDetailProperty(property)))
                {
                    hasInvalidDetailProperties = true;
                }

                if (item.DetailNone && item.Properties.Count > 0)
                {
                    hasDetailNoneConflict = true;
                }

                break;

            case JavaScriptApiKind.Attribute:
                AddMissing(missing, item.Target, "@target");
                AddMissing(missing, item.Type, "@type");
                break;

            case JavaScriptApiKind.Config:
                AddMissing(missing, item.Type, "@type");
                AddMissing(missing, item.Source, "@source");
                break;

            case JavaScriptApiKind.ModuleContract:
                AddMissing(missing, item.Signature, "@signature");
                AddMissing(missing, item.Target, "@target");
                break;

            case JavaScriptApiKind.CssCustomProperty:
                AddMissing(missing, item.Target, "@target");
                AddMissing(missing, item.Syntax, "@syntax");
                break;

            case JavaScriptApiKind.CssHook:
                AddMissing(missing, item.Target, "@target");
                AddMissing(missing, item.Stability, "@stability");
                break;
        }

        if (missing.Count == 0 && !hasInvalidDetailProperties && !hasDetailNoneConflict)
        {
            return;
        }

        var isStrictEventDiagnostic = item.Kind == JavaScriptApiKind.Event
                                      && item.IsPublic
                                      && requireCompleteEventDoclets;
        diagnostics.Add(CreateDiagnostic(
            isStrictEventDiagnostic
                ? DocHarvestDiagnosticCodes.JavaScriptIncompletePublicEventDoclet
                : DocHarvestDiagnosticCodes.JavaScriptIncompletePublicDoclet,
            isStrictEventDiagnostic
                ? DocHarvestDiagnosticSeverity.Error
                : DocHarvestDiagnosticSeverity.Warning,
            BuildCompletenessProblem(item, missing.Count > 0, hasInvalidDetailProperties, hasDetailNoneConflict),
            "The item will render, but readers may not know enough about the public browser contract to consume it confidently.",
            BuildCompletenessFix(missing, hasInvalidDetailProperties, hasDetailNoneConflict)));
    }

    private static void AddMissing(ICollection<string> missing, string? value, string tagName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add(tagName);
        }
    }

    private static string BuildCompletenessProblem(
        JavaScriptApiItem item,
        bool hasMissingFields,
        bool hasInvalidDetailProperties,
        bool hasDetailNoneConflict)
    {
        var kindLabel = GetKindLabel(item.Kind);
        if (hasMissingFields && (hasInvalidDetailProperties || hasDetailNoneConflict))
        {
            return $"{kindLabel} '{item.Name}' is missing or has invalid public contract fields.";
        }

        if (hasInvalidDetailProperties || hasDetailNoneConflict)
        {
            return $"{kindLabel} '{item.Name}' has invalid or contradictory public contract fields.";
        }

        return $"{kindLabel} '{item.Name}' is missing public contract fields.";
    }

    private static string BuildCompletenessFix(
        IReadOnlyCollection<string> missing,
        bool hasInvalidDetailProperties,
        bool hasDetailNoneConflict)
    {
        var clauses = new List<string>();
        if (missing.Count > 0)
        {
            clauses.Add("Add " + string.Join(", ", missing) + " to the public JavaScript doclet.");
        }

        if (hasInvalidDetailProperties)
        {
            clauses.Add("Fix @property names to use valid detail.* paths or an exact detail payload typedef reference, or remove invalid event detail properties.");
        }

        if (hasDetailNoneConflict)
        {
            clauses.Add("Remove @detail none or remove the event detail @property tags.");
        }

        return string.Join(" ", clauses);
    }

    /// <summary>
    /// Validates whether a parsed <c>@property</c> name is a supported event detail field contract.
    /// </summary>
    /// <param name="value">Parsed member name from the doclet property tag.</param>
    /// <returns>
    /// <see langword="true"/> when the member name represents a valid <c>detail.*</c> path; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// The validator trims whitespace, strips optional JSDoc property wrappers through
    /// <see cref="StripOptionalPropertyWrapper"/>, requires an ordinal <c>detail.</c> prefix, and validates each remaining
    /// segment with <see cref="IsValidEventDetailPropertySegment"/>. Array contracts use a trailing <c>[]</c> on a segment,
    /// such as <c>detail.items[]</c> or <c>detail.items[].id</c>. Common pitfalls are blank names, omitted
    /// <c>detail.</c> prefixes, empty segments, unsupported characters, and assuming case-insensitive matching.
    /// </remarks>
    internal static bool IsValidEventDetailPropertyName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = StripOptionalPropertyWrapper(value.Trim());
        if (!name.StartsWith("detail.", StringComparison.Ordinal) || name.Length == "detail.".Length)
        {
            return false;
        }

        var segments = name["detail.".Length..].Split('.');
        return segments.All(IsValidEventDetailPropertySegment);
    }

    private static bool IsValidEventDetailProperty(JavaScriptMember property)
    {
        if (IsValidEventDetailPropertyName(property.Name))
        {
            return true;
        }

        return string.Equals(StripOptionalPropertyWrapper(property.Name.Trim()), "detail", StringComparison.Ordinal)
               && property.TypeReference is not null;
    }

    private static string StripOptionalPropertyWrapper(string value)
    {
        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
        {
            return value;
        }

        var inner = value[1..^1].Trim();
        var defaultSeparator = inner.IndexOf('=', StringComparison.Ordinal);
        return defaultSeparator < 0
            ? inner
            : inner[..defaultSeparator].Trim();
    }

    private static bool IsValidEventDetailPropertySegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var segment = value.EndsWith("[]", StringComparison.Ordinal)
            ? value[..^2]
            : value;
        if (segment.Length == 0)
        {
            return false;
        }

        return segment.All(static character =>
            (character >= 'A' && character <= 'Z')
            || (character >= 'a' && character <= 'z')
            || (character >= '0' && character <= '9')
            || character == '_'
            || character == '$'
            || character == '-');
    }

    private static IReadOnlyList<DocNode> BuildDocNodes(IReadOnlyList<JavaScriptApiItem> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var reservedGroupSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return CreateJavaScriptApiGroups(items)
            .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Identity, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => CreateGroupDocNodes(group, reservedGroupSlugs))
            .ToArray();
    }

    private static IReadOnlyList<JavaScriptApiGroup> CreateJavaScriptApiGroups(IReadOnlyList<JavaScriptApiItem> items)
    {
        var groups = items
            .GroupBy(item => item.GroupIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var groupItems = group.ToArray();
                var displayName = groupItems[0].GroupDisplayName;
                var isPathFallback = groupItems.Any(item => item.GroupIsPathFallback);
                var sourcePath = groupItems
                    .Select(item => item.SourcePath)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .First();
                var routeSlugSeed = isPathFallback
                    ? BuildFallbackPathDisplayName(sourcePath)
                    : displayName;

                return new JavaScriptApiGroup(
                    group.Key,
                    displayName,
                    isPathFallback,
                    sourcePath,
                    routeSlugSeed,
                    groupItems);
            })
            .ToArray();
        var duplicateFallbackLabels = groups
            .Where(group => group.IsPathFallback)
            .GroupBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(group => group.Identity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (duplicateFallbackLabels.Count == 0)
        {
            return groups;
        }

        return groups
            .Select(group => duplicateFallbackLabels.Contains(group.Identity)
                ? group with { DisplayName = BuildFallbackPathDisplayName(group.SourcePath) }
                : group)
            .ToArray();
    }

    private static IReadOnlyList<DocNode> CreateGroupDocNodes(
        JavaScriptApiGroup group,
        ISet<string> reservedGroupSlugs)
    {
        var orderedItems = group.Items
            .OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StartLine)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var groupSlug = ReserveGroupSlug(group.RouteSlugSeed, group.Identity, reservedGroupSlugs);
        var groupPath = $"api/javascript/{groupSlug}";
        var groupTitle = $"{group.DisplayName} JavaScript API";
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
            group.DisplayName,
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
                        group.DisplayName,
                        groupSlug,
                        order: 251)));
        }

        return nodes;
    }

    private static string ReserveGroupSlug(string groupName, string groupIdentity, ISet<string> reservedGroupSlugs)
    {
        var baseSlug = Slugify(groupName);
        if (reservedGroupSlugs.Add(baseSlug))
        {
            return baseSlug;
        }

        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(groupIdentity)))[..8].ToLowerInvariant();
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
            CodeLanguage = "javascript",
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
        var renderedPreviews = new HashSet<string>(StringComparer.Ordinal);
        AppendContractMetadata(builder, item, renderedPreviews);
        AppendMembers(builder, "Parameters", item.Parameters, renderedPreviews);
        AppendMembers(builder, item.Kind == JavaScriptApiKind.Event ? "Detail fields" : "Properties", item.Properties, renderedPreviews);
        AppendReturns(builder, item.Returns, renderedPreviews);

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
        var signature = item.Kind switch
        {
            JavaScriptApiKind.Function => $"{item.Name}({string.Join(", ", item.Parameters.Select(parameter => parameter.Name))})",
            JavaScriptApiKind.ModuleContract when !string.IsNullOrWhiteSpace(item.Signature) => item.Signature,
            _ => item.Name
        };

        builder.Append("<pre><code class=\"language-js\">");
        builder.Append(WebUtility.HtmlEncode(signature));
        builder.Append("</code></pre>");
    }

    private static void AppendContractMetadata(
        StringBuilder builder,
        JavaScriptApiItem item,
        ISet<string> renderedPreviews)
    {
        if (!HasContractMetadata(item))
        {
            return;
        }

        builder.Append("<ul>");
        AppendListItem(builder, "Target", item.Target);
        AppendTypeListItem(builder, "Type", item.Type, item.TypeReference);
        AppendListItem(builder, "Default", item.DefaultValue);
        AppendListItem(builder, "Values", item.Values);
        AppendListItem(builder, "Source", item.Source);
        AppendListItem(builder, "Signature", item.Signature);
        AppendListItem(builder, "Syntax", item.Syntax);
        AppendListItem(builder, "Inherits", item.Inherits);
        AppendListItem(builder, "Hook kind", item.HookKind);
        AppendListItem(builder, "Stability", item.Stability);
        if (item.Kind == JavaScriptApiKind.Event)
        {
            AppendListItem(builder, "Fires when", item.FiresWhen);
        }

        AppendListItem(builder, "Bubbles", item.Bubbles?.ToString().ToLowerInvariant());
        AppendListItem(builder, "Cancelable", item.Cancelable?.ToString().ToLowerInvariant());
        if (item.DetailNone)
        {
            AppendListItem(builder, "Detail", "none");
        }

        builder.Append("</ul>");
        AppendTypedefPreview(builder, item.TypeReference, renderedPreviews);
    }

    private static bool HasContractMetadata(JavaScriptApiItem item)
    {
        return !string.IsNullOrWhiteSpace(item.Target)
               || !string.IsNullOrWhiteSpace(item.Type)
               || !string.IsNullOrWhiteSpace(item.DefaultValue)
               || !string.IsNullOrWhiteSpace(item.Values)
               || !string.IsNullOrWhiteSpace(item.Source)
               || !string.IsNullOrWhiteSpace(item.Signature)
               || !string.IsNullOrWhiteSpace(item.Syntax)
               || !string.IsNullOrWhiteSpace(item.Inherits)
               || !string.IsNullOrWhiteSpace(item.HookKind)
               || !string.IsNullOrWhiteSpace(item.Stability)
               || !string.IsNullOrWhiteSpace(item.FiresWhen)
               || !string.IsNullOrWhiteSpace(item.Bubbles)
               || !string.IsNullOrWhiteSpace(item.Cancelable)
               || item.DetailNone;
    }

    private static void AppendMembers(
        StringBuilder builder,
        string heading,
        IReadOnlyList<JavaScriptMember> members,
        ISet<string> renderedPreviews)
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
                AppendTypeValue(builder, member.Type, member.TypeReference);
                builder.Append("</span>");
            }

            if (!string.IsNullOrWhiteSpace(member.Description))
            {
                builder.Append(" - ");
                builder.Append(WebUtility.HtmlEncode(member.Description));
            }

            AppendTypedefPreview(builder, member.TypeReference, renderedPreviews);
            builder.Append("</li>");
        }

        builder.Append("</ul>");
    }

    private static void AppendReturns(
        StringBuilder builder,
        JavaScriptReturnValue? returns,
        ISet<string> renderedPreviews)
    {
        if (returns is null || string.IsNullOrWhiteSpace(returns.OriginalText))
        {
            return;
        }

        if (returns.TypeReference is null || string.IsNullOrWhiteSpace(returns.Type))
        {
            AppendParagraph(builder, "Returns: " + returns.OriginalText);
            return;
        }

        builder.Append("<p>Returns: ");
        AppendTypeValue(builder, returns.Type, returns.TypeReference);
        if (!string.IsNullOrWhiteSpace(returns.Description))
        {
            builder.Append(" - ");
            builder.Append(WebUtility.HtmlEncode(returns.Description));
        }

        builder.Append("</p>");
        AppendTypedefPreview(builder, returns.TypeReference, renderedPreviews);
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

    private static void AppendTypeListItem(
        StringBuilder builder,
        string label,
        string? value,
        JavaScriptTypedefReference? reference)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append("<li><strong>");
        builder.Append(WebUtility.HtmlEncode(label));
        builder.Append(":</strong> ");
        AppendTypeValue(builder, value, reference);
        builder.Append("</li>");
    }

    private static void AppendTypeValue(
        StringBuilder builder,
        string value,
        JavaScriptTypedefReference? reference)
    {
        if (reference is null)
        {
            builder.Append(WebUtility.HtmlEncode(value));
            return;
        }

        builder.Append("<a href=\"#");
        builder.Append(WebUtility.HtmlEncode(reference.Target.AnchorId));
        builder.Append("\">");
        builder.Append(WebUtility.HtmlEncode(value));
        builder.Append("</a>");
    }

    private static void AppendTypedefPreview(
        StringBuilder builder,
        JavaScriptTypedefReference? reference,
        ISet<string> renderedPreviews)
    {
        if (reference is null || !renderedPreviews.Add(reference.Target.AnchorId))
        {
            return;
        }

        var typedef = reference.Target;
        builder.Append("<div class=\"doc-javascript-typedef-preview\"><p>Type preview: ");
        builder.Append("<a href=\"#");
        builder.Append(WebUtility.HtmlEncode(typedef.AnchorId));
        builder.Append("\">");
        builder.Append(WebUtility.HtmlEncode(typedef.Name));
        builder.Append("</a>");
        if (!string.IsNullOrWhiteSpace(typedef.Summary)
            && !string.Equals(typedef.Summary, typedef.Name, StringComparison.Ordinal))
        {
            builder.Append(" - ");
            builder.Append(WebUtility.HtmlEncode(typedef.Summary));
        }

        builder.Append("</p>");
        if (typedef.Properties.Count > 0)
        {
            builder.Append("<ul>");
            foreach (var property in typedef.Properties.Take(5))
            {
                builder.Append("<li><code>");
                builder.Append(WebUtility.HtmlEncode(property.Name));
                builder.Append("</code>");
                if (!string.IsNullOrWhiteSpace(property.Type))
                {
                    builder.Append(" <span class=\"doc-kind\">");
                    builder.Append(WebUtility.HtmlEncode(property.Type));
                    builder.Append("</span>");
                }

                if (!string.IsNullOrWhiteSpace(property.Description))
                {
                    builder.Append(" - ");
                    builder.Append(WebUtility.HtmlEncode(property.Description));
                }

                builder.Append("</li>");
            }

            if (typedef.Properties.Count > 5)
            {
                builder.Append("<li><a href=\"#");
                builder.Append(WebUtility.HtmlEncode(typedef.AnchorId));
                builder.Append("\">View full ");
                builder.Append(WebUtility.HtmlEncode(typedef.Name));
                builder.Append(" contract</a></li>");
            }

            builder.Append("</ul>");
        }

        builder.Append("</div>");
    }

    private static string CreateSymbolSourcePlaceholder(string anchorId)
    {
        return $@"<span data-appsurfacedocs-symbol-source=""{WebUtility.HtmlEncode(anchorId)}""></span>";
    }

    private IEnumerable<string> EnumerateJavaScriptFiles(
        string rootPath,
        IReadOnlyList<string> includePatterns,
        IHarvestPathPolicy pathPolicy,
        List<DocHarvestDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        if (includePatterns.Count == 0)
        {
            var globalIncludePatterns = NormalizeIncludePatterns(_options.Harvest?.Paths?.IncludeGlobs ?? []).ToArray();
            if (globalIncludePatterns.Length > 0)
            {
                foreach (var file in EnumerateJavaScriptFilesForIncludePatterns(
                             fullRoot,
                             globalIncludePatterns,
                             pathPolicy,
                             diagnostics,
                             cancellationToken))
                {
                    yield return file;
                }

                yield break;
            }

            foreach (var file in pathPolicy.EnumerateCandidateFiles(
                         fullRoot,
                         AppSurfaceDocsHarvestSourceKind.JavaScript,
                         "*.js",
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            yield break;
        }

        foreach (var file in EnumerateJavaScriptFilesForIncludePatterns(fullRoot, includePatterns, pathPolicy, diagnostics, cancellationToken))
        {
            yield return file;
        }
    }

    private IEnumerable<string> EnumerateJavaScriptFilesForIncludePatterns(
        string fullRoot,
        IReadOnlyList<string> includePatterns,
        IHarvestPathPolicy pathPolicy,
        List<DocHarvestDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var includeMatcher = new AppSurfaceDocsHarvestPathMatcher(includePatterns);
        var yielded = new HashSet<string>(PathComparer);
        foreach (var includeRoot in ResolveIncludeRoots(fullRoot, includePatterns))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootCandidate = ClassifyHarvestCandidate(fullRoot, includeRoot.FullPath);
            if (rootCandidate.Status == JavaScriptHarvestCandidateStatus.File)
            {
                if (IsJavaScriptFile(rootCandidate.FullPath) && includeMatcher.MatchFirst(rootCandidate.RelativePath) is not null && yielded.Add(rootCandidate.FullPath))
                {
                    yield return rootCandidate.FullPath;
                }

                continue;
            }

            if (rootCandidate.Status != JavaScriptHarvestCandidateStatus.Directory)
            {
                AddIncludeRootDiagnostic(includeRoot, rootCandidate, diagnostics);
                continue;
            }

            if (!CanEnumerateDirectory(rootCandidate.FullPath))
            {
                AddIncludeRootDiagnostic(
                    includeRoot,
                    rootCandidate with { Status = JavaScriptHarvestCandidateStatus.Inaccessible },
                    diagnostics);
                continue;
            }

            foreach (var file in EnumerateJavaScriptFilesUnderRoot(fullRoot, rootCandidate.FullPath, includeMatcher, pathPolicy, yielded, cancellationToken))
            {
                yield return file;
            }
        }
    }

    private IEnumerable<string> EnumerateJavaScriptFilesUnderRoot(
        string repositoryRoot,
        string traversalRoot,
        AppSurfaceDocsHarvestPathMatcher includeMatcher,
        IHarvestPathPolicy pathPolicy,
        HashSet<string> yielded,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(traversalRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var file in EnumerateFilesSafely(current))
            {
                var candidate = ClassifyHarvestCandidate(repositoryRoot, file);
                if (candidate.Status != JavaScriptHarvestCandidateStatus.File
                    || includeMatcher.MatchFirst(candidate.RelativePath) is null
                    || !yielded.Add(candidate.FullPath))
                {
                    continue;
                }

                yield return candidate.FullPath;
            }

            foreach (var directory in EnumerateDirectoriesSafely(current))
            {
                var candidate = ClassifyHarvestCandidate(repositoryRoot, directory);
                if (candidate.Status != JavaScriptHarvestCandidateStatus.Directory
                    || ShouldPruneDirectory(candidate.RelativePath, candidate.FullPath, pathPolicy)
                    || includeMatcher.MatchFileInDirectoryOrDescendant(candidate.RelativePath) is null)
                {
                    continue;
                }

                pending.Push(candidate.FullPath);
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

    private static IEnumerable<JavaScriptIncludeRoot> ResolveIncludeRoots(string rootPath, IEnumerable<string> includePatterns)
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

            yield return new JavaScriptIncludeRoot(
                candidate,
                NormalizeRelativePath(Path.GetRelativePath(fullRoot, candidate)),
                ContainsGlobToken(trimmedPattern),
                CouldMatchJavaScriptFile(trimmedPattern));
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
        string directory,
        IHarvestPathPolicy pathPolicy)
    {
        var directoryInfo = new DirectoryInfo(directory);
        if (AlwaysPrunedDirectoryNames.Contains(directoryInfo.Name, StringComparer.OrdinalIgnoreCase)
            || directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        return pathPolicy.ShouldPruneDirectory(relativeDirectory, AppSurfaceDocsHarvestSourceKind.JavaScript);
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

    private static bool CanEnumerateDirectory(string directory)
    {
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            _ = enumerator.MoveNext();
            return true;
        }
        catch (Exception ex) when (IsFileReadException(ex))
        {
            return false;
        }
    }

    private static bool HasUsableIncludeGlobs(IEnumerable<string>? includePatterns)
    {
        return includePatterns?.Any(static pattern => !string.IsNullOrWhiteSpace(pattern)) == true;
    }

    private static bool ContainsGlobToken(string value)
    {
        return value.Contains('*', StringComparison.Ordinal)
               || value.Contains('?', StringComparison.Ordinal)
               || value.Contains('[', StringComparison.Ordinal);
    }

    private static bool CouldMatchJavaScriptFile(string pattern)
    {
        var normalizedPattern = NormalizeRelativePath(pattern);
        if (!ContainsGlobToken(normalizedPattern))
        {
            return IsJavaScriptFile(normalizedPattern);
        }

        var fileNamePattern = Path.GetFileName(normalizedPattern);
        var extension = Path.GetExtension(fileNamePattern);
        return extension.Length == 0
               || ContainsGlobToken(extension)
               || string.Equals(extension, ".js", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classifies a JavaScript harvest candidate before the harvester reads a source file or descends into a directory.
    /// </summary>
    /// <param name="rootPath">The repository root that bounds built-in JavaScript harvesting.</param>
    /// <param name="candidatePath">The candidate file or directory path to inspect.</param>
    /// <returns>The candidate's normalized path metadata and safety state.</returns>
    internal static JavaScriptHarvestCandidate ClassifyHarvestCandidate(string rootPath, string candidatePath)
    {
        return ClassifyHarvestCandidate(rootPath, candidatePath, File.GetAttributes);
    }

    /// <summary>
    /// Classifies a JavaScript harvest candidate with a caller-supplied attribute reader for deterministic boundary tests.
    /// </summary>
    /// <param name="rootPath">The repository root that bounds built-in JavaScript harvesting.</param>
    /// <param name="candidatePath">The candidate file or directory path to inspect.</param>
    /// <param name="getAttributes">Reads file-system attributes for an already-normalized in-root candidate.</param>
    /// <returns>The candidate's normalized path metadata and safety state.</returns>
    internal static JavaScriptHarvestCandidate ClassifyHarvestCandidate(
        string rootPath,
        string candidatePath,
        Func<string, FileAttributes> getAttributes)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var fullCandidate = Path.GetFullPath(candidatePath);
        var relativePath = NormalizeRelativePath(Path.GetRelativePath(fullRoot, fullCandidate));
        if (!IsUnderRoot(fullRoot, fullCandidate))
        {
            return new JavaScriptHarvestCandidate(JavaScriptHarvestCandidateStatus.OutsideRoot, fullCandidate, relativePath);
        }

        var current = fullRoot;
        var candidateRelativePath = Path.GetRelativePath(fullRoot, fullCandidate);
        if (string.Equals(candidateRelativePath, ".", StringComparison.Ordinal))
        {
            current = fullCandidate;
        }

        FileAttributes attributes = default;
        try
        {
            foreach (var segment in candidateRelativePath.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(segment, ".", StringComparison.Ordinal))
                {
                    continue;
                }

                current = Path.GetFullPath(Path.Join(current, segment));
                attributes = getAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return new JavaScriptHarvestCandidate(JavaScriptHarvestCandidateStatus.ReparsePoint, fullCandidate, relativePath);
                }
            }

            if (string.Equals(current, fullRoot, PathComparison))
            {
                attributes = getAttributes(fullCandidate);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new JavaScriptHarvestCandidate(JavaScriptHarvestCandidateStatus.Missing, fullCandidate, relativePath);
        }
        catch (Exception ex) when (IsFileReadException(ex))
        {
            return new JavaScriptHarvestCandidate(JavaScriptHarvestCandidateStatus.Inaccessible, fullCandidate, relativePath);
        }

        return attributes.HasFlag(FileAttributes.Directory)
            ? new JavaScriptHarvestCandidate(JavaScriptHarvestCandidateStatus.Directory, fullCandidate, relativePath)
            : new JavaScriptHarvestCandidate(JavaScriptHarvestCandidateStatus.File, fullCandidate, relativePath);
    }

    private static void AddIncludeRootDiagnostic(
        JavaScriptIncludeRoot includeRoot,
        JavaScriptHarvestCandidate candidate,
        List<DocHarvestDiagnostic> diagnostics)
    {
        if (!includeRoot.CouldMatchJavaScriptFile)
        {
            return;
        }

        if (includeRoot.HasGlobTokens
            && candidate.Status is not JavaScriptHarvestCandidateStatus.ReparsePoint
                and not JavaScriptHarvestCandidateStatus.Inaccessible)
        {
            return;
        }

        switch (candidate.Status)
        {
            case JavaScriptHarvestCandidateStatus.ReparsePoint:
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.JavaScriptReparsePointSkipped,
                    DocHarvestDiagnosticSeverity.Error,
                    $"Skipped configured JavaScript include '{includeRoot.RelativePath}' because it is a file-system link.",
                    "AppSurface Docs does not follow symlinks, junctions, or other reparse points while harvesting built-in JavaScript source files.",
                    "Replace the link with a real source file under the repository root, include the real non-link source path, disable JavaScript harvesting, or provide a custom harvester for a host-owned trust model. See the JavaScript harvesting pitfalls in the AppSurface Docs README."));
                break;
            case JavaScriptHarvestCandidateStatus.Missing when !includeRoot.HasGlobTokens && IsJavaScriptFile(includeRoot.FullPath):
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.JavaScriptMissingInclude,
                    DocHarvestDiagnosticSeverity.Warning,
                    $"Skipped configured JavaScript include '{includeRoot.RelativePath}' because it does not exist.",
                    "The include pattern resolves to an exact JavaScript source file path, but no file exists at that repository-relative path.",
                    "Fix the JavaScript include glob, create the source file, or remove the include if it is no longer public API surface."));
                break;
            case JavaScriptHarvestCandidateStatus.Inaccessible:
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.JavaScriptParseFailed,
                    DocHarvestDiagnosticSeverity.Warning,
                    $"Skipped configured JavaScript include '{includeRoot.RelativePath}' because it could not be inspected.",
                    "The configured include's file-system attributes or directory entries could not be inspected before JavaScript source traversal.",
                    "Fix file permissions or locks, point the include at an inspectable source path, or remove this path from JavaScript harvesting."));
                break;
        }
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

    private static bool ShouldIncludeDoclet(JavaScriptDoclet doclet, bool requirePublicTag)
    {
        if (IsHardExcluded(doclet))
        {
            return false;
        }

        return requirePublicTag ? HasPublicSignal(doclet) : doclet.HasAnyTag;
    }

    private static bool ShouldRequirePublicTag(
        AppSurfaceDocsJavaScriptHarvestOptions options,
        IEnumerable<string> normalizedIncludePatterns)
    {
        return options.RequirePublicTag || !HasUsableIncludeGlobs(normalizedIncludePatterns);
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
            : new JavaScriptMember(name, type, TryCreateTypedefReferenceName(type), description);
    }

    private static JavaScriptReturnValue? ParseReturnValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var originalText = value.Trim();
        var remaining = originalText;
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

        if (remaining.StartsWith("-", StringComparison.Ordinal))
        {
            remaining = remaining[1..].Trim();
        }

        return new JavaScriptReturnValue(
            originalText,
            type,
            TryCreateTypedefReferenceName(type),
            remaining);
    }

    private static string? TryCreateTypedefReferenceNameFromBracedExpression(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return null;
        }

        var typeEnd = trimmed.IndexOf('}', StringComparison.Ordinal);
        if (typeEnd != trimmed.Length - 1 || typeEnd <= 0)
        {
            return null;
        }

        return TryCreateTypedefReferenceName(trimmed[1..typeEnd].Trim());
    }

    private static string? TryCreateTypedefReferenceName(string? type)
    {
        if (string.IsNullOrWhiteSpace(type) || IsKnownNonTypedefTypeName(type))
        {
            return null;
        }

        return Regex.IsMatch(type, @"^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant)
            ? type
            : null;
    }

    private static bool IsKnownNonTypedefTypeName(string type)
    {
        if ((type.StartsWith("HTML", StringComparison.Ordinal)
             || type.StartsWith("SVG", StringComparison.Ordinal)
             || type.StartsWith("MathML", StringComparison.Ordinal))
            && type.EndsWith("Element", StringComparison.Ordinal))
        {
            return true;
        }

        return type is "*"
            or "any"
            or "unknown"
            or "string"
            or "String"
            or "boolean"
            or "Boolean"
            or "number"
            or "Number"
            or "bigint"
            or "BigInt"
            or "symbol"
            or "Symbol"
            or "object"
            or "Object"
            or "Array"
            or "ArrayBuffer"
            or "DataView"
            or "Date"
            or "Error"
            or "EvalError"
            or "RangeError"
            or "ReferenceError"
            or "SyntaxError"
            or "TypeError"
            or "URIError"
            or "AggregateError"
            or "Function"
            or "Promise"
            or "RegExp"
            or "Map"
            or "Set"
            or "WeakMap"
            or "WeakSet"
            or "Int8Array"
            or "Uint8Array"
            or "Uint8ClampedArray"
            or "Int16Array"
            or "Uint16Array"
            or "Int32Array"
            or "Uint32Array"
            or "Float32Array"
            or "Float64Array"
            or "BigInt64Array"
            or "BigUint64Array"
            or "void"
            or "undefined"
            or "null"
            or "AbortController"
            or "AbortSignal"
            or "Blob"
            or "DOMException"
            or "DOMParser"
            or "File"
            or "FileList"
            or "FormData"
            or "Headers"
            or "Request"
            or "Response"
            or "URL"
            or "URLSearchParams"
            or "HTMLElement"
            or "Element"
            or "Node"
            or "Document"
            or "Window"
            or "Event"
            or "InputEvent"
            or "KeyboardEvent"
            or "MouseEvent"
            or "PointerEvent"
            or "SubmitEvent"
            or "CustomEvent"
            or "Record";
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

    private static JavaScriptApiGroupReference ResolveGroup(
        JavaScriptDoclet doclet,
        string itemName,
        string relativePath,
        AppSurfaceDocsJavaScriptHarvestOptions options)
    {
        if (doclet.TryGetFirstNonBlankTagValue("namespace") is { } namespaceGroup)
        {
            return CreateNamedGroupReference(namespaceGroup);
        }

        if (doclet.TryGetFirstNonBlankTagValue("module") is { } moduleGroup)
        {
            return CreateNamedGroupReference(moduleGroup);
        }

        if (TryResolveConfiguredGroup(relativePath, options.GroupNameRules, out var configuredGroup))
        {
            return CreateNamedGroupReference(configuredGroup);
        }

        if (itemName.StartsWith("window.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = itemName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                return CreateNamedGroupReference(parts[1]);
            }
        }

        var normalizedPath = NormalizeRelativePath(relativePath);
        return new JavaScriptApiGroupReference(
            $"path:{normalizedPath}",
            BuildFallbackBaseDisplayName(normalizedPath),
            IsPathFallback: true);
    }

    private static JavaScriptApiGroupReference CreateNamedGroupReference(string groupName)
    {
        var displayName = groupName.Trim();
        return new JavaScriptApiGroupReference($"name:{displayName}", displayName, IsPathFallback: false);
    }

    private static bool TryResolveConfiguredGroup(
        string relativePath,
        IReadOnlyList<AppSurfaceDocsJavaScriptGroupNameRule>? rules,
        out string groupName)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        foreach (var rule in rules ?? [])
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Name))
            {
                continue;
            }

            var matcher = new AppSurfaceDocsHarvestPathMatcher(rule.IncludeGlobs ?? []);
            if (matcher.MatchFirst(normalizedPath) is not null)
            {
                groupName = rule.Name.Trim();
                return true;
            }
        }

        groupName = string.Empty;
        return false;
    }

    private static string BuildFallbackBaseDisplayName(string relativePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        return string.IsNullOrWhiteSpace(fileName) ? "JavaScript" : fileName;
    }

    private static string BuildFallbackPathDisplayName(string relativePath)
    {
        var pathWithoutExtension = Path.ChangeExtension(NormalizeRelativePath(relativePath), null);
        var segments = pathWithoutExtension
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return BuildFallbackBaseDisplayName(relativePath);
        }

        var segmentCount = Math.Min(2, segments.Length);
        return string.Join('/', segments[^segmentCount..]);
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
        return doclet.TryGetTagValue("detail")?.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) == true;
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
        return kind switch
        {
            JavaScriptApiKind.ModuleContract => "module-contract",
            JavaScriptApiKind.CssCustomProperty => "css-custom-property",
            JavaScriptApiKind.CssHook => "css-hook",
            _ => kind.ToString().ToLowerInvariant()
        };
    }

    private static string GetKindLabel(JavaScriptApiKind kind)
    {
        return kind switch
        {
            JavaScriptApiKind.ModuleContract => "JavaScript Module Contract",
            JavaScriptApiKind.CssCustomProperty => "JavaScript CSS Custom Property",
            JavaScriptApiKind.CssHook => "JavaScript CSS Hook",
            JavaScriptApiKind.Config => "JavaScript Config Field",
            _ => $"JavaScript {kind}"
        };
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

    private readonly record struct JavaScriptIncludeRoot(
        string FullPath,
        string RelativePath,
        bool HasGlobTokens,
        bool CouldMatchJavaScriptFile);

    /// <summary>
    /// Captures the normalized path and boundary decision for a JavaScript harvest file-system candidate.
    /// </summary>
    /// <param name="Status">The pre-read safety state for the candidate.</param>
    /// <param name="FullPath">The normalized absolute candidate path.</param>
    /// <param name="RelativePath">The repository-root-relative candidate path used in glob matching and diagnostics.</param>
    internal readonly record struct JavaScriptHarvestCandidate(
        JavaScriptHarvestCandidateStatus Status,
        string FullPath,
        string RelativePath);

    /// <summary>
    /// Describes how the built-in JavaScript harvester may treat a file-system candidate before reading or traversal.
    /// </summary>
    internal enum JavaScriptHarvestCandidateStatus
    {
        /// <summary>The candidate is a regular file that may be read if it also matches harvest policy.</summary>
        File,

        /// <summary>The candidate is a real directory that may be enumerated if it also matches harvest policy.</summary>
        Directory,

        /// <summary>The candidate path does not exist.</summary>
        Missing,

        /// <summary>The candidate path is outside the configured repository root.</summary>
        OutsideRoot,

        /// <summary>The candidate is a symlink, junction, or other reparse point and must not be followed.</summary>
        ReparsePoint,

        /// <summary>The candidate metadata could not be inspected before a read or traversal decision.</summary>
        Inaccessible
    }

    private enum JavaScriptApiKind
    {
        Function,
        Constant,
        Global,
        Event,
        Typedef,
        Attribute,
        Config,
        ModuleContract,
        CssCustomProperty,
        CssHook
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

        public string? TryGetFirstNonBlankTagValue(string name)
        {
            return Tags
                .Where(tag => tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(tag => tag.Value.Trim())
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
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

    private enum JavaScriptTypedefDiagnosticKind
    {
        Missing,
        Ambiguous
    }

    private sealed record JavaScriptTypedefDiagnosticKey(
        string GroupIdentity,
        string ReferenceName,
        JavaScriptTypedefDiagnosticKind Kind);

    private sealed record JavaScriptTypedefReference(string ReferenceName, JavaScriptApiItem Target);

    private sealed record JavaScriptMember(string Name, string? Type, string? TypeReferenceName, string Description)
    {
        public JavaScriptTypedefReference? TypeReference { get; set; }
    }

    private sealed record JavaScriptReturnValue(
        string OriginalText,
        string? Type,
        string? TypeReferenceName,
        string Description)
    {
        public JavaScriptTypedefReference? TypeReference { get; set; }
    }

    private sealed record JavaScriptApiGroupReference(string Identity, string DisplayName, bool IsPathFallback);

    private sealed record JavaScriptApiGroup(
        string Identity,
        string DisplayName,
        bool IsPathFallback,
        string SourcePath,
        string RouteSlugSeed,
        IReadOnlyList<JavaScriptApiItem> Items);

    private sealed record JavaScriptApiItem(
        string Name,
        JavaScriptApiKind Kind,
        string GroupIdentity,
        string GroupDisplayName,
        bool GroupIsPathFallback,
        string Summary,
        string Description,
        IReadOnlyList<JavaScriptMember> Parameters,
        IReadOnlyList<JavaScriptMember> Properties,
        JavaScriptReturnValue? Returns,
        string? Target,
        string? FiresWhen,
        string? Type,
        string? TypeReferenceName,
        string? DefaultValue,
        string? Values,
        string? Source,
        string? Signature,
        string? Syntax,
        string? Inherits,
        string? HookKind,
        string? Stability,
        string? Bubbles,
        string? Cancelable,
        bool DetailNone,
        bool IsPublic,
        string? Example,
        string? Deprecated,
        string SourcePath,
        int StartLine)
    {
        public string AnchorId { get; set; } = string.Empty;

        public JavaScriptTypedefReference? TypeReference { get; set; }
    }

}
