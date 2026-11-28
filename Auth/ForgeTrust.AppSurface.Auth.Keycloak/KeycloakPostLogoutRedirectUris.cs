using System.Text.Json;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Reads and writes Keycloak's client attribute for post-logout redirect URIs.</summary>
internal static class KeycloakPostLogoutRedirectUris
{
    /// <summary>The attribute used in both realm imports and live client representations.</summary>
    internal const string AttributeName = "post.logout.redirect.uris";

    private const string Separator = "##";

    /// <summary>Serializes the caller's validated URI strings in declaration order.</summary>
    internal static string Format(IEnumerable<string> uris) => string.Join(Separator, uris);

    /// <summary>Reads the configured URIs as an ordinal set, ignoring empty entries and surrounding whitespace.</summary>
    /// <remarks>Missing, empty, and non-string attribute values represent an empty set for baseline verification.</remarks>
    internal static HashSet<string> Read(JsonElement client)
    {
        if (!client.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object
            || !attributes.TryGetProperty(AttributeName, out var attribute) || attribute.ValueKind != JsonValueKind.String
            || attribute.GetString() is not { Length: > 0 } value)
        {
            return [];
        }

        return value
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}
