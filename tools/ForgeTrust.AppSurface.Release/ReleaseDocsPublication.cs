using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// Creates deterministic AppSurface Docs release archives, version catalogs, Pages staging payloads, and maintainer recovery summaries.
/// </summary>
internal sealed class ReleaseDocsPublication
{
    private const string ReleaseManifestFileName = ".appsurface-docs-release-manifest.json";
    private readonly ReleaseWorkspace _workspace;

    /// <summary>
    /// Creates a docs publication planner for a repository workspace.
    /// </summary>
    /// <param name="workspace">Repository workspace paths.</param>
    internal ReleaseDocsPublication(ReleaseWorkspace workspace)
    {
        _workspace = workspace;
    }

    /// <summary>
    /// Produces the release docs publication plan and all local artifacts the publish workflow transports.
    /// </summary>
    /// <param name="request">Publication request from the release workflow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed publication plan.</returns>
    internal async Task<DocsPublicationPlan> CreateAsync(DocsPublicationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expectedTag = request.Version.TagName;
        if (!string.Equals(request.Tag, expectedTag, StringComparison.Ordinal))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-docs-publication-tag-mismatch",
                "The docs publication tag does not match the requested version.",
                $"`--version {request.Version}` maps to `{expectedTag}`, but `--tag {request.Tag}` was supplied.",
                "Use the same version/tag pair that release-publish validated before exporting docs.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
        }

        ValidateExactTreePath(request.ExactTreePath);
        Directory.CreateDirectory(Path.GetDirectoryName(request.ArchivePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(request.PlanPath)!);
        Directory.CreateDirectory(request.PagesStagingRoot);

        var manifestPath = Path.Join(request.ExactTreePath, ReleaseManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-docs-publication-manifest-missing",
                "The exported docs exact tree is missing its release manifest.",
                $"`{_workspace.DisplayPath(manifestPath)}` was not found.",
                "Run `appsurface docs export` for the tagged commit before planning docs publication.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
        }

        var releaseManifestSha256 = await ComputeFileSha256Async(manifestPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.ExpectedReleaseManifestSha256)
            && !string.Equals(request.ExpectedReleaseManifestSha256, releaseManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-docs-publication-manifest-digest-mismatch",
                "The exported docs manifest does not match release evidence.",
                $"Evidence expected `{request.ExpectedReleaseManifestSha256}`, but the exact tree manifest hashed to `{releaseManifestSha256}`.",
                "Re-export docs from the annotated tag commit and rerun the release publish workflow.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
        }

        await CreateDeterministicTarGzAsync(request.ExactTreePath, request.ArchivePath, cancellationToken);
        var archiveSha256 = await ComputeFileSha256Async(request.ArchivePath, cancellationToken);
        var sha256Path = request.ArchivePath + ".sha256";
        await File.WriteAllTextAsync(sha256Path, $"{archiveSha256}  {Path.GetFileName(request.ArchivePath)}{Environment.NewLine}", cancellationToken);

        ResetDirectory(request.PagesStagingRoot);
        if (!string.IsNullOrWhiteSpace(request.ExistingPagesRoot) && Directory.Exists(request.ExistingPagesRoot))
        {
            CopyDirectory(request.ExistingPagesRoot, request.PagesStagingRoot);
        }

        var exactTreePath = $"releases/{request.Version}";
        ValidateCatalogExactTreePath(exactTreePath);
        var stagedExactTree = Path.Join(request.PagesStagingRoot, "releases", request.Version.ToString());
        CopyDirectory(request.ExactTreePath, stagedExactTree);
        var promotedStable = request.Version.IsStable && request.PromoteRecommended;
        var catalogPath = Path.Join(request.PagesStagingRoot, "versions.json");
        var recommendedVersion = await WriteCatalogAsync(request, catalogPath, exactTreePath, releaseManifestSha256, cancellationToken);

        var summaryPath = request.SummaryPath ?? Path.Join(Path.GetDirectoryName(request.PlanPath)!, "docs-publication-summary.md");
        var plan = new DocsPublicationPlan(
            Schema: "appsurface-docs-publication-plan-v1",
            Version: request.Version.ToString(),
            Tag: request.Tag,
            PlanPath: request.PlanPath,
            ArchiveAssetName: Path.GetFileName(request.ArchivePath),
            ArchivePath: request.ArchivePath,
            ArchiveSha256: archiveSha256,
            Sha256Path: sha256Path,
            ExactTreePath: exactTreePath,
            ReleaseManifestSha256: releaseManifestSha256,
            PagesStagingRoot: request.PagesStagingRoot,
            CatalogPath: catalogPath,
            RecommendedVersion: recommendedVersion,
            CatalogEntry: new DocsPublicationCatalogEntry(
                request.Version.ToString(),
                request.Version.ToString(),
                $"AppSurface {request.Version}",
                promotedStable ? "Current" : "Maintained",
                "Public",
                "None",
                exactTreePath,
                releaseManifestSha256),
            RetryPolicy: new DocsPublicationRetryPolicy(DraftAssetReplaceAllowed: true, PublicAssetReplaceAllowed: false),
            Recovery: new DocsPublicationRecovery(summaryPath));

        await File.WriteAllTextAsync(request.PlanPath, JsonSerializer.Serialize(plan, ReleaseJson.Options) + Environment.NewLine, cancellationToken);
        await WriteRecoverySummaryAsync(plan, cancellationToken);
        return plan;
    }

    /// <summary>
    /// Writes GitHub Actions outputs for the generated publication plan.
    /// </summary>
    /// <param name="plan">Completed docs publication plan.</param>
    /// <param name="githubOutputPath">Optional GitHub Actions output path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task WriteOutputsAsync(DocsPublicationPlan plan, string? githubOutputPath, CancellationToken cancellationToken)
    {
        if (githubOutputPath is null)
        {
            return;
        }

        var outputDirectory = Path.GetDirectoryName(githubOutputPath);
        if (string.IsNullOrEmpty(outputDirectory))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-github-output-path-invalid",
                "The GitHub output path must be a file path, not a root directory.",
                $"`--github-output {githubOutputPath}` does not include a parent directory.",
                "Pass a file path such as `$GITHUB_OUTPUT` or `artifacts/release-output.txt`.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
        }

        Directory.CreateDirectory(outputDirectory);
        var builder = new StringBuilder();
        AppendOutput(builder, "docs_publication_plan", plan.PlanPath);
        AppendOutput(builder, "archive_asset_name", plan.ArchiveAssetName);
        AppendOutput(builder, "archive_path", plan.ArchivePath);
        AppendOutput(builder, "archive_sha256", plan.ArchiveSha256);
        AppendOutput(builder, "sha256_path", plan.Sha256Path);
        AppendOutput(builder, "catalog_path", plan.CatalogPath);
        AppendOutput(builder, "pages_staging_root", plan.PagesStagingRoot);
        AppendOutput(builder, "exact_tree_path", plan.ExactTreePath);
        AppendOutput(builder, "release_manifest_sha256", plan.ReleaseManifestSha256);
        AppendOutput(builder, "recovery_summary_path", plan.Recovery.SummaryPath);
        await File.AppendAllTextAsync(githubOutputPath, builder.ToString(), cancellationToken);
    }

    private static async Task CreateDeterministicTarGzAsync(string sourceDirectory, string archivePath, CancellationToken cancellationToken)
    {
        var tempTarPath = archivePath + ".tar";
        await using (var tarStream = File.Create(tempTarPath))
        await using (var writer = new TarWriter(tarStream, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                         .OrderBy(path => Path.GetRelativePath(sourceDirectory, path).Replace(Path.DirectorySeparatorChar, '/'), StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
                ValidateArchiveEntryPath(relativePath);
                await using var data = File.OpenRead(file);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, relativePath)
                {
                    DataStream = data,
                    ModificationTime = DateTimeOffset.UnixEpoch,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    Uid = 0,
                    Gid = 0,
                    UserName = "root",
                    GroupName = "root"
                };
                await writer.WriteEntryAsync(entry, cancellationToken);
            }
        }

        await using (var input = File.OpenRead(tempTarPath))
        await using (var output = File.Create(archivePath))
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: false))
        {
            await input.CopyToAsync(gzip, cancellationToken);
        }

        File.Delete(tempTarPath);
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string?> WriteCatalogAsync(
        DocsPublicationRequest request,
        string catalogPath,
        string exactTreePath,
        string releaseManifestSha256,
        CancellationToken cancellationToken)
    {
        var versions = new List<JsonObject>();
        string? existingRecommendedVersion = null;
        var existingCatalogPath = !string.IsNullOrWhiteSpace(request.ExistingPagesRoot)
            ? Path.Join(request.ExistingPagesRoot, "versions.json")
            : catalogPath;
        if (File.Exists(existingCatalogPath))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(existingCatalogPath, cancellationToken));
            if (document.RootElement.TryGetProperty("recommendedVersion", out var recommendedVersionProperty)
                && recommendedVersionProperty.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(recommendedVersionProperty.GetString()))
            {
                existingRecommendedVersion = recommendedVersionProperty.GetString();
            }

            if (document.RootElement.TryGetProperty("versions", out var existingVersions) && existingVersions.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in existingVersions.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("version", out var versionProperty)
                        || string.Equals(versionProperty.GetString(), request.Version.ToString(), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    versions.Add(JsonNode.Parse(item.GetRawText())!.AsObject());
                }
            }
        }

        var promotedStable = request.Version.IsStable && request.PromoteRecommended;
        if (promotedStable)
        {
            var highestStable = versions
                .Select(version => version.TryGetPropertyValue("version", out var value) ? value?.GetValue<string>() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => TryParseStable(value!, out var parsed) ? parsed : null)
                .Where(value => value is not null)
                .Cast<SemVer>()
                .OrderByDescending(value => value.Major)
                .ThenByDescending(value => value.Minor)
                .ThenByDescending(value => value.Patch)
                .FirstOrDefault();
            if (highestStable is not null && CompareStable(request.Version, highestStable) < 0)
            {
                throw new ReleaseToolException(ReleaseDiagnostic.Error(
                    "release-docs-publication-recommended-downgrade",
                    "Docs publication would downgrade the public recommended version.",
                    $"Existing public stable `{highestStable}` is newer than `{request.Version}`.",
                    "Publish a newer stable version or perform manual recovery before changing recommendedVersion.",
                    "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
            }
        }

        if (promotedStable)
        {
            foreach (var version in versions.Where(version =>
                         version.TryGetPropertyValue("supportState", out var supportState)
                         && string.Equals(supportState?.GetValue<string>(), "Current", StringComparison.Ordinal)))
            {
                version["supportState"] = "Maintained";
            }
        }

        versions.Add(new JsonObject
        {
            ["version"] = request.Version.ToString(),
            ["label"] = request.Version.ToString(),
            ["summary"] = $"AppSurface {request.Version}",
            ["supportState"] = promotedStable ? "Current" : "Maintained",
            ["visibility"] = "Public",
            ["advisoryState"] = "None",
            ["exactTreePath"] = exactTreePath,
            ["releaseManifestSha256"] = releaseManifestSha256
        });

        versions = versions
            .OrderByDescending(version => StableSortKey(version.TryGetPropertyValue("version", out var value) ? value?.GetValue<string>() : null))
            .ThenBy(version => version.TryGetPropertyValue("version", out var value) ? value?.GetValue<string>() : null, StringComparer.Ordinal)
            .ToList();

        var recommendedVersion = promotedStable ? request.Version.ToString() : existingRecommendedVersion;
        var catalog = new JsonObject
        {
            ["schema"] = "appsurface-docs-version-catalog-v1",
            ["recommendedVersion"] = recommendedVersion,
            ["versions"] = new JsonArray(versions.Select(version => version.DeepClone()).ToArray())
        };
        await File.WriteAllTextAsync(catalogPath, catalog.ToJsonString(ReleaseJson.Options) + Environment.NewLine, cancellationToken);
        return recommendedVersion;
    }

    private static (int Major, int Minor, int Patch) StableSortKey(string? version)
    {
        return TryParseStable(version, out var parsed)
            ? (parsed.Major, parsed.Minor, parsed.Patch)
            : (-1, -1, -1);
    }

    private static bool TryParseStable(string? value, [NotNullWhen(true)] out SemVer? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var parsed = SemVer.Parse(value);
            if (!parsed.IsStable)
            {
                return false;
            }

            version = parsed;
            return true;
        }
        catch (ReleaseToolException)
        {
            return false;
        }
    }

    private static int CompareStable(SemVer left, SemVer right)
    {
        var major = left.Major.CompareTo(right.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = left.Minor.CompareTo(right.Minor);
        return minor != 0 ? minor : left.Patch.CompareTo(right.Patch);
    }

    private static async Task WriteRecoverySummaryAsync(DocsPublicationPlan plan, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Recovery.SummaryPath)!);
        var summary = $"""
            # AppSurface Docs publication recovery

            Version: `{plan.Version}`
            Tag: `{plan.Tag}`
            Archive: `{plan.ArchiveAssetName}`
            Archive SHA-256: `{plan.ArchiveSha256}`
            Release manifest SHA-256: `{plan.ReleaseManifestSha256}`
            Pages catalog: `{plan.CatalogPath}`
            Exact tree: `{plan.ExactTreePath}`

            ## Resume commands

            Asset uploaded, Pages failed:

            ```bash
            gh release view {plan.Tag} --json isDraft,url
            appsurface docs verify-archive --catalog "{plan.CatalogPath}" --version "{plan.Version}" --trusted-release-root "{plan.PagesStagingRoot}"
            ```

            Pages deployed, release publish failed:

            ```bash
            curl -fsSL "$PAGES_URL/versions.json"
            gh release edit {plan.Tag} --draft=false
            ```

            Abort draft:

            ```bash
            gh release delete {plan.Tag} --cleanup-tag=false
            ```
            """;
        await File.WriteAllTextAsync(plan.Recovery.SummaryPath, summary, cancellationToken);
    }

    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Join(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Join(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private void ValidateExactTreePath(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-docs-publication-exact-tree-missing",
                "The docs exact tree does not exist.",
                $"`{path}` is not an ordinary directory.",
                "Export docs from the tag commit before creating a publication plan.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
        }

        if (ReleaseWorkspace.IsUnderPath(_workspace.RepositoryRoot, path))
        {
            var relativePath = Path.GetRelativePath(_workspace.RepositoryRoot, path).Replace('\\', '/');
            if (relativePath.Split("/", StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".." || segment.StartsWith(".", StringComparison.Ordinal)))
            {
                throw new ReleaseToolException(ReleaseDiagnostic.Error(
                    "release-docs-publication-path-unsafe",
                    "Docs publication encountered an unsafe archive path.",
                    $"`{relativePath}` is not a trusted release-root-relative path.",
                    "Use ordinary relative paths without hidden or parent segments.",
                    "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
            }
        }
    }

    private static void ValidateCatalogExactTreePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Split("/", StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".." || segment.StartsWith(".", StringComparison.Ordinal)))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-docs-publication-path-unsafe",
                "Docs publication encountered an unsafe archive path.",
                $"`{path}` is not a trusted release-root-relative path.",
                "Use ordinary relative paths without hidden or parent segments.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
        }
    }

    private static void ValidateArchiveEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Split("/", StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw new ReleaseToolException(ReleaseDiagnostic.Error(
                "release-docs-publication-path-unsafe",
                "Docs publication encountered an unsafe archive path.",
                $"`{path}` is not a safe archive-relative path.",
                "Use ordinary relative paths without parent segments.",
                "tools/ForgeTrust.AppSurface.Release/README.md#docs-publication"));
        }
    }

    private static void AppendOutput(StringBuilder builder, string name, string value)
    {
        builder.AppendLine($"{name}={value}");
    }
}

/// <summary>
/// Request for creating docs publication artifacts.
/// </summary>
internal sealed record DocsPublicationRequest(
    SemVer Version,
    string Tag,
    string ExactTreePath,
    string? ExistingPagesRoot,
    string ArchivePath,
    string PagesStagingRoot,
    string PlanPath,
    string? SummaryPath,
    string? ExpectedReleaseManifestSha256,
    bool PromoteRecommended);

/// <summary>
/// Machine-readable release docs publication plan.
/// </summary>
internal sealed record DocsPublicationPlan(
    string Schema,
    string Version,
    string Tag,
    string PlanPath,
    string ArchiveAssetName,
    string ArchivePath,
    string ArchiveSha256,
    string Sha256Path,
    string ExactTreePath,
    string ReleaseManifestSha256,
    string PagesStagingRoot,
    string CatalogPath,
    string? RecommendedVersion,
    DocsPublicationCatalogEntry CatalogEntry,
    DocsPublicationRetryPolicy RetryPolicy,
    DocsPublicationRecovery Recovery);

/// <summary>
/// Version catalog entry produced for the released docs tree.
/// </summary>
internal sealed record DocsPublicationCatalogEntry(
    string Version,
    string Label,
    string Summary,
    string SupportState,
    string Visibility,
    string AdvisoryState,
    string ExactTreePath,
    string ReleaseManifestSha256);

/// <summary>
/// Draft/public asset retry policy emitted with the publication plan.
/// </summary>
internal sealed record DocsPublicationRetryPolicy(
    [property: JsonPropertyName("draftAssetReplaceAllowed")] bool DraftAssetReplaceAllowed,
    [property: JsonPropertyName("publicAssetReplaceAllowed")] bool PublicAssetReplaceAllowed);

/// <summary>
/// Maintainer recovery summary metadata.
/// </summary>
internal sealed record DocsPublicationRecovery(string SummaryPath);
