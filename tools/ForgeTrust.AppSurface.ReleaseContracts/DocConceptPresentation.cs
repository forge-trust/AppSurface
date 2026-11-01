using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.ReleaseContracts;

/// <summary>Identifies the exact destination to which inert transition records belong.</summary>
internal sealed record DocConceptDestination(string Instance, string Version, string Pin, string? Locale, string CanonicalRoute);

/// <summary>One caller-validated version link; the URL is projected from verified physical facts.</summary>
internal sealed record DocConceptChoiceLink(string Version, string Label, string Href, IReadOnlyList<string>? AnchorIds = null);

/// <summary>One validated incoming transition; no field originates from a request-supplied URL or notice.</summary>
internal sealed record DocConceptIncomingView(
    string SourceKey, string Reason, string Notice, string ReturnUrl,
    string SourceVersion, string SourceTitle, DocConceptDestination Target);

/// <summary>A page-local view projected by a caller from the shared immutable identity index.</summary>
internal sealed record DocConceptGuideView(
    DocConceptDestination Target, string Title, string SourceKey, string ChoicePageUrl,
    string ArchiveUrl, string RuntimeUrl, string StyleUrl,
    IReadOnlyList<DocConceptChoiceLink> Choices, IEnumerable<DocConceptIncomingView> Incoming, string? PageUrl = null);

/// <summary>A verified recovery action; labels distinguish browsing from documented counterparts.</summary>
internal sealed record DocConceptAction(string Label, string Href);

/// <summary>One target release's complete, honest counterpart or recovery explanation.</summary>
internal sealed record DocConceptChoiceView(
    string Version, string LocaleLabel, string Heading, string Explanation,
    IReadOnlyList<DocConceptAction> Actions, string ReturnUrl);

/// <summary>Separates small encoded projection output from caller-owned DOM and HTTP policy handling.</summary>
internal sealed record DocConceptGuideMarkup(string Head, string Body, int Utf8Bytes);

/// <summary>Signals whole-artifact rejection; callers select their approved hosted or publication failure behavior.</summary>
internal sealed class DocConceptArtifactOverflowException : IOException
{
    /// <summary>Creates an overflow with safe byte counts and no source or secret content.</summary>
    internal DocConceptArtifactOverflowException(long observedBytes, int maximumBytes)
        : base($"Complete switch artifact exceeds {maximumBytes} UTF-8 bytes (observed at least {observedBytes}).")
    {
        ObservedBytes = observedBytes;
        MaximumBytes = maximumBytes;
    }

    /// <summary>Gets the lower bound observed before the writer rejected the whole artifact.</summary>
    internal long ObservedBytes { get; }

    /// <summary>Gets the unchanged caller-supplied artifact budget.</summary>
    internal int MaximumBytes { get; }
}

/// <summary>Encodes small switch views shared by hosted Docs and static publication.</summary>
/// <remarks>
/// This BCL-only projection owns text/JSON encoding and the complete added-artifact byte budget. Callers own verified
/// URL projection, source reads, HTML parsing/insertion, CSP and output publication. It never reads a file or builds a
/// global transition matrix. The exact build-owned runtime integrity value is supplied by the caller's asset contract.
/// </remarks>
internal static class DocConceptPresentation
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Builds bounded head/body fragments; no partial result is returned on overflow.</summary>
    internal static DocConceptGuideMarkup Guide(DocConceptGuideView view, string integrity, int maximumBytes, string? incomingKey = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        var writer = new BudgetWriter(maximumBytes);
        writer.Add($"<link rel=\"stylesheet\" href=\"{Url(view.StyleUrl)}\" data-docs-version-style>" +
                   $"<script src=\"{Url(view.RuntimeUrl)}\" integrity=\"{Text(integrity)}\" crossorigin=\"anonymous\" defer data-docs-switch-runtime></script>");
        var head = writer.Contents;
        writer.ClearContents();
        var id = "docs-version-options-" + Key(view.SourceKey);
        writer.Add($"<section class=\"docs-version-switch\" data-docs-version-switch aria-label=\"Documentation version\"><div class=\"docs-version-identity\"><span>{Text(view.Target.Version)}</span><span>{Text(Locale(view.Target.Locale))}</span></div>");
        if (view.Choices.Count > 0)
        {
            writer.Add($"<a href=\"{Url(view.ChoicePageUrl)}#docs-switch-choices\" data-docs-version-fallback>Change version</a>");
            writer.Add($"<button type=\"button\" hidden data-docs-version-toggle aria-expanded=\"false\" aria-controls=\"{id}\">Change version</button><div id=\"{id}\" data-docs-version-options hidden><ul>");
            foreach (var choice in view.Choices)
            {
                var anchors = choice.AnchorIds is null ? string.Empty : $" data-docs-version-anchor-ids=\"{Text(Json(choice.AnchorIds))}\"";
                writer.Add($"<li><a href=\"{Url(choice.Href)}\"{anchors}>{Text(choice.Label)}</a></li>");
            }
            writer.Add($"<li><a href=\"{Url(view.ChoicePageUrl)}#docs-switch-choices\">All version choices</a></li></ul></div>");
        }
        else
        {
            writer.Add("<span>No other versions available</span>");
        }

        writer.Add($"<a href=\"{Url(view.ArchiveUrl)}\" class=\"docs-version-archive\">Version archive</a>");
        writer.Add("<script type=\"application/json\" data-docs-version-data>{\"sourceKey\":" + Json(Key(view.SourceKey)) +
                   ",\"version\":" + Json(view.Target.Version) + ",\"locale\":" + Json(view.Target.Locale) +
                   ",\"pageUrl\":" + Json(view.PageUrl is null ? null : VerifiedUrl(view.PageUrl)) +
                   ",\"target\":" + Json(view.Target) + ",\"incoming\":[");
        var first = true;
        DocConceptIncomingView? selected = null;
        foreach (var incoming in view.Incoming)
        {
            // Refuse to encode a caller's accidentally cross-bound row, even though the query is never authority.
            if (incoming.Target != view.Target) throw new ArgumentException("Incoming transition belongs to another destination.", nameof(view));
            _ = Url(incoming.ReturnUrl);
            _ = Key(incoming.SourceKey);
            writer.Add((first ? string.Empty : ",") + Json(incoming));
            if (string.Equals(incoming.SourceKey, incomingKey, StringComparison.Ordinal)) selected = incoming;
            first = false;
        }
        writer.Add("]}</script></section>");
        if (selected is not null && selected.Notice.Length > 0)
        {
            writer.Add(Notice(selected));
        }
        return new DocConceptGuideMarkup(head, writer.Contents, writer.Bytes);
    }

    /// <summary>Encodes a complete no-JavaScript choice page, including the caller's already-validated policy markup.</summary>
    /// <remarks>Entry links target the focusable heading fragment because a hosted sandbox blocks automatic focus.</remarks>
    internal static string Choices(
        string sourceTitle, string sourceVersion, string sourceUrl, string archiveUrl, string styleUrl,
        IEnumerable<DocConceptChoiceView> choices, int maximumBytes, string headPolicy = "")
    {
        var writer = new BudgetWriter(maximumBytes);
        writer.Add("<!DOCTYPE html><html lang=\"en\"><head>" + headPolicy +
                   "<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                   $"<title>Version choices: {Text(sourceTitle)}</title><link rel=\"stylesheet\" href=\"{Url(styleUrl)}\"></head><body><main class=\"docs-version-choice\"><p>{Text(sourceVersion)}</p><h1 id=\"docs-switch-choices\" tabindex=\"-1\" autofocus>Choose a version of {Text(sourceTitle)}</h1>");
        foreach (var choice in choices)
        {
            writer.Add($"<section><p>{Text(choice.Version)} · {Text(choice.LocaleLabel)}</p><h2 id=\"{ChoiceAnchor(choice.Version)}\" tabindex=\"-1\">{Text(choice.Heading)}</h2><p>{Text(choice.Explanation)}</p><ul>");
            foreach (var action in choice.Actions)
            {
                writer.Add($"<li><a href=\"{Url(action.Href)}\">{Text(action.Label)}</a></li>");
            }
            writer.Add($"</ul><p><a href=\"{Url(choice.ReturnUrl)}\">Return to original guide</a></p></section>");
        }
        writer.Add($"<p><a href=\"{Url(sourceUrl)}\">Return to original guide</a> · <a href=\"{Url(archiveUrl)}\">Version archive</a></p></main></body></html>");
        return writer.Contents;
    }

    /// <summary>Creates complete bounded generic recovery markup, or returns null when the whole artifact cannot fit.</summary>
    internal static string? Recovery(string archiveUrl, string? returnUrl, int maximumBytes, bool standalone)
    {
        try
        {
            var writer = new BudgetWriter(maximumBytes);
            if (standalone) writer.Add("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Version switching unavailable</title></head><body><main>");
            writer.Add("<aside data-docs-version-unavailable><p>Version switching is unavailable for this guide.</p>");
            writer.Add($"<a href=\"{Url(archiveUrl)}\">Version archive</a>");
            if (returnUrl is not null) writer.Add($" <a href=\"{Url(returnUrl)}\">Return to original guide</a>");
            writer.Add("</aside>");
            if (standalone) writer.Add("</main></body></html>");
            return writer.Contents;
        }
        catch (DocConceptArtifactOverflowException) { return null; }
    }

    /// <summary>Returns a stable safe fragment for a target's reason heading on the complete choice page.</summary>
    internal static string ChoiceAnchor(string version) => "docs-switch-version-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version))).ToLowerInvariant()[..16];

    /// <summary>Encodes one previously validated transition explanation without executable markup.</summary>
    internal static string Notice(DocConceptIncomingView incoming) =>
        $"<aside class=\"docs-version-notice\" data-docs-version-notice><p id=\"docs-version-explanation\">{Text(incoming.Notice)}</p><p>{Text(incoming.SourceTitle)} · {Text(incoming.SourceVersion)} <a href=\"{Url(incoming.ReturnUrl)}\">Return to original guide</a></p></aside>";

    private static string Text(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static string Locale(string? locale) => string.IsNullOrEmpty(locale) ? "Default language" : locale;

    private static string Url(string value)
        => Text(VerifiedUrl(value));

    private static string VerifiedUrl(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)
            || value.Any(char.IsControl) || value.Contains('\\'))
            throw new ArgumentException("Switch destinations must be validated app-relative URLs.", nameof(value));
        return value;
    }

    private static string Key(string value)
    {
        if (value.Length != 64 || !value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'))
            throw new ArgumentException("Source key must be a normalized SHA-256 lookup key.", nameof(value));
        return value;
    }

    private sealed class BudgetWriter
    {
        private readonly int _limit;
        private readonly StringBuilder _contents = new();

        internal BudgetWriter(int limit)
        {
            if (!DocConceptArtifactBudget.IsValidLimit(limit)) throw new ArgumentOutOfRangeException(nameof(limit));
            _limit = limit;
        }

        internal int Bytes { get; private set; }
        internal string Contents => _contents.ToString();
        internal void ClearContents() => _contents.Clear();

        internal void Add(string value)
        {
            var bytes = Encoding.UTF8.GetByteCount(value);
            if ((long)Bytes + bytes > _limit) throw new DocConceptArtifactOverflowException((long)Bytes + bytes, _limit);
            Bytes += bytes;
            _contents.Append(value);
        }
    }
}
