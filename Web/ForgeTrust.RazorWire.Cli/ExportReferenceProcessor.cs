using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Discovers and rewrites exporter-managed references in HTML and CSS bodies.
/// </summary>
/// <remarks>
/// The processor intentionally separates semantic discovery from output rewriting. HTML discovery uses AngleSharp so valid browser
/// markup such as unquoted attributes and case-insensitive element names is traversed through a parser. Rewrite operations continue
/// to operate on the original source text and replace only the attribute or CSS token value that resolves to an emitted artifact,
/// preserving document formatting, comments, casing, and unrelated attributes.
/// </remarks>
internal sealed partial class ExportReferenceProcessor
{
    private const string ExportIgnoreAttributeName = "data-rw-export-ignore";
    private static readonly Uri ManagedUrlBase = new("http://dummy");

    private static readonly HashSet<string> SourceNavigationAnchorExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".cshtml",
        ".csproj",
        ".fs",
        ".fsproj",
        ".props",
        ".razor",
        ".sln",
        ".slnx",
        ".targets",
        ".vb",
        ".vbproj",
    };

    private static readonly HashSet<string> SupportedLinkRelTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "stylesheet",
        "icon",
        "preload",
        "prefetch",
        "dns-prefetch",
        "canonical",
    };

    private readonly HtmlParser _htmlParser = new();
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExportReferenceProcessor"/> class.
    /// </summary>
    /// <param name="logger">Logger used for low-noise discovery decisions such as ignored source-navigation anchors.</param>
    public ExportReferenceProcessor(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Extracts exporter-managed internal references from HTML or CSS content.
    /// </summary>
    /// <param name="content">The HTML document, style block, style attribute, or stylesheet body to scan.</param>
    /// <param name="currentRoute">The normalized route that owns <paramref name="content"/>.</param>
    /// <param name="htmlScope"><see langword="true"/> for HTML documents; <see langword="false"/> for standalone CSS bodies.</param>
    /// <returns>Managed references with URL provenance. External, hash-only, data, JavaScript, mailto, and malformed values are filtered out.</returns>
    public IReadOnlyList<ExportReference> ExtractReferences(string content, string currentRoute, bool htmlScope)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRoute);

        return htmlScope
            ? ExtractHtmlReferences(content, currentRoute)
            : ExtractCssReferences(content, currentRoute, "stylesheet", null, null);
    }

    /// <summary>
    /// Rewrites managed references to their emitted artifact URLs while preserving the surrounding source text.
    /// </summary>
    /// <param name="content">HTML or CSS content to rewrite.</param>
    /// <param name="currentRoute">The normalized route that owns <paramref name="content"/>.</param>
    /// <param name="htmlScope"><see langword="true"/> for HTML documents; <see langword="false"/> for standalone CSS bodies.</param>
    /// <param name="resolveArtifactUrl">Callback that returns an emitted artifact URL for a managed reference, or <see langword="null"/> when unresolved.</param>
    /// <returns>The rewritten content. Unresolved, external, malformed, and unsupported references remain unchanged.</returns>
    public string RewriteManagedReferences(
        string content,
        string currentRoute,
        bool htmlScope,
        Func<ExportReference, string?> resolveArtifactUrl)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRoute);
        ArgumentNullException.ThrowIfNull(resolveArtifactUrl);

        return htmlScope
            ? RewriteHtmlReferences(content, currentRoute, resolveArtifactUrl)
            : RewriteCssReferences(content, currentRoute, "stylesheet", null, null, resolveArtifactUrl);
    }

    /// <summary>
    /// Resolves a potentially relative URL against a base route.
    /// </summary>
    /// <param name="baseRoute">The source route that owns the reference.</param>
    /// <param name="url">The raw URL value from HTML or CSS.</param>
    /// <returns>A root-relative URL when resolution succeeds; otherwise the original <paramref name="url"/>.</returns>
    public string ResolveRelativeUrl(string baseRoute, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (url.StartsWith('/') || Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return url;
        }

        try
        {
            var baseUri = new Uri(new Uri("http://dummy"), baseRoute);
            var resolvedUri = new Uri(baseUri, url);

            return resolvedUri.AbsolutePath + resolvedUri.Query + resolvedUri.Fragment;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve relative URL: {Url} against {BaseRoute}", url, baseRoute);

            return url;
        }
    }

    /// <summary>
    /// Splits an exporter-managed root-relative URL into path, query, and fragment parts.
    /// </summary>
    /// <param name="rawRef">The URL to inspect.</param>
    /// <param name="path">The normalized managed path without query or fragment.</param>
    /// <param name="query">The query text including the leading question mark, when present.</param>
    /// <param name="fragment">The fragment text including the leading hash, when present.</param>
    /// <returns><see langword="true"/> when <paramref name="rawRef"/> is a valid exporter-managed URL.</returns>
    internal static bool TrySplitManagedUrl(
        string rawRef,
        out string path,
        out string query,
        out string fragment)
    {
        path = string.Empty;
        query = string.Empty;
        fragment = string.Empty;

        if (string.IsNullOrWhiteSpace(rawRef) || !rawRef.StartsWith('/') || rawRef.StartsWith("//"))
        {
            return false;
        }

        if (!Uri.TryCreate(ManagedUrlBase, rawRef, out var normalizedUri))
        {
            return false;
        }

        rawRef = normalizedUri.PathAndQuery + normalizedUri.Fragment;

        var pathEnd = rawRef.Length;
        var queryStart = rawRef.IndexOf('?');
        var fragmentStart = rawRef.IndexOf('#');
        var hasQuery = queryStart >= 0 && (fragmentStart < 0 || queryStart < fragmentStart);

        if (hasQuery)
        {
            pathEnd = Math.Min(pathEnd, queryStart);
        }

        if (fragmentStart >= 0)
        {
            pathEnd = Math.Min(pathEnd, fragmentStart);
        }

        path = rawRef[..pathEnd];
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (hasQuery)
        {
            var queryEnd = fragmentStart >= 0 ? fragmentStart : rawRef.Length;
            query = rawRef[queryStart..queryEnd];
        }

        if (fragmentStart >= 0)
        {
            fragment = rawRef[fragmentStart..];
        }

        return true;
    }

    private IReadOnlyList<ExportReference> ExtractHtmlReferences(string html, string currentRoute)
    {
        var references = new List<ExportReference>();
        var document = _htmlParser.ParseDocument(html);

        foreach (var element in document.QuerySelectorAll("a[href]"))
        {
            var href = element.GetAttribute("href") ?? string.Empty;
            if (ShouldIgnoreAnchorReference(element, href))
            {
                _logger.LogDebug("Skipping source-navigation anchor href {Href} from {CurrentRoute}.", href, currentRoute);
                continue;
            }

            AddReference(references, href, ExportReferenceKind.AnchorHref, currentRoute, CreateHtmlProvenance(html, element, "href"));
        }

        foreach (var element in document.QuerySelectorAll("turbo-frame[src]"))
        {
            AddReference(references, element.GetAttribute("src") ?? string.Empty, ExportReferenceKind.TurboFrameSrc, currentRoute, CreateHtmlProvenance(html, element, "src"));
        }

        foreach (var element in document.QuerySelectorAll("script[src]"))
        {
            AddReference(references, element.GetAttribute("src") ?? string.Empty, ExportReferenceKind.ScriptSrc, currentRoute, CreateHtmlProvenance(html, element, "src"));
        }

        foreach (var element in document.QuerySelectorAll("link[href]"))
        {
            if (IsSupportedLinkRel(element.GetAttribute("rel") ?? string.Empty))
            {
                AddReference(references, element.GetAttribute("href") ?? string.Empty, ExportReferenceKind.LinkHref, currentRoute, CreateHtmlProvenance(html, element, "href"));
            }
        }

        foreach (var element in document.QuerySelectorAll("img[src]"))
        {
            AddReference(references, element.GetAttribute("src") ?? string.Empty, ExportReferenceKind.ImgSrc, currentRoute, CreateHtmlProvenance(html, element, "src"));
        }

        foreach (var element in document.QuerySelectorAll("[srcset]"))
        {
            foreach (var candidate in ParseSrcSetCandidates(element.GetAttribute("srcset") ?? string.Empty))
            {
                AddReference(references, candidate.Url, ExportReferenceKind.ImgSrcSet, currentRoute, CreateHtmlProvenance(html, element, "srcset", "srcset candidate"));
            }
        }

        foreach (var element in document.QuerySelectorAll("style"))
        {
            references.AddRange(ExtractCssReferences(element.TextContent, currentRoute, "style", element.LocalName, null));
        }

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            references.AddRange(ExtractCssReferences(element.GetAttribute("style") ?? string.Empty, currentRoute, "style", element.LocalName, "style"));
        }

        return references;
    }

    private IReadOnlyList<ExportReference> ExtractCssReferences(
        string css,
        string currentRoute,
        string surface,
        string? elementName,
        string? attributeName)
    {
        var references = new List<ExportReference>();

        foreach (var token in EnumerateCssReferenceTokens(css))
        {
            AddReference(
                references,
                DecodeCssReferenceValue(token.RawValue),
                ExportReferenceKind.CssUrl,
                currentRoute,
                CreateCssProvenance(css, surface, elementName, attributeName, token));
        }

        return references;
    }

    private void AddReference(
        ICollection<ExportReference> references,
        string rawValue,
        ExportReferenceKind kind,
        string currentRoute,
        ExportReferenceProvenance provenance)
    {
        var reference = CreateReference(rawValue.Trim(), kind, currentRoute, provenance);
        if (reference is not null)
        {
            references.Add(reference);
        }
    }

    private ExportReference? CreateReference(
        string rawValue,
        ExportReferenceKind kind,
        string currentRoute,
        ExportReferenceProvenance provenance)
    {
        if (IsHashOnlyReference(rawValue))
        {
            return null;
        }

        var resolved = ResolveRelativeUrl(currentRoute, rawValue);
        if (!TrySplitManagedUrl(resolved, out var path, out var query, out var fragment))
        {
            return null;
        }

        return new ExportReference(currentRoute, kind, rawValue, resolved, path, query, fragment, provenance);
    }

    private string RewriteHtmlReferences(string html, string currentRoute, Func<ExportReference, string?> resolveArtifactUrl)
    {
        var rewritten = RewriteTagAttributeReferences(html, currentRoute, resolveArtifactUrl);
        return RewriteStyleBlockReferences(rewritten, currentRoute, resolveArtifactUrl);
    }

    private string RewriteTagAttributeReferences(string html, string currentRoute, Func<ExportReference, string?> resolveArtifactUrl)
    {
        var replacements = new List<TextReplacement>();

        foreach (var tag in EnumerateTags(html))
        {
            var attributes = ParseAttributes(html, tag).ToList();
            if (attributes.Count == 0)
            {
                continue;
            }

            var tagName = tag.Name;
            if (tagName.Equals("a", StringComparison.OrdinalIgnoreCase))
            {
                AddAttributeRewrite(replacements, attributes, "href", ExportReferenceKind.AnchorHref, currentRoute, resolveArtifactUrl, rawValue => ShouldIgnoreAnchorReference(attributes, rawValue));
            }
            else if (tagName.Equals("turbo-frame", StringComparison.OrdinalIgnoreCase))
            {
                AddAttributeRewrite(replacements, attributes, "src", ExportReferenceKind.TurboFrameSrc, currentRoute, resolveArtifactUrl);
            }
            else if (tagName.Equals("script", StringComparison.OrdinalIgnoreCase))
            {
                AddAttributeRewrite(replacements, attributes, "src", ExportReferenceKind.ScriptSrc, currentRoute, resolveArtifactUrl);
            }
            else if (tagName.Equals("link", StringComparison.OrdinalIgnoreCase) && IsSupportedLinkRel(GetAttributeValue(attributes, "rel") ?? string.Empty))
            {
                AddAttributeRewrite(replacements, attributes, "href", ExportReferenceKind.LinkHref, currentRoute, resolveArtifactUrl);
            }
            else if (tagName.Equals("img", StringComparison.OrdinalIgnoreCase))
            {
                AddAttributeRewrite(replacements, attributes, "src", ExportReferenceKind.ImgSrc, currentRoute, resolveArtifactUrl);
            }

            AddSrcSetRewrites(replacements, attributes, currentRoute, resolveArtifactUrl);
            AddStyleAttributeRewrite(replacements, attributes, currentRoute, resolveArtifactUrl);
        }

        return ApplyReplacements(html, replacements);
    }

    private string RewriteStyleBlockReferences(string html, string currentRoute, Func<ExportReference, string?> resolveArtifactUrl)
    {
        var replacements = new List<TextReplacement>();
        foreach (var tag in EnumerateTags(html))
        {
            if (!tag.Name.Equals("style", StringComparison.OrdinalIgnoreCase)
                || IsSelfClosingTag(html, tag.Start, tag.End)
                || !TryFindRawTextClose(html, tag, out var closeStart, out _))
            {
                continue;
            }

            var valueStart = tag.End + 1;
            var valueLength = closeStart - valueStart;
            var value = html.Substring(valueStart, valueLength);
            var rewritten = RewriteCssReferences(value, currentRoute, "style", tag.Name, null, resolveArtifactUrl);
            if (!string.Equals(value, rewritten, StringComparison.Ordinal))
            {
                replacements.Add(new TextReplacement(valueStart, valueLength, rewritten));
            }
        }

        return ApplyReplacements(html, replacements);
    }

    private void AddAttributeRewrite(
        ICollection<TextReplacement> replacements,
        IReadOnlyList<HtmlAttributeSpan> attributes,
        string attributeName,
        ExportReferenceKind kind,
        string currentRoute,
        Func<ExportReference, string?> resolveArtifactUrl,
        Func<string, bool>? shouldSkip = null)
    {
        if (GetAttribute(attributes, attributeName) is not { } attribute
            || !attribute.ValueStart.HasValue
            || !attribute.ValueLength.HasValue)
        {
            return;
        }

        var rawValue = attribute.Value.Trim();
        if (shouldSkip?.Invoke(rawValue) == true)
        {
            return;
        }

        var reference = CreateReference(rawValue, kind, currentRoute, CreateAttributeProvenance(attribute));
        var artifactUrl = reference is null ? null : resolveArtifactUrl(reference);
        if (artifactUrl is null)
        {
            return;
        }

        replacements.Add(new TextReplacement(attribute.ValueStart.Value, attribute.ValueLength.Value, artifactUrl));
    }

    private void AddSrcSetRewrites(
        ICollection<TextReplacement> replacements,
        IReadOnlyList<HtmlAttributeSpan> attributes,
        string currentRoute,
        Func<ExportReference, string?> resolveArtifactUrl)
    {
        if (GetAttribute(attributes, "srcset") is not { } attribute || !attribute.ValueStart.HasValue)
        {
            return;
        }

        foreach (var candidate in ParseSrcSetCandidates(attribute.Value))
        {
            var reference = CreateReference(candidate.Url, ExportReferenceKind.ImgSrcSet, currentRoute, CreateAttributeProvenance(attribute, "srcset candidate"));
            var artifactUrl = reference is null ? null : resolveArtifactUrl(reference);
            if (artifactUrl is null)
            {
                continue;
            }

            replacements.Add(new TextReplacement(attribute.ValueStart.Value + candidate.Start, candidate.Length, artifactUrl));
        }
    }

    private void AddStyleAttributeRewrite(
        ICollection<TextReplacement> replacements,
        IReadOnlyList<HtmlAttributeSpan> attributes,
        string currentRoute,
        Func<ExportReference, string?> resolveArtifactUrl)
    {
        if (GetAttribute(attributes, "style") is not { } attribute
            || !attribute.ValueStart.HasValue
            || !attribute.ValueLength.HasValue)
        {
            return;
        }

        var rewritten = RewriteCssReferences(attribute.Value, currentRoute, "style", attribute.ElementName, "style", resolveArtifactUrl);
        if (!string.Equals(attribute.Value, rewritten, StringComparison.Ordinal))
        {
            replacements.Add(new TextReplacement(attribute.ValueStart.Value, attribute.ValueLength.Value, rewritten));
        }
    }

    private string RewriteCssReferences(
        string css,
        string currentRoute,
        string surface,
        string? elementName,
        string? attributeName,
        Func<ExportReference, string?> resolveArtifactUrl)
    {
        var replacements = new List<TextReplacement>();
        foreach (var token in EnumerateCssReferenceTokens(css))
        {
            var reference = CreateReference(
                DecodeCssReferenceValue(token.RawValue).Trim(),
                ExportReferenceKind.CssUrl,
                currentRoute,
                CreateCssProvenance(css, surface, elementName, attributeName, token));
            var artifactUrl = reference is null ? null : resolveArtifactUrl(reference);
            if (artifactUrl is null)
            {
                continue;
            }

            replacements.Add(new TextReplacement(token.ValueStart, token.ValueLength, artifactUrl));
        }

        return ApplyReplacements(css, replacements);
    }

    private static IEnumerable<TagSpan> EnumerateTags(string html)
    {
        for (var index = 0; index < html.Length; index++)
        {
            if (html[index] != '<' || index + 1 >= html.Length)
            {
                continue;
            }

            if (StartsWithOrdinalIgnoreCase(html, index, "<!--"))
            {
                var commentEnd = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                if (commentEnd < 0)
                {
                    yield break;
                }

                index = commentEnd + 2;
                continue;
            }

            if (html[index + 1] is '/' or '!' or '?')
            {
                continue;
            }

            var nameStart = index + 1;
            var nameEnd = nameStart;
            while (nameEnd < html.Length && IsHtmlNameChar(html[nameEnd]))
            {
                nameEnd++;
            }

            if (nameEnd == nameStart)
            {
                continue;
            }

            var tagEnd = FindTagEnd(html, nameEnd);
            if (tagEnd < 0)
            {
                yield break;
            }

            var tagName = html[nameStart..nameEnd];
            yield return new TagSpan(tagName, index, tagEnd - index + 1, nameEnd, tagEnd);
            if (IsRawTextElement(tagName) && !IsSelfClosingTag(html, index, tagEnd))
            {
                var tag = new TagSpan(tagName, index, tagEnd - index + 1, nameEnd, tagEnd);
                if (TryFindRawTextClose(html, tag, out _, out var closeEnd))
                {
                    index = closeEnd;
                    continue;
                }
            }

            index = tagEnd;
        }
    }

    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var index = start; index < html.Length; index++)
        {
            var ch = html[index];
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<HtmlAttributeSpan> ParseAttributes(string html, TagSpan tag)
    {
        var index = tag.NameEnd;
        while (index < tag.End)
        {
            while (index < tag.End && (char.IsWhiteSpace(html[index]) || html[index] == '/'))
            {
                index++;
            }

            if (index >= tag.End)
            {
                yield break;
            }

            var nameStart = index;
            while (index < tag.End && IsHtmlNameChar(html[index]))
            {
                index++;
            }

            if (index == nameStart)
            {
                index++;
                continue;
            }

            var name = html[nameStart..index];
            while (index < tag.End && char.IsWhiteSpace(html[index]))
            {
                index++;
            }

            if (index >= tag.End || html[index] != '=')
            {
                yield return new HtmlAttributeSpan(tag.Name, name, string.Empty, null, null, nameStart, CalculateLine(html, nameStart));
                continue;
            }

            index++;
            while (index < tag.End && char.IsWhiteSpace(html[index]))
            {
                index++;
            }

            if (index >= tag.End)
            {
                yield return new HtmlAttributeSpan(tag.Name, name, string.Empty, index, 0, nameStart, CalculateLine(html, nameStart));
                continue;
            }

            int valueStart;
            int valueEnd;
            if (html[index] is '"' or '\'')
            {
                var quote = html[index++];
                valueStart = index;
                while (index < tag.End && html[index] != quote)
                {
                    index++;
                }

                valueEnd = index;
                if (index < tag.End)
                {
                    index++;
                }
            }
            else
            {
                valueStart = index;
                while (index < tag.End && !char.IsWhiteSpace(html[index]) && html[index] != '>')
                {
                    index++;
                }

                valueEnd = index;
            }

            yield return new HtmlAttributeSpan(
                tag.Name,
                name,
                html[valueStart..valueEnd],
                valueStart,
                valueEnd - valueStart,
                nameStart,
                CalculateLine(html, nameStart));
        }
    }

    private static IReadOnlyList<SrcSetCandidate> ParseSrcSetCandidates(string srcSet)
    {
        var candidates = new List<SrcSetCandidate>();
        var index = 0;

        while (index < srcSet.Length)
        {
            while (index < srcSet.Length && (char.IsWhiteSpace(srcSet[index]) || srcSet[index] == ','))
            {
                index++;
            }

            if (index >= srcSet.Length)
            {
                break;
            }

            var urlStart = index;
            if (StartsWithOrdinalIgnoreCase(srcSet, index, "data:"))
            {
                index = FindDataUrlCandidateEnd(srcSet, index);
            }
            else
            {
                while (index < srcSet.Length && !char.IsWhiteSpace(srcSet[index]) && srcSet[index] != ',')
                {
                    index++;
                }
            }

            if (index > urlStart)
            {
                candidates.Add(new SrcSetCandidate(srcSet[urlStart..index], urlStart, index - urlStart));
            }

            while (index < srcSet.Length && srcSet[index] != ',')
            {
                index++;
            }
        }

        return candidates;
    }

    private static int FindDataUrlCandidateEnd(string srcSet, int start)
    {
        var index = start;
        while (index < srcSet.Length)
        {
            if (char.IsWhiteSpace(srcSet[index]))
            {
                var descriptorStart = index;
                while (descriptorStart < srcSet.Length && char.IsWhiteSpace(srcSet[descriptorStart]))
                {
                    descriptorStart++;
                }

                if (LooksLikeSrcSetDescriptor(srcSet, descriptorStart, out _))
                {
                    return index;
                }
            }

            index++;
        }

        return srcSet.Length;
    }

    private static bool LooksLikeSrcSetDescriptor(string srcSet, int start, out int end)
    {
        end = start;
        while (end < srcSet.Length && !char.IsWhiteSpace(srcSet[end]) && srcSet[end] != ',')
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        var descriptor = srcSet[start..end];
        return descriptor.EndsWith('w') || descriptor.EndsWith('x') || descriptor.EndsWith('h');
    }

    private static IEnumerable<CssReferenceToken> EnumerateCssReferenceTokens(string css)
    {
        var index = 0;
        while (index < css.Length)
        {
            if (index + 1 < css.Length && css[index] == '/' && css[index + 1] == '*')
            {
                index = SkipCssComment(css, index + 2);
                continue;
            }

            if (css[index] is '"' or '\'')
            {
                index = SkipCssString(css, index);
                continue;
            }

            if (StartsWithCssIdentifier(css, index, "url") && TryReadCssUrlToken(css, index, out var urlToken))
            {
                yield return urlToken;
                index = urlToken.TokenEnd;
                continue;
            }

            if (StartsWithCssIdentifier(css, index, "@import") && TryReadCssImportStringToken(css, index, out var importToken))
            {
                yield return importToken;
                index = importToken.TokenEnd;
                continue;
            }

            index++;
        }
    }

    private static bool TryReadCssUrlToken(string css, int start, out CssReferenceToken token)
    {
        token = default;
        var index = start + 3;
        while (index < css.Length && char.IsWhiteSpace(css[index]))
        {
            index++;
        }

        if (index >= css.Length || css[index] != '(')
        {
            return false;
        }

        index++;
        while (index < css.Length && char.IsWhiteSpace(css[index]))
        {
            index++;
        }

        if (index >= css.Length)
        {
            return false;
        }

        int valueStart;
        int valueEnd;
        if (css[index] is '"' or '\'')
        {
            var quote = css[index++];
            valueStart = index;
            while (index < css.Length)
            {
                if (css[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (css[index] == quote)
                {
                    break;
                }

                index++;
            }

            if (index >= css.Length)
            {
                return false;
            }

            valueEnd = index++;
            while (index < css.Length && char.IsWhiteSpace(css[index]))
            {
                index++;
            }

            if (index >= css.Length || css[index] != ')')
            {
                return false;
            }
        }
        else
        {
            valueStart = index;
            while (index < css.Length)
            {
                if (css[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (css[index] == ')')
                {
                    break;
                }

                if (css[index] is '"' or '\'')
                {
                    return false;
                }

                index++;
            }

            if (index >= css.Length || css[index] != ')')
            {
                return false;
            }

            valueEnd = index;
            while (valueEnd > valueStart && char.IsWhiteSpace(css[valueEnd - 1]))
            {
                valueEnd--;
            }
        }

        token = new CssReferenceToken(css[valueStart..valueEnd], valueStart, valueEnd - valueStart, index + 1, "url()");
        return true;
    }

    private static bool TryReadCssImportStringToken(string css, int start, out CssReferenceToken token)
    {
        token = default;
        var index = start + "@import".Length;
        if (index < css.Length && IsCssIdentifierChar(css[index]))
        {
            return false;
        }

        while (index < css.Length && char.IsWhiteSpace(css[index]))
        {
            index++;
        }

        if (index >= css.Length || css[index] is not ('"' or '\''))
        {
            return false;
        }

        var quote = css[index++];
        var valueStart = index;
        while (index < css.Length)
        {
            if (css[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (css[index] == quote)
            {
                token = new CssReferenceToken(css[valueStart..index], valueStart, index - valueStart, index + 1, "@import string");
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool StartsWithCssIdentifier(string css, int index, string identifier)
    {
        if (!StartsWithOrdinalIgnoreCase(css, index, identifier))
        {
            return false;
        }

        var before = index > 0 ? css[index - 1] : '\0';
        return before == '\0' || !IsCssIdentifierChar(before);
    }

    private static int SkipCssComment(string css, int index)
    {
        while (index + 1 < css.Length)
        {
            if (css[index] == '*' && css[index + 1] == '/')
            {
                return index + 2;
            }

            index++;
        }

        return css.Length;
    }

    private static int SkipCssString(string css, int start)
    {
        var quote = css[start];
        var index = start + 1;
        while (index < css.Length)
        {
            if (css[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (css[index] == quote)
            {
                return index + 1;
            }

            index++;
        }

        return css.Length;
    }

    private static bool ShouldIgnoreAnchorReference(IElement element, string href)
    {
        return element.HasAttribute(ExportIgnoreAttributeName) || IsSourceNavigationAnchorHref(href);
    }

    private static bool ShouldIgnoreAnchorReference(IReadOnlyList<HtmlAttributeSpan> attributes, string href)
    {
        return GetAttribute(attributes, ExportIgnoreAttributeName) is not null || IsSourceNavigationAnchorHref(href);
    }

    private static bool IsSourceNavigationAnchorHref(string href)
    {
        if (string.IsNullOrWhiteSpace(href)
            || href.StartsWith('/')
            || Uri.TryCreate(href, UriKind.Absolute, out _))
        {
            return false;
        }

        var pathEnd = href.IndexOfAny(['?', '#']);
        var pathOnly = pathEnd >= 0 ? href[..pathEnd] : href;
        return SourceNavigationAnchorExtensions.Contains(Path.GetExtension(pathOnly));
    }

    private static bool IsSupportedLinkRel(string rel)
    {
        return rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(SupportedLinkRelTokens.Contains);
    }

    private static HtmlAttributeSpan? GetAttribute(IReadOnlyList<HtmlAttributeSpan> attributes, string name)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute;
            }
        }

        return null;
    }

    private static string? GetAttributeValue(IReadOnlyList<HtmlAttributeSpan> attributes, string name)
    {
        return GetAttribute(attributes, name)?.Value;
    }

    private static bool IsHashOnlyReference(string rawValue)
    {
        return rawValue.TrimStart().StartsWith('#');
    }

    private static bool IsHtmlNameChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch is ':' or '_' or '-' or '.';
    }

    private static bool IsCssIdentifierChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch is '_' or '-';
    }

    private static bool IsRawTextElement(string tagName)
    {
        return tagName.Equals("script", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("style", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("textarea", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("title", StringComparison.OrdinalIgnoreCase)
            || tagName.Equals("template", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSelfClosingTag(string html, int tagStart, int tagEnd)
    {
        var index = tagEnd - 1;
        while (index > tagStart && char.IsWhiteSpace(html[index]))
        {
            index--;
        }

        return index > tagStart && html[index] == '/';
    }

    private static bool TryFindRawTextClose(string html, TagSpan tag, out int closeStart, out int closeEnd)
    {
        var searchIndex = tag.End + 1;
        while (searchIndex < html.Length)
        {
            closeStart = html.IndexOf("</", searchIndex, StringComparison.Ordinal);
            if (closeStart < 0)
            {
                closeEnd = -1;
                return false;
            }

            var nameStart = closeStart + 2;
            var nameEnd = nameStart + tag.Name.Length;
            if (StartsWithOrdinalIgnoreCase(html, nameStart, tag.Name)
                && nameEnd < html.Length
                && !IsHtmlNameChar(html[nameEnd]))
            {
                closeEnd = FindTagEnd(html, nameEnd);
                return closeEnd >= 0;
            }

            searchIndex = closeStart + 2;
        }

        closeStart = -1;
        closeEnd = -1;
        return false;
    }

    private static string DecodeCssReferenceValue(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\')
            {
                decoded.Append(value[index]);
                continue;
            }

            if (index + 1 >= value.Length)
            {
                continue;
            }

            index++;
            var escapeStart = index;
            var hexLength = 0;
            while (index < value.Length && hexLength < 6 && Uri.IsHexDigit(value[index]))
            {
                index++;
                hexLength++;
            }

            if (hexLength > 0)
            {
                var hex = value.Substring(escapeStart, hexLength);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var codePoint))
                {
                    decoded.Append(IsValidUnicodeScalar(codePoint) ? char.ConvertFromUtf32(codePoint) : "\uFFFD");
                }

                if (index < value.Length && char.IsWhiteSpace(value[index]))
                {
                    continue;
                }

                index--;
                continue;
            }

            decoded.Append(value[index]);
        }

        return decoded.ToString();
    }

    private static bool IsValidUnicodeScalar(int codePoint)
    {
        return codePoint is > 0 and <= 0x10FFFF
            && (codePoint < 0xD800 || codePoint > 0xDFFF);
    }

    private static bool StartsWithOrdinalIgnoreCase(string value, int index, string prefix)
    {
        return index >= 0
            && index + prefix.Length <= value.Length
            && string.Compare(value, index, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static string ApplyReplacements(string source, IReadOnlyCollection<TextReplacement> replacements)
    {
        if (replacements.Count == 0)
        {
            return source;
        }

        var rewritten = new StringBuilder(source);
        foreach (var replacement in replacements.OrderByDescending(replacement => replacement.Start))
        {
            rewritten.Remove(replacement.Start, replacement.Length);
            rewritten.Insert(replacement.Start, replacement.Value);
        }

        return rewritten.ToString();
    }

    private static ExportReferenceProvenance CreateHtmlProvenance(
        string html,
        IElement element,
        string attributeName,
        string? tokenType = null)
    {
        var offset = FindAttributeOffset(html, element.LocalName, attributeName, element.GetAttribute(attributeName));
        return new ExportReferenceProvenance(
            "html",
            element.LocalName,
            attributeName,
            tokenType ?? $"{element.LocalName} {attributeName}",
            offset,
            offset.HasValue ? CalculateLine(html, offset.Value) : null);
    }

    private static ExportReferenceProvenance CreateAttributeProvenance(HtmlAttributeSpan attribute, string? tokenType = null)
    {
        return new ExportReferenceProvenance(
            "html",
            attribute.ElementName,
            attribute.Name,
            tokenType ?? $"{attribute.ElementName} {attribute.Name}",
            attribute.NameStart,
            attribute.Line);
    }

    private static ExportReferenceProvenance CreateCssProvenance(
        string css,
        string surface,
        string? elementName,
        string? attributeName,
        CssReferenceToken token)
    {
        return new ExportReferenceProvenance(
            surface,
            elementName,
            attributeName,
            token.TokenType,
            token.ValueStart,
            CalculateLine(css, token.ValueStart));
    }

    private static int? FindAttributeOffset(string html, string elementName, string attributeName, string? value)
    {
        foreach (var tag in EnumerateTags(html))
        {
            if (!tag.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var attribute in ParseAttributes(html, tag))
            {
                if (attribute.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase)
                    && (value is null || string.Equals(attribute.Value, value, StringComparison.Ordinal)))
                {
                    return attribute.NameStart;
                }
            }
        }

        return null;
    }

    private static int CalculateLine(string content, int offset)
    {
        var line = 1;
        var limit = Math.Min(offset, content.Length);
        for (var index = 0; index < limit; index++)
        {
            if (content[index] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private readonly record struct CssReferenceToken(string RawValue, int ValueStart, int ValueLength, int TokenEnd, string TokenType);

    private readonly record struct HtmlAttributeSpan(
        string ElementName,
        string Name,
        string Value,
        int? ValueStart,
        int? ValueLength,
        int NameStart,
        int Line);

    private readonly record struct SrcSetCandidate(string Url, int Start, int Length);

    private readonly record struct TagSpan(string Name, int Start, int Length, int NameEnd, int End);

    private readonly record struct TextReplacement(int Start, int Length, string Value);
}
