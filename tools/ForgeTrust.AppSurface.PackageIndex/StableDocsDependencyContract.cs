namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Defines the exact parser and sanitizer identities approved for stable AppSurface Docs packages.
/// </summary>
/// <remarks>
/// This is the single source of truth for the stable-exit package contract tracked by issue #682. Package archive
/// validation, isolated consumer graph verification, and generated proof guidance all consume these identities so a
/// future dependency update cannot change one proof surface without changing the others.
/// </remarks>
internal static class StableDocsDependencyContract
{
    /// <summary>
    /// Gets the package id whose stable dependency graph this contract protects.
    /// </summary>
    internal const string DocsPackageId = "ForgeTrust.AppSurface.Docs";

    /// <summary>
    /// Gets the approved parser and sanitizer package identities in reader-facing order.
    /// </summary>
    internal static IReadOnlyList<StableDocsPackageDependency> Dependencies { get; } =
    [
        new("AngleSharp", "1.7.1"),
        new("HtmlSanitizer", "9.2.995"),
        new("AngleSharp.Css", "1.0.1")
    ];

    /// <summary>
    /// Gets the exact NuGet dependency declaration required for the supplied stable dependency.
    /// </summary>
    /// <param name="dependency">Approved parser or sanitizer dependency.</param>
    /// <returns>An exact bracketed NuGet version range.</returns>
    internal static string ExactVersionRange(StableDocsPackageDependency dependency) => $"[{dependency.Version}]";

    /// <summary>
    /// Gets the Markdown-formatted dependency list used in maintainer guidance.
    /// </summary>
    internal static string MarkdownDependencyList => string.Join(
        ", ",
        Dependencies.Select(dependency => $"`{dependency.Id}` `{ExactVersionRange(dependency)}`"));

    /// <summary>
    /// Gets a plain-text dependency list used in actionable diagnostics.
    /// </summary>
    internal static string PlainTextDependencyList => string.Join(
        ", ",
        Dependencies.Select(dependency => $"{dependency.Id} {ExactVersionRange(dependency)}"));
}

/// <summary>
/// One approved stable Docs parser or sanitizer dependency identity.
/// </summary>
/// <param name="Id">NuGet package id.</param>
/// <param name="Version">Exact resolved stable version.</param>
internal sealed record StableDocsPackageDependency(string Id, string Version);
