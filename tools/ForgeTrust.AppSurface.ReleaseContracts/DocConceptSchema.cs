using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.ReleaseContracts;

/// <summary>Detached parsed representation of one release concept sidecar.</summary>
internal sealed class DocConceptSidecarSnapshot
{
    internal DocConceptSidecarSnapshot(DocConceptIdentityState identityState, IReadOnlyList<DocConceptCanonicalPage> pages)
    {
        IdentityState = identityState;
        Pages = new ReadOnlyCollection<DocConceptCanonicalPage>(pages.ToArray());
    }

    /// <summary>Gets whether this sidecar provides complete or partial trusted identity coverage.</summary>
    public DocConceptIdentityState IdentityState { get; }

    /// <summary>Gets the detached public canonical page inventory described by the sidecar.</summary>
    public IReadOnlyList<DocConceptCanonicalPage> Pages { get; }
}

/// <summary>One parsed, digest-pinned legacy bridge membership.</summary>
internal sealed record DocConceptBridgeMembership(
    string ConceptId,
    string Version,
    string ReleaseManifestSha256,
    string? Locale,
    bool IsAbsent,
    string? CanonicalRoutePath,
    string? FallbackSectionRoute,
    bool? SectionLanding);

/// <summary>Strictly parses the small JSON schemas used by the shared concept core.</summary>
internal static class DocConceptSchema
{
    /// <summary>Gets the embedded-index schema identifier.</summary>
    public const string IndexSchema = "appsurface-docs-concepts-v1";

    /// <summary>Gets the catalog-wide bridge schema identifier.</summary>
    public const string MapSchema = "appsurface-docs-concept-map-v1";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Validates a concept identifier and returns its approved trimmed spelling.</summary>
    /// <param name="value">Authored or serialized identifier.</param>
    /// <param name="conceptId">Trimmed identifier when valid, or null for absent/blank values.</param>
    /// <returns>Whether a nonblank value satisfies the 1–128 character ASCII grammar.</returns>
    public static bool TryNormalizeConceptId(string? value, out string? conceptId)
    {
        conceptId = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is < 1 or > 128 || !IsAsciiAlphaNumeric(trimmed[0]) || !IsAsciiAlphaNumeric(trimmed[^1]))
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!IsAsciiAlphaNumeric(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        conceptId = trimmed;
        return true;
    }

    /// <summary>Parses a versioned sidecar and verifies exact coverage against caller-supplied canonical winners.</summary>
    /// <param name="bytes">Exact UTF-8 bytes already verified against the archive manifest.</param>
    /// <param name="canonicalRoutes">Public canonical winners and physical mappings from the verified frozen manifest.</param>
    /// <param name="maximumBytes">Effective shared per-artifact UTF-8 budget.</param>
    /// <param name="sidecar">Detached immutable sidecar snapshot when validation succeeds.</param>
    /// <param name="issue">Safe validation issue when parsing or cross-checking fails.</param>
    /// <returns>Whether the bytes describe a fully valid sidecar.</returns>
    public static bool TryLoadSidecar(
        ReadOnlySpan<byte> bytes,
        IReadOnlyList<DocConceptCanonicalRouteFact> canonicalRoutes,
        int maximumBytes,
        out DocConceptSidecarSnapshot? sidecar,
        out DocConceptValidationIssue? issue)
    {
        ArgumentNullException.ThrowIfNull(canonicalRoutes);
        sidecar = null;
        issue = null;

        if (!DocConceptArtifactBudget.IsValidLimit(maximumBytes))
        {
            issue = new DocConceptValidationIssue(DocConceptValidationFailure.ArtifactBudgetInvalid, "concept-index", "The configured artifact limit is outside the supported range.");
            return false;
        }

        if (!DocConceptArtifactBudget.TryMeasure(bytes, maximumBytes, out _))
        {
            issue = new DocConceptValidationIssue(DocConceptValidationFailure.ArtifactTooLarge, "concept-index", "The complete sidecar exceeds the configured UTF-8 byte limit.");
            return false;
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            ValidateNoDuplicateProperties(document.RootElement, DocConceptValidationFailure.IndexInvalid);
            EnsureObject(document.RootElement, "concept-index", "schema", "identityCoverage", "pages");
            var root = document.RootElement;
            if (!string.Equals(GetRequiredString(root, "schema"), IndexSchema, StringComparison.Ordinal))
            {
                throw new SchemaException(DocConceptValidationFailure.IndexInvalid, "The sidecar schema is unsupported.");
            }

            var coverageText = GetRequiredString(root, "identityCoverage");
            var identityState = coverageText switch
            {
                "complete" => DocConceptIdentityState.ValidComplete,
                "partial" => DocConceptIdentityState.ValidPartial,
                _ => throw new SchemaException(DocConceptValidationFailure.IndexInvalid, "The identityCoverage value is unsupported.")
            };

            var routeByExactPath = BuildCanonicalRouteMap(canonicalRoutes);
            var pageByRoute = new Dictionary<string, DocConceptCanonicalPage>(StringComparer.OrdinalIgnoreCase);
            var conceptsByLocale = new HashSet<ConceptLocaleKey>(ConceptLocaleKeyComparer.Instance);
            foreach (var element in GetRequiredArray(root, "pages").EnumerateArray())
            {
                EnsureObject(element, "concept-index-page", "canonicalRoutePath", "locale", "conceptId", "title", "anchorIds", "sectionLanding");
                var routePath = GetRequiredString(element, "canonicalRoutePath");
                if (!IsSafeRoutePath(routePath) || !routeByExactPath.TryGetValue(routePath, out var routeFact))
                {
                    throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "A sidecar page does not exactly reference a verified public canonical winner.");
                }

                if (!routeFact.IsServeable || string.IsNullOrWhiteSpace(routeFact.ArtifactPath) || !IsSafeArtifactPath(routeFact.ArtifactPath))
                {
                    throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "A sidecar page is not bound to a verified ordinary HTML artifact.");
                }

                var locale = GetRequiredNullableString(element, "locale");
                if (locale is not null && !IsNormalizedLocale(locale))
                {
                    throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "A sidecar page locale is not normalized.");
                }

                if (routeFact.LocaleIsKnown && !string.Equals(routeFact.Locale, locale, StringComparison.Ordinal))
                {
                    throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "A sidecar locale disagrees with its verified canonical route facts.");
                }

                var conceptIdValue = GetRequiredNullableString(element, "conceptId");
                if (!TryNormalizeConceptId(conceptIdValue, out var conceptId))
                {
                    throw new SchemaException(DocConceptValidationFailure.InvalidConceptId, "A sidecar contains an invalid concept identifier.");
                }

                var title = GetRequiredString(element, "title");
                var anchors = GetRequiredStringArray(element, "anchorIds");
                ValidateAnchors(anchors);
                var landing = element.TryGetProperty("sectionLanding", out var landingElement)
                    && GetBoolean(landingElement, "sectionLanding");

                var page = new DocConceptCanonicalPage(
                    routePath,
                    routeFact.ArtifactPath,
                    locale,
                    localeIsKnown: true,
                    title,
                    anchors,
                    landing,
                    conceptId,
                    isServeable: true);
                if (!pageByRoute.TryAdd(routePath, page))
                {
                    throw new SchemaException(DocConceptValidationFailure.IdentityConflict, "A sidecar repeats a canonical route.");
                }

                if (conceptId is not null && !conceptsByLocale.Add(new ConceptLocaleKey(locale, conceptId)))
                {
                    throw new SchemaException(DocConceptValidationFailure.IdentityConflict, "A release assigns one concept and locale to multiple canonical pages.");
                }
            }

            if (pageByRoute.Count != canonicalRoutes.Count)
            {
                throw new SchemaException(DocConceptValidationFailure.IndexInvalid, "The sidecar does not cover the complete public canonical inventory.");
            }

            var allHaveIds = pageByRoute.Values.All(page => page.ConceptId is not null);
            if ((identityState == DocConceptIdentityState.ValidComplete) != allHaveIds)
            {
                throw new SchemaException(DocConceptValidationFailure.IndexInvalid, "identityCoverage does not match the page concept identifiers.");
            }

            sidecar = new DocConceptSidecarSnapshot(identityState, pageByRoute.Values.ToArray());
            return true;
        }
        catch (SchemaException ex)
        {
            issue = new DocConceptValidationIssue(ex.Failure, "concept-index", ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or OverflowException)
        {
            issue = new DocConceptValidationIssue(DocConceptValidationFailure.IndexInvalid, "concept-index", "The sidecar is not valid UTF-8 JSON for the supported schema.");
            return false;
        }
    }

    /// <summary>Parses a digest-checked bridge into detached, typed membership facts.</summary>
    /// <param name="bytes">Exact UTF-8 bridge bytes.</param>
    /// <param name="maximumBytes">Effective shared per-artifact UTF-8 budget.</param>
    /// <param name="memberships">Detached parsed bridge memberships.</param>
    /// <param name="issue">Safe validation issue when parsing fails.</param>
    /// <returns>Whether the bytes describe a supported bridge.</returns>
    public static bool TryLoadMap(
        ReadOnlySpan<byte> bytes,
        int maximumBytes,
        out IReadOnlyList<DocConceptBridgeMembership> memberships,
        out DocConceptValidationIssue? issue)
    {
        memberships = Array.Empty<DocConceptBridgeMembership>();
        issue = null;
        if (!DocConceptArtifactBudget.IsValidLimit(maximumBytes))
        {
            issue = new DocConceptValidationIssue(DocConceptValidationFailure.ArtifactBudgetInvalid, "concept-map", "The configured artifact limit is outside the supported range.");
            return false;
        }

        if (!DocConceptArtifactBudget.TryMeasure(bytes, maximumBytes, out _))
        {
            issue = new DocConceptValidationIssue(DocConceptValidationFailure.ArtifactTooLarge, "concept-map", "The complete bridge exceeds the configured UTF-8 byte limit.");
            return false;
        }

        try
        {
            _ = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            ValidateNoDuplicateProperties(document.RootElement, DocConceptValidationFailure.MapInvalid);
            EnsureObject(document.RootElement, "concept-map", "schema", "concepts");
            var root = document.RootElement;
            if (!string.Equals(GetRequiredString(root, "schema"), MapSchema, StringComparison.Ordinal))
            {
                throw new SchemaException(DocConceptValidationFailure.MapInvalid, "The bridge schema is unsupported.");
            }

            var parsed = new List<DocConceptBridgeMembership>();
            var seenConceptIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var concept in GetRequiredArray(root, "concepts").EnumerateArray())
            {
                EnsureObject(concept, "concept-map-concept", "conceptId", "memberships");
                var rawConceptId = GetRequiredString(concept, "conceptId");
                if (!TryNormalizeConceptId(rawConceptId, out var conceptId) || conceptId is null)
                {
                    throw new SchemaException(DocConceptValidationFailure.InvalidConceptId, "The bridge contains an invalid concept identifier.");
                }

                if (!seenConceptIds.Add(conceptId))
                {
                    throw new SchemaException(DocConceptValidationFailure.IdentityConflict, "The bridge repeats a concept record.");
                }

                var conceptMemberships = GetRequiredArray(concept, "memberships");
                if (conceptMemberships.GetArrayLength() == 0)
                {
                    throw new SchemaException(DocConceptValidationFailure.MapInvalid, "Each bridge concept requires at least one release membership.");
                }

                var seenMembershipKeys = new HashSet<(string Version, string Pin, string? Locale)>();
                foreach (var membership in conceptMemberships.EnumerateArray())
                {
                    EnsureObject(membership, "concept-map-membership", "version", "releaseManifestSha256", "locale", "kind", "canonicalRoutePath", "fallbackSectionRoute", "sectionLanding");
                    var version = GetRequiredString(membership, "version");
                    var pin = GetRequiredString(membership, "releaseManifestSha256");
                    if (string.IsNullOrWhiteSpace(version) || !IsSha256(pin))
                    {
                        throw new SchemaException(DocConceptValidationFailure.MapInvalid, "A bridge membership requires an exact version and SHA-256 release pin.");
                    }

                    var locale = GetRequiredNullableString(membership, "locale");
                    if (locale is not null && !IsNormalizedLocale(locale))
                    {
                        throw new SchemaException(DocConceptValidationFailure.MapInvalid, "A bridge membership locale is not normalized.");
                    }

                    if (!seenMembershipKeys.Add((version, pin.ToLowerInvariant(), locale)))
                    {
                        throw new SchemaException(DocConceptValidationFailure.IdentityConflict, "A bridge repeats a concept membership key.");
                    }

                    var kind = GetRequiredString(membership, "kind");
                    var isAbsent = kind switch
                    {
                        "page" => false,
                        "absent" => true,
                        _ => throw new SchemaException(DocConceptValidationFailure.MapInvalid, "A bridge membership kind must be page or absent.")
                    };

                    var routePath = membership.TryGetProperty("canonicalRoutePath", out var routeElement)
                        ? GetString(routeElement, "canonicalRoutePath")
                        : null;
                    var fallbackRoute = membership.TryGetProperty("fallbackSectionRoute", out var fallbackElement)
                        ? GetString(fallbackElement, "fallbackSectionRoute")
                        : null;
                    bool? sectionLanding = membership.TryGetProperty("sectionLanding", out var landingElement)
                        ? GetBoolean(landingElement, "sectionLanding")
                        : null;

                    if (isAbsent)
                    {
                        if (routePath is not null || sectionLanding is not null)
                        {
                            throw new SchemaException(DocConceptValidationFailure.MapInvalid, "An absence membership cannot name a page or landing eligibility.");
                        }
                    }
                    else if (routePath is null || !IsSafeRoutePath(routePath))
                    {
                        throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "A page membership requires a safe canonical route path.");
                    }

                    if (fallbackRoute is not null && !IsSafeRoutePath(fallbackRoute))
                    {
                        throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "A fallback section route is unsafe.");
                    }

                    parsed.Add(new DocConceptBridgeMembership(conceptId, version, pin.ToLowerInvariant(), locale, isAbsent, routePath, fallbackRoute, sectionLanding));
                }
            }

            memberships = Array.AsReadOnly(parsed.ToArray());
            return true;
        }
        catch (SchemaException ex)
        {
            var failure = ex.Failure == DocConceptValidationFailure.IndexInvalid ? DocConceptValidationFailure.MapInvalid : ex.Failure;
            issue = new DocConceptValidationIssue(failure, "concept-map", ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or OverflowException)
        {
            issue = new DocConceptValidationIssue(DocConceptValidationFailure.MapInvalid, "concept-map", "The bridge is not valid UTF-8 JSON for the supported schema.");
            return false;
        }
    }

    /// <summary>Checks the stable lowercase ASCII concept identifier grammar.</summary>
    /// <param name="character">Character to check.</param>
    /// <returns>Whether the character is an ASCII lowercase letter or digit.</returns>
    private static bool IsAsciiAlphaNumeric(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';

    /// <summary>Builds an exact route lookup and rejects unsafe or duplicate caller-supplied canonical facts.</summary>
    private static Dictionary<string, DocConceptCanonicalRouteFact> BuildCanonicalRouteMap(IReadOnlyList<DocConceptCanonicalRouteFact> routes)
    {
        var byExactRoute = new Dictionary<string, DocConceptCanonicalRouteFact>(StringComparer.Ordinal);
        var routeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (route is null || !IsSafeRoutePath(route.RoutePath) || !routeNames.Add(route.RoutePath))
            {
                throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "Verified canonical route facts contain an unsafe or duplicate route.");
            }

            if (route.ArtifactPath is not null && !IsSafeArtifactPath(route.ArtifactPath))
            {
                throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "Verified canonical route facts contain an unsafe artifact mapping.");
            }

            if (route.IsServeable && string.IsNullOrWhiteSpace(route.ArtifactPath))
            {
                throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "A serveable canonical route requires its verified physical artifact mapping.");
            }

            if (route.LocaleIsKnown && route.Locale is not null && !IsNormalizedLocale(route.Locale))
            {
                throw new SchemaException(DocConceptValidationFailure.CanonicalRouteInvalid, "Verified canonical route facts contain an unnormalized locale.");
            }

            byExactRoute.Add(route.RoutePath, route);
        }

        return byExactRoute;
    }

    /// <summary>Checks that a route is a safe docs-root-relative canonical path and contains no URL context.</summary>
    internal static bool IsSafeRoutePath(string? routePath)
    {
        if (routePath is null || routePath.StartsWith("/", StringComparison.Ordinal) || routePath.Contains('\\')
            || routePath.Contains('?') || routePath.Contains('#')
            || routePath.Contains("//", StringComparison.Ordinal) || routePath.Any(char.IsControl))
        {
            return false;
        }

        if (routePath.Length == 0)
        {
            return true;
        }

        return routePath.Split('/').All(segment => segment.Length > 0 && !segment.StartsWith(".", StringComparison.Ordinal));
    }

    /// <summary>Checks a whole-export-root relative ordinary HTML artifact mapping and its trusted canonical fragment.</summary>
    internal static bool IsSafeArtifactPath(string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath)
            || !string.Equals(artifactPath, artifactPath.Trim(), StringComparison.Ordinal)
            || artifactPath.Any(char.IsControl))
        {
            return false;
        }

        var fragmentStart = artifactPath.IndexOf('#');
        var pathPart = fragmentStart >= 0 ? artifactPath[..fragmentStart] : artifactPath;
        var fragment = fragmentStart >= 0 ? artifactPath[(fragmentStart + 1)..] : null;
        if (string.IsNullOrWhiteSpace(pathPart)
            || pathPart.StartsWith("/", StringComparison.Ordinal)
            || pathPart.IndexOfAny(['\\', ':', '?', '#', '%']) >= 0
            || pathPart.Contains("//", StringComparison.Ordinal)
            || !pathPart.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || pathPart.Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment.StartsWith(".", StringComparison.Ordinal)))
        {
            return false;
        }

        return fragment is null || IsSafeArtifactFragment(fragment);
    }

    /// <summary>Allows canonical fragment data without letting it become another filesystem path component.</summary>
    private static bool IsSafeArtifactFragment(string fragment)
    {
        if (fragment.IndexOfAny(['/', '\\', ':', '?', '#']) >= 0)
        {
            return false;
        }

        for (var index = 0; index < fragment.Length; index++)
        {
            if (fragment[index] != '%')
            {
                continue;
            }

            if (index + 2 >= fragment.Length || !Uri.IsHexDigit(fragment[index + 1]) || !Uri.IsHexDigit(fragment[index + 2]))
            {
                return false;
            }

            // Encoded separators in the trusted canonical fragment remain fragment data and are never decoded
            // into the filesystem path. Preserve them because the route manifest owns this fragment identity.
            index += 2;
        }

        return true;
    }

    /// <summary>Checks that a locale is already trimmed and contains no URL, whitespace, or control syntax.</summary>
    private static bool IsNormalizedLocale(string locale) =>
        locale.Length is > 0 and <= 64
        && string.Equals(locale, locale.Trim(), StringComparison.Ordinal)
        && !locale.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\' or '?' or '#');

    /// <summary>Checks a 64-character hexadecimal SHA-256 value.</summary>
    internal static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    /// <summary>Checks every JSON object for repeated property names before any schema field is read.</summary>
    private static void ValidateNoDuplicateProperties(JsonElement value, DocConceptValidationFailure failure)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!names.Add(property.Name))
                        {
                            throw new SchemaException(failure, "A JSON object repeats a field name.");
                        }

                        ValidateNoDuplicateProperties(property.Value, failure);
                    }

                    break;
                }
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ValidateNoDuplicateProperties(item, failure);
                }

                break;
        }
    }

    /// <summary>Requires an object to contain only approved schema fields.</summary>
    private static void EnsureObject(JsonElement element, string label, params string[] allowedFields)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new SchemaException(DocConceptValidationFailure.IndexInvalid, $"The {label} must be a JSON object.");
        }

        var allowed = new HashSet<string>(allowedFields, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new SchemaException(DocConceptValidationFailure.IndexInvalid, $"The {label} contains an unsupported field.");
            }
        }
    }

    /// <summary>Reads a required JSON string property.</summary>
    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new SchemaException(DocConceptValidationFailure.IndexInvalid, $"The required {propertyName} field is missing.");
        }

        return GetString(property, propertyName);
    }

    /// <summary>Reads a required nullable JSON string property, requiring that the property itself be present.</summary>
    private static string? GetRequiredNullableString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new SchemaException(DocConceptValidationFailure.IndexInvalid, $"The required {propertyName} field is missing.");
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return GetString(property, propertyName);
    }

    /// <summary>Reads a JSON string without coercing numbers, booleans, or null.</summary>
    private static string GetString(JsonElement property, string propertyName) => property.ValueKind == JsonValueKind.String
        ? property.GetString() ?? string.Empty
        : throw new SchemaException(DocConceptValidationFailure.IndexInvalid, $"The {propertyName} field must be a string.");

    /// <summary>Reads a required JSON array property.</summary>
    private static JsonElement GetRequiredArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new SchemaException(DocConceptValidationFailure.IndexInvalid, $"The required {propertyName} field must be an array.");
        }

        return property;
    }

    /// <summary>Reads an array containing only JSON strings.</summary>
    private static IReadOnlyList<string> GetRequiredStringArray(JsonElement element, string propertyName)
    {
        var array = GetRequiredArray(element, propertyName);
        var values = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            values.Add(GetString(item, propertyName));
        }

        return values.AsReadOnly();
    }

    /// <summary>Reads a JSON boolean without coercion.</summary>
    private static bool GetBoolean(JsonElement element, string propertyName) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new SchemaException(DocConceptValidationFailure.IndexInvalid, $"The {propertyName} field must be a boolean.")
    };

    /// <summary>Rejects empty, whitespace, control-bearing, or duplicate anchor identifiers.</summary>
    private static void ValidateAnchors(IReadOnlyList<string> anchors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in anchors)
        {
            if (string.IsNullOrWhiteSpace(anchor) || anchor.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character == '#') || !seen.Add(anchor))
            {
                throw new SchemaException(DocConceptValidationFailure.IndexInvalid, "The sidecar contains an invalid or duplicate emitted anchor identifier.");
            }
        }
    }

    /// <summary>Creates an internal exception that carries a typed core validation cause.</summary>
    private sealed class SchemaException(DocConceptValidationFailure failure, string message) : Exception(message)
    {
        public DocConceptValidationFailure Failure { get; } = failure;
    }

    /// <summary>Uses exact ordinal locale and concept identity in the sidecar uniqueness check.</summary>
    private readonly record struct ConceptLocaleKey(string? Locale, string ConceptId);

    /// <summary>Compares locale and concept identifiers ordinally for membership uniqueness.</summary>
    private sealed class ConceptLocaleKeyComparer : IEqualityComparer<ConceptLocaleKey>
    {
        public static ConceptLocaleKeyComparer Instance { get; } = new();
        public bool Equals(ConceptLocaleKey left, ConceptLocaleKey right) =>
            string.Equals(left.Locale, right.Locale, StringComparison.Ordinal)
            && string.Equals(left.ConceptId, right.ConceptId, StringComparison.Ordinal);
        public int GetHashCode(ConceptLocaleKey value) => HashCode.Combine(
            value.Locale is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Locale),
            StringComparer.Ordinal.GetHashCode(value.ConceptId));
    }
}
