using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace ForgeTrust.AppSurface.Docs.Services;

/// <summary>
/// Classifies the received request before routing can normalize away alias ownership evidence.
/// </summary>
/// <remarks>
/// Raw escaped path → one strict decoded validation view → application-relative ownership → safety.
/// The decoded view never becomes a filesystem path. Ownership survives every safety failure.
/// </remarks>
internal static class AppSurfaceDocsAliasRequestClassifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Finds an owner in raw, decoded, then framework coordinates and validates the selected path.</summary>
    /// <param name="context">Received context after host PathBase setup.</param>
    /// <param name="owners">Disjoint active alias namespace roots.</param>
    /// <returns>Sticky owner and accepted label, or null for an unowned request.</returns>
    internal static AppSurfaceDocsAliasRequest? Classify(HttpContext context, IReadOnlyList<string> owners)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(owners);
        var framework = context.Request.Path.Value ?? string.Empty;
        var extracted = TryExtractPath(context.Features.Get<IHttpRequestFeature>()?.RawTarget, out var raw);
        var decoded = string.Empty;
        var decodedOk = extracted && TryDecode(raw, out decoded);
        var pathBase = context.Request.PathBase.Value ?? string.Empty;
        var rawBaseOk = StripPathBase(raw, context.Request.PathBase.ToUriComponent(), out var rawRelative);
        var decodedBaseOk = StripPathBase(decoded, pathBase, out var decodedRelative);
        var owner = FindOwner(rawRelative, owners) ?? FindOwner(decodedRelative, owners) ?? FindOwner(framework, owners);
        if (owner is null)
        {
            return null;
        }

        if (!extracted || !decodedOk || !rawBaseOk || !decodedBaseOk
            || !IsSafePath(rawRelative, allowEscapes: true) || HasEncodedSeparator(rawRelative)
            || !IsSafePath(decodedRelative) || !IsSafePath(framework)
            || !string.Equals(decodedRelative, framework, StringComparison.OrdinalIgnoreCase)
            || !HasPrefix(decodedRelative, owner))
        {
            return new AppSurfaceDocsAliasRequest(owner, null, false);
        }

        var decodedSuffix = decodedRelative.Length == owner.Length ? string.Empty : decodedRelative[(owner.Length + 1)..];
        var name = decodedSuffix.Split('/', 2)[0];
        if (name.Length == 0)
        {
            return new AppSurfaceDocsAliasRequest(owner, null, true);
        }

        // The raw name must itself use the ASCII descriptor grammar; an escaped name is never a lookup key.
        var rawSegments = rawRelative.Split('/');
        var rootSegmentCount = owner.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        if (rawSegments.Length <= rootSegmentCount + 1
            || !string.Equals(rawSegments[rootSegmentCount + 1], name, StringComparison.OrdinalIgnoreCase)
            || !AppSurfaceDocsVersionAliasName.TryNormalize(name, out var normalized)
            || !string.Equals(normalized, name, StringComparison.OrdinalIgnoreCase))
        {
            return new AppSurfaceDocsAliasRequest(owner, null, false);
        }

        return new AppSurfaceDocsAliasRequest(owner, normalized, true);
    }

    /// <summary>Extracts origin/HTTP absolute-form paths without URI path normalization.</summary>
    internal static bool TryExtractPath(string? target, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        if (target.StartsWith('/'))
        {
            var query = target.IndexOf('?');
            path = query < 0 ? target : target[..query];
            return !path.Contains('#');
        }

        var start = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7
            : target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 : 0;
        if (start == 0)
        {
            return false;
        }

        var authorityEnd = target.IndexOfAny(['/', '?', '#'], start);
        var authority = authorityEnd < 0 ? target[start..] : target[start..authorityEnd];
        if (!Uri.TryCreate(target[..start] + authority, UriKind.Absolute, out var origin)
            || string.IsNullOrEmpty(origin.Host) || authority.Contains('@') || target.Contains('#'))
        {
            return false;
        }

        if (authorityEnd < 0 || target[authorityEnd] == '?')
        {
            path = "/";
            return true;
        }

        var queryIndex = target.IndexOf('?', authorityEnd);
        path = queryIndex < 0 ? target[authorityEnd..] : target[authorityEnd..queryIndex];
        return true;
    }

    private static bool TryDecode(string raw, out string decoded)
    {
        var output = new StringBuilder(raw.Length);
        decoded = string.Empty;
        try
        {
            for (var index = 0; index < raw.Length; index++)
            {
                if (raw[index] != '%')
                {
                    output.Append(raw[index]);
                    continue;
                }

                var bytes = new List<byte>();
                while (index < raw.Length && raw[index] == '%')
                {
                    if (index + 2 >= raw.Length || !byte.TryParse(raw.AsSpan(index + 1, 2),
                            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    {
                        decoded = output.ToString();
                        return false;
                    }

                    bytes.Add(value);
                    index += 3;
                }

                index--;
                output.Append(StrictUtf8.GetString(bytes.ToArray()));
            }

            decoded = output.ToString();
            return true;
        }
        catch (DecoderFallbackException)
        {
            decoded = output.ToString();
            return false;
        }
    }

    private static bool StripPathBase(string path, string pathBase, out string relative)
    {
        relative = path;
        if (pathBase.Length == 0 || pathBase == "/")
        {
            return true;
        }

        if (!HasPrefix(path, pathBase))
        {
            return false;
        }

        relative = path.Length == pathBase.Length ? "/" : path[pathBase.Length..];
        return true;
    }

    private static string? FindOwner(string path, IReadOnlyList<string> owners)
    {
        foreach (var owner in owners)
        {
            if (HasPrefix(path, owner))
            {
                return owner;
            }
        }

        return null;
    }

    private static bool HasPrefix(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static bool HasEncodedSeparator(string path) => path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || path.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafePath(string path, bool allowEscapes = false) => path.StartsWith('/')
        && !path.Any(character => character == '\\' || char.IsControl(character))
        && !path.Split('/').Any(segment => segment is "." or "..")
        && (allowEscapes || !path.Contains('%')) && !path.Contains('#') && !path.Contains('?');
}

/// <summary>Sticky ownership state; unsafe requests retain the same owner and authorization endpoint.</summary>
/// <param name="NamespaceRoot">Selected app-relative active namespace.</param>
/// <param name="Name">Accepted normalized ASCII label, or null for unknown/unsafe input.</param>
/// <param name="IsSafe">Whether the received raw and framework paths passed the boundary.</param>
internal sealed record AppSurfaceDocsAliasRequest(string NamespaceRoot, string? Name, bool IsSafe);
