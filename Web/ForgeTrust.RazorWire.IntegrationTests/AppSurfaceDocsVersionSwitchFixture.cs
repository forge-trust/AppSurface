using System.Security.Cryptography;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using ForgeTrust.AppSurface.Core;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>
/// Builds two real, pinned docs exports and a local Release publication tree for concept-switch integration tests.
/// </summary>
/// <remarks>
/// The source repositories are checked-in fixtures. Every export, archive copy, publication plan, and Pages staging
/// output is generated below one unique temporary directory; no publication or upload leaves the local test process.
/// </remarks>
public sealed class AppSurfaceDocsVersionSwitchFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _staticPublicationLock = new(1, 1);
    private string? _temporaryRoot;
    private bool _preserveArtifacts;
    private bool _staticPublicationComplete;

    /// <summary>Gets the checked-in source tree for release 1.0.0.</summary>
    public string OlderSourceRoot { get; private set; } = string.Empty;

    /// <summary>Gets the checked-in source tree for release 2.0.0.</summary>
    public string NewerSourceRoot { get; private set; } = string.Empty;

    /// <summary>Gets the accumulated, locally published static Pages tree.</summary>
    public string PagesRoot { get; private set; } = string.Empty;

    /// <summary>Gets the temporary trusted release root used by the real hosted Docs application.</summary>
    public string HostedReleaseRoot { get; private set; } = string.Empty;

    /// <summary>Gets the source exports that remain the immutable inputs to the publication workflow.</summary>
    public IReadOnlyDictionary<string, string> ExportRootsByVersion { get; private set; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets SHA-256 snapshots of all source-export files, captured before Release publication runs.</summary>
    public IReadOnlyDictionary<string, string> OriginalArchiveHashes { get; private set; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the count of ordinary files in the source exports used to bound this correctness fixture.</summary>
    public int ExportedArchiveFileCount { get; private set; }

    /// <summary>Gets the total UTF-8 byte count of all ordinary files in the source exports.</summary>
    public long ExportedArchiveBytes { get; private set; }

    /// <summary>Gets the disposable export/publication directory created by this fixture.</summary>
    public string TemporaryRoot => _temporaryRoot
        ?? throw new InvalidOperationException("The concept-switch fixture has not been initialized.");

    /// <summary>Looks up the frozen canonical route for a concept in one exact exported release.</summary>
    public string GetCanonicalRoutePath(string version, string conceptId, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(conceptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        return FindCanonicalRoute(version, page =>
            string.Equals(page.GetProperty("conceptId").GetString(), conceptId, StringComparison.Ordinal)
            && string.Equals(page.GetProperty("locale").GetString(), locale, StringComparison.Ordinal));
    }

    /// <summary>Looks up the frozen canonical route for a legacy fixture page by its recorded title.</summary>
    public string GetCanonicalRoutePathByTitle(string version, string title, string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        return FindCanonicalRoute(version, page =>
            string.Equals(page.GetProperty("title").GetString(), title, StringComparison.Ordinal)
            && string.Equals(page.GetProperty("locale").GetString(), locale, StringComparison.Ordinal));
    }

    /// <summary>Builds an exact-version reader URL using the frozen physical artifact mapped to a canonical route.</summary>
    public string BuildVersionPageUrl(string origin, string version, string canonicalRoutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(canonicalRoutePath);
        var artifactPath = FindCanonicalArtifactPath(version, canonicalRoutePath);
        var encodedArtifact = string.Join('/', artifactPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return $"{origin.TrimEnd('/')}/docs/v/{Uri.EscapeDataString(version)}/{encodedArtifact}";
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _preserveArtifacts = string.Equals(
            Environment.GetEnvironmentVariable("APPSURFACE_DOCS_VERSION_SWITCH_KEEP_ARTIFACTS"),
            "1",
            StringComparison.Ordinal);
        var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
        var fixtureRoot = TestPathUtils.PathUnder(repositoryRoot, "tests", "fixtures", "concept-switch");
        OlderSourceRoot = TestPathUtils.PathUnder(fixtureRoot, "v1");
        NewerSourceRoot = TestPathUtils.PathUnder(fixtureRoot, "v2");
        _temporaryRoot = TestPathUtils.PathUnder(Path.GetTempPath(), $"appsurface-docs-version-switch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);

        var olderExport = Path.Join(_temporaryRoot, "exports", "1.0.0");
        var newerExport = Path.Join(_temporaryRoot, "exports", "2.0.0");
        await ExportAsync(repositoryRoot, OlderSourceRoot, olderExport);
        await ExportAsync(repositoryRoot, NewerSourceRoot, newerExport);

        ExportRootsByVersion = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["1.0.0"] = olderExport,
            ["2.0.0"] = newerExport
        };
        OriginalArchiveHashes = CaptureArchiveHashes(ExportRootsByVersion.Values);
        ExportedArchiveFileCount = OriginalArchiveHashes.Count;
        ExportedArchiveBytes = ExportRootsByVersion.Values
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Sum(path => new FileInfo(path).Length);

        HostedReleaseRoot = Path.Join(_temporaryRoot, "hosted-releases");
        Directory.CreateDirectory(HostedReleaseRoot);
        CopyDirectory(olderExport, Path.Join(HostedReleaseRoot, "releases", "1.0.0"));
        CopyDirectory(newerExport, Path.Join(HostedReleaseRoot, "releases", "2.0.0"));
        await WritePinnedHostedCatalogAsync();

        AssertOriginalArchiveHashesUnchanged();
    }

    /// <summary>
    /// Runs the real Release publication command for both exact exports and returns its completed derived Pages tree.
    /// </summary>
    /// <remarks>
    /// Static publication is intentionally lazy so hosted browser checks can run while Release's derived serving-tree
    /// implementation is being integrated. The first call publishes 1.0.0, then 2.0.0 over that Pages root, matching
    /// the ordinary two-release accumulation workflow. All output remains under this fixture's temporary directory.
    /// </remarks>
    public async Task<string> EnsureStaticPublicationAsync()
    {
        await _staticPublicationLock.WaitAsync();
        try
        {
            if (_staticPublicationComplete)
            {
                return PagesRoot;
            }

            var repositoryRoot = PathUtils.FindRepositoryRoot(AppContext.BaseDirectory);
            var firstPagesRoot = Path.Join(_temporaryRoot!, "pages-v1");
            await PublishAsync(repositoryRoot, "1.0.0", ExportRootsByVersion["1.0.0"], firstPagesRoot, existingPagesRoot: null);
            PagesRoot = Path.Join(_temporaryRoot!, "pages-v2");
            await PublishAsync(repositoryRoot, "2.0.0", ExportRootsByVersion["2.0.0"], PagesRoot, firstPagesRoot);
            _staticPublicationComplete = true;
            AssertOriginalArchiveHashesUnchanged();
            return PagesRoot;
        }
        finally
        {
            _staticPublicationLock.Release();
        }
    }

    /// <summary>
    /// Creates the additional standalone-host arguments for a hosted two-release browser test.
    /// </summary>
    /// <returns>Arguments that mount the exact catalog created by the real Release publication commands.</returns>
    public IReadOnlyList<string> CreateHostedHostArguments()
    {
        return
        [
            "--AppSurfaceDocs:Source:RepositoryRoot", NewerSourceRoot,
            "--AppSurfaceDocs:Routing:RouteRootPath", "/docs",
            "--AppSurfaceDocs:Routing:DocsRootPath", "/docs/next",
            "--AppSurfaceDocs:Versioning:Enabled", "true",
            "--AppSurfaceDocs:Versioning:CatalogPath", Path.Join(HostedReleaseRoot, "versions.json"),
            "--AppSurfaceDocs:Versioning:TrustedReleaseRootPath", HostedReleaseRoot
        ];
    }

    /// <summary>Rechecks that static publication did not change either exact export used for verification.</summary>
    public void AssertOriginalArchiveHashesUnchanged()
    {
        var current = CaptureArchiveHashes(ExportRootsByVersion.Values);
        if (current.Count != OriginalArchiveHashes.Count)
        {
            throw new InvalidOperationException(
                $"Concept-switch publication changed the source export inventory: expected {OriginalArchiveHashes.Count} files, found {current.Count}.");
        }

        foreach (var (path, expectedHash) in OriginalArchiveHashes)
        {
            if (!current.TryGetValue(path, out var actualHash)
                || !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Concept-switch publication changed an immutable source export: {path}.");
            }
        }
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        if (_temporaryRoot is not null && Directory.Exists(_temporaryRoot) && _preserveArtifacts)
        {
            Console.WriteLine($"Concept-switch browser artifacts preserved at: {_temporaryRoot}");
        }
        else if (_temporaryRoot is not null && Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task ExportAsync(string repositoryRoot, string sourceRoot, string outputRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputRoot)!);
        await RunDotnetAsync(
            repositoryRoot,
            [
                "run", "--project", Path.Join(repositoryRoot, "Cli", "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Cli.csproj"),
                "--no-launch-profile", "--no-restore", "-p:UseSharedCompilation=false", "--", "docs", "export",
                "--repo", sourceRoot,
                "--output", outputRoot,
                "--mode", "cdn",
                "--strict"
            ]);

        var releaseManifestPath = Path.Join(outputRoot, ".appsurface-docs-release-manifest.json");
        if (!File.Exists(releaseManifestPath))
        {
            throw new InvalidOperationException($"The real docs export command did not write its pinned archive manifest: {releaseManifestPath}.");
        }

        if (!File.Exists(Path.Join(outputRoot, ".appsurface-docs-concepts.json")))
        {
            throw new InvalidOperationException($"The real docs export command did not write the concept index: {outputRoot}.");
        }
    }

    private string FindCanonicalRoute(string version, Func<JsonElement, bool> predicate)
    {
        if (!ExportRootsByVersion.TryGetValue(version, out var exportRoot))
        {
            throw new ArgumentException($"The fixture has no export for version '{version}'.", nameof(version));
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Join(exportRoot, ".appsurface-docs-concepts.json")));
        var matches = document.RootElement.GetProperty("pages").EnumerateArray().Where(predicate).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one concept-index route for the requested fixture page in version {version}; found {matches.Length}.");
        }

        return matches[0].GetProperty("canonicalRoutePath").GetString()
            ?? throw new InvalidOperationException($"The requested fixture page in version {version} has no canonical route.");
    }

    private string FindCanonicalArtifactPath(string version, string canonicalRoutePath)
    {
        if (!ExportRootsByVersion.TryGetValue(version, out var exportRoot))
        {
            throw new ArgumentException($"The fixture has no export for version '{version}'.", nameof(version));
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Join(exportRoot, ".appsurface-docs-route-manifest.json")));
        var matches = document.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => string.Equals(entry.GetProperty("canonicalRoutePath").GetString(), canonicalRoutePath, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one frozen artifact mapping for canonical route '{canonicalRoutePath}' in version {version}; found {matches.Length}.");
        }

        return matches[0].GetProperty("canonicalArtifactPath").GetString()
            ?? throw new InvalidOperationException($"The canonical route '{canonicalRoutePath}' in version {version} has no physical artifact path.");
    }

    private async Task PublishAsync(
        string repositoryRoot,
        string version,
        string exactTreeRoot,
        string pagesStagingRoot,
        string? existingPagesRoot)
    {
        var manifestPath = Path.Join(exactTreeRoot, ".appsurface-docs-release-manifest.json");
        var manifestSha256 = await ComputeSha256Async(manifestPath);
        var outputRoot = Path.Join(_temporaryRoot!, "publication", version);
        Directory.CreateDirectory(outputRoot);

        var arguments = new List<string>
        {
            "run", "--project", Path.Join(repositoryRoot, "tools", "ForgeTrust.AppSurface.Release", "ForgeTrust.AppSurface.Release.csproj"),
            "--no-launch-profile", "--no-restore", "-p:UseSharedCompilation=false", "--", "docs-publication",
            "--version", version,
            "--tag", $"v{version}",
            "--docs-exact-tree", exactTreeRoot,
            "--archive-output", Path.Join(outputRoot, $"appsurface-docs-v{version}.tar.gz"),
            "--pages-staging-root", pagesStagingRoot,
            "--plan-output", Path.Join(outputRoot, "plan.json"),
            "--summary-output", Path.Join(outputRoot, "summary.md"),
            "--expected-release-manifest-sha256", manifestSha256
        };
        if (existingPagesRoot is not null)
        {
            arguments.Add("--existing-pages-root");
            arguments.Add(existingPagesRoot);
        }

        await RunDotnetAsync(repositoryRoot, arguments);
    }

    private async Task WritePinnedHostedCatalogAsync()
    {
        var versions = new[]
        {
            new
            {
                version = "2.0.0",
                label = "2.0.0",
                summary = "Fixture release 2.0.0",
                supportState = "Current",
                visibility = "Public",
                advisoryState = "None",
                exactTreePath = "releases/2.0.0",
                releaseManifestSha256 = await ComputeSha256Async(Path.Join(ExportRootsByVersion["2.0.0"], ".appsurface-docs-release-manifest.json"))
            },
            new
            {
                version = "1.0.0",
                label = "1.0.0",
                summary = "Fixture release 1.0.0",
                supportState = "Maintained",
                visibility = "Public",
                advisoryState = "None",
                exactTreePath = "releases/1.0.0",
                releaseManifestSha256 = await ComputeSha256Async(Path.Join(ExportRootsByVersion["1.0.0"], ".appsurface-docs-release-manifest.json"))
            }
        };
        var catalog = new
        {
            schema = "appsurface-docs-version-catalog-v1",
            recommendedVersion = "2.0.0",
            versions
        };
        await File.WriteAllTextAsync(
            Path.Join(HostedReleaseRoot, "versions.json"),
            JsonSerializer.Serialize(catalog, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + Environment.NewLine);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Join(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static async Task RunDotnetAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var command = CliWrap.Cli.Wrap("dotnet")
            .WithArguments(arguments)
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None);
        var result = await command.ExecuteBufferedAsync();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The local concept-switch fixture command failed with exit code {result.ExitCode}.{Environment.NewLine}"
                + $"stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}"
                + $"stderr:{Environment.NewLine}{result.StandardError}");
        }
    }

    private static IReadOnlyDictionary<string, string> CaptureArchiveHashes(IEnumerable<string> roots)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                hashes.Add(Path.GetFullPath(path), ComputeSha256(path));
            }
        }

        return hashes;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
