using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ForgeTrust.AppSurface.Auth.Keycloak;

/// <summary>Configures exact leaf-certificate pinning for Keycloak worker HTTP requests.</summary>
internal static class KeycloakPinnedCertificate
{
    /// <summary>Loads the expected certificate from PEM and installs the worker's pin-validation callback.</summary>
    /// <param name="handler">The worker HTTP handler to configure.</param>
    /// <param name="pemPath">The path to the PEM containing the expected leaf certificate.</param>
    /// <remarks>The callback checks the hostname, the inclusive UTC validity window, and the exact SHA-256 leaf hash.</remarks>
    internal static void Configure(HttpClientHandler handler, string pemPath)
    {
        using var expected = X509Certificate2.CreateFromPem(File.ReadAllText(pemPath));
        var expectedHash = expected.GetCertHash(HashAlgorithmName.SHA256);
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            certificate is not null && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
            && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime()
            && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), expectedHash);
    }
}
