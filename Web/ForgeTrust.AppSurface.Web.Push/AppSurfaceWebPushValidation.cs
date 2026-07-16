using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Push;

/// <summary>Provides package-internal canonical validation for configuration, routes, and browser subscription material.</summary>
/// <remarks>Validation methods return <see langword="false"/> for malformed external input and never echo secret key material.</remarks>
internal static partial class AppSurfaceWebPushValidation
{
    /// <summary>Determines whether a key identifier uses 1 through 64 safe ASCII characters and begins alphanumerically.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <returns><see langword="true"/> when the identifier is safe for diagnostics and lookup.</returns>
    public static bool IsSafeKeyId(string? value) =>
        !string.IsNullOrEmpty(value) && KeyIdPattern().IsMatch(value);

    /// <summary>Decodes a canonical unpadded base64url value with an exact decoded length.</summary>
    /// <param name="value">The candidate base64url text. Padding and noncanonical encodings are rejected.</param>
    /// <param name="expectedLength">The required decoded byte count.</param>
    /// <param name="decoded">Receives decoded bytes on success, or an empty array on failure.</param>
    /// <returns><see langword="true"/> only when decoding, length, alphabet, and canonical round-trip checks pass.</returns>
    public static bool TryDecodeCanonicalBase64Url(string? value, int expectedLength, out byte[] decoded)
    {
        decoded = [];
        if (string.IsNullOrEmpty(value)
            || value.Contains('=')
            || !Base64UrlPattern().IsMatch(value))
        {
            return false;
        }

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            decoded = Convert.FromBase64String(padded);
            return decoded.Length == expectedLength && Base64UrlEncode(decoded) == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Determines whether a value is a canonical uncompressed NIST P-256 public point.</summary>
    /// <param name="value">The 65-byte uncompressed point encoded as canonical unpadded base64url.</param>
    /// <returns><see langword="true"/> when the runtime accepts the point on NIST P-256.</returns>
    public static bool IsValidP256PublicKey(string? value)
    {
        if (!TryDecodeCanonicalBase64Url(value, 65, out var bytes) || bytes[0] != 4)
        {
            return false;
        }

        try
        {
            using var algorithm = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = bytes[1..33], Y = bytes[33..65] },
            });
            return algorithm.KeySize == 256;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Determines whether canonical P-256 public and private VAPID keys form one key pair.</summary>
    /// <param name="publicKey">The 65-byte uncompressed public point encoded as canonical base64url.</param>
    /// <param name="privateKey">The 32-byte private scalar encoded as canonical base64url.</param>
    /// <returns><see langword="true"/> when the derived public coordinates match in fixed time.</returns>
    public static bool IsMatchingVapidPair(string? publicKey, string? privateKey)
    {
        if (!IsValidP256PublicKey(publicKey)
            || !TryDecodeCanonicalBase64Url(privateKey, 32, out var privateBytes)
            || !TryDecodeCanonicalBase64Url(publicKey, 65, out var publicBytes))
        {
            return false;
        }

        try
        {
            using var algorithm = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = privateBytes,
            });
            var derived = algorithm.ExportParameters(false);
            return CryptographicOperations.FixedTimeEquals(derived.Q.X!, publicBytes.AsSpan(1, 32))
                && CryptographicOperations.FixedTimeEquals(derived.Q.Y!, publicBytes.AsSpan(33, 32));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Determines whether a VAPID subject is a canonical contact <c>mailto:</c> or HTTPS URI.</summary>
    /// <param name="value">The candidate contact URI.</param>
    /// <returns><see langword="true"/> when the value contains no whitespace, user-info, fragment, or unsupported scheme.</returns>
    public static bool IsValidSubject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (value.StartsWith("mailto:", StringComparison.Ordinal))
        {
            const string prefix = "mailto:";
            var addressText = value[prefix.Length..];
            return uri.Scheme == Uri.UriSchemeMailto
                && string.IsNullOrEmpty(uri.Query)
                && !addressText.Contains('%')
                && MailAddress.TryCreate(addressText, out var address)
                && !string.IsNullOrEmpty(address.Host)
                && string.Equals(address.Address, addressText, StringComparison.Ordinal);
        }

        return uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo)
            && !string.IsNullOrEmpty(uri.Host);
    }

    /// <summary>Normalizes and validates one exact HTTPS default-port push-service origin.</summary>
    /// <param name="value">The candidate origin without credentials, path, query, fragment, wildcard, or custom port.</param>
    /// <param name="origin">Receives the canonical authority on success, or an empty string on failure.</param>
    /// <returns><see langword="true"/> only when the input already equals its canonical origin.</returns>
    public static bool TryNormalizeAllowedOrigin(string? value, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || value.Contains('%')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return string.Equals(value, origin, StringComparison.Ordinal);
    }

    /// <summary>Validates a push subscription endpoint against the exact allowed-origin set.</summary>
    /// <param name="value">The absolute HTTPS default-port endpoint, limited to 4,096 characters.</param>
    /// <param name="allowedOrigins">Canonical origins accepted by the host.</param>
    /// <param name="origin">Receives the endpoint authority when its URL shape is valid, even if it is not allowed.</param>
    /// <returns><see langword="true"/> when the endpoint shape is valid and its authority is allowlisted.</returns>
    public static bool TryValidateEndpoint(string? value, ISet<string> allowedOrigins, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrEmpty(value)
            || value.Length > 4096
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return allowedOrigins.Contains(origin);
    }

    /// <summary>Determines whether a local asset path is literal, app-root-relative, and free of query or traversal syntax.</summary>
    /// <param name="value">The candidate path.</param>
    /// <returns><see langword="true"/> when raw and repeatedly decoded forms remain safe.</returns>
    public static bool IsValidAssetPath(string? value) => IsValidPath(value, allowQuery: false);

    /// <summary>Determines whether a local destination path is safe, allowing at most one query separator.</summary>
    /// <param name="value">The candidate path and optional query.</param>
    /// <returns><see langword="true"/> when raw and repeatedly decoded path forms remain app-root-relative and traversal-free.</returns>
    public static bool IsValidDestinationPath(string? value) => IsValidPath(value, allowQuery: true);

    /// <summary>Determines whether an optional Web Push topic uses 1 through 32 URL-safe characters.</summary>
    /// <param name="value">The topic, or <see langword="null"/> when no topic is requested.</param>
    /// <returns><see langword="true"/> for a missing or valid topic.</returns>
    public static bool IsValidTopic(string? value) =>
        value is null || TopicPattern().IsMatch(value);

    /// <summary>Encodes bytes as canonical unpadded base64url text.</summary>
    /// <param name="value">The bytes to encode.</param>
    /// <returns>The canonical base64url representation without padding.</returns>
    public static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsValidPath(string? value, bool allowQuery)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 1024
            || value[0] != '/'
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Contains('#')
            || (!allowQuery && value.Contains('?'))
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '{' or '}'))
        {
            return false;
        }

        var queryIndex = value.IndexOf('?');
        if (allowQuery && queryIndex >= 0 && value.IndexOf('?', queryIndex + 1) >= 0)
        {
            return false;
        }

        var pathname = queryIndex < 0 ? value : value[..queryIndex];
        if (HasMalformedEscape(pathname))
        {
            return false;
        }

        try
        {
            var decoded = pathname;
            while (true)
            {
                var next = Uri.UnescapeDataString(decoded);
                if (next == decoded)
                {
                    break;
                }

                decoded = next;
            }

            return !decoded.StartsWith("//", StringComparison.Ordinal)
                && !decoded.Contains('\\')
                && !decoded.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '{' or '}' or '?' or '#')
                && !decoded.Split('/').Any(segment => segment is "." or "..");
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool HasMalformedEscape(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length
                || !Uri.IsHexDigit(value[index + 1])
                || !Uri.IsHexDigit(value[index + 2]))
            {
                return true;
            }

            index += 2;
        }

        return false;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64UrlPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();
}

/// <summary>Validates the bounded VAPID key ring and exact push-service origin allowlist at startup.</summary>
internal sealed class AppSurfaceWebPushOptionsValidator : IValidateOptions<AppSurfaceWebPushOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AppSurfaceWebPushOptions options)
    {
        var failures = new List<string>();
        if (options.VapidKeys.Count is < 1 or > 8)
        {
            failures.Add("ASPUSHCFG001: VapidKeys must contain 1 through 8 entries.");
        }

        if (!AppSurfaceWebPushValidation.IsSafeKeyId(options.ActiveVapidKeyId)
            || !options.VapidKeys.ContainsKey(options.ActiveVapidKeyId!))
        {
            failures.Add("ASPUSHCFG002: ActiveVapidKeyId must be a safe ID present in VapidKeys.");
        }

        foreach (var pair in options.VapidKeys)
        {
            if (!AppSurfaceWebPushValidation.IsSafeKeyId(pair.Key))
            {
                failures.Add("ASPUSHCFG003: A VapidKeys entry has an invalid safe ID.");
                continue;
            }

            if (!AppSurfaceWebPushValidation.IsValidSubject(pair.Value.Subject))
            {
                failures.Add($"ASPUSHCFG004: VapidKeys['{pair.Key}'].Subject must be a mailto or HTTPS URI.");
            }

            if (!AppSurfaceWebPushValidation.IsMatchingVapidPair(pair.Value.PublicKey, pair.Value.PrivateKey))
            {
                failures.Add($"ASPUSHCFG005: VapidKeys['{pair.Key}'] must contain a matching P-256 key pair.");
            }
        }

        if (options.AllowedPushServiceOrigins.Count is < 1 or > 16)
        {
            failures.Add("ASPUSHCFG006: AllowedPushServiceOrigins must contain 1 through 16 exact origins.");
        }

        foreach (var allowedOrigin in options.AllowedPushServiceOrigins)
        {
            if (!AppSurfaceWebPushValidation.TryNormalizeAllowedOrigin(allowedOrigin, out _))
            {
                failures.Add("ASPUSHCFG007: AllowedPushServiceOrigins contains an invalid exact HTTPS default-port origin.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
