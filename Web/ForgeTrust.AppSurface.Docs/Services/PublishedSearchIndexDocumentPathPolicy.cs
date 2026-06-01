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
    /// <returns>
    /// A structured validation result whose <see cref="PublishedSearchIndexPathValidationResult.Reason"/> identifies the
    /// first rejected condition, or <see cref="PublishedSearchIndexPathRejectionReason.None"/> when the archive value is
    /// safe to store.
    /// </returns>
    /// <remarks>
    /// Archive validation is intentionally stricter than served-link validation: stored paths must use canonical
    /// <c>/docs/...</c> forms and must not include request path bases, custom route roots, origins, or deployment aliases.
    /// The checks run from syntax and URL-shape hazards toward docs-root and version-family hazards. When several
    /// rejection reasons apply, callers should log or surface only the returned first reason plus
    /// <see cref="PublishedSearchIndexPathValidationResult.RedactedValue"/>; do not log the original value.
    /// </remarks>
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
    /// <returns>
    /// A structured validation result whose <see cref="PublishedSearchIndexPathValidationResult.NormalizedPath"/> is safe
    /// to use as a browser-visible link only when <see cref="PublishedSearchIndexPathValidationResult.IsValid"/> is
    /// <see langword="true"/>.
    /// </returns>
    /// <remarks>
    /// Served validation accepts the active docs root and optional archive root after route rewriting has already happened.
    /// It still rejects executable schemes, off-root links, traversal, encoded separators, controls, and reserved docs
    /// endpoints before the value can enter client-side result rendering.
    /// </remarks>
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

    /// <summary>
    /// Converts a rejection reason to the stable lower-kebab diagnostic code used in logs, telemetry, and tests.
    /// </summary>
    /// <param name="reason">The structured validation reason returned by the policy.</param>
    /// <returns>
    /// One of <c>none</c>, <c>missing</c>, <c>whitespace</c>, <c>not-root-relative</c>, <c>scheme-url</c>,
    /// <c>absolute-url</c>, <c>protocol-relative</c>, <c>backslash</c>, <c>control-character</c>,
    /// <c>malformed-percent-encoding</c>, <c>encoded-separator</c>, <c>encoded-traversal</c>,
    /// <c>outside-docs-root</c>, <c>reserved-route</c>, <c>wrong-version</c>, or <c>unknown</c> for future enum values.
    /// </returns>
    /// <remarks>
    /// Use <see cref="PublishedSearchIndexPathRejectionReason"/> for code branching and this string only for stable
    /// diagnostics that may cross process, log, or test boundaries.
    /// </remarks>
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
        if (ContainsControlCharacter(decodedPath))
        {
            return Reject(PublishedSearchIndexPathRejectionReason.ControlCharacter, value);
        }

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
/// <param name="Reason">
/// The stable branch-friendly rejection category, or <see cref="PublishedSearchIndexPathRejectionReason.None"/> when valid.
/// Use <see cref="PublishedSearchIndexDocumentPathPolicy.ToDiagnosticCode(PublishedSearchIndexPathRejectionReason)"/> when
/// a lower-kebab telemetry or log value is required.
/// </param>
/// <param name="NormalizedPath">
/// The validated path portion without query string or fragment when valid. This value is safe to log and to use for
/// docs-root matching because unsafe suffixes and rejected inputs never appear here.
/// </param>
/// <param name="RedactedValue">
/// A non-sensitive description of the rejected input for diagnostics. This is the only rejected-value field callers
/// should include in operator-visible logs or reader-facing availability messages.
/// </param>
internal sealed record PublishedSearchIndexPathValidationResult(
    bool IsValid,
    PublishedSearchIndexPathRejectionReason Reason,
    string? NormalizedPath,
    string RedactedValue)
{
    /// <summary>
    /// Creates a successful validation result for a normalized path-only value.
    /// </summary>
    /// <param name="normalizedPath">The safe-to-log path portion with any query string or fragment removed.</param>
    /// <returns>A valid result with <see cref="PublishedSearchIndexPathRejectionReason.None"/>.</returns>
    public static PublishedSearchIndexPathValidationResult Valid(string normalizedPath)
    {
        return new(true, PublishedSearchIndexPathRejectionReason.None, normalizedPath, string.Empty);
    }

    /// <summary>
    /// Creates a rejected validation result with a stable reason and redacted original value.
    /// </summary>
    /// <param name="reason">The first rejection reason observed by the policy's ordered checks.</param>
    /// <param name="redactedValue">A length-only or otherwise non-sensitive substitute for the rejected value.</param>
    /// <returns>An invalid result whose <see cref="NormalizedPath"/> is <see langword="null"/>.</returns>
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
/// <remarks>
/// Use enum members for local control flow and
/// <see cref="PublishedSearchIndexDocumentPathPolicy.ToDiagnosticCode(PublishedSearchIndexPathRejectionReason)"/> for
/// telemetry, log fields, and persisted diagnostics. The policy returns the first matching category in its ordered checks,
/// so a value that is both off-root and reserved reports the earlier root or syntax failure.
/// </remarks>
internal enum PublishedSearchIndexPathRejectionReason
{
    /// <summary>
    /// The path is valid for the requested validation context.
    /// </summary>
    None,

    /// <summary>
    /// The value is null, empty, or whitespace-only.
    /// </summary>
    Missing,

    /// <summary>
    /// The value contains leading or trailing whitespace even though it is otherwise non-empty.
    /// </summary>
    Whitespace,

    /// <summary>
    /// The value is not root-relative, for example <c>docs/guide.html</c>.
    /// </summary>
    NotRootRelative,

    /// <summary>
    /// The value uses a non-HTTP absolute scheme such as <c>javascript:</c> or <c>data:</c>.
    /// </summary>
    SchemeUrl,

    /// <summary>
    /// The value is an absolute HTTP(S) URL instead of a docs-root-relative path.
    /// </summary>
    AbsoluteUrl,

    /// <summary>
    /// The value is protocol-relative, for example <c>//example.test/docs/guide.html</c>.
    /// </summary>
    ProtocolRelative,

    /// <summary>
    /// The value contains a backslash in the path or suffix, which could be interpreted as a separator by some consumers.
    /// </summary>
    Backslash,

    /// <summary>
    /// The value contains a Unicode control character, including C0, DEL, and C1 controls.
    /// </summary>
    ControlCharacter,

    /// <summary>
    /// The value contains a percent sign that is not followed by two hexadecimal digits.
    /// </summary>
    MalformedPercentEncoding,

    /// <summary>
    /// A percent-encoded or double-encoded slash or backslash appears in the path portion.
    /// </summary>
    EncodedSeparator,

    /// <summary>
    /// A raw, percent-encoded, or double-encoded dot segment could traverse out of the docs root.
    /// </summary>
    EncodedTraversal,

    /// <summary>
    /// The path is syntactically safe but does not live under the expected archive or served docs root.
    /// </summary>
    OutsideDocsRoot,

    /// <summary>
    /// The path targets a docs operational route, runtime asset, version-list endpoint, or reserved version-family child.
    /// </summary>
    ReservedRoute,

    /// <summary>
    /// An exact-version or archive-version path targets a different catalog version than the tree being validated.
    /// </summary>
    WrongVersion
}
