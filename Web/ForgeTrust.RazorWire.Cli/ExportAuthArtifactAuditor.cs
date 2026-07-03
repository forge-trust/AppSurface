using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using ForgeTrust.RazorWire.TagHelpers;

namespace ForgeTrust.RazorWire.Cli;

/// <summary>
/// Validates static export text artifacts for RazorWire auth projection leaks before bytes are published.
/// </summary>
internal static class ExportAuthArtifactAuditor
{
    /// <summary>
    /// Diagnostic code emitted when static auth projection auditing blocks an unsafe text artifact.
    /// </summary>
    internal const string DiagnosticCode = "RWEXPORT010";
    /// <summary>
    /// Repository-relative documentation path for the static auth projection contract and remediation anchors.
    /// </summary>
    internal const string DocsPath = "Web/ForgeTrust.RazorWire/Docs/static-auth-projection.md";
    /// <summary>
    /// Reason label used when a protected auth view has no explicit anonymous fallback for static export.
    /// </summary>
    internal const string MissingFallback = "auth-missing-fallback";
    /// <summary>
    /// Reason label used when protected allowed auth UI would be written to a public static artifact.
    /// </summary>
    internal const string PrivateContent = "auth-private-content";
    /// <summary>
    /// Reason label used when evaluated auth outcome or provider metadata appears in static output.
    /// </summary>
    internal const string UnsafeMetadata = "auth-unsafe-metadata";
    /// <summary>
    /// Reason label used when live auth diagnostics, policy names, or reason attributes appear in static output.
    /// </summary>
    internal const string Diagnostics = "auth-diagnostics";
    /// <summary>
    /// Reason label used when DevAuth, persona, subject, or similar test-auth markers appear in static output.
    /// </summary>
    internal const string ArtifactLeak = "auth-artifact-leak";

    private static readonly Regex AttributeRegex = new(
        @"\s(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))",
        RegexOptions.Compiled);

    private static readonly string[] TextExtensions =
    [
        ".css",
        ".htm",
        ".html",
        ".js",
        ".json",
        ".map",
        ".mjs",
        ".svg",
        ".txt",
        ".webmanifest",
        ".xml",
        ".yml",
        ".yaml"
    ];

    private static readonly string[] TextMediaTypes =
    [
        "application/javascript",
        "application/json",
        "application/manifest+json",
        "application/x-javascript",
        "application/xml",
        "image/svg+xml",
        "text/"
    ];

    private static readonly string[] ExtensionlessTextFileNames =
    [
        ".nojekyll",
        "CNAME",
        "_headers",
        "_redirects"
    ];

    private static readonly string[] ArtifactLeakMarkers =
    [
        AppSurfaceDevAuthStaticExportMarkers.MarkerCssClass,
        AppSurfaceDevAuthStaticExportMarkers.MarkerAttributeName,
        "data-appsurface-persona",
        "data-appsurface-subject"
    ];

    /// <summary>
    /// Validates and writes a generated text artifact, failing before opening the destination when auth content is unsafe.
    /// </summary>
    /// <remarks>
    /// Use this for string materialization paths such as route manifests, redirects, and release manifests. The route and
    /// artifact path are diagnostic context only; the output path boundary is still enforced by
    /// <see cref="ExportOutputPathGuards"/> after the auth audit passes.
    /// </remarks>
    internal static async Task WriteTextArtifactAsync(
        string outputPath,
        string artifactPath,
        string artifactKind,
        string? route,
        string contents,
        Encoding? encoding,
        CancellationToken cancellationToken)
    {
        ValidateTextArtifact(contents, artifactKind, route, artifactPath);
        await ExportOutputPathGuards.WriteTextArtifactAsync(
            outputPath,
            artifactPath,
            artifactKind,
            route,
            contents,
            encoding,
            cancellationToken);
    }

    /// <summary>
    /// Validates and writes generated text bytes without changing their original encoding.
    /// </summary>
    /// <remarks>
    /// The byte payload is decoded for audit using the declared charset when available, then UTF-8 and Windows-1252
    /// fallbacks. The exact input bytes are written only after all decoded representations pass, which keeps legacy
    /// encoded text stable while preserving fail-closed auth leak detection.
    /// </remarks>
    internal static async Task WriteTextArtifactBytesAsync(
        string outputPath,
        string artifactPath,
        string artifactKind,
        string? route,
        byte[] contents,
        Encoding? declaredEncoding,
        CancellationToken cancellationToken)
    {
        ValidateTextArtifactBytes(contents, artifactKind, route, artifactPath, declaredEncoding);
        await using var fileStream = ExportOutputPathGuards.OpenWritableArtifactStream(
            outputPath,
            artifactPath,
            artifactKind,
            route);
        await fileStream.WriteAsync(contents, cancellationToken);
    }

    /// <summary>
    /// Validates rendered text for forbidden static auth projection markers.
    /// </summary>
    /// <remarks>
    /// The audit checks raw text plus HTML-decoded and JSON-unescaped forms so escaped private auth markers cannot hide
    /// in search payloads, manifests, JavaScript strings, or copied text extras. Diagnostics intentionally name the
    /// reason label and artifact kind without echoing sensitive policy, subject, claim, or persona values.
    /// </remarks>
    internal static void ValidateTextArtifact(
        string contents,
        string artifactKind,
        string? route,
        string? artifactPath = null)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKind);

        foreach (var auditContents in EnumerateAuditRepresentations(contents))
        {
            if (TryFindExportViolation(auditContents, out var violationReason, out var helper))
            {
                throw CreateException(violationReason, artifactKind, route, helper, artifactPath);
            }

            if (ContainsAttribute(auditContents, "data-rw-auth-policy")
                || ContainsAttribute(auditContents, "data-rw-auth-reason"))
            {
                throw CreateException(Diagnostics, artifactKind, route, helper: null, artifactPath);
            }

            if (ContainsAttribute(auditContents, "data-rw-auth-outcome"))
            {
                throw CreateException(UnsafeMetadata, artifactKind, route, helper: null, artifactPath);
            }

            if (ContainsAllowedAuthState(auditContents))
            {
                throw CreateException(PrivateContent, artifactKind, route, helper: null, artifactPath);
            }

            if (ContainsArtifactLeakMarker(auditContents))
            {
                throw CreateException(ArtifactLeak, artifactKind, route, helper: null, artifactPath);
            }
        }
    }

    /// <summary>
    /// Validates text bytes for forbidden static auth projection markers.
    /// </summary>
    /// <remarks>
    /// Pass a declared encoding from the HTTP charset when known. Unknown or unsupported charsets are ignored by
    /// <see cref="ResolveDeclaredEncoding(string?)"/> so the UTF-8 and Windows-1252 audit fallbacks still run.
    /// </remarks>
    internal static void ValidateTextArtifactBytes(
        byte[] contents,
        string artifactKind,
        string? route,
        string? artifactPath = null,
        Encoding? declaredEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(contents);

        foreach (var decoded in EnumerateByteAuditRepresentations(contents, declaredEncoding))
        {
            ValidateTextArtifact(decoded, artifactKind, route, artifactPath);
        }
    }

    /// <summary>
    /// Classifies response artifacts that are known text from content type or filename.
    /// </summary>
    /// <remarks>
    /// This method follows explicit HTTP and extension signals. Use <see cref="ShouldAuditLocalTextArtifact(string)"/>
    /// for final inventory or local-copy scans where extensionless artifacts are also text candidates.
    /// </remarks>
    internal static bool IsTextArtifact(string? contentType, string artifactPath)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var mediaType = contentType.Split(';', 2)[0].Trim();
            if (IsTextMediaType(mediaType))
            {
                return true;
            }
        }

        return HasTextArtifactPath(artifactPath);
    }

    /// <summary>
    /// Determines whether a local artifact path should be audited as generated text.
    /// </summary>
    /// <remarks>
    /// Final inventory treats extensionless files as auditable because static routes commonly materialize without file
    /// extensions. Callers must pass the concrete output path that will be written or scanned.
    /// </remarks>
    internal static bool ShouldAuditLocalTextArtifact(string artifactPath)
    {
        if (HasTextArtifactPath(artifactPath))
        {
            return true;
        }

        return string.IsNullOrEmpty(Path.GetExtension(artifactPath));
    }

    /// <summary>
    /// Resolves an optional HTTP charset for byte-preserving artifact audits.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> for missing, invalid, or unsupported charsets so audit callers can continue with
    /// default decoding fallbacks instead of treating a bad charset as permission to skip validation.
    /// </remarks>
    internal static Encoding? ResolveDeclaredEncoding(string? charset)
    {
        var normalized = charset?.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(normalized);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static ExportValidationException CreateException(
        string reason,
        string artifactKind,
        string? route,
        string? helper,
        string? artifactPath)
    {
        var effectiveRoute = string.IsNullOrWhiteSpace(route) ? "/" : route;
        var normalizedReason = NormalizeReason(reason);
        var message = $"[{normalizedReason}] Static auth projection cannot export unsafe auth content. "
                      + $"Problem: {DescribeProblem(normalizedReason)} "
                      + $"Cause: {DescribeCause(normalizedReason)} "
                      + $"Fix: {DescribeFix(normalizedReason)} "
                      + $"Artifact: {artifactKind}."
                      + (string.IsNullOrWhiteSpace(helper) ? string.Empty : $" Helper: {helper}.")
                      + (string.IsNullOrWhiteSpace(artifactPath) ? string.Empty : $" Path: {Path.GetFileName(artifactPath)}.")
                      + $" See: {DocsPath}#{normalizedReason}.";

        return new ExportValidationException([new ExportDiagnostic(DiagnosticCode, message, effectiveRoute, reference: null)]);
    }

    private static string NormalizeReason(string? reason)
    {
        return reason switch
        {
            MissingFallback => MissingFallback,
            PrivateContent => PrivateContent,
            UnsafeMetadata => UnsafeMetadata,
            Diagnostics => Diagnostics,
            ArtifactLeak => ArtifactLeak,
            _ => ArtifactLeak,
        };
    }

    private static string DescribeProblem(string reason)
    {
        return reason switch
        {
            MissingFallback => "A protected auth view did not provide an explicit static anonymous fallback.",
            PrivateContent => "Protected allowed auth UI would become public static output.",
            UnsafeMetadata => "The artifact contains evaluated auth outcome or provider metadata.",
            Diagnostics => "The artifact contains auth diagnostics that are not static-safe.",
            _ => "The artifact contains DevAuth, persona, or auth marker content.",
        };
    }

    private static string DescribeCause(string reason)
    {
        return reason switch
        {
            MissingFallback => "`rw:auth-view` was exported without a rendered `rw:auth-anonymous` fallback.",
            PrivateContent => "An allowed-only auth helper has no v0 static fallback contract.",
            UnsafeMetadata => "Static export output must not expose host auth outcomes, subjects, claims, messages, or metadata.",
            Diagnostics => "`include-diagnostics` and policy/reason attributes are for live maintainer pages only.",
            _ => "Development or test auth state was rendered into a generated text artifact.",
        };
    }

    private static string DescribeFix(string reason)
    {
        return reason switch
        {
            MissingFallback => "Add a generic `rw:auth-anonymous` fallback, move protected UI behind live rendering, or remove this route from static export.",
            PrivateContent => "Replace allowed gates with `rw:auth-view` plus an explicit anonymous fallback, keep the content server-rendered only, or do not export the route.",
            UnsafeMetadata => "Remove auth metadata from rendered markup and let RazorWire emit only static-safe auth markers.",
            Diagnostics => "Disable auth diagnostics for exported routes.",
            _ => "Disable DevAuth/test persona UI for exported routes or exclude the route from static export.",
        };
    }

    private static bool TryFindExportViolation(string contents, out string reason, out string? helper)
    {
        reason = string.Empty;
        helper = null;
        foreach (Match match in AttributeRegex.Matches(contents))
        {
            var name = match.Groups["name"].Value;
            if (string.Equals(name, AuthViewTagHelper.StaticViolationAttributeName, StringComparison.OrdinalIgnoreCase))
            {
                reason = AttributeValue(match);
                helper = FindNearestHelper(contents, match.Index);
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateAuditRepresentations(string contents)
    {
        var backslashDecoded = DecodeBackslashEscapes(contents);
        var candidates = new[]
        {
            contents,
            WebUtility.HtmlDecode(contents),
            backslashDecoded,
            WebUtility.HtmlDecode(backslashDecoded),
        };
        var yielded = new List<string>(candidates.Length);
        foreach (var candidate in candidates)
        {
            if (yielded.Contains(candidate, StringComparer.Ordinal))
            {
                continue;
            }

            yielded.Add(candidate);
            yield return candidate;
        }
    }

    private static IEnumerable<string> EnumerateByteAuditRepresentations(byte[] contents, Encoding? declaredEncoding)
    {
        var encodings = new List<Encoding>();
        AddEncoding(encodings, declaredEncoding);
        AddEncoding(encodings, Encoding.UTF8);
        AddEncoding(encodings, Encoding.Unicode);
        AddEncoding(encodings, Encoding.BigEndianUnicode);
        AddEncoding(encodings, Encoding.UTF32);
        AddEncoding(encodings, Encoding.Latin1);

        var yielded = new List<string>(encodings.Count);
        foreach (var decoded in encodings.Select(encoding => DecodeBytes(contents, encoding)))
        {
            if (yielded.Contains(decoded, StringComparer.Ordinal))
            {
                continue;
            }

            yielded.Add(decoded);
            yield return decoded;
        }
    }

    private static void AddEncoding(List<Encoding> encodings, Encoding? encoding)
    {
        if (encoding is null || encodings.Any(existing => string.Equals(existing.WebName, encoding.WebName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        encodings.Add(encoding);
    }

    private static string DecodeBytes(byte[] contents, Encoding encoding)
    {
        using var stream = new MemoryStream(contents);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool IsTextMediaType(string mediaType)
    {
        return TextMediaTypes.Any(type => mediaType.StartsWith(type, StringComparison.OrdinalIgnoreCase))
               || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
               || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeBackslashEscapes(string contents)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < contents.Length; i++)
        {
            if (contents[i] != '\\' || i + 1 >= contents.Length)
            {
                builder?.Append(contents[i]);
                continue;
            }

            var consumed = 1;
            char decoded;
            var next = contents[i + 1];
            if (TryDecodeUnicodeEscape(contents, i, out decoded))
            {
                consumed = 5;
            }
            else if (!TryDecodeSimpleEscape(next, out decoded))
            {
                builder?.Append(contents[i]);
                continue;
            }

            builder ??= new StringBuilder(contents.Length).Append(contents, 0, i);
            builder.Append(decoded);
            i += consumed;
        }

        return builder?.ToString() ?? contents;
    }

    private static bool TryDecodeSimpleEscape(char escaped, out char decoded)
    {
        decoded = escaped switch
        {
            '"' => '"',
            '\'' => '\'',
            '\\' => '\\',
            '/' => '/',
            'b' => '\b',
            'f' => '\f',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            _ => '\0',
        };

        return decoded != '\0';
    }

    private static bool TryDecodeUnicodeEscape(string contents, int slashIndex, out char decoded)
    {
        decoded = '\0';
        if (slashIndex + 5 >= contents.Length || contents[slashIndex + 1] != 'u')
        {
            return false;
        }

        var value = 0;
        for (var offset = 2; offset <= 5; offset++)
        {
            var digit = FromHex(contents[slashIndex + offset]);
            if (digit < 0)
            {
                return false;
            }

            value = (value << 4) + digit;
        }

        decoded = (char)value;
        return true;
    }

    private static int FromHex(char value)
    {
        if (value is >= '0' and <= '9')
        {
            return value - '0';
        }

        if (value is >= 'a' and <= 'f')
        {
            return value - 'a' + 10;
        }

        return value is >= 'A' and <= 'F'
            ? value - 'A' + 10
            : -1;
    }

    private static string? FindNearestHelper(string contents, int markerIndex)
    {
        var tagStart = contents.LastIndexOf('<', Math.Max(0, markerIndex));
        if (tagStart < 0)
        {
            return null;
        }

        var tagEnd = contents.IndexOf('>', markerIndex);
        if (tagEnd < 0)
        {
            return null;
        }

        var tag = contents[tagStart..tagEnd];
        var match = AttributeRegex.Matches(tag)
            .Cast<Match>()
            .Where(match => string.Equals(match.Groups["name"].Value, "data-rw-auth-helper", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        return match is null ? null : AttributeValue(match);
    }

    private static bool ContainsAttribute(string contents, string attributeName)
    {
        return AttributeRegex.Matches(contents)
            .Cast<Match>()
            .Any(match => string.Equals(match.Groups["name"].Value, attributeName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAttributeValue(string contents, string attributeName, string value)
    {
        return AttributeRegex.Matches(contents)
            .Cast<Match>()
            .Any(match => string.Equals(match.Groups["name"].Value, attributeName, StringComparison.OrdinalIgnoreCase)
                          && AttributeValue(match).Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAllowedAuthState(string contents)
    {
        return AttributeRegex.Matches(contents)
            .Cast<Match>()
            .Any(match => string.Equals(match.Groups["name"].Value, "data-rw-auth-state", StringComparison.OrdinalIgnoreCase)
                          && string.Equals(AttributeValue(match), "allowed", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsArtifactLeakMarker(string contents)
    {
        return ArtifactLeakMarkers.Any(marker => contents.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasTextArtifactPath(string artifactPath)
    {
        var fileName = Path.GetFileName(artifactPath);
        if (ExtensionlessTextFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return TextExtensions.Contains(Path.GetExtension(artifactPath), StringComparer.OrdinalIgnoreCase);
    }

    private static string AttributeValue(Match match)
    {
        var value = match.Groups["double"].Success
            ? match.Groups["double"].Value
            : match.Groups["single"].Success
                ? match.Groups["single"].Value
                : match.Groups["bare"].Value;

        return NormalizeEscapedAttributeValue(value);
    }

    private static string NormalizeEscapedAttributeValue(string value)
    {
        var normalized = value.Trim();
        normalized = normalized
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\'", "'", StringComparison.Ordinal);

        if (normalized.Length >= 2
            && ((normalized[0] == '"' && normalized[^1] == '"')
                || (normalized[0] == '\'' && normalized[^1] == '\'')))
        {
            normalized = normalized[1..^1];
        }

        return normalized;
    }
}
