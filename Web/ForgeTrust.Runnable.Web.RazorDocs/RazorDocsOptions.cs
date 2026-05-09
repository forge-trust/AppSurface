using System.Text.RegularExpressions;
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
    /// Gets the default value, in minutes, for <see cref="CacheExpirationMinutes"/>.
    /// </summary>
    public const double DefaultCacheExpirationMinutes = 5;

    /// <summary>
    /// Gets the smallest supported value, in minutes, for <see cref="CacheExpirationMinutes"/>.
    /// </summary>
    /// <remarks>
    /// The limit keeps the configured duration representable by the derived search-index <c>Cache-Control</c>
    /// <c>max-age</c> header, which uses whole seconds.
    /// </remarks>
    public const double MinCacheExpirationMinutes = 1d / 60d;

    /// <summary>
    /// Gets the largest supported value, in minutes, for <see cref="CacheExpirationMinutes"/>.
    /// </summary>
    /// <remarks>
    /// The limit keeps the derived search-index <c>Cache-Control</c> <c>max-age</c> within the range of a 32-bit
    /// positive delta-seconds value.
    /// </remarks>
    public const double MaxCacheExpirationMinutes = (int.MaxValue - 1) / 60d;

    /// <summary>
    /// Gets or sets the active docs source mode.
    /// </summary>
    public RazorDocsMode Mode { get; set; } = RazorDocsMode.Source;

    /// <summary>
    /// Gets or sets the absolute docs snapshot cache lifetime in minutes.
    /// The default is <see cref="DefaultCacheExpirationMinutes"/> minutes.
    /// </summary>
    /// <remarks>
    /// This setting controls the shared aggregation snapshot used by docs pages, public-section data, and the generated
    /// search-index payload. Shorter values make source-backed development changes visible sooner, while longer values
    /// reduce repeated harvest and search-index generation work in production hosts. The value must be finite, must be
    /// between <see cref="MinCacheExpirationMinutes"/> and <see cref="MaxCacheExpirationMinutes"/>, and must represent
    /// a whole number of seconds because the generated search-index <c>Cache-Control</c> <c>max-age</c> header uses
    /// whole-second delta values. Values outside those constraints are rejected during options validation.
    /// </remarks>
    public double CacheExpirationMinutes { get; set; } = DefaultCacheExpirationMinutes;

    /// <summary>
    /// Gets source-mode settings used when docs are harvested from a repository checkout.
    /// </summary>
    public RazorDocsSourceOptions Source { get; set; } = new();

    /// <summary>
    /// Gets harvest policy settings used by runtime and export hosts.
    /// </summary>
    public RazorDocsHarvestOptions Harvest { get; set; } = new();

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
/// Harvest policy settings for RazorDocs source-backed documentation.
/// </summary>
/// <remarks>
/// The default policy is tolerant so public runtime hosts can continue serving even when source harvesting has a
/// transient problem. Enable <see cref="FailOnFailure"/> in CI or export hosts that should fail closed when every
/// configured harvester fails, times out, or cancels.
/// </remarks>
public sealed class RazorDocsHarvestOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether host startup should fail when the aggregate harvest health is
    /// <see cref="ForgeTrust.Runnable.Web.RazorDocs.Models.DocHarvestHealthStatus.Failed"/>.
    /// </summary>
    /// <remarks>
    /// Strict mode treats only the aggregate failed state as fatal. Empty docs and degraded partial harvests remain
    /// non-fatal in this slice because they can represent intentional empty repositories or still-usable partial docs.
    /// </remarks>
    public bool FailOnFailure { get; set; }
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
/// Routing settings for the live RazorDocs source surface.
/// </summary>
/// <remarks>
/// The live source-backed surface always stays under the <c>/docs</c> URL family. RazorDocs normalizes configured
/// values into app-relative paths before validation, so hosts may provide <c>docs/preview</c> and still get the
/// canonical <c>/docs/preview</c> route contract. The one supported exception is <c>/</c>, which mounts the live
/// docs surface at the application root for single-purpose docs hosts. When versioning is off the live surface
/// defaults to <c>/docs</c>. When versioning is on the live surface defaults to <c>/docs/next</c> so the stable
/// <c>/docs</c> alias can point at the recommended published release instead. These defaults are applied by
/// <c>AddRazorDocs()</c> during options binding, so callers can omit <see cref="DocsRootPath"/> when the standard
/// route contract is acceptable.
/// </remarks>
public sealed class RazorDocsRoutingOptions
{
    /// <summary>
    /// Gets or sets the app-relative root path for the live source-backed docs surface.
    /// </summary>
    /// <remarks>
    /// Relative-looking values are normalized into app-relative paths. For example, <c>docs/live</c> becomes
    /// <c>/docs/live</c> during options binding. The normalized path must either be exactly <c>/</c> for a
    /// root-mounted docs host or start with <c>/docs</c>; it must not end with <c>/</c> and cannot include query or
    /// fragment segments.
    /// When versioning is disabled the default path is <c>/docs</c>. When versioning is enabled the default path
    /// becomes <c>/docs/next</c> so the current unreleased snapshot does not collide with the recommended released
    /// docs alias at <c>/docs</c>.
    /// Avoid reserved versioning paths such as <c>/docs</c>, <c>/docs/versions</c>, <c>/docs/v</c>, and any
    /// <c>/docs/v/{version}</c> route when versioning is enabled unless you intentionally use the root-mounted
    /// <c>/</c> special case. Hosts that customize this root should configure it before any generated links or
    /// exported trees are produced so server-rendered pages, search assets, and static export output all agree on the
    /// same live preview root.
    /// </remarks>
    public string? DocsRootPath { get; set; }
}

/// <summary>
/// Versioning settings for published RazorDocs release trees.
/// </summary>
/// <remarks>
/// Enabling versioning turns on the published-release route contract:
/// <c>/docs</c> for the recommended release alias, <c>/docs/v/{version}</c> for immutable exact trees,
/// <c>/docs/versions</c> for the archive, and a live preview surface rooted at <see cref="RazorDocsRoutingOptions.DocsRootPath"/>.
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
        var harvest = options.Harvest;
        var bundle = options.Bundle;
        var sidebar = options.Sidebar;
        var contributor = options.Contributor;
        var routing = options.Routing;
        var versioning = options.Versioning;

        if (!Enum.IsDefined(options.Mode))
        {
            failures.Add($"Unsupported RazorDocs mode '{options.Mode}'.");
        }

        if (!IsValidCacheExpirationMinutes(options.CacheExpirationMinutes))
        {
            failures.Add(
                $"RazorDocs:CacheExpirationMinutes must be a finite number between {RazorDocsOptions.MinCacheExpirationMinutes} and {RazorDocsOptions.MaxCacheExpirationMinutes} minutes that maps to a whole number of seconds.");
        }

        if (source is null)
        {
            failures.Add("RazorDocs:Source must not be null.");
        }

        if (harvest is null)
        {
            failures.Add("RazorDocs:Harvest must not be null.");
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

        if (routing?.DocsRootPath is null)
        {
            failures.Add("RazorDocs:Routing:DocsRootPath must not be null.");
        }
        else if (!IsValidDocsRootPath(routing.DocsRootPath))
        {
            failures.Add(
                "RazorDocs:Routing:DocsRootPath must be exactly '/' or start with '/docs', must not end with '/', and cannot contain query or fragment segments.");
        }

        if (versioning?.Enabled == true)
        {
            if (string.IsNullOrWhiteSpace(versioning.CatalogPath))
            {
                failures.Add("RazorDocs versioning requires RazorDocs:Versioning:CatalogPath.");
            }

            if (string.Equals(routing?.DocsRootPath, "/docs", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    "RazorDocs versioning cannot use '/docs' as the live source docs root. Use '/docs/next' or another '/docs/*' preview path.");
            }

            if (routing?.DocsRootPath is not null
                && IsReservedVersioningPath(routing.DocsRootPath))
            {
                failures.Add(
                    "RazorDocs:Routing:DocsRootPath cannot use a reserved versioning path such as '/docs/versions', '/docs/v', or '/docs/v/...'.");
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

    internal static bool IsValidCacheExpirationMinutes(double cacheExpirationMinutes)
    {
        if (!double.IsFinite(cacheExpirationMinutes)
            || cacheExpirationMinutes < RazorDocsOptions.MinCacheExpirationMinutes
            || cacheExpirationMinutes > RazorDocsOptions.MaxCacheExpirationMinutes)
        {
            return false;
        }

        var cacheDuration = TimeSpan.FromMinutes(cacheExpirationMinutes);
        return cacheDuration.Ticks % TimeSpan.TicksPerSecond == 0;
    }

    private static bool IsValidDocsRootPath(string docsRootPath)
    {
        if (string.IsNullOrWhiteSpace(docsRootPath))
        {
            return false;
        }

        if (!string.Equals(docsRootPath, "/", StringComparison.Ordinal)
            && !string.Equals(docsRootPath, "/docs", StringComparison.OrdinalIgnoreCase)
            && !docsRootPath.StartsWith("/docs/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (docsRootPath.Length > 1 && docsRootPath[^1] == '/')
        {
            return false;
        }

        return docsRootPath.IndexOfAny(['?', '#']) < 0;
    }

    private static bool IsReservedVersioningPath(string docsRootPath)
    {
        return string.Equals(docsRootPath, "/docs/v", StringComparison.OrdinalIgnoreCase)
               || docsRootPath.StartsWith("/docs/v/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(docsRootPath, "/docs/versions", StringComparison.OrdinalIgnoreCase);
    }
}
