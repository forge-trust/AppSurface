using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.Auth.Aspire.Keycloak;

/// <summary>
/// Represents an immutable container image reference used to prove a Keycloak login theme.
/// </summary>
/// <remarks>
/// Theme verification is meaningful only when it is associated with a concrete Keycloak image. This type requires a
/// registry, tag, and SHA-256 digest so a moving tag cannot silently change the template baseline beneath a theme.
/// </remarks>
public sealed class AppSurfaceKeycloakImageReference
{
    private static readonly Regex DigestPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex RegistryLabelPattern = new("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex RepositoryPattern = new("^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex TagPattern = new("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);

    private AppSurfaceKeycloakImageReference(string registry, string image, string tag, string sha256)
    {
        Registry = registry;
        Image = image;
        Tag = tag;
        Sha256 = sha256;
    }

    /// <summary>
    /// Gets the container registry, including an optional port.
    /// </summary>
    public string Registry { get; }

    /// <summary>
    /// Gets the slash-separated repository name without the registry.
    /// </summary>
    public string Image { get; }

    /// <summary>
    /// Gets the immutable-reference tag retained for human-readable evidence.
    /// </summary>
    public string Tag { get; }

    /// <summary>
    /// Gets the lowercase hexadecimal SHA-256 image digest without the <c>sha256:</c> prefix.
    /// </summary>
    public string Sha256 { get; }

    /// <summary>
    /// Gets the canonical registry, image, tag, and digest reference.
    /// </summary>
    public string Value => $"{Registry}/{Image}:{Tag}@sha256:{Sha256}";

    /// <summary>
    /// Parses a fully-qualified immutable container image reference.
    /// </summary>
    /// <param name="value">A reference in <c>registry/repository:tag@sha256:&lt;64 lowercase hex characters&gt;</c> form.</param>
    /// <returns>The parsed image reference.</returns>
    /// <exception cref="AppSurfaceKeycloakException">The image reference is incomplete or non-deterministic.</exception>
    public static AppSurfaceKeycloakImageReference Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw Invalid("image reference cannot be blank or contain whitespace.");
        }

        const string digestMarker = "@sha256:";
        var digestIndex = value.IndexOf(digestMarker, StringComparison.Ordinal);
        if (digestIndex <= 0 || value.IndexOf(digestMarker, digestIndex + digestMarker.Length, StringComparison.Ordinal) >= 0)
        {
            throw Invalid("image reference must include exactly one @sha256 digest marker.");
        }

        var repositoryAndTag = value[..digestIndex];
        var sha256 = value[(digestIndex + digestMarker.Length)..];
        if (!DigestPattern.IsMatch(sha256))
        {
            throw Invalid("image digest must contain 64 lowercase hexadecimal characters.");
        }

        var firstSlash = repositoryAndTag.IndexOf('/', StringComparison.Ordinal);
        var lastSlash = repositoryAndTag.LastIndexOf('/');
        var tagSeparator = repositoryAndTag.LastIndexOf(':');
        if (firstSlash <= 0 || tagSeparator <= lastSlash || tagSeparator == repositoryAndTag.Length - 1)
        {
            throw Invalid("image reference must include registry, repository, and tag before its digest.");
        }

        var registry = repositoryAndTag[..firstSlash];
        var image = repositoryAndTag[(firstSlash + 1)..tagSeparator];
        var tag = repositoryAndTag[(tagSeparator + 1)..];
        if (!IsValidRegistry(registry) || !RepositoryPattern.IsMatch(image) || !TagPattern.IsMatch(tag))
        {
            throw Invalid("image registry, repository, or tag contains unsupported characters.");
        }

        return new AppSurfaceKeycloakImageReference(registry, image, tag, sha256);
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsValidRegistry(string registry)
    {
        var separator = registry.LastIndexOf(':');
        var host = separator < 0 ? registry : registry[..separator];
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host.Split('.').Any(label => !RegistryLabelPattern.IsMatch(label)))
        {
            return false;
        }

        if (separator < 0)
        {
            return true;
        }

        var portText = registry[(separator + 1)..];
        return portText.Length > 0
            && portText.All(char.IsAsciiDigit)
            && int.TryParse(portText, out var port)
            && port is > 0 and <= 65_535;
    }

    private static AppSurfaceKeycloakException Invalid(string detail) =>
        new(
            AppSurfaceKeycloakDiagnosticCodes.InvalidThemeConfiguration,
            $"Problem: the AppSurface Keycloak login theme image reference is invalid. Cause: {detail} Fix: use registry/repository:tag@sha256:<64 lowercase hex characters>. Docs: Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md#theme-quickstart. Code: {AppSurfaceKeycloakDiagnosticCodes.InvalidThemeConfiguration}.");
}
