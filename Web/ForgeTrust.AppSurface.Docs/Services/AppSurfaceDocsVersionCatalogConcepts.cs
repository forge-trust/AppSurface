using System.Text.Json;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Docs.Services;

public sealed partial class AppSurfaceDocsVersionCatalogService
{
    /// <summary>Loads optional identity bytes only after the existing archive verifier established their pins.</summary>
    private AppSurfaceDocsConceptSwitching LoadConceptSwitching(
        IReadOnlyList<(JsonElement Entry, AppSurfaceDocsResolvedVersion Version)> declarations,
        string trustedRoot)
    {
        var limit = checked((int)_options.Versioning.MaxRewrittenFileSizeBytes);
        var releases = new List<DocConceptReleaseInput>();
        var maps = new List<DocConceptMapReference>();
        foreach (var (entry, version) in declarations)
        {
            var archive = version.VerifiedReleaseArchive;
            var routes = new List<DocConceptCanonicalRouteFact>();
            ReadOnlyMemory<byte>? indexBytes = null;
            if (archive is not null && version.ExactTreePath is not null)
            {
                foreach (var route in archive.FrozenRouteManifest.CanonicalRoutePaths)
                {
                    var logicalRoute = route.Split('#')[0];
                    var path = ResolveConceptArtifact(archive, logicalRoute);
                    if (path is not null && AppSurfaceDocsTrustedReleasePathGuard.TryValidateFileCandidate(
                            version.ExactTreePath, path.Split('#')[0], out _, out _))
                    {
                        routes.Add(new DocConceptCanonicalRouteFact(logicalRoute, path, null, LocaleIsKnown: false));
                    }
                }

                const string indexName = ".appsurface-docs-concepts.json";
                if (archive.TryGetFile(indexName, out var file))
                {
                    try
                    {
                        indexBytes = AppSurfaceDocsConceptFileReader.Read(
                            version.ExactTreePath, indexName, file.Sha256, limit, file.Length);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
                    {
                        // Present-but-invalid is deliberately different from a legacy archive without a sidecar.
                        indexBytes = ReadOnlyMemory<byte>.Empty;
                        AppSurfaceDocsConceptDiagnostics.Rejected(_logger, "docs-concept-index-invalid", "startup", "concept-index", limit);
                    }
                }
            }

            // Descriptors without a valid archive pin are represented by the caller's generic unavailable choice.
            // Inventing a pin would turn a presentation placeholder into false identity evidence.
            if (version.ReleaseManifestSha256 is { } pin && pin.Length == 64 && pin.All(char.IsAsciiHexDigit))
            {
                releases.Add(new DocConceptReleaseInput(version.Version, pin,
                    version.Visibility == AppSurfaceDocsVersionVisibility.Public,
                    version.IsAvailable && archive is not null,
                    routes.Count > 0, archive?.TryGetFile("search-index.json", out _) == true,
                    routes.FirstOrDefault(route => route.RoutePath.Length == 0)?.RoutePath,
                    routes, indexBytes));
            }

            // Bridge ownership is catalog-wide: a map attached to either descriptor participates once in the snapshot.
            var pathValid = TryReadOptionalTrimmedString(entry, "conceptMapPath", out var mapPath, out _);
            var pinValid = TryReadOptionalTrimmedString(entry, "conceptMapSha256", out var mapPin, out _);
            if (pathValid && pinValid && mapPath is null && mapPin is null) continue;
            try
            {
                if (!pathValid || !pinValid || string.IsNullOrWhiteSpace(mapPath) || string.IsNullOrWhiteSpace(mapPin))
                    throw new InvalidDataException("Concept bridge path and digest must be supplied together.");
                var bytes = AppSurfaceDocsConceptFileReader.Read(trustedRoot, mapPath, mapPin, limit);
                maps.Add(new DocConceptMapReference(mapPath, mapPin, bytes));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                AppSurfaceDocsConceptDiagnostics.Rejected(_logger, "docs-concept-map-invalid", "startup", "concept-map", limit);
                // An unreadable bridge cannot be safely split into groups. The core receives the rejected artifact
                // rather than silently resurrecting same-path continuity through an unknown conflicting group.
                maps.Add(new DocConceptMapReference(mapPath ?? "invalid-concept-map", mapPin ?? string.Empty, ReadOnlyMemory<byte>.Empty));
            }
        }

        DocConceptIndexBuildResult result;
        try
        {
            result = DocConceptIdentityIndex.BuildCombined(releases, maps, DocConceptValidationMode.Quarantine, limit);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppSurfaceDocsConceptDiagnostics.Unexpected(_logger, new DefaultHttpContext(), exception);
            // Optional identity loading must not turn an otherwise healthy immutable archive into an unavailable site.
            // Empty identity data yields generic unavailable choices and never re-enables same-route matching.
            result = DocConceptIdentityIndex.BuildCombined([], [], DocConceptValidationMode.Quarantine, limit);
        }
        foreach (var issue in result.Issues)
        {
            var code = issue.Failure == DocConceptValidationFailure.InvalidConceptId ? "docs-concept-id-invalid"
                : issue.Artifact == "concept-index" ? "docs-concept-index-invalid" : "docs-concept-map-invalid";
            AppSurfaceDocsConceptDiagnostics.Rejected(_logger, code, "startup", issue.Artifact, limit, detail: issue.Detail);
        }
        return new AppSurfaceDocsConceptSwitching(result.Index, declarations.Select(value => value.Version).ToArray(),
            _docsUrlBuilder, InstanceName, limit);
    }

    /// <summary>Projects through a verified frozen mapping, using only the established candidate resolver for legacy manifests.</summary>
    private static string? ResolveConceptArtifact(AppSurfaceDocsVerifiedReleaseArchive archive, string route)
    {
        if (archive.FrozenRouteManifest.TryResolveCanonicalArtifactPath(route, out var artifact))
        {
            var path = artifact.Split('#')[0];
            return archive.TryGetFile(path, out _) ? artifact : null;
        }

        foreach (var candidate in AppSurfaceDocsPublishedTreeHandler.BuildHtmlDocumentCandidatePaths(route))
        {
            if (archive.TryGetFile(candidate, out _)) return candidate;
            if (archive.TryGetFile("docs/" + candidate, out _)) return "docs/" + candidate;
        }
        return null;
    }
}
