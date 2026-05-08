using System.Text.RegularExpressions;
using ForgeTrust.Runnable.Web.RazorDocs.Services;
using Microsoft.Extensions.Options;

namespace ForgeTrust.Runnable.Web.RazorDocs;

/// <summary>
/// Represents configuration for the RazorDocs package and host.
/// </summary>
public sealed class RazorDocsOptions
{
    /// <summary>
    /// Gets the root configuration section name for RazorDocs.
    /// </summary>
    public const string SectionName = "RazorDocs";

    /// <summary>
    /// Gets or sets the active docs source mode.
    /// </summary>
    public RazorDocsMode Mode { get; set; } = RazorDocsMode.Source;

    /// <summary>
    /// Gets source-mode settings used when docs are harvested from a repository checkout.
    /// </summary>
    public RazorDocsSourceOptions Source { get; set; } = new();

    /// <summary>
    /// Gets bundle-mode settings used by future bundle-backed runtime loading.
    /// </summary>
    public RazorDocsBundleOptions Bundle { get; set; } = new();

    /// <summary>
    /// Gets sidebar rendering settings.
    /// </summary>
    public RazorDocsSidebarOptions Sidebar { get; set; } = new();

    /// <summary>
    /// Gets contributor provenance settings used to render source, edit, and freshness evidence on details pages.
    /// </summary>
    public RazorDocsContributorOptions Contributor { get; set; } = new();

    /// <summary>
    /// Gets routing settings that control where the live RazorDocs source surface is exposed.
    /// </summary>
    public RazorDocsRoutingOptions Routing { get; set; } = new();

    /// <summary>
    /// Gets versioning settings used to mount exact release trees and the archive surface.
    /// </summary>
    public RazorDocsVersioningOptions Versioning { get; set; } = new();
}

/// <summary>
/// Enumerates the supported RazorDocs content source modes.
/// </summary>
/// <remarks>
/// Numeric values are part of the public configuration and serialization contract. Do not reorder or renumber existing
/// members; changing these assignments can break persisted configuration, serialized payloads, and consumers. Add new
/// modes by appending members with new explicit values.
/// </remarks>
public enum RazorDocsMode
{
    /// <summary>
    /// Harvest docs from source files at runtime.
    /// </summary>
    Source = 0,

    /// <summary>
    /// Load docs from a prebuilt bundle. Reserved for a later implementation slice.
    /// </summary>
    Bundle = 1
}

/// <summary>
/// Source-mode configuration for RazorDocs.
/// </summary>
public sealed class RazorDocsSourceOptions
{
    /// <summary>
    /// Gets or sets the repository root used for source harvesting.
    /// When null, RazorDocs falls back to repository discovery from the content root.
    /// </summary>
    public string? RepositoryRoot { get; set; }
}

/// <summary>
/// Bundle-mode configuration for RazorDocs.
/// </summary>
public sealed class RazorDocsBundleOptions
{
    /// <summary>
    /// Gets or sets the path to the docs bundle payload.
    /// </summary>
    public string? Path { get; set; }
}

/// <summary>
/// Sidebar presentation settings for RazorDocs.
/// </summary>
public sealed class RazorDocsSidebarOptions
{
    /// <summary>
    /// Gets or sets configured namespace prefixes for sidebar label simplification.
    /// </summary>
    public string[] NamespacePrefixes { get; set; } = [];
}

/// <summary>
/// Contributor-provenance configuration for RazorDocs details pages.
/// </summary>
/// <remarks>
/// This contract controls the global contributor-provenance surface. Use <see cref="Enabled"/> to switch the entire
/// feature on or off for a host, and use page-level contributor metadata to suppress or override individual pages
/// without mutating host-wide defaults.
/// </remarks>
public sealed class RazorDocsContributorOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether contributor provenance rendering is enabled for RazorDocs details pages.
    /// Disable this when the host should suppress all contributor affordances, even if page-level overrides or
    /// trustworthy source paths exist. When <see langword="false" />, RazorDocs also skips contributor-template startup
    /// validation because the feature is globally inactive.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the stable branch name used when expanding configured source and edit URL templates.
    /// Required when <see cref="Enabled"/> is <see langword="true" /> and either
    /// <see cref="SourceUrlTemplate"/> or <see cref="EditUrlTemplate"/> is configured, and used as the fallback
    /// source ref for symbol links when <see cref="SourceRef"/> is not configured.
    /// </summary>
    public string? DefaultBranch { get; set; }

    /// <summary>
    /// Gets or sets the source-link template. Supported tokens are <c>{branch}</c> and <c>{path}</c>.
    /// Configured templates must include <c>{path}</c> so each page expands to its own source location when
    /// <see cref="Enabled"/> is <see langword="true" />.
    /// </summary>
    public string? SourceUrlTemplate { get; set; }

    /// <summary>
    /// Gets or sets the edit-link template. Supported tokens are <c>{branch}</c> and <c>{path}</c>.
    /// Configured templates must include <c>{path}</c> when <see cref="Enabled"/> is <see langword="true" />.
    /// Prefer this when maintainers should land directly in an edit workflow rather than in repository browsing.
    /// </summary>
    public string? EditUrlTemplate { get; set; }

    /// <summary>
    /// Gets or sets the source-link template for generated C# API symbols.
    /// Supported tokens are <c>{path}</c>, <c>{line}</c>, <c>{branch}</c>, and <c>{ref}</c>.
    /// Configured templates must include <c>{path}</c> and <c>{line}</c> when <see cref="Enabled"/> is
    /// <see langword="true" />, and unsupported token placeholders are rejected during startup validation.
    /// Use <c>{ref}</c> when links should prefer a commit SHA supplied through <see cref="SourceRef"/> and fall back to
    /// <see cref="DefaultBranch"/>.
    /// </summary>
    public string? SymbolSourceUrlTemplate { get; set; }

    /// <summary>
    /// Gets or sets the source-control ref used for generated C# API symbol links.
    /// Prefer a commit SHA when the docs build knows one. When omitted, symbol links that use <c>{ref}</c> fall back
    /// to <see cref="DefaultBranch"/> so hosts can still render moving-branch links intentionally.
    /// </summary>
    public string? SourceRef { get; set; }

    /// <summary>
    /// Gets or sets the mode used to resolve contributor freshness.
    /// The default is <see cref="RazorDocsLastUpdatedMode.None"/> so hosts opt into git-backed freshness explicitly
    /// instead of paying unexpected snapshot-time git costs.
    /// <see cref="RazorDocsLastUpdatedMode.Git"/> uses local repository history when a trustworthy source path exists and
    /// omits only freshness when git data is unavailable or untrustworthy.
    /// </summary>
    public RazorDocsLastUpdatedMode LastUpdatedMode { get; set; } = RazorDocsLastUpdatedMode.None;
}

/// <summary>
/// Enumerates the supported contributor freshness modes for RazorDocs details pages.
/// </summary>
/// <remarks>
/// Numeric values are part of the public configuration and serialization contract. Do not reorder or renumber existing
/// members; changing these assignments can break persisted configuration, serialized payloads, and consumers. Add new
/// modes by appending members with new explicit values.
/// </remarks>
public enum RazorDocsLastUpdatedMode
{
    /// <summary>
    /// Do not render automatic contributor freshness.
    /// </summary>
    None = 0,

    /// <summary>
    /// Resolve contributor freshness from local git history when a trustworthy source path exists.
    /// Hosts should expect graceful omission when git history is unavailable, shallow, or not trustworthy for the page.
    /// </summary>
    Git = 1
}

/// <summary>
/// Routing settings for the RazorDocs route family and live source surface.
/// </summary>
/// <remarks>
/// RazorDocs separates the route-family root from the live docs root. <see cref="RouteRootPath"/> owns stable entry,
/// archive, and exact-version routes. <see cref="DocsRootPath"/> owns the live source-backed surface used for current
/// docs, search, and the current search-index payload. For a custom versioned host, use values such as
/// <c>RouteRootPath=/foo/bar</c> and <c>DocsRootPath=/foo/bar/next</c>. Both values are normalized into app-relative
/// paths during <c>AddRazorDocs()</c> post-configuration.
/// </remarks>
public sealed class RazorDocsRoutingOptions
{
    /// <summary>
    /// Gets or sets the app-relative route-family root for this RazorDocs instance.
    /// </summary>
    /// <remarks>
    /// The route root is the parent for stable entry, archive, and exact-version routes. When omitted, it defaults to the
    /// live docs root with versioning disabled and to <c>/docs</c> with versioning enabled. Relative-looking values are
    /// normalized into app-relative paths. For example, <c>foo/bar</c> becomes <c>/foo/bar</c>. Use <c>/</c> for a
    /// single-purpose root-mounted docs host. The normalized path must not end with <c>/</c>, include query or fragment
    /// segments, or target reserved child routes such as <c>/foo/bar/versions</c> or <c>/foo/bar/v</c>.
    /// </remarks>
    public string? RouteRootPath { get; set; }

    /// <summary>
    /// Gets or sets the app-relative root path for the live source-backed docs surface.
    /// </summary>
    /// <remarks>
    /// The live root serves current source-backed docs, search, and the current search index. Relative-looking values
    /// are normalized into app-relative paths. For example, <c>foo/bar/next</c> becomes <c>/foo/bar/next</c>. When
    /// versioning is disabled the default path is the route root. When versioning is enabled the default path is
    /// <c>{RouteRootPath}/next</c>, or <c>/docs/next</c> for the default route family. The live root must not collide
    /// with the route-family root or its reserved archive/exact-version children when versioning is enabled.
    /// </remarks>
    public string? DocsRootPath { get; set; }
}

/// <summary>
/// Versioning settings for published RazorDocs release trees.
/// </summary>
/// <remarks>
/// Enabling versioning turns on the published-release route contract:
/// <see cref="RazorDocsRoutingOptions.RouteRootPath"/> for the recommended release alias,
/// <c>{RouteRootPath}/v/{version}</c> for immutable exact trees, <c>{RouteRootPath}/versions</c> for the archive, and
/// a live preview surface rooted at <see cref="RazorDocsRoutingOptions.DocsRootPath"/>.
/// The catalog stays file-based in this slice: runtime consumes a JSON manifest plus prebuilt exact release trees and
/// does not perform Git or bundle resolution at request time. The catalog must describe the recommended version
/// alias plus one or more exact release trees whose exported contents satisfy the exact-tree contract documented in
/// the package README.
/// </remarks>
public sealed class RazorDocsVersioningOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether release-tree versioning is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the path to the version catalog JSON file.
    /// </summary>
    /// <remarks>
    /// This property is required when <see cref="Enabled"/> is <see langword="true"/>.
    /// The catalog describes available exact-version trees, the recommended version alias, and release-level status
    /// metadata such as support and advisory state. Relative paths resolve from the app content root.
    /// The JSON payload is expected to contain a top-level recommended version plus a <c>versions</c> array whose
    /// entries point at exported exact-version trees.
    /// A missing, unreadable, or malformed catalog does not crash RazorDocs, but it leaves all published releases
    /// unavailable until the catalog can be loaded successfully. When <see cref="Enabled"/> is <see langword="true"/>
    /// and this property is blank, startup validation fails before the app begins serving requests.
    /// </remarks>
    public string? CatalogPath { get; set; }
}

/// <summary>
/// Validates <see cref="RazorDocsOptions"/> and rejects unsupported or ambiguous startup configurations.
/// </summary>
public sealed class RazorDocsOptionsValidator : IValidateOptions<RazorDocsOptions>
{
    private static readonly Regex SymbolSourceTemplateTokenRegex = new(
        @"\{[^}]+\}",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RazorDocsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        var source = options.Source;
        var bundle = options.Bundle;
        var sidebar = options.Sidebar;
        var contributor = options.Contributor;
        var routing = options.Routing;
        var versioning = options.Versioning;
        string? normalizedRouteRootPath = null;
        string? normalizedDocsRootPath = null;

        if (!Enum.IsDefined(options.Mode))
        {
            failures.Add($"Unsupported RazorDocs mode '{options.Mode}'.");
        }

        if (source is null)
        {
            failures.Add("RazorDocs:Source must not be null.");
        }

        if (bundle is null)
        {
            failures.Add("RazorDocs:Bundle must not be null.");
        }

        if (sidebar is null)
        {
            failures.Add("RazorDocs:Sidebar must not be null.");
        }
        else if (sidebar.NamespacePrefixes is null)
        {
            failures.Add("RazorDocs:Sidebar:NamespacePrefixes must not be null.");
        }

        if (contributor is null)
        {
            failures.Add("RazorDocs:Contributor must not be null.");
        }
        else if (!Enum.IsDefined(contributor.LastUpdatedMode))
        {
            failures.Add($"Unsupported RazorDocs contributor last-updated mode '{contributor.LastUpdatedMode}'.");
        }

        if (routing is null)
        {
            failures.Add("RazorDocs:Routing must not be null.");
        }
        else
        {
            var routeRootPathIsValid = true;
            var docsRootPathIsValid = true;

            if (routing.RouteRootPath is not null)
            {
                if (!IsValidAppRelativeRootPath(routing.RouteRootPath))
                {
                    failures.Add(
                        "RazorDocs:Routing:RouteRootPath must be an app-relative path such as '/docs' or '/foo/bar'. It must not end with '/', include a query or fragment, or use an absolute URL.");
                    routeRootPathIsValid = false;
                }
                else if (IsReservedRouteFamilyChildPath(routing.RouteRootPath))
                {
                    failures.Add(
                        "RazorDocs:Routing:RouteRootPath cannot target a reserved archive or exact-version child route such as '/foo/bar/versions' or '/foo/bar/v'. Use the parent route root, for example '/foo/bar'.");
                    routeRootPathIsValid = false;
                }
            }

            if (routing.DocsRootPath is not null && !IsValidAppRelativeRootPath(routing.DocsRootPath))
            {
                failures.Add(
                    "RazorDocs:Routing:DocsRootPath must be an app-relative path such as '/docs/next' or '/foo/bar/next'. It must not end with '/', include a query or fragment, or use an absolute URL.");
                docsRootPathIsValid = false;
            }

            if (routeRootPathIsValid && docsRootPathIsValid)
            {
                normalizedDocsRootPath = DocsUrlBuilder.NormalizeDocsRootPath(
                    routing.DocsRootPath,
                    versioning?.Enabled == true);
                normalizedRouteRootPath = DocsUrlBuilder.NormalizeRouteRootPath(
                    routing.RouteRootPath,
                    normalizedDocsRootPath,
                    versioning?.Enabled == true);
            }
        }

        if (versioning is null)
        {
            failures.Add("RazorDocs:Versioning must not be null.");
        }

        if (options.Mode == RazorDocsMode.Bundle)
        {
            if (bundle is null || string.IsNullOrWhiteSpace(bundle.Path))
            {
                failures.Add("RazorDocs bundle mode requires RazorDocs:Bundle:Path.");
            }

            failures.Add("RazorDocs bundle mode is not implemented yet. Use RazorDocs:Mode=Source for Slice 1.");
        }

        if (options.Mode == RazorDocsMode.Source
            && source?.RepositoryRoot is not null
            && string.IsNullOrWhiteSpace(source.RepositoryRoot))
        {
            failures.Add("RazorDocs:Source:RepositoryRoot cannot be whitespace.");
        }

        if (versioning?.Enabled == true)
        {
            if (string.IsNullOrWhiteSpace(versioning.CatalogPath))
            {
                failures.Add("RazorDocs versioning requires RazorDocs:Versioning:CatalogPath.");
            }

            if (normalizedRouteRootPath is not null
                && string.Equals(normalizedDocsRootPath, normalizedRouteRootPath, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    "RazorDocs versioning cannot use the route-family root as the live source docs root. The live preview would collide with stable entry, archive, and exact-version routes. Use '/next' for root-mounted docs or a child such as '/foo/bar/next'.");
            }

            if (normalizedRouteRootPath is not null
                && normalizedDocsRootPath is not null
                && IsReservedRouteFamilyChildPath(normalizedDocsRootPath, normalizedRouteRootPath))
            {
                failures.Add(
                    "RazorDocs:Routing:DocsRootPath cannot use a reserved archive or exact-version child of the same route root, such as '/foo/bar/versions' or '/foo/bar/v'. Use a live preview child such as '/foo/bar/next'.");
            }
        }

        if (contributor is not null
            && contributor.Enabled
            && (!string.IsNullOrWhiteSpace(contributor.SourceUrlTemplate)
                || !string.IsNullOrWhiteSpace(contributor.EditUrlTemplate))
            && string.IsNullOrWhiteSpace(contributor.DefaultBranch))
        {
            failures.Add("RazorDocs:Contributor:DefaultBranch is required when SourceUrlTemplate or EditUrlTemplate is configured.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SourceUrlTemplate)
            && contributor.SourceUrlTemplate.Contains("{path}", StringComparison.Ordinal) is false)
        {
            failures.Add("RazorDocs:Contributor:SourceUrlTemplate must contain the {path} token.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.EditUrlTemplate)
            && contributor.EditUrlTemplate.Contains("{path}", StringComparison.Ordinal) is false)
        {
            failures.Add("RazorDocs:Contributor:EditUrlTemplate must contain the {path} token.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SymbolSourceUrlTemplate)
            && contributor.SymbolSourceUrlTemplate.Contains("{path}", StringComparison.Ordinal) is false)
        {
            failures.Add("RazorDocs:Contributor:SymbolSourceUrlTemplate must contain the {path} token.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SymbolSourceUrlTemplate)
            && contributor.SymbolSourceUrlTemplate.Contains("{line}", StringComparison.Ordinal) is false)
        {
            failures.Add("RazorDocs:Contributor:SymbolSourceUrlTemplate must contain the {line} token.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SymbolSourceUrlTemplate))
        {
            var unsupportedSymbolSourceTokens = SymbolSourceTemplateTokenRegex
                .Matches(contributor.SymbolSourceUrlTemplate)
                .Select(match => match.Value)
                .Where(token => token is not "{path}" and not "{line}" and not "{branch}" and not "{ref}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (unsupportedSymbolSourceTokens.Length > 0)
            {
                failures.Add(
                    $"RazorDocs:Contributor:SymbolSourceUrlTemplate contains unsupported token(s): {string.Join(", ", unsupportedSymbolSourceTokens)}.");
            }
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SymbolSourceUrlTemplate)
            && contributor.SymbolSourceUrlTemplate.Contains("{branch}", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(contributor.DefaultBranch))
        {
            failures.Add("RazorDocs:Contributor:DefaultBranch is required when SymbolSourceUrlTemplate contains the {branch} token.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SymbolSourceUrlTemplate)
            && contributor.SymbolSourceUrlTemplate.Contains("{ref}", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(contributor.SourceRef)
            && string.IsNullOrWhiteSpace(contributor.DefaultBranch))
        {
            failures.Add("RazorDocs:Contributor:SourceRef or DefaultBranch is required when SymbolSourceUrlTemplate contains the {ref} token.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsValidAppRelativeRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Length > 1 && path[^1] == '/')
        {
            return false;
        }

        return path.IndexOfAny(['?', '#']) < 0;
    }

    private static bool IsReservedRouteFamilyChildPath(string path)
    {
        return path.EndsWith("/versions", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/v", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReservedRouteFamilyChildPath(string docsRootPath, string routeRootPath)
    {
        var versionsRoot = DocsUrlBuilder.JoinPath(routeRootPath, "versions");
        var versionPrefix = DocsUrlBuilder.JoinPath(routeRootPath, "v");
        return string.Equals(docsRootPath, versionsRoot, StringComparison.OrdinalIgnoreCase)
               || string.Equals(docsRootPath, versionPrefix, StringComparison.OrdinalIgnoreCase)
               || docsRootPath.StartsWith(versionPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }
}
