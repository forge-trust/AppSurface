using System.Net;
using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Docs.Models;
using Markdig;
using Markdig.Extensions.CustomContainers;
using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Registers the bounded rich-authoring directives that AppSurface Docs owns.
/// </summary>
/// <remarks>
/// The extension deliberately builds on Markdig's fenced custom-container parser. The harvester first recognizes valid
/// tabs and emits their complete server-rendered baseline; this extension then renders callouts, preserves generic
/// custom containers, and leaves invalid rich directives visible as source markers with their body intact instead of
/// guessing them into an interactive component.
/// </remarks>
internal sealed class AppSurfaceDocsRichAuthoringMarkdownExtension : IMarkdownExtension
{
    /// <inheritdoc />
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        pipeline.UseCustomContainers();
    }

    /// <inheritdoc />
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(renderer);

        if (renderer is not HtmlRenderer htmlRenderer)
        {
            return;
        }

        var existingRenderer = htmlRenderer.ObjectRenderers
            .OfType<HtmlCustomContainerRenderer>()
            .SingleOrDefault();
        if (existingRenderer is not null)
        {
            htmlRenderer.ObjectRenderers.Remove(existingRenderer);
        }

        htmlRenderer.ObjectRenderers.Add(new AppSurfaceDocsRichAuthoringRenderer());
    }
}

/// <summary>
/// Renders AppSurface Docs' package-owned rich-authoring custom containers.
/// </summary>
internal sealed class AppSurfaceDocsRichAuthoringRenderer : HtmlObjectRenderer<CustomContainer>
{
    /// <inheritdoc />
    protected override void Write(HtmlRenderer renderer, CustomContainer obj)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(obj);

        var validation = AppSurfaceDocsRichAuthoringSyntax.Validate(obj);
        if (validation.Kind == AppSurfaceDocsRichAuthoringKind.None)
        {
            WriteGenericContainer(renderer, obj);
            return;
        }

        if (!validation.IsValid)
        {
            WriteLiteralFallback(renderer, obj);
            return;
        }

        switch (validation.Kind)
        {
            case AppSurfaceDocsRichAuthoringKind.Callout:
                WriteCallout(renderer, obj, validation.CalloutKind!);
                break;
            case AppSurfaceDocsRichAuthoringKind.Tabs:
                WriteTabs(renderer, obj, validation);
                break;
            default:
                WriteLiteralFallback(renderer, obj);
                break;
        }
    }

    private static void WriteGenericContainer(HtmlRenderer renderer, CustomContainer obj)
    {
        renderer.EnsureLine()
            .Write("<div")
            .WriteAttributes(obj)
            .Write(">");
        renderer.WriteChildren(obj);
        renderer.EnsureLine()
            .Write("</div>")
            .EnsureLine();
    }

    private static void WriteCallout(HtmlRenderer renderer, CustomContainer obj, string calloutKind)
    {
        var label = AppSurfaceDocsRichAuthoringSyntax.GetCalloutLabel(calloutKind);
        renderer.EnsureLine()
            .Write("<section class=\"docs-rich-callout docs-rich-callout--")
            .Write(calloutKind)
            .Write("\" data-appsurfacedocs-rich=\"callout\" role=\"note\" aria-label=\"")
            .WriteEscape(label)
            .Write("\">")
            .EnsureLine()
            .Write("<p class=\"docs-rich-callout__label\">")
            .WriteEscape(label)
            .Write("</p>")
            .EnsureLine()
            .Write("<div class=\"docs-rich-callout__body\">")
            .EnsureLine();
        renderer.WriteChildren(obj);
        renderer.EnsureLine()
            .Write("</div>")
            .EnsureLine()
            .Write("</section>")
            .EnsureLine();
    }

    private static void WriteTabs(
        HtmlRenderer renderer,
        CustomContainer obj,
        AppSurfaceDocsRichAuthoringValidation validation)
    {
        var identifier = $"docs-rich-tabs-{obj.Line + 1}";
        renderer.EnsureLine()
            .Write("<section class=\"docs-rich-tabs\" data-appsurfacedocs-rich=\"tabs\" data-appsurfacedocs-rich-tabs=\"true\">")
            .EnsureLine()
            .Write("<p class=\"docs-rich-tabs__prompt\" id=\"")
            .Write(identifier)
            .Write("-prompt\">")
            .WriteEscape(validation.Prompt!)
            .Write("</p>")
            .EnsureLine()
            .Write("<p class=\"docs-rich-tabs__baseline\" data-appsurfacedocs-rich-tabs-baseline=\"true\">All paths are available below.</p>")
            .EnsureLine();

        var panels = obj.OfType<CustomContainer>().ToArray();
        for (var index = 0; index < panels.Length; index++)
        {
            var panel = panels[index];
            var label = validation.PanelLabels[index];
            renderer.EnsureLine()
                .Write("<section class=\"docs-rich-tabs__panel\" data-appsurfacedocs-rich-tab-panel=\"true\" data-appsurfacedocs-rich-tab-label=\"")
                .WriteEscape(label)
                .Write("\">")
                .EnsureLine()
                .Write("<h3 class=\"docs-rich-tabs__panel-title\">")
                .WriteEscape(label)
                .Write("</h3>")
                .EnsureLine();
            renderer.WriteChildren(panel);
            renderer.EnsureLine()
                .Write("</section>")
                .EnsureLine();
        }

        renderer.Write("</section>").EnsureLine();
    }

    private static void WriteLiteralFallback(HtmlRenderer renderer, CustomContainer obj)
    {
        var name = AppSurfaceDocsRichAuthoringSyntax.GetDirectiveName(obj);
        var arguments = AppSurfaceDocsRichAuthoringSyntax.GetDirectiveArguments(obj);
        renderer.EnsureLine().Write("<p class=\"docs-rich-source\"><code>:::").WriteEscape(name);
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            renderer.Write(" ").WriteEscape(arguments);
        }

        renderer.Write("</code></p>").EnsureLine();
        renderer.WriteChildren(obj);
        if (obj.ClosingFencedCharCount > 0)
        {
            renderer.EnsureLine().Write("<p class=\"docs-rich-source\"><code>:::</code></p>").EnsureLine();
        }
    }
}

/// <summary>
/// Validates the deliberately small rich-authoring directive grammar and produces author diagnostics.
/// </summary>
internal static class AppSurfaceDocsRichAuthoringSyntax
{
    private const int MaximumPromptRunes = 160;
    private const int MaximumLabelRunes = 80;

    /// <summary>
    /// Gets the largest directive nesting level converted to Markdig fences in one document.
    /// </summary>
    /// <remarks>
    /// This bound keeps malformed source such as a long run of unclosed directive openings readable without allocating
    /// progressively larger fence strings. Content beyond the bound remains literal source until its matching closes.
    /// </remarks>
    internal const int MaximumNormalizedDirectiveNestingDepth = 16;

    /// <summary>
    /// Normalizes package directives to Markdig's generic custom-container fence shape without changing authored code.
    /// </summary>
    /// <param name="markdown">The Markdown source about to enter the Markdig pipeline.</param>
    /// <returns>Markdown whose supported directive opening fences have the parser-required separator.</returns>
    /// <remarks>
    /// Markdig's generic custom-container extension recognizes <c>::: callout</c>, while AppSurface Docs deliberately
    /// exposes the compact <c>:::callout</c> authoring grammar. The conversion is limited to actual Markdown lines and
    /// skips fenced code so syntax examples continue to render as literal code.
    /// </remarks>
    internal static string NormalizeDirectiveFences(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (!markdown.Contains(":::", StringComparison.Ordinal))
        {
            return markdown;
        }

        var lines = markdown.Split('\n');
        var inCodeFence = false;
        char codeFenceCharacter = default;
        var codeFenceLength = 0;
        var richFenceDepth = 0;
        var literalOverflowDepth = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart(' ', '\t');
            if (TryGetCodeFence(trimmed, out var fenceCharacter, out var fenceLength))
            {
                if (!inCodeFence)
                {
                    inCodeFence = true;
                    codeFenceCharacter = fenceCharacter;
                    codeFenceLength = fenceLength;
                }
                else if (fenceCharacter == codeFenceCharacter && fenceLength >= codeFenceLength)
                {
                    inCodeFence = false;
                }

                continue;
            }

            if (!inCodeFence && TryParseDirectiveOpening(line, out var indent, out var directive, out var arguments))
            {
                if (literalOverflowDepth > 0 || richFenceDepth >= MaximumNormalizedDirectiveNestingDepth)
                {
                    literalOverflowDepth++;
                    continue;
                }

                lines[index] = $"{indent}{new string(':', 3 + richFenceDepth)} {{.appsurface-rich-{directive} data-appsurface-rich-argument=\"{EncodeArgument(arguments)}\"}}";
                richFenceDepth++;
            }
            else if (!inCodeFence && (richFenceDepth > 0 || literalOverflowDepth > 0) && TryParseRichClosing(line, out indent))
            {
                if (literalOverflowDepth > 0)
                {
                    literalOverflowDepth--;
                    continue;
                }

                richFenceDepth--;
                lines[index] = indent + new string(':', 3 + richFenceDepth);
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Replaces valid package tabs with fixed server-rendered HTML before Markdig parses the surrounding Markdown.
    /// </summary>
    /// <param name="markdown">The authored Markdown body.</param>
    /// <param name="sourcePath">The source-relative documentation path used to namespace generated DOM IDs.</param>
    /// <param name="renderPanelMarkdown">Renders one parsed panel body through the package Markdown pipeline.</param>
    /// <returns>A source-safe replacement plan for valid tabs and their complete server-rendered source-order baseline.</returns>
    /// <remarks>
    /// Markdig custom containers intentionally do not nest containers with an identical author-facing fence. AppSurface
    /// Docs keeps the compact uniform <c>:::</c> grammar by recognizing the bounded tabs structure before invoking
    /// Markdig, then delegates each panel's ordinary Markdown back to the same pipeline. Invalid groups are left
    /// untouched so the generic rich-authoring fallback remains visible and diagnostic-producing.
    /// </remarks>
    internal static AppSurfaceDocsRichAuthoringTabsRenderResult RenderValidTabs(
        string markdown,
        string sourcePath,
        Func<string, string> renderPanelMarkdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(renderPanelMarkdown);
        if (!markdown.Contains(":::tabs", StringComparison.OrdinalIgnoreCase))
        {
            return AppSurfaceDocsRichAuthoringTabsRenderResult.Unchanged(markdown);
        }

        var lines = markdown.Split('\n');
        var result = new List<string>(lines.Length);
        var replacements = new List<AppSurfaceDocsRichAuthoringTabsReplacement>();
        var tabsIdentifier = 0;
        var documentIdentifier = GetDocumentIdentifier(sourcePath);
        string? tabsToken = null;
        var inCodeFence = false;
        char codeFenceCharacter = default;
        var codeFenceLength = 0;
        var openSourceDirectives = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart(' ', '\t');
            if (TryGetCodeFence(trimmed, out var fenceCharacter, out var fenceLength))
            {
                if (!inCodeFence)
                {
                    inCodeFence = true;
                    codeFenceCharacter = fenceCharacter;
                    codeFenceLength = fenceLength;
                }
                else if (fenceCharacter == codeFenceCharacter && fenceLength >= codeFenceLength)
                {
                    inCodeFence = false;
                }

                result.Add(line);
                continue;
            }

            if (inCodeFence)
            {
                result.Add(line);
                continue;
            }

            if (TryParseDirectiveOpening(line, out _, out var directive, out var prompt))
            {
                if (directive.Equals("tabs", StringComparison.OrdinalIgnoreCase)
                    && !openSourceDirectives.Contains("tabs", StringComparer.OrdinalIgnoreCase)
                    && TryParseQuotedValue(prompt, MaximumPromptRunes, out var parsedPrompt)
                    && TryReadTabs(lines, index, out var endIndex, out var panels)
                    && panels.Count is >= 2 and <= 4
                    && !panels.Any(panel => !TryParseQuotedValue(panel.Label, MaximumLabelRunes, out _))
                    && panels.Select(panel => panel.Label[1..^1].Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == panels.Count)
                {
                    tabsIdentifier++;
                    tabsToken ??= CreateTabsToken();
                    var placeholder = $"<!--appsurface-rich-tabs-{tabsIdentifier}-{tabsToken}-->";
                    result.Add(placeholder);
                    replacements.Add(new AppSurfaceDocsRichAuthoringTabsReplacement(
                        placeholder,
                        BuildTabsHtml(
                            tabsIdentifier,
                            documentIdentifier,
                            tabsToken,
                            parsedPrompt,
                            panels.Select(panel => new AppSurfaceDocsRichAuthoringTabPanel(
                                panel.Label[1..^1].Trim(),
                                renderPanelMarkdown(string.Join('\n', panel.Lines)))).ToArray())));
                    index = endIndex;
                    continue;
                }

                openSourceDirectives.Add(directive);
                result.Add(line);
                continue;
            }

            if (TryParseRichClosing(line, out _))
            {
                if (openSourceDirectives.Count > 0)
                {
                    openSourceDirectives.RemoveAt(openSourceDirectives.Count - 1);
                }

                result.Add(line);
                continue;
            }

            result.Add(line);
        }

        return new AppSurfaceDocsRichAuthoringTabsRenderResult(
            string.Join('\n', result),
            replacements,
            tabsToken is null ? [] : [tabsToken]);
    }

    internal static AppSurfaceDocsRichAuthoringValidation Validate(CustomContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        var directiveName = GetDirectiveName(container);
        if (directiveName.Equals("callout", StringComparison.OrdinalIgnoreCase))
        {
            if (container.ClosingFencedCharCount == 0)
            {
                return AppSurfaceDocsRichAuthoringValidation.Invalid(
                    AppSurfaceDocsRichAuthoringKind.Callout,
                    "The callout is missing its closing ::: fence.");
            }

            var kind = GetDirectiveArguments(container).Trim().ToLowerInvariant();
            return kind is "note" or "tip" or "warning" or "danger"
                ? AppSurfaceDocsRichAuthoringValidation.Callout(kind)
                : AppSurfaceDocsRichAuthoringValidation.Invalid(
                    AppSurfaceDocsRichAuthoringKind.Callout,
                    "A callout must use one of: note, tip, warning, or danger.");
        }

        if (directiveName.Equals("tabs", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateTabs(container);
        }

        if (directiveName.Equals("tab", StringComparison.OrdinalIgnoreCase))
        {
            return AppSurfaceDocsRichAuthoringValidation.Invalid(
                AppSurfaceDocsRichAuthoringKind.Tab,
                "A tab must be a direct child of a valid :::tabs block.");
        }

        return AppSurfaceDocsRichAuthoringValidation.NotRich();
    }

    internal static IReadOnlyList<DocHarvestDiagnostic> CollectDiagnostics(MarkdownDocument document, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var diagnostics = new List<DocHarvestDiagnostic>();
        foreach (var container in DescendantContainers(document))
        {
            var validation = Validate(container);
            if (validation.IsValid || validation.Kind == AppSurfaceDocsRichAuthoringKind.None)
            {
                continue;
            }

            var code = validation.Kind switch
            {
                AppSurfaceDocsRichAuthoringKind.Callout => DocHarvestDiagnosticCodes.RichAuthoringInvalidCallout,
                AppSurfaceDocsRichAuthoringKind.Tabs => DocHarvestDiagnosticCodes.RichAuthoringInvalidTabs,
                _ => DocHarvestDiagnosticCodes.RichAuthoringInvalidTab
            };
            var line = container.Line + 1;
            diagnostics.Add(new DocHarvestDiagnostic(
                code,
                DocHarvestDiagnosticSeverity.Warning,
                nameof(MarkdownHarvester),
                $"Rich authoring in '{sourcePath}' at line {line} was rendered as literal source: {validation.Problem}",
                "The directive did not match AppSurface Docs' intentionally bounded rich-authoring grammar.",
                "Use the Rich authoring reference for the supported :::callout and :::tabs syntax, then preview the page before publishing."));
        }

        return diagnostics;
    }

    internal static string GetDirectiveName(CustomContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var className = container.TryGetAttributes()?.Classes?
            .FirstOrDefault(value => value.StartsWith("appsurface-rich-", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(className))
        {
            return className["appsurface-rich-".Length..];
        }

        return GetString(container.UnescapedInfo, container.Info).Trim();
    }

    internal static string GetDirectiveArguments(CustomContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        foreach (var property in container.TryGetAttributes()?.Properties ?? [])
        {
            if (property.Key.Equals("data-appsurface-rich-argument", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeArgument(property.Value ?? string.Empty);
            }
        }

        return GetString(container.UnescapedArguments, container.Arguments).Trim();
    }

    internal static string GetCalloutLabel(string calloutKind)
    {
        return calloutKind switch
        {
            "note" => "Note",
            "tip" => "Tip",
            "warning" => "Warning",
            "danger" => "Danger",
            _ => "Note"
        };
    }

    private static AppSurfaceDocsRichAuthoringValidation ValidateTabs(CustomContainer container)
    {
        if (container.ClosingFencedCharCount == 0)
        {
            return AppSurfaceDocsRichAuthoringValidation.Invalid(
                AppSurfaceDocsRichAuthoringKind.Tabs,
                "The tabs block is missing its closing ::: fence.");
        }

        if (!TryParseQuotedValue(GetDirectiveArguments(container), MaximumPromptRunes, out var prompt))
        {
            return AppSurfaceDocsRichAuthoringValidation.Invalid(
                AppSurfaceDocsRichAuthoringKind.Tabs,
                "Tabs require one non-empty quoted prompt no longer than 160 Unicode characters.");
        }

        var children = container.ToArray();
        if (children.Length is < 2 or > 4 || children.Any(child => child is not CustomContainer))
        {
            return AppSurfaceDocsRichAuthoringValidation.Invalid(
                AppSurfaceDocsRichAuthoringKind.Tabs,
                "Tabs must contain two to four direct :::tab blocks and no other direct content.");
        }

        var labels = new List<string>(children.Length);
        var labelsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children.Cast<CustomContainer>())
        {
            if (!GetDirectiveName(child).Equals("tab", StringComparison.OrdinalIgnoreCase)
                || child.ClosingFencedCharCount == 0
                || !TryParseQuotedValue(GetDirectiveArguments(child), MaximumLabelRunes, out var label))
            {
                return AppSurfaceDocsRichAuthoringValidation.Invalid(
                    AppSurfaceDocsRichAuthoringKind.Tabs,
                    "Each tab must have a closing fence and a unique non-empty quoted label no longer than 80 Unicode characters.");
            }

            if (!labelsSeen.Add(label) || ContainsTabs(child))
            {
                return AppSurfaceDocsRichAuthoringValidation.Invalid(
                    AppSurfaceDocsRichAuthoringKind.Tabs,
                    "Tab labels must be unique and tabs cannot be nested inside another tabs block.");
            }

            labels.Add(label);
        }

        return AppSurfaceDocsRichAuthoringValidation.Tabs(prompt, labels);
    }

    private static bool ContainsTabs(ContainerBlock container)
    {
        foreach (var child in container)
        {
            if (child is not CustomContainer nested)
            {
                continue;
            }

            if (GetDirectiveName(nested).Equals("tabs", StringComparison.OrdinalIgnoreCase) || ContainsTabs(nested))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseQuotedValue(string value, int maximumRunes, out string parsed)
    {
        parsed = string.Empty;
        if (value.Length < 2 || value[0] != '\"' || value[^1] != '\"')
        {
            return false;
        }

        var candidate = value[1..^1].Trim();
        if (candidate.Length == 0 || candidate.Contains('\"'))
        {
            return false;
        }

        if (candidate.EnumerateRunes().Count() > maximumRunes)
        {
            return false;
        }

        parsed = candidate;
        return true;
    }

    private static bool TryReadTabs(
        IReadOnlyList<string> lines,
        int openingIndex,
        out int endIndex,
        out List<AppSurfaceDocsRichAuthoringSourcePanel> panels)
    {
        endIndex = openingIndex;
        panels = [];
        List<string>? panelLines = null;
        string? label = null;
        var inCodeFence = false;
        char codeFenceCharacter = default;
        var codeFenceLength = 0;
        var calloutDepth = 0;

        for (var index = openingIndex + 1; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart(' ', '\t');
            if (TryGetCodeFence(trimmed, out var fenceCharacter, out var fenceLength))
            {
                if (!inCodeFence)
                {
                    inCodeFence = true;
                    codeFenceCharacter = fenceCharacter;
                    codeFenceLength = fenceLength;
                }
                else if (fenceCharacter == codeFenceCharacter && fenceLength >= codeFenceLength)
                {
                    inCodeFence = false;
                }

                panelLines?.Add(line);
                continue;
            }

            if (inCodeFence)
            {
                panelLines?.Add(line);
                continue;
            }

            if (TryParseDirectiveOpening(line, out _, out var directive, out var arguments))
            {
                if (directive.Equals("callout", StringComparison.OrdinalIgnoreCase) && panelLines is not null)
                {
                    calloutDepth++;
                    panelLines.Add(line);
                    continue;
                }

                if (directive.Equals("tab", StringComparison.OrdinalIgnoreCase) && calloutDepth == 0)
                {
                    if (panelLines is not null)
                    {
                        return false;
                    }

                    label = arguments;
                    panelLines = [];
                    continue;
                }

                return false;
            }

            if (TryParseRichClosing(line, out _))
            {
                if (calloutDepth > 0)
                {
                    calloutDepth--;
                    panelLines?.Add(line);
                    continue;
                }

                if (panelLines is not null)
                {
                    panels.Add(new AppSurfaceDocsRichAuthoringSourcePanel(label!, panelLines));
                    panelLines = null;
                    label = null;
                    continue;
                }

                endIndex = index;
                return true;
            }

            if (panelLines is null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return false;
                }
            }
            else
            {
                panelLines.Add(line);
            }
        }

        return false;
    }

    private static string BuildTabsHtml(
        int identifier,
        string documentIdentifier,
        string token,
        string prompt,
        IReadOnlyList<AppSurfaceDocsRichAuthoringTabPanel> panels)
    {
        var id = $"docs-rich-tabs-{documentIdentifier}-{identifier}";
        var builder = new StringBuilder();
        builder.Append("<section class=\"docs-rich-tabs\" data-appsurfacedocs-rich=\"tabs\" data-appsurfacedocs-rich-tabs=\"true\" data-appsurfacedocs-rich-tabs-token=\"")
            .Append(token)
            .Append("\">")
            .Append("<p class=\"docs-rich-tabs__prompt\" id=\"").Append(id).Append("-prompt\">")
            .Append(WebUtility.HtmlEncode(prompt)).Append("</p>")
            .Append("<p class=\"docs-rich-tabs__baseline\" data-appsurfacedocs-rich-tabs-baseline=\"true\">All paths are available below.</p>");
        foreach (var panel in panels)
        {
            builder.Append("<section class=\"docs-rich-tabs__panel\" data-appsurfacedocs-rich-tab-panel=\"true\" data-appsurfacedocs-rich-tab-label=\"")
                .Append(WebUtility.HtmlEncode(panel.Label)).Append("\">")
                .Append("<h3 class=\"docs-rich-tabs__panel-title\">").Append(WebUtility.HtmlEncode(panel.Label)).Append("</h3>")
                .Append(panel.Html)
                .Append("</section>");
        }

        return builder.Append("</section>").ToString();
    }

    private static IEnumerable<CustomContainer> DescendantContainers(ContainerBlock parent)
    {
        foreach (var child in parent)
        {
            if (child is not ContainerBlock nested)
            {
                continue;
            }

            if (nested is CustomContainer container)
            {
                yield return container;
            }

            foreach (var descendant in DescendantContainers(nested))
            {
                yield return descendant;
            }
        }
    }

    private static string GetString(StringSlice unescaped, string? fallback)
    {
        return unescaped.IsEmpty ? fallback ?? string.Empty : unescaped.ToString();
    }

    private static bool TryParseDirectiveOpening(
        string line,
        out string indent,
        out string directive,
        out string arguments)
    {
        indent = string.Empty;
        directive = string.Empty;
        arguments = string.Empty;
        var cursor = 0;
        while (cursor < line.Length && cursor < 3 && (line[cursor] == ' ' || line[cursor] == '\t'))
        {
            cursor++;
        }

        if (cursor + 3 > line.Length || !line.AsSpan(cursor).StartsWith(":::", StringComparison.Ordinal))
        {
            return false;
        }

        cursor += 3;
        var directiveStart = cursor;
        while (cursor < line.Length && char.IsLetter(line[cursor]))
        {
            cursor++;
        }

        directive = line[directiveStart..cursor].ToLowerInvariant();
        if (directive is not ("callout" or "tabs" or "tab")
            || (cursor < line.Length && !char.IsWhiteSpace(line[cursor])))
        {
            return false;
        }

        indent = line[..directiveStart][..^3];
        arguments = line[cursor..].Trim();
        return true;
    }

    private static bool TryParseRichClosing(string line, out string indent)
    {
        indent = string.Empty;
        var cursor = 0;
        while (cursor < line.Length && cursor < 3 && (line[cursor] == ' ' || line[cursor] == '\t'))
        {
            cursor++;
        }

        if (cursor + 3 != line.TrimEnd().Length || !line.AsSpan(cursor).StartsWith(":::", StringComparison.Ordinal))
        {
            return false;
        }

        indent = line[..cursor];
        return true;
    }

    private static string EncodeArgument(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string DecodeArgument(string value)
    {
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static bool TryGetCodeFence(string value, out char character, out int length)
    {
        character = default;
        length = 0;
        if (value.Length < 3 || (value[0] != '`' && value[0] != '~'))
        {
            return false;
        }

        character = value[0];
        while (length < value.Length && value[length] == character)
        {
            length++;
        }

        return length >= 3;
    }

    private static string CreateTabsToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static string GetDocumentIdentifier(string sourcePath)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath)).AsSpan(0, 6)).ToLowerInvariant();
    }

    private sealed record AppSurfaceDocsRichAuthoringSourcePanel(string Label, IReadOnlyList<string> Lines);

    private sealed record AppSurfaceDocsRichAuthoringTabPanel(string Label, string Html);
}

/// <summary>
/// Holds valid tabs rendered into placeholders so directive normalization never reprocesses their generated HTML.
/// </summary>
internal sealed record AppSurfaceDocsRichAuthoringTabsRenderResult(
    string Markdown,
    IReadOnlyList<AppSurfaceDocsRichAuthoringTabsReplacement> Replacements,
    IReadOnlyList<string> Tokens)
{
    private const string PlaceholderPrefix = "<!--appsurface-rich-tabs-";

    internal static AppSurfaceDocsRichAuthoringTabsRenderResult Unchanged(string markdown)
    {
        return new AppSurfaceDocsRichAuthoringTabsRenderResult(markdown, [], []);
    }

    internal string ReplacePlaceholders(string normalizedMarkdown)
    {
        ArgumentNullException.ThrowIfNull(normalizedMarkdown);
        if (Replacements.Count == 0)
        {
            return normalizedMarkdown;
        }

        var replacementsByPlaceholder = Replacements.ToDictionary(
            replacement => replacement.Placeholder,
            replacement => replacement.Html,
            StringComparer.Ordinal);
        var builder = new StringBuilder(normalizedMarkdown.Length);
        var cursor = 0;
        while (cursor < normalizedMarkdown.Length)
        {
            var placeholderStart = normalizedMarkdown.IndexOf(PlaceholderPrefix, cursor, StringComparison.Ordinal);
            if (placeholderStart < 0)
            {
                builder.Append(normalizedMarkdown, cursor, normalizedMarkdown.Length - cursor);
                break;
            }

            var placeholderEnd = normalizedMarkdown.IndexOf("-->", placeholderStart, StringComparison.Ordinal);
            if (placeholderEnd < 0)
            {
                builder.Append(normalizedMarkdown, cursor, normalizedMarkdown.Length - cursor);
                break;
            }

            var placeholderLength = placeholderEnd + 3 - placeholderStart;
            var placeholder = normalizedMarkdown.Substring(placeholderStart, placeholderLength);
            if (!replacementsByPlaceholder.TryGetValue(placeholder, out var html))
            {
                builder.Append(normalizedMarkdown, cursor, placeholderEnd + 3 - cursor);
                cursor = placeholderEnd + 3;
                continue;
            }

            builder.Append(normalizedMarkdown, cursor, placeholderStart - cursor);
            builder.Append(html);
            cursor = placeholderEnd + 3;
        }

        return builder.ToString();
    }
}

/// <summary>
/// Associates one source-safe tabs placeholder with its generated server HTML.
/// </summary>
internal sealed record AppSurfaceDocsRichAuthoringTabsReplacement(string Placeholder, string Html);

/// <summary>
/// Describes the result of validating one custom container against the rich-authoring grammar.
/// </summary>
internal sealed record AppSurfaceDocsRichAuthoringValidation(
    bool IsValid,
    AppSurfaceDocsRichAuthoringKind Kind,
    string? CalloutKind,
    string? Prompt,
    IReadOnlyList<string> PanelLabels,
    string? Problem)
{
    internal static AppSurfaceDocsRichAuthoringValidation NotRich() => new(false, AppSurfaceDocsRichAuthoringKind.None, null, null, [], null);

    internal static AppSurfaceDocsRichAuthoringValidation Callout(string kind) => new(true, AppSurfaceDocsRichAuthoringKind.Callout, kind, null, [], null);

    internal static AppSurfaceDocsRichAuthoringValidation Tabs(string prompt, IReadOnlyList<string> labels) => new(true, AppSurfaceDocsRichAuthoringKind.Tabs, null, prompt, labels, null);

    internal static AppSurfaceDocsRichAuthoringValidation Invalid(AppSurfaceDocsRichAuthoringKind kind, string problem) => new(false, kind, null, null, [], problem);
}

/// <summary>
/// Identifies a package-owned rich-authoring directive family.
/// </summary>
internal enum AppSurfaceDocsRichAuthoringKind
{
    None,
    Callout,
    Tabs,
    Tab
}
