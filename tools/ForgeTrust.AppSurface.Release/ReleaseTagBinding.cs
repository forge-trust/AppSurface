using System.Globalization;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Canonical annotated-tag binding for one prepared AppSurface release.
/// </summary>
/// <remarks>
/// The binding repeats the prepared sidecar and manifest digests alongside the release evidence subject so a maintainer can
/// identify the mismatched artifact directly. It is not a replacement for the checked-in evidence bundle; the resolver validates
/// both sources and uses the tag object as the immutable state-transition record.
/// </remarks>
internal sealed record ReleaseTagBinding(
    string ReleaseId,
    string PreparedSidecarSha256,
    string ManifestSha256,
    string EvidenceSubjectSha256)
{
    internal const string ReleaseIdKey = "AppSurface-Release-Id";
    internal const string PreparedSidecarSha256Key = "AppSurface-Release-Prepared-Sidecar-Sha256";
    internal const string ManifestSha256Key = "AppSurface-Release-Manifest-Sha256";
    internal const string EvidenceSubjectSha256Key = "AppSurface-Release-Evidence-Subject-Sha256";

    private static readonly string[] RequiredKeys =
    [
        ReleaseIdKey,
        PreparedSidecarSha256Key,
        ManifestSha256Key,
        EvidenceSubjectSha256Key
    ];

    /// <summary>
    /// Renders the exact trailing message block accepted by the resolver.
    /// </summary>
    /// <returns>Canonical four-line trailer block with a trailing newline.</returns>
    internal string Render()
    {
        return string.Join(
            "\n",
            $"{ReleaseIdKey}: {ReleaseId}",
            $"{PreparedSidecarSha256Key}: {PreparedSidecarSha256}",
            $"{ManifestSha256Key}: {ManifestSha256}",
            $"{EvidenceSubjectSha256Key}: {EvidenceSubjectSha256}")
            + "\n";
    }

    /// <summary>
    /// Parses a Git tag object and validates its final AppSurface release trailer block.
    /// </summary>
    /// <param name="tag">Annotated tag name used in diagnostics.</param>
    /// <param name="tagObject">Raw output from git cat-file -p.</param>
    /// <param name="expected">Expected binding computed from tagged artifact bytes.</param>
    internal static void ParseAndValidate(string tag, string tagObject, ReleaseTagBinding expected)
    {
        var body = ExtractMessage(tag, tagObject);
        var lines = body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n')
            .Split('\n', StringSplitOptions.None);
        if (lines.Length < RequiredKeys.Length)
        {
            throw MissingRequiredTrailer(tag, RequiredKeys[0]);
        }

        var trailerStart = lines.Length - RequiredKeys.Length;
        for (var index = 0; index < RequiredKeys.Length; index++)
        {
            var expectedKey = RequiredKeys[index];
            var line = lines[trailerStart + index];
            if (!line.StartsWith(expectedKey + ": ", StringComparison.Ordinal))
            {
                if (line.StartsWith("AppSurface-Release-", StringComparison.Ordinal))
                {
                    throw InvalidTrailer(tag, expectedKey, "The canonical release trailer block is out of order or malformed.");
                }

                throw MissingRequiredTrailer(tag, expectedKey);
            }

            var value = line[(expectedKey.Length + 2)..];
            if (string.IsNullOrWhiteSpace(value)
                || value != value.Trim())
            {
                throw InvalidTrailer(tag, expectedKey, "Trailer values must be non-empty and must not contain leading or trailing whitespace.");
            }
        }

        foreach (var line in lines[..trailerStart])
        {
            if (line.StartsWith("AppSurface-Release-", StringComparison.Ordinal))
            {
                throw InvalidTrailer(tag, line.Split(':', 2)[0], "Only the final canonical binding block may use the reserved AppSurface-Release namespace.");
            }
        }

        var actual = new ReleaseTagBinding(
            TrailerValue(lines[trailerStart], ReleaseIdKey, tag),
            TrailerValue(lines[trailerStart + 1], PreparedSidecarSha256Key, tag),
            TrailerValue(lines[trailerStart + 2], ManifestSha256Key, tag),
            TrailerValue(lines[trailerStart + 3], EvidenceSubjectSha256Key, tag));
        ValidateShape(tag, actual);
        ValidateMatches(tag, expected, actual);
    }

    /// <summary>
    /// Reads the tagger timestamp from a raw annotated Git tag object.
    /// </summary>
    /// <param name="tag">Annotated tag name used in diagnostics.</param>
    /// <param name="tagObject">Raw output from git cat-file -p.</param>
    /// <returns>Timestamp and offset recorded by the annotated tagger.</returns>
    internal static DateTimeOffset ParseTaggerTimestamp(string tag, string tagObject)
    {
        var taggerLine = tagObject
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("tagger ", StringComparison.Ordinal));
        if (taggerLine is null)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-tagger-missing",
                $"Annotated tag {tag} does not include a tagger timestamp.",
                "The tag object has no tagger header.",
                "Create an annotated tag with Git and run inspect before pushing it.",
                "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
        }

        var parts = taggerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !long.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds)
            || !TryParseOffset(parts[^1], out var offset))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-tagger-invalid",
                $"Annotated tag {tag} has an invalid tagger timestamp.",
                $"Git returned tagger header: {taggerLine}.",
                "Recreate the unpushed annotated tag with a valid Git identity and inspect it again.",
                "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).ToOffset(offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-tagger-invalid",
                $"Annotated tag {tag} has an out-of-range tagger timestamp.",
                $"Git returned epoch seconds {epochSeconds}.",
                "Recreate the unpushed annotated tag and inspect it again.",
                "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
        }
    }

    private static string ExtractMessage(string tag, string tagObject)
    {
        var normalizedTagObject = tagObject.Replace("\r\n", "\n", StringComparison.Ordinal);
        var separator = normalizedTagObject.IndexOf("\n\n", StringComparison.Ordinal);
        if (separator < 0 || separator == normalizedTagObject.Length - 2)
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-trailer-missing",
                $"Annotated tag {tag} does not include the canonical release binding trailers.",
                "The tag object has no non-empty message body.",
                "Generate the tag message with ./eng/release tag-message, recreate the unpushed annotated tag, and run inspect again.",
                "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
        }

        return normalizedTagObject[(separator + 2)..];
    }

    private static string TrailerValue(string line, string key, string tag)
    {
        if (!line.StartsWith(key + ": ", StringComparison.Ordinal))
        {
            throw MissingRequiredTrailer(tag, key);
        }

        return line[(key.Length + 2)..];
    }

    private static void ValidateShape(string tag, ReleaseTagBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.ReleaseId))
        {
            throw InvalidTrailer(tag, ReleaseIdKey, "The release ID must not be empty.");
        }

        ValidateDigest(tag, PreparedSidecarSha256Key, binding.PreparedSidecarSha256);
        ValidateDigest(tag, ManifestSha256Key, binding.ManifestSha256);
        ValidateDigest(tag, EvidenceSubjectSha256Key, binding.EvidenceSubjectSha256);
    }

    private static void ValidateMatches(string tag, ReleaseTagBinding expected, ReleaseTagBinding actual)
    {
        ValidateMatch(tag, ReleaseIdKey, expected.ReleaseId, actual.ReleaseId);
        ValidateMatch(tag, PreparedSidecarSha256Key, expected.PreparedSidecarSha256, actual.PreparedSidecarSha256);
        ValidateMatch(tag, ManifestSha256Key, expected.ManifestSha256, actual.ManifestSha256);
        ValidateMatch(tag, EvidenceSubjectSha256Key, expected.EvidenceSubjectSha256, actual.EvidenceSubjectSha256);
    }

    private static void ValidateMatch(string tag, string key, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-tag-trailer-mismatch",
                $"Annotated tag {tag} has a stale or mismatched {key} binding.",
                $"Expected {expected} from the tagged release artifacts but found {actual}.",
                "Regenerate the tag message from the merged release commit. If the tag was not pushed, recreate it and run inspect again.",
                "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
        }
    }

    private static void ValidateDigest(string tag, string key, string value)
    {
        if (value.Length != 64
            || !value.All(character => (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
        {
            throw InvalidTrailer(tag, key, "SHA-256 trailer values must be lowercase 64-character hexadecimal strings.");
        }
    }

    private static ReleaseToolException MissingRequiredTrailer(string tag, string key)
    {
        return new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-tag-trailer-missing",
            $"Annotated tag {tag} is missing required trailer {key}.",
            "The tag does not contain the exact final AppSurface release binding block.",
            "Generate the tag message from the merged release commit, recreate the unpushed tag, and inspect it before pushing.",
            "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
    }

    private static ReleaseToolException InvalidTrailer(string tag, string key, string detail)
    {
        return new ReleaseToolException(ReleaseDiagnostic.Error(
            "release-tag-trailer-invalid",
            $"Annotated tag {tag} has an invalid {key} trailer.",
            detail,
            "Generate the tag message from the merged release commit, recreate the unpushed tag, and inspect it before pushing.",
            "tools/ForgeTrust.AppSurface.Release/README.md#prepared-to-tagged-state"));
    }

    private static bool TryParseOffset(string text, out TimeSpan offset)
    {
        offset = default;
        if (text.Length != 5
            || text[0] is not ('+' or '-')
            || !int.TryParse(text.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || hours > 14
            || minutes > 59
            || (hours == 14 && minutes != 0))
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, 0);
        if (text[0] == '-')
        {
            offset = -offset;
        }

        return true;
    }
}
