using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

internal sealed record NamespaceEntryPointPanelRenderResult(
    string Content,
    IReadOnlyList<DocHarvestDiagnostic> Diagnostics);

internal static class NamespaceEntryPointPanelRenderer
{
    private const string NamespaceIntroClass = "doc-namespace-intro";
    private const string NamespaceGroupsClass = "doc-namespace-groups";
    private const string EntryPointsClass = "doc-namespace-entry-points";

    private static readonly Regex AnchorIdRegex = new(
        """<(?<tag>section|details|article)\b(?=[^>]*\bid\s*=\s*(?<quote>["'])(?<id>[^"']+)\k<quote>)(?=[^>]*\bclass\s*=\s*(?<classQuote>["'])(?<class>[^"']*)\k<classQuote>)[^>]*>""",
        RegexOptions.IgnoreCase);

    internal static NamespaceEntryPointPanelRenderResult Render(
        string namespaceName,
        string namespaceContent,
        IReadOnlyList<DocOutlineItem>? outline,
        IReadOnlyList<DocNamespaceEntryPoint>? entryPoints)
    {
        if ((entryPoints?.Count ?? 0) == 0)
        {
            return new NamespaceEntryPointPanelRenderResult(namespaceContent, []);
        }

        var anchors = BuildAnchorSet(namespaceContent, outline);
        var diagnostics = new List<DocHarvestDiagnostic>();
        var renderedEntries = entryPoints!
            .OrderBy(entry => entry.Order is null ? 1 : 0)
            .ThenBy(entry => entry.Order ?? int.MaxValue)
            .ThenBy(entry => entry.SourceIndex)
            .Select(entry => RenderEntry(namespaceName, entry, anchors, diagnostics))
            .Where(entry => !string.IsNullOrEmpty(entry))
            .ToArray();

        if (renderedEntries.Length == 0)
        {
            return new NamespaceEntryPointPanelRenderResult(namespaceContent, diagnostics);
        }

        var panel = new StringBuilder();
        panel.Append("<section class=\"");
        panel.Append(EntryPointsClass);
        panel.Append("\" aria-labelledby=\"common-entry-points\">");
        panel.Append("<h2 id=\"common-entry-points\">Common entry points</h2>");
        panel.Append("<ul>");
        foreach (var renderedEntry in renderedEntries)
        {
            panel.Append(renderedEntry);
        }

        panel.Append("</ul>");
        panel.Append("</section>");

        return new NamespaceEntryPointPanelRenderResult(
            InsertPanel(namespaceContent, panel.ToString()),
            diagnostics);
    }

    private static string RenderEntry(
        string namespaceName,
        DocNamespaceEntryPoint entry,
        HashSet<string> anchors,
        List<DocHarvestDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(entry.Label))
        {
            return string.Empty;
        }

        var label = WebUtility.HtmlEncode(entry.Label.Trim());
        var summary = string.IsNullOrWhiteSpace(entry.Summary) ? null : WebUtility.HtmlEncode(entry.Summary.Trim());
        var unresolvedTarget = false;
        var href = ResolveHref(namespaceName, entry, anchors, diagnostics, out unresolvedTarget);
        var body = new StringBuilder();
        body.Append("<span class=\"doc-namespace-entry-point-label\"><code>");
        body.Append(label);
        body.Append("</code></span>");
        if (summary is not null)
        {
            body.Append("<span class=\"doc-namespace-entry-point-summary\">");
            body.Append(summary);
            body.Append("</span>");
        }

        if (href is null && unresolvedTarget)
        {
            body.Append("<span class=\"doc-namespace-entry-point-status\">Target unavailable</span>");
            return $"<li class=\"doc-namespace-entry-point doc-namespace-entry-point--unresolved\">{body}</li>";
        }

        if (href is null)
        {
            return $"<li class=\"doc-namespace-entry-point doc-namespace-entry-point--text\">{body}</li>";
        }

        return
            $"<li class=\"doc-namespace-entry-point\"><a href=\"{WebUtility.HtmlEncode(href)}\">{body}</a></li>";
    }

    private static string? ResolveHref(
        string namespaceName,
        DocNamespaceEntryPoint entry,
        HashSet<string> anchors,
        List<DocHarvestDiagnostic> diagnostics,
        out bool unresolvedTarget)
    {
        unresolvedTarget = false;
        if (!string.IsNullOrWhiteSpace(entry.Target))
        {
            var target = entry.Target.Trim();
            if (anchors.Contains(target))
            {
                return "#" + target;
            }

            diagnostics.Add(
                new DocHarvestDiagnostic(
                    DocHarvestDiagnosticCodes.NamespaceEntryPointTargetUnresolved,
                    DocHarvestDiagnosticSeverity.Warning,
                    HarvesterType: null,
                    $"Namespace entry point '{entry.Label}' could not resolve target '{target}'.",
                    $"The namespace README for '{namespaceName}' references a generated anchor that is not present on the merged namespace page.",
                    "Update the entry_points target to a generated namespace-page anchor, or remove the target until the API exists."));
            unresolvedTarget = true;
            return null;
        }

        return string.IsNullOrWhiteSpace(entry.Href) ? null : entry.Href.Trim();
    }

    private static HashSet<string> BuildAnchorSet(string content, IReadOnlyList<DocOutlineItem>? outline)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outlineItem in (outline ?? []).Where(outlineItem => !string.IsNullOrWhiteSpace(outlineItem.Id)))
        {
            anchors.Add(WebUtility.HtmlDecode(outlineItem.Id.Trim()));
        }

        foreach (Match match in AnchorIdRegex.Matches(content))
        {
            var classes = match.Groups["class"].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!classes.Any(IsGeneratedApiAnchorClass))
            {
                continue;
            }

            var id = WebUtility.HtmlDecode(match.Groups["id"].Value).Trim();
            if (id.Length > 0)
            {
                anchors.Add(id);
            }
        }

        return anchors;
    }

    private static bool IsGeneratedApiAnchorClass(string cssClass)
    {
        return cssClass.Equals("doc-type", StringComparison.Ordinal)
               || cssClass.Equals("doc-method-group", StringComparison.Ordinal)
               || cssClass.Equals("doc-property", StringComparison.Ordinal)
               || cssClass.Equals("doc-enum", StringComparison.Ordinal);
    }

    private static string InsertPanel(string content, string panel)
    {
        var introEnd = FindSectionEndByClass(content, NamespaceIntroClass);
        if (introEnd >= 0)
        {
            return content.Insert(introEnd, panel);
        }

        var groupsEnd = FindSectionEndByClass(content, NamespaceGroupsClass);
        if (groupsEnd >= 0)
        {
            return content.Insert(groupsEnd, panel);
        }

        var generatedStart = FindGeneratedApiSectionStart(content);
        return generatedStart >= 0
            ? content.Insert(generatedStart, panel)
            : content + panel;
    }

    private static int FindSectionEndByClass(string content, string cssClass)
    {
        var classIndex = content.IndexOf(cssClass, StringComparison.Ordinal);
        if (classIndex < 0)
        {
            return -1;
        }

        var sectionStart = content.LastIndexOf("<section", classIndex, StringComparison.OrdinalIgnoreCase);
        if (sectionStart < 0)
        {
            return -1;
        }

        var sectionEnd = FindMatchingSectionEnd(content, sectionStart);
        return sectionEnd < 0 ? -1 : sectionEnd + "</section>".Length;
    }

    private static int FindGeneratedApiSectionStart(string content)
    {
        var typeIndex = content.IndexOf("doc-type", StringComparison.Ordinal);
        var methodIndex = content.IndexOf("doc-method-group", StringComparison.Ordinal);
        var markerIndex = typeIndex < 0 ? methodIndex : methodIndex < 0 ? typeIndex : Math.Min(typeIndex, methodIndex);
        return markerIndex < 0
            ? -1
            : content.LastIndexOf("<section", markerIndex, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindMatchingSectionEnd(string content, int sectionStart)
    {
        var depth = 0;
        var cursor = sectionStart;

        while (cursor < content.Length)
        {
            var nextOpen = content.IndexOf("<section", cursor, StringComparison.OrdinalIgnoreCase);
            var nextClose = content.IndexOf("</section>", cursor, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0)
            {
                return -1;
            }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                depth++;
                var openEnd = content.IndexOf('>', nextOpen);
                if (openEnd < 0 || openEnd > nextClose)
                {
                    return -1;
                }

                cursor = openEnd + 1;
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return nextClose;
            }

            cursor = nextClose + "</section>".Length;
        }

        return -1;
    }
}
