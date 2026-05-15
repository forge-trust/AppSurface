using System.Globalization;
using System.Text;
using ForgeTrust.AppSurface.Docs.Models;

namespace ForgeTrust.AppSurface.Docs.Services;

internal enum DocRouteResolutionKind
{
    Canonical = 0,
    AliasRedirect = 1,
    InternalSourceMatch = 2,
    CollisionLoser = 3,
    ReservedRoute = 4,
    NotFound = 5
}

internal sealed record DocRouteResolution(
    DocRouteResolutionKind Kind,
    string? SourcePath = null,
    string? PublicRoutePath = null);

internal sealed record DocRouteIdentity(
    string SourcePath,
    string PublicRoutePath,
    bool IsPublicCanonicalWinner,
    bool SourcePathIsMarkdown,
    IReadOnlyList<string> RedirectAliasPaths);

/// <summary>
/// Owns the route identity contract for one cached RazorDocs snapshot.
/// </summary>
/// <remarks>
/// The catalog deliberately separates source identity from public route identity:
///
///   source path        -> internal lookup and authoring provenance
///   public route path  -> browser-facing canonical URL
///   redirect alias     -> declared or Markdown source-shaped URL that redirects to public route
///
/// Controllers render only public canonical winners. Declared aliases and Markdown source-shaped paths
/// for public winners redirect to the canonical route, while non-Markdown source paths, collision losers,
/// and reserved routes stay non-public. Link builders can still resolve source paths so authored Markdown
/// stays source-friendly without rendering source-shaped URLs into the reader-facing surface.
/// </remarks>
internal sealed class DocRouteIdentityCatalog
{
    private static readonly string[] ReservedRelativePaths =
    [
        "",
        "search",
        "search-index.json",
        "_health",
        "_health.json",
        "search.css",
        "search-client.js",
        "outline-client.js",
        "minisearch.min.js",
        "versions"
    ];

    private readonly Dictionary<string, DocRouteIdentity> _identityBySourcePath;
    private readonly Dictionary<string, DocRouteIdentity> _identityByInternalPath;
    private readonly Dictionary<string, DocRouteIdentity> _identityByExactInternalPath;
    private readonly Dictionary<string, DocRouteIdentity> _publicIdentityByRoutePath;
    private readonly Dictionary<string, DocRouteIdentity> _aliasIdentityByRoutePath;
    private readonly HashSet<string> _reservedRoutePaths;

    private DocRouteIdentityCatalog(
        Dictionary<string, DocRouteIdentity> identityBySourcePath,
        Dictionary<string, DocRouteIdentity> identityByInternalPath,
        Dictionary<string, DocRouteIdentity> identityByExactInternalPath,
        Dictionary<string, DocRouteIdentity> publicIdentityByRoutePath,
        Dictionary<string, DocRouteIdentity> aliasIdentityByRoutePath,
        HashSet<string> reservedRoutePaths,
        IReadOnlyList<DocHarvestDiagnostic> diagnostics)
    {
        _identityBySourcePath = identityBySourcePath;
        _identityByInternalPath = identityByInternalPath;
        _identityByExactInternalPath = identityByExactInternalPath;
        _publicIdentityByRoutePath = publicIdentityByRoutePath;
        _aliasIdentityByRoutePath = aliasIdentityByRoutePath;
        _reservedRoutePaths = reservedRoutePaths;
        Diagnostics = diagnostics;
    }

    internal IReadOnlyList<DocHarvestDiagnostic> Diagnostics { get; }

    internal static DocRouteIdentityCatalog Create(IEnumerable<DocNode> docs, DocsUrlBuilder docsUrlBuilder)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(docsUrlBuilder);

        var reservedRoutePaths = BuildReservedRoutePaths(docsUrlBuilder);
        var diagnostics = new List<DocHarvestDiagnostic>();
        var candidates = docs
            .Select((doc, index) => CreateCandidate(doc, index, diagnostics))
            .ToList();

        var publicWinners = ResolvePublicWinners(candidates, reservedRoutePaths, diagnostics);
        var identityBySourcePath = new Dictionary<string, DocRouteIdentity>(StringComparer.OrdinalIgnoreCase);
        var identityByInternalPath = new Dictionary<string, DocRouteIdentity>(StringComparer.OrdinalIgnoreCase);
        var identityByExactInternalPath = new Dictionary<string, DocRouteIdentity>(StringComparer.OrdinalIgnoreCase);
        var publicIdentityByRoutePath = new Dictionary<string, DocRouteIdentity>(StringComparer.OrdinalIgnoreCase);
        var aliasIdentityByRoutePath = new Dictionary<string, DocRouteIdentity>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var isWinner = publicWinners.TryGetValue(candidate.PublicRouteLookupPath, out var winner)
                           && ReferenceEquals(candidate, winner);
            var identity = new DocRouteIdentity(
                candidate.SourcePath,
                candidate.PublicRoutePath,
                isWinner,
                candidate.SourcePathIsMarkdown,
                []);
            AddIdentity(identityBySourcePath, candidate.SourceLookupPath, identity);
            AddInternalIdentity(identityByInternalPath, candidate.SourceLookupPath, identity);
            AddInternalIdentity(identityByInternalPath, candidate.PublicRouteLookupPath, identity);
            AddInternalIdentity(identityByInternalPath, candidate.LegacyHtmlLookupPath, identity);
            AddInternalIdentity(identityByExactInternalPath, candidate.SourceExactLookupPath, identity);
            AddInternalIdentity(identityByExactInternalPath, candidate.PublicRouteExactLookupPath, identity);
            AddInternalIdentity(identityByExactInternalPath, candidate.LegacyHtmlExactLookupPath, identity);

            if (isWinner)
            {
                publicIdentityByRoutePath[candidate.PublicRouteLookupPath] = identity;
            }
        }

        foreach (var candidate in candidates)
        {
            if (!identityBySourcePath.TryGetValue(candidate.SourceLookupPath, out var identity)
                || !identity.IsPublicCanonicalWinner)
            {
                continue;
            }

            var aliases = ResolveAliases(
                candidate,
                identity,
                reservedRoutePaths,
                publicIdentityByRoutePath,
                identityByInternalPath,
                aliasIdentityByRoutePath,
                diagnostics);
            if (aliases.Count > 0)
            {
                var identityWithAliases = identity with { RedirectAliasPaths = aliases };
                identityBySourcePath[candidate.SourceLookupPath] = identityWithAliases;
                AddInternalIdentity(identityByInternalPath, candidate.SourceLookupPath, identityWithAliases);
                AddInternalIdentity(identityByInternalPath, candidate.PublicRouteLookupPath, identityWithAliases);
                AddInternalIdentity(identityByInternalPath, candidate.LegacyHtmlLookupPath, identityWithAliases);
                AddInternalIdentity(identityByExactInternalPath, candidate.SourceExactLookupPath, identityWithAliases);
                AddInternalIdentity(identityByExactInternalPath, candidate.PublicRouteExactLookupPath, identityWithAliases);
                AddInternalIdentity(identityByExactInternalPath, candidate.LegacyHtmlExactLookupPath, identityWithAliases);
                publicIdentityByRoutePath[candidate.PublicRouteLookupPath] = identityWithAliases;
                foreach (var alias in aliases)
                {
                    aliasIdentityByRoutePath[NormalizeRouteLookupPath(alias)] = identityWithAliases;
                }
            }
        }

        return new DocRouteIdentityCatalog(
            identityBySourcePath,
            identityByInternalPath,
            identityByExactInternalPath,
            publicIdentityByRoutePath,
            aliasIdentityByRoutePath,
            reservedRoutePaths,
            diagnostics.ToArray());
    }

    internal DocRouteResolution ResolvePublicRoute(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var lookupPath = NormalizeRouteLookupPath(path);
        if (_aliasIdentityByRoutePath.TryGetValue(lookupPath, out var aliasIdentity))
        {
            return new DocRouteResolution(
                DocRouteResolutionKind.AliasRedirect,
                aliasIdentity.SourcePath,
                aliasIdentity.PublicRoutePath);
        }

        if (_publicIdentityByRoutePath.TryGetValue(lookupPath, out var publicIdentity))
        {
            return new DocRouteResolution(
                DocRouteResolutionKind.Canonical,
                publicIdentity.SourcePath,
                publicIdentity.PublicRoutePath);
        }

        if (_reservedRoutePaths.Contains(lookupPath) || IsReservedPrefix(lookupPath))
        {
            return new DocRouteResolution(DocRouteResolutionKind.ReservedRoute);
        }

        if (_identityByInternalPath.TryGetValue(lookupPath, out var internalIdentity))
        {
            return new DocRouteResolution(
                internalIdentity.IsPublicCanonicalWinner
                    ? ResolveInternalPublicWinnerKind(internalIdentity)
                    : DocRouteResolutionKind.CollisionLoser,
                internalIdentity.SourcePath,
                internalIdentity.PublicRoutePath);
        }

        return new DocRouteResolution(DocRouteResolutionKind.NotFound);
    }

    internal bool TryGetPublicRoutePath(string path, out string publicRoutePath)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_identityByExactInternalPath.TryGetValue(NormalizeExactRouteLookupPath(path), out var exactIdentity))
        {
            return TryReturnPublicLinkableRoute(exactIdentity, out publicRoutePath);
        }

        var routeLookupPath = NormalizeRouteLookupPath(path);
        if (_aliasIdentityByRoutePath.TryGetValue(routeLookupPath, out var aliasIdentity))
        {
            return TryReturnPublicLinkableRoute(aliasIdentity, out publicRoutePath);
        }

        if (_publicIdentityByRoutePath.TryGetValue(routeLookupPath, out var publicIdentity))
        {
            return TryReturnPublicLinkableRoute(publicIdentity, out publicRoutePath);
        }

        if (_identityByInternalPath.TryGetValue(routeLookupPath, out var identity))
        {
            return TryReturnPublicLinkableRoute(identity, out publicRoutePath);
        }

        publicRoutePath = string.Empty;
        return false;
    }

    private static DocRouteResolutionKind ResolveInternalPublicWinnerKind(DocRouteIdentity identity)
    {
        return identity.SourcePathIsMarkdown
            ? DocRouteResolutionKind.AliasRedirect
            : DocRouteResolutionKind.InternalSourceMatch;
    }

    private static bool TryReturnPublicLinkableRoute(DocRouteIdentity identity, out string publicRoutePath)
    {
        if (identity.IsPublicCanonicalWinner
            || HasNonRootFragment(identity.PublicRoutePath)
            || IsDocsHomeFragment(identity))
        {
            publicRoutePath = identity.PublicRoutePath;
            return true;
        }

        publicRoutePath = string.Empty;
        return false;
    }

    private static bool HasNonRootFragment(string? publicRoutePath)
    {
        return !string.IsNullOrWhiteSpace(publicRoutePath)
               && publicRoutePath.IndexOf('#', StringComparison.Ordinal) > 0;
    }

    private static bool IsDocsHomeFragment(DocRouteIdentity identity)
    {
        return !string.IsNullOrWhiteSpace(identity.PublicRoutePath)
               && identity.PublicRoutePath.StartsWith("#", StringComparison.Ordinal)
               && identity.SourcePath.TrimStart().StartsWith("#", StringComparison.Ordinal);
    }

    internal static string NormalizeRouteLookupPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var sanitized = path.Trim().Replace('\\', '/').Trim('/');
        var queryIndex = sanitized.IndexOf('?');
        if (queryIndex >= 0)
        {
            sanitized = sanitized[..queryIndex];
        }

        var hashIndex = sanitized.IndexOf('#');
        if (hashIndex >= 0)
        {
            sanitized = sanitized[..hashIndex];
        }

        return sanitized;
    }

    internal static string NormalizeExactRouteLookupPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var sanitized = path.Trim().Replace('\\', '/').Trim('/');
        var queryIndex = sanitized.IndexOf('?');
        if (queryIndex >= 0)
        {
            sanitized = sanitized[..queryIndex];
        }

        return sanitized;
    }

    private static Dictionary<string, DocRouteCandidate> ResolvePublicWinners(
        IReadOnlyList<DocRouteCandidate> candidates,
        HashSet<string> reservedRoutePaths,
        List<DocHarvestDiagnostic> diagnostics)
    {
        var publicWinners = new Dictionary<string, DocRouteCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(candidate => candidate.PublicRouteLookupPath, StringComparer.OrdinalIgnoreCase))
        {
            var routePath = group.Key;
            var groupCandidates = group.ToList();
            if ((reservedRoutePaths.Contains(routePath) || IsReservedPrefix(routePath))
                && !IsDocsHomeRouteGroup(routePath, groupCandidates))
            {
                foreach (var candidate in groupCandidates)
                {
                    diagnostics.Add(CreateDiagnostic(
                        DocHarvestDiagnosticCodes.DocReservedRouteCollision,
                        $"Doc route '{DisplayRoute(routePath)}' is reserved by RazorDocs.",
                        "A documentation page resolved to a route used by RazorDocs chrome, search, health, versions, or assets.",
                        $"Change canonical_slug for '{candidate.SourcePath}' or move the source file so it does not publish at '{DisplayRoute(routePath)}'."));
                }

                continue;
            }

            var winner = groupCandidates
                .OrderBy(candidate => candidate.HasFragment ? 1 : 0)
                .ThenBy(candidate => string.IsNullOrWhiteSpace(candidate.Document.Content) ? 1 : 0)
                .ThenBy(candidate => candidate.Index)
                .First();
            publicWinners[routePath] = winner;

            foreach (var loser in groupCandidates.Where(candidate => !ReferenceEquals(candidate, winner)))
            {
                if (IsSameFragmentFamily(winner, loser))
                {
                    continue;
                }

                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.DocRouteCollision,
                    $"Multiple docs resolved to route '{DisplayRoute(routePath)}'.",
                    $"'{winner.SourcePath}' won the route. '{loser.SourcePath}' remains internally resolvable but is not public.",
                    $"Give '{loser.SourcePath}' a unique canonical_slug or move it to a unique source route."));
            }
        }

        return publicWinners;
    }

    private static List<string> ResolveAliases(
        DocRouteCandidate candidate,
        DocRouteIdentity identity,
        HashSet<string> reservedRoutePaths,
        Dictionary<string, DocRouteIdentity> publicIdentityByRoutePath,
        Dictionary<string, DocRouteIdentity> identityByInternalPath,
        Dictionary<string, DocRouteIdentity> aliasIdentityByRoutePath,
        List<DocHarvestDiagnostic> diagnostics)
    {
        var aliases = new List<string>();
        foreach (var rawAlias in candidate.Document.Metadata?.RedirectAliases ?? [])
        {
            var normalizedAlias = NormalizeRedirectAliasPath(rawAlias, diagnostics, candidate.SourcePath);
            if (string.IsNullOrWhiteSpace(normalizedAlias))
            {
                continue;
            }

            var aliasLookup = NormalizeRouteLookupPath(normalizedAlias);
            if (reservedRoutePaths.Contains(aliasLookup) || IsReservedPrefix(aliasLookup))
            {
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.DocInvalidRedirectAlias,
                    $"Redirect alias '{DisplayRoute(aliasLookup)}' is reserved by RazorDocs.",
                    "A redirect alias cannot shadow docs chrome, search, health, versions, sections, or assets.",
                    $"Choose a different redirect_aliases entry for '{candidate.SourcePath}'."));
                continue;
            }

            if (publicIdentityByRoutePath.TryGetValue(aliasLookup, out var publicIdentity)
                && !string.Equals(publicIdentity.SourcePath, identity.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.DocRedirectAliasCollision,
                    $"Redirect alias '{DisplayRoute(aliasLookup)}' collides with another canonical doc route.",
                    "Canonical doc routes win over redirect aliases so public pages stay stable.",
                    $"Choose a different redirect_aliases entry for '{candidate.SourcePath}'."));
                continue;
            }

            if (identityByInternalPath.TryGetValue(aliasLookup, out var internalIdentity)
                && internalIdentity.SourcePathIsMarkdown)
            {
                if (string.Equals(internalIdentity.SourcePath, identity.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.DocRedirectAliasCollision,
                    $"Redirect alias '{DisplayRoute(aliasLookup)}' collides with another Markdown source path.",
                    "Markdown source paths and their legacy .html forms are implicit redirects, so declared aliases cannot safely shadow them.",
                    $"Choose a different redirect_aliases entry for '{candidate.SourcePath}'."));
                continue;
            }

            if (aliasIdentityByRoutePath.ContainsKey(aliasLookup))
            {
                diagnostics.Add(CreateDiagnostic(
                    DocHarvestDiagnosticCodes.DocRedirectAliasCollision,
                    $"Redirect alias '{DisplayRoute(aliasLookup)}' is declared by multiple docs.",
                    "RazorDocs cannot safely choose between duplicate redirect aliases.",
                    $"Keep the alias on only one page."));
                continue;
            }

            if (string.Equals(aliasLookup, candidate.PublicRouteLookupPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            aliases.Add(normalizedAlias);
        }

        return aliases;
    }

    private static DocRouteCandidate CreateCandidate(DocNode doc, int index, List<DocHarvestDiagnostic> diagnostics)
    {
        var sourcePath = NormalizeSourcePath(doc.Path);
        var (routePath, hadLossyNormalization) = BuildPublicRoutePath(doc, sourcePath, diagnostics);
        if (hadLossyNormalization)
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.DocLossySlugNormalization,
                $"Doc source '{sourcePath}' needed lossy route slug normalization.",
                "A source segment contained characters that cannot be represented directly in the public ASCII route contract.",
                "Set canonical_slug explicitly if the generated route is not what readers should see."));
        }

        return new DocRouteCandidate(
            doc,
            index,
            sourcePath,
            NormalizeRouteLookupPath(sourcePath),
            NormalizeExactRouteLookupPath(sourcePath),
            routePath,
            NormalizeRouteLookupPath(routePath),
            NormalizeExactRouteLookupPath(routePath),
            NormalizeRouteLookupPath(BuildLegacyHtmlPath(sourcePath)),
            NormalizeExactRouteLookupPath(BuildLegacyHtmlPath(sourcePath)),
            IsMarkdownPath(sourcePath),
            routePath.Contains('#', StringComparison.Ordinal));
    }

    private static (string RoutePath, bool HadLossyNormalization) BuildPublicRoutePath(
        DocNode doc,
        string sourcePath,
        List<DocHarvestDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(doc.Metadata?.CanonicalSlug))
        {
            var normalized = NormalizeCanonicalRoutePath(doc.Metadata.CanonicalSlug!, diagnostics, sourcePath);
            return (AppendFragment(normalized, sourcePath), false);
        }

        var fragment = GetFragmentWithHash(sourcePath);
        var sourceWithoutFragment = StripFragment(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceWithoutFragment))
        {
            return (fragment, false);
        }

        if (!IsMarkdownPath(sourceWithoutFragment))
        {
            return (DocRoutePath.BuildCanonicalPath(sourceWithoutFragment) + fragment, false);
        }

        var collapsed = CollapseContentPath(sourceWithoutFragment);
        var hadLossy = false;
        var normalizedSegments = collapsed
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var normalized = SlugifySegment(segment, out var segmentWasLossy);
                hadLossy |= segmentWasLossy;
                return normalized;
            })
            .Where(segment => segment.Length > 0)
            .ToArray();
        if (normalizedSegments.Any(IsUnsafeRelativeRouteSegment))
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug,
                $"Doc source '{sourcePath}' resolved to an unsafe public route segment.",
                "Route segments named '.' or '..' are ambiguous for clients, proxies, and static export paths.",
                "Set canonical_slug to a docs-relative route without dot-directory segments."));
            return (string.Empty, hadLossy);
        }

        var routePath = string.Join('/', normalizedSegments);
        if (string.IsNullOrWhiteSpace(routePath) && !IsRootReadme(sourceWithoutFragment))
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug,
                $"Doc source '{sourcePath}' resolved to an empty public route.",
                "Every public documentation page needs at least one route segment unless it deliberately feeds the docs home.",
                "Set canonical_slug to a non-empty docs-relative route."));
        }

        return (routePath + fragment, hadLossy);
    }

    private static string NormalizeCanonicalRoutePath(
        string value,
        List<DocHarvestDiagnostic> diagnostics,
        string sourcePath)
    {
        const string fieldName = "canonical_slug";
        if (value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug,
                $"{fieldName} for '{sourcePath}' contains a query string or fragment.",
                "Route identity is page-level. Query strings and fragments are not part of the canonical page route.",
                $"Remove the query string or fragment from the {fieldName} entry."));
            return string.Empty;
        }

        var hadLossy = false;
        var segments = value
            .Trim()
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var normalized = SlugifySegment(segment, out var segmentWasLossy);
                hadLossy |= segmentWasLossy;
                return normalized;
            })
            .Where(segment => segment.Length > 0)
            .ToArray();
        if (segments.Any(IsUnsafeRelativeRouteSegment))
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug,
                $"{fieldName} for '{sourcePath}' contains an unsafe dot-directory route segment.",
                "Route segments named '.' or '..' are ambiguous for clients, proxies, and static export paths.",
                $"Set {fieldName} to a docs-relative route without dot-directory segments."));
            return string.Empty;
        }

        var routePath = string.Join('/', segments);
        if (string.IsNullOrWhiteSpace(routePath))
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.DocInvalidCanonicalSlug,
                $"{fieldName} for '{sourcePath}' resolved to an empty route.",
                "Empty routes collide with the docs home and cannot identify a public document page.",
                $"Set {fieldName} to a non-empty docs-relative route."));
            return string.Empty;
        }

        if (hadLossy)
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.DocLossySlugNormalization,
                $"{fieldName} for '{sourcePath}' needed lossy route slug normalization.",
                "The authored route contained characters that cannot be represented directly in the public ASCII route contract.",
                $"Review the generated route '{routePath}' and set an explicit ASCII value if needed."));
        }

        return routePath;
    }

    private static bool IsUnsafeRelativeRouteSegment(string segment)
    {
        return string.Equals(segment, ".", StringComparison.Ordinal)
               || string.Equals(segment, "..", StringComparison.Ordinal);
    }

    private static string NormalizeRedirectAliasPath(
        string value,
        List<DocHarvestDiagnostic> diagnostics,
        string sourcePath)
    {
        const string fieldName = "redirect_aliases";
        if (value.Contains('?', StringComparison.Ordinal) || value.Contains('#', StringComparison.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.DocInvalidRedirectAlias,
                $"{fieldName} for '{sourcePath}' contains a query string or fragment.",
                "Route identity is page-level. Query strings and fragments are not part of the alias route.",
                $"Remove the query string or fragment from the {fieldName} entry."));
            return string.Empty;
        }

        var segments = value
            .Trim()
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToArray();
        var routePath = string.Join('/', segments);
        if (string.IsNullOrWhiteSpace(routePath))
        {
            diagnostics.Add(CreateDiagnostic(
                DocHarvestDiagnosticCodes.DocInvalidRedirectAlias,
                $"{fieldName} for '{sourcePath}' resolved to an empty route.",
                "Empty aliases collide with the docs home and cannot identify a redirect source.",
                $"Set {fieldName} to a non-empty docs-relative route."));
            return string.Empty;
        }

        return routePath;
    }

    private static string CollapseContentPath(string sourceWithoutFragment)
    {
        var trimmed = sourceWithoutFragment.Trim().Replace('\\', '/').Trim('/');
        var withoutExtension = RemoveContentSuffix(trimmed);
        var segments = withoutExtension.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0
            && (segments[^1].Equals("README", StringComparison.OrdinalIgnoreCase)
                || segments[^1].Equals("index", StringComparison.OrdinalIgnoreCase)))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return string.Join('/', segments);
    }

    private static string SlugifySegment(string segment, out bool hadLossyNormalization)
    {
        hadLossyNormalization = false;
        var decoded = DecodeSegment(segment.Trim());
        var folded = decoded.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);
        var previousWasSeparator = false;
        foreach (var ch in folded)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                hadLossyNormalization = true;
                continue;
            }

            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' || ch == '.')
            {
                builder.Append(ch);
                previousWasSeparator = false;
                continue;
            }

            if (ch is >= 'A' and <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSeparator = false;
                continue;
            }

            if (ch == '-' || ch == '_' || char.IsWhiteSpace(ch))
            {
                AppendSeparator(builder, ref previousWasSeparator);
                hadLossyNormalization |= ch != '-';
                continue;
            }

            AppendSeparator(builder, ref previousWasSeparator);
            hadLossyNormalization = true;
        }

        return builder.ToString().Trim('-');
    }

    private static void AppendSeparator(StringBuilder builder, ref bool previousWasSeparator)
    {
        if (!previousWasSeparator && builder.Length > 0)
        {
            builder.Append('-');
            previousWasSeparator = true;
        }
    }

    private static string DecodeSegment(string segment)
    {
        try
        {
            return Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            return segment;
        }
    }

    private static string BuildLegacyHtmlPath(string sourcePath)
    {
        return DocRoutePath.BuildCanonicalPath(sourcePath);
    }

    private static string NormalizeSourcePath(string path)
    {
        return path.Trim().Replace('\\', '/').Trim('/');
    }

    private static string StripFragment(string path)
    {
        var hashIndex = path.IndexOf('#');
        return hashIndex >= 0 ? path[..hashIndex] : path;
    }

    private static string GetFragmentWithHash(string path)
    {
        var hashIndex = path.IndexOf('#');
        return hashIndex >= 0 ? path[hashIndex..] : string.Empty;
    }

    private static string AppendFragment(string routePath, string sourcePath)
    {
        return routePath + GetFragmentWithHash(sourcePath);
    }

    private static string RemoveContentSuffix(string path)
    {
        if (path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^".markdown".Length];
        }

        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return path[..^".md".Length];
        }

        return path;
    }

    private static bool IsMarkdownPath(string path)
    {
        var withoutFragment = StripFragment(path);
        return withoutFragment.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
               || withoutFragment.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootReadme(string path)
    {
        var normalized = path.Trim().Replace('\\', '/').Trim('/');
        return normalized.Equals("README.md", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("README.markdown", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("index.md", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("index.markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDocsHomeRouteGroup(string routePath, IEnumerable<DocRouteCandidate> candidates)
    {
        return string.IsNullOrWhiteSpace(routePath) && candidates.All(IsDocsHomeCandidate);
    }

    private static bool IsDocsHomeCandidate(DocRouteCandidate candidate)
    {
        var sourceWithoutFragment = StripFragment(candidate.SourcePath);
        return string.IsNullOrWhiteSpace(sourceWithoutFragment) || IsRootReadme(sourceWithoutFragment);
    }

    private static bool IsSameFragmentFamily(DocRouteCandidate winner, DocRouteCandidate loser)
    {
        return string.Equals(
            StripFragment(winner.PublicRoutePath),
            StripFragment(loser.PublicRoutePath),
            StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                StripFragment(winner.SourcePath),
                StripFragment(loser.SourcePath),
                StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIdentity(Dictionary<string, DocRouteIdentity> lookup, string key, DocRouteIdentity identity)
    {
        if (!lookup.ContainsKey(key))
        {
            lookup[key] = identity;
        }
    }

    private static void AddInternalIdentity(Dictionary<string, DocRouteIdentity> lookup, string key, DocRouteIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            lookup[key] = identity;
        }
    }

    private static HashSet<string> BuildReservedRoutePaths(DocsUrlBuilder docsUrlBuilder)
    {
        var reserved = new HashSet<string>(ReservedRelativePaths, StringComparer.OrdinalIgnoreCase);
        AddRouteReference(reserved, docsUrlBuilder.Routes.Home, docsUrlBuilder.CurrentDocsRootPath);
        AddRouteReference(reserved, docsUrlBuilder.Routes.Search, docsUrlBuilder.CurrentDocsRootPath);
        AddRouteReference(reserved, docsUrlBuilder.Routes.SearchIndex, docsUrlBuilder.CurrentDocsRootPath);
        AddRouteReference(reserved, docsUrlBuilder.Routes.Health, docsUrlBuilder.CurrentDocsRootPath);
        AddRouteReference(reserved, docsUrlBuilder.Routes.HealthJson, docsUrlBuilder.CurrentDocsRootPath);
        AddRouteReference(reserved, docsUrlBuilder.Routes.Versions, docsUrlBuilder.RouteRootPath);
        return reserved;
    }

    private static void AddRouteReference(HashSet<string> reserved, string route, string root)
    {
        var normalizedRoute = NormalizeRouteLookupPath(route);
        var normalizedRoot = NormalizeRouteLookupPath(root);
        if (string.Equals(normalizedRoute, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            reserved.Add(string.Empty);
            return;
        }

        var prefix = string.IsNullOrWhiteSpace(normalizedRoot) ? string.Empty : normalizedRoot + "/";
        if (prefix.Length > 0 && normalizedRoute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            reserved.Add(normalizedRoute[prefix.Length..]);
        }
    }

    private static bool IsReservedPrefix(string routePath)
    {
        return routePath.StartsWith("sections/", StringComparison.OrdinalIgnoreCase)
               || routePath.StartsWith("v/", StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayRoute(string routePath)
    {
        return string.IsNullOrWhiteSpace(routePath) ? "/" : routePath;
    }

    private static DocHarvestDiagnostic CreateDiagnostic(string code, string problem, string cause, string fix)
    {
        return new DocHarvestDiagnostic(code, DocHarvestDiagnosticSeverity.Warning, HarvesterType: null, problem, cause, fix);
    }

    private sealed record DocRouteCandidate(
        DocNode Document,
        int Index,
        string SourcePath,
        string SourceLookupPath,
        string SourceExactLookupPath,
        string PublicRoutePath,
        string PublicRouteLookupPath,
        string PublicRouteExactLookupPath,
        string LegacyHtmlLookupPath,
        string LegacyHtmlExactLookupPath,
        bool SourcePathIsMarkdown,
        bool HasFragment);
}
