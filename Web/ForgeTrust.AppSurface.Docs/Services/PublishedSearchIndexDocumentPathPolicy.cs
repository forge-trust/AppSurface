namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Validates document paths carried by published AppSurface Docs search-index payloads.
/// </summary>
/// <remarks>
/// Published search indexes are archive metadata, not a general URL surface. Archive payloads must store canonical
/// <c>/docs</c>-rooted document paths so the published-tree rewriter can safely rebase them for aliases, exact
/// versions, custom route roots, and request path bases at serve time. The served-path entry point is a defense-in-depth
/// check for already-rebased browser links; it must not be used to validate immutable archive contents.
/// </remarks>
internal static class PublishedSearchIndexDocumentPathPolicy
{
    private const string ExpectedArchiveRoot = DocsUrlBuilder.DocsEntryPath;
    private const string ExactVersionPrefix = DocsUrlBuilder.DocsVersionPrefix + "/";
    private const string ArchiveVersionPrefix = DocsUrlBuilder.DocsVersionsPath + "/";

    private static readonly HashSet<string> ReservedDocumentRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "search",
        "search-index.json",
        "search.css",
        "search-client.js",
        "outline-client.js",
        "minisearch.min.js",
        "_health",
        "_health.json",
        "_routes",
        "_routes.json",
        "_search-index",
        "v",
        "versions"
    };

    /// <summary>
    /// Validates a path stored in an exact published release tree's <c>search-index.json</c>.
    /// </summary>
    /// <param name="value">The candidate <c>documents[*].path</c> value.</param>
    /// <param name="context">The immutable archive validation context.</param>
    /// <returns>A structured validation result with a stable rejection reason.</returns>
    internal static PublishedSearchIndexPathValidationResult ValidateArchivePath(
        string? value,
        PublishedSearchIndexArchivePathContext context)
    {
        var common = ValidateCommon(value);
        if (!common.IsValid)
        {
            return common;
        }

        var path = common.NormalizedPath!;
        if (!IsUnderRootOrdinal(path, ExpectedArchiveRoot))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.OutsideDocsRoot, value);
        }

        var relativePath = path.Length == ExpectedArchiveRoot.Length
            ? string.Empty
            : path[(ExpectedArchiveRoot.Length + 1)..];

        if (relativePath.Equals("v", StringComparison.OrdinalIgnoreCase)
            || relativePath.Equals("versions", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.ReservedRoute, value);
        }

        if (path.StartsWith(ExactVersionPrefix, StringComparison.Ordinal))
        {
            return ValidateVersionedArchivePath(value, path, ExactVersionPrefix, context.Version);
        }

        if (path.StartsWith(ArchiveVersionPrefix, StringComparison.Ordinal))
        {
            return ValidateVersionedArchivePath(value, path, ArchiveVersionPrefix, context.Version);
        }

        return IsReservedDocumentRoute(relativePath)
            ? Reject(PublishedSearchIndexPathRejectionReason.ReservedRoute, value)
            : common;
    }

    /// <summary>
    /// Validates a browser-visible search result path after published-tree rewriting has applied the active docs root.
    /// </summary>
    /// <param name="value">The candidate browser-visible path.</param>
    /// <param name="context">The served docs surface context.</param>
    /// <returns>A structured validation result with a stable rejection reason.</returns>
    internal static PublishedSearchIndexPathValidationResult ValidateServedPath(
        string? value,
        PublishedSearchIndexServedPathContext context)
    {
        var common = ValidateCommon(value);
        if (!common.IsValid)
        {
            return common;
        }

        var path = common.NormalizedPath!;
        var matchedRoot = GetAllowedServedRoots(context)
            .FirstOrDefault(root => IsUnderRootOrdinal(path, root.RootPath));
        if (matchedRoot is null)
        {
            return Reject(PublishedSearchIndexPathRejectionReason.OutsideDocsRoot, value);
        }

        var relativePath = path.Length == matchedRoot.RootPath.Length
            ? string.Empty
            : path[(matchedRoot.RootPath.Length == 1 ? 1 : matchedRoot.RootPath.Length + 1)..];
        if (matchedRoot.IsArchiveRoot)
        {
            relativePath = StripArchiveVersionSegment(relativePath);
            if (relativePath is null)
            {
                return Reject(PublishedSearchIndexPathRejectionReason.ReservedRoute, value);
            }
        }

        return IsReservedDocumentRoute(relativePath)
            ? Reject(PublishedSearchIndexPathRejectionReason.ReservedRoute, value)
            : common;
    }

    internal static string ToDiagnosticCode(PublishedSearchIndexPathRejectionReason reason)
    {
        return reason switch
        {
            PublishedSearchIndexPathRejectionReason.None => "none",
            PublishedSearchIndexPathRejectionReason.Missing => "missing",
            PublishedSearchIndexPathRejectionReason.Whitespace => "whitespace",
            PublishedSearchIndexPathRejectionReason.NotRootRelative => "not-root-relative",
            PublishedSearchIndexPathRejectionReason.SchemeUrl => "scheme-url",
            PublishedSearchIndexPathRejectionReason.AbsoluteUrl => "absolute-url",
            PublishedSearchIndexPathRejectionReason.ProtocolRelative => "protocol-relative",
            PublishedSearchIndexPathRejectionReason.Backslash => "backslash",
            PublishedSearchIndexPathRejectionReason.ControlCharacter => "control-character",
            PublishedSearchIndexPathRejectionReason.MalformedPercentEncoding => "malformed-percent-encoding",
            PublishedSearchIndexPathRejectionReason.EncodedSeparator => "encoded-separator",
            PublishedSearchIndexPathRejectionReason.EncodedTraversal => "encoded-traversal",
            PublishedSearchIndexPathRejectionReason.OutsideDocsRoot => "outside-docs-root",
            PublishedSearchIndexPathRejectionReason.ReservedRoute => "reserved-route",
            PublishedSearchIndexPathRejectionReason.WrongVersion => "wrong-version",
            _ => "unknown"
        };
    }

    private static PublishedSearchIndexPathValidationResult ValidateVersionedArchivePath(
        string? originalValue,
        string path,
        string prefix,
        string version)
    {
        var remainder = path[prefix.Length..];
        var separator = remainder.IndexOf('/');
        var pathVersion = separator >= 0 ? remainder[..separator] : remainder;
        if (!string.Equals(pathVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.WrongVersion, originalValue);
        }

        var versionRelativePath = separator >= 0 ? remainder[(separator + 1)..] : string.Empty;
        return IsReservedDocumentRoute(versionRelativePath)
            ? Reject(PublishedSearchIndexPathRejectionReason.ReservedRoute, originalValue)
            : PublishedSearchIndexPathValidationResult.Valid(path);
    }

    private static PublishedSearchIndexPathValidationResult ValidateCommon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.Missing, value);
        }

        var candidate = value!;
        if (ContainsControlCharacter(candidate))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.ControlCharacter, value);
        }

        if (!string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.Whitespace, value);
        }

        if (candidate.Contains('\\'))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.Backslash, value);
        }

        if (!candidate.StartsWith("/", StringComparison.Ordinal))
        {
            return TryClassifyAbsoluteOrSchemeUrl(candidate, value, out var classified)
                ? classified
                : Reject(PublishedSearchIndexPathRejectionReason.NotRootRelative, value);
        }

        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.ProtocolRelative, value);
        }

        var suffixIndex = candidate.IndexOfAny(['?', '#']);
        var path = suffixIndex >= 0 ? candidate[..suffixIndex] : candidate;

        if (!TryValidatePercentEscapes(candidate, scanForSensitivePathTokens: false, out var reason)
            || !TryValidatePercentEscapes(path, scanForSensitivePathTokens: true, out reason))
        {
            return Reject(reason, value);
        }

        var decodedPath = Uri.UnescapeDataString(path);
        if (ContainsDotSegment(path) || ContainsDotSegment(decodedPath))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.EncodedTraversal, value);
        }

        return PublishedSearchIndexPathValidationResult.Valid(path);
    }

    private static bool TryClassifyAbsoluteOrSchemeUrl(
        string candidate,
        string? originalValue,
        out PublishedSearchIndexPathValidationResult result)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absoluteUri))
        {
            result = string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? Reject(PublishedSearchIndexPathRejectionReason.AbsoluteUrl, originalValue)
                : Reject(PublishedSearchIndexPathRejectionReason.SchemeUrl, originalValue);
            return true;
        }

        var colonIndex = candidate.IndexOf(':');
        var firstSeparator = candidate.IndexOfAny(['/', '?', '#']);
        if (colonIndex > 0 && (firstSeparator < 0 || colonIndex < firstSeparator))
        {
            result = Reject(PublishedSearchIndexPathRejectionReason.SchemeUrl, originalValue);
            return true;
        }

        result = PublishedSearchIndexPathValidationResult.Invalid(
            PublishedSearchIndexPathRejectionReason.NotRootRelative,
            Redact(candidate));
        return false;
    }

    private static bool TryValidatePercentEscapes(
        string value,
        bool scanForSensitivePathTokens,
        out PublishedSearchIndexPathRejectionReason reason)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                continue;
            }

            if (i + 2 >= value.Length || !IsHex(value[i + 1]) || !IsHex(value[i + 2]))
            {
                reason = PublishedSearchIndexPathRejectionReason.MalformedPercentEncoding;
                return false;
            }

            var decoded = HexToByte(value[i + 1], value[i + 2]);
            if (decoded < 0x20 || decoded == 0x7f)
            {
                reason = PublishedSearchIndexPathRejectionReason.ControlCharacter;
                return false;
            }

            if (scanForSensitivePathTokens && (decoded == '/' || decoded == '\\'))
            {
                reason = PublishedSearchIndexPathRejectionReason.EncodedSeparator;
                return false;
            }

            if (scanForSensitivePathTokens && decoded == '.')
            {
                reason = PublishedSearchIndexPathRejectionReason.EncodedTraversal;
                return false;
            }

            if (decoded == '%' && i + 4 < value.Length && IsHex(value[i + 3]) && IsHex(value[i + 4]))
            {
                var doubleDecoded = HexToByte(value[i + 3], value[i + 4]);
                if (doubleDecoded < 0x20 || doubleDecoded == 0x7f)
                {
                    reason = PublishedSearchIndexPathRejectionReason.ControlCharacter;
                    return false;
                }

                if (scanForSensitivePathTokens && (doubleDecoded == '/' || doubleDecoded == '\\'))
                {
                    reason = PublishedSearchIndexPathRejectionReason.EncodedSeparator;
                    return false;
                }

                if (scanForSensitivePathTokens && doubleDecoded == '.')
                {
                    reason = PublishedSearchIndexPathRejectionReason.EncodedTraversal;
                    return false;
                }
            }
        }

        reason = PublishedSearchIndexPathRejectionReason.None;
        return true;
    }

    private static bool IsReservedDocumentRoute(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        var trimmed = relativePath.Trim('/');
        var firstSeparator = trimmed.IndexOf('/');
        var firstSegment = firstSeparator >= 0 ? trimmed[..firstSeparator] : trimmed;
        if (firstSegment.Equals("v", StringComparison.OrdinalIgnoreCase)
            || firstSegment.Equals("versions", StringComparison.OrdinalIgnoreCase)
            || ReservedDocumentRoutes.Contains(trimmed))
        {
            return true;
        }

        return trimmed.StartsWith("_search-index/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRootOrdinal(string path, string root)
    {
        if (string.Equals(root, "/", StringComparison.Ordinal))
        {
            return path.StartsWith("/", StringComparison.Ordinal);
        }

        return string.Equals(path, root, StringComparison.Ordinal)
               || path.StartsWith(root + "/", StringComparison.Ordinal);
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return ExpectedArchiveRoot;
        }

        var normalized = root.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        normalized = normalized.TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private static IReadOnlyList<PublishedSearchIndexServedRoot> GetAllowedServedRoots(
        PublishedSearchIndexServedPathContext context)
    {
        var normalizedRoot = NormalizeRoot(context.DocsRootPath);
        var roots = new List<PublishedSearchIndexServedRoot> { new(normalizedRoot, IsArchiveRoot: false) };
        if (!string.IsNullOrWhiteSpace(context.ArchiveRootPath))
        {
            roots.Add(new PublishedSearchIndexServedRoot(NormalizeRoot(context.ArchiveRootPath), IsArchiveRoot: true));
        }

        return roots
            .OrderByDescending(root => root.RootPath.Length)
            .ToList();
    }

    private static string? StripArchiveVersionSegment(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return null;
        }

        var separator = relativePath.IndexOf('/');
        var versionSegment = separator >= 0 ? relativePath[..separator] : relativePath;
        if (IsReservedDocumentRoute(versionSegment))
        {
            return null;
        }

        return separator >= 0 ? relativePath[(separator + 1)..] : string.Empty;
    }

    private static bool ContainsDotSegment(string path)
    {
        return path.Split('/', StringSplitOptions.None).Any(segment => segment is "." or "..");
    }

    private static bool ContainsControlCharacter(string value)
    {
        return value.Any(char.IsControl);
    }

    private static bool IsHex(char value)
    {
        return value is >= '0' and <= '9'
               || value is >= 'a' and <= 'f'
               || value is >= 'A' and <= 'F';
    }

    private static int HexToByte(char high, char low)
    {
        return (HexValue(high) << 4) + HexValue(low);
    }

    private static int HexValue(char value)
    {
        return value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => value - 'A' + 10
        };
    }

    private static PublishedSearchIndexPathValidationResult Reject(
        PublishedSearchIndexPathRejectionReason reason,
        string? value)
    {
        return PublishedSearchIndexPathValidationResult.Invalid(reason, Redact(value));
    }

    private static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "<redacted:0>";
        }

        return $"<redacted:{value.Length}>";
    }

    private sealed record PublishedSearchIndexServedRoot(string RootPath, bool IsArchiveRoot);
}

/// <summary>
/// Immutable context for validating document paths stored in a published exact-version archive.
/// </summary>
/// <param name="Version">The catalog version whose exact tree is being validated.</param>
internal sealed record PublishedSearchIndexArchivePathContext(string Version);

/// <summary>
/// Runtime context for validating already-rebased browser-visible search result paths.
/// </summary>
/// <param name="DocsRootPath">The active browser-visible docs root, including request path base if present.</param>
/// <param name="ArchiveRootPath">The active browser-visible archive root, including request path base if present.</param>
internal sealed record PublishedSearchIndexServedPathContext(string DocsRootPath, string? ArchiveRootPath = null);

/// <summary>
/// Structured result for a published search-index document path validation attempt.
/// </summary>
/// <param name="IsValid">Whether the candidate path is safe for the target context.</param>
/// <param name="Reason">The stable rejection category, or <see cref="PublishedSearchIndexPathRejectionReason.None"/> when valid.</param>
/// <param name="NormalizedPath">The validated path portion without query string or fragment when valid.</param>
/// <param name="RedactedValue">A non-sensitive description of the rejected input for diagnostics.</param>
internal sealed record PublishedSearchIndexPathValidationResult(
    bool IsValid,
    PublishedSearchIndexPathRejectionReason Reason,
    string? NormalizedPath,
    string RedactedValue)
{
    public static PublishedSearchIndexPathValidationResult Valid(string normalizedPath)
    {
        return new(true, PublishedSearchIndexPathRejectionReason.None, normalizedPath, string.Empty);
    }

    public static PublishedSearchIndexPathValidationResult Invalid(
        PublishedSearchIndexPathRejectionReason reason,
        string redactedValue)
    {
        return new(false, reason, null, redactedValue);
    }
}

/// <summary>
/// Stable rejection categories for published search-index document path validation.
/// </summary>
internal enum PublishedSearchIndexPathRejectionReason
{
    None,
    Missing,
    Whitespace,
    NotRootRelative,
    SchemeUrl,
    AbsoluteUrl,
    ProtocolRelative,
    Backslash,
    ControlCharacter,
    MalformedPercentEncoding,
    EncodedSeparator,
    EncodedTraversal,
    OutsideDocsRoot,
    ReservedRoute,
    WrongVersion
}
