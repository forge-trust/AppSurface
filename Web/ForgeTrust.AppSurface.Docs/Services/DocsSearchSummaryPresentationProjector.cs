using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Projects authored Markdown summaries into the small, safe display contract used by docs search results.
/// </summary>
/// <remarks>
/// The projector deliberately uses Markdig's base parser rather than the Docs rendering pipeline. Search-result
/// previews need semantic inline formatting, not HTML, extensions, attributes, links, or renderer behavior. Its output
/// is bounded to protect browser consumers even when a summary is author-authored or sourced from an external index.
/// </remarks>
internal static class DocsSearchSummaryPresentationProjector
{
    internal const int MaxDepth = 8;
    internal const int MaxNodes = 128;
    internal const int MaxScalars = 1024;
    private const int MinScalarsForWhitespaceTruncation = MaxScalars / 2;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();
    private static readonly Regex UrlTokenRegex = new(
        @"(?<!\w)(?:https?://|www\.)[^\s<>()]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Projects one raw Markdown summary, returning <c>null</c> when it has no reader-facing content.
    /// </summary>
    /// <param name="summary">The raw summary retained unchanged in the legacy search-index field.</param>
    /// <returns>A bounded display tree, or <c>null</c> when no usable text remains.</returns>
    internal static IReadOnlyList<DocsSearchSummaryPresentationNode>? Project(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var builder = new ProjectionBuilder();
        var document = Markdown.Parse(summary, Pipeline);

        foreach (var block in document)
        {
            if (builder.IsTruncated)
            {
                break;
            }

            builder.BeginBlock();
            AppendBlock(block, builder, builder.Root, depth: 1);
        }

        return builder.Build();
    }

    private static void AppendBlock(
        Block block,
        ProjectionBuilder builder,
        List<MutableNode> target,
        int depth)
    {
        if (builder.IsTruncated || block is HtmlBlock)
        {
            return;
        }

        if (block is CodeBlock codeBlock)
        {
            builder.AppendText(target, "text", RemoveUrlTokens(codeBlock.Lines.ToString()), depth);
            return;
        }

        if (block is LeafBlock { Inline: not null } leaf)
        {
            AppendInline(leaf.Inline.FirstChild, builder, target, depth);
            return;
        }

        if (block is ContainerBlock container)
        {
            foreach (var child in container)
            {
                if (builder.IsTruncated)
                {
                    break;
                }

                builder.BeginBlock();
                AppendBlock(child, builder, target, depth);
            }
        }
    }

    private static void AppendInline(
        Inline? inline,
        ProjectionBuilder builder,
        List<MutableNode> target,
        int depth)
    {
        var rawHtmlSuppressionDepth = 0;
        while (inline is not null && !builder.IsTruncated)
        {
            if (inline is HtmlInline html)
            {
                UpdateRawHtmlSuppression(html.Tag, ref rawHtmlSuppressionDepth);
                inline = inline.NextSibling;
                continue;
            }

            if (rawHtmlSuppressionDepth > 0)
            {
                inline = inline.NextSibling;
                continue;
            }

            switch (inline)
            {
                case LiteralInline literal:
                    builder.AppendText(target, "text", RemoveUrlTokens(literal.Content.ToString()), depth);
                    break;
                case CodeInline code:
                    builder.AppendText(target, "code", RemoveUrlTokens(code.Content), depth);
                    break;
                case LineBreakInline:
                    builder.AppendText(target, "text", " ", depth);
                    break;
                case EmphasisInline emphasis:
                    AppendFormattedInline(
                        emphasis,
                        emphasis.DelimiterCount >= 2 ? "strong" : "emphasis",
                        builder,
                        target,
                        depth);
                    break;
                case ContainerInline container:
                    AppendInline(container.FirstChild, builder, target, depth);
                    break;
            }

            inline = inline.NextSibling;
        }
    }

    private static void AppendFormattedInline(
        ContainerInline inline,
        string kind,
        ProjectionBuilder builder,
        List<MutableNode> target,
        int depth)
    {
        if (depth >= MaxDepth)
        {
            builder.AppendText(target, "text", ExtractInlineText(inline.FirstChild), depth);
            return;
        }

        var formatted = builder.AddContainer(target, kind, depth);
        if (formatted is null)
        {
            return;
        }

        AppendInline(inline.FirstChild, builder, formatted.Children, depth + 1);
        if (formatted.Children.Count == 0)
        {
            target.Remove(formatted);
            builder.RemoveNode();
        }
    }

    private static string ExtractInlineText(Inline? inline)
    {
        var builder = new StringBuilder();
        var rawHtmlSuppressionDepth = 0;

        while (inline is not null)
        {
            if (inline is HtmlInline html)
            {
                UpdateRawHtmlSuppression(html.Tag, ref rawHtmlSuppressionDepth);
                inline = inline.NextSibling;
                continue;
            }

            if (rawHtmlSuppressionDepth > 0)
            {
                inline = inline.NextSibling;
                continue;
            }

            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(RemoveUrlTokens(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    builder.Append(RemoveUrlTokens(code.Content));
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case ContainerInline container:
                    builder.Append(ExtractInlineText(container.FirstChild));
                    break;
            }

            inline = inline.NextSibling;
        }

        return builder.ToString();
    }

    private static string RemoveUrlTokens(string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : UrlTokenRegex.Replace(value, string.Empty);
    }

    private static void UpdateRawHtmlSuppression(string tag, ref int depth)
    {
        if (!TryClassifySuppressedHtmlTag(tag, out var isClosing, out var isSelfClosing))
        {
            return;
        }

        if (isClosing)
        {
            depth = Math.Max(0, depth - 1);
        }
        else if (!isSelfClosing)
        {
            depth++;
        }
    }

    /// <summary>
    /// Classifies the raw HTML tags that suppress their inline contents from a search summary.
    /// </summary>
    /// <param name="tag">Raw HTML tag text reported by the Markdown parser.</param>
    /// <param name="isClosing">Whether <paramref name="tag"/> closes a suppressed element.</param>
    /// <param name="isSelfClosing">Whether <paramref name="tag"/> is self-closing.</param>
    /// <returns><see langword="true"/> when the tag is a script or style element; otherwise, <see langword="false"/>.</returns>
    internal static bool TryClassifySuppressedHtmlTag(string tag, out bool isClosing, out bool isSelfClosing)
    {
        var value = tag.Trim();
        isClosing = value.StartsWith("</", StringComparison.Ordinal);
        var nameStart = isClosing ? 2 : 1;
        isSelfClosing = false;

        if (value.Length <= nameStart || value[0] != '<')
        {
            return false;
        }

        foreach (var tagName in new[] { "script", "style" })
        {
            if (!value.AsSpan(nameStart).StartsWith(tagName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nameEnd = nameStart + tagName.Length;
            if (nameEnd >= value.Length)
            {
                return false;
            }

            var delimiter = value[nameEnd];
            if (!char.IsWhiteSpace(delimiter) && delimiter != '>' && delimiter != '/')
            {
                return false;
            }

            isSelfClosing = !isClosing && value.EndsWith("/>", StringComparison.Ordinal);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Accumulates the bounded presentation tree while enforcing node and scalar budgets.
    /// </summary>
    /// <remarks>
    /// Internal visibility permits direct verification of defensive budget guards that valid Markdown cannot reach.
    /// </remarks>
    internal sealed class ProjectionBuilder
    {
        private readonly List<MutableNode> _root = [];
        private int _nodeCount;
        private int _scalarCount;
        private bool _hasContent;
        private bool _pendingBlockSeparator;
        private bool _pendingInlineSeparator;

        /// <summary>
        /// Gets the mutable top-level nodes collected for the current projection.
        /// </summary>
        internal List<MutableNode> Root => _root;

        /// <summary>
        /// Gets whether the builder has emitted its terminal truncation marker.
        /// </summary>
        internal bool IsTruncated { get; private set; }

        /// <summary>
        /// Records a boundary between reader-visible Markdown blocks.
        /// </summary>
        internal void BeginBlock()
        {
            if (_hasContent)
            {
                _pendingBlockSeparator = true;
            }
        }

        /// <summary>
        /// Adds a formatted container when the depth and node budgets allow it.
        /// </summary>
        /// <param name="target">The parent node collection.</param>
        /// <param name="kind">The safe presentation-node kind.</param>
        /// <param name="depth">The candidate container depth.</param>
        /// <returns>The added node, or <see langword="null"/> after budget truncation.</returns>
        internal MutableNode? AddContainer(List<MutableNode> target, string kind, int depth)
        {
            if (depth >= MaxDepth || !CanAddNode())
            {
                Truncate();
                return null;
            }

            var node = new MutableNode(kind);
            target.Add(node);
            _nodeCount++;
            return node;
        }

        /// <summary>
        /// Normalizes and appends a bounded text or code leaf.
        /// </summary>
        /// <param name="target">The parent node collection.</param>
        /// <param name="kind">The safe presentation-node kind.</param>
        /// <param name="value">The raw text to append.</param>
        /// <param name="depth">The leaf depth.</param>
        internal void AppendText(List<MutableNode> target, string kind, string? value, int depth)
        {
            if (IsTruncated || depth > MaxDepth)
            {
                return;
            }

            var text = NormalizeWhitespace(value);
            if (text.Length == 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _pendingInlineSeparator |= _hasContent;
                return;
            }

            if ((_pendingBlockSeparator || _pendingInlineSeparator) && _hasContent && !char.IsWhiteSpace(text[0]))
            {
                text = " " + text;
            }

            _pendingBlockSeparator = false;
            _pendingInlineSeparator = false;
            AppendBounded(target, kind, text);
        }

        /// <summary>
        /// Reclaims the node count after a newly created empty container is discarded.
        /// </summary>
        internal void RemoveNode()
        {
            _nodeCount--;
        }

        /// <summary>
        /// Materializes a safe immutable presentation tree.
        /// </summary>
        /// <returns>The immutable nodes, or <see langword="null"/> when no reader-visible text remains.</returns>
        internal IReadOnlyList<DocsSearchSummaryPresentationNode>? Build()
        {
            var nodes = _root
                .Select(node => node.ToImmutable())
                .Where(node => node is not null)
                .Select(node => node!)
                .ToArray();

            return nodes.Length == 0 ? null : nodes;
        }

        private void AppendBounded(List<MutableNode> target, string kind, string text)
        {
            var remaining = MaxScalars - _scalarCount;
            var scalarLength = CountScalars(text);

            if (scalarLength <= remaining)
            {
                if (AppendToLeaf(target, kind, text))
                {
                    _scalarCount += scalarLength;
                    _hasContent = true;
                }

                return;
            }

            var retained = Math.Max(0, MaxScalars - 1 - _scalarCount);
            if (retained > 0)
            {
                var prefix = TruncateAtBoundary(text, retained);
                if (!AppendToLeaf(target, kind, prefix))
                {
                    return;
                }

                _scalarCount += CountScalars(prefix);
            }

            Truncate(target, kind);
        }

        private bool AppendToLeaf(List<MutableNode> target, string kind, string text)
        {
            if (target.LastOrDefault() is { Kind: var lastKind } last && lastKind == kind && last.Children.Count == 0)
            {
                last.Text.Append(text);
                return true;
            }

            if (!CanAddNode())
            {
                Truncate();
                return false;
            }

            var leaf = new MutableNode(kind);
            leaf.Text.Append(text);
            target.Add(leaf);
            _nodeCount++;
            return true;
        }

        private bool CanAddNode() => _nodeCount < MaxNodes;

        private void Truncate(List<MutableNode>? target = null, string kind = "text")
        {
            if (IsTruncated)
            {
                return;
            }

            var lastLeaf = FindLastLeaf(_root);
            if (lastLeaf is null && target is not null && CanAddNode())
            {
                lastLeaf = new MutableNode(kind);
                target.Add(lastLeaf);
                _nodeCount++;
            }

            if (lastLeaf is null)
            {
                return;
            }

            TrimLeafToScalars(_root, lastLeaf, Math.Max(0, MaxScalars - 1));
            lastLeaf.Text.Append('\u2026');
            _scalarCount = Math.Min(MaxScalars, CountAllScalars(_root));
            _hasContent = true;
            IsTruncated = true;
        }

        private static MutableNode? FindLastLeaf(IEnumerable<MutableNode> nodes)
        {
            return nodes
                .Select(node => node.Children.Count == 0 && node.Text.Length > 0
                    ? node
                    : FindLastLeaf(node.Children))
                .LastOrDefault(candidate => candidate is not null);
        }

        private static void TrimLeafToScalars(IEnumerable<MutableNode> root, MutableNode leaf, int maximum)
        {
            var beforeLeaf = CountScalarsBeforeLeaf(root, leaf);
            var leafLimit = Math.Max(0, maximum - beforeLeaf);
            var truncated = TruncateAtBoundary(leaf.Text.ToString(), leafLimit);
            leaf.Text.Clear();
            leaf.Text.Append(truncated);
        }

        private static int CountScalarsBeforeLeaf(IEnumerable<MutableNode> nodes, MutableNode target)
        {
            var count = 0;
            foreach (var node in nodes)
            {
                if (ReferenceEquals(node, target))
                {
                    return count;
                }

                count += CountScalars(node.Text.ToString());
                var childCount = CountScalarsBeforeLeaf(node.Children, target);
                if (childCount >= 0)
                {
                    return count + childCount;
                }

                count += CountAllScalars(node.Children);
            }

            return -1;
        }

        private static int CountAllScalars(IEnumerable<MutableNode> nodes)
        {
            var count = 0;
            foreach (var node in nodes)
            {
                count += CountScalars(node.Text.ToString());
                count += CountAllScalars(node.Children);
            }

            return count;
        }

        private static int CountScalars(string value)
        {
            var count = 0;
            foreach (var _ in value.EnumerateRunes())
            {
                count++;
            }

            return count;
        }

        private static string TruncateAtBoundary(string value, int maximum)
        {
            if (maximum <= 0)
            {
                return string.Empty;
            }

            var runes = value.EnumerateRunes().Take(maximum).ToArray();
            if (runes.Length == CountScalars(value))
            {
                return value;
            }

            if (runes.Length >= MinScalarsForWhitespaceTruncation)
            {
                var boundary = Array.FindLastIndex(runes, rune => Rune.IsWhiteSpace(rune));
                if (boundary >= MinScalarsForWhitespaceTruncation)
                {
                    runes = runes[..boundary];
                }
            }

            return string.Concat(runes);
        }

        private static string NormalizeWhitespace(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            var pendingSpace = false;
            var leadingSpace = false;
            foreach (var rune in value.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune))
                {
                    if (builder.Length == 0)
                    {
                        leadingSpace = true;
                    }
                    else
                    {
                        pendingSpace = true;
                    }

                    continue;
                }

                if (leadingSpace || pendingSpace)
                {
                    builder.Append(' ');
                    leadingSpace = false;
                    pendingSpace = false;
                }

                builder.Append(rune);
            }

            if (pendingSpace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            if (builder.Length == 0 && leadingSpace)
            {
                return " ";
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Holds one mutable presentation node until projection construction completes.
    /// </summary>
    internal sealed class MutableNode(string kind)
    {
        /// <summary>
        /// Gets the safe presentation-node kind.
        /// </summary>
        internal string Kind { get; } = kind;

        /// <summary>
        /// Gets the mutable text leaf content.
        /// </summary>
        internal StringBuilder Text { get; } = new();

        /// <summary>
        /// Gets the child nodes for formatted containers.
        /// </summary>
        internal List<MutableNode> Children { get; } = [];

        /// <summary>
        /// Converts this node and its valid descendants into the public immutable contract.
        /// </summary>
        /// <returns>The immutable node, or <see langword="null"/> when no visible content remains.</returns>
        internal DocsSearchSummaryPresentationNode? ToImmutable()
        {
            if (Children.Count > 0)
            {
                var children = Children
                    .Select(child => child.ToImmutable())
                    .Where(child => child is not null)
                    .Select(child => child!)
                    .ToArray();
                return children.Length == 0 ? null : new DocsSearchSummaryPresentationNode(Kind, Children: children);
            }

            var text = Text.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : new DocsSearchSummaryPresentationNode(Kind, Text: text);
        }
    }
}
