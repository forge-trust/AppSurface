namespace ForgeTrust.AppSurface.Durable;

/// <summary>
/// Describes the canonical privacy-safe problem, cause, corrective action, and documentation for a Durable diagnostic.
/// </summary>
/// <remarks>
/// Descriptors are created and published by <see cref="DurableDiagnosticCatalog"/>. Applications may retain or
/// compare them, and may pass their fields to <see cref="DurableProblem"/> with a caller-owned correlation identifier.
/// </remarks>
public sealed class DurableDiagnosticDescriptor
{
    /// <summary>Initializes a descriptor for use by the canonical diagnostic catalog.</summary>
    /// <param name="code">Stable Durable diagnostic code.</param>
    /// <param name="problem">Safe description of the observed problem.</param>
    /// <param name="cause">Safe description of the likely cause.</param>
    /// <param name="fix">Safe corrective guidance.</param>
    /// <param name="documentationUrl">Canonical HTTPS troubleshooting destination.</param>
    internal DurableDiagnosticDescriptor(
        string code,
        string problem,
        string cause,
        string fix,
        Uri documentationUrl)
    {
        Code = DurableIdentifier.Require(code, nameof(code), 120);
        Problem = DurableIdentifier.RequireSafeLabel(problem, nameof(problem), 500);
        Cause = DurableIdentifier.RequireSafeLabel(cause, nameof(cause), 1_000);
        Fix = DurableIdentifier.RequireSafeLabel(fix, nameof(fix), 1_000);
        DocumentationUrl = documentationUrl ?? throw new ArgumentNullException(nameof(documentationUrl));
        if (!DocumentationUrl.IsAbsoluteUri || DocumentationUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Durable diagnostic documentation URLs must be absolute HTTPS URLs.", nameof(documentationUrl));
        }
    }

    /// <summary>Gets the stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the canonical safe description of what failed.</summary>
    public string Problem { get; }

    /// <summary>Gets the canonical safe description of the likely cause.</summary>
    public string Cause { get; }

    /// <summary>Gets the canonical safe corrective guidance.</summary>
    public string Fix { get; }

    /// <summary>Gets the absolute HTTPS URL for this diagnostic's canonical troubleshooting entry.</summary>
    public Uri DocumentationUrl { get; }
}
