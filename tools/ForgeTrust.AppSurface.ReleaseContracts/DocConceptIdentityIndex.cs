using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.ReleaseContracts;

/// <summary>Indexes verified release concepts, pinned legacy memberships, and exact canonical-page facts.</summary>
/// <remarks>
/// This is a transport-neutral BCL-only snapshot. It performs no filesystem, HTTP, or Docs-web work. Callers resolve
/// bridge paths under their trusted root, verify archive bytes first, pass exact bounded artifact bytes, and retain this
/// result for the catalog snapshot lifetime. Public lookup uses exact release pins and normalized locale identity;
/// routes compare with the repository's case-insensitive logical-routing semantics while concept IDs and anchors remain
/// ordinal. Incoming transitions are enumerated on demand and are never cached as an all-page/all-version matrix.
/// </remarks>
internal sealed class DocConceptIdentityIndex
{
    private readonly IReadOnlyDictionary<ReleaseKey, DocConceptReleaseSnapshot> _releaseByKey;
    private readonly IReadOnlyDictionary<PageKey, DocConceptCanonicalPage> _pageByKey;
    private readonly IReadOnlyDictionary<ConceptMembershipKey, MembershipFact> _membershipByConcept;
    private readonly IReadOnlyList<DocConceptReleaseSnapshot> _releases;
    private readonly IReadOnlyList<DocConceptReleaseSnapshot> _publicAvailableReleases;
    private readonly HashSet<string> _quarantinedConceptIds;
    private readonly HashSet<PageKey> _quarantinedPages;
    private readonly bool _hasUntrustedMapData;

    private DocConceptIdentityIndex(
        IReadOnlyDictionary<ReleaseKey, DocConceptReleaseSnapshot> releaseByKey,
        IReadOnlyDictionary<PageKey, DocConceptCanonicalPage> pageByKey,
        IReadOnlyDictionary<ConceptMembershipKey, MembershipFact> membershipByConcept,
        IEnumerable<string> quarantinedConceptIds,
        IEnumerable<PageKey> quarantinedPages,
        bool hasUntrustedMapData)
    {
        _releaseByKey = new ReadOnlyDictionary<ReleaseKey, DocConceptReleaseSnapshot>(new Dictionary<ReleaseKey, DocConceptReleaseSnapshot>(releaseByKey, ReleaseKeyComparer.Instance));
        _pageByKey = new ReadOnlyDictionary<PageKey, DocConceptCanonicalPage>(new Dictionary<PageKey, DocConceptCanonicalPage>(pageByKey, PageKeyComparer.Instance));
        _membershipByConcept = new ReadOnlyDictionary<ConceptMembershipKey, MembershipFact>(new Dictionary<ConceptMembershipKey, MembershipFact>(membershipByConcept, ConceptMembershipKeyComparer.Instance));
        _releases = Array.AsReadOnly(releaseByKey.Values.ToArray());
        _publicAvailableReleases = Array.AsReadOnly(_releases.Where(release => release.IsPublic && release.IsAvailable).ToArray());
        _quarantinedConceptIds = new HashSet<string>(quarantinedConceptIds, StringComparer.Ordinal);
        _quarantinedPages = new HashSet<PageKey>(quarantinedPages, PageKeyComparer.Instance);
        _hasUntrustedMapData = hasUntrustedMapData;
    }

    /// <summary>Gets the immutable public, available exact releases in the supplied catalog order.</summary>
    public IReadOnlyList<DocConceptReleaseSnapshot> Releases => _releases;

    /// <summary>Gets the immutable public, available exact releases in the supplied catalog order.</summary>
    public IReadOnlyList<DocConceptReleaseSnapshot> PublicAvailableReleases => _publicAvailableReleases;

    /// <summary>
    /// Parses a release sidecar against its verified canonical inventory. Missing files are represented by the caller as
    /// null at build time; present empty, stale, malformed, or oversized files must be passed here and become invalid.
    /// </summary>
    /// <param name="bytes">Exact bytes already verified against the release archive manifest.</param>
    /// <param name="verifiedCanonicalRoutes">Only public canonical winners and their verified artifact mappings.</param>
    /// <param name="maximumBytes">Effective shared per-artifact UTF-8 byte limit.</param>
    /// <param name="sidecar">Detached parsed sidecar when valid.</param>
    /// <param name="issue">Transport-neutral validation cause when invalid.</param>
    /// <returns>Whether the complete sidecar passed byte, schema, locale, route, identity, and coverage checks.</returns>
    public static bool TryLoadSidecar(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<DocConceptCanonicalRouteFact> verifiedCanonicalRoutes,
        int maximumBytes,
        out DocConceptSidecarSnapshot? sidecar,
        out DocConceptValidationIssue? issue) =>
        DocConceptSchema.TryLoadSidecar(bytes, verifiedCanonicalRoutes, maximumBytes, out sidecar, out issue);

    /// <summary>
    /// Builds one combined catalog snapshot from detached release facts and every distinct optional pinned bridge.
    /// Identical map references and memberships deduplicate; incompatible facts quarantine their full connected concept
    /// groups. In strict mode, any issue makes <see cref="DocConceptIndexBuildResult.IsValid"/> false for publication.
    /// </summary>
    /// <param name="releases">Exact public and hidden release facts from the immutable catalog snapshot.</param>
    /// <param name="mapReferences">All optional bridge references, regardless of which descriptor referenced each one.</param>
    /// <param name="mode">Strict authored-publication validation or conservative startup quarantine.</param>
    /// <param name="maximumBytes">Effective shared per-artifact UTF-8 byte limit.</param>
    /// <returns>An immutable combined index and detached issue list.</returns>
    public static DocConceptIndexBuildResult BuildCombined(
        IReadOnlyList<DocConceptReleaseInput> releases,
        IReadOnlyList<DocConceptMapReference> mapReferences,
        DocConceptValidationMode mode,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(mapReferences);

        var issues = new List<DocConceptValidationIssue>();
        var strictValid = DocConceptArtifactBudget.IsValidLimit(maximumBytes);
        if (!strictValid)
        {
            issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.ArtifactBudgetInvalid, "concept-artifacts", "The configured artifact limit is outside the supported range."));
        }

        var mutableReleases = new Dictionary<ReleaseKey, MutableRelease>(ReleaseKeyComparer.Instance);
        var releaseByVersion = new Dictionary<string, MutableRelease>(StringComparer.Ordinal);
        foreach (var releaseInput in releases)
        {
            if (releaseInput is null || string.IsNullOrWhiteSpace(releaseInput.Version) || !DocConceptSchema.IsSha256(releaseInput.ReleaseManifestSha256))
            {
                strictValid = false;
                issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.ReleasePinInvalid, "release-catalog", "A release is missing an exact version or SHA-256 manifest pin."));
                continue;
            }

            var releaseKey = new ReleaseKey(releaseInput.Version, releaseInput.ReleaseManifestSha256.ToLowerInvariant());
            if (releaseByVersion.ContainsKey(releaseInput.Version) || mutableReleases.ContainsKey(releaseKey))
            {
                strictValid = false;
                issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.IdentityConflict, "release-catalog", "The catalog repeats an exact release identity."));
                continue;
            }

            var routeFactsValid = TryValidateCanonicalRouteFacts(releaseInput.CanonicalRoutes, out var routesByPath, out var routeIssue);
            if (!routeFactsValid)
            {
                strictValid = false;
                issues.Add(routeIssue!);
            }

            var pages = new List<MutablePage>();
            var identityState = routeFactsValid ? DocConceptIdentityState.LegacyUnknown : DocConceptIdentityState.Quarantined;
            if (routeFactsValid && releaseInput.ConceptIndexBytes is { } indexBytes)
            {
                if (DocConceptSchema.TryLoadSidecar(indexBytes.Span, releaseInput.CanonicalRoutes, maximumBytes, out var sidecar, out var issue))
                {
                    identityState = sidecar!.IdentityState;
                    pages.AddRange(sidecar.Pages.Select(page => new MutablePage(page)));
                }
                else
                {
                    identityState = DocConceptIdentityState.Quarantined;
                    strictValid = false;
                    issues.Add(issue!);
                }
            }

            if (!routeFactsValid)
            {
                pages.Clear();
            }
            else if (pages.Count == 0 || identityState is DocConceptIdentityState.LegacyUnknown or DocConceptIdentityState.Quarantined)
            {
                pages.Clear();
                foreach (var routeFact in releaseInput.CanonicalRoutes)
                {
                    if (!DocConceptSchema.IsSafeRoutePath(routeFact.RoutePath))
                    {
                        continue;
                    }

                    pages.Add(new MutablePage(routeFact.RoutePath, routeFact.ArtifactPath, routeFact.Locale, routeFact.LocaleIsKnown, string.Empty, [], false, null, routeFact.IsServeable));
                }
            }

            var mutableRelease = new MutableRelease(
                releaseKey,
                releaseInput.IsPublic,
                releaseInput.IsAvailable,
                releaseInput.HasDocumentation,
                releaseInput.HasSearchIndex,
                ResolveHomeRoute(releaseInput.HomeRoutePath, pages),
                identityState,
                pages,
                routesByPath);
            mutableReleases.Add(releaseKey, mutableRelease);
            releaseByVersion.Add(releaseInput.Version, mutableRelease);
        }

        var quarantinedConceptIds = new HashSet<string>(StringComparer.Ordinal);
        var quarantinedPages = new HashSet<PageKey>(PageKeyComparer.Instance);
        var membershipFacts = new Dictionary<ConceptMembershipKey, MembershipFact>(ConceptMembershipKeyComparer.Instance);
        var conceptIdsByPage = new Dictionary<PageKey, HashSet<string>>(PageKeyComparer.Instance);
        var conceptGraph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var pageByKey = new Dictionary<PageKey, MutablePage>(PageKeyComparer.Instance);

        foreach (var release in mutableReleases.Values)
        {
            foreach (var page in release.Pages)
            {
                var pageKey = new PageKey(release.Key, page.Locale, page.LocaleIsKnown, page.RoutePath);
                if (!pageByKey.TryAdd(pageKey, page))
                {
                    strictValid = false;
                    issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.IdentityConflict, "concept-index", "The catalog has duplicate canonical page identities."));
                    quarantinedPages.Add(pageKey);
                }

                if (page.ConceptId is not null)
                {
                    AddEmbeddedMembership(pageKey, page, membershipFacts, conceptIdsByPage, conceptGraph, quarantinedConceptIds, quarantinedPages, issues, ref strictValid);
                }
            }
        }

        var hasUntrustedMapData = false;
        var distinctMapReferences = new Dictionary<MapReferenceKey, DocConceptMapReference>(MapReferenceKeyComparer.Instance);
        foreach (var mapReference in mapReferences)
        {
            if (mapReference is null || !IsSafeRelativeMapPath(mapReference.Path) || !DocConceptSchema.IsSha256(mapReference.Sha256))
            {
                strictValid = false;
                hasUntrustedMapData = true;
                issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.MapInvalid, "concept-map", "A bridge reference has an unsafe path or invalid SHA-256 pin."));
                continue;
            }

            var referenceKey = new MapReferenceKey(mapReference.Path, mapReference.Sha256.ToLowerInvariant());
            if (distinctMapReferences.TryGetValue(referenceKey, out var priorReference))
            {
                if (!priorReference.Bytes.Span.SequenceEqual(mapReference.Bytes.Span))
                {
                    strictValid = false;
                    hasUntrustedMapData = true;
                    issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.MapInvalid, "concept-map", "Repeated path and pin references did not provide identical bytes."));
                }

                continue;
            }

            distinctMapReferences.Add(referenceKey, mapReference);
        }

        foreach (var reference in distinctMapReferences.Values)
        {
            var actualDigest = Convert.ToHexString(SHA256.HashData(reference.Bytes.Span)).ToLowerInvariant();
            if (!string.Equals(actualDigest, reference.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                strictValid = false;
                hasUntrustedMapData = true;
                issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.MapInvalid, reference.Path, "The bridge bytes do not match the descriptor digest pin."));
                continue;
            }

            if (!DocConceptSchema.TryLoadMap(reference.Bytes.Span, maximumBytes, out var parsedMemberships, out var mapIssue))
            {
                strictValid = false;
                hasUntrustedMapData = true;
                issues.Add(mapIssue! with { Artifact = reference.Path });
                continue;
            }

            foreach (var membership in parsedMemberships)
            {
                if (!releaseByVersion.TryGetValue(membership.Version, out var release)
                    || !string.Equals(release.Key.ManifestSha256, membership.ReleaseManifestSha256, StringComparison.OrdinalIgnoreCase))
                {
                    strictValid = false;
                    hasUntrustedMapData = true;
                    quarantinedConceptIds.Add(membership.ConceptId);
                    issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.ReleasePinInvalid, reference.Path, "A bridge membership does not match an exact catalog release pin."));
                    continue;
                }

                if (!release.IsPublic || !release.IsAvailable)
                {
                    strictValid = false;
                    hasUntrustedMapData = true;
                    quarantinedConceptIds.Add(membership.ConceptId);
                    issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.ReleasePinInvalid, reference.Path, "A bridge membership cannot target a hidden or unavailable release."));
                    continue;
                }

                var conceptKey = new ConceptMembershipKey(release.Key, membership.Locale, membership.ConceptId);
                PageKey? mappedPageKey = null;
                if (!membership.IsAbsent)
                {
                    if (!TryResolveMembershipPage(release, membership, out var mappedPage, out var membershipFailure))
                    {
                        strictValid = false;
                        hasUntrustedMapData = true;
                        quarantinedConceptIds.Add(membership.ConceptId);
                        issues.Add(new DocConceptValidationIssue(membershipFailure, reference.Path, "A bridge page or fallback does not name a verified public canonical target in the selected locale."));
                        continue;
                    }

                    mappedPageKey = new PageKey(release.Key, mappedPage!.Locale, mappedPage.LocaleIsKnown, mappedPage.RoutePath);
                    if (mappedPage.ConceptId is not null && !string.Equals(mappedPage.ConceptId, membership.ConceptId, StringComparison.Ordinal))
                    {
                        strictValid = false;
                        hasUntrustedMapData = true;
                        quarantinedConceptIds.Add(mappedPage.ConceptId);
                        quarantinedConceptIds.Add(membership.ConceptId);
                        quarantinedPages.Add(mappedPageKey.Value);
                        LinkConcepts(mappedPage.ConceptId, membership.ConceptId, conceptGraph);
                        issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.IdentityConflict, reference.Path, "A bridge conflicts with a published concept identifier."));
                        continue;
                    }

                    if (mappedPage.Landing != membership.SectionLanding && membership.SectionLanding.HasValue && release.IdentityState is DocConceptIdentityState.ValidPartial or DocConceptIdentityState.ValidComplete)
                    {
                        strictValid = false;
                        hasUntrustedMapData = true;
                        quarantinedConceptIds.Add(membership.ConceptId);
                        quarantinedPages.Add(mappedPageKey.Value);
                        issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.IdentityConflict, reference.Path, "A bridge conflicts with published section-landing eligibility."));
                        continue;
                    }

                    if (release.IdentityState is DocConceptIdentityState.LegacyUnknown && membership.SectionLanding == true)
                    {
                        mappedPage.Landing = true;
                    }

                    if (mappedPage.ConceptId is null)
                    {
                        mappedPage.ConceptId = membership.ConceptId;
                    }
                }

                MutablePage? fallbackPage = null;
                if (membership.FallbackSectionRoute is not null
                    && !TryResolveMembershipFallback(release, membership, out fallbackPage))
                {
                    strictValid = false;
                    hasUntrustedMapData = true;
                    quarantinedConceptIds.Add(membership.ConceptId);
                    issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.CanonicalRouteInvalid, reference.Path, "A bridge fallback does not name a verified public canonical route in the selected locale."));
                    continue;
                }

                if (fallbackPage is not null)
                {
                    fallbackPage.Locale = membership.Locale;
                    fallbackPage.LocaleIsKnown = true;
                }

                var fact = new MembershipFact(membership.IsAbsent, mappedPageKey, membership.FallbackSectionRoute, membership.SectionLanding);
                if (!AddMembershipFact(conceptKey, fact, membershipFacts, conceptIdsByPage, conceptGraph, quarantinedConceptIds, quarantinedPages, issues, reference.Path, ref strictValid))
                {
                    hasUntrustedMapData = true;
                }
            }
        }

        QuarantineConnectedConceptGroups(quarantinedConceptIds, conceptGraph);

        var finalReleases = new Dictionary<ReleaseKey, DocConceptReleaseSnapshot>(ReleaseKeyComparer.Instance);
        var finalPages = new Dictionary<PageKey, DocConceptCanonicalPage>(PageKeyComparer.Instance);
        foreach (var mutableRelease in mutableReleases.Values)
        {
            var pages = mutableRelease.Pages.Select(page => page.ToImmutable()).ToArray();
            var snapshot = new DocConceptReleaseSnapshot(
                mutableRelease.Key.Version,
                mutableRelease.Key.ManifestSha256,
                mutableRelease.IsPublic,
                mutableRelease.IsAvailable,
                mutableRelease.HasDocumentation,
                mutableRelease.HasSearchIndex,
                mutableRelease.HomeRoutePath,
                mutableRelease.IdentityState,
                pages);
            finalReleases.Add(mutableRelease.Key, snapshot);
            foreach (var page in pages)
            {
                var pageKey = new PageKey(mutableRelease.Key, page.Locale, page.LocaleIsKnown, page.RoutePath);
                finalPages[pageKey] = page;
                if (quarantinedConceptIds.Contains(page.ConceptId ?? string.Empty))
                {
                    quarantinedPages.Add(pageKey);
                }
            }
        }

        var immutableMemberships = new Dictionary<ConceptMembershipKey, MembershipFact>(ConceptMembershipKeyComparer.Instance);
        foreach (var (key, fact) in membershipFacts)
        {
            immutableMemberships[key] = fact;
        }

        var index = new DocConceptIdentityIndex(finalReleases, finalPages, immutableMemberships, quarantinedConceptIds, quarantinedPages, hasUntrustedMapData);
        var isValid = mode == DocConceptValidationMode.Quarantine || strictValid && issues.Count == 0;
        return new DocConceptIndexBuildResult(index, issues, isValid);
    }

    /// <summary>Gets one verified public canonical page by exact release pin, normalized locale, and logical route.</summary>
    /// <param name="identity">Exact release and canonical route identity requested by the caller.</param>
    /// <returns>The immutable page when it exists, is public, and is serveable; otherwise null.</returns>
    public DocConceptCanonicalPage? GetPage(DocConceptPageIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!TryGetRelease(identity.Version, identity.ReleaseManifestSha256, out var release)
            || !release.IsPublic || !release.IsAvailable
            || !_pageByKey.TryGetValue(PageKey.From(release, identity.Locale, identity.LocaleIsKnown, identity.RoutePath), out var page)
            || !page.IsServeable)
        {
            return null;
        }

        return page;
    }

    /// <summary>Finds the exact public page identity for a caller-resolved route in a pinned release.</summary>
    /// <param name="version">Exact public release version.</param>
    /// <param name="releaseManifestSha256">SHA-256 pin for that release.</param>
    /// <param name="routePath">Canonical route already selected by caller-owned route authority.</param>
    /// <param name="identity">The winning route's resolved locale and exact release identity, when found.</param>
    /// <returns>Whether exactly one serveable public canonical winner matches the route.</returns>
    public bool TryGetPageIdentity(string version, string releaseManifestSha256, string routePath, out DocConceptPageIdentity? identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(releaseManifestSha256);
        ArgumentNullException.ThrowIfNull(routePath);
        identity = null;
        if (!TryGetRelease(version, releaseManifestSha256, out var release) || !release.IsPublic || !release.IsAvailable)
        {
            return false;
        }

        var matches = release.Pages.Where(page => page.IsServeable
            && string.Equals(page.RoutePath, routePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        var page = matches[0];
        identity = new DocConceptPageIdentity(release.Version, release.ReleaseManifestSha256, page.RoutePath, page.Locale, page.LocaleIsKnown);
        return true;
    }

    /// <summary>Resolves a source page against the exact selected version and release-manifest pin.</summary>
    /// <param name="sourceIdentity">Verified canonical source identity; aliases must already be resolved by the caller.</param>
    /// <param name="targetVersion">Exact version selected by the reader.</param>
    /// <param name="targetReleaseManifestSha256">Exact pinned target archive manifest digest.</param>
    /// <returns>A neutral outcome with a counterpart, honest reason, and validated target-local recovery options.</returns>
    public DocConceptResolution Resolve(
        DocConceptPageIdentity sourceIdentity,
        string targetVersion,
        string targetReleaseManifestSha256)
    {
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(targetVersion);
        ArgumentNullException.ThrowIfNull(targetReleaseManifestSha256);

        var sourceReleaseExists = TryGetRelease(sourceIdentity.Version, sourceIdentity.ReleaseManifestSha256, out var sourceRelease);
        var sourcePage = sourceReleaseExists && sourceRelease.IsPublic && sourceRelease.IsAvailable
            ? GetStoredPage(sourceRelease, sourceIdentity.Locale, sourceIdentity.LocaleIsKnown, sourceIdentity.RoutePath)
            : null;
        if (!TryGetRelease(targetVersion, targetReleaseManifestSha256, out var targetRelease)
            || !targetRelease.IsPublic)
        {
            return CreateResolution(DocConceptResolutionReason.TargetUnavailable, sourceIdentity, sourcePage, null, null, null, false, null);
        }

        if (!targetRelease.IsAvailable)
        {
            return CreateResolution(DocConceptResolutionReason.TargetUnavailable, sourceIdentity, sourcePage, null, null, null, false, null);
        }

        if (!sourceReleaseExists || !sourceRelease.IsPublic || !sourceRelease.IsAvailable || sourcePage is null || !sourcePage.IsServeable)
        {
            return CreateResolution(DocConceptResolutionReason.Unknown, sourceIdentity, sourcePage, targetRelease, null, null, false, null);
        }

        var selectedLocalePages = targetRelease.Pages
            .Where(page => page.IsServeable && page.LocaleIsKnown && LocaleEquals(page.Locale, sourceIdentity.Locale, sourceIdentity.LocaleIsKnown))
            .ToArray();
        if (!sourceIdentity.LocaleIsKnown)
        {
            return CreateRecovery(DocConceptResolutionReason.Unknown, sourceIdentity, sourcePage, targetRelease, null, allowLocalActions: false);
        }

        var sourcePageKey = PageKey.From(sourceRelease, sourcePage.Locale, sourcePage.LocaleIsKnown, sourcePage.RoutePath);
        var sourceConceptId = sourcePage.ConceptId;
        if (selectedLocalePages.Length == 0)
        {
            // A pinned bridge's explicit absence is locale-scoped evidence even when a legacy release has no
            // trustworthy per-page locale facts to attach. It can establish absence, but cannot authorize any
            // target-local recovery link or search action without a verified page in that locale.
            if (sourceConceptId is not null
                && _membershipByConcept.TryGetValue(
                    new ConceptMembershipKey(new ReleaseKey(targetRelease.Version, targetRelease.ReleaseManifestSha256), sourceIdentity.Locale, sourceConceptId),
                    out var localeMembership)
                && localeMembership.IsAbsent)
            {
                return CreateResolution(DocConceptResolutionReason.KnownAbsent, sourceIdentity, sourcePage, targetRelease, null, null, false, null);
            }

            var targetHasUnknownLocale = targetRelease.Pages.Any(page => page.IsServeable && !page.LocaleIsKnown);
            var reason = targetHasUnknownLocale && targetRelease.IdentityState != DocConceptIdentityState.ValidComplete
                ? DocConceptResolutionReason.Unknown
                : DocConceptResolutionReason.NoSelectedLocaleDocs;
            return CreateResolution(reason, sourceIdentity, sourcePage, targetRelease, null, null, false, null);
        }

        if (_quarantinedPages.Contains(sourcePageKey) || sourceConceptId is not null && _quarantinedConceptIds.Contains(sourceConceptId))
        {
            return CreateRecovery(DocConceptResolutionReason.Ambiguous, sourceIdentity, sourcePage, targetRelease, null, allowLocalActions: true);
        }

        if (sourceRelease.IdentityState == DocConceptIdentityState.Quarantined || targetRelease.IdentityState == DocConceptIdentityState.Quarantined)
        {
            return CreateRecovery(DocConceptResolutionReason.Unknown, sourceIdentity, sourcePage, targetRelease, null, allowLocalActions: true);
        }

        if (_hasUntrustedMapData)
        {
            return CreateRecovery(DocConceptResolutionReason.Unknown, sourceIdentity, sourcePage, targetRelease, null, allowLocalActions: true);
        }

        MembershipFact? targetMembership = null;
        if (sourceConceptId is not null)
        {
            if (_membershipByConcept.TryGetValue(new ConceptMembershipKey(new ReleaseKey(targetRelease.Version, targetRelease.ReleaseManifestSha256), sourceIdentity.Locale, sourceConceptId), out var foundMembership))
            {
                targetMembership = foundMembership;
                if (foundMembership.IsAbsent)
                {
                    return CreateRecovery(DocConceptResolutionReason.KnownAbsent, sourceIdentity, sourcePage, targetRelease, foundMembership, allowLocalActions: true);
                }

                if (foundMembership.Page is { } memberKey && _pageByKey.TryGetValue(memberKey, out var mappedTarget) && mappedTarget.IsServeable)
                {
                    return CreateCounterpart(sourceIdentity, sourcePage, targetRelease, mappedTarget);
                }

                return CreateRecovery(DocConceptResolutionReason.Unknown, sourceIdentity, sourcePage, targetRelease, foundMembership, allowLocalActions: true);
            }

            if (selectedLocalePages.Any(page => string.Equals(page.ConceptId, sourceConceptId, StringComparison.Ordinal)))
            {
                var targetPage = selectedLocalePages.Single(page => string.Equals(page.ConceptId, sourceConceptId, StringComparison.Ordinal));
                if (!targetPage.IsServeable)
                {
                    return CreateRecovery(DocConceptResolutionReason.Unknown, sourceIdentity, sourcePage, targetRelease, null, allowLocalActions: true);
                }

                return CreateCounterpart(sourceIdentity, sourcePage, targetRelease, targetPage);
            }

            var conceptExistsInOtherLocale = targetRelease.Pages.Any(page => page.LocaleIsKnown && !LocaleEquals(page.Locale, sourceIdentity.Locale, sourceIdentity.LocaleIsKnown)
                && string.Equals(page.ConceptId, sourceConceptId, StringComparison.Ordinal));
            if (conceptExistsInOtherLocale)
            {
                return CreateResolution(DocConceptResolutionReason.NoSelectedLocaleDocs, sourceIdentity, sourcePage, targetRelease, null, null, false, null);
            }

            if (targetRelease.IdentityState == DocConceptIdentityState.ValidComplete)
            {
                return CreateRecovery(DocConceptResolutionReason.KnownAbsent, sourceIdentity, sourcePage, targetRelease, null, allowLocalActions: true);
            }
        }

        var targetRoute = selectedLocalePages.FirstOrDefault(page => string.Equals(page.RoutePath, sourcePage.RoutePath, StringComparison.OrdinalIgnoreCase));
        if (targetRoute is not null)
        {
            var targetRouteKey = PageKey.From(targetRelease, targetRoute.Locale, targetRoute.LocaleIsKnown, targetRoute.RoutePath);
            var contradictoryIdentity = sourceConceptId is not null && targetRoute.ConceptId is not null
                && !string.Equals(sourceConceptId, targetRoute.ConceptId, StringComparison.Ordinal);
            var completeIndexContradiction = sourceConceptId is not null
                && targetRelease.IdentityState == DocConceptIdentityState.ValidComplete
                && targetRoute.ConceptId is null;
            var quarantinedTarget = _quarantinedPages.Contains(targetRouteKey)
                || targetRoute.ConceptId is not null && _quarantinedConceptIds.Contains(targetRoute.ConceptId);
            if (!contradictoryIdentity && !completeIndexContradiction && !quarantinedTarget && targetRoute.IsServeable)
            {
                return CreateCounterpart(sourceIdentity, sourcePage, targetRelease, targetRoute);
            }
        }

        return CreateRecovery(DocConceptResolutionReason.Unknown, sourceIdentity, sourcePage, targetRelease, targetMembership, allowLocalActions: true);
    }

    /// <summary>Enumerates one resolution per selected exact target without retaining a result matrix.</summary>
    /// <param name="sourceIdentity">Verified canonical source page.</param>
    /// <param name="targets">Selected public or unavailable exact version pins in caller order.</param>
    /// <returns>Lazy neutral switch outcomes in target order.</returns>
    public IEnumerable<DocConceptResolution> EnumerateOutgoing(
        DocConceptPageIdentity sourceIdentity,
        IEnumerable<(string Version, string ReleaseManifestSha256)> targets)
    {
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var target in targets)
        {
            yield return Resolve(sourceIdentity, target.Version, target.ReleaseManifestSha256);
        }
    }

    /// <summary>
    /// Lazily enumerates destination-bound source contexts. Only validated source pages whose exact resolver result lands
    /// on the destination as a counterpart are returned; unknown, stale, unrelated, and browsing-fallback outcomes are
    /// omitted. The caller binds the returned rows to this exact destination page in its inert page-local projection.
    /// </summary>
    /// <param name="destinationIdentity">Exact canonical destination page for the current served document.</param>
    /// <param name="sourceIdentities">Candidate verified public source pages from the immutable catalog snapshot.</param>
    /// <param name="instanceId">Stable public Docs instance identifier included in each source key.</param>
    /// <returns>Lazy incoming source contexts; no lookup result cache is retained.</returns>
    public IEnumerable<DocConceptIncomingSource> GetIncoming(
        DocConceptPageIdentity destinationIdentity,
        IEnumerable<DocConceptPageIdentity> sourceIdentities,
        string instanceId)
    {
        ArgumentNullException.ThrowIfNull(destinationIdentity);
        ArgumentNullException.ThrowIfNull(sourceIdentities);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var destinationPage = GetPage(destinationIdentity);
        if (destinationPage is null)
        {
            yield break;
        }

        foreach (var sourceIdentity in sourceIdentities)
        {
            if (sourceIdentity is null
                || string.Equals(sourceIdentity.Version, destinationIdentity.Version, StringComparison.Ordinal)
                && string.Equals(sourceIdentity.ReleaseManifestSha256, destinationIdentity.ReleaseManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolution = Resolve(sourceIdentity, destinationIdentity.Version, destinationIdentity.ReleaseManifestSha256);
            if (resolution.TargetPage is null || !SamePage(resolution.TargetPage, destinationPage))
            {
                continue;
            }

            var sourcePage = GetPage(sourceIdentity);
            if (sourcePage is null)
            {
                continue;
            }

            yield return new DocConceptIncomingSource(
                DocConceptSourceKey.Create(instanceId, sourceIdentity),
                sourceIdentity,
                sourcePage,
                resolution.Reason);
        }
    }

    /// <summary>Computes a public stable source lookup key for a canonical page in this immutable snapshot.</summary>
    /// <param name="instanceId">Stable named Docs instance identifier; it is data, not an authorization token.</param>
    /// <param name="sourceIdentity">Exact source release pin, known locale state, and resolved canonical route.</param>
    /// <returns>Lowercase SHA-256 lookup key using an unambiguous length-prefixed UTF-8 tuple encoding.</returns>
    public static string CreateSourceKey(string instanceId, DocConceptPageIdentity sourceIdentity) =>
        DocConceptSourceKey.Create(instanceId, sourceIdentity);

    /// <summary>Returns all public available pages as a lazy canonical candidate sequence.</summary>
    /// <returns>Public canonical pages from available releases, in catalog order.</returns>
    public IEnumerable<(DocConceptPageIdentity Identity, DocConceptCanonicalPage Page)> EnumeratePublicPages()
    {
        foreach (var release in _releases)
        {
            if (!release.IsPublic || !release.IsAvailable)
            {
                continue;
            }

            foreach (var page in release.Pages)
            {
                if (!page.IsServeable)
                {
                    continue;
                }

                yield return (new DocConceptPageIdentity(release.Version, release.ReleaseManifestSha256, page.RoutePath, page.Locale, page.LocaleIsKnown), page);
            }
        }
    }

    /// <summary>Builds a neutral counterpart result and derives only location/title reason from verified endpoint facts.</summary>
    private static DocConceptResolution CreateCounterpart(
        DocConceptPageIdentity sourceIdentity,
        DocConceptCanonicalPage sourcePage,
        DocConceptReleaseSnapshot targetRelease,
        DocConceptCanonicalPage targetPage)
    {
        var reason = !string.Equals(sourcePage.RoutePath, targetPage.RoutePath, StringComparison.OrdinalIgnoreCase)
            ? DocConceptResolutionReason.MovedPage
            : !string.Equals(sourcePage.Title, targetPage.Title, StringComparison.Ordinal)
                ? DocConceptResolutionReason.RenamedPage
                : DocConceptResolutionReason.SamePage;
        return new DocConceptResolution(reason, sourceIdentity, sourcePage, targetRelease, targetPage, null, false, null);
    }

    /// <summary>Builds a recovery result and applies the approved authored-first then longest-ancestor fallback rule.</summary>
    private DocConceptResolution CreateRecovery(
        DocConceptResolutionReason reason,
        DocConceptPageIdentity sourceIdentity,
        DocConceptCanonicalPage? sourcePage,
        DocConceptReleaseSnapshot targetRelease,
        MembershipFact? targetMembership,
        bool allowLocalActions)
    {
        if (!allowLocalActions || !HasSelectedLocalePages(targetRelease, sourceIdentity.Locale, sourceIdentity.LocaleIsKnown))
        {
            return CreateResolution(reason, sourceIdentity, sourcePage, targetRelease, null, null, false, null);
        }

        DocConceptCanonicalPage? section = null;
        if (targetMembership?.FallbackSectionRoute is { } authoredRoute)
        {
            section = targetRelease.Pages.FirstOrDefault(page =>
                page.IsServeable
                && page.LocaleIsKnown
                && LocaleEquals(page.Locale, sourceIdentity.Locale, sourceIdentity.LocaleIsKnown)
                && !IsPageQuarantined(targetRelease, page)
                && string.Equals(page.RoutePath, authoredRoute, StringComparison.OrdinalIgnoreCase));
        }

        if (section is null && sourcePage is not null)
        {
            section = targetRelease.Pages
                .Where(page => page.IsServeable
                    && page.Landing
                    && page.LocaleIsKnown
                    && LocaleEquals(page.Locale, sourceIdentity.Locale, sourceIdentity.LocaleIsKnown)
                    && !IsPageQuarantined(targetRelease, page)
                    && IsStrictNonRootAncestor(page.RoutePath, sourcePage.RoutePath))
                .OrderByDescending(page => SegmentCount(page.RoutePath))
                .FirstOrDefault();
        }

        var hasLocalDocs = HasSelectedLocalePages(targetRelease, sourceIdentity.Locale, sourceIdentity.LocaleIsKnown);
        var searchAvailable = hasLocalDocs && targetRelease.HasSearchIndex;
        var homePage = hasLocalDocs && targetRelease.HomeRoutePath is { } candidateHome
            ? targetRelease.Pages.FirstOrDefault(page => page.IsServeable
                && page.LocaleIsKnown
                && LocaleEquals(page.Locale, sourceIdentity.Locale, sourceIdentity.LocaleIsKnown)
                && !IsPageQuarantined(targetRelease, page)
                && string.Equals(page.RoutePath, candidateHome, StringComparison.OrdinalIgnoreCase))
            : null;
        return CreateResolution(reason, sourceIdentity, sourcePage, targetRelease, null, section, searchAvailable, homePage?.RoutePath);
    }

    /// <summary>Creates a result with no target-local actions when no selected-locale route is verified.</summary>
    private static DocConceptResolution CreateResolution(
        DocConceptResolutionReason reason,
        DocConceptPageIdentity sourceIdentity,
        DocConceptCanonicalPage? sourcePage,
        DocConceptReleaseSnapshot? targetRelease,
        DocConceptCanonicalPage? targetPage,
        DocConceptCanonicalPage? fallbackSection,
        bool searchAvailable,
        string? homeRoutePath) =>
        new(reason, sourceIdentity, sourcePage, targetRelease, targetPage, fallbackSection, searchAvailable, homeRoutePath);

    /// <summary>Tests whether an exact release identity exists in the snapshot.</summary>
    private bool TryGetRelease(string version, string manifestSha256, out DocConceptReleaseSnapshot release)
    {
        release = null!;
        if (!DocConceptSchema.IsSha256(manifestSha256))
        {
            return false;
        }

        return _releaseByKey.TryGetValue(new ReleaseKey(version, manifestSha256.ToLowerInvariant()), out release!);
    }

    /// <summary>Looks up a page even when unavailable for public resolution, for internal source diagnosis only.</summary>
    private DocConceptCanonicalPage? GetStoredPage(DocConceptReleaseSnapshot release, string? locale, bool localeIsKnown, string routePath)
    {
        return _pageByKey.TryGetValue(PageKey.From(release, locale, localeIsKnown, routePath), out var page) ? page : null;
    }

    /// <summary>Checks whether a release contains at least one canonical page in the selected known locale.</summary>
    private static bool HasSelectedLocalePages(DocConceptReleaseSnapshot release, string? locale, bool localeIsKnown) =>
        localeIsKnown && release.Pages.Any(page => page.IsServeable && page.LocaleIsKnown && LocaleEquals(page.Locale, locale, true));

    /// <summary>Returns whether a page belongs to a quarantined concept or a quarantined canonical conflict.</summary>
    private bool IsPageQuarantined(DocConceptReleaseSnapshot release, DocConceptCanonicalPage page) =>
        _quarantinedPages.Contains(PageKey.From(release, page.Locale, page.LocaleIsKnown, page.RoutePath))
        || page.ConceptId is not null && _quarantinedConceptIds.Contains(page.ConceptId);

    /// <summary>Compares known normalized locale identity without case folding or default-locale substitution.</summary>
    private static bool LocaleEquals(string? left, string? right, bool localeIsKnown) =>
        localeIsKnown && string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>Compares two canonical pages by exact release identity, exact locale, and routing-equivalent path.</summary>
    private static bool SamePage(DocConceptCanonicalPage left, DocConceptCanonicalPage right) =>
        left.LocaleIsKnown == right.LocaleIsKnown
        && left.LocaleIsKnown
        && LocaleEquals(left.Locale, right.Locale, true)
        && string.Equals(left.RoutePath, right.RoutePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns whether a candidate route is a strict path-segment ancestor, excluding the docs root.</summary>
    private static bool IsStrictNonRootAncestor(string ancestor, string route)
    {
        if (ancestor.Length == 0 || string.Equals(ancestor, route, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return route.Length > ancestor.Length
            && route.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
            && route[ancestor.Length] == '/';
    }

    /// <summary>Counts route segments for the deepest verified section-landing selection.</summary>
    private static int SegmentCount(string routePath) => routePath.Length == 0 ? 0 : routePath.Count(character => character == '/') + 1;

    /// <summary>Validates canonical facts before the loader uses them as a route-authority boundary.</summary>
    private static bool TryValidateCanonicalRouteFacts(
        IReadOnlyList<DocConceptCanonicalRouteFact> canonicalRoutes,
        out Dictionary<string, DocConceptCanonicalRouteFact> routesByPath,
        out DocConceptValidationIssue? issue)
    {
        routesByPath = new Dictionary<string, DocConceptCanonicalRouteFact>(StringComparer.OrdinalIgnoreCase);
        issue = null;
        foreach (var fact in canonicalRoutes)
        {
            if (fact is null || !DocConceptSchema.IsSafeRoutePath(fact.RoutePath)
                || fact.ArtifactPath is not null && !DocConceptSchema.IsSafeArtifactPath(fact.ArtifactPath)
                || fact.IsServeable && string.IsNullOrWhiteSpace(fact.ArtifactPath))
            {
                issue = new DocConceptValidationIssue(DocConceptValidationFailure.CanonicalRouteInvalid, "canonical-route-inventory", "A verified canonical route or physical artifact mapping is unsafe or incomplete.");
                return false;
            }

            if (fact.LocaleIsKnown && fact.Locale is not null && (fact.Locale.Length == 0 || !string.Equals(fact.Locale, fact.Locale.Trim(), StringComparison.Ordinal)))
            {
                issue = new DocConceptValidationIssue(DocConceptValidationFailure.CanonicalRouteInvalid, "canonical-route-inventory", "A verified route locale is not normalized.");
                return false;
            }

            if (!routesByPath.TryAdd(fact.RoutePath, fact))
            {
                issue = new DocConceptValidationIssue(DocConceptValidationFailure.IdentityConflict, "canonical-route-inventory", "The canonical route inventory contains a case-insensitive duplicate.");
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates a caller-provided home route against its canonical serving inventory.</summary>
    private static string? ResolveHomeRoute(string? routePath, IReadOnlyList<MutablePage> pages) =>
        routePath is not null && DocConceptSchema.IsSafeRoutePath(routePath)
            && pages.Any(page => page.IsServeable && string.Equals(page.RoutePath, routePath, StringComparison.OrdinalIgnoreCase))
                ? pages.First(page => page.IsServeable && string.Equals(page.RoutePath, routePath, StringComparison.OrdinalIgnoreCase)).RoutePath
                : null;

    /// <summary>Finds a bridge page and fallback in one exact verified release and locale.</summary>
    private static bool TryResolveMembershipPage(
        MutableRelease release,
        DocConceptBridgeMembership membership,
        out MutablePage? page,
        out DocConceptValidationFailure failure)
    {
        page = null;
        failure = DocConceptValidationFailure.CanonicalRouteInvalid;
        if (!release.IsPublic || !release.IsAvailable)
        {
            failure = DocConceptValidationFailure.ReleasePinInvalid;
            return false;
        }

        var candidates = release.Pages.Where(candidate =>
                string.Equals(candidate.RoutePath, membership.CanonicalRoutePath, StringComparison.OrdinalIgnoreCase)
                && candidate.IsServeable)
            .ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        page = candidates[0];
        if (page.LocaleIsKnown)
        {
            if (!string.Equals(page.Locale, membership.Locale, StringComparison.Ordinal))
            {
                page = null;
                return false;
            }
        }
        else
        {
            page.Locale = membership.Locale;
            page.LocaleIsKnown = true;
        }

        return true;
    }

    /// <summary>Finds an authored fallback in the exact public release and selected locale.</summary>
    private static bool TryResolveMembershipFallback(MutableRelease release, DocConceptBridgeMembership membership, out MutablePage? fallback)
    {
        fallback = null;
        if (!release.IsPublic || !release.IsAvailable || membership.FallbackSectionRoute is null)
        {
            return false;
        }

        var candidates = release.Pages.Where(candidate => candidate.IsServeable
            && string.Equals(candidate.RoutePath, membership.FallbackSectionRoute, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        fallback = candidates[0];
        if (fallback.LocaleIsKnown)
        {
            return string.Equals(fallback.Locale, membership.Locale, StringComparison.Ordinal);
        }

        fallback.Locale = membership.Locale;
        fallback.LocaleIsKnown = true;
        return true;
    }

    /// <summary>Adds embedded page identity as a release/locale membership before legacy bridges are merged.</summary>
    private static void AddEmbeddedMembership(
        PageKey pageKey,
        MutablePage page,
        IDictionary<ConceptMembershipKey, MembershipFact> membershipFacts,
        IDictionary<PageKey, HashSet<string>> conceptIdsByPage,
        IDictionary<string, HashSet<string>> conceptGraph,
        ISet<string> quarantinedConceptIds,
        ISet<PageKey> quarantinedPages,
        ICollection<DocConceptValidationIssue> issues,
        ref bool strictValid)
    {
        var conceptId = page.ConceptId!;
        var membershipKey = new ConceptMembershipKey(pageKey.Release, page.Locale, conceptId);
        var fact = new MembershipFact(false, pageKey, null, page.Landing);
        if (!membershipFacts.TryAdd(membershipKey, fact))
        {
            strictValid = false;
            quarantinedConceptIds.Add(conceptId);
            quarantinedPages.Add(pageKey);
            issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.IdentityConflict, "concept-index", "One concept has multiple canonical pages in the same release and locale."));
        }

        AddConceptPageAssignment(pageKey, conceptId, conceptIdsByPage, conceptGraph, quarantinedConceptIds, quarantinedPages, issues, "concept-index", ref strictValid);
    }

    /// <summary>Adds or compares one pinned bridge membership without choosing a declaration-order winner.</summary>
    private static bool AddMembershipFact(
        ConceptMembershipKey key,
        MembershipFact fact,
        IDictionary<ConceptMembershipKey, MembershipFact> membershipFacts,
        IDictionary<PageKey, HashSet<string>> conceptIdsByPage,
        IDictionary<string, HashSet<string>> conceptGraph,
        ISet<string> quarantinedConceptIds,
        ISet<PageKey> quarantinedPages,
        ICollection<DocConceptValidationIssue> issues,
        string artifact,
        ref bool strictValid)
    {
        if (membershipFacts.TryGetValue(key, out var existing))
        {
            if (!existing.IsCompatibleWith(fact))
            {
                quarantinedConceptIds.Add(key.ConceptId);
                if (existing.Page is { } existingPage)
                {
                    quarantinedPages.Add(existingPage);
                    LinkPageConcepts(key.ConceptId, existingPage, conceptIdsByPage, conceptGraph);
                }

                if (fact.Page is { } newPage)
                {
                    quarantinedPages.Add(newPage);
                    LinkPageConcepts(key.ConceptId, newPage, conceptIdsByPage, conceptGraph);
                }

                strictValid = false;
                issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.IdentityConflict, artifact, "Pinned bridge maps contain contradictory facts for one concept membership."));
                return false;
            }

            membershipFacts[key] = existing.Merge(fact);
        }
        else
        {
            membershipFacts.Add(key, fact);
        }

        if (fact.Page is { } pageKey)
        {
            AddConceptPageAssignment(pageKey, key.ConceptId, conceptIdsByPage, conceptGraph, quarantinedConceptIds, quarantinedPages, issues, artifact, ref strictValid);
        }

        return true;
    }

    /// <summary>Connects a conflicting concept to every other identity assigned to either affected canonical page.</summary>
    private static void LinkPageConcepts(
        string conceptId,
        PageKey pageKey,
        IDictionary<PageKey, HashSet<string>> conceptIdsByPage,
        IDictionary<string, HashSet<string>> conceptGraph)
    {
        if (!conceptIdsByPage.TryGetValue(pageKey, out var relatedConcepts))
        {
            return;
        }

        foreach (var relatedConceptId in relatedConcepts)
        {
            if (!string.Equals(conceptId, relatedConceptId, StringComparison.Ordinal))
            {
                LinkConcepts(conceptId, relatedConceptId, conceptGraph);
            }
        }
    }

    /// <summary>Tracks page-to-concept assignments and quarantines all concepts sharing an ambiguous page.</summary>
    private static void AddConceptPageAssignment(
        PageKey pageKey,
        string conceptId,
        IDictionary<PageKey, HashSet<string>> conceptIdsByPage,
        IDictionary<string, HashSet<string>> conceptGraph,
        ISet<string> quarantinedConceptIds,
        ISet<PageKey> quarantinedPages,
        ICollection<DocConceptValidationIssue> issues,
        string artifact,
        ref bool strictValid)
    {
        if (!conceptIdsByPage.TryGetValue(pageKey, out var ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            conceptIdsByPage.Add(pageKey, ids);
        }

        foreach (var existingConceptId in ids)
        {
            if (string.Equals(existingConceptId, conceptId, StringComparison.Ordinal))
            {
                continue;
            }

            strictValid = false;
            quarantinedConceptIds.Add(existingConceptId);
            quarantinedConceptIds.Add(conceptId);
            quarantinedPages.Add(pageKey);
            LinkConcepts(existingConceptId, conceptId, conceptGraph);
            issues.Add(new DocConceptValidationIssue(DocConceptValidationFailure.IdentityConflict, artifact, "One canonical page is assigned to more than one concept."));
        }

        ids.Add(conceptId);
    }

    /// <summary>Connects concepts that share an ambiguous canonical page so quarantine reaches the full group.</summary>
    private static void LinkConcepts(string first, string second, IDictionary<string, HashSet<string>> graph)
    {
        if (!graph.TryGetValue(first, out var firstLinks))
        {
            firstLinks = new HashSet<string>(StringComparer.Ordinal);
            graph.Add(first, firstLinks);
        }

        if (!graph.TryGetValue(second, out var secondLinks))
        {
            secondLinks = new HashSet<string>(StringComparer.Ordinal);
            graph.Add(second, secondLinks);
        }

        firstLinks.Add(second);
        secondLinks.Add(first);
    }

    /// <summary>Expands quarantine seeds through every concept edge created by ambiguous page assignments.</summary>
    private static void QuarantineConnectedConceptGroups(ISet<string> quarantinedConceptIds, IReadOnlyDictionary<string, HashSet<string>> graph)
    {
        var pending = new Queue<string>(quarantinedConceptIds);
        while (pending.TryDequeue(out var current))
        {
            if (!graph.TryGetValue(current, out var related))
            {
                continue;
            }

            foreach (var conceptId in related)
            {
                if (quarantinedConceptIds.Add(conceptId))
                {
                    pending.Enqueue(conceptId);
                }
            }
        }
    }

    /// <summary>Checks the map reference path remains relative and confined before it participates in deduplication.</summary>
    private static bool IsSafeRelativeMapPath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Contains('\\')
        && !path.Contains('?')
        && !path.Contains('#')
        && !path.Any(char.IsControl)
        && !path.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..");

    /// <summary>Normalizes exact digest spelling into a release key without changing the version identifier.</summary>
    private readonly record struct ReleaseKey(string Version, string ManifestSha256);

    /// <summary>Identifies one route/locale membership in an exact release.</summary>
    private readonly record struct PageKey(ReleaseKey Release, string? Locale, bool LocaleIsKnown, string RoutePath)
    {
        public static PageKey From(DocConceptReleaseSnapshot release, string? locale, bool localeIsKnown, string routePath) =>
            new(new ReleaseKey(release.Version, release.ReleaseManifestSha256.ToLowerInvariant()), locale, localeIsKnown, routePath);
    }

    /// <summary>Identifies one concept's exact membership in a pinned release and normalized locale.</summary>
    private readonly record struct ConceptMembershipKey(ReleaseKey Release, string? Locale, string ConceptId);

    /// <summary>Identifies one path/digest pair for deterministic bridge-reference deduplication.</summary>
    private readonly record struct MapReferenceKey(string Path, string Sha256);

    /// <summary>Captures either an exact page membership or explicit absence and optional recovery facts.</summary>
    private sealed record MembershipFact(bool IsAbsent, PageKey? Page, string? FallbackSectionRoute, bool? SectionLanding)
    {
        public bool IsCompatibleWith(MembershipFact other)
        {
            if (IsAbsent != other.IsAbsent || !NullablePageKeyEquals(Page, other.Page))
            {
                return false;
            }

            if (FallbackSectionRoute is not null && other.FallbackSectionRoute is not null
                && !string.Equals(FallbackSectionRoute, other.FallbackSectionRoute, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return SectionLanding is null || other.SectionLanding is null || SectionLanding == other.SectionLanding;
        }

        public MembershipFact Merge(MembershipFact other) => new(
            IsAbsent,
            Page ?? other.Page,
            FallbackSectionRoute ?? other.FallbackSectionRoute,
            SectionLanding ?? other.SectionLanding);
    }

    /// <summary>Compares optional membership pages using release-pin, locale, and logical-route equality.</summary>
    private static bool NullablePageKeyEquals(PageKey? left, PageKey? right) =>
        left.HasValue == right.HasValue
        && (!left.HasValue || PageKeyComparer.Instance.Equals(left.Value, right!.Value));

    /// <summary>Holds detached mutable construction facts until all map conflicts have been resolved.</summary>
    private sealed class MutableRelease(
        ReleaseKey key,
        bool isPublic,
        bool isAvailable,
        bool hasDocumentation,
        bool hasSearchIndex,
        string? homeRoutePath,
        DocConceptIdentityState identityState,
        List<MutablePage> pages,
        IReadOnlyDictionary<string, DocConceptCanonicalRouteFact> routeFacts)
    {
        public ReleaseKey Key { get; } = key;
        public bool IsPublic { get; } = isPublic;
        public bool IsAvailable { get; } = isAvailable;
        public bool HasDocumentation { get; } = hasDocumentation;
        public bool HasSearchIndex { get; } = hasSearchIndex;
        public string? HomeRoutePath { get; } = homeRoutePath;
        public DocConceptIdentityState IdentityState { get; } = identityState;
        public List<MutablePage> Pages { get; } = pages;
        public IReadOnlyDictionary<string, DocConceptCanonicalRouteFact> RouteFacts { get; } = routeFacts;
    }

    /// <summary>Mutable only inside the loader; released snapshots are detached immutable records.</summary>
    private sealed class MutablePage
    {
        public MutablePage(DocConceptCanonicalPage page)
        {
            RoutePath = page.RoutePath;
            ArtifactPath = page.ArtifactPath;
            Locale = page.Locale;
            LocaleIsKnown = page.LocaleIsKnown;
            Title = page.Title;
            Anchors = page.Anchors.ToArray();
            Landing = page.Landing;
            ConceptId = page.ConceptId;
            IsServeable = page.IsServeable;
        }

        public MutablePage(string routePath, string? artifactPath, string? locale, bool localeIsKnown, string title, IReadOnlyList<string> anchors, bool landing, string? conceptId, bool isServeable)
        {
            RoutePath = routePath;
            ArtifactPath = artifactPath;
            Locale = locale;
            LocaleIsKnown = localeIsKnown;
            Title = title;
            Anchors = anchors.ToArray();
            Landing = landing;
            ConceptId = conceptId;
            IsServeable = isServeable;
        }

        public string RoutePath { get; }
        public string? ArtifactPath { get; }
        public string? Locale { get; set; }
        public bool LocaleIsKnown { get; set; }
        public string Title { get; }
        public string[] Anchors { get; }
        public bool Landing { get; set; }
        public string? ConceptId { get; set; }
        public bool IsServeable { get; }

        public DocConceptCanonicalPage ToImmutable() => new(RoutePath, ArtifactPath, Locale, LocaleIsKnown, Title, Anchors, Landing, ConceptId, IsServeable);
    }

    /// <summary>Comparer that preserves exact release version and pin while normalizing hexadecimal pin case.</summary>
    private sealed class ReleaseKeyComparer : IEqualityComparer<ReleaseKey>
    {
        public static ReleaseKeyComparer Instance { get; } = new();
        public bool Equals(ReleaseKey left, ReleaseKey right) => string.Equals(left.Version, right.Version, StringComparison.Ordinal)
            && string.Equals(left.ManifestSha256, right.ManifestSha256, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(ReleaseKey value) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Version), StringComparer.OrdinalIgnoreCase.GetHashCode(value.ManifestSha256));
    }

    /// <summary>Comparer that applies exact locale identity and case-insensitive canonical route identity.</summary>
    private sealed class PageKeyComparer : IEqualityComparer<PageKey>
    {
        public static PageKeyComparer Instance { get; } = new();
        public bool Equals(PageKey left, PageKey right) => ReleaseKeyComparer.Instance.Equals(left.Release, right.Release)
            && left.LocaleIsKnown == right.LocaleIsKnown
            && string.Equals(left.Locale, right.Locale, StringComparison.Ordinal)
            && string.Equals(left.RoutePath, right.RoutePath, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(PageKey value) => HashCode.Combine(
            ReleaseKeyComparer.Instance.GetHashCode(value.Release),
            value.Locale is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Locale),
            value.LocaleIsKnown,
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.RoutePath));
    }

    /// <summary>Comparer that uses ordinal concept IDs and locale values.</summary>
    private sealed class ConceptMembershipKeyComparer : IEqualityComparer<ConceptMembershipKey>
    {
        public static ConceptMembershipKeyComparer Instance { get; } = new();
        public bool Equals(ConceptMembershipKey left, ConceptMembershipKey right) => ReleaseKeyComparer.Instance.Equals(left.Release, right.Release)
            && string.Equals(left.Locale, right.Locale, StringComparison.Ordinal)
            && string.Equals(left.ConceptId, right.ConceptId, StringComparison.Ordinal);
        public int GetHashCode(ConceptMembershipKey value) => HashCode.Combine(
            ReleaseKeyComparer.Instance.GetHashCode(value.Release),
            value.Locale is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Locale),
            StringComparer.Ordinal.GetHashCode(value.ConceptId));
    }

    /// <summary>Comparer that deduplicates descriptor references by exact path and case-insensitive digest hex.</summary>
    private sealed class MapReferenceKeyComparer : IEqualityComparer<MapReferenceKey>
    {
        public static MapReferenceKeyComparer Instance { get; } = new();
        public bool Equals(MapReferenceKey left, MapReferenceKey right) => string.Equals(left.Path, right.Path, StringComparison.Ordinal)
            && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(MapReferenceKey value) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Path), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Sha256));
    }

}

/// <summary>Computes stable public source keys without using caller-controlled URLs or ambiguous tuple delimiters.</summary>
internal static class DocConceptSourceKey
{
    /// <summary>Hashes an exact source identity with a versioned, length-prefixed UTF-8 tuple encoding.</summary>
    /// <param name="instanceId">Stable public Docs instance identifier.</param>
    /// <param name="sourceIdentity">Exact release pin, canonical route, and known locale identity.</param>
    /// <returns>Lowercase hexadecimal SHA-256 source key.</returns>
    public static string Create(string instanceId, DocConceptPageIdentity sourceIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(sourceIdentity);
        if (string.IsNullOrWhiteSpace(sourceIdentity.Version)
            || !DocConceptSchema.IsSha256(sourceIdentity.ReleaseManifestSha256)
            || !DocConceptSchema.IsSafeRoutePath(sourceIdentity.RoutePath))
        {
            throw new ArgumentException("A source key requires a pinned exact release and verified canonical route.", nameof(sourceIdentity));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, "appsurface-docs-concept-source-v1");
        AppendField(hash, instanceId);
        AppendField(hash, sourceIdentity.Version);
        AppendField(hash, sourceIdentity.ReleaseManifestSha256.ToLowerInvariant());
        AppendField(hash, sourceIdentity.LocaleIsKnown ? sourceIdentity.Locale is null ? "nonlocalized" : "locale" : "unknown-locale");
        AppendField(hash, sourceIdentity.Locale ?? string.Empty);
        AppendField(hash, sourceIdentity.RoutePath);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Appends one UTF-8 field with a fixed-width big-endian byte length.</summary>
    private static void AppendField(IncrementalHash hash, string field)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(field);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
