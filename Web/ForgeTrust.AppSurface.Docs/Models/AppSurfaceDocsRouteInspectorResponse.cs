using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Docs.Services;

namespace ForgeTrust.AppSurface.Docs.Models;

/// <summary>
/// Redacted response for the AppSurface Docs route inspector HTML and JSON surfaces.
/// </summary>
/// <remarks>
/// This response mirrors the current snapshot route manifest for public route entries and can include an optional probe
/// explaining how one maintainer-supplied path resolves. It intentionally contains repository-relative source paths and
/// app-relative live URLs only; it does not expose absolute repository roots, filesystem paths, or raw exceptions.
/// </remarks>
public sealed record AppSurfaceDocsRouteInspectorResponse
{
    /// <summary>
    /// Gets the optional route probe result for the requested path.
    /// </summary>
    [JsonPropertyName("probe")]
    public AppSurfaceDocsRouteProbeResponse? Probe { get; init; }

    /// <summary>
    /// Gets public canonical route entries from the current snapshot route manifest.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<AppSurfaceDocsRouteInspectorEntryResponse> Entries { get; init; } = [];

    /// <summary>
    /// Gets redacted route diagnostics from the current snapshot route manifest.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<AppSurfaceDocsHarvestDiagnosticResponse> Diagnostics { get; init; } = [];

    internal static AppSurfaceDocsRouteInspectorResponse FromManifest(
        AppSurfaceDocsRouteManifest manifest,
        AppSurfaceDocsRouteProbeResponse? probe)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return new AppSurfaceDocsRouteInspectorResponse
        {
            Probe = probe,
            Entries = manifest.Entries.Select(AppSurfaceDocsRouteInspectorEntryResponse.FromEntry).ToArray(),
            Diagnostics = manifest.Diagnostics.Select(AppSurfaceDocsHarvestDiagnosticResponse.FromDiagnostic).ToArray()
        };
    }
}

/// <summary>
/// Describes one public canonical AppSurface Docs route and its aliases.
/// </summary>
public sealed record AppSurfaceDocsRouteInspectorEntryResponse
{
    /// <summary>
    /// Gets the repository-relative source path for the canonical winner.
    /// </summary>
    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the docs-root-relative canonical route path.
    /// </summary>
    [JsonPropertyName("canonicalRoutePath")]
    public string CanonicalRoutePath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the app-relative live URL for the canonical route.
    /// </summary>
    [JsonPropertyName("canonicalLiveUrl")]
    public string CanonicalLiveUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the source path is a Markdown document.
    /// </summary>
    [JsonPropertyName("sourcePathIsMarkdown")]
    public bool SourcePathIsMarkdown { get; init; }

    /// <summary>
    /// Gets implicit source-shaped aliases that recover copied Markdown paths.
    /// </summary>
    [JsonPropertyName("recoveryAliases")]
    public IReadOnlyList<AppSurfaceDocsRouteAliasResponse> RecoveryAliases { get; init; } = [];

    /// <summary>
    /// Gets author-declared redirect aliases from documentation metadata.
    /// </summary>
    [JsonPropertyName("declaredAliases")]
    public IReadOnlyList<AppSurfaceDocsRouteAliasResponse> DeclaredAliases { get; init; } = [];

    internal static AppSurfaceDocsRouteInspectorEntryResponse FromEntry(AppSurfaceDocsRouteManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new AppSurfaceDocsRouteInspectorEntryResponse
        {
            SourcePath = entry.SourcePath,
            CanonicalRoutePath = entry.CanonicalRoutePath,
            CanonicalLiveUrl = entry.CanonicalLiveUrl,
            SourcePathIsMarkdown = entry.SourcePathIsMarkdown,
            RecoveryAliases = entry.RecoveryAliases.Select(AppSurfaceDocsRouteAliasResponse.FromAlias).ToArray(),
            DeclaredAliases = entry.DeclaredAliases.Select(AppSurfaceDocsRouteAliasResponse.FromAlias).ToArray()
        };
    }
}

/// <summary>
/// Describes one route alias surfaced by the route inspector.
/// </summary>
public sealed record AppSurfaceDocsRouteAliasResponse
{
    /// <summary>
    /// Gets the docs-root-relative alias route path.
    /// </summary>
    [JsonPropertyName("routePath")]
    public string RoutePath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the app-relative live URL for the alias.
    /// </summary>
    [JsonPropertyName("liveUrl")]
    public string LiveUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets the alias source category.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    internal static AppSurfaceDocsRouteAliasResponse FromAlias(AppSurfaceDocsRouteAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);

        return new AppSurfaceDocsRouteAliasResponse
        {
            RoutePath = alias.RoutePath,
            LiveUrl = alias.LiveUrl,
            Kind = alias.Kind.ToString()
        };
    }
}

/// <summary>
/// Explains how one requested route-inspector path resolves against the current route identity catalog.
/// </summary>
public sealed record AppSurfaceDocsRouteProbeResponse
{
    /// <summary>
    /// Gets the raw path supplied to the route inspector.
    /// </summary>
    [JsonPropertyName("inputPath")]
    public string InputPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the docs-root-relative path used for route lookup, when the input was valid.
    /// </summary>
    [JsonPropertyName("normalizedPath")]
    public string? NormalizedPath { get; init; }

    /// <summary>
    /// Gets the route resolution kind.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the repository-relative source path associated with the route, when one exists.
    /// </summary>
    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; init; }

    /// <summary>
    /// Gets the docs-root-relative canonical route path associated with the route, when one exists.
    /// </summary>
    [JsonPropertyName("canonicalRoutePath")]
    public string? CanonicalRoutePath { get; init; }

    /// <summary>
    /// Gets the app-relative live URL for the canonical route, when one exists.
    /// </summary>
    [JsonPropertyName("canonicalLiveUrl")]
    public string? CanonicalLiveUrl { get; init; }

    /// <summary>
    /// Gets a short maintainer-facing explanation of the route result.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    internal static AppSurfaceDocsRouteProbeResponse Invalid(string inputPath, string message)
    {
        return new AppSurfaceDocsRouteProbeResponse
        {
            InputPath = inputPath,
            Kind = "InvalidInput",
            Message = message
        };
    }

    internal static AppSurfaceDocsRouteProbeResponse FromResolution(
        string inputPath,
        string normalizedPath,
        DocRouteResolution resolution,
        DocsUrlBuilder docsUrlBuilder)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(docsUrlBuilder);

        return new AppSurfaceDocsRouteProbeResponse
        {
            InputPath = inputPath,
            NormalizedPath = normalizedPath,
            Kind = resolution.Kind.ToString(),
            SourcePath = resolution.SourcePath,
            CanonicalRoutePath = resolution.PublicRoutePath,
            CanonicalLiveUrl = string.IsNullOrWhiteSpace(resolution.PublicRoutePath)
                ? null
                : docsUrlBuilder.BuildDocUrl(resolution.PublicRoutePath),
            Message = GetMessage(resolution.Kind)
        };
    }

    private static string GetMessage(DocRouteResolutionKind kind)
    {
        return kind switch
        {
            DocRouteResolutionKind.Canonical => "This path is the canonical public route.",
            DocRouteResolutionKind.AliasRedirect => "This path redirects to the canonical public route.",
            DocRouteResolutionKind.InternalSourceMatch => "This path matches an internal source shape but is not itself a public route.",
            DocRouteResolutionKind.CollisionLoser => "This path belongs to a document that lost canonical route ownership.",
            DocRouteResolutionKind.ReservedRoute => "This path is reserved for AppSurface Docs infrastructure.",
            DocRouteResolutionKind.NotFound => "No route identity matched this path.",
            _ => "The route result is not recognized by this AppSurface Docs version."
        };
    }
}
