using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.RazorWire.Cli;

public partial class ExportEngine
{
    private const string RecommendationDiagnosticCode = "RWEXPORT011";
    private const string WayfindingReference = "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md#recommended-next-reads";

    private static readonly HashSet<string> ReservedRecommendationRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "search", "search-index.json", "search.css", "search-client.js", "outline-client.js", "rich-authoring-client.js",
        "minisearch.min.js", "_harvest", "_health", "_health.json", "_routes", "_routes.json", "_search-index", "_markdown",
        "v", "versions", "next", "sections", "search-index"
    };

    private sealed record RecommendationReference(JsonObject Row, ExportReference Reference, string Location);

    /// <summary>
    /// Parses complete next-read rows and schedules their targets, including pages absent from the search corpus. The
    /// original complete URL is checked before normalization; queries are discarded and approved fragments retained.
    /// The exporter owns this wire boundary and deliberately has no dependency on the Docs package.
    /// </summary>
    private static IReadOnlyList<RecommendationReference> ReadRecommendationReferences(
        JsonObject root, string route, ExportContext context)
    {
        var references = new List<RecommendationReference>();
        if (root["documents"] is not JsonArray documents) return references;
        var sourceRoot = route[..^"/search-index.json".Length].TrimEnd('/');
        for (var i = 0; i < documents.Count; i++)
        {
            if (documents[i] is not JsonObject document || !document.TryGetPropertyValue("recommendedPages", out var recommendationNode)) continue;
            var location = $"documents[{i}].recommendedPages";
            if (recommendationNode is not JsonArray)
                ThrowRecommendationFailure(context, route, location, "expected an array");
            // The throw helper never returns, but pattern-local definite assignment is intentionally explicit.
            var recommendations = (JsonArray)recommendationNode!;
            for (var j = 0; j < recommendations.Count; j++)
            {
                var rowLocation = $"{location}[{j}]";
                if (recommendations[j] is not JsonObject)
                    ThrowRecommendationFailure(context, route, rowLocation, "expected an object");
                var row = (JsonObject)recommendations[j]!;
                foreach (var key in new[] { "path", "title" })
                {
                    if (row[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
                        ThrowRecommendationFailure(context, route, rowLocation, $"missing or invalid {key}");
                }
                // Optional presentation metadata is opaque at this boundary. Unknown future fields/values retain
                // otherwise valid navigation; the typed Docs producer verifies its complete row contract separately.
                var original = row["path"]!.GetValue<string>();
                if (!TryValidateRecommendationUrl(original, sourceRoot, out var path, out var fragment))
                    ThrowRecommendationFailure(context, route, rowLocation, "unsafe or outside selected docs tree path (value redacted)");
                references.Add(new RecommendationReference(row,
                    new ExportReference(route, ExportReferenceKind.AnchorHref, ExportReferenceRole.PageRoute,
                        path + fragment, path + fragment, path, string.Empty, fragment,
                        new ExportReferenceProvenance("search-index", null, null, rowLocation, null, null)), rowLocation));
            }
        }
        return references;
    }

    /// <summary>
    /// Validates the original JSON URL before URI normalization can erase dot segments. Retains the existing exporter
    /// origin/escaped-path guards and rejects controls, malformed escapes and single/double encoded path hazards.
    /// </summary>
    private static bool TryValidateRecommendationUrl(string value, string root, out string path, out string fragment)
    {
        path = string.Empty;
        fragment = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl) || value.Contains('\\') ||
            !value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)) return false;
        var suffix = value.IndexOfAny(['?', '#']);
        var originalPath = suffix < 0 ? value : value[..suffix];
        if (!ValidateRecommendationEscapes(value, false) || !ValidateRecommendationEscapes(originalPath, true) ||
            originalPath.Split('/').Any(segment => segment is "." or "..")) return false;
        if (!ExportReferenceProcessor.TrySplitManagedUrl(value, out path, out _, out fragment)) return false;
        if (root.Length > 0 && !(path == root || path.StartsWith(root + "/", StringComparison.Ordinal))) return false;
        var relative = root.Length == 0 ? path.TrimStart('/') : path[root.Length..].TrimStart('/');
        var first = relative.Split('/', 2)[0];
        if (relative.Split('/').Any(segment => segment.StartsWith('.')) ||
            ReservedRecommendationRoutes.Contains(first)) return false;
        return Uri.TryCreate("https://export.invalid" + originalPath, UriKind.Absolute, out var uri) && HasSafeEscapedPathSegments(uri);
    }

    private static bool ValidateRecommendationEscapes(string value, bool path)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return false;
            var decoded = Convert.ToByte(value.Substring(i + 1, 2), 16);
            if (decoded < 32 || decoded == 127 || path && decoded is 46 or 47 or 92) return false;
            if (decoded == '%' && i + 4 < value.Length && Uri.IsHexDigit(value[i + 3]) && Uri.IsHexDigit(value[i + 4]))
            {
                var twice = Convert.ToByte(value.Substring(i + 3, 2), 16);
                if (twice < 32 || twice == 127 || path && twice is 46 or 47 or 92) return false;
            }
            i += 2;
        }
        return true;
    }

    /// <summary>
    /// Rewrites each next-read only after successful same-tree HTML materialization is known. Frozen archives keep
    /// stable /docs-rooted artifact URLs, ordinary static output uses emitted URLs; identities and explanations stay intact.
    /// No partial JSON file is written if a target failed, points off-tree or was not materialized.
    /// </summary>
    private static string RewriteRecommendationIndex(string body, string route, ExportContext context, bool frozenProjection = false)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { ThrowRecommendationFailure(context, route, "documents", "unreadable JSON"); throw; }
        if (root is null) return body;
        var references = ReadRecommendationReferences(root, route, context);
        var sourceRoot = route[..^"/search-index.json".Length].TrimEnd('/');
        foreach (var item in references)
        {
            if (!context.RouteOutcomes.TryGetValue(item.Reference.Path, out var outcome) || !outcome.Succeeded ||
                !outcome.IsHtml || outcome.ArtifactPath is null || outcome.ArtifactUrl is null ||
                outcome.IsRedirectAliasArtifact)
                ThrowRecommendationFailure(context, route, item.Location, "target did not materialize as a canonical HTML artifact");
            if (!File.Exists(outcome.ArtifactPath))
                ThrowRecommendationFailure(context, route, item.Location, "target HTML artifact is missing");
            ExportOutputPathGuards.ValidateArchiveEntryPath(context.OutputPath, outcome.ArtifactPath!, "recommendation-target");
            // The exact archive is the complete export root, including docs.html and docs/* artifacts. The
            // original URL was already constrained to this product's docs route family before being queued.
            var relative = Path.GetRelativePath(context.OutputPath, outcome.ArtifactPath!);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                ThrowRecommendationFailure(context, route, item.Location, "target artifact is outside the selected docs tree");
            item.Row["path"] = (frozenProjection ? ProjectArchiveSearchIndexArtifactUrl(context, outcome.ArtifactUrl!) : outcome.ArtifactUrl!) + item.Reference.Fragment;
        }
        if (context.ReleaseArchiveManifestEnabled && root["documents"] is JsonArray documents)
        {
            foreach (var document in documents.OfType<JsonObject>())
            {
                if (document["path"] is JsonValue pathValue && pathValue.TryGetValue<string>(out var value) &&
                    TryValidateRecommendationUrl(value, sourceRoot, out var path, out var fragment) &&
                    context.RouteOutcomes.TryGetValue(path, out var documentOutcome) && documentOutcome.Succeeded &&
                    documentOutcome.ArtifactUrl is { } documentArtifactUrl)
                    document["path"] = (frozenProjection ? ProjectArchiveSearchIndexArtifactUrl(context, documentArtifactUrl) : documentArtifactUrl) + fragment;
            }
        }
        return root.ToJsonString();
    }

    /// <summary>
    /// Projects emitted artifact URLs into the stable archive root for the frozen search-index JSON contract.
    /// Ordinary HTML, frame, and alias artifacts keep their export-root URLs for generic static hosts.
    /// </summary>
    private static string ProjectArchiveSearchIndexArtifactUrl(ExportContext context, string artifactUrl) =>
        context.ReleaseArchiveManifestEnabled ? "/docs" + artifactUrl : artifactUrl;

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowRecommendationFailure(ExportContext context, string route, string location, string cause)
    {
        context.Diagnostics.Add(new ExportDiagnostic(RecommendationDiagnosticCode,
            $"[recommendation-data] Invalid documentation recommendation at {location}. Cause: {cause}. Fix: correct metadata, re-export every target into the same docs tree, and verify the archive. See {WayfindingReference}.", route));
        throw new ExportValidationException(context.Diagnostics);
    }
}
