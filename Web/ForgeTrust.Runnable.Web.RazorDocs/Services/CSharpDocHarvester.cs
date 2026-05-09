using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ForgeTrust.Runnable.Web.RazorDocs.Models;
using ForgeTrust.Runnable.Web.RazorWire;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// Harvester implementation that scans C# source files for XML documentation comments.
/// </summary>
public class CSharpDocHarvester : IDocHarvester
{
    private readonly ILogger<CSharpDocHarvester> _logger;
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of <see cref="CSharpDocHarvester"/> with the provided logger.
    /// </summary>
    public CSharpDocHarvester(ILogger<CSharpDocHarvester> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Collects XML documentation from C# source files under the specified root and produces DocNode entries containing titles, relative file paths with anchors, and HTML-formatted content.
    /// </summary>
    /// <param name="rootPath">The root directory to recursively scan for .cs files.</param>
    /// <param name="cancellationToken">An optional token to observe for cancellation requests.</param>
    /// <returns>A collection of DocNode objects; each contains a title, a relative file path including a fragment anchor, and the extracted HTML documentation.</returns>
    /// <remarks>
    /// Skips files in excluded directories (for example "node_modules", "bin", "obj", and "Tests") and hidden dot-prefixed directories unless explicitly allowlisted. Dot-prefixed files are included.
    /// </remarks>
    public async Task<IReadOnlyList<DocNode>> HarvestAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var nodes = new List<DocNode>();
        var stubNodes = new List<DocNode>();
        var namespacePages = new Dictionary<string, NamespaceDocPage>(StringComparer.OrdinalIgnoreCase);
        var csFiles = Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories);

        foreach (var file in csFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(rootPath, file)
                .Replace('\\', '/'); // Normalize to forward slashes for URLs
            if (HarvestPathExclusions.ShouldExcludeFilePath(relativePath))
            {
                continue;
            }

            try
            {
                var code = await File.ReadAllTextAsync(file, cancellationToken);
                var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: cancellationToken);
                var root = await tree.GetRootAsync(cancellationToken);

                // Capture Classes, Structs, Interfaces, Records
                var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
                foreach (var typeDecl in typeDeclarations)
                {
                    var doc = ExtractDoc(typeDecl);
                    var documentedMethods = typeDecl.Members
                        .OfType<MethodDeclarationSyntax>()
                        .Select(method => new
                        {
                            Method = method,
                            Doc = ExtractDoc(method)
                        })
                        .Where(x => x.Doc != null)
                        .ToList();
                    var documentedProperties = typeDecl.Members
                        .OfType<PropertyDeclarationSyntax>()
                        .Select(property => new
                        {
                            Property = property,
                            Doc = ExtractDoc(property)
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
                    var doc = ExtractDoc(enumDecl);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse C# file: {File}", file);
            }
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
        }

        nodes.AddRange(stubNodes);

        return nodes;
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
        return $@"<span data-razordocs-symbol-source=""{WebUtility.HtmlEncode(anchorId)}""></span>";
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
    /// Determines whether a parameter is a compiler-generated caller information parameter (e.g., [CallerFilePath]).
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
                || name.EndsWith("CallerLineNumberAttribute", StringComparison.Ordinal));
    }

    /// <summary>
    /// Extracts XML documentation from the leading trivia of a syntax node and converts it into HTML fragments.
    /// </summary>
    /// <param name="node">The syntax node whose leading XML documentation comments will be parsed.</param>
    /// <returns>The HTML string containing structured documentation sections, or <c>null</c> if no documentation is present or parsing fails.</returns>
    private string? ExtractDoc(SyntaxNode node)
    {
        var xml = node.GetLeadingTrivia()
            .Select(i => i.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();

        if (xml == null) return null;

        try
        {
            var cleanXml = xml.ToString().Replace("///", "").Trim();
            var wrappedXml = $"<doc>{cleanXml}</doc>";
            var xdoc = XDocument.Parse(wrappedXml, LoadOptions.PreserveWhitespace);
            var root = xdoc.Root!;

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
                root.Elements("param").Where(e => !IsCompilerGeneratedDocParameter(e.Attribute("name")?.Value)),
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
    /// Determines whether a parameter name corresponds to a compiler-generated caller information parameter.
    /// </summary>
    /// <param name="parameterName">The name of the parameter to check.</param>
    /// <returns><c>true</c> if it is a compiler-generated parameter; otherwise, <c>false</c>.</returns>
    private static bool IsCompilerGeneratedDocParameter(string? parameterName)
    {
        return parameterName is "callerFilePath" or "callerLineNumber";
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
