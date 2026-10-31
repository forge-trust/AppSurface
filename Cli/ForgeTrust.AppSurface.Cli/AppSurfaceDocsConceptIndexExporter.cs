using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AngleSharp.Html.Parser;
using ForgeTrust.AppSurface.Docs.Services;
using ForgeTrust.AppSurface.ReleaseContracts;
using ForgeTrust.RazorWire.Cli;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>Writes the concept identity sidecar for one finalized AppSurface Docs export.</summary>
/// <remarks>
/// The writer joins source-backed metadata to the verified canonical winners in the finalized route manifest, then reads
/// each emitted HTML artifact only through its frozen physical mapping. It runs before release archive hashing so the
/// concept sidecar is part of the exact release pin.
/// </remarks>
internal static class AppSurfaceDocsConceptIndexExporter
{
    internal const string FileName = ".appsurface-docs-concepts.json";
    internal const string Schema = "appsurface-docs-concepts-v1";

    private const string RouteManifestFileName = ".appsurface-docs-route-manifest.json";
    private const string DiagnosticCode = "docs-concept-index-invalid";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Registers this release-owned metadata writer with an export context after enabling archive hashing.</summary>
    /// <param name="context">Release archive export context.</param>
    /// <param name="pages">Immutable canonical source metadata captured from the Docs snapshot.</param>
    /// <param name="maximumArtifactBytes">The effective Docs per-artifact UTF-8 limit.</param>
    internal static void Register(
        ExportContext context,
        IReadOnlyList<AppSurfaceDocsConceptExportPage> pages,
        int maximumArtifactBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pages);

        context.RegisterReleaseArchiveArtifactFinalizer(
            (exportContext, cancellationToken) => WriteAsync(
                exportContext.OutputPath,
                pages,
                maximumArtifactBytes,
                cancellationToken));
    }

    /// <summary>Writes and validates the complete concept index from finalized route and HTML artifacts.</summary>
    /// <param name="outputPath">Whole exact-release export root.</param>
    /// <param name="pages">Immutable canonical source metadata captured from the Docs snapshot.</param>
    /// <param name="maximumArtifactBytes">The effective Docs per-artifact UTF-8 limit.</param>
    /// <param name="cancellationToken">Token observed during route-map and HTML reads.</param>
    internal static async Task WriteAsync(
        string outputPath,
        IReadOnlyList<AppSurfaceDocsConceptExportPage> pages,
        int maximumArtifactBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(pages);

        var routeManifestPath = Path.Join(outputPath, RouteManifestFileName);
        ExportOutputPathGuards.ValidateArchiveEntryPath(outputPath, routeManifestPath, "concept-index-route-manifest-read");
        byte[] routeManifestBytes;
        try
        {
            routeManifestBytes = await File.ReadAllBytesAsync(routeManifestPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateFailure("The finalized frozen route manifest could not be read before concept-index generation.", "/" + RouteManifestFileName);
        }

        if (!AppSurfaceDocsFrozenRouteManifest.TryLoadVerified(routeManifestBytes, out var frozenRoutes, out var routeIssue)
            || !frozenRoutes.HtmlUsesExportRootUrls)
        {
            throw CreateFailure(
                $"The finalized frozen route manifest is invalid for concept-index generation ({routeIssue}).",
                "/" + RouteManifestFileName);
        }

        var canonicalPagesByRoute = new Dictionary<string, AppSurfaceDocsConceptExportPage>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (page is null)
            {
                throw CreateFailure("The source Docs snapshot contains a missing canonical page projection.", "/");
            }

            var routePath = GetLogicalRoutePath(page.CanonicalRoutePath);
            if (!canonicalPagesByRoute.TryAdd(routePath, page))
            {
                throw CreateFailure("The source Docs snapshot repeats a canonical page route.", BuildDiagnosticRoute(routePath));
            }

            if (page.ConceptIdIsInvalid)
            {
                throw CreateFailure("A public canonical page has an invalid authored concept_id.", BuildDiagnosticRoute(routePath), "docs-concept-id-invalid");
            }
        }

        if (canonicalPagesByRoute.Count != frozenRoutes.CanonicalEntries.Count)
        {
            throw CreateFailure("Concept metadata does not cover every finalized public canonical route winner.", "/" + RouteManifestFileName);
        }

        var emittedPages = new List<ConceptIndexPage>(frozenRoutes.CanonicalEntries.Count);
        var routeFacts = new List<DocConceptCanonicalRouteFact>(frozenRoutes.CanonicalEntries.Count);
        var conceptIdsByLocale = new HashSet<(string? Locale, string ConceptId)>();

        foreach (var route in frozenRoutes.CanonicalEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!canonicalPagesByRoute.Remove(GetLogicalRoutePath(route.RoutePath), out var authoredPage))
            {
                throw CreateFailure(
                    $"The finalized canonical route '{route.RoutePath}' has no matching source metadata projection.",
                    BuildDiagnosticRoute(route.RoutePath));
            }

            var mappedArtifactPath = route.ArtifactPath;
            ArgumentNullException.ThrowIfNull(mappedArtifactPath);

            if (string.IsNullOrWhiteSpace(authoredPage.Title))
            {
                throw CreateFailure("A public canonical page has no resolved title for the concept index.", BuildDiagnosticRoute(route.RoutePath));
            }

            if (authoredPage.Locale is not null && !string.Equals(authoredPage.Locale, authoredPage.Locale.Trim(), StringComparison.Ordinal))
            {
                throw CreateFailure("A public canonical page has an unnormalized locale for the concept index.", BuildDiagnosticRoute(route.RoutePath));
            }

            if (authoredPage.ConceptId is { } conceptId && !conceptIdsByLocale.Add((authoredPage.Locale, conceptId)))
            {
                throw CreateFailure(
                    $"The concept identifier '{conceptId}' is assigned to more than one canonical page in locale '{authoredPage.Locale ?? "default"}'.",
                    BuildDiagnosticRoute(route.RoutePath));
            }

            var artifactPath = GetPhysicalArtifactPath(mappedArtifactPath);
            var htmlPath = Path.Join(outputPath, artifactPath.Replace('/', Path.DirectorySeparatorChar));
            ExportOutputPathGuards.ValidateArchiveEntryPath(outputPath, htmlPath, "concept-index-anchor-read");
            byte[] htmlBytes;
            try
            {
                htmlBytes = await ReadBoundedHtmlAsync(htmlPath, maximumArtifactBytes, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw CreateFailure("A mapped canonical HTML artifact could not be read for emitted anchor collection.", BuildDiagnosticRoute(route.RoutePath));
            }

            string html;
            try
            {
                html = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(htmlBytes);
            }
            catch (DecoderFallbackException)
            {
                throw CreateFailure("A mapped canonical HTML artifact is not valid UTF-8.", BuildDiagnosticRoute(route.RoutePath));
            }

            var anchorIds = ReadEmittedAnchorIds(html, route.RoutePath);
            emittedPages.Add(new ConceptIndexPage(
                GetLogicalRoutePath(route.RoutePath),
                authoredPage.Locale,
                authoredPage.ConceptId,
                authoredPage.Title.Trim(),
                anchorIds,
                authoredPage.SectionLanding ? true : null));
            routeFacts.Add(new DocConceptCanonicalRouteFact(
                GetLogicalRoutePath(route.RoutePath),
                mappedArtifactPath,
                authoredPage.Locale,
                LocaleIsKnown: true));
        }

        var identityCoverage = emittedPages.All(page => page.ConceptId is not null) ? "complete" : "partial";
        var contents = JsonSerializer.Serialize(
            new ConceptIndexDocument(Schema, identityCoverage, emittedPages),
            SerializerOptions) + "\n";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        if (!DocConceptArtifactBudget.TryMeasure(bytes, maximumArtifactBytes, out _))
        {
            throw CreateFailure(
                $"The complete concept index exceeds the configured per-artifact limit of {maximumArtifactBytes} UTF-8 bytes.",
                "/" + FileName);
        }

        if (!DocConceptSchema.TryLoadSidecar(bytes, routeFacts, maximumArtifactBytes, out _, out var sidecarIssue))
        {
            throw CreateFailure(
                $"The generated concept index does not satisfy the release contract ({sidecarIssue?.Failure}: {sidecarIssue?.Detail}).",
                "/" + FileName);
        }

        var conceptIndexPath = Path.Join(outputPath, FileName);
        ExportOutputPathGuards.ValidateWritableArtifactPath(
            outputPath,
            conceptIndexPath,
            "AppSurface Docs concept index",
            "/" + FileName,
            "concept-index-write");
        if (File.Exists(conceptIndexPath) || Directory.Exists(conceptIndexPath))
        {
            throw CreateFailure("The output already contains the reserved concept index path.", "/" + FileName);
        }

        await ExportAuthArtifactAuditor.WriteTextArtifactAsync(
            outputPath,
            conceptIndexPath,
            "AppSurface Docs concept index",
            "/" + FileName,
            contents,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    /// <summary>Rejects a complete source HTML artifact above the existing coupled budget, including growth during reading.</summary>
    private static async Task<byte[]> ReadBoundedHtmlAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > maximumBytes) throw CreateFailure("A canonical HTML artifact exceeds the configured rewrite input budget.", "/");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + count > maximumBytes) throw CreateFailure("A canonical HTML artifact grew beyond the configured rewrite input budget.", "/");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static IReadOnlyList<string> ReadEmittedAnchorIds(string html, string routePath)
    {
        var document = new HtmlParser().ParseDocument(html);
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.QuerySelectorAll("[id]"))
        {
            var id = element.GetAttribute("id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)
                || id.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character == '#')
                || !seen.Add(id))
            {
                throw CreateFailure("A mapped canonical HTML artifact contains an invalid or duplicate emitted anchor ID.", BuildDiagnosticRoute(routePath));
            }

            ids.Add(id);
        }

        return Array.AsReadOnly(ids.ToArray());
    }

    private static string GetLogicalRoutePath(string routePath)
    {
        var fragmentIndex = routePath.IndexOf('#', StringComparison.Ordinal);
        return fragmentIndex < 0 ? routePath : routePath[..fragmentIndex];
    }

    private static string GetPhysicalArtifactPath(string artifactPath)
    {
        var fragmentIndex = artifactPath.IndexOf('#', StringComparison.Ordinal);
        return fragmentIndex < 0 ? artifactPath : artifactPath[..fragmentIndex];
    }

    private static string BuildDiagnosticRoute(string routePath) =>
        routePath.Length == 0 ? "/" : "/docs/" + routePath;

    private static ExportValidationException CreateFailure(string message, string route, string code = DiagnosticCode) =>
        new([new ExportDiagnostic(code, $"[concept-index] {message} Correct source metadata, re-export and verify the complete tree, update its matching pin, then restart/redeploy. See https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md#concept-switch-diagnostics", route)]);

    private sealed record ConceptIndexDocument(string Schema, string IdentityCoverage, IReadOnlyList<ConceptIndexPage> Pages);

    private sealed record ConceptIndexPage(
        string CanonicalRoutePath,
        string? Locale,
        string? ConceptId,
        string Title,
        IReadOnlyList<string> AnchorIds,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SectionLanding);
}
