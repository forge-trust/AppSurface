using System.Text;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>Checks immutable startup inventories before publishing the newly reserved alias subtree.</summary>
internal static class AppSurfaceDocsAliasCollisionGuard
{
    /// <summary>Rejects live/recommended ownership overlap without reserving anything for an inactive catalog.</summary>
    /// <param name="catalog">Immutable catalog snapshot, including the declaration state.</param>
    /// <param name="urls">The instance's normalized public routes.</param>
    /// <param name="logger">Optional startup logger; no archive paths or descriptor copy is emitted.</param>
    /// <param name="instanceName">Validated, bounded product identity.</param>
    internal static void Validate(AppSurfaceDocsResolvedVersionCatalog catalog, DocsUrlBuilder urls,
        ILogger? logger = null, string instanceName = "Default")
    {
        if (!catalog.IsAliasNamespaceActive)
        {
            return;
        }

        var root = DocsUrlBuilder.JoinPath(urls.RouteRootPath, "a");
        if (DocsUrlBuilder.IsUnderRoot(root, urls.CurrentDocsRootPath)
            || DocsUrlBuilder.IsUnderRoot(urls.CurrentDocsRootPath, root))
        {
            Fail(root, logger, instanceName);
        }

        if (catalog.RecommendedVersion?.VerifiedReleaseArchive is not { } archive)
        {
            return;
        }

        foreach (var file in archive.CoveredPaths)
        {
            if (AppSurfaceDocsPublishedTreeHandler.IsHandlerServeableFilePath(file, allowSvg: true)
                && OccupiesNamespace(file))
            {
                Fail(root, logger, instanceName);
            }
        }

        foreach (var route in archive.FrozenRouteManifest.PublicRoutePaths)
        {
            if (OccupiesNamespace(route))
            {
                Fail(root, logger, instanceName);
            }
        }
    }

    private static bool OccupiesNamespace(string path)
    {
        var normalized = path.TrimStart('/').Split('#', 2)[0];
        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..];
        }

        return string.Equals(normalized, "a", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "a.html", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("a/", StringComparison.OrdinalIgnoreCase);
    }

    private static void Fail(string path, ILogger? logger, string instanceName)
    {
        var safe = new StringBuilder();
        var bytes = 0;
        foreach (var rune in path.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 253)
            {
                safe.Append("…");
                break;
            }

            safe.Append(Rune.IsControl(rune) ? " " : rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }

        const string action = "Relocate the conflicting content or live root, or remove aliases, then restart.";
        const string documentation = "https://github.com/forge-trust/AppSurface/blob/main/Web/ForgeTrust.AppSurface.Docs/README.md#asdocsalias009";
        logger?.LogError("{AliasDiagnosticCode}: {AliasReason}. {AliasAction} InstanceName={InstanceName}; CollisionRoute={CollisionRoute}. See {DocumentationReference}.",
            "ASDOCSALIAS009", "namespace-collision", action, AppSurfaceDocsInstanceDeclaration.NormalizeName(instanceName), safe.ToString(), documentation);
        throw new InvalidOperationException($"ASDOCSALIAS009: Docs namespace '{safe}' overlaps live Docs or "
            + "the recommended release inventory. Relocate the conflicting content or live root, or remove aliases, "
            + $"then restart. See {documentation}.");
    }
}
