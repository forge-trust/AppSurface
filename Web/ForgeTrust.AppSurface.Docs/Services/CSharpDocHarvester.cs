using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Docs.Models;
using ForgeTrust.RazorWire;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Harvester implementation that scans C# source files for XML documentation comments.
/// </summary>
public class CSharpDocHarvester : IDocHarvester, IDocHarvesterDiagnosticProvider
{
    private const string HarvesterType = nameof(CSharpDocHarvester);
    private const string MaxFileSizeConfigurationKey = "AppSurfaceDocs:Harvest:CSharp:MaxFileSizeBytes";

    /// <summary>
    /// The greatest supported XML element depth in one C# documentation comment, including a top-level documentation section.
    /// </summary>
    /// <remarks>
    /// The bound lets the typed and legacy compatibility projections safely flatten arbitrary custom XML elements without
    /// unbounded recursion.
    /// It is intentionally internal so regression tests can exercise the published diagnostic behavior without making this
    /// implementation limit part of the application configuration surface.
    /// </remarks>
    internal const int MaximumXmlCommentNestingDepth = 32;

    private readonly AppSurfaceDocsOptions _options;
    private readonly ILogger<CSharpDocHarvester> _logger;
    private readonly AppSurfaceDocsHarvestPathPolicy _pathPolicy;
    private IReadOnlyList<DocHarvestDiagnostic> _lastDiagnostics = [];

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DocumentationCommentPrefixRegex = new(@"^[\t ]*/// ?", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Initializes a new instance of <see cref="CSharpDocHarvester"/> with the provided logger.
    /// </summary>
    public CSharpDocHarvester(ILogger<CSharpDocHarvester> logger)
        : this(new AppSurfaceDocsOptions(), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="CSharpDocHarvester"/> with normalized AppSurface Docs options.
    /// </summary>
    /// <param name="options">Normalized AppSurface Docs options that contain C# harvest settings.</param>
    /// <param name="logger">Logger used for non-fatal C# harvest diagnostics.</param>
    internal CSharpDocHarvester(AppSurfaceDocsOptions options, ILogger<CSharpDocHarvester> logger)
        : this(options, logger, new AppSurfaceDocsHarvestPathPolicy(options, NullLogger<AppSurfaceDocsHarvestPathPolicy>.Instance))
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="CSharpDocHarvester"/> with the provided logger and harvest path policy.
    /// </summary>
    internal CSharpDocHarvester(
        ILogger<CSharpDocHarvester> logger,
        AppSurfaceDocsHarvestPathPolicy pathPolicy)
        : this(new AppSurfaceDocsOptions(), logger, pathPolicy)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="CSharpDocHarvester"/> with normalized options and a shared harvest path policy.
    /// </summary>
    /// <param name="options">Normalized AppSurface Docs options that contain C# harvest settings.</param>
    /// <param name="logger">Logger used for non-fatal C# harvest diagnostics.</param>
    /// <param name="pathPolicy">Shared harvest path policy used to decide which C# candidates publish.</param>
    internal CSharpDocHarvester(
        AppSurfaceDocsOptions options,
        ILogger<CSharpDocHarvester> logger,
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
    /// Collects XML documentation from C# source files under the specified root and produces DocNode entries containing titles, relative file paths with anchors, and HTML-formatted content.
    /// </summary>
    /// <param name="rootPath">The root directory to recursively scan for .cs files.</param>
    /// <param name="cancellationToken">An optional token to observe for cancellation requests.</param>
    /// <returns>A collection of DocNode objects; each contains a title, a relative file path including a fragment anchor, and the extracted HTML documentation.</returns>
    /// <remarks>
    /// Skips files in excluded directories (for example "node_modules", "bin", "obj", "Tests", and "examples") and hidden dot-prefixed directories unless explicitly allowlisted. Dot-prefixed files are included. File and directory reparse points are skipped so symlinks and junctions cannot point the built-in harvester outside <paramref name="rootPath"/>.
    /// </remarks>
    public async Task<IReadOnlyList<DocNode>> HarvestAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        return await HarvestAsync(rootPath, _pathPolicy, progress: null, cancellationToken);
    }

    /// <summary>
    /// Collects XML documentation with the repository-scoped path policy captured for the current aggregation pass.
    /// </summary>
    /// <param name="context">The harvest context containing the repository root and active path policy snapshot.</param>
    /// <param name="cancellationToken">An optional token to observe for cancellation requests.</param>
    /// <returns>A collection of generated C# API documentation nodes.</returns>
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

        if (GetType() != typeof(CSharpDocHarvester))
        {
            return await ((IDocHarvester)this).HarvestAsync(context.RepositoryRoot, cancellationToken);
        }

        return await HarvestTypedAsync(context.RepositoryRoot, context.PathPolicy, context.Progress, cancellationToken);
    }

    /// <summary>
    /// Harvests built-in C# namespace pages into the internal semantic projection consumed by Razor.
    /// </summary>
    /// <remarks>
    /// This is intentionally reachable only through the exact built-in aggregation overload. The public overload above
    /// continues to call the legacy HTML compatibility serializer so existing <see cref="IDocHarvester"/> consumers
    /// and derived harvesters retain their established contract.
    /// </remarks>
    private async Task<IReadOnlyList<DocNode>> HarvestTypedAsync(
        string rootPath,
        IHarvestPathPolicy pathPolicy,
        AppSurfaceDocsHarvestProgressSession? progress,
        CancellationToken cancellationToken)
    {
        var namespacePages = new Dictionary<string, TypedNamespacePage>(StringComparer.OrdinalIgnoreCase);
        var stubNodes = new List<DocNode>();
        var diagnostics = new List<DocHarvestDiagnostic>();

        try
        {
            if (progress is not null)
            {
                await progress.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Discovering);
            }

            var csharpOptions = _options.Harvest?.CSharp ?? new AppSurfaceDocsCSharpHarvestOptions();
            foreach (var file in EnumerateEligibleCSharpFiles(rootPath, pathPolicy, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                if (!pathPolicy.ShouldIncludeFilePath(relativePath, AppSurfaceDocsHarvestSourceKind.CSharp))
                {
                    continue;
                }

                try
                {
                    if (progress is not null)
                    {
                        await progress.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Parsing);
                        await progress.ReportSourceUnitAsync(0);
                    }

                    var readResult = await AppSurfaceDocsParserInputBudget.ReadUtf8SourceAsync(
                        file,
                        relativePath,
                        csharpOptions.MaxFileSizeBytes,
                        MaxFileSizeConfigurationKey,
                        DocHarvestDiagnosticCodes.CSharpFileTooLarge,
                        HarvesterType,
                        "C#",
                        "Exclude generated C# with AppSurfaceDocs:Harvest:CSharp:ExcludeGlobs, or raise AppSurfaceDocs:Harvest:CSharp:MaxFileSizeBytes only for authored source that should be parsed.",
                        cancellationToken);
                    if (!readResult.Included)
                    {
                        diagnostics.Add(readResult.Diagnostic!);
                        continue;
                    }

                    var tree = CSharpSyntaxTree.ParseText(readResult.Source!, cancellationToken: cancellationToken);
                    var syntaxError = tree.GetDiagnostics(cancellationToken)
                        .FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                    if (syntaxError is not null)
                    {
                        diagnostics.Add(CreateCSharpParseDiagnostic(relativePath, syntaxError));
                        continue;
                    }

                    var root = await tree.GetRootAsync(cancellationToken);
                    var fileResult = BuildTypedFileResult(root, relativePath, diagnostics);

                    // Do not mutate aggregate namespace state until every declaration in this source file has been
                    // projected. A later failure therefore cannot expose a partial type, fragment, outline, or search
                    // record from the same file.
                    MergeTypedFileResult(namespacePages, stubNodes, fileResult);
                    if (progress is not null)
                    {
                        await progress.ReportSourceUnitAsync(1);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException
                                           and not OutOfMemoryException
                                           and not StackOverflowException
                                           and not AccessViolationException
                                           and not AppDomainUnloadedException
                                           and not BadImageFormatException
                                           and not CannotUnloadAppDomainException
                                           and not ThreadAbortException)
                {
                    diagnostics.Add(CreateCSharpParseDiagnostic(relativePath, diagnostic: null));
                    _logger.LogError(ex, "Failed to project C# documentation source {File}.", relativePath);
                }
            }

            if (progress is not null)
            {
                await progress.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Finalizing);
            }

            EnsureTypedNamespaceHierarchy(namespacePages);
            var nodes = namespacePages.Values
                .OrderBy(page => page.Path, StringComparer.OrdinalIgnoreCase)
                .Select(
                    page =>
                    {
                        var document = page.ToDocument();
                        return new DocNode(
                            page.Title,
                            page.Path,
                            string.Empty,
                            Metadata: page.Metadata,
                            Outline: document.Outline,
                            SymbolSourceProvenance: document.SymbolSourceProvenance)
                        {
                            CSharpNamespaceDocument = document
                        };
                    })
                .ToList();
            nodes.AddRange(stubNodes);

            if (progress is not null && nodes.Count > 0)
            {
                await progress.ReportOutputOnlyAsync(nodes.Count);
            }

            return nodes;
        }
        finally
        {
            _lastDiagnostics = diagnostics.ToArray();
            LogDiagnostics(diagnostics);
        }
    }

    private TypedCSharpFileResult BuildTypedFileResult(
        SyntaxNode root,
        string relativePath,
        List<DocHarvestDiagnostic> diagnostics)
    {
        var pages = new Dictionary<string, TypedNamespacePage>(StringComparer.OrdinalIgnoreCase);
        var stubs = new List<DocNode>();

        foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeDocumentation = ExtractTypedDocumentation(typeDeclaration, relativePath, diagnostics);
            var methods = typeDeclaration.Members
                .OfType<MethodDeclarationSyntax>()
                .Select(method => new TypedDocumentedMethod(method, ExtractTypedDocumentation(method, relativePath, diagnostics)))
                .Where(item => item.Documentation.HasComment)
                .ToList();
            var properties = typeDeclaration.Members
                .OfType<PropertyDeclarationSyntax>()
                .Select(property => new TypedDocumentedProperty(property, ExtractTypedDocumentation(property, relativePath, diagnostics)))
                .Where(item => item.Documentation.HasComment)
                .ToList();

            if (!typeDocumentation.HasComment && methods.Count == 0 && properties.Count == 0)
            {
                continue;
            }

            var namespacePage = GetOrCreateTypedNamespacePage(pages, GetNamespaceName(typeDeclaration));
            var qualifiedTypeName = GetQualifiedName(typeDeclaration);
            var typeAnchor = StringUtils.ToSafeId(qualifiedTypeName);
            AddTypedOutlineItem(namespacePage, GetDisplayTypeName(typeDeclaration), typeAnchor, level: 2);
            AddSymbolSourceProvenance(namespacePage.SymbolSourceProvenance, typeAnchor, relativePath, typeDeclaration);

            var methodGroups = methods
                .GroupBy(item => item.Method.Identifier.Text, StringComparer.Ordinal)
                .Select(
                    group =>
                    {
                        var groupAnchor = GetMethodGroupId(group.Key, qualifiedTypeName);
                        AddTypedOutlineItem(namespacePage, group.Key, groupAnchor, level: 3);
                        var overloads = group.Select(
                                item =>
                                {
                                    var anchor = GetMethodId(item.Method, qualifiedTypeName);
                                    AddSymbolSourceProvenance(namespacePage.SymbolSourceProvenance, anchor, relativePath, item.Method);
                                    return new CSharpMethodDocument(
                                        anchor,
                                        CreateMethodSignature(item.Method),
                                        item.Documentation.Documentation ?? new CSharpDocumentation([]));
                                })
                            .ToArray();
                        return new CSharpMethodGroupDocument(groupAnchor, group.Key, overloads);
                    })
                .ToArray();
            var typedProperties = properties
                .Select(
                    item =>
                    {
                        var anchor = GetPropertyId(item.Property, qualifiedTypeName);
                        AddTypedOutlineItem(namespacePage, item.Property.Identifier.Text, anchor, level: 3);
                        AddSymbolSourceProvenance(namespacePage.SymbolSourceProvenance, anchor, relativePath, item.Property);
                        return new CSharpPropertyDocument(
                            anchor,
                            item.Property.Identifier.Text,
                            CreatePropertySignature(item.Property),
                            item.Documentation.Documentation ?? new CSharpDocumentation([]));
                    })
                .ToArray();

            namespacePage.Types.Add(
                new CSharpTypeDocument(
                    typeAnchor,
                    GetDisplayTypeName(typeDeclaration),
                    typeDocumentation.Documentation,
                    methodGroups,
                    typedProperties));
            stubs.Add(
                new DocNode(
                    GetDisplayTypeName(typeDeclaration),
                    namespacePage.Path + "#" + typeAnchor,
                    string.Empty,
                    namespacePage.Path,
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata(GetDisplayTypeName(typeDeclaration), namespacePage.FullNamespace)));
        }

        foreach (var enumDeclaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            var documentation = ExtractTypedDocumentation(enumDeclaration, relativePath, diagnostics);
            if (!documentation.HasComment)
            {
                continue;
            }

            var namespacePage = GetOrCreateTypedNamespacePage(pages, GetNamespaceName(enumDeclaration));
            var anchor = StringUtils.ToSafeId(GetQualifiedName(enumDeclaration));
            AddTypedOutlineItem(namespacePage, enumDeclaration.Identifier.Text, anchor, level: 2);
            AddSymbolSourceProvenance(namespacePage.SymbolSourceProvenance, anchor, relativePath, enumDeclaration);
            namespacePage.Enums.Add(
                new CSharpEnumDocument(
                    anchor,
                    enumDeclaration.Identifier.Text,
                    documentation.Documentation ?? new CSharpDocumentation([])));
            stubs.Add(
                new DocNode(
                    enumDeclaration.Identifier.Text,
                    namespacePage.Path + "#" + anchor,
                    string.Empty,
                    namespacePage.Path,
                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata(enumDeclaration.Identifier.Text, namespacePage.FullNamespace)));
        }

        return new TypedCSharpFileResult(pages.Values.ToArray(), stubs);
    }

    private static void MergeTypedFileResult(
        IDictionary<string, TypedNamespacePage> namespacePages,
        ICollection<DocNode> stubNodes,
        TypedCSharpFileResult fileResult)
    {
        foreach (var sourcePage in fileResult.NamespacePages)
        {
            var targetPage = GetOrCreateTypedNamespacePage(namespacePages, sourcePage.FullNamespace);
            targetPage.Types.AddRange(sourcePage.Types);
            targetPage.Enums.AddRange(sourcePage.Enums);
            foreach (var outlineItem in sourcePage.Outline)
            {
                AddTypedOutlineItem(targetPage, outlineItem.Title, outlineItem.Id, outlineItem.Level);
            }

            targetPage.SymbolSourceProvenance.AddRange(sourcePage.SymbolSourceProvenance);
        }

        foreach (var stub in fileResult.Stubs)
        {
            stubNodes.Add(stub);
        }
    }

    private static TypedNamespacePage GetOrCreateTypedNamespacePage(
        IDictionary<string, TypedNamespacePage> namespacePages,
        string namespaceName)
    {
        var normalizedNamespace = string.IsNullOrWhiteSpace(namespaceName) ? "Global" : namespaceName.Trim();
        var path = BuildNamespaceDocPath(normalizedNamespace);
        if (!namespacePages.TryGetValue(path, out var page))
        {
            var title = GetNamespaceTitle(normalizedNamespace);
            page = new TypedNamespacePage(
                normalizedNamespace,
                path,
                title,
                DocMetadataFactory.CreateApiReferenceMetadata(title, normalizedNamespace));
            namespacePages[path] = page;
        }

        return page;
    }

    private static void EnsureTypedNamespaceHierarchy(IDictionary<string, TypedNamespacePage> namespacePages)
    {
        var pagesByNamespace = namespacePages.Values.ToDictionary(page => page.FullNamespace, StringComparer.OrdinalIgnoreCase);
        if (!pagesByNamespace.ContainsKey(string.Empty))
        {
            pagesByNamespace[string.Empty] = new TypedNamespacePage(
                string.Empty,
                "Namespaces",
                "Namespaces",
                DocMetadataFactory.CreateApiReferenceMetadata("Namespaces", string.Empty));
        }

        foreach (var namespaceName in pagesByNamespace.Keys.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray())
        {
            var parts = namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (var length = 1; length < parts.Length; length++)
            {
                var parentNamespace = string.Join(".", parts.Take(length));
                if (!pagesByNamespace.ContainsKey(parentNamespace))
                {
                    var title = GetNamespaceTitle(parentNamespace);
                    pagesByNamespace[parentNamespace] = new TypedNamespacePage(
                        parentNamespace,
                        BuildNamespaceDocPath(parentNamespace),
                        title,
                        DocMetadataFactory.CreateApiReferenceMetadata(title, parentNamespace));
                }
            }
        }

        foreach (var page in pagesByNamespace.Values)
        {
            page.ChildNamespaces.Clear();
        }

        foreach (var page in pagesByNamespace.Values.Where(page => !string.IsNullOrWhiteSpace(page.FullNamespace)))
        {
            if (pagesByNamespace.TryGetValue(GetParentNamespace(page.FullNamespace), out var parent))
            {
                parent.ChildNamespaces.Add(page.FullNamespace);
            }
        }

        namespacePages.Clear();
        foreach (var page in pagesByNamespace.Values)
        {
            namespacePages[page.Path] = page;
        }
    }

    private TypedDocumentationResult ExtractTypedDocumentation(
        SyntaxNode node,
        string relativePath,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var xml = node.GetLeadingTrivia()
            .Select(trivia => trivia.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();
        if (xml is null)
        {
            return TypedDocumentationResult.None;
        }

        try
        {
            var cleanXml = NormalizeDocumentationCommentXml(xml);
            var wrappedXml = $"<doc>{cleanXml}</doc>";
            if (!IsXmlCommentNestingDepthWithinLimit(wrappedXml))
            {
                diagnostics.Add(CreateXmlCommentDepthExceededDiagnostic(relativePath, node));
                return new TypedDocumentationResult(null, HasComment: true);
            }

            var root = XDocument.Parse(wrappedXml, LoadOptions.PreserveWhitespace).Root!;
            var excludedParameterNames = node is MethodDeclarationSyntax method
                ? GetCompilerGeneratedCallerParameterNames(method)
                : null;
            var sections = new List<CSharpDocumentationSection>();
            AddTypedDocumentationSection(sections, CSharpDocumentationSectionKind.Summary, root.Element("summary"));
            AddTypedNamedDocumentationSections(sections, CSharpDocumentationSectionKind.TypeParameter, root.Elements("typeparam"), "name");
            AddTypedNamedDocumentationSections(
                sections,
                CSharpDocumentationSectionKind.Parameter,
                root.Elements("param").Where(
                    element => !IsExcludedDocumentationParameter(
                        element.Attribute("name")?.Value,
                        excludedParameterNames)),
                "name");
            AddTypedDocumentationSection(sections, CSharpDocumentationSectionKind.Returns, root.Element("returns"));
            AddTypedNamedDocumentationSections(sections, CSharpDocumentationSectionKind.Exception, root.Elements("exception"), attributeName: null, crefAttributeName: "cref");
            AddTypedDocumentationSection(sections, CSharpDocumentationSectionKind.Remarks, root.Element("remarks"));
            AddTypedDocumentationSection(sections, CSharpDocumentationSectionKind.Example, root.Element("example"));
            return new TypedDocumentationResult(new CSharpDocumentation(sections), HasComment: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            diagnostics.Add(CreateMalformedXmlDiagnostic(relativePath, node));
            _logger.LogWarning(ex, "Failed to parse C# XML documentation in {File}.", relativePath);
            return new TypedDocumentationResult(null, HasComment: true);
        }
    }

    private static bool IsXmlCommentNestingDepthWithinLimit(string wrappedXml)
    {
        using var reader = XmlReader.Create(
            new StringReader(wrappedXml),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
        while (reader.Read())
        {
            // The synthetic doc root is at depth zero, so its direct XML documentation sections count as level one.
            if (reader.NodeType == XmlNodeType.Element && reader.Depth > MaximumXmlCommentNestingDepth)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddTypedDocumentationSection(
        ICollection<CSharpDocumentationSection> sections,
        CSharpDocumentationSectionKind kind,
        XElement? element)
    {
        if (element is null)
        {
            return;
        }

        var content = ToTypedXmlNodes(element.Nodes());
        if (content.Count > 0)
        {
            sections.Add(new CSharpDocumentationSection(kind, null, null, null, content));
        }
    }

    private static void AddTypedNamedDocumentationSections(
        ICollection<CSharpDocumentationSection> sections,
        CSharpDocumentationSectionKind kind,
        IEnumerable<XElement> elements,
        string? attributeName,
        string? crefAttributeName = null)
    {
        foreach (var element in elements)
        {
            var content = ToTypedXmlNodes(element.Nodes());
            if (content.Count == 0)
            {
                continue;
            }

            var target = crefAttributeName is null ? null : element.Attribute(crefAttributeName)?.Value?.Trim();
            sections.Add(
                new CSharpDocumentationSection(
                    kind,
                    attributeName is null ? null : element.Attribute(attributeName)?.Value?.Trim(),
                    target,
                    SimplifyCref(target),
                    content));
        }
    }

    private static IReadOnlyList<CSharpXmlNode> ToTypedXmlNodes(IEnumerable<XNode> nodes)
    {
        var result = new List<CSharpXmlNode>();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText text:
                    {
                        var value = NormalizeWhitespace(text.Value);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            result.Add(new CSharpXmlNode(CSharpXmlNodeKind.Text, value));
                        }

                        break;
                    }
                case XElement element:
                    AddTypedXmlElement(result, element);
                    break;
            }
        }

        return result;
    }

    private static void AddTypedXmlElement(ICollection<CSharpXmlNode> result, XElement element)
    {
        var name = element.Name.LocalName;
        if (name is "paramref" or "typeparamref")
        {
            var value = element.Attribute("name")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(new CSharpXmlNode(name == "paramref" ? CSharpXmlNodeKind.ParameterReference : CSharpXmlNodeKind.TypeParameterReference, value));
            }

            return;
        }

        if (name == "see")
        {
            var target = element.Attribute("cref")?.Value?.Trim() ?? element.Attribute("href")?.Value?.Trim();
            var childText = BuildXmlReaderText(ToTypedXmlNodes(element.Nodes()));
            var display = element.Attribute("langword")?.Value?.Trim()
                ?? SimplifyCref(element.Attribute("cref")?.Value);
            if (string.IsNullOrWhiteSpace(display))
            {
                // An href can supply a useful fallback, but authored element text is the reader-facing label.
                display = string.IsNullOrWhiteSpace(childText) ? element.Attribute("href")?.Value?.Trim() : childText;
            }

            if (!string.IsNullOrWhiteSpace(display))
            {
                result.Add(new CSharpXmlNode(CSharpXmlNodeKind.Cref, display, target));
            }

            return;
        }

        if (name == "c")
        {
            var text = BuildXmlReaderText(ToTypedXmlNodes(element.Nodes()));
            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(new CSharpXmlNode(CSharpXmlNodeKind.InlineCode, text));
            }

            return;
        }

        if (name == "code")
        {
            var text = element.Value.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(new CSharpXmlNode(CSharpXmlNodeKind.CodeBlock, text));
            }

            return;
        }

        if (name == "para")
        {
            var children = ToTypedXmlNodes(element.Nodes());
            if (children.Count > 0)
            {
                result.Add(new CSharpXmlNode(CSharpXmlNodeKind.Paragraph, Children: children));
            }

            return;
        }

        if (name == "list")
        {
            var items = element.Elements("item")
                .Select(item => ToTypedXmlNodes((item.Element("description") ?? item).Nodes()))
                .Where(children => children.Count > 0)
                .Select(children => new CSharpXmlNode(CSharpXmlNodeKind.ListItem, Children: children))
                .ToArray();
            if (items.Length > 0)
            {
                result.Add(
                    new CSharpXmlNode(
                        CSharpXmlNodeKind.List,
                        Children: items,
                        Ordered: string.Equals(element.Attribute("type")?.Value, "number", StringComparison.OrdinalIgnoreCase)));
            }

            return;
        }

        foreach (var child in ToTypedXmlNodes(element.Nodes()))
        {
            result.Add(child);
        }
    }

    private static CSharpSignature CreateMethodSignature(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters
            .Where(parameter => !IsCompilerGeneratedCallerParameter(parameter))
            .Select(
                parameter => new CSharpSignatureParameter(
                    parameter.Modifiers.ToString().Trim() is { Length: > 0 } modifier ? modifier : null,
                    parameter.Type?.ToString() ?? "object",
                    parameter.Identifier.Text,
                    parameter.Default?.Value.ToString()))
            .ToArray();
        return new CSharpSignature(
            method.ReturnType.ToString(),
            method.Identifier.Text,
            parameters,
            method.TypeParameterList?.Parameters.Select(parameter => parameter.Identifier.Text).ToArray() ?? [],
            method.ExplicitInterfaceSpecifier?.ToString().Trim());
    }

    private static HashSet<string> GetCompilerGeneratedCallerParameterNames(MethodDeclarationSyntax method)
    {
        return method.ParameterList.Parameters
            .Where(IsCompilerGeneratedCallerParameter)
            .Select(parameter => parameter.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsExcludedDocumentationParameter(string? parameterName, ISet<string>? excludedParameterNames)
    {
        return !string.IsNullOrWhiteSpace(parameterName)
            && excludedParameterNames?.Contains(parameterName.Trim()) == true;
    }

    private static CSharpSignature CreatePropertySignature(PropertyDeclarationSyntax property)
    {
        return new CSharpSignature(
            property.Type.ToString(),
            property.Identifier.Text,
            [],
            [],
            AccessorSignature: GetPropertyAccessorSignature(property));
    }

    private static void AddTypedOutlineItem(TypedNamespacePage namespacePage, string title, string id, int level)
    {
        if (string.IsNullOrWhiteSpace(title)
            || string.IsNullOrWhiteSpace(id)
            || !namespacePage.OutlineIds.Add(id.Trim()))
        {
            return;
        }

        namespacePage.Outline.Add(new DocOutlineItem { Title = title.Trim(), Id = id.Trim(), Level = level });
    }

    private static void AddSymbolSourceProvenance(
        ICollection<DocSymbolSourceProvenance> provenance,
        string anchorId,
        string relativePath,
        CSharpSyntaxNode node)
    {
        var lineSpan = node.SyntaxTree.GetLineSpan(node.Span);
        provenance.Add(
            new DocSymbolSourceProvenance
            {
                AnchorId = anchorId,
                SourcePath = relativePath,
                StartLine = lineSpan.StartLinePosition.Line + 1
            });
    }

    private static DocHarvestDiagnostic CreateCSharpParseDiagnostic(string relativePath, Diagnostic? diagnostic)
    {
        var sourceLocation = diagnostic is { Location.IsInSource: true } ? diagnostic.Location : null;
        var location = sourceLocation?.GetLineSpan().StartLinePosition ?? default;
        var locationText = sourceLocation is not null
            ? $" at line {location.Line + 1}, column {location.Character + 1}"
            : string.Empty;
        return new DocHarvestDiagnostic(
            DocHarvestDiagnosticCodes.CSharpParseFailed,
            DocHarvestDiagnosticSeverity.Error,
            HarvesterType,
            $"C# source '{relativePath}' could not be harvested{locationText}.",
            "The source contains syntax errors or the C# documentation projection could not complete, so AppSurface Docs omitted this file atomically.",
            "Fix the C# syntax or documentation shape at the reported source location, then refresh the Docs harvest.");
    }

    private static DocHarvestDiagnostic CreateMalformedXmlDiagnostic(string relativePath, SyntaxNode node)
    {
        var location = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition;
        return new DocHarvestDiagnostic(
            DocHarvestDiagnosticCodes.CSharpXmlCommentMalformed,
            DocHarvestDiagnosticSeverity.Warning,
            HarvesterType,
            $"C# XML documentation in '{relativePath}' at line {location.Line + 1}, column {location.Character + 1} is malformed.",
            "The documentation comment could not be parsed safely, so its documentation fields were omitted while the declaration anchor remains available.",
            "Repair the XML documentation comment and refresh the Docs harvest.");
    }

    private static DocHarvestDiagnostic CreateXmlCommentDepthExceededDiagnostic(string relativePath, SyntaxNode node)
    {
        var location = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition;
        return new DocHarvestDiagnostic(
            DocHarvestDiagnosticCodes.CSharpXmlCommentDepthExceeded,
            DocHarvestDiagnosticSeverity.Warning,
            HarvesterType,
            $"C# XML documentation in '{relativePath}' at line {location.Line + 1}, column {location.Character + 1} exceeds the supported nesting depth.",
            $"The documentation comment contains more than {MaximumXmlCommentNestingDepth} XML element levels, so its documentation fields were omitted to preserve harvest availability.",
            $"Reduce the XML documentation nesting to no more than {MaximumXmlCommentNestingDepth} element levels and refresh the Docs harvest.");
    }

    private void LogDiagnostics(IEnumerable<DocHarvestDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            _logger.Log(
                diagnostic.Severity >= DocHarvestDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning,
                "AppSurface Docs C# harvest diagnostic {DiagnosticCode}: {Problem}",
                diagnostic.Code,
                diagnostic.Problem);
        }
    }

    private sealed record TypedCSharpFileResult(
        IReadOnlyList<TypedNamespacePage> NamespacePages,
        IReadOnlyList<DocNode> Stubs);

    private sealed record TypedDocumentedMethod(MethodDeclarationSyntax Method, TypedDocumentationResult Documentation);

    private sealed record TypedDocumentedProperty(PropertyDeclarationSyntax Property, TypedDocumentationResult Documentation);

    private sealed record TypedDocumentationResult(CSharpDocumentation? Documentation, bool HasComment)
    {
        internal static TypedDocumentationResult None { get; } = new(null, false);
    }

    private sealed class TypedNamespacePage
    {
        internal TypedNamespacePage(string fullNamespace, string path, string title, DocMetadata metadata)
        {
            FullNamespace = fullNamespace;
            Path = path;
            Title = title;
            Metadata = metadata;
        }

        internal string FullNamespace { get; }

        internal string Path { get; }

        internal string Title { get; }

        internal DocMetadata Metadata { get; }

        internal List<CSharpTypeDocument> Types { get; } = [];

        internal List<CSharpEnumDocument> Enums { get; } = [];

        internal List<DocOutlineItem> Outline { get; } = [];

        internal HashSet<string> OutlineIds { get; } = new(StringComparer.Ordinal);

        internal List<DocSymbolSourceProvenance> SymbolSourceProvenance { get; } = [];

        internal HashSet<string> ChildNamespaces { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal CSharpNamespaceDocument ToDocument()
        {
            var children = ChildNamespaces
                .OrderBy(namespaceName => namespaceName, StringComparer.OrdinalIgnoreCase)
                .Select(
                    namespaceName => new CSharpChildNamespace(
                        DocRoutePath.BuildCanonicalPath(BuildNamespaceDocPath(namespaceName)),
                        GetNamespaceTitle(namespaceName)))
                .ToArray();
            var types = MergeTypedTypes(Types);
            var enums = Enums
                .GroupBy(@enum => @enum.AnchorId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            return new CSharpNamespaceDocument(
                FullNamespace,
                Title,
                children,
                types,
                enums,
                Outline.ToArray(),
                SymbolSourceProvenance.ToArray(),
                BuildNamespaceReaderText(FullNamespace, Title, children, types, enums));
        }
    }

    private static IReadOnlyList<CSharpTypeDocument> MergeTypedTypes(IEnumerable<CSharpTypeDocument> types)
    {
        return types
            .GroupBy(type => type.AnchorId, StringComparer.Ordinal)
            .Select(
                group =>
                {
                    var first = group.First();
                    if (group.Count() == 1)
                    {
                        return first;
                    }

                    return first with
                    {
                        Documentation = MergeTypedDocumentation(group.Select(type => type.Documentation)),
                        MethodGroups = group
                            .SelectMany(type => type.MethodGroups)
                            .GroupBy(methodGroup => methodGroup.AnchorId, StringComparer.Ordinal)
                            .Select(
                                methodGroup => methodGroup.First() with
                                {
                                    Overloads = methodGroup
                                        .SelectMany(item => item.Overloads)
                                        .GroupBy(overload => overload.AnchorId, StringComparer.Ordinal)
                                        .Select(overload => overload.First())
                                        .ToArray()
                                })
                            .ToArray(),
                        Properties = group
                            .SelectMany(type => type.Properties)
                            .GroupBy(property => property.AnchorId, StringComparer.Ordinal)
                            .Select(property => property.First())
                            .ToArray()
                    };
                })
            .ToArray();
    }

    private static CSharpDocumentation? MergeTypedDocumentation(IEnumerable<CSharpDocumentation?> documentation)
    {
        var sections = documentation
            .Where(item => item is not null)
            .SelectMany(item => item!.Sections)
            .ToArray();
        return sections.Length == 0 ? null : new CSharpDocumentation(sections);
    }

    private static string BuildNamespaceReaderText(
        string fullNamespace,
        string title,
        IReadOnlyList<CSharpChildNamespace> children,
        IReadOnlyList<CSharpTypeDocument> types,
        IReadOnlyList<CSharpEnumDocument> enums)
    {
        var sections = new List<string> { title, fullNamespace };
        sections.AddRange(children.Select(child => child.Title));
        foreach (var type in types)
        {
            var typeParts = new List<string> { type.DisplayName };
            AddDocumentationReaderText(typeParts, type.Documentation);
            foreach (var methodGroup in type.MethodGroups)
            {
                var methodGroupParts = new List<string> { methodGroup.Name };
                foreach (var overload in methodGroup.Overloads)
                {
                    var overloadParts = new List<string>();
                    AddSignatureReaderText(overloadParts, overload.Signature);
                    AddDocumentationReaderText(overloadParts, overload.Documentation);
                    methodGroupParts.Add(string.Join(' ', overloadParts.Where(part => !string.IsNullOrWhiteSpace(part))));
                }

                typeParts.Add(string.Join("\n", methodGroupParts.Where(part => !string.IsNullOrWhiteSpace(part))));
            }

            foreach (var property in type.Properties)
            {
                var propertyParts = new List<string> { property.Name };
                AddSignatureReaderText(propertyParts, property.Signature);
                AddDocumentationReaderText(propertyParts, property.Documentation);
                typeParts.Add(string.Join(' ', propertyParts.Where(part => !string.IsNullOrWhiteSpace(part))));
            }

            sections.Add(string.Join("\n", typeParts.Where(part => !string.IsNullOrWhiteSpace(part))));
        }

        foreach (var @enum in enums)
        {
            var enumParts = new List<string> { @enum.DisplayName };
            AddDocumentationReaderText(enumParts, @enum.Documentation);
            sections.Add(string.Join(' ', enumParts.Where(part => !string.IsNullOrWhiteSpace(part))));
        }

        // Normalize each semantic section as it is appended. Joining the whole namespace and splitting it again made
        // a second, full-page string and array for every generated API page.
        var readerText = new StringBuilder();
        foreach (var section in sections)
        {
            foreach (var line in section.Split('\n'))
            {
                var normalizedLine = NormalizeWhitespace(line);
                if (!string.IsNullOrWhiteSpace(normalizedLine))
                {
                    readerText.AppendLine(normalizedLine);
                }
            }
        }

        return readerText.ToString().Trim();
    }

    private static void AddSignatureReaderText(ICollection<string> parts, CSharpSignature signature)
    {
        parts.Add(signature.Type);

        // Keep the search projection aligned with the mutually exclusive signature shapes rendered by _Signature.cshtml.
        // Properties expose accessors rather than a callable member shape, while explicit-interface methods display the
        // qualification immediately before the member name.
        if (!string.IsNullOrWhiteSpace(signature.AccessorSignature))
        {
            parts.Add(signature.Name);
            parts.Add(signature.AccessorSignature);
            return;
        }

        parts.Add(signature.ExplicitInterface ?? string.Empty);
        parts.Add(signature.Name);
        foreach (var typeParameter in signature.TypeParameters)
        {
            parts.Add(typeParameter);
        }
        foreach (var parameter in signature.Parameters)
        {
            parts.Add(parameter.Modifier ?? string.Empty);
            parts.Add(parameter.Type);
            parts.Add(parameter.Name);
            parts.Add(parameter.DefaultValue ?? string.Empty);
        }
    }

    private static void AddDocumentationReaderText(ICollection<string> parts, CSharpDocumentation? documentation)
    {
        if (documentation is null)
        {
            return;
        }

        foreach (var section in documentation.Sections)
        {
            parts.Add(section.Name ?? string.Empty);
            parts.Add(section.CrefDisplay ?? string.Empty);
            parts.Add(BuildXmlReaderText(section.Content));
        }
    }

    private static string BuildXmlReaderText(IEnumerable<CSharpXmlNode> nodes)
    {
        var parts = new List<string>();
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.Text))
            {
                parts.Add(node.Text);
            }

            if (node.Children is { Count: > 0 })
            {
                parts.Add(BuildXmlReaderText(node.Children));
            }
        }

        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private async Task<IReadOnlyList<DocNode>> HarvestAsync(
        string rootPath,
        IHarvestPathPolicy pathPolicy,
        AppSurfaceDocsHarvestProgressSession? progress,
        CancellationToken cancellationToken)
    {
        var nodes = new List<DocNode>();
        var stubNodes = new List<DocNode>();
        var namespacePages = new Dictionary<string, NamespaceDocPage>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<DocHarvestDiagnostic>();
        try
        {
            if (progress is not null)
            {
                await progress.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Discovering);
            }

            var csharpOptions = _options.Harvest?.CSharp ?? new AppSurfaceDocsCSharpHarvestOptions();
            foreach (var file in EnumerateEligibleCSharpFiles(rootPath, pathPolicy, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(rootPath, file)
                    .Replace('\\', '/'); // Normalize to forward slashes for URLs
                if (!pathPolicy.ShouldIncludeFilePath(relativePath, AppSurfaceDocsHarvestSourceKind.CSharp))
                {
                    continue;
                }

                try
                {
                    if (progress is not null)
                    {
                        await progress.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Parsing);
                        await progress.ReportSourceUnitAsync(0);
                    }

                    var readResult = await AppSurfaceDocsParserInputBudget.ReadUtf8SourceAsync(
                        file,
                        relativePath,
                        csharpOptions.MaxFileSizeBytes,
                        MaxFileSizeConfigurationKey,
                        DocHarvestDiagnosticCodes.CSharpFileTooLarge,
                        HarvesterType,
                        "C#",
                        "Exclude generated C# with AppSurfaceDocs:Harvest:CSharp:ExcludeGlobs, or raise AppSurfaceDocs:Harvest:CSharp:MaxFileSizeBytes only for authored source that should be parsed.",
                        cancellationToken);
                    if (!readResult.Included)
                    {
                        diagnostics.Add(readResult.Diagnostic!);
                        continue;
                    }

                    var tree = CSharpSyntaxTree.ParseText(readResult.Source!, cancellationToken: cancellationToken);
                    var root = await tree.GetRootAsync(cancellationToken);

                    // Capture Classes, Structs, Interfaces, Records
                    var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
                    foreach (var typeDecl in typeDeclarations)
                    {
                        var doc = ExtractDoc(typeDecl, relativePath, diagnostics);
                        var documentedMethods = typeDecl.Members
                            .OfType<MethodDeclarationSyntax>()
                            .Select(method => new
                            {
                                Method = method,
                                Doc = ExtractDoc(method, relativePath, diagnostics)
                            })
                            .Where(x => x.Doc != null)
                            .ToList();
                        var documentedProperties = typeDecl.Members
                            .OfType<PropertyDeclarationSyntax>()
                            .Select(property => new
                            {
                                Property = property,
                                Doc = ExtractDoc(property, relativePath, diagnostics)
                            })
                            .Where(x => x.Doc != null)
                            .ToList();

                        if (doc == null && documentedMethods.Count == 0 && documentedProperties.Count == 0)
                        {
                            continue;
                        }

                        var qualifiedTypeName = GetQualifiedName(typeDecl);
                        var typeDisplayName = GetDisplayTypeName(typeDecl);
                        var typeId = StringUtils.ToSafeId(qualifiedTypeName);
                        var namespacePage = GetOrCreateNamespacePage(namespacePages, GetNamespaceName(typeDecl));
                        AddOutlineItem(namespacePage, typeDisplayName, typeId, level: 2);

                        namespacePage.Content.Append(
                            $@"<section id=""{typeId}"" class=""doc-type"">
                        <header class=""doc-type-header"">
                            <span class=""doc-kind"">Type</span>
                            <h2>{WebUtility.HtmlEncode(typeDisplayName)}</h2>
                            {CreateSymbolSourcePlaceholder(typeId)}
                        </header>");

                        AddSymbolSourceProvenance(namespacePage, typeId, relativePath, typeDecl);

                        if (!string.IsNullOrWhiteSpace(doc))
                        {
                            namespacePage.Content.Append(
                                $@"<div class=""doc-body"">
                            {doc}
                        </div>");
                        }

                        stubNodes.Add(
                            new DocNode(
                                typeDisplayName,
                                namespacePage.Path + "#" + typeId,
                                string.Empty,
                                namespacePage.Path,
                                Metadata: DocMetadataFactory.CreateApiReferenceMetadata(typeDisplayName, namespacePage.FullNamespace)));

                        foreach (var methodGroup in documentedMethods.GroupBy(x => x.Method.Identifier.Text))
                        {
                            var overloadCount = methodGroup.Count();
                            var methodGroupId = GetMethodGroupId(methodGroup.Key, qualifiedTypeName);
                            AddOutlineItem(namespacePage, methodGroup.Key, methodGroupId, level: 3);

                            namespacePage.Content.Append(
                                $@"<section id=""{methodGroupId}"" class=""doc-method-group"">
                            <header class=""doc-method-group-header"">
                                <span class=""doc-kind"">Method</span>
                                <h3>{WebUtility.HtmlEncode(methodGroup.Key)}</h3>");

                            if (overloadCount > 1)
                            {
                                namespacePage.Content.Append(
                                    $@"<span class=""doc-overload-count"">{WebUtility.HtmlEncode($"{overloadCount} overloads")}</span>");
                            }

                            namespacePage.Content.Append("</header>");

                            var index = 0;
                            foreach (var methodItem in methodGroup)
                            {
                                var method = methodItem.Method;
                                var methodDoc = methodItem.Doc!;
                                var id = GetMethodId(method, qualifiedTypeName);
                                var highlightedDisplaySignature = GetHighlightedDisplaySignature(method);
                                var openAttribute = index == 0 ? " open" : string.Empty;

                                namespacePage.Content.Append(
                                    $@"<details id=""{id}"" class=""doc-overload""{openAttribute}>
                                <summary>
                                    <code class=""doc-signature"">{highlightedDisplaySignature}</code>
                                    {CreateSymbolSourcePlaceholder(id)}
                                </summary>
                                <div class=""doc-overload-body"">
                                    {methodDoc}
                                </div>
                            </details>");

                                AddSymbolSourceProvenance(namespacePage, id, relativePath, method);

                                index++;
                            }

                            namespacePage.Content.Append("</section>");
                        }

                        foreach (var propertyItem in documentedProperties)
                        {
                            var property = propertyItem.Property;
                            var propertyDoc = propertyItem.Doc!;
                            var id = GetPropertyId(property, qualifiedTypeName);
                            var highlightedPropertySignature = GetHighlightedPropertySignature(property);
                            AddOutlineItem(namespacePage, property.Identifier.Text, id, level: 3);

                            namespacePage.Content.Append(
                                $@"<section id=""{id}"" class=""doc-method-group"">
                            <header class=""doc-method-group-header"">
                                <span class=""doc-kind"">Property</span>
                                <h3>{WebUtility.HtmlEncode(property.Identifier.Text)}</h3>
                            </header>
                            <article class=""doc-overload doc-property"">
                                <div class=""doc-property-signature"">
                                    <code class=""doc-signature"">{highlightedPropertySignature}</code>
                                    {CreateSymbolSourcePlaceholder(id)}
                                </div>
                                <div class=""doc-overload-body"">
                                    {propertyDoc}
                                </div>
                            </article>
                        </section>");

                            AddSymbolSourceProvenance(namespacePage, id, relativePath, property);
                        }

                        namespacePage.Content.Append("</section>");
                    }

                    // Capture Enums
                    var enumDeclarations = root.DescendantNodes().OfType<EnumDeclarationSyntax>().ToList();
                    foreach (var enumDecl in enumDeclarations)
                    {
                        var doc = ExtractDoc(enumDecl, relativePath, diagnostics);
                        if (doc != null)
                        {
                            var namespacePage = GetOrCreateNamespacePage(namespacePages, GetNamespaceName(enumDecl));
                            var qualifiedName = GetQualifiedName(enumDecl);
                            var enumId = StringUtils.ToSafeId(qualifiedName);
                            AddOutlineItem(namespacePage, enumDecl.Identifier.Text, enumId, level: 2);

                            namespacePage.Content.Append(
                                $@"<section id=""{enumId}"" class=""doc-type doc-enum"">
                            <header class=""doc-type-header"">
                                <span class=""doc-kind"">Enum</span>
                                <h2>{WebUtility.HtmlEncode(enumDecl.Identifier.Text)}</h2>
                                {CreateSymbolSourcePlaceholder(enumId)}
                            </header>
                            <div class=""doc-body"">
                                {doc}
                            </div>
                        </section>");

                            AddSymbolSourceProvenance(namespacePage, enumId, relativePath, enumDecl);

                            stubNodes.Add(
                                new DocNode(
                                    enumDecl.Identifier.Text,
                                    namespacePage.Path + "#" + enumId,
                                    string.Empty,
                                    namespacePage.Path,
                                    Metadata: DocMetadataFactory.CreateApiReferenceMetadata(enumDecl.Identifier.Text, namespacePage.FullNamespace)));
                        }
                    }

                }
                catch (Exception ex) when (ex is not OperationCanceledException
                                           and not OutOfMemoryException
                                           and not StackOverflowException
                                           and not AccessViolationException
                                           and not AppDomainUnloadedException
                                           and not BadImageFormatException
                                           and not CannotUnloadAppDomainException
                                           and not ThreadAbortException)
                {
                    _logger.LogError(ex, "Failed to parse C# file: {File}", file);
                }
            }

            if (progress is not null)
            {
                await progress.TransitionAsync(AppSurfaceDocsHarvestProgressPhase.Finalizing);
            }

            EnsureNamespaceHierarchy(namespacePages);

            foreach (var namespacePage in namespacePages.Values.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (namespacePage.Content.Length == 0)
                {
                    continue;
                }

                nodes.Add(
                    new DocNode(
                        namespacePage.Title,
                        namespacePage.Path,
                        namespacePage.Content.ToString(),
                        Metadata: namespacePage.Metadata,
                        Outline: namespacePage.Outline,
                        SymbolSourceProvenance: namespacePage.SymbolSourceProvenance));
                if (progress is not null)
                {
                    await progress.ReportOutputOnlyAsync(1);
                }
            }

            nodes.AddRange(stubNodes);
            if (progress is not null && stubNodes.Count > 0)
            {
                await progress.ReportOutputOnlyAsync(stubNodes.Count);
            }

            return nodes;
        }
        finally
        {
            _lastDiagnostics = diagnostics.ToArray();
            LogDiagnostics(diagnostics);
        }
    }

    IReadOnlyList<DocHarvestDiagnostic> IDocHarvesterDiagnosticProvider.GetHarvestDiagnostics()
    {
        return GetType() == typeof(CSharpDocHarvester) ? _lastDiagnostics : [];
    }

    private IEnumerable<string> EnumerateEligibleCSharpFiles(
        string rootPath,
        IHarvestPathPolicy pathPolicy,
        CancellationToken cancellationToken)
    {
        foreach (var file in pathPolicy.EnumerateCandidateFiles(
                     rootPath,
                     AppSurfaceDocsHarvestSourceKind.CSharp,
                     "*.cs",
                     cancellationToken))
        {
            yield return file;
        }
    }

    /// <summary>
    /// Computes the safe ID for a method to be used in HTML content and stub nodes.
    /// </summary>
    /// <param name="method">The method declaration syntax.</param>
    /// <param name="qualifiedTypeName">The qualified name of the containing type.</param>
    /// <returns>The safe ID string for the method documentation section.</returns>
    internal static string GetMethodId(
        MethodDeclarationSyntax method,
        string qualifiedTypeName)
    {
        var paramList = string.Join(
            ", ",
            method.ParameterList.Parameters.Select(p =>
                $"{p.Modifiers.ToString().Trim()} {p.Type?.ToString() ?? "object"}".Trim()));

        var typeParams = method.TypeParameterList?.ToString().Trim() ?? "";
        var explicitInterface = method.ExplicitInterfaceSpecifier?.ToString().Trim() ?? "";
        var methodName = explicitInterface + method.Identifier.Text + typeParams;
        var signature = $"{methodName}({paramList})";

        var id = StringUtils.ToSafeId($"{qualifiedTypeName}.{signature}");

        return id;
    }

    /// <summary>
    /// Computes the safe ID for a property to be used in HTML content and stub nodes.
    /// </summary>
    /// <param name="property">The property declaration syntax.</param>
    /// <param name="qualifiedTypeName">The qualified name of the containing type.</param>
    /// <returns>The safe ID string for the property documentation section.</returns>
    private static string GetPropertyId(
        PropertyDeclarationSyntax property,
        string qualifiedTypeName)
    {
        var signature = $"{property.Type} {property.Identifier.Text}{GetPropertyAccessorSignature(property)}";
        var id = StringUtils.ToSafeId($"{qualifiedTypeName}.{signature}");

        return id;
    }

    private static string GetMethodGroupId(string methodName, string qualifiedTypeName)
    {
        // Reserve a distinct suffix so the group anchor never collides with a parameterless overload anchor.
        return StringUtils.ToSafeId($"{qualifiedTypeName}.{methodName}.method-group");
    }

    private static string CreateSymbolSourcePlaceholder(string anchorId)
    {
        return $@"<span data-appsurfacedocs-symbol-source=""{WebUtility.HtmlEncode(anchorId)}""></span>";
    }

    private static void AddSymbolSourceProvenance(
        NamespaceDocPage namespacePage,
        string anchorId,
        string relativePath,
        CSharpSyntaxNode syntaxNode)
    {
        if (string.IsNullOrWhiteSpace(anchorId) || string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var lineSpan = syntaxNode.SyntaxTree.GetLineSpan(syntaxNode.Span);
        namespacePage.SymbolSourceProvenance.Add(
            new DocSymbolSourceProvenance
            {
                AnchorId = anchorId,
                SourcePath = relativePath,
                StartLine = lineSpan.StartLinePosition.Line + 1
            });
    }

    /// <summary>
    /// Generates a syntax-highlighted HTML string representing a method signature for display.
    /// </summary>
    /// <param name="method">The method declaration syntax.</param>
    /// <returns>An HTML fragment containing the highlighted signature.</returns>
    private static string GetHighlightedDisplaySignature(MethodDeclarationSyntax method)
    {
        var builder = new StringBuilder();
        var explicitInterface = method.ExplicitInterfaceSpecifier?.ToString().Trim();

        builder.Append($@"<span class=""sig-return"">{WebUtility.HtmlEncode(method.ReturnType.ToString())}</span> ");
        if (!string.IsNullOrEmpty(explicitInterface))
        {
            builder.Append($@"<span class=""sig-type"">{WebUtility.HtmlEncode(explicitInterface)}</span>");
        }

        builder.Append($@"<span class=""sig-method"">{WebUtility.HtmlEncode(method.Identifier.Text)}</span>");

        if (method.TypeParameterList is { Parameters.Count: > 0 } typeParams)
        {
            var typeParamDisplay = string.Join(", ", typeParams.Parameters.Select(p => p.Identifier.Text));
            builder.Append($@"<span class=""sig-generic"">&lt;{WebUtility.HtmlEncode(typeParamDisplay)}&gt;</span>");
        }

        builder.Append("(");
        var parameters = method.ParameterList.Parameters
            .Where(p => !IsCompilerGeneratedCallerParameter(p))
            .ToList();

        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            AppendHighlightedParameter(builder, parameters[i]);
        }

        builder.Append(")");

        return builder.ToString();
    }

    /// <summary>
    /// Generates a syntax-highlighted HTML string representing a property signature for display.
    /// </summary>
    /// <param name="property">The property declaration syntax.</param>
    /// <returns>An HTML fragment containing the highlighted signature.</returns>
    private static string GetHighlightedPropertySignature(PropertyDeclarationSyntax property)
    {
        var builder = new StringBuilder();
        builder.Append($@"<span class=""sig-type"">{WebUtility.HtmlEncode(property.Type.ToString())}</span> ");
        builder.Append($@"<span class=""sig-parameter"">{WebUtility.HtmlEncode(property.Identifier.Text)}</span>");

        var accessorSignature = GetPropertyAccessorSignature(property);
        if (!string.IsNullOrWhiteSpace(accessorSignature))
        {
            builder.Append(" ");
            builder.Append($@"<span class=""sig-operator"">{WebUtility.HtmlEncode(accessorSignature)}</span>");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Computes the accessors (get/set/init) for a property as a string for inclusion in signatures.
    /// </summary>
    /// <param name="property">The property declaration syntax.</param>
    /// <returns>A string like "{ get; set; }" or "{ get; }".</returns>
    internal static string GetPropertyAccessorSignature(PropertyDeclarationSyntax property)
    {
        if (property.ExpressionBody != null)
        {
            return "{ get; }";
        }

        var accessors = property.AccessorList?.Accessors
            .Select(a => $"{a.Keyword.Text};")
            .ToList();

        if (accessors == null || accessors.Count == 0)
        {
            return string.Empty;
        }

        return "{ " + string.Join(" ", accessors) + " }";
    }

    /// <summary>
    /// Appends a syntax-highlighted parameter declaration to the provided StringBuilder.
    /// </summary>
    /// <param name="builder">The StringBuilder to append to.</param>
    /// <param name="parameter">The parameter declaration syntax.</param>
    internal static void AppendHighlightedParameter(StringBuilder builder, ParameterSyntax parameter)
    {
        var modifier = parameter.Modifiers.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(modifier))
        {
            builder.Append($@"<span class=""sig-modifier"">{WebUtility.HtmlEncode(modifier)}</span> ");
        }

        var type = parameter.Type?.ToString() ?? "object";
        builder.Append($@"<span class=""sig-type"">{WebUtility.HtmlEncode(type)}</span> ");
        builder.Append($@"<span class=""sig-parameter"">{WebUtility.HtmlEncode(parameter.Identifier.Text)}</span>");

        var defaultValue = parameter.Default?.Value.ToString();
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            builder.Append(@" <span class=""sig-operator"">=</span> ");
            builder.Append($@"<span class=""sig-literal"">{WebUtility.HtmlEncode(defaultValue)}</span>");
        }
    }

    /// <summary>
    /// Gets the display name for a type declaration, including generic type parameter placeholders (e.g., &lt;T&gt;).
    /// </summary>
    /// <param name="typeDecl">The type declaration syntax.</param>
    /// <returns>The display name string.</returns>
    private static string GetDisplayTypeName(TypeDeclarationSyntax typeDecl)
    {
        var typeParams = typeDecl.TypeParameterList?.Parameters;
        if (typeParams == null || typeParams.Value.Count == 0)
        {
            return typeDecl.Identifier.Text;
        }

        var names = string.Join(", ", typeParams.Value.Select(p => p.Identifier.Text));

        return $"{typeDecl.Identifier.Text}<{names}>";
    }

    /// <summary>
    /// Gets the type name for a qualified ID, appending backtick arity for generic types (e.g., MyType`1).
    /// </summary>
    /// <param name="typeDecl">The type declaration syntax.</param>
    /// <returns>The type name string used in safe IDs.</returns>
    private static string GetTypeNameForQualifiedId(TypeDeclarationSyntax typeDecl)
    {
        var arity = typeDecl.TypeParameterList?.Parameters.Count ?? 0;

        return arity > 0 ? $"{typeDecl.Identifier.Text}`{arity}" : typeDecl.Identifier.Text;
    }

    /// <summary>
    /// Determines whether a parameter is a compiler-generated caller information parameter (for example, [CallerFilePath]).
    /// </summary>
    /// <param name="parameter">The parameter declaration syntax.</param>
    /// <returns><c>true</c> if the parameter should be hidden from documentation; otherwise, <c>false</c>.</returns>
    private static bool IsCompilerGeneratedCallerParameter(ParameterSyntax parameter)
    {
        return parameter.AttributeLists
            .SelectMany(list => list.Attributes)
            .Select(attribute => attribute.Name.ToString())
            .Any(name =>
                name.EndsWith("CallerFilePath", StringComparison.Ordinal)
                || name.EndsWith("CallerFilePathAttribute", StringComparison.Ordinal)
                || name.EndsWith("CallerLineNumber", StringComparison.Ordinal)
                || name.EndsWith("CallerLineNumberAttribute", StringComparison.Ordinal)
                || name.EndsWith("CallerMemberName", StringComparison.Ordinal)
                || name.EndsWith("CallerMemberNameAttribute", StringComparison.Ordinal));
    }

    /// <summary>
    /// Extracts XML documentation from the leading trivia of a syntax node and converts it into HTML fragments.
    /// </summary>
    /// <param name="node">The syntax node whose leading XML documentation comments will be parsed.</param>
    /// <param name="relativePath">The repository-relative path used by structured documentation diagnostics.</param>
    /// <param name="diagnostics">The harvest diagnostics that receive local XML depth-limit warnings.</param>
    /// <returns>The HTML string containing structured documentation sections, or <c>null</c> if no documentation is present or parsing fails.</returns>
    private string? ExtractDoc(
        SyntaxNode node,
        string relativePath,
        ICollection<DocHarvestDiagnostic> diagnostics)
    {
        var xml = node.GetLeadingTrivia()
            .Select(i => i.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();

        if (xml == null) return null;

        try
        {
            var cleanXml = NormalizeDocumentationCommentXml(xml);
            var wrappedXml = $"<doc>{cleanXml}</doc>";
            if (!IsXmlCommentNestingDepthWithinLimit(wrappedXml))
            {
                diagnostics.Add(CreateXmlCommentDepthExceededDiagnostic(relativePath, node));
                return null;
            }

            var xdoc = XDocument.Parse(wrappedXml, LoadOptions.PreserveWhitespace);
            var root = xdoc.Root!;
            var excludedParameterNames = node is MethodDeclarationSyntax method
                ? GetCompilerGeneratedCallerParameterNames(method)
                : null;

            var html = new StringBuilder();

            AppendTextSection(html, "doc-summary", root.Element("summary"));
            AppendNamedListSection(
                html,
                "doc-typeparams",
                "Type Parameters",
                root.Elements("typeparam"),
                e => e.Attribute("name")?.Value);
            AppendNamedListSection(
                html,
                "doc-params",
                "Parameters",
                root.Elements("param").Where(
                    element => !IsExcludedDocumentationParameter(
                        element.Attribute("name")?.Value,
                        excludedParameterNames)),
                e => e.Attribute("name")?.Value);
            AppendTextSection(html, "doc-returns", root.Element("returns"), "Returns");
            AppendNamedListSection(
                html,
                "doc-exceptions",
                "Exceptions",
                root.Elements("exception"),
                e => SimplifyCref(e.Attribute("cref")?.Value));
            AppendTextSection(html, "doc-remarks", root.Element("remarks"), "Remarks");
            AppendTextSection(html, "doc-example", root.Element("example"), "Example");

            var output = html.ToString();

            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse XML documentation for node {Node}", node.ToString().Split('\n')[0]);

            return null;
        }
    }

    /// <summary>
    /// Appends a simple text section (like summary or remarks) to the HTML builder.
    /// </summary>
    /// <param name="html">The StringBuilder to append to.</param>
    /// <param name="cssClass">The CSS class name for the section container.</param>
    /// <param name="section">The XElement containing the documentation section.</param>
    /// <param name="heading">Optional heading text for the section.</param>
    private static void AppendTextSection(
        StringBuilder html,
        string cssClass,
        XElement? section,
        string? heading = null)
    {
        if (section == null)
        {
            return;
        }

        var body = RenderBlockContent(section);
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        var encodedCssClass = WebUtility.HtmlEncode(cssClass);
        html.Append($"<div class=\"{encodedCssClass}\">");
        if (!string.IsNullOrWhiteSpace(heading))
        {
            html.Append($"<h4>{WebUtility.HtmlEncode(heading)}</h4>");
        }

        html.Append(body);
        html.Append("</div>");
    }

    /// <summary>
    /// Appends a list of named entries (like parameters or exceptions) to the HTML builder.
    /// </summary>
    /// <param name="html">The StringBuilder to append to.</param>
    /// <param name="cssClass">The CSS class name for the section container.</param>
    /// <param name="heading">The heading text for the section.</param>
    /// <param name="entries">The collection of XElements to process.</param>
    /// <param name="keySelector">A function that extracts the name or key for each entry.</param>
    private static void AppendNamedListSection(
        StringBuilder html,
        string cssClass,
        string heading,
        IEnumerable<XElement> entries,
        Func<XElement, string?> keySelector)
    {
        var rows = entries
            .Select(entry => new
            {
                Key = keySelector(entry)?.Trim(),
                Description = RenderInlineContent(entry)
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.Description))
            .ToList();

        if (rows.Count == 0)
        {
            return;
        }

        var encodedCssClass = WebUtility.HtmlEncode(cssClass);
        html.Append($"<div class=\"{encodedCssClass}\">");
        html.Append($"<h4>{WebUtility.HtmlEncode(heading)}</h4>");
        html.Append("<ul>");
        foreach (var row in rows)
        {
            html.Append("<li>");
            if (!string.IsNullOrWhiteSpace(row.Key))
            {
                html.Append($"<code>{WebUtility.HtmlEncode(row.Key)}</code>");
            }

            html.Append($"<span>{row.Description}</span>");
            html.Append("</li>");
        }

        html.Append("</ul>");
        html.Append("</div>");
    }

    /// <summary>
    /// Renders the content of an XElement as block-level HTML (wrapping in paragraphs if necessary).
    /// </summary>
    /// <param name="element">The XElement to render.</param>
    /// <returns>An HTML fragment string.</returns>
    private static string RenderBlockContent(XElement element)
    {
        var rendered = RenderNodes(element.Nodes(), inlineContext: false).Trim();
        if (string.IsNullOrWhiteSpace(rendered))
        {
            return string.Empty;
        }

        var hasBlockChildren = element.Elements().Any(e => e.Name.LocalName is "para" or "code" or "list");

        return hasBlockChildren ? rendered : $"<p>{rendered}</p>";
    }

    /// <summary>
    /// Renders the content of an XElement as inline HTML.
    /// </summary>
    /// <param name="element">The XElement to render.</param>
    /// <returns>An HTML fragment string.</returns>
    private static string RenderInlineContent(XElement element)
    {
        return RenderNodes(element.Nodes(), inlineContext: true).Trim();
    }

    /// <summary>
    /// Renders a collection of XML nodes into HTML strings.
    /// </summary>
    /// <param name="nodes">The nodes to render.</param>
    /// <param name="inlineContext">Indicates whether rendering occurs in an inline context (affects paragraph handling).</param>
    /// <returns>The combined HTML string.</returns>
    private static string RenderNodes(IEnumerable<XNode> nodes, bool inlineContext)
    {
        var builder = new StringBuilder();
        foreach (var node in nodes)
        {
            builder.Append(RenderNode(node, inlineContext));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders a single XML node into its corresponding HTML fragment.
    /// </summary>
    /// <param name="node">The node to render.</param>
    /// <param name="inlineContext">Indicates whether rendering occurs in an inline context.</param>
    /// <returns>The HTML fragment string.</returns>
    private static string RenderNode(XNode node, bool inlineContext)
    {
        return node switch
        {
            XText textNode => WebUtility.HtmlEncode(NormalizeWhitespace(textNode.Value)),
            XElement elementNode => RenderElement(elementNode, inlineContext),
            _ => string.Empty
        };
    }

    private static string RenderElement(XElement element, bool inlineContext)
    {
        switch (element.Name.LocalName)
        {
            case "paramref":
            case "typeparamref":
                {
                    var name = element.Attribute("name")?.Value;

                    return string.IsNullOrWhiteSpace(name)
                        ? string.Empty
                        : $"<code>{WebUtility.HtmlEncode(name)}</code>";
                }
            case "see":
                {
                    var langword = element.Attribute("langword")?.Value;
                    var cref = SimplifyCref(element.Attribute("cref")?.Value);
                    var href = element.Attribute("href")?.Value;
                    var displayText =
                        langword ?? cref ?? href ?? RenderNodes(element.Nodes(), inlineContext: true).Trim();

                    return string.IsNullOrWhiteSpace(displayText)
                        ? string.Empty
                        : $"<code>{WebUtility.HtmlEncode(displayText)}</code>";
                }
            case "c":
                return $"<code>{RenderNodes(element.Nodes(), inlineContext: true).Trim()}</code>";
            case "code":
                return $"<pre><code>{WebUtility.HtmlEncode(element.Value.Trim())}</code></pre>";
            case "para":
                {
                    var paragraph = RenderNodes(element.Nodes(), inlineContext: true).Trim();
                    if (string.IsNullOrWhiteSpace(paragraph))
                    {
                        return string.Empty;
                    }

                    return inlineContext ? paragraph : $"<p>{paragraph}</p>";
                }
            case "list":
                {
                    var listTag = string.Equals(
                        element.Attribute("type")?.Value,
                        "number",
                        StringComparison.OrdinalIgnoreCase)
                        ? "ol"
                        : "ul";

                    var listItems = element.Elements("item")
                        .Select(item =>
                        {
                            var description = item.Element("description");
                            var contentSource = description ?? item;

                            return RenderNodes(contentSource.Nodes(), inlineContext: true).Trim();
                        })
                        .Where(content => !string.IsNullOrWhiteSpace(content))
                        .ToList();

                    if (listItems.Count == 0)
                    {
                        return string.Empty;
                    }

                    var builder = new StringBuilder();
                    builder.Append($"<{listTag}>");
                    foreach (var item in listItems)
                    {
                        builder.Append($"<li>{item}</li>");
                    }

                    builder.Append($"</{listTag}>");

                    return builder.ToString();
                }
            default:
                return RenderNodes(element.Nodes(), inlineContext);
        }
    }

    /// <summary>
    /// Normalizes whitespace in the provided string by replacing all whitespace sequences with a single space.
    /// </summary>
    /// <param name="value">The string to normalize.</param>
    /// <returns>The normalized string.</returns>
    private static string NormalizeWhitespace(string value)
    {
        return WhitespaceRegex.Replace(value, " ");
    }

    private static string NormalizeDocumentationCommentXml(DocumentationCommentTriviaSyntax xml)
    {
        return DocumentationCommentPrefixRegex.Replace(xml.ToString(), string.Empty).Trim();
    }

    /// <summary>
    /// Adds an outline item to a namespace page when the entry is complete and its target ID has not already been recorded.
    /// </summary>
    /// <param name="namespacePage">The namespace page receiving the outline item.</param>
    /// <param name="title">The reader-facing outline title.</param>
    /// <param name="id">The fragment identifier for the rendered documentation section.</param>
    /// <param name="level">The normalized outline level.</param>
    internal static void AddOutlineItem(
        NamespaceDocPage namespacePage,
        string title,
        string id,
        int level)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (namespacePage.Outline.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
        {
            return;
        }

        namespacePage.Outline.Add(
            new DocOutlineItem
            {
                Title = title.Trim(),
                Id = id.Trim(),
                Level = level
            });
    }

    /// <summary>
    /// Simplifies a "cref" attribute value by removing the type prefix (e.g., "M:", "T:").
    /// </summary>
    /// <param name="cref">The cref value to simplify.</param>
    /// <returns>The simplified string, or <c>null</c> if the input was empty.</returns>
    internal static string? SimplifyCref(string? cref)
    {
        if (string.IsNullOrWhiteSpace(cref))
        {
            return null;
        }

        var simplified = cref.Trim();
        if (simplified.Length >= 2 && simplified[1] == ':')
        {
            simplified = simplified[2..];
        }

        return string.IsNullOrWhiteSpace(simplified) ? null : simplified;
    }

    /// <summary>
    /// Gets an existing <see cref="NamespaceDocPage"/> for the specified namespace name, or creates a new one if it doesn't exist.
    /// </summary>
    /// <param name="namespacePages">The dictionary of existing pages.</param>
    /// <param name="namespaceName">The dotted namespace name.</param>
    /// <returns>The retrieved or newly created page.</returns>
    internal static NamespaceDocPage GetOrCreateNamespacePage(
        IDictionary<string, NamespaceDocPage> namespacePages,
        string namespaceName)
    {
        var normalizedNamespace = string.IsNullOrWhiteSpace(namespaceName) ? "Global" : namespaceName.Trim();
        var path = BuildNamespaceDocPath(normalizedNamespace);

        if (!namespacePages.TryGetValue(path, out var page))
        {
            var title = GetNamespaceTitle(normalizedNamespace);
            page = new NamespaceDocPage(
                normalizedNamespace,
                path,
                title,
                DocMetadataFactory.CreateApiReferenceMetadata(title, normalizedNamespace));
            namespacePages[path] = page;
        }

        return page;
    }

    /// <summary>
    /// Builds the hierarchical structure for namespaces, ensuring parent pages exist and child links are added back into the content.
    /// Rebuilds <paramref name="namespacePages"/> in place keyed by <see cref="NamespaceDocPage.Path"/>.
    /// </summary>
    /// <param name="namespacePages">The dictionary containing all unique namespace pages encountered during harvesting.</param>
    private static void EnsureNamespaceHierarchy(IDictionary<string, NamespaceDocPage> namespacePages)
    {
        var pagesByNamespace = namespacePages.Values
            .ToDictionary(p => p.FullNamespace, StringComparer.OrdinalIgnoreCase);

        if (!pagesByNamespace.ContainsKey(string.Empty))
        {
            pagesByNamespace[string.Empty] = new NamespaceDocPage(
                string.Empty,
                "Namespaces",
                "Namespaces",
                DocMetadataFactory.CreateApiReferenceMetadata("Namespaces", string.Empty));
        }

        var fullNamespaces = pagesByNamespace.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        foreach (var namespaceName in fullNamespaces)
        {
            var parts = namespaceName.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                var parentNamespace = string.Join(".", parts.Take(i));
                if (!pagesByNamespace.ContainsKey(parentNamespace))
                {
                    pagesByNamespace[parentNamespace] = new NamespaceDocPage(
                        parentNamespace,
                        BuildNamespaceDocPath(parentNamespace),
                        GetNamespaceTitle(parentNamespace),
                        DocMetadataFactory.CreateApiReferenceMetadata(GetNamespaceTitle(parentNamespace), parentNamespace));
                }
            }
        }

        foreach (var page in pagesByNamespace.Values)
        {
            page.ChildNamespaces.Clear();
        }

        foreach (var namespaceName in pagesByNamespace.Keys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            var parentNamespace = GetParentNamespace(namespaceName);
            if (pagesByNamespace.TryGetValue(parentNamespace, out var parentPage))
            {
                parentPage.ChildNamespaces.Add(namespaceName);
            }
        }

        foreach (var page in pagesByNamespace.Values)
        {
            if (page.ChildNamespaces.Count == 0)
            {
                continue;
            }

            var childLinks = page.ChildNamespaces
                .OrderBy(child => child, StringComparer.OrdinalIgnoreCase)
                .Select(child =>
                {
                    var childPath = BuildNamespaceDocPath(child);
                    var childTitle = GetNamespaceTitle(child);

                    return
                        $@"<li><a href=""/{WebUtility.HtmlEncode(childPath)}.html"">{WebUtility.HtmlEncode(childTitle)}</a></li>";
                });

            var childSection = new StringBuilder();
            childSection.Append("<section class=\"doc-namespace-groups\">");
            childSection.Append("<h4>Namespaces</h4>");
            childSection.Append("<ul>");
            foreach (var link in childLinks)
            {
                childSection.Append(link);
            }

            childSection.Append("</ul>");
            childSection.Append("</section>");

            page.Content.Insert(0, childSection.ToString());
        }

        namespacePages.Clear();
        foreach (var page in pagesByNamespace.Values)
        {
            namespacePages[page.Path] = page;
        }
    }

    /// <summary>
    /// Extracts the dotted namespace name for a given syntax node by traversing its ancestors.
    /// </summary>
    /// <param name="node">The syntax node to process.</param>
    /// <returns>The full dotted namespace name, or "Global" if none is found.</returns>
    private static string GetNamespaceName(SyntaxNode node)
    {
        var namespaceParts = node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(n => n.Name.ToString())
            .Reverse()
            .ToList();

        if (namespaceParts.Count == 0)
        {
            return "Global";
        }

        return string.Join(".", namespaceParts);
    }

    /// <summary>
    /// Constructs the relative documentation route path for a given namespace name.
    /// </summary>
    /// <param name="namespaceName">The dotted namespace name.</param>
    /// <returns>The relative route path string (e.g., "Namespaces/MyNamespace").</returns>
    internal static string BuildNamespaceDocPath(string namespaceName)
    {
        return string.IsNullOrWhiteSpace(namespaceName) ? "Namespaces" : $"Namespaces/{namespaceName}";
    }

    /// <summary>
    /// Derives a display title for a namespace name.
    /// </summary>
    /// <param name="fullNamespace">The dotted namespace name.</param>
    /// <returns>The display title; returns the last segment of the namespace or "Namespaces" for the root.</returns>
    internal static string GetNamespaceTitle(string fullNamespace)
    {
        if (string.IsNullOrWhiteSpace(fullNamespace))
        {
            return "Namespaces";
        }

        var separatorIndex = fullNamespace.LastIndexOf('.');

        return separatorIndex >= 0 ? fullNamespace[(separatorIndex + 1)..] : fullNamespace;
    }

    /// <summary>
    /// Gets the parent namespace name for a dotted namespace string.
    /// </summary>
    /// <param name="namespaceName">The dotted namespace name.</param>
    /// <returns>The parent namespace name, or an empty string if it is a root namespace.</returns>
    private static string GetParentNamespace(string namespaceName)
    {
        var separatorIndex = namespaceName.LastIndexOf('.');

        return separatorIndex < 0 ? string.Empty : namespaceName[..separatorIndex];
    }

    /// <summary>
    /// Builds the dot-delimited qualified name for a type or enum declaration, including enclosing types and namespaces.
    /// </summary>
    /// <param name="node">The type or enum declaration syntax node to compute the qualified name for.</param>
    /// <returns>The qualified name as a dot-delimited string containing nested type and namespace segments.</returns>
    private string GetQualifiedName(BaseTypeDeclarationSyntax node)
    {
        var parts = new Stack<string>();
        if (node is TypeDeclarationSyntax rootType)
        {
            parts.Push(GetTypeNameForQualifiedId(rootType));
        }
        else
        {
            parts.Push(node.Identifier.Text);
        }

        var parent = node.Parent;
        while (parent != null)
        {
            if (parent is TypeDeclarationSyntax typeDecl)
            {
                parts.Push(GetTypeNameForQualifiedId(typeDecl));
            }
            else if (parent is BaseNamespaceDeclarationSyntax namespaceDecl)
            {
                parts.Push(namespaceDecl.Name.ToString());
            }

            parent = parent.Parent;
        }

        return string.Join(".", parts);
    }

    /// <summary>
    /// Represents a single documentation page for a C# namespace, accumulating content from types within it.
    /// </summary>
    internal sealed class NamespaceDocPage
    {
        public NamespaceDocPage(string fullNamespace, string path, string title, DocMetadata metadata)
        {
            FullNamespace = fullNamespace;
            Title = title;
            Path = path;
            Metadata = metadata;
        }

        public string FullNamespace { get; }

        public string Title { get; }

        public string Path { get; }

        public DocMetadata Metadata { get; }

        public StringBuilder Content { get; } = new();

        public List<DocOutlineItem> Outline { get; } = [];

        public List<DocSymbolSourceProvenance> SymbolSourceProvenance { get; } = [];

        public HashSet<string> ChildNamespaces { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
