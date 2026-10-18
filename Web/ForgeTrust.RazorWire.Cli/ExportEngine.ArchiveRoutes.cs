using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.RazorWire.Cli;

public partial class ExportEngine
{
    private const string AppSurfaceDocsRouteManifestFileName = ".appsurface-docs-route-manifest.json";
    private const string AppSurfaceDocsRouteManifestSchema = "appsurface-docs-route-manifest-v1";
    private const string ArchiveRouteMappingDiagnosticCode = "RWEXPORT012";
    private const string ExportCanonicalLiveUrlPropertyName = "exportCanonicalLiveUrl";
    private const string CanonicalArtifactPathPropertyName = "canonicalArtifactPath";
    private const string HtmlUrlBasePropertyName = "htmlUrlBase";

    private static readonly JsonSerializerOptions ArchiveRouteManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed record StagedArchiveRouteMapping(
        JsonObject Entry,
        string ArtifactPath,
        string CanonicalRoutePath,
        IReadOnlyList<string> AliasPaths,
        int Index);

    /// <summary>
    /// Replaces private route-manifest staging URLs with guarded paths to the ordinary HTML files that were actually
    /// materialized, before the release archive writer binds the final manifest bytes.
    /// </summary>
    private static async Task FinalizeAppSurfaceDocsRouteManifestAsync(
        ExportContext context,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Join(context.OutputPath, AppSurfaceDocsRouteManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        ExportOutputPathGuards.ValidateArchiveEntryPath(
            context.OutputPath,
            manifestPath,
            "archive-route-manifest-read");

        string manifestContents;
        try
        {
            manifestContents = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ThrowArchiveRouteMappingFailure(context, "manifest-unreadable", null);
            throw;
        }

        JsonObject? manifest;
        try
        {
            manifest = JsonNode.Parse(manifestContents) as JsonObject;
        }
        catch (JsonException)
        {
            ThrowArchiveRouteMappingFailure(context, "manifest-unreadable", null);
            throw;
        }

        if (manifest is null || manifest["entries"] is not JsonArray)
        {
            ThrowArchiveRouteMappingFailure(context, "manifest-shape", null);
        }

        var entries = (JsonArray)manifest!["entries"]!;
        var hasStaging = entries.OfType<JsonObject>()
            .Any(entry => entry.ContainsKey(ExportCanonicalLiveUrlPropertyName));
        if (!hasStaging)
        {
            // V1 manifests written before export staging remain byte-for-byte unchanged.
            return;
        }

        if (!TryGetRequiredString(manifest!, "schema", out var schema)
            || !string.Equals(schema, AppSurfaceDocsRouteManifestSchema, StringComparison.Ordinal))
        {
            ThrowArchiveRouteMappingFailure(context, "manifest-schema-unsupported", null);
        }

        if (manifest!.ContainsKey(HtmlUrlBasePropertyName)
            || entries.OfType<JsonObject>().Any(entry => entry.ContainsKey(CanonicalArtifactPathPropertyName)))
        {
            ThrowArchiveRouteMappingFailure(context, "unexpected-finalized-fields", null);
        }

        var mappings = new List<StagedArchiveRouteMapping>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries[index] is not JsonObject)
            {
                ThrowArchiveRouteMappingFailure(context, "staging-invalid", index);
            }

            var entry = (JsonObject)entries[index]!;
            var liveUrl = string.Empty;
            var normalizedCanonicalRoutePath = string.Empty;
            IReadOnlyList<string> recoveryAliases = [];
            IReadOnlyList<string> declaredAliases = [];
            if (!TryGetRequiredString(entry, ExportCanonicalLiveUrlPropertyName, out liveUrl)
                || !TryGetRequiredString(entry, "canonicalRoutePath", out var canonicalRoutePath)
                || !TryNormalizeManifestRoutePath(
                    canonicalRoutePath,
                    allowEmpty: true,
                    out normalizedCanonicalRoutePath)
                || !TryGetManifestAliases(entry, "recoveryAliases", out recoveryAliases)
                || !TryGetManifestAliases(entry, "declaredAliases", out declaredAliases))
            {
                ThrowArchiveRouteMappingFailure(context, "staging-invalid", index);
            }

            if (!TryResolveStagedLiveRoute(liveUrl, out var liveRoute))
            {
                ThrowArchiveRouteMappingFailure(context, "canonical-live-url-invalid", index);
            }

            if (!TryGetOrdinaryHtmlArtifactPath(
                    context,
                    liveRoute,
                    index,
                    out var artifactPath,
                    out var mappingFailureReason))
            {
                ThrowArchiveRouteMappingFailure(context, mappingFailureReason, index);
            }

            var aliasPaths = new List<string>(recoveryAliases.Count + declaredAliases.Count);
            foreach (var aliasPath in recoveryAliases.Concat(declaredAliases))
            {
                if (!TryNormalizeManifestRoutePath(aliasPath, allowEmpty: false, out var normalizedAliasPath))
                {
                    ThrowArchiveRouteMappingFailure(context, "alias-route-invalid", index);
                }

                aliasPaths.Add(normalizedAliasPath);
            }

            mappings.Add(new StagedArchiveRouteMapping(
                entry,
                artifactPath,
                normalizedCanonicalRoutePath,
                aliasPaths,
                index));
        }

        ValidateArchiveRouteNamespaces(context, mappings);

        foreach (var mapping in mappings)
        {
            mapping.Entry.Remove(ExportCanonicalLiveUrlPropertyName);
            mapping.Entry[CanonicalArtifactPathPropertyName] = mapping.ArtifactPath;
        }

        manifest[HtmlUrlBasePropertyName] = "export-root";
        var finalizedContents = manifest.ToJsonString(ArchiveRouteManifestJsonOptions) + "\n";
        await ExportAuthArtifactAuditor.WriteTextArtifactAsync(
            context.OutputPath,
            manifestPath,
            "AppSurface Docs frozen route manifest",
            "/" + AppSurfaceDocsRouteManifestFileName,
            finalizedContents,
            encoding: null,
            cancellationToken);
    }

    private static bool TryGetOrdinaryHtmlArtifactPath(
        ExportContext context,
        string route,
        int entryIndex,
        out string artifactPath,
        out string failureReason)
    {
        artifactPath = string.Empty;
        failureReason = string.Empty;
        if (!context.RouteOutcomes.TryGetValue(route, out var outcome)
            || !outcome.Succeeded
            || !outcome.IsHtml
            || outcome.IsRedirectAliasArtifact
            || string.IsNullOrWhiteSpace(outcome.ArtifactPath))
        {
            failureReason = "canonical-html-unavailable";
            return false;
        }

        ExportOutputPathGuards.ValidateArchiveEntryPath(
            context.OutputPath,
            outcome.ArtifactPath,
            $"archive-route-map-entry-{entryIndex}");
        if (!File.Exists(outcome.ArtifactPath))
        {
            failureReason = "artifact-file-missing";
            return false;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(outcome.ArtifactPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failureReason = "artifact-file-missing";
            return false;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            failureReason = "artifact-file-missing";
            return false;
        }

        var relativePath = Path.GetRelativePath(
                Path.GetFullPath(context.OutputPath),
                Path.GetFullPath(outcome.ArtifactPath))
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        if (!IsSafeCanonicalArtifactPath(relativePath))
        {
            failureReason = "artifact-path-invalid";
            return false;
        }

        artifactPath = relativePath;
        return true;
    }

    private static bool TryResolveStagedLiveRoute(string liveUrl, out string route)
    {
        route = string.Empty;
        if (liveUrl != liveUrl.Trim()
            || liveUrl.Any(char.IsControl)
            || liveUrl.Contains('\\', StringComparison.Ordinal)
            || !ExportReferenceProcessor.TrySplitManagedUrl(liveUrl, out var path, out var query, out _)
            || query.Length != 0
            || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        route = path;
        return true;
    }

    private static bool TryGetRequiredString(JsonObject entry, string propertyName, out string value)
    {
        value = string.Empty;
        if (entry[propertyName] is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var stringValue)
            || stringValue is null)
        {
            return false;
        }

        value = stringValue;
        return true;
    }

    private static bool TryGetManifestAliases(
        JsonObject entry,
        string propertyName,
        out IReadOnlyList<string> aliases)
    {
        aliases = [];
        if (entry[propertyName] is not JsonArray array)
        {
            return false;
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var value)
                || value is null)
            {
                return false;
            }

            values.Add(value);
        }

        aliases = values;
        return true;
    }

    private static bool TryNormalizeManifestRoutePath(string value, bool allowEmpty, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (value != value.Trim()
            || value.Any(char.IsControl)
            || value.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var routePath = value.TrimStart('/');
        var fragmentIndex = routePath.IndexOf('#', StringComparison.Ordinal);
        var pathPart = fragmentIndex < 0 ? routePath : routePath[..fragmentIndex];
        if ((!allowEmpty && pathPart.Length == 0)
            || pathPart.Contains('?', StringComparison.Ordinal)
            || pathPart.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (pathPart.Length > 0
            && pathPart.Split('/').Any(segment =>
                segment.Length == 0 || segment.StartsWith(".", StringComparison.Ordinal)))
        {
            return false;
        }

        normalizedPath = pathPart;
        return true;
    }

    private static bool IsSafeCanonicalArtifactPath(string artifactPath)
    {
        return !string.IsNullOrWhiteSpace(artifactPath)
               && artifactPath == artifactPath.Trim()
               && !Path.IsPathRooted(artifactPath)
               && !artifactPath.Any(char.IsControl)
               && artifactPath.IndexOfAny(['\\', ':', '?', '#', '%']) < 0
               && !artifactPath.Contains("//", StringComparison.Ordinal)
               && artifactPath.Split('/').All(segment =>
                   !string.IsNullOrWhiteSpace(segment) && !segment.StartsWith(".", StringComparison.Ordinal))
               && Path.GetExtension(artifactPath).Equals(".html", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateArchiveRouteNamespaces(
        ExportContext context,
        IReadOnlyList<StagedArchiveRouteMapping> mappings)
    {
        var canonicalOwnerByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var aliasOwnerByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            if (!canonicalOwnerByPath.TryAdd(mapping.CanonicalRoutePath, mapping.Index))
            {
                ThrowArchiveRouteMappingFailure(context, "canonical-route-collision", mapping.Index);
            }
        }

        foreach (var mapping in mappings)
        {
            foreach (var aliasPath in mapping.AliasPaths)
            {
                if (canonicalOwnerByPath.ContainsKey(aliasPath)
                    || !aliasOwnerByPath.TryAdd(aliasPath, mapping.Index))
                {
                    ThrowArchiveRouteMappingFailure(context, "alias-route-collision", mapping.Index);
                }
            }
        }

        var artifactOwnerByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            if (!artifactOwnerByPath.TryAdd(mapping.ArtifactPath, mapping.Index))
            {
                ThrowArchiveRouteMappingFailure(context, "artifact-target-collision", mapping.Index);
            }

            if (aliasOwnerByPath.ContainsKey(mapping.ArtifactPath))
            {
                ThrowArchiveRouteMappingFailure(context, "alias-artifact-collision", mapping.Index);
            }

            if (canonicalOwnerByPath.TryGetValue(mapping.ArtifactPath, out var canonicalOwner)
                && canonicalOwner != mapping.Index)
            {
                ThrowArchiveRouteMappingFailure(context, "artifact-canonical-collision", mapping.Index);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowArchiveRouteMappingFailure(ExportContext context, string reason, int? entryIndex)
    {
        var entryDescription = entryIndex is null ? string.Empty : $" Entry: {entryIndex.Value}.";
        context.Diagnostics.Add(new ExportDiagnostic(
            ArchiveRouteMappingDiagnosticCode,
            $"[archive-route-mapping] The staged AppSurface Docs route manifest could not be finalized ({reason})."
            + entryDescription
            + " Fix: re-export with every canonical live URL materialized as ordinary HTML and remove route or artifact collisions. "
            + "See ../ForgeTrust.AppSurface.Docs/README.md#exact-version-tree-contract.",
            "/" + AppSurfaceDocsRouteManifestFileName));
        throw new ExportValidationException(context.Diagnostics);
    }
}
