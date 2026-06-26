using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using ForgeTrust.AppSurface.Docs.Services;
using Microsoft.Extensions.Options;

namespace ForgeTrust.AppSurface.Docs;

/// <summary>
/// Represents configuration for the AppSurface Docs package and host.
/// </summary>
public sealed class AppSurfaceDocsOptions
{
    /// <summary>
    /// Gets the root configuration section name for AppSurface Docs.
    /// </summary>
    public const string SectionName = "AppSurfaceDocs";

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
    public AppSurfaceDocsMode Mode { get; set; } = AppSurfaceDocsMode.Source;

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
    /// Gets identity settings used by the built-in docs chrome.
    /// </summary>
    public AppSurfaceDocsIdentityOptions Identity { get; set; } = new();

    /// <summary>
    /// Gets source-mode settings used when docs are harvested from a repository checkout.
    /// </summary>
    public AppSurfaceDocsSourceOptions Source { get; set; } = new();

    /// <summary>
    /// Gets harvest policy settings used by runtime and export hosts.
    /// </summary>
    public AppSurfaceDocsHarvestOptions Harvest { get; set; } = new();

    /// <summary>
    /// Gets diagnostics settings for maintainer-facing AppSurface Docs inspection surfaces.
    /// </summary>
    public AppSurfaceDocsDiagnosticsOptions Diagnostics { get; set; } = new();

    /// <summary>
    /// Gets search-quality metrics settings for AppSurface Docs.
    /// </summary>
    /// <remarks>
    /// Metrics are disabled by default. Enabling this block controls only AppSurface Docs search-quality events and does
    /// not enable unrelated AppSurface or RazorWire experimental product-intelligence contracts. Use the browser
    /// collector for static exports and live hosted docs that should forward safe registry-shaped events to a configured
    /// endpoint. Use hosted collection and hosted review when a running AppSurface Docs server should validate browser
    /// submissions, forward accepted events to host-owned sinks, and expose a bounded maintainer diagnostics surface.
    /// </remarks>
    public AppSurfaceDocsMetricsOptions Metrics { get; set; } = new();

    /// <summary>
    /// Gets bundle-mode settings used by future bundle-backed runtime loading.
    /// </summary>
    public AppSurfaceDocsBundleOptions Bundle { get; set; } = new();

    /// <summary>
    /// Gets sidebar rendering settings.
    /// </summary>
    public AppSurfaceDocsSidebarOptions Sidebar { get; set; } = new();

    /// <summary>
    /// Gets contributor provenance settings used to render source, edit, and freshness evidence on details pages.
    /// </summary>
    public AppSurfaceDocsContributorOptions Contributor { get; set; } = new();

    /// <summary>
    /// Gets routing settings that control where the live AppSurface Docs source surface is exposed.
    /// </summary>
    public AppSurfaceDocsRoutingOptions Routing { get; set; } = new();

    /// <summary>
    /// Gets versioning settings used to mount exact release trees and the archive surface.
    /// </summary>
    public AppSurfaceDocsVersioningOptions Versioning { get; set; } = new();

    /// <summary>
    /// Gets localization settings for locale-aware live docs routes, metadata, search projections, and fallback policy.
    /// </summary>
    /// <remarks>
    /// Localization is disabled by default so existing AppSurface Docs hosts keep their current routes, search payload shape,
    /// and view behavior until they opt in. When enabled, this object defines the supported locales, the default locale,
    /// route-prefix behavior, fallback policy, and search scoping defaults used by the locale-aware document graph.
    /// </remarks>
    public AppSurfaceDocsLocalizationOptions Localization { get; set; } = new();
}

/// <summary>
/// Identity configuration for AppSurface Docs browser chrome.
/// </summary>
/// <remarks>
/// Identity settings intentionally cover the low-level brand assets every consuming repository expects to own:
/// display name, logo, favicon variants, optional branding-asset directory serving, and the brand home link. Theme,
/// layout, color, typography, and template customization are separate future work so identity remains safe to configure
/// from appsettings, config_*.json, command-line arguments, and environment variables.
/// </remarks>
public sealed class AppSurfaceDocsIdentityOptions
{
    /// <summary>
    /// Gets the default display name used when <see cref="DisplayName"/> is omitted or blank.
    /// </summary>
    public const string DefaultDisplayName = "Documentation";

    /// <summary>
    /// Gets or sets the visible product name rendered in titles and navigation chrome.
    /// </summary>
    /// <remarks>
    /// Blank values fall back to <see cref="DefaultDisplayName"/>. The value is plain text and is HTML-encoded by Razor
    /// views; do not put markup here.
    /// </remarks>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the brand link target used by the built-in docs chrome.
    /// </summary>
    /// <remarks>
    /// When omitted or blank, AppSurface Docs links the brand to the configured docs home route. Configured values must
    /// be app-root browser paths such as <c>/docs</c> or application-relative paths such as <c>~/docs</c>. Remote URLs,
    /// relative paths, query strings, fragments, and protocol-relative URLs are rejected during startup validation.
    /// </remarks>
    public string? HomeHref { get; set; }

    /// <summary>
    /// Gets logo settings used by the built-in navigation chrome.
    /// </summary>
    public AppSurfaceDocsLogoOptions Logo { get; set; } = new();

    /// <summary>
    /// Gets wordmark presentation settings used by the built-in navigation chrome.
    /// </summary>
    public AppSurfaceDocsWordmarkOptions Wordmark { get; set; } = new();

    /// <summary>
    /// Gets favicon settings emitted into the document head.
    /// </summary>
    public AppSurfaceDocsFaviconOptions Favicon { get; set; } = new();

    /// <summary>
    /// Gets optional static-file serving settings for consumer-owned branding assets.
    /// </summary>
    public AppSurfaceDocsBrandingAssetsOptions BrandingAssets { get; set; } = new();
}

/// <summary>
/// Static-file serving settings for consumer-owned AppSurface Docs branding assets.
/// </summary>
/// <remarks>
/// <para>
/// These settings bridge the difference between filesystem asset ownership and browser paths. <see cref="DirectoryPath" />
/// points at a directory on disk that can live outside AppSurface Docs packaged static web assets, while
/// <see cref="RequestPath" /> defines the app-root browser prefix used by <see cref="AppSurfaceDocsLogoOptions.Path" />
/// and <see cref="AppSurfaceDocsFaviconOptions" /> paths.
/// </para>
/// <para>
/// Logo and favicon paths are browser URL paths, not filesystem paths, and are not joined with
/// <see cref="DirectoryPath" />. When <see cref="DirectoryPath" /> is <c>branding</c> and
/// <see cref="RequestPath" /> uses its default <c>/branding</c>, a file at <c>branding/logo.png</c> is referenced as
/// <c>/branding/logo.png</c>. SVG files use the same URL mapping only after <see cref="AllowSvgAssets" /> is enabled
/// for a trusted branding directory.
/// </para>
/// <para>
/// Relative directory paths resolve against <see cref="AppSurfaceDocsSourceOptions.RepositoryRoot" /> when configured;
/// otherwise they resolve against the host content root. Absolute directory paths are served as-is. The endpoint only
/// serves common web image and icon file extensions; keep this directory dedicated to public brand assets. SVG files
/// are denied by default because SVG is active document content; enable <see cref="AllowSvgAssets" /> only for
/// operator-owned assets that are already trusted. Leave <see cref="DirectoryPath" /> blank when the owning application
/// serves branding assets itself.
/// </para>
/// </remarks>
public sealed class AppSurfaceDocsBrandingAssetsOptions
{
    /// <summary>
    /// Gets the default request prefix for docs-served branding assets.
    /// </summary>
    public const string DefaultRequestPath = "/branding";

    /// <summary>
    /// Gets or sets the filesystem directory containing consumer-owned branding assets.
    /// </summary>
    /// <remarks>
    /// Values may be absolute paths, repository-relative paths when
    /// <see cref="AppSurfaceDocsSourceOptions.RepositoryRoot" /> is configured, or content-root-relative paths otherwise.
    /// The directory is not enabled until this value is non-blank. This is a server-side filesystem path; logo and
    /// favicon options still use browser paths under <see cref="RequestPath" />. The serving endpoint intentionally
    /// ignores files outside the supported image and icon extension set.
    /// </remarks>
    public string? DirectoryPath { get; set; }

    /// <summary>
    /// Gets or sets the app-root or application-relative URL prefix used to serve <see cref="DirectoryPath" />.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>/branding</c>. Values must follow the same browser-path rules as logo and favicon paths and must not
    /// be the application root because branding asset serving is intentionally scoped to a dedicated path prefix. Override
    /// this only when <c>/branding</c> conflicts with an owning application route.
    /// </remarks>
    public string? RequestPath { get; set; } = DefaultRequestPath;

    /// <summary>
    /// Gets or sets a value indicating whether AppSurface Docs may serve SVG files from <see cref="DirectoryPath"/>.
    /// </summary>
    /// <remarks>
    /// The default is <c>false</c>. SVG is an active document format, not only an optimized image format, and standard
    /// SVG optimization does not make arbitrary SVG safe to serve under the application origin. Enable this only for
    /// branding directories whose SVG files are owned and reviewed by trusted operators. Hosts that need SVG sanitization
    /// should perform that sanitization before files reach the configured branding directory.
    /// </remarks>
    public bool AllowSvgAssets { get; set; }
}

/// <summary>
/// Wordmark presentation settings for AppSurface Docs browser chrome.
/// </summary>
/// <remarks>
/// Wordmark settings are intentionally opt-in so the built-in docs chrome stays short, bounded, and plain text by
/// default. Use them when the publishing repository needs a specific product wordmark, shorter display treatment, or
/// substring highlight color. The highlighted text must appear in the resolved display name, and the color accepts only
/// CSS hex values so configuration cannot inject arbitrary style declarations into the package layout.
/// </remarks>
public sealed class AppSurfaceDocsWordmarkOptions
{
    /// <summary>
    /// Gets or sets the display-name substring highlighted by the built-in docs chrome.
    /// </summary>
    /// <remarks>
    /// Blank values disable wordmark highlighting. Non-blank values must match part of the resolved
    /// <see cref="AppSurfaceDocsIdentityOptions.DisplayName"/> using ordinal comparison. Only the first occurrence is
    /// highlighted; choose a more specific substring if the display name repeats the same word.
    /// </remarks>
    public string? HighlightText { get; set; }

    /// <summary>
    /// Gets or sets the CSS hex color used for the highlighted wordmark substring.
    /// </summary>
    /// <remarks>
    /// Valid values are CSS hex colors such as <c>#3b82f6</c> or <c>#38bdf8</c>. Blank values leave the highlighted
    /// substring in the surrounding text color. Supplying a color without <see cref="HighlightText"/> is rejected because
    /// it has no visible effect.
    /// </remarks>
    public string? HighlightColor { get; set; }
}

/// <summary>
/// Logo settings for AppSurface Docs browser chrome.
/// </summary>
public sealed class AppSurfaceDocsLogoOptions
{
    /// <summary>
    /// Gets or sets the app-root or application-relative logo path.
    /// </summary>
    /// <remarks>
    /// Valid values look like <c>/branding/docs-logo.svg</c> or <c>~/branding/docs-logo.svg</c>. This is always a
    /// browser URL path. When <see cref="AppSurfaceDocsBrandingAssetsOptions.DirectoryPath" /> is configured, reference
    /// files through the branding request prefix, for example <c>/branding/docs-logo.svg</c> for
    /// <c>branding/docs-logo.svg</c>. Remote URLs, relative paths, query strings, fragments, backslashes, and traversal
    /// segments are rejected.
    /// </remarks>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets accessible text for logo-only renderers that consume the resolved identity.
    /// </summary>
    /// <remarks>
    /// When omitted or blank, the resolved display name is used. The built-in AppSurface Docs chrome renders configured
    /// logo images as decorative because the visible display name is rendered in the same brand link.
    /// </remarks>
    public string? AltText { get; set; }
}

/// <summary>
/// Favicon settings for AppSurface Docs.
/// </summary>
/// <remarks>
/// When no custom favicon path is configured, the built-in layout links the packaged AppSurface Docs document-layers SVG
/// mark. Standalone AppSurface Docs hosts also serve that same SVG mark at <c>/favicon.ico</c> for browsers that request
/// the conventional root favicon URL before reading page metadata. When <see cref="SvgPath" /> is configured, standalone
/// hosts redirect <c>/favicon.ico</c> to that SVG path so the conventional probe matches the rendered metadata. Embedded
/// hosts do not claim <c>/favicon.ico</c>; their owning app should serve any app-wide favicon itself.
/// </remarks>
public sealed class AppSurfaceDocsFaviconOptions
{
    /// <summary>
    /// Gets or sets an SVG favicon path. Values must be browser-safe app-root paths, such as
    /// <c>/docs/favicon.svg</c>, or application-relative paths, such as <c>~/docs/favicon.svg</c>.
    /// </summary>
    /// <remarks>
    /// Remote URLs, protocol-relative URLs, query strings, fragments, backslashes, and traversal segments are rejected
    /// during options validation. Null or blank values omit the custom SVG favicon; the layout renders the built-in
    /// AppSurface Docs SVG favicon only when no custom favicon path is configured.
    /// </remarks>
    public string? SvgPath { get; set; }

    /// <summary>
    /// Gets or sets an ICO favicon path. Values must be browser-safe app-root paths, such as
    /// <c>/favicon.ico</c>, or application-relative paths, such as <c>~/favicon.ico</c>.
    /// </summary>
    /// <remarks>
    /// Remote URLs, protocol-relative URLs, query strings, fragments, backslashes, and traversal segments are rejected
    /// during options validation. Null or blank values omit the custom ICO favicon; the layout renders the built-in
    /// AppSurface Docs SVG favicon only when no custom favicon path is configured.
    /// </remarks>
    public string? IcoPath { get; set; }

    /// <summary>
    /// Gets or sets a PNG favicon path. Values must be browser-safe app-root paths, such as
    /// <c>/docs/favicon.png</c>, or application-relative paths, such as <c>~/docs/favicon.png</c>.
    /// </summary>
    /// <remarks>
    /// Remote URLs, protocol-relative URLs, query strings, fragments, backslashes, and traversal segments are rejected
    /// during options validation. Null or blank values omit the custom PNG favicon; the layout renders the built-in
    /// AppSurface Docs SVG favicon only when no custom favicon path is configured.
    /// </remarks>
    public string? PngPath { get; set; }
}

/// <summary>
/// Enumerates the supported AppSurface Docs content source modes.
/// </summary>
/// <remarks>
/// Numeric values are part of the public configuration and serialization contract. Do not reorder or renumber existing
/// members; changing these assignments can break persisted configuration, serialized payloads, and consumers. Add new
/// modes by appending members with new explicit values.
/// </remarks>
public enum AppSurfaceDocsMode
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
/// Source-mode configuration for AppSurface Docs.
/// </summary>
public sealed class AppSurfaceDocsSourceOptions
{
    /// <summary>
    /// Gets or sets the repository root used for source harvesting.
    /// When null, AppSurface Docs falls back to repository discovery from the content root.
    /// </summary>
    public string? RepositoryRoot { get; set; }
}

/// <summary>
/// Harvest policy settings for AppSurface Docs source-backed documentation.
/// </summary>
/// <remarks>
/// The default policy is tolerant so public runtime hosts can continue serving even when source harvesting has a
/// transient problem. Enable <see cref="FailOnFailure"/> in CI or export hosts that should fail closed when every
/// active harvester fails, times out, or cancels. Use <see cref="Paths"/>, <see cref="Markdown"/>, and
/// <see cref="CSharp"/> to define the repository-relative public documentation boundary shared by runtime hosts,
/// export flows, and hygiene checks. JavaScript discovery is enabled by default and remains annotation-first; use
/// <see cref="JavaScript"/> for JavaScript opt-out, narrowing, and strict-health behavior.
/// </remarks>
public sealed class AppSurfaceDocsHarvestOptions
{
    /// <summary>
    /// Gets the default amount of time a first docs page request waits for the initial harvest before rendering the live
    /// harvest observatory.
    /// </summary>
    public const int DefaultInitialRequestWaitBudgetMilliseconds = 350;

    /// <summary>
    /// Gets or sets a value indicating whether host startup should fail when the aggregate harvest health is
    /// <see cref="ForgeTrust.AppSurface.Docs.Models.DocHarvestHealthStatus.Failed"/>.
    /// </summary>
    /// <remarks>
    /// Strict mode treats only the aggregate failed state as fatal. Empty docs and degraded partial harvests remain
    /// non-fatal in this slice because they can represent intentional empty repositories or still-usable partial docs.
    /// </remarks>
    public bool FailOnFailure { get; set; }

    /// <summary>
    /// Gets or sets how AppSurface Docs starts the initial source harvest.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="AppSurfaceDocsHarvestStartupMode.Background"/>, which starts the same memoized harvest
    /// during host startup and lets first requests render live progress if the snapshot is not ready within
    /// <see cref="InitialRequestWaitBudgetMilliseconds"/>. <see cref="FailOnFailure"/> still makes startup wait for the
    /// snapshot and fail closed when the aggregate status is failed.
    /// </remarks>
    public AppSurfaceDocsHarvestStartupMode StartupMode { get; set; } = AppSurfaceDocsHarvestStartupMode.Background;

    /// <summary>
    /// Gets or sets the first-request wait budget, in milliseconds, before AppSurface Docs renders the harvest observatory.
    /// </summary>
    /// <remarks>
    /// The budget applies only while the initial harvest is still running. Caller cancellation cancels the wait, not the
    /// shared memoized harvest. Use zero to render the observatory immediately for cold requests.
    /// </remarks>
    public int InitialRequestWaitBudgetMilliseconds { get; set; } = DefaultInitialRequestWaitBudgetMilliseconds;

    /// <summary>
    /// Gets or sets an artificial delay before active harvesters start, in milliseconds, for local and automated
    /// observatory testing.
    /// </summary>
    /// <remarks>
    /// The delay is inserted after AppSurface Docs publishes the live harvest run and before it invokes any active
    /// harvester. The default is zero. This option exists so development and test hosts can intentionally keep the
    /// harvest observatory in its startup phase; do not enable it in production traffic.
    /// </remarks>
    public int TestingPreHarvestDelayMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets an artificial delay per harvester, in milliseconds, for local and automated observatory testing.
    /// </summary>
    /// <remarks>
    /// The delay is inserted after a harvester publishes its running state and before that harvester starts real work. The
    /// default is zero. This option exists so development and test hosts can intentionally keep the harvest observatory
    /// visible; do not enable it in production traffic.
    /// </remarks>
    public int TestingDelayPerHarvesterMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets an artificial delay per harvested document, in milliseconds, for local and automated observatory
    /// testing.
    /// </summary>
    /// <remarks>
    /// The delay is inserted after a harvester returns documents and before AppSurface Docs marks that harvester complete.
    /// When live progress is available, the harvester's document count is published one document at a time. The default is
    /// zero. This option exists so development and test hosts can intentionally keep the harvest observatory visible; do
    /// not enable it in production traffic.
    /// </remarks>
    public int TestingDelayPerDocumentMilliseconds { get; set; }

    /// <summary>
    /// Gets health-surface settings for the operator-facing AppSurface Docs harvest health routes and sidebar chrome.
    /// </summary>
    public AppSurfaceDocsHarvestHealthOptions Health { get; set; } = new();

    /// <summary>
    /// Gets global repository-relative path policy settings shared by every built-in harvester.
    /// </summary>
    /// <remarks>
    /// Global include globs are a repository-wide boundary. When configured, every built-in source kind must match
    /// one global include before harvester-specific includes are considered. Global excludes win over includes and
    /// default-exclusion allows.
    /// </remarks>
    public AppSurfaceDocsHarvestPathOptions Paths { get; set; } = new();

    /// <summary>
    /// Gets Markdown-specific path policy settings that refine the global path policy.
    /// </summary>
    public AppSurfaceDocsMarkdownHarvestOptions Markdown { get; set; } = new();

    /// <summary>
    /// Gets C# API-reference path policy settings that refine the global path policy.
    /// </summary>
    public AppSurfaceDocsCSharpHarvestOptions CSharp { get; set; } = new();

    /// <summary>
    /// Gets JavaScript public API path policy and parser settings.
    /// </summary>
    public AppSurfaceDocsJavaScriptHarvestOptions JavaScript { get; set; } = new();
}

/// <summary>
/// Controls when AppSurface Docs starts its initial source-backed harvest.
/// </summary>
/// <remarks>
/// The numeric values are part of the options binding and serialization contract. Do not renumber or reorder existing
/// members; append new modes with explicit numeric values so persisted configuration remains stable.
/// </remarks>
public enum AppSurfaceDocsHarvestStartupMode
{
    /// <summary>
    /// Do not start the initial harvest during host startup; the first docs read starts it lazily.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Start the initial harvest during host startup without blocking startup unless strict failure mode is enabled.
    /// </summary>
    Background = 1,

    /// <summary>
    /// Start the initial harvest during host startup and wait for completion before startup continues.
    /// </summary>
    Blocking = 2
}

/// <summary>
/// Operator-facing harvest health settings for AppSurface Docs.
/// </summary>
/// <remarks>
/// The health surface exposes the same cached harvest state returned by
/// <see cref="ForgeTrust.AppSurface.Docs.Services.DocAggregator.GetHarvestHealthAsync(System.Threading.CancellationToken)"/>.
/// Development hosts show the route response and sidebar chrome by default so local failures are visible immediately.
/// Other environments must opt in explicitly before AppSurface Docs returns health responses or displays sidebar chrome.
/// </remarks>
public sealed class AppSurfaceDocsHarvestHealthOptions
{
    /// <summary>
    /// Gets or sets when AppSurface Docs should return health responses from <c>{DocsRootPath}/_health</c> and
    /// <c>{DocsRootPath}/_health.json</c>.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly"/>. AppSurface Docs always reserves these route
    /// patterns ahead of the docs catch-all route so they do not fall through to document lookup. Setting this to
    /// <see cref="AppSurfaceDocsHarvestHealthExposure.Always"/> allows the controller actions
    /// to return operator-facing responses in non-development hosts; protect those responses with host-owned
    /// authentication, authorization, or network controls when they are reachable by untrusted users.
    /// </remarks>
    public AppSurfaceDocsHarvestHealthExposure ExposeRoutes { get; set; } = AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly;

    /// <summary>
    /// Gets or sets when AppSurface Docs should show the harvest health entry in the built-in docs sidebar.
    /// </summary>
    /// <remarks>
    /// This option is intentionally independent from <see cref="ExposeRoutes"/> so hosts can expose a machine-readable
    /// endpoint without advertising it in the docs chrome, or show local-development chrome without changing
    /// non-development route behavior.
    /// </remarks>
    public AppSurfaceDocsHarvestHealthExposure ShowChrome { get; set; } = AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly;
}

/// <summary>
/// Enumerates environment-aware exposure policies for AppSurface Docs harvest health surfaces.
/// </summary>
/// <remarks>
/// Numeric values are part of the public configuration and serialization contract. Do not reorder or renumber existing
/// members; changing these assignments can break persisted configuration, serialized payloads, and consumers. Add new
/// modes by appending members with new explicit values.
/// </remarks>
public enum AppSurfaceDocsHarvestHealthExposure
{
    /// <summary>
    /// Expose the surface only when the host environment is Development.
    /// </summary>
    DevelopmentOnly = 0,

    /// <summary>
    /// Always expose the surface in every host environment.
    /// </summary>
    Always = 1,

    /// <summary>
    /// Never expose the surface.
    /// </summary>
    Never = 2
}

/// <summary>
/// Maintainer diagnostics settings for AppSurface Docs.
/// </summary>
/// <remarks>
/// Diagnostics surfaces expose route and harvest state intended for local development and trusted operators. The defaults
/// keep route-inspector responses and sidebar discovery available in Development only. Setting
/// <see cref="ExposeRouteInspector"/> or <see cref="ShowChrome"/> to
/// <see cref="AppSurfaceDocsHarvestHealthExposure.Always"/> does not add authentication or authorization; production
/// hosts must protect the route at the host, reverse proxy, or network layer when route identity is sensitive.
/// </remarks>
public sealed class AppSurfaceDocsDiagnosticsOptions
{
    /// <summary>
    /// Gets or sets when AppSurface Docs should return route-inspector responses from <c>{DocsRootPath}/_routes</c>
    /// and <c>{DocsRootPath}/_routes.json</c>.
    /// </summary>
    public AppSurfaceDocsHarvestHealthExposure ExposeRouteInspector { get; set; } = AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly;

    /// <summary>
    /// Gets or sets when AppSurface Docs should show route-inspector discovery in the built-in docs sidebar diagnostics
    /// chrome.
    /// </summary>
    /// <remarks>
    /// This option is intentionally independent from <see cref="ExposeRouteInspector"/> so hosts can expose route
    /// inspector responses for trusted automation without advertising them in docs chrome, or show diagnostics chrome
    /// only in environments where maintainers are expected to use the sidebar. Chrome never creates or authorizes a
    /// route; route exposure still follows <see cref="ExposeRouteInspector"/>.
    /// </remarks>
    public AppSurfaceDocsHarvestHealthExposure ShowChrome { get; set; } = AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly;

    /// <summary>
    /// Gets or sets the host-owned authorization policy required for mutating AppSurface Docs operator actions.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="null"/>, which denies packaged operator writes such as
    /// <c>{DocsRootPath}/_harvest/rebuild</c>. Set this to the name of a policy registered with ASP.NET Core
    /// authorization when trusted maintainers may rebuild the live source-backed docs harvest from browser forms or
    /// host automation. When this option is blank, AppSurface Docs falls back to <see cref="SearchIndexRefreshPolicy"/>
    /// for source compatibility with hosts that already configured the older search-index refresh endpoint.
    /// </remarks>
    public string? OperatorWritePolicy { get; set; }

    /// <summary>
    /// Gets or sets the host-owned authorization policy required to refresh the live docs search-index cache.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="null"/>, which denies the packaged
    /// <c>{DocsRootPath}/_search-index/refresh</c> endpoint. Set this to the name of a policy registered with
    /// ASP.NET Core authorization when a host wants browser-form-based operator refresh. New hosts should prefer
    /// <see cref="OperatorWritePolicy"/> so one docs-maintainer policy covers harvest rebuild and search refresh. AppSurface
    /// Docs does not register a permissive fallback policy because cache refresh is a mutating operator action.
    /// </remarks>
    public string? SearchIndexRefreshPolicy { get; set; }
}

/// <summary>
/// Search-quality metrics settings for AppSurface Docs.
/// </summary>
/// <remarks>
/// This options block is intentionally docs-specific. It never captures raw search text, full URLs, reader identity,
/// cookies, request bodies, or free-form comments. The browser collector forwards only the semantic event name and
/// low-cardinality string properties already emitted by AppSurface Docs search UI. Hosted collection revalidates those
/// events through the AppSurface product-event registry before any host sink or in-memory review model sees them.
/// </remarks>
public sealed class AppSurfaceDocsMetricsOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether AppSurface Docs metrics features may run.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. When disabled, the docs layout emits no browser collector endpoint,
    /// feedback controls stay hidden, hosted metrics collection returns not found, and hosted review returns not found.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets browser collector settings used by live and static docs pages.
    /// </summary>
    public AppSurfaceDocsBrowserMetricsCollectorOptions BrowserCollector { get; set; } = new();

    /// <summary>
    /// Gets hosted collection settings for the package-owned ingestion endpoint.
    /// </summary>
    public AppSurfaceDocsHostedMetricsCollectionOptions HostedCollection { get; set; } = new();

    /// <summary>
    /// Gets hosted review settings for maintainer-facing search quality diagnostics.
    /// </summary>
    public AppSurfaceDocsHostedMetricsReviewOptions HostedReview { get; set; } = new();
}

/// <summary>
/// Browser-side docs metrics collector settings.
/// </summary>
public sealed class AppSurfaceDocsBrowserMetricsCollectorOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the browser collector should forward safe docs metrics events.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. When enabled, <see cref="AppSurfaceDocsMetricsOptions.Enabled"/> must
    /// also be enabled. <see cref="EndpointUrl"/> may point at an app-root same-origin path such as
    /// <c>/docs/_metrics/collect</c> or an HTTPS absolute URL for static CDN deployments.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the browser collector endpoint.
    /// </summary>
    /// <remarks>
    /// Values may be same-origin app-root paths or HTTPS absolute URLs. Protocol-relative URLs, non-HTTP(S) schemes,
    /// query strings, fragments, embedded credentials, and secret-like values are rejected during options validation.
    /// When hosted collection is enabled and this value is blank, the layout uses the package-owned hosted collection
    /// endpoint for the current docs root.
    /// </remarks>
    public string? EndpointUrl { get; set; }
}

/// <summary>
/// Hosted docs metrics collection settings.
/// </summary>
public sealed class AppSurfaceDocsHostedMetricsCollectionOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the package-owned metrics collection endpoint is enabled.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>. The endpoint is a public low-trust browser submission boundary: it accepts
    /// narrow JSON event DTOs, validates through the product-event registry, updates only bounded in-memory review
    /// aggregates, forwards to host-owned product-intelligence sinks when configured, and never echoes submitted values.
    /// </remarks>
    public bool Enabled { get; set; }
}

/// <summary>
/// Hosted search-quality review settings.
/// </summary>
public sealed class AppSurfaceDocsHostedMetricsReviewOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the hosted search-quality diagnostics route is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets when AppSurface Docs should expose the hosted search-quality diagnostics route.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly"/>. Production hosts that set this
    /// to <see cref="AppSurfaceDocsHarvestHealthExposure.Always"/> must protect the route with host-owned access controls
    /// when the search-quality aggregate is sensitive.
    /// </remarks>
    public AppSurfaceDocsHarvestHealthExposure Exposure { get; set; } = AppSurfaceDocsHarvestHealthExposure.DevelopmentOnly;
}

/// <summary>
/// Bundle-mode configuration for AppSurface Docs.
/// </summary>
public sealed class AppSurfaceDocsBundleOptions
{
    /// <summary>
    /// Gets or sets the path to the docs bundle payload.
    /// </summary>
    public string? Path { get; set; }
}

/// <summary>
/// Sidebar presentation settings for AppSurface Docs.
/// </summary>
public sealed class AppSurfaceDocsSidebarOptions
{
    /// <summary>
    /// Gets or sets configured namespace prefixes for sidebar label simplification.
    /// </summary>
    public string[] NamespacePrefixes { get; set; } = [];
}

/// <summary>
/// Contributor-provenance configuration for AppSurface Docs details pages.
/// </summary>
/// <remarks>
/// This contract controls the global contributor-provenance surface. Use <see cref="Enabled"/> to switch the entire
/// feature on or off for a host, and use page-level contributor metadata to suppress or override individual pages
/// without mutating host-wide defaults.
/// </remarks>
public sealed class AppSurfaceDocsContributorOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether contributor provenance rendering is enabled for AppSurface Docs details pages.
    /// Disable this when the host should suppress all contributor affordances, even if page-level overrides or
    /// trustworthy source paths exist. When <see langword="false" />, AppSurface Docs also skips contributor-template startup
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
    /// The default is <see cref="AppSurfaceDocsLastUpdatedMode.None"/> so hosts opt into git-backed freshness explicitly
    /// instead of paying unexpected snapshot-time git costs.
    /// <see cref="AppSurfaceDocsLastUpdatedMode.Git"/> uses local repository history when a trustworthy source path exists and
    /// omits only freshness when git data is unavailable or untrustworthy.
    /// </summary>
    public AppSurfaceDocsLastUpdatedMode LastUpdatedMode { get; set; } = AppSurfaceDocsLastUpdatedMode.None;
}

/// <summary>
/// Enumerates the supported contributor freshness modes for AppSurface Docs details pages.
/// </summary>
/// <remarks>
/// Numeric values are part of the public configuration and serialization contract. Do not reorder or renumber existing
/// members; changing these assignments can break persisted configuration, serialized payloads, and consumers. Add new
/// modes by appending members with new explicit values.
/// </remarks>
public enum AppSurfaceDocsLastUpdatedMode
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
/// Routing settings for the AppSurface Docs route family and live source surface.
/// </summary>
/// <remarks>
/// AppSurface Docs separates the route-family root from the live docs root. <see cref="RouteRootPath"/> owns stable entry,
/// archive, and exact-version routes. <see cref="DocsRootPath"/> owns the live source-backed surface used for current
/// docs, search, and the current search-index payload. For a custom versioned host, use values such as
/// <c>RouteRootPath=/foo/bar</c> and <c>DocsRootPath=/foo/bar/next</c>. Both values are normalized into app-relative
/// paths during <c>AddAppSurfaceDocs()</c> post-configuration.
/// </remarks>
public sealed class AppSurfaceDocsRoutingOptions
{
    /// <summary>
    /// Gets or sets the app-relative route-family root for this AppSurface Docs instance.
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

    /// <summary>
    /// Gets or sets the public origin used when AppSurface Docs renders absolute canonical metadata.
    /// </summary>
    /// <remarks>
    /// This value is optional. When omitted, details pages keep app-relative canonical links so local preview and unknown
    /// deployment environments do not leak an inferred host name. When set, it must be an absolute <c>http</c> or
    /// <c>https</c> origin such as <c>https://docs.example.com</c>. Do not include <see cref="RouteRootPath"/>,
    /// <see cref="DocsRootPath"/>, a query string, or a fragment; AppSurface Docs joins the origin to the canonical route
    /// it already knows for the current page.
    /// </remarks>
    public string? PublicOrigin { get; set; }
}

/// <summary>
/// Versioning settings for published AppSurface Docs release trees.
/// </summary>
/// <remarks>
/// Enabling versioning turns on the published-release route contract:
/// <see cref="AppSurfaceDocsRoutingOptions.RouteRootPath"/> for the recommended release alias,
/// <c>{RouteRootPath}/v/{version}</c> for immutable exact trees, <c>{RouteRootPath}/versions</c> for the archive, and
/// a live preview surface rooted at <see cref="AppSurfaceDocsRoutingOptions.DocsRootPath"/>.
/// The catalog stays file-based in this slice: runtime consumes a JSON manifest plus prebuilt exact release trees and
/// does not perform Git or bundle resolution at request time. The catalog must describe the recommended version
/// alias plus one or more exact release trees whose exported contents satisfy the exact-tree contract documented in
/// the package README. Public exact release trees must pin <c>releaseManifestSha256</c> in the catalog so runtime
/// verification can prove local archive integrity before serving mounted archive HTML, scripts, stylesheets, SVG, and
/// search payloads.
/// </remarks>
public sealed class AppSurfaceDocsVersioningOptions
{
    /// <summary>
    /// Gets the default maximum rewritten published-tree input size in bytes.
    /// </summary>
    public const long DefaultMaxRewrittenFileSizeBytes = 2L * 1024L * 1024L;

    /// <summary>
    /// Gets the smallest supported rewritten published-tree input size in bytes.
    /// </summary>
    public const long MinMaxRewrittenFileSizeBytes = 1;

    /// <summary>
    /// Gets the largest supported rewritten published-tree input size in bytes.
    /// </summary>
    public const long MaxMaxRewrittenFileSizeBytes = 32L * 1024L * 1024L;

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
    /// entries point at exported exact-version trees. Each public entry must include <c>releaseManifestSha256</c> to pin
    /// the digest of that tree's <c>.appsurface-docs-release-manifest.json</c>. Entries without the pin are unavailable
    /// and are not mounted.
    /// A missing, unreadable, or malformed catalog does not crash AppSurface Docs, but it leaves all published releases
    /// unavailable until the catalog can be loaded successfully. When <see cref="Enabled"/> is <see langword="true"/>
    /// and this property is blank, startup validation fails before the app begins serving requests.
    /// </remarks>
    public string? CatalogPath { get; set; }

    /// <summary>
    /// Gets or sets the trusted filesystem root that contains published exact-version release trees.
    /// </summary>
    /// <remarks>
    /// Catalog entries use <see cref="AppSurfaceDocsPublishedVersion.ExactTreePath"/> values relative to this directory.
    /// When this property is blank, AppSurface Docs defaults the trusted release root to the directory containing
    /// <see cref="CatalogPath"/>, which supports the common layout where <c>catalog.json</c> sits beside a
    /// <c>releases/</c> directory. Relative configured values resolve from the app content root.
    /// The root and every mounted exact tree must be ordinary directories, not symbolic links, junctions, or other
    /// reparse points. Absolute catalog <c>exactTreePath</c> values are invalid; set this property to the old parent
    /// directory and make each catalog entry relative when migrating older catalogs.
    /// </remarks>
    public string? TrustedReleaseRootPath { get; set; }

    /// <summary>
    /// Gets or sets the maximum input size, in bytes, for request-time rewrites of published-tree HTML and search-index files.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="DefaultMaxRewrittenFileSizeBytes"/> bytes. The value must be between
    /// <see cref="MinMaxRewrittenFileSizeBytes"/> and <see cref="MaxMaxRewrittenFileSizeBytes"/>. This limit applies
    /// only to exported <c>.html</c> files and the root <c>search-index.json</c> file when a published release tree is
    /// mounted through AppSurface Docs versioning. Static assets such as CSS, JavaScript, images, and fonts continue to
    /// stream normally and are not capped by this option.
    /// </remarks>
    public long MaxRewrittenFileSizeBytes { get; set; } = DefaultMaxRewrittenFileSizeBytes;
}

/// <summary>
/// Localization settings for the live AppSurface Docs source-backed documentation surface.
/// </summary>
/// <remarks>
/// V1 localization applies only to the live source-backed docs surface. Published exact-version release trees keep the
/// existing unlocalized route contract until localized release trees are designed as a separate feature. The built-in
/// defaults keep localization off, use <c>en</c> as the default locale when enabled, prefix localized routes with the
/// locale route segment, and default search to the active locale.
/// </remarks>
public sealed class AppSurfaceDocsLocalizationOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether AppSurface Docs builds locale-aware document graph data.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the default locale code. Defaults to <c>en</c>.
    /// </summary>
    public string DefaultLocale { get; set; } = "en";

    /// <summary>
    /// Gets or sets the locales supported by the live docs surface.
    /// </summary>
    public AppSurfaceDocsLocaleOptions[] Locales { get; set; } = [];

    /// <summary>
    /// Gets or sets how locale prefixes are represented in public live docs routes.
    /// </summary>
    public AppSurfaceDocsLocaleRouteMode RouteMode { get; set; } = AppSurfaceDocsLocaleRouteMode.LocalePrefix;

    /// <summary>
    /// Gets or sets the global missing-translation fallback behavior.
    /// </summary>
    public AppSurfaceDocsLocaleFallbackMode FallbackMode { get; set; } = AppSurfaceDocsLocaleFallbackMode.DefaultLocaleWithNotice;

    /// <summary>
    /// Gets or sets the default localized search scope.
    /// </summary>
    public AppSurfaceDocsLocaleSearchMode SearchMode { get; set; } = AppSurfaceDocsLocaleSearchMode.ActiveLocale;
}

/// <summary>
/// Describes one configured AppSurface Docs locale validated during AppSurface Docs startup.
/// </summary>
/// <remarks>
/// Locale entries are runtime configuration, not loose display hints: <see cref="Code"/> values must be unique valid
/// BCP-47 tags, and resolved route prefixes must be unique and avoid AppSurface Docs reserved route segments. Omitted
/// <see cref="Lang"/> and <see cref="RoutePrefix"/> values fall back to <see cref="Code"/>, so duplicate codes or route
/// aliases can fail startup validation even when those properties are not explicitly set.
/// </remarks>
public sealed class AppSurfaceDocsLocaleOptions
{
    /// <summary>
    /// Gets or sets the unique BCP-47 locale code, such as <c>en</c>, <c>fr</c>, or <c>pt-BR</c>.
    /// </summary>
    /// <remarks>
    /// The code is required, must parse as a culture name, and becomes the default <see cref="Lang"/> and
    /// <see cref="RoutePrefix"/> when those properties are blank.
    /// </remarks>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the reader-facing locale label, such as <c>English</c> or <c>Français</c>.
    /// </summary>
    /// <remarks>
    /// The label is optional in Phase 1 and is not validated beyond normal configuration binding.
    /// </remarks>
    public string? Label { get; set; }

    /// <summary>
    /// Gets or sets the HTML <c>lang</c> value. When omitted, <see cref="Code"/> is used.
    /// </summary>
    /// <remarks>
    /// Use this only when the rendered HTML language tag should differ from the configured AppSurface Docs locale code.
    /// Blank values are treated as omitted and fall back to <see cref="Code"/>.
    /// </remarks>
    public string? Lang { get; set; }

    /// <summary>
    /// Gets or sets the text direction for this locale. Defaults to <see cref="AppSurfaceDocsTextDirection.Ltr"/>.
    /// </summary>
    public AppSurfaceDocsTextDirection Direction { get; set; } = AppSurfaceDocsTextDirection.Ltr;

    /// <summary>
    /// Gets or sets the route segment used for this locale. When omitted, <see cref="Code"/> is used.
    /// </summary>
    /// <remarks>
    /// The resolved route prefix must be unique across configured locales and must not collide with reserved AppSurface Docs
    /// segments such as search, health, package, version, release, public-section, or asset endpoints. Collisions fail
    /// startup validation.
    /// </remarks>
    public string? RoutePrefix { get; set; }

    /// <summary>
    /// Resolves the locale route prefix after applying the documented fallback to <see cref="Code"/>.
    /// </summary>
    /// <remarks>
    /// This helper centralizes prefix trimming so graph and route-candidate generation do not drift when
    /// <see cref="RoutePrefix"/> is omitted.
    /// </remarks>
    internal string ResolveRoutePrefix()
    {
        return string.IsNullOrWhiteSpace(RoutePrefix)
            ? Code.Trim()
            : RoutePrefix!.Trim();
    }
}

/// <summary>
/// Enumerates supported locale route modes.
/// </summary>
/// <remarks>
/// Numeric values are part of the public configuration contract. Append new modes with new explicit values.
/// </remarks>
public enum AppSurfaceDocsLocaleRouteMode
{
    /// <summary>
    /// Prefix live docs routes with a configured locale route segment.
    /// </summary>
    LocalePrefix = 0
}

/// <summary>
/// Enumerates supported global localization fallback modes.
/// </summary>
/// <remarks>
/// Numeric values are part of the public configuration contract. Append new modes with new explicit values.
/// </remarks>
public enum AppSurfaceDocsLocaleFallbackMode
{
    /// <summary>
    /// Render default-locale content on the target-locale route with visible fallback context.
    /// </summary>
    DefaultLocaleWithNotice = 0,

    /// <summary>
    /// Do not create fallback pages for missing localized variants.
    /// </summary>
    Disabled = 1
}

/// <summary>
/// Enumerates supported localized search defaults.
/// </summary>
/// <remarks>
/// Numeric values are part of the public configuration contract. Append new modes with new explicit values.
/// </remarks>
public enum AppSurfaceDocsLocaleSearchMode
{
    /// <summary>
    /// Search the active locale by default.
    /// </summary>
    ActiveLocale = 0
}

/// <summary>
/// Enumerates supported text directions for localized docs pages.
/// </summary>
/// <remarks>
/// Numeric values are part of the public configuration contract. Append new directions with new explicit values.
/// </remarks>
public enum AppSurfaceDocsTextDirection
{
    /// <summary>
    /// Left-to-right text direction.
    /// </summary>
    Ltr = 0,

    /// <summary>
    /// Right-to-left text direction.
    /// </summary>
    Rtl = 1
}

/// <summary>
/// Validates <see cref="AppSurfaceDocsOptions"/> and rejects unsupported or ambiguous startup configurations.
/// </summary>
public sealed class AppSurfaceDocsOptionsValidator : IValidateOptions<AppSurfaceDocsOptions>
{
    private static readonly Regex SymbolSourceTemplateTokenRegex = new(
        @"\{[^}]+\}",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AppSurfaceDocsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        var source = options.Source;
        var identity = options.Identity;
        var harvest = options.Harvest;
        var diagnostics = options.Diagnostics;
        var metrics = options.Metrics;
        var bundle = options.Bundle;
        var sidebar = options.Sidebar;
        var contributor = options.Contributor;
        var routing = options.Routing;
        var versioning = options.Versioning;
        var localization = options.Localization;
        string? normalizedRouteRootPath = null;
        string? normalizedDocsRootPath = null;

        if (!Enum.IsDefined(options.Mode))
        {
            failures.Add($"Unsupported AppSurface Docs mode '{options.Mode}'.");
        }

        if (!IsValidCacheExpirationMinutes(options.CacheExpirationMinutes))
        {
            failures.Add(
                $"AppSurfaceDocs:CacheExpirationMinutes must be a finite number between {AppSurfaceDocsOptions.MinCacheExpirationMinutes} and {AppSurfaceDocsOptions.MaxCacheExpirationMinutes} minutes that maps to a whole number of seconds.");
        }

        if (identity is null)
        {
            failures.Add("AppSurfaceDocs:Identity must not be null.");
        }
        else
        {
            if (identity.Logo is null)
            {
                failures.Add("AppSurfaceDocs:Identity:Logo must not be null.");
            }
            else
            {
                AddIdentityBrowserPathFailure(
                    failures,
                    "AppSurfaceDocs:Identity:Logo:Path",
                    identity.Logo.Path,
                    allowDocsHomeDefault: false);
            }

            if (identity.Wordmark is null)
            {
                failures.Add("AppSurfaceDocs:Identity:Wordmark must not be null.");
            }
            else
            {
                var highlightText = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(identity.Wordmark.HighlightText);
                var highlightColor = AppSurfaceDocsIdentityPath.NormalizeTextOrNull(identity.Wordmark.HighlightColor);
                if (highlightColor is not null
                    && !AppSurfaceDocsIdentityPath.TryNormalizeCssHexColor(highlightColor, out _, out var colorError))
                {
                    failures.Add($"AppSurfaceDocs:Identity:Wordmark:HighlightColor {colorError}");
                }

                if (highlightColor is not null && highlightText is null)
                {
                    failures.Add(
                        "AppSurfaceDocs:Identity:Wordmark:HighlightColor requires AppSurfaceDocs:Identity:Wordmark:HighlightText.");
                }

                if (highlightText is not null
                    && !AppSurfaceDocsIdentityPath.NormalizeDisplayName(identity.DisplayName)
                        .Contains(highlightText, StringComparison.Ordinal))
                {
                    failures.Add(
                        "AppSurfaceDocs:Identity:Wordmark:HighlightText must match part of the resolved AppSurfaceDocs:Identity:DisplayName.");
                }
            }

            if (identity.Favicon is null)
            {
                failures.Add("AppSurfaceDocs:Identity:Favicon must not be null.");
            }
            else
            {
                AddIdentityBrowserPathFailure(
                    failures,
                    "AppSurfaceDocs:Identity:Favicon:SvgPath",
                    identity.Favicon.SvgPath,
                    allowDocsHomeDefault: false);
                AddIdentityBrowserPathFailure(
                    failures,
                    "AppSurfaceDocs:Identity:Favicon:IcoPath",
                    identity.Favicon.IcoPath,
                    allowDocsHomeDefault: false);
                AddIdentityBrowserPathFailure(
                    failures,
                    "AppSurfaceDocs:Identity:Favicon:PngPath",
                    identity.Favicon.PngPath,
                    allowDocsHomeDefault: false);
            }

            if (identity.BrandingAssets is null)
            {
                failures.Add("AppSurfaceDocs:Identity:BrandingAssets must not be null.");
            }
            else
            {
                AddIdentityBrowserPathFailure(
                    failures,
                    "AppSurfaceDocs:Identity:BrandingAssets:RequestPath",
                    identity.BrandingAssets.RequestPath,
                    allowDocsHomeDefault: false);

                if (AppSurfaceDocsIdentityPath.TryNormalizeBrowserPath(
                        identity.BrandingAssets.RequestPath,
                        out var brandingRequestPath,
                        out _)
                    && string.Equals(NormalizeApplicationRelativePath(brandingRequestPath), "/", StringComparison.Ordinal))
                {
                    failures.Add("AppSurfaceDocs:Identity:BrandingAssets:RequestPath must not be the application root.");
                }
            }

            AddIdentityBrowserPathFailure(
                failures,
                "AppSurfaceDocs:Identity:HomeHref",
                identity.HomeHref,
                allowDocsHomeDefault: true);
        }

        if (source is null)
        {
            failures.Add("AppSurfaceDocs:Source must not be null.");
        }

        if (harvest is null)
        {
            failures.Add("AppSurfaceDocs:Harvest must not be null.");
        }
        else
        {
            if (!Enum.IsDefined(harvest.StartupMode))
            {
                failures.Add($"Unsupported AppSurface Docs harvest startup mode '{harvest.StartupMode}'.");
            }

            if (harvest.InitialRequestWaitBudgetMilliseconds < 0)
            {
                failures.Add("AppSurfaceDocs:Harvest:InitialRequestWaitBudgetMilliseconds must be greater than or equal to zero.");
            }

            if (harvest.TestingPreHarvestDelayMilliseconds < 0)
            {
                failures.Add("AppSurfaceDocs:Harvest:TestingPreHarvestDelayMilliseconds must be greater than or equal to zero.");
            }

            if (harvest.TestingDelayPerHarvesterMilliseconds < 0)
            {
                failures.Add("AppSurfaceDocs:Harvest:TestingDelayPerHarvesterMilliseconds must be greater than or equal to zero.");
            }

            if (harvest.TestingDelayPerDocumentMilliseconds < 0)
            {
                failures.Add("AppSurfaceDocs:Harvest:TestingDelayPerDocumentMilliseconds must be greater than or equal to zero.");
            }

            if (harvest.Health is null)
            {
                failures.Add("AppSurfaceDocs:Harvest:Health must not be null.");
            }
            else
            {
                if (!Enum.IsDefined(harvest.Health.ExposeRoutes))
                {
                    failures.Add($"Unsupported AppSurface Docs harvest health route exposure mode '{harvest.Health.ExposeRoutes}'.");
                }

                if (!Enum.IsDefined(harvest.Health.ShowChrome))
                {
                    failures.Add($"Unsupported AppSurface Docs harvest health chrome exposure mode '{harvest.Health.ShowChrome}'.");
                }
            }

            ValidateHarvestPathOptions(harvest.Paths, "AppSurfaceDocs:Harvest:Paths", failures);
            ValidateHarvestSourceOptions(harvest.Markdown, "AppSurfaceDocs:Harvest:Markdown", failures);
            ValidateHarvestSourceOptions(harvest.CSharp, "AppSurfaceDocs:Harvest:CSharp", failures);
            ValidateHarvestSourceOptions(harvest.JavaScript, "AppSurfaceDocs:Harvest:JavaScript", failures);
            if (harvest.Markdown is not null)
            {
                if (harvest.Markdown.MaxFileSizeBytes <= 0)
                {
                    failures.Add("AppSurfaceDocs:Harvest:Markdown:MaxFileSizeBytes must be greater than zero.");
                }

                if (harvest.Markdown.MaxMetadataFileSizeBytes <= 0)
                {
                    failures.Add("AppSurfaceDocs:Harvest:Markdown:MaxMetadataFileSizeBytes must be greater than zero.");
                }
            }

            if (harvest.CSharp is not null && harvest.CSharp.MaxFileSizeBytes <= 0)
            {
                failures.Add(
                    $"AppSurfaceDocs:Harvest:CSharp:MaxFileSizeBytes must be a positive byte value. Remove the setting to use the default {AppSurfaceDocsCSharpHarvestOptions.DefaultMaxFileSizeBytes} byte limit, or set a positive value for authored C# source that should be parsed.");
            }

            if (harvest.JavaScript is not null)
            {
                if (harvest.JavaScript.MaxFileSizeBytes <= 0)
                {
                    failures.Add("AppSurfaceDocs:Harvest:JavaScript:MaxFileSizeBytes must be greater than zero.");
                }

            }
        }

        if (bundle is null)
        {
            failures.Add("AppSurfaceDocs:Bundle must not be null.");
        }

        if (diagnostics is null)
        {
            failures.Add("AppSurfaceDocs:Diagnostics must not be null.");
        }
        else
        {
            if (!Enum.IsDefined(diagnostics.ExposeRouteInspector))
            {
                failures.Add($"Unsupported AppSurface Docs route inspector exposure mode '{diagnostics.ExposeRouteInspector}'.");
            }

            if (!Enum.IsDefined(diagnostics.ShowChrome))
            {
                failures.Add($"Unsupported AppSurface Docs diagnostics chrome exposure mode '{diagnostics.ShowChrome}'.");
            }
        }

        if (metrics is null)
        {
            failures.Add("AppSurfaceDocs:Metrics must not be null.");
        }
        else
        {
            if (metrics.BrowserCollector is null)
            {
                failures.Add("AppSurfaceDocs:Metrics:BrowserCollector must not be null.");
            }
            else
            {
                if (metrics.BrowserCollector.Enabled && !metrics.Enabled)
                {
                    failures.Add("AppSurfaceDocs:Metrics:BrowserCollector:Enabled requires AppSurfaceDocs:Metrics:Enabled.");
                }

                var endpointUrl = metrics.BrowserCollector.EndpointUrl;
                if (!string.IsNullOrWhiteSpace(endpointUrl)
                    && !TryNormalizeMetricsEndpointUrl(endpointUrl, out _, out var endpointError))
                {
                    failures.Add($"AppSurfaceDocs:Metrics:BrowserCollector:EndpointUrl {endpointError}");
                }

                if (metrics.Enabled
                    && metrics.BrowserCollector.Enabled
                    && string.IsNullOrWhiteSpace(endpointUrl)
                    && metrics.HostedCollection?.Enabled != true)
                {
                    failures.Add(
                        "AppSurfaceDocs:Metrics:BrowserCollector:EndpointUrl is required when browser collection is enabled without hosted collection.");
                }
            }

            if (metrics.HostedCollection is null)
            {
                failures.Add("AppSurfaceDocs:Metrics:HostedCollection must not be null.");
            }
            else if (metrics.HostedCollection.Enabled && !metrics.Enabled)
            {
                failures.Add("AppSurfaceDocs:Metrics:HostedCollection:Enabled requires AppSurfaceDocs:Metrics:Enabled.");
            }

            if (metrics.HostedReview is null)
            {
                failures.Add("AppSurfaceDocs:Metrics:HostedReview must not be null.");
            }
            else
            {
                if (metrics.HostedReview.Enabled && !metrics.Enabled)
                {
                    failures.Add("AppSurfaceDocs:Metrics:HostedReview:Enabled requires AppSurfaceDocs:Metrics:Enabled.");
                }

                if (metrics.HostedReview.Enabled && metrics.HostedCollection?.Enabled != true)
                {
                    failures.Add("AppSurfaceDocs:Metrics:HostedReview:Enabled requires AppSurfaceDocs:Metrics:HostedCollection:Enabled.");
                }

                if (!Enum.IsDefined(metrics.HostedReview.Exposure))
                {
                    failures.Add($"Unsupported AppSurface Docs search-quality exposure mode '{metrics.HostedReview.Exposure}'.");
                }
            }
        }

        if (sidebar is null)
        {
            failures.Add("AppSurfaceDocs:Sidebar must not be null.");
        }
        else if (sidebar.NamespacePrefixes is null)
        {
            failures.Add("AppSurfaceDocs:Sidebar:NamespacePrefixes must not be null.");
        }

        if (contributor is null)
        {
            failures.Add("AppSurfaceDocs:Contributor must not be null.");
        }
        else if (!Enum.IsDefined(contributor.LastUpdatedMode))
        {
            failures.Add($"Unsupported AppSurface Docs contributor last-updated mode '{contributor.LastUpdatedMode}'.");
        }

        if (routing is null)
        {
            failures.Add("AppSurfaceDocs:Routing must not be null.");
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
                        "AppSurfaceDocs:Routing:RouteRootPath must be an app-relative path such as '/docs', 'docs', '/foo/bar', or 'foo/bar'. It must not end with '/', include a query or fragment, or use an absolute URL.");
                    routeRootPathIsValid = false;
                }
                else if (IsReservedRouteFamilyChildPath(routing.RouteRootPath))
                {
                    failures.Add(
                        "AppSurfaceDocs:Routing:RouteRootPath cannot target a reserved archive or exact-version child route such as '/foo/bar/versions' or '/foo/bar/v'. Use the parent route root, for example '/foo/bar'.");
                    routeRootPathIsValid = false;
                }
            }

            if (routing.DocsRootPath is not null && !IsValidAppRelativeRootPath(routing.DocsRootPath))
            {
                failures.Add(
                    "AppSurfaceDocs:Routing:DocsRootPath must be an app-relative path such as '/docs/next', 'docs/next', '/foo/bar/next', or 'foo/bar/next'. It must not end with '/', include a query or fragment, or use an absolute URL.");
                docsRootPathIsValid = false;
            }

            if (routing.PublicOrigin is not null
                && !DocsUrlBuilder.TryNormalizePublicOrigin(routing.PublicOrigin, out _))
            {
                failures.Add(
                    "AppSurfaceDocs:Routing:PublicOrigin must be an absolute http or https origin such as 'https://docs.example.com'. Configure only the origin: do not include a docs path, query string, fragment, userinfo, or unsupported URL scheme.");
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
            failures.Add("AppSurfaceDocs:Versioning must not be null.");
        }
        else if (versioning.MaxRewrittenFileSizeBytes < AppSurfaceDocsVersioningOptions.MinMaxRewrittenFileSizeBytes
                 || versioning.MaxRewrittenFileSizeBytes > AppSurfaceDocsVersioningOptions.MaxMaxRewrittenFileSizeBytes)
        {
            failures.Add(
                $"AppSurfaceDocs:Versioning:MaxRewrittenFileSizeBytes must be between {AppSurfaceDocsVersioningOptions.MinMaxRewrittenFileSizeBytes} and {AppSurfaceDocsVersioningOptions.MaxMaxRewrittenFileSizeBytes} bytes.");
        }

        ValidateLocalization(localization, failures);

        if (options.Mode == AppSurfaceDocsMode.Bundle)
        {
            if (bundle is null || string.IsNullOrWhiteSpace(bundle.Path))
            {
                failures.Add("AppSurface Docs bundle mode requires AppSurfaceDocs:Bundle:Path.");
            }

            failures.Add("AppSurface Docs bundle mode is not implemented yet. Use AppSurfaceDocs:Mode=Source for Slice 1.");
        }

        if (options.Mode == AppSurfaceDocsMode.Source
            && source?.RepositoryRoot is not null
            && string.IsNullOrWhiteSpace(source.RepositoryRoot))
        {
            failures.Add("AppSurfaceDocs:Source:RepositoryRoot cannot be whitespace.");
        }

        if (versioning?.Enabled == true)
        {
            if (string.IsNullOrWhiteSpace(versioning.CatalogPath))
            {
                failures.Add("AppSurface Docs versioning requires AppSurfaceDocs:Versioning:CatalogPath.");
            }

            if (normalizedRouteRootPath is not null
                && string.Equals(normalizedDocsRootPath, normalizedRouteRootPath, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    "AppSurface Docs versioning cannot use the route-family root as the live source docs root. The live preview would collide with stable entry, archive, and exact-version routes. Use '/next' for root-mounted docs or a child such as '/foo/bar/next'.");
            }

            if (normalizedRouteRootPath is not null
                && normalizedDocsRootPath is not null
                && IsReservedRouteFamilyChildPath(normalizedDocsRootPath, normalizedRouteRootPath))
            {
                failures.Add(
                    "AppSurfaceDocs:Routing:DocsRootPath cannot use a reserved archive or exact-version child of the same route root, such as '/foo/bar/versions' or '/foo/bar/v'. Use a live preview child such as '/foo/bar/next'.");
            }
        }

        if (contributor is not null
            && contributor.Enabled
            && (!string.IsNullOrWhiteSpace(contributor.SourceUrlTemplate)
                || !string.IsNullOrWhiteSpace(contributor.EditUrlTemplate))
            && string.IsNullOrWhiteSpace(contributor.DefaultBranch))
        {
            failures.Add("AppSurfaceDocs:Contributor:DefaultBranch is required when SourceUrlTemplate or EditUrlTemplate is configured.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SourceUrlTemplate)
            && contributor.SourceUrlTemplate.Contains("{path}", StringComparison.Ordinal) is false)
        {
            failures.Add("AppSurfaceDocs:Contributor:SourceUrlTemplate must contain the {path} token.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.EditUrlTemplate)
            && contributor.EditUrlTemplate.Contains("{path}", StringComparison.Ordinal) is false)
        {
            failures.Add("AppSurfaceDocs:Contributor:EditUrlTemplate must contain the {path} token.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SymbolSourceUrlTemplate)
            && contributor.SymbolSourceUrlTemplate.Contains("{path}", StringComparison.Ordinal) is false)
        {
            failures.Add("AppSurfaceDocs:Contributor:SymbolSourceUrlTemplate must contain the {path} token.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SymbolSourceUrlTemplate)
            && contributor.SymbolSourceUrlTemplate.Contains("{line}", StringComparison.Ordinal) is false)
        {
            failures.Add("AppSurfaceDocs:Contributor:SymbolSourceUrlTemplate must contain the {line} token.");
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
                    $"AppSurfaceDocs:Contributor:SymbolSourceUrlTemplate contains unsupported token(s): {string.Join(", ", unsupportedSymbolSourceTokens)}.");
            }
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SymbolSourceUrlTemplate)
            && contributor.SymbolSourceUrlTemplate.Contains("{branch}", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(contributor.DefaultBranch))
        {
            failures.Add("AppSurfaceDocs:Contributor:DefaultBranch is required when SymbolSourceUrlTemplate contains the {branch} token.");
        }

        if (contributor is not null
            && contributor.Enabled
            && !string.IsNullOrWhiteSpace(contributor.SymbolSourceUrlTemplate)
            && contributor.SymbolSourceUrlTemplate.Contains("{ref}", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(contributor.SourceRef)
            && string.IsNullOrWhiteSpace(contributor.DefaultBranch))
        {
            failures.Add("AppSurfaceDocs:Contributor:SourceRef or DefaultBranch is required when SymbolSourceUrlTemplate contains the {ref} token.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsValidCacheExpirationMinutes(double cacheExpirationMinutes)
    {
        if (!double.IsFinite(cacheExpirationMinutes)
            || cacheExpirationMinutes < AppSurfaceDocsOptions.MinCacheExpirationMinutes
            || cacheExpirationMinutes > AppSurfaceDocsOptions.MaxCacheExpirationMinutes)
        {
            return false;
        }

        var cacheDuration = TimeSpan.FromMinutes(cacheExpirationMinutes);
        return cacheDuration.Ticks % TimeSpan.TicksPerSecond == 0;
    }

    internal static string? NormalizeMetricsEndpointUrlOrNull(string? endpointUrl)
    {
        return TryNormalizeMetricsEndpointUrl(endpointUrl, out var normalizedEndpointUrl, out _)
            ? normalizedEndpointUrl
            : endpointUrl;
    }

    internal static bool TryNormalizeMetricsEndpointUrl(
        string? endpointUrl,
        out string? normalizedEndpointUrl,
        [NotNullWhen(false)] out string? error)
    {
        normalizedEndpointUrl = null;
        error = null;
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            return true;
        }

        var value = endpointUrl.Trim();
        if (ContainsForbiddenMetricsEndpointText(value))
        {
            error = "must not include secret-like values.";
            return false;
        }

        if (value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("\\", StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            error = "must be a same-origin app-root path or an HTTPS absolute URL.";
            return false;
        }

        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            if (value.Length == 1 || value.Contains("://", StringComparison.Ordinal) || value.IndexOfAny(['?', '#']) >= 0)
            {
                error = "must be an app-root path without query string, fragment, or URL scheme.";
                return false;
            }

            normalizedEndpointUrl = value.TrimEnd('/');
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = "must be a same-origin app-root path or an HTTPS absolute URL.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrEmpty(uri.AbsolutePath)
            || string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "must be an HTTPS absolute URL with a path and without userinfo, query string, or fragment.";
            return false;
        }

        normalizedEndpointUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    private static bool ContainsForbiddenMetricsEndpointText(string value)
    {
        return value.Contains("token", StringComparison.OrdinalIgnoreCase)
               || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
               || value.Contains("password", StringComparison.OrdinalIgnoreCase)
               || value.Contains("apikey", StringComparison.OrdinalIgnoreCase)
               || value.Contains("api-key", StringComparison.OrdinalIgnoreCase)
               || value.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
               || value.Contains("connection_string", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIdentityBrowserPathFailure(
        List<string> failures,
        string key,
        string? value,
        bool allowDocsHomeDefault)
    {
        if (allowDocsHomeDefault && string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!AppSurfaceDocsIdentityPath.TryNormalizeBrowserPath(value, out _, out var error))
        {
            failures.Add($"{key} {error}");
        }
    }

    private static string? NormalizeApplicationRelativePath(string? path)
    {
        return path?.StartsWith("~/", StringComparison.Ordinal) == true
            ? "/" + path[2..]
            : path;
    }

    private static bool IsValidAppRelativeRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmedPath = path.Trim();
        if (trimmedPath.Contains("://", StringComparison.Ordinal)
            || trimmedPath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmedPath.Length > 1 && trimmedPath[^1] == '/')
        {
            return false;
        }

        return trimmedPath.IndexOfAny(['?', '#']) < 0;
    }

    private static void ValidateHarvestPathOptions(
        AppSurfaceDocsHarvestPathOptions? options,
        string configurationPath,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add($"{configurationPath} must not be null.");
            return;
        }

        ValidateGlobPatterns(options.IncludeGlobs, $"{configurationPath}:IncludeGlobs", failures);
        ValidateGlobPatterns(options.ExcludeGlobs, $"{configurationPath}:ExcludeGlobs", failures);
        ValidateDefaultExclusions(options.DefaultExclusions, $"{configurationPath}:DefaultExclusions", failures);
        ValidateVcsIgnoreOptions(options.VcsIgnore, $"{configurationPath}:VcsIgnore", failures);
    }

    private static void ValidateHarvestSourceOptions(
        AppSurfaceDocsMarkdownHarvestOptions? options,
        string configurationPath,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add($"{configurationPath} must not be null.");
            return;
        }

        ValidateGlobPatterns(options.IncludeGlobs, $"{configurationPath}:IncludeGlobs", failures);
        ValidateGlobPatterns(options.ExcludeGlobs, $"{configurationPath}:ExcludeGlobs", failures);
        ValidateDefaultExclusions(options.DefaultExclusions, $"{configurationPath}:DefaultExclusions", failures);
    }

    private static void ValidateHarvestSourceOptions(
        AppSurfaceDocsCSharpHarvestOptions? options,
        string configurationPath,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add($"{configurationPath} must not be null.");
            return;
        }

        ValidateGlobPatterns(options.IncludeGlobs, $"{configurationPath}:IncludeGlobs", failures);
        ValidateGlobPatterns(options.ExcludeGlobs, $"{configurationPath}:ExcludeGlobs", failures);
        ValidateDefaultExclusions(options.DefaultExclusions, $"{configurationPath}:DefaultExclusions", failures);
    }

    private static void ValidateHarvestSourceOptions(
        AppSurfaceDocsJavaScriptHarvestOptions? options,
        string configurationPath,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add($"{configurationPath} must not be null.");
            return;
        }

        ValidateGlobPatterns(options.IncludeGlobs, $"{configurationPath}:IncludeGlobs", failures);
        ValidateGlobPatterns(options.ExcludeGlobs, $"{configurationPath}:ExcludeGlobs", failures);
        ValidateDefaultExclusions(options.DefaultExclusions, $"{configurationPath}:DefaultExclusions", failures);
        ValidateJavaScriptGroupNameRules(options.GroupNameRules, $"{configurationPath}:GroupNameRules", failures);
    }

    private static void ValidateJavaScriptGroupNameRules(
        IReadOnlyList<AppSurfaceDocsJavaScriptGroupNameRule>? rules,
        string configurationPath,
        List<string> failures)
    {
        if (rules is null)
        {
            failures.Add($"{configurationPath} must not be null.");
            return;
        }

        for (var index = 0; index < rules.Count; index++)
        {
            var rulePath = $"{configurationPath}:{index}";
            var rule = rules[index];
            if (rule is null)
            {
                failures.Add($"{rulePath} must not be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                failures.Add($"{rulePath}:Name must not be blank.");
            }

            if (rule.IncludeGlobs is null)
            {
                failures.Add($"{rulePath}:IncludeGlobs must not be null.");
                continue;
            }

            if (!rule.IncludeGlobs.Any(pattern => !string.IsNullOrWhiteSpace(pattern)))
            {
                failures.Add($"{rulePath}:IncludeGlobs must contain at least one repository-relative glob pattern.");
            }

            ValidateGlobPatterns(rule.IncludeGlobs, $"{rulePath}:IncludeGlobs", failures);
        }
    }

    private static void ValidateGlobPatterns(
        IReadOnlyList<string>? patterns,
        string configurationPath,
        List<string> failures)
    {
        if (patterns is null)
        {
            failures.Add($"{configurationPath} must not be null.");
            return;
        }

        foreach (var pattern in patterns.Where(pattern => !AppSurfaceDocsHarvestPathPatternValidator.IsValidConfiguredGlobPattern(pattern)))
        {
            failures.Add(
                $"{configurationPath} contains invalid repository-relative glob pattern '{pattern}'. Use forward-slash paths without leading '/', './', URI schemes, drive roots, query strings, fragments, or '..' segments.");
        }
    }

    private static void ValidateDefaultExclusions(
        AppSurfaceDocsHarvestDefaultExclusionOptions? options,
        string configurationPath,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add($"{configurationPath} must not be null.");
            return;
        }

        if (options.DisabledGroups is null)
        {
            failures.Add($"{configurationPath}:DisabledGroups must not be null.");
        }
        else
        {
            foreach (var groupId in options.DisabledGroups)
            {
                ValidateDefaultGroupId(groupId, $"{configurationPath}:DisabledGroups", failures);
            }
        }

        if (options.AllowGlobs is null)
        {
            failures.Add($"{configurationPath}:AllowGlobs must not be null.");
            return;
        }

        foreach (var (groupId, patterns) in options.AllowGlobs)
        {
            ValidateDefaultGroupId(groupId, $"{configurationPath}:AllowGlobs", failures);
            ValidateGlobPatterns(patterns, $"{configurationPath}:AllowGlobs:{groupId}", failures);
        }
    }

    private static void ValidateVcsIgnoreOptions(
        AppSurfaceDocsHarvestVcsIgnoreOptions? options,
        string configurationPath,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add($"{configurationPath} must not be null.");
            return;
        }

        ValidateGlobPatterns(options.AllowGlobs, $"{configurationPath}:AllowGlobs", failures);
    }

    private static void ValidateDefaultGroupId(
        string? groupId,
        string configurationPath,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(groupId)
            || !AppSurfaceDocsHarvestPathPolicy.IsKnownDefaultGroupId(groupId))
        {
            failures.Add(
                $"{configurationPath} contains unsupported default exclusion group '{groupId}'. Supported groups are: {string.Join(", ", Enum.GetNames<AppSurfaceDocsHarvestDefaultExclusionGroup>())}.");
        }
    }

    private static bool IsReservedRouteFamilyChildPath(string path)
    {
        var normalizedPath = DocsUrlBuilder.NormalizeRouteRootPath(path, DocsUrlBuilder.DocsEntryPath, versioningEnabled: false);
        return normalizedPath.EndsWith("/versions", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.EndsWith("/v", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReservedRouteFamilyChildPath(string docsRootPath, string routeRootPath)
    {
        var versionsRoot = DocsUrlBuilder.JoinPath(routeRootPath, "versions");
        var versionPrefix = DocsUrlBuilder.JoinPath(routeRootPath, "v");
        return string.Equals(docsRootPath, versionsRoot, StringComparison.OrdinalIgnoreCase)
               || string.Equals(docsRootPath, versionPrefix, StringComparison.OrdinalIgnoreCase)
               || docsRootPath.StartsWith(versionPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateLocalization(
        AppSurfaceDocsLocalizationOptions? localization,
        List<string> failures)
    {
        if (localization is null)
        {
            failures.Add("AppSurfaceDocs:Localization must not be null.");
            return;
        }

        if (!Enum.IsDefined(localization.RouteMode))
        {
            failures.Add($"Unsupported AppSurface Docs localization route mode '{localization.RouteMode}'.");
        }

        if (!Enum.IsDefined(localization.FallbackMode))
        {
            failures.Add($"Unsupported AppSurface Docs localization fallback mode '{localization.FallbackMode}'.");
        }

        if (!Enum.IsDefined(localization.SearchMode))
        {
            failures.Add($"Unsupported AppSurface Docs localization search mode '{localization.SearchMode}'.");
        }

        if (localization.Locales is null)
        {
            failures.Add("AppSurfaceDocs:Localization:Locales must not be null.");
            return;
        }

        if (!localization.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(localization.DefaultLocale))
        {
            failures.Add("AppSurfaceDocs:Localization:DefaultLocale is required when localization is enabled.");
        }

        if (localization.Locales.Length == 0)
        {
            failures.Add("AppSurface Docs localization requires at least one configured locale when enabled.");
            return;
        }

        var localeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routePrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultLocaleMatched = false;
        for (var i = 0; i < localization.Locales.Length; i++)
        {
            var locale = localization.Locales[i];
            var path = $"AppSurfaceDocs:Localization:Locales:{i}";
            if (locale is null)
            {
                failures.Add($"{path} must not be null.");
                continue;
            }

            var code = locale.Code;
            if (string.IsNullOrWhiteSpace(code))
            {
                failures.Add($"{path}:Code is required.");
            }
            else
            {
                if (!IsValidCultureTag(code))
                {
                    failures.Add($"{path}:Code must be a valid BCP-47 culture tag.");
                }

                if (!localeCodes.Add(code))
                {
                    failures.Add($"AppSurface Docs localization locale code '{code}' is configured more than once.");
                }

                if (string.Equals(code, localization.DefaultLocale, StringComparison.OrdinalIgnoreCase))
                {
                    defaultLocaleMatched = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(locale.Lang) && !IsValidCultureTag(locale.Lang))
            {
                failures.Add($"{path}:Lang must be a valid BCP-47 culture tag.");
            }

            if (!Enum.IsDefined(locale.Direction))
            {
                failures.Add($"{path}:Direction must be Ltr or Rtl.");
            }

            var routePrefix = string.IsNullOrWhiteSpace(locale.RoutePrefix) ? code : locale.RoutePrefix;
            if (string.IsNullOrWhiteSpace(routePrefix))
            {
                failures.Add($"{path}:RoutePrefix is required when Code is blank.");
            }
            else if (!IsValidLocaleRoutePrefix(routePrefix))
            {
                failures.Add($"{path}:RoutePrefix must be a single safe route segment and must not contain '/', '?', '#', '.', or '..'.");
            }
            else if (IsReservedLocalizationRoutePrefix(routePrefix))
            {
                failures.Add($"{path}:RoutePrefix '{routePrefix}' collides with a reserved AppSurface Docs route segment.");
            }
            else if (!routePrefixes.Add(routePrefix))
            {
                failures.Add($"AppSurface Docs localization route prefix '{routePrefix}' is configured more than once.");
            }
        }

        if (!string.IsNullOrWhiteSpace(localization.DefaultLocale)
            && !defaultLocaleMatched)
        {
            failures.Add("AppSurfaceDocs:Localization:DefaultLocale must match one configured locale code.");
        }
    }

    private static bool IsValidCultureTag(string value)
    {
        try
        {
            _ = System.Globalization.CultureInfo.GetCultureInfo(value.Trim());
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static bool IsValidLocaleRoutePrefix(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0
               && !trimmed.Contains('/', StringComparison.Ordinal)
               && !trimmed.Contains('\\', StringComparison.Ordinal)
               && !trimmed.Contains('?', StringComparison.Ordinal)
               && !trimmed.Contains('#', StringComparison.Ordinal)
               && !trimmed.Contains('.', StringComparison.Ordinal)
               && !string.Equals(trimmed, "..", StringComparison.Ordinal);
    }

    private static bool IsReservedLocalizationRoutePrefix(string value)
    {
        return value.Trim() is { } trimmed
               && (trimmed.Equals("search", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("search-index.json", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("_search-index", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("_harvest", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("_health", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("_health.json", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("_routes", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("_routes.json", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("sections", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("versions", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("v", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("search.css", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("search-client.js", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("outline-client.js", StringComparison.OrdinalIgnoreCase)
                   || trimmed.Equals("minisearch.min.js", StringComparison.OrdinalIgnoreCase));
    }
}
