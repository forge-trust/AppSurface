using System.Text;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Defines the opaque client credential text policy shared by both custody profiles.</summary>
internal static class KeycloakClientCredentialText
{
    /// <summary>The maximum length of a captured credential in UTF-8 bytes.</summary>
    internal const int MaximumBytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Encodes the exact supplied text, throwing if it cannot be represented as strict UTF-8.</summary>
    internal static byte[] Encode(string value) => StrictUtf8.GetBytes(value);

    /// <summary>Decodes the exact supplied bytes, throwing on invalid UTF-8.</summary>
    internal static string Decode(ReadOnlySpan<byte> bytes) => StrictUtf8.GetString(bytes);

    /// <summary>Requires non-whitespace text without control characters.</summary>
    internal static bool IsValid(string value) => !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
