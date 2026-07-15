using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Web.Push;

internal static partial class AppSurfaceWebPushValidation
{
    public static bool IsSafeKeyId(string? value) =>
        !string.IsNullOrEmpty(value) && KeyIdPattern().IsMatch(value);

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

    public static bool IsValidAssetPath(string? value) => IsValidPath(value, allowQuery: false);

    public static bool IsValidDestinationPath(string? value) => IsValidPath(value, allowQuery: true);

    public static bool IsValidTopic(string? value) =>
        value is null || TopicPattern().IsMatch(value);

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

internal sealed class AppSurfaceWebPushOptionsValidator : IValidateOptions<AppSurfaceWebPushOptions>
{
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
