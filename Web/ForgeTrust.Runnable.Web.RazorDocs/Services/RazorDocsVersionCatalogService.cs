using System.Text.Json;
namespace ForgeTrust.Runnable.Web.RazorDocs.Services;

/// <summary>
/// Loads, validates, and resolves the configured RazorDocs version catalog.
/// </summary>
/// <remarks>
/// The service performs best-effort validation so a broken stored release tree becomes unavailable without preventing
/// healthy versions or the live preview surface from loading. Validation is intentionally release-local: every version
/// is checked independently for a readable tree root, the required landing and search pages, the search index, and
/// the shared search runtime assets that exact-version pages depend on. The page-local outline runtime is validated
/// conditionally so historical exact trees that never reference <c>outline-client.js</c> remain available after a host
/// upgrade, while newer trees that emit the outline rail still fail fast when the referenced client asset is missing.
/// Public
/// <see cref="RazorDocsResolvedVersion.AvailabilityIssue"/> values are sanitized for archive UI consumption, while
/// filesystem paths and exception details stay in structured logs only.
/// </remarks>
public sealed class RazorDocsVersionCatalogService
{
    private readonly record struct AvailabilityFailure(string PublicMessage, string InternalDetail);

    private static readonly string[] RequiredExactTreeFiles =
    [
        "index.html",
        "search.html",
        "search-index.json",
        "search.css",
        "search-client.js",
        "minisearch.min.js"
    ];

    private static readonly EnumerationOptions ExactTreeHtmlEnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true
    };

    private static readonly JsonDocumentOptions CatalogDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly RazorDocsOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<RazorDocsVersionCatalogService> _logger;
    private readonly Lazy<RazorDocsResolvedVersionCatalog> _catalog;

    /// <summary>
    /// Initializes a new instance of <see cref="RazorDocsVersionCatalogService"/>.
    /// </summary>
    /// <param name="options">Typed RazorDocs options.</param>
    /// <param name="environment">Hosting environment used to resolve relative catalog and tree paths.</param>
    /// <param name="logger">Logger used for availability warnings.</param>
    public RazorDocsVersionCatalogService(
        RazorDocsOptions options,
        IWebHostEnvironment environment,
        ILogger<RazorDocsVersionCatalogService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _catalog = new Lazy<RazorDocsResolvedVersionCatalog>(LoadCatalog, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Returns the resolved version catalog for the current host.
    /// </summary>
    /// <returns>
    /// The resolved catalog including availability information for each published version. Returns
    /// <see cref="RazorDocsResolvedVersionCatalog.Disabled" /> when versioning is off for this host,
    /// <see cref="RazorDocsResolvedVersionCatalog.EnabledWithoutCatalog" /> when versioning is on but no catalog path
    /// was configured, and <see cref="RazorDocsResolvedVersionCatalog.CreateUnavailable(string?)" /> semantics when a
    /// configured catalog could not be loaded into a usable published-release set.
    /// </returns>
    public RazorDocsResolvedVersionCatalog GetCatalog()
    {
        return _catalog.Value;
    }

    private RazorDocsResolvedVersionCatalog LoadCatalog()
    {
        if (_options.Versioning?.Enabled != true)
        {
            return RazorDocsResolvedVersionCatalog.Disabled;
        }

        var configuredCatalogPath = _options.Versioning.CatalogPath;
        if (string.IsNullOrWhiteSpace(configuredCatalogPath))
        {
            _logger.LogWarning("RazorDocs versioning is enabled, but no catalog path was configured.");
            return RazorDocsResolvedVersionCatalog.EnabledWithoutCatalog;
        }

        string catalogPath;
        try
        {
            catalogPath = ResolveAbsolutePath(configuredCatalogPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            var trimmedCatalogPath = configuredCatalogPath.Trim();
            _logger.LogWarning(
                ex,
                "RazorDocs version catalog path {CatalogPath} is invalid. Versioned release trees will stay unavailable.",
                trimmedCatalogPath);
            return RazorDocsResolvedVersionCatalog.CreateUnavailable(trimmedCatalogPath);
        }

        if (!File.Exists(catalogPath))
        {
            _logger.LogWarning(
                "RazorDocs version catalog {CatalogPath} does not exist. Versioned release trees will stay unavailable.",
                catalogPath);
            return RazorDocsResolvedVersionCatalog.CreateUnavailable(catalogPath);
        }

        JsonElement root;
        try
        {
            var json = File.ReadAllText(catalogPath);
            using var document = JsonDocument.Parse(json, CatalogDocumentOptions);
            root = document.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RazorDocs version catalog {CatalogPath} could not be read. Versioned release trees will stay unavailable.",
                catalogPath);
            return RazorDocsResolvedVersionCatalog.CreateUnavailable(catalogPath);
        }

        if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            _logger.LogWarning(
                "RazorDocs version catalog {CatalogPath} must be a JSON object or null. Versioned release trees will stay unavailable.",
                catalogPath);
            return RazorDocsResolvedVersionCatalog.CreateUnavailable(catalogPath);
        }

        var catalogDirectory = Path.GetDirectoryName(catalogPath) ?? _environment.ContentRootPath;
        var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versions = new List<RazorDocsResolvedVersion>();
        string? recommendedVersion;
        IEnumerable<JsonElement> versionEntries;
        try
        {
            recommendedVersion = root.ValueKind == JsonValueKind.Null
                ? null
                : TryReadOptionalTrimmedString(root, "recommendedVersion", out var recommendedVersionValue, out var recommendedVersionIssue)
                    ? recommendedVersionValue
                    : LogAndIgnoreInvalidRecommendedVersion(catalogPath, recommendedVersionIssue!);
            versionEntries = GetVersionEntries(root, catalogPath);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "RazorDocs version catalog {CatalogPath} has an invalid top-level payload. Versioned release trees will stay unavailable.",
                catalogPath);
            return RazorDocsResolvedVersionCatalog.CreateUnavailable(catalogPath);
        }

        foreach (var versionEntry in versionEntries)
        {
            if (versionEntry.ValueKind == JsonValueKind.Null)
            {
                _logger.LogWarning(
                    "Skipping one RazorDocs catalog version entry from {CatalogPath} because the entry itself is null.",
                    catalogPath);
                continue;
            }

            if (versionEntry.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning(
                    "Skipping one RazorDocs catalog version entry from {CatalogPath} because the entry is not a JSON object.",
                    catalogPath);
                continue;
            }

            if (!TryParseVersionEntry(versionEntry, catalogPath, out var version))
            {
                continue;
            }

            var normalizedVersion = version.Version.Trim();
            if (!seenVersions.Add(normalizedVersion))
            {
                _logger.LogWarning(
                    "Skipping duplicate RazorDocs catalog entry for version {Version} from {CatalogPath}.",
                    normalizedVersion,
                    catalogPath);
                continue;
            }

            var resolved = ResolveVersion(version, normalizedVersion, catalogDirectory);
            versions.Add(resolved);
        }

        var recommendedVersionEntry = ResolveRecommendedVersion(recommendedVersion, versions, catalogPath);
        return new RazorDocsResolvedVersionCatalog(RazorDocsResolvedVersionCatalogStatus.Resolved, catalogPath, versions, recommendedVersionEntry);
    }

    private string? LogAndIgnoreInvalidRecommendedVersion(string catalogPath, string issue)
    {
        _logger.LogWarning(
            "Ignoring RazorDocs recommended version metadata from {CatalogPath} because {Issue}",
            catalogPath,
            issue);
        return null;
    }

    private static IEnumerable<JsonElement> GetVersionEntries(JsonElement root, string catalogPath)
    {
        if (root.ValueKind == JsonValueKind.Null || !TryGetPropertyIgnoreCase(root, "versions", out var versionsElement))
        {
            return [];
        }

        if (versionsElement.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (versionsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"RazorDocs version catalog '{catalogPath}' has a non-array versions payload.");
        }

        return versionsElement.EnumerateArray().ToArray();
    }

    private bool TryParseVersionEntry(JsonElement versionEntry, string catalogPath, out RazorDocsPublishedVersion version)
    {
        version = new RazorDocsPublishedVersion();

        if (!TryReadOptionalTrimmedString(versionEntry, "version", out var versionIdentifier, out var versionIssue)
            || string.IsNullOrWhiteSpace(versionIdentifier))
        {
            _logger.LogWarning(
                "Skipping one RazorDocs catalog version entry from {CatalogPath} because {Issue}",
                catalogPath,
                versionIssue ?? "it has no version identifier.");
            return false;
        }

        if (!TryReadOptionalTrimmedString(versionEntry, "label", out var label, out var labelIssue))
        {
            LogInvalidVersionEntry(versionIdentifier, catalogPath, labelIssue!);
            return false;
        }

        if (!TryReadOptionalTrimmedString(versionEntry, "summary", out var summary, out var summaryIssue))
        {
            LogInvalidVersionEntry(versionIdentifier, catalogPath, summaryIssue!);
            return false;
        }

        if (!TryReadOptionalTrimmedString(versionEntry, "exactTreePath", out var exactTreePath, out var exactTreePathIssue))
        {
            LogInvalidVersionEntry(versionIdentifier, catalogPath, exactTreePathIssue!);
            return false;
        }

        if (!TryReadEnum(versionEntry, "supportState", RazorDocsVersionSupportState.Current, out var supportState, out var supportStateIssue))
        {
            LogInvalidVersionEntry(versionIdentifier, catalogPath, supportStateIssue!);
            return false;
        }

        if (!TryReadEnum(versionEntry, "visibility", RazorDocsVersionVisibility.Public, out var visibility, out var visibilityIssue))
        {
            LogInvalidVersionEntry(versionIdentifier, catalogPath, visibilityIssue!);
            return false;
        }

        if (!TryReadEnum(versionEntry, "advisoryState", RazorDocsVersionAdvisoryState.None, out var advisoryState, out var advisoryStateIssue))
        {
            LogInvalidVersionEntry(versionIdentifier, catalogPath, advisoryStateIssue!);
            return false;
        }

        version = new RazorDocsPublishedVersion
        {
            Version = versionIdentifier,
            Label = label,
            Summary = summary,
            ExactTreePath = exactTreePath,
            SupportState = supportState,
            Visibility = visibility,
            AdvisoryState = advisoryState
        };
        return true;
    }

    private void LogInvalidVersionEntry(string versionIdentifier, string catalogPath, string issue)
    {
        _logger.LogWarning(
            "Skipping RazorDocs catalog entry for version {Version} from {CatalogPath} because {Issue}",
            versionIdentifier,
            catalogPath,
            issue);
    }

    private static bool TryReadOptionalTrimmedString(
        JsonElement element,
        string propertyName,
        out string? value,
        out string? issue)
    {
        value = null;
        issue = null;

        if (!TryGetPropertyIgnoreCase(element, propertyName, out var propertyValue)
            || propertyValue.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (propertyValue.ValueKind != JsonValueKind.String)
        {
            issue = $"property '{propertyName}' must be a JSON string or null.";
            return false;
        }

        value = propertyValue.GetString()?.Trim();
        return true;
    }

    private static bool TryReadEnum<TEnum>(
        JsonElement element,
        string propertyName,
        TEnum defaultValue,
        out TEnum value,
        out string? issue)
        where TEnum : struct, Enum
    {
        issue = null;
        value = defaultValue;

        if (!TryGetPropertyIgnoreCase(element, propertyName, out var propertyValue)
            || propertyValue.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (propertyValue.ValueKind != JsonValueKind.String)
        {
            if (propertyValue.ValueKind == JsonValueKind.Number
                && propertyValue.TryGetInt32(out var numericValue)
                && Enum.IsDefined(typeof(TEnum), numericValue))
            {
                value = (TEnum)Enum.ToObject(typeof(TEnum), numericValue);
                return true;
            }

            issue = $"property '{propertyName}' must be a supported JSON string or number when present.";
            return false;
        }

        var rawValue = propertyValue.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            issue = $"property '{propertyName}' cannot be blank when present.";
            return false;
        }

        if (!Enum.TryParse<TEnum>(rawValue, ignoreCase: true, out value)
            || !Enum.IsDefined(value))
        {
            issue = $"property '{propertyName}' value '{rawValue}' is not a supported {typeof(TEnum).Name} value.";
            return false;
        }

        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement propertyValue)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    propertyValue = property.Value;
                    return true;
                }
            }
        }

        propertyValue = default;
        return false;
    }

    private RazorDocsResolvedVersion ResolveVersion(
        RazorDocsPublishedVersion version,
        string normalizedVersion,
        string catalogDirectory)
    {
        var label = string.IsNullOrWhiteSpace(version.Label) ? normalizedVersion : version.Label.Trim();
        var summary = string.IsNullOrWhiteSpace(version.Summary) ? null : version.Summary.Trim();
        var exactRootUrl = DocsUrlBuilder.DocsVersionPrefix + "/" + Uri.EscapeDataString(normalizedVersion);
        var isPublic = version.Visibility == RazorDocsVersionVisibility.Public;
        string? exactTreePath;
        AvailabilityFailure? availabilityFailure;

        try
        {
            exactTreePath = ResolveCatalogRelativePath(catalogDirectory, version.ExactTreePath);
            availabilityFailure = ValidateExactTree(exactTreePath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            exactTreePath = null;
            availabilityFailure = new AvailabilityFailure(
                PublicMessage: "Published release tree path is invalid.",
                InternalDetail: $"ExactTreePath '{version.ExactTreePath?.Trim()}' is invalid: {ex.Message}");
        }

        if (availabilityFailure is not null && isPublic)
        {
            _logger.LogWarning(
                "RazorDocs version {Version} is unavailable: {AvailabilityIssue} Detail: {AvailabilityDetail}",
                normalizedVersion,
                availabilityFailure.Value.PublicMessage,
                availabilityFailure.Value.InternalDetail);
        }

        return new RazorDocsResolvedVersion(
            Version: normalizedVersion,
            Label: label,
            Summary: summary,
            ExactTreePath: exactTreePath,
            ExactRootUrl: exactRootUrl,
            SupportState: version.SupportState,
            Visibility: version.Visibility,
            AdvisoryState: version.AdvisoryState,
            IsAvailable: availabilityFailure is null,
            AvailabilityIssue: availabilityFailure?.PublicMessage);
    }

    private RazorDocsResolvedVersion? ResolveRecommendedVersion(
        string? configuredRecommendedVersion,
        IReadOnlyList<RazorDocsResolvedVersion> versions,
        string catalogPath)
    {
        if (string.IsNullOrWhiteSpace(configuredRecommendedVersion))
        {
            return null;
        }

        var normalizedVersion = configuredRecommendedVersion.Trim();
        var recommendedVersion = versions.FirstOrDefault(
            version => string.Equals(version.Version, normalizedVersion, StringComparison.OrdinalIgnoreCase));
        if (recommendedVersion is null)
        {
            _logger.LogWarning(
                "RazorDocs recommended version {Version} from {CatalogPath} was not found in the catalog entries.",
                normalizedVersion,
                catalogPath);
            return null;
        }

        if (recommendedVersion.Visibility != RazorDocsVersionVisibility.Public)
        {
            _logger.LogWarning(
                "RazorDocs recommended version {Version} from {CatalogPath} is hidden and cannot be mounted at /docs.",
                normalizedVersion,
                catalogPath);
            return null;
        }

        if (!recommendedVersion.IsAvailable)
        {
            _logger.LogWarning(
                "RazorDocs recommended version {Version} from {CatalogPath} is unavailable and cannot be mounted at /docs.",
                normalizedVersion,
                catalogPath);
            return null;
        }

        return recommendedVersion;
    }

    private string ResolveAbsolutePath(string path)
    {
        path = path.Trim();
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, path));
    }

    private static string? ResolveCatalogRelativePath(string catalogDirectory, string? configuredPath)
    {
        configuredPath = configuredPath?.Trim();
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(catalogDirectory, configuredPath));
    }

    private static AvailabilityFailure? ValidateExactTree(string? exactTreePath)
    {
        if (string.IsNullOrWhiteSpace(exactTreePath))
        {
            return new AvailabilityFailure(
                PublicMessage: "Published release tree path is missing.",
                InternalDetail: "ExactTreePath is missing.");
        }

        if (!Directory.Exists(exactTreePath))
        {
            return new AvailabilityFailure(
                PublicMessage: "Published release tree directory does not exist.",
                InternalDetail: $"ExactTreePath '{exactTreePath}' does not exist.");
        }

        foreach (var requiredFile in RequiredExactTreeFiles)
        {
            var requiredPath = Path.Combine(exactTreePath, requiredFile);
            if (!File.Exists(requiredPath))
            {
                return new AvailabilityFailure(
                    PublicMessage: $"Published release tree is missing {requiredFile}.",
                    InternalDetail: $"ExactTreePath '{exactTreePath}' is missing {requiredFile}.");
            }
        }

        var outlineAssetValidationIssue = ValidateOutlineClientAsset(exactTreePath);
        if (outlineAssetValidationIssue is not null)
        {
            return outlineAssetValidationIssue;
        }

        var searchIndexValidationIssue = ValidateSearchIndexPayload(Path.Combine(exactTreePath, "search-index.json"));
        if (searchIndexValidationIssue is not null)
        {
            return searchIndexValidationIssue;
        }

        return null;
    }

    private static AvailabilityFailure? ValidateOutlineClientAsset(string exactTreePath)
    {
        var outlineClientPath = Path.Combine(exactTreePath, "outline-client.js");
        if (File.Exists(outlineClientPath))
        {
            return null;
        }

        foreach (var htmlPath in Directory.EnumerateFiles(exactTreePath, "*.*", ExactTreeHtmlEnumerationOptions))
        {
            if (!IsHtmlFile(htmlPath)
                || !File.ReadAllText(htmlPath).Contains("outline-client.js", StringComparison.Ordinal))
            {
                continue;
            }

            return new AvailabilityFailure(
                PublicMessage: "Published release tree is missing outline-client.js.",
                InternalDetail: $"ExactTreePath '{exactTreePath}' references outline-client.js but the asset is missing.");
        }

        return null;
    }

    private static bool IsHtmlFile(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static AvailabilityFailure? ValidateSearchIndexPayload(string searchIndexPath)
    {
        try
        {
            using var stream = File.OpenRead(searchIndexPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new AvailabilityFailure(
                    PublicMessage: "Published release tree has a search-index.json payload that is not a JSON object.",
                    InternalDetail: $"ExactTreePath '{Path.GetDirectoryName(searchIndexPath)}' has a search-index.json payload that is not a JSON object.");
            }

            if (!document.RootElement.TryGetProperty("documents", out var documents)
                || documents.ValueKind != JsonValueKind.Array)
            {
                return new AvailabilityFailure(
                    PublicMessage: "Published release tree has a search-index.json payload without a documents array.",
                    InternalDetail: $"ExactTreePath '{Path.GetDirectoryName(searchIndexPath)}' has a search-index.json payload without a documents array.");
            }

            foreach (var item in documents.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("path", out var path)
                    || path.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(path.GetString())
                    || !item.TryGetProperty("title", out var title)
                    || title.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(title.GetString()))
                {
                    return new AvailabilityFailure(
                        PublicMessage: "Published release tree has a search-index.json document entry without the required path/title fields.",
                        InternalDetail: $"ExactTreePath '{Path.GetDirectoryName(searchIndexPath)}' has a search-index.json document entry without the required path/title fields.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AvailabilityFailure(
                PublicMessage: "Published release tree has an unreadable search-index.json payload.",
                InternalDetail: $"ExactTreePath '{Path.GetDirectoryName(searchIndexPath)}' has an unreadable search-index.json payload: {ex.Message}");
        }

        return null;
    }
}

/// <summary>
/// Describes how the current host resolved its published-version catalog state.
/// </summary>
/// <remarks>
/// Numeric values are explicit and stable because callers may serialize or persist catalog-resolution state across
/// process boundaries.
/// </remarks>
public enum RazorDocsResolvedVersionCatalogStatus
{
    /// <summary>
    /// Versioning is enabled and the configured catalog resolved successfully enough to describe published releases.
    /// </summary>
    Resolved = 0,

    /// <summary>
    /// Versioning is disabled for the current host.
    /// </summary>
    Disabled = 1,

    /// <summary>
    /// Versioning is enabled, but no catalog path was configured.
    /// </summary>
    EnabledWithoutCatalog = 2,

    /// <summary>
    /// Versioning is enabled and a catalog path was configured, but the catalog could not be loaded into a usable
    /// published-release set.
    /// </summary>
    Unavailable = 3
}

/// <summary>
/// Represents the resolved version catalog used by the current host.
/// </summary>
/// <param name="Status">
/// The high-level catalog resolution state for the current host. This distinguishes successful resolution from the
/// three sentinel states where versioning is disabled, missing a catalog path, or configured but unavailable.
/// </param>
/// <param name="CatalogPath">
/// The catalog path associated with the resolved state. This stays <see langword="null" /> for the
/// <see cref="Disabled" /> and <see cref="EnabledWithoutCatalog" /> sentinels, is typically an absolute filesystem
/// path after successful resolution or file-based unavailability checks, and can remain the normalized configured
/// value when <see cref="Status" /> is <see cref="RazorDocsResolvedVersionCatalogStatus.Unavailable" /> because
/// absolute resolution failed before <see cref="CreateUnavailable(string?)" /> was created.
/// </param>
/// <param name="Versions">
/// The resolved catalog entries in authored catalog order. Entries stay present even when a published tree is
/// unavailable so archive, diagnostics, and fallback experiences can explain the broken release instead of silently
/// hiding it.
/// </param>
/// <param name="RecommendedVersion">
/// The resolved recommended version when one is public and available. This can be <see langword="null" /> when
/// versioning is disabled, no recommendation was configured, the configured identifier did not resolve, or the matching
/// release was hidden or unavailable after validation.
/// </param>
/// <remarks>
/// <para>
/// <see cref="Disabled" /> means the host is running with versioning off, so callers should treat the live docs
/// surface as the only public experience and skip published-release archive UI entirely.
/// </para>
/// <para>
/// <see cref="EnabledWithoutCatalog" /> means versioning was turned on but no catalog path was configured, so callers
/// can still expose the live preview surface but should not expect any published releases to resolve.
/// </para>
/// <para>
/// <see cref="PublicVersions" /> preserves the ordering from <see cref="Versions" /> after filtering by
/// <see cref="RazorDocsVersionVisibility.Public" /> only. Public-but-unavailable releases remain in that list so the
/// archive can surface their degraded status instead of pretending they do not exist.
/// </para>
/// </remarks>
public sealed record RazorDocsResolvedVersionCatalog(
    RazorDocsResolvedVersionCatalogStatus Status,
    string? CatalogPath,
    IReadOnlyList<RazorDocsResolvedVersion> Versions,
    RazorDocsResolvedVersion? RecommendedVersion)
{
    /// <summary>
    /// Gets the sentinel catalog result for hosts where versioning is disabled entirely.
    /// </summary>
    public static RazorDocsResolvedVersionCatalog Disabled { get; } = new(RazorDocsResolvedVersionCatalogStatus.Disabled, null, [], null);

    /// <summary>
    /// Gets the sentinel catalog result for hosts where versioning is enabled but no catalog path was configured.
    /// </summary>
    public static RazorDocsResolvedVersionCatalog EnabledWithoutCatalog { get; } = new(RazorDocsResolvedVersionCatalogStatus.EnabledWithoutCatalog, null, [], null);

    /// <summary>
    /// Gets the public versions that should appear in the archive.
    /// </summary>
    /// <remarks>
    /// This list preserves the authored order from <see cref="Versions" /> after filtering only by
    /// <see cref="RazorDocsVersionVisibility.Public" />. Versions stay in the list even when
    /// <see cref="RazorDocsResolvedVersion.IsAvailable" /> is <see langword="false" /> so archive consumers can show
    /// degraded-release messaging instead of silently dropping known public releases.
    /// </remarks>
    public IReadOnlyList<RazorDocsResolvedVersion> PublicVersions => Versions
        .Where(version => version.Visibility == RazorDocsVersionVisibility.Public)
        .ToList();

    /// <summary>
    /// Creates an enabled catalog result with no available versions because the backing catalog could not be used.
    /// </summary>
    /// <param name="catalogPath">
    /// The resolved catalog path to surface with the unavailable sentinel. This is usually an absolute filesystem path,
    /// but can also be the normalized configured value when resolution failed before an absolute path could be
    /// constructed.
    /// </param>
    /// <returns>An enabled-but-unavailable catalog result.</returns>
    public static RazorDocsResolvedVersionCatalog CreateUnavailable(string? catalogPath)
    {
        return new RazorDocsResolvedVersionCatalog(RazorDocsResolvedVersionCatalogStatus.Unavailable, catalogPath, [], null);
    }
}

/// <summary>
/// Represents one resolved published docs version and its runtime availability.
/// </summary>
/// <param name="Version">The exact published version identifier.</param>
/// <param name="Label">The archive label shown to readers.</param>
/// <param name="Summary">Optional summary copy shown in the archive.</param>
/// <param name="ExactTreePath">The resolved absolute path to the exported exact-version subtree.</param>
/// <param name="ExactRootUrl">The canonical public root URL for the exact version.</param>
/// <param name="SupportState">The support-state badge surfaced in the archive.</param>
/// <param name="Visibility">The archive visibility state.</param>
/// <param name="AdvisoryState">The release-level advisory state.</param>
/// <param name="IsAvailable">Whether the exact-version tree validated successfully.</param>
/// <param name="AvailabilityIssue">
/// The sanitized public-facing availability explanation when the tree is unavailable. Internal logs retain filesystem
/// paths and exception details, but this message is safe to surface in archive UI and reader-facing diagnostics.
/// </param>
public sealed record RazorDocsResolvedVersion(
    string Version,
    string Label,
    string? Summary,
    string? ExactTreePath,
    string ExactRootUrl,
    RazorDocsVersionSupportState SupportState,
    RazorDocsVersionVisibility Visibility,
    RazorDocsVersionAdvisoryState AdvisoryState,
    bool IsAvailable,
    string? AvailabilityIssue);
