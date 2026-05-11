using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Generates and verifies the manifest-backed package chooser markdown for the repository.
/// </summary>
/// <remarks>
/// This generator is intentionally repository-aware. It expects the manifest, chooser sidecar,
/// package README links, and release-surface links to resolve to files under the supplied
/// repository root. Callers should validate repository layout drift through <see cref="VerifyAsync"/>
/// in CI whenever package or docs paths change.
/// </remarks>
internal sealed class PackageIndexGenerator
{
    private const string WebPackageId = "ForgeTrust.AppSurface.Web";
    private const string RazorWireCliPackageId = "ForgeTrust.RazorWire.Cli";
    private const string ReleaseHubPath = "releases/README.md";
    private const string UnreleasedPath = "releases/unreleased.md";
    private const string ChangelogPath = "CHANGELOG.md";
    private const string UpgradePolicyPath = "releases/upgrade-policy.md";
    private const string WebExamplePath = "examples/web-app/README.md";

    private readonly PackageProjectScanner _scanner;
    private readonly IProjectMetadataProvider _metadataProvider;
    private readonly PackageManifestLoader _manifestLoader;

    /// <summary>
    /// Creates a generator that discovers candidate projects, loads package metadata, and reads the chooser manifest.
    /// </summary>
    /// <param name="scanner">Project scanner used to discover direct candidate project files under the repository root.</param>
    /// <param name="metadataProvider">Metadata provider that evaluates candidate projects into package metadata.</param>
    /// <param name="manifestLoader">Manifest loader responsible for parsing the chooser manifest.</param>
    internal PackageIndexGenerator(
        PackageProjectScanner scanner,
        IProjectMetadataProvider metadataProvider,
        PackageManifestLoader manifestLoader)
    {
        _scanner = scanner;
        _metadataProvider = metadataProvider;
        _manifestLoader = manifestLoader;
    }

    /// <summary>
    /// Generates chooser markdown and writes it to the configured output path.
    /// </summary>
    /// <param name="request">Generation request describing the repository root, manifest path, and output path.</param>
    /// <param name="cancellationToken">Cancellation token used for manifest loading, metadata evaluation, and file writes.</param>
    /// <returns>A task that completes when the chooser file has been written.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when the repository layout is invalid, required docs are missing, or the manifest cannot be rendered safely.
    /// </exception>
    /// <remarks>
    /// This method creates the output directory when it does not already exist and overwrites the chooser file atomically
    /// from the generated markdown payload.
    /// </remarks>
    internal async Task GenerateToFileAsync(PackageIndexRequest request, CancellationToken cancellationToken = default)
    {
        var markdown = await GenerateAsync(request, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        await File.WriteAllTextAsync(request.OutputPath, markdown, cancellationToken);
    }

    /// <summary>
    /// Generates chooser markdown from the manifest and evaluated project metadata without writing it to disk.
    /// </summary>
    /// <param name="request">Generation request describing the repository root, manifest path, and output path context.</param>
    /// <param name="cancellationToken">Cancellation token used while loading the manifest and project metadata.</param>
    /// <returns>The fully rendered chooser markdown.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when repository layout, manifest content, or linked docs targets do not satisfy the chooser contract.
    /// </exception>
    internal async Task<string> GenerateAsync(PackageIndexRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var manifest = await _manifestLoader.LoadAsync(request.ManifestPath, cancellationToken);
        var candidateProjects = _scanner.DiscoverProjects(request.RepositoryRoot);
        var metadata = await LoadMetadataAsync(request.RepositoryRoot, candidateProjects, cancellationToken);
        var entries = ResolveEntries(request.RepositoryRoot, manifest, candidateProjects, metadata);
        ValidateStaticDocumentationTargets(request.RepositoryRoot);
        return RenderMarkdown(request, entries);
    }

    /// <summary>
    /// Verifies that the checked-in chooser file matches the current repository truth.
    /// </summary>
    /// <param name="request">Verification request describing the repository root, manifest path, and generated chooser file.</param>
    /// <param name="cancellationToken">Cancellation token used while regenerating and reading the existing chooser file.</param>
    /// <returns>A task that completes when verification succeeds.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when the generated chooser is missing or differs from the freshly generated markdown.
    /// </exception>
    internal async Task VerifyAsync(PackageIndexRequest request, CancellationToken cancellationToken = default)
    {
        var expected = await GenerateAsync(request, cancellationToken);
        if (!File.Exists(request.OutputPath))
        {
            throw new PackageIndexException(
                $"Missing generated file '{Path.GetRelativePath(request.RepositoryRoot, request.OutputPath)}'. Run the package index generator.");
        }

        var current = await File.ReadAllTextAsync(request.OutputPath, cancellationToken);
        if (!string.Equals(current, expected, StringComparison.Ordinal))
        {
            throw new PackageIndexException(
                $"Generated file '{Path.GetRelativePath(request.RepositoryRoot, request.OutputPath)}' is stale. Run the package index generator.");
        }
    }

    /// <summary>
    /// Runs release-readiness checks that protect the package manifest from drifting away from publishable packages.
    /// </summary>
    /// <param name="request">Gate request describing the repository root, manifest path, and generated chooser file.</param>
    /// <param name="cancellationToken">Cancellation token used while evaluating package metadata and scanning files.</param>
    /// <returns>A report summarizing the package and source-file surfaces covered by the gate.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when release metadata is missing, package class rules are violated, or stale brand strings remain.
    /// </exception>
    internal async Task<PackageGateReport> RunPackageGateAsync(
        PackageIndexRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var manifest = await _manifestLoader.LoadAsync(request.ManifestPath, cancellationToken);
        var candidateProjects = _scanner.DiscoverProjects(request.RepositoryRoot);
        var metadata = await LoadMetadataAsync(request.RepositoryRoot, candidateProjects, cancellationToken);
        var entries = ResolveEntries(request.RepositoryRoot, manifest, candidateProjects, metadata);
        ValidateStaticDocumentationTargets(request.RepositoryRoot);

        return PackageGateValidator.Validate(request.RepositoryRoot, entries);
    }

    private static void ValidateRequest(PackageIndexRequest request)
    {
        if (!Directory.Exists(request.RepositoryRoot))
        {
            throw new PackageIndexException($"Repository root '{request.RepositoryRoot}' does not exist.");
        }

        if (!File.Exists(request.ManifestPath))
        {
            throw new PackageIndexException(
                $"Manifest '{Path.GetRelativePath(request.RepositoryRoot, request.ManifestPath)}' does not exist.");
        }

        var sidecarPath = Path.Combine(Path.GetDirectoryName(request.OutputPath)!, "README.md.yml");
        if (!File.Exists(sidecarPath))
        {
            throw new PackageIndexException(
                $"Expected paired sidecar '{Path.GetRelativePath(request.RepositoryRoot, sidecarPath)}' to exist beside the generated chooser.");
        }
    }

    private static void ValidateStaticDocumentationTargets(string repositoryRoot)
    {
        ResolveRepositoryFilePath(repositoryRoot, WebExamplePath, "Web example README");
        ResolveRepositoryFilePath(repositoryRoot, ReleaseHubPath, "Release hub");
        ResolveRepositoryFilePath(repositoryRoot, ChangelogPath, "Changelog");
        ResolveRepositoryFilePath(repositoryRoot, UpgradePolicyPath, "Pre-1.0 upgrade policy");
    }

    private async Task<IReadOnlyDictionary<string, PackageProjectMetadata>> LoadMetadataAsync(
        string repositoryRoot,
        IReadOnlyList<string> candidateProjects,
        CancellationToken cancellationToken)
    {
        var metadataByPath = new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in candidateProjects)
        {
            var metadata = await _metadataProvider.GetMetadataAsync(repositoryRoot, projectPath, cancellationToken);
            metadataByPath.Add(projectPath, metadata);
        }

        return metadataByPath;
    }

    internal static IReadOnlyList<ResolvedPackageEntry> ResolveEntries(
        string repositoryRoot,
        PackageManifest manifest,
        IReadOnlyList<string> candidateProjects,
        IReadOnlyDictionary<string, PackageProjectMetadata> metadataByPath)
    {
        var manifestByProject = manifest.Packages
            .GroupBy(entry => entry.Project, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var duplicate in manifestByProject.Where(group => group.Value.Length > 1))
        {
            throw new PackageIndexException($"Manifest declares '{duplicate.Key}' more than once.");
        }

        foreach (var projectPath in candidateProjects)
        {
            if (!manifestByProject.ContainsKey(projectPath))
            {
                throw new PackageIndexException($"Manifest is missing a classification for '{projectPath}'.");
            }
        }

        foreach (var manifestPath in manifestByProject.Keys)
        {
            if (!metadataByPath.ContainsKey(manifestPath))
            {
                throw new PackageIndexException($"Manifest references '{manifestPath}', but the project was not discovered.");
            }
        }

        var knownPackageIds = metadataByPath.Values
            .Select(entry => entry.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resolvedEntries = new List<ResolvedPackageEntry>(manifest.Packages.Count);
        foreach (var manifestEntry in manifest.Packages.OrderBy(entry => entry.Order))
        {
            var metadata = metadataByPath[manifestEntry.Project];
            ValidateManifestEntry(repositoryRoot, manifestEntry, metadata, knownPackageIds);
            resolvedEntries.Add(new ResolvedPackageEntry(manifestEntry, metadata));
        }

        _ = RequireSinglePublicWebEntry(resolvedEntries);

        return resolvedEntries;
    }

    private static void ValidateManifestEntry(
        string repositoryRoot,
        PackageManifestEntry entry,
        PackageProjectMetadata metadata,
        IReadOnlySet<string> knownPackageIds)
    {
        if (entry.Classification == PackageClassification.Public)
        {
            RequireValue(entry.Project, nameof(entry.UseWhen), entry.UseWhen);
            RequireValue(entry.Project, nameof(entry.Includes), entry.Includes);
            RequireValue(entry.Project, nameof(entry.DoesNotInclude), entry.DoesNotInclude);
            RequireValue(entry.Project, nameof(entry.StartHerePath), entry.StartHerePath);
        }
        else
        {
            RequireValue(entry.Project, nameof(entry.Note), entry.Note);
        }

        if (!string.IsNullOrWhiteSpace(entry.StartHerePath))
        {
            ResolveRepositoryFilePath(
                repositoryRoot,
                entry.StartHerePath,
                $"Manifest entry '{entry.Project}' documentation target");
        }

        foreach (var dependency in entry.DependsOn)
        {
            if (!knownPackageIds.Contains(dependency))
            {
                throw new PackageIndexException(
                    $"Manifest entry '{entry.Project}' depends on unknown package id '{dependency}'.");
            }
        }

        ValidatePublishContract(entry, knownPackageIds);
    }

    private static void ValidatePublishContract(PackageManifestEntry entry, IReadOnlySet<string> knownPackageIds)
    {
        if (entry.PublishDecision is null)
        {
            throw new PackageIndexException($"Manifest entry '{entry.Project}' must define 'publish_decision'.");
        }

        if (entry.PublishDecision == PackagePublishDecision.DoNotPublish
            && string.IsNullOrWhiteSpace(entry.PublishReason))
        {
            throw new PackageIndexException($"Manifest entry '{entry.Project}' must define 'publish_reason' when 'publish_decision' is 'do_not_publish'.");
        }

        foreach (var packageId in entry.ExpectedDependencyPackageIds)
        {
            if (string.IsNullOrWhiteSpace(packageId))
            {
                throw new PackageIndexException(
                    $"Manifest entry '{entry.Project}' must define a package id in 'expected_dependency_package_ids'.");
            }

            if (!knownPackageIds.Contains(packageId))
            {
                throw new PackageIndexException(
                    $"Manifest entry '{entry.Project}' expects unknown dependency package id '{packageId}'.");
            }
        }
    }

    private static void RequireValue(string projectPath, string propertyName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PackageIndexException($"Manifest entry '{projectPath}' must define '{propertyName}'.");
        }
    }

    private static ResolvedPackageEntry RequireSinglePublicWebEntry(IEnumerable<ResolvedPackageEntry> entries)
    {
        var webEntries = entries
            .Where(entry => entry.Manifest.Classification == PackageClassification.Public)
            .Where(entry => string.Equals(entry.Metadata.PackageId, WebPackageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (webEntries.Length != 1)
        {
            throw new PackageIndexException($"Manifest must include exactly one public '{WebPackageId}' entry.");
        }

        return webEntries[0];
    }

    private static string RenderMarkdown(PackageIndexRequest request, IReadOnlyList<ResolvedPackageEntry> entries)
    {
        var repositoryRoot = request.RepositoryRoot;
        var publicEntries = entries.Where(entry => entry.Manifest.Classification == PackageClassification.Public).ToArray();
        var supportEntries = entries.Where(entry => entry.Manifest.Classification == PackageClassification.Support).ToArray();
        var proofHostEntries = entries.Where(entry => entry.Manifest.Classification == PackageClassification.ProofHost).ToArray();
        var excludedEntries = entries.Where(entry => entry.Manifest.Classification == PackageClassification.Excluded).ToArray();
        var webEntry = RequireSinglePublicWebEntry(entries);
        var publicTargetFrameworks = publicEntries
            .Select(entry => entry.Metadata.TargetFramework)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var targetFrameworkSummary = publicTargetFrameworks.Length == 1
            ? $"All direct-install packages and tools currently target `{publicTargetFrameworks[0]}`."
            : $"Direct-install packages and tools currently target {string.Join(", ", publicTargetFrameworks.Select(value => $"`{value}`"))}.";

        var builder = new StringBuilder();
        builder.AppendLine("# AppSurface v0.1 package chooser");
        builder.AppendLine();
        builder.AppendLine("> Generated from `packages/package-index.yml` and evaluated project metadata. Do not edit this file by hand.");
        builder.AppendLine();
        builder.AppendLine("AppSurface v0.1 is a coordinated .NET 10 package family. Start with the package that matches the app you're building, then add optional modules only when your app needs them.");
        builder.AppendLine();
        builder.AppendLine($"{targetFrameworkSummary} Library package rows use `dotnet package add`; in .NET 10, `dotnet package add` and `dotnet add package` are equivalent. Tool rows use `dotnet tool install`.");
        builder.AppendLine();
        builder.AppendLine("## Web app");
        builder.AppendLine();
        builder.AppendLine(webEntry.Manifest.UseWhen!);
        builder.AppendLine();
        builder.AppendLine("```bash");
        builder.AppendLine(webEntry.Metadata.InstallCommand);
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine($"What you get: {webEntry.Manifest.Includes}");
        builder.AppendLine();
        builder.AppendLine($"Not included: {webEntry.Manifest.DoesNotInclude}");
        builder.AppendLine();
        builder.AppendLine($"Read next: {FormatMarkdownLink("examples/web-app/README.md", GetRelativeDocPath(request, WebExamplePath))}");
        builder.AppendLine();
        builder.AppendLine("Release and readiness:");
        builder.AppendLine($"- {FormatMarkdownLink("Release hub", GetRelativeDocPath(request, ReleaseHubPath))} keeps the public release story, adoption risk, and policy links in one place.");
        if (File.Exists(Path.Combine(repositoryRoot, UnreleasedPath.Replace('/', Path.DirectorySeparatorChar))))
        {
            builder.AppendLine($"- {FormatMarkdownLink("Unreleased proof artifact", GetRelativeDocPath(request, UnreleasedPath))} shows what is queued for the next coordinated version.");
        }
        else
        {
            builder.AppendLine("- Unreleased proof artifact: Not published yet. This row stays visible so the chooser does not quietly hide missing release-state evidence.");
        }

        builder.AppendLine($"- {FormatMarkdownLink("CHANGELOG.md", GetRelativeDocPath(request, ChangelogPath))} is the compact ledger for tagged and in-flight package changes.");
        builder.AppendLine($"- {FormatMarkdownLink("Pre-1.0 upgrade policy", GetRelativeDocPath(request, UpgradePolicyPath))} explains the current stability contract before `v1.0.0`.");
        builder.AppendLine();
        builder.AppendLine("## Also building...");
        builder.AppendLine();
        foreach (var recipeEntry in publicEntries.Where(entry => !string.IsNullOrWhiteSpace(entry.Manifest.RecipeSummary)
                                                                 && !string.Equals(entry.Metadata.PackageId, WebPackageId, StringComparison.OrdinalIgnoreCase)))
        {
            builder.AppendLine($"- {recipeEntry.Manifest.RecipeSummary}");
        }

        builder.AppendLine();
        builder.AppendLine("## Package matrix");
        builder.AppendLine();
        builder.AppendLine("Swipe to compare package details on narrow screens.");
        builder.AppendLine();
        builder.AppendLine("| Package | Use when | Install | Includes | Does not include | Start here | Release |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var entry in publicEntries)
        {
            builder.Append("| ");
            builder.Append(EscapeTableCell($"`{entry.Metadata.PackageId}`"));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(entry.Manifest.UseWhen!));
            builder.Append(" | ");
            builder.Append(EscapeTableCell($"`{entry.Metadata.InstallCommand}`"));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(entry.Manifest.Includes!));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(entry.Manifest.DoesNotInclude!));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(FormatMarkdownLink(entry.Manifest.StartHereLabel ?? "Package README", GetRelativeDocPath(request, entry.Manifest.StartHerePath!))));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(FormatReleaseCell(request, entry.Manifest)));
            builder.AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("## Support and proof-host surfaces");
        builder.AppendLine();

        if (supportEntries.Length > 0)
        {
            builder.AppendLine("### Support and runtime packages");
            builder.AppendLine();
            foreach (var entry in supportEntries)
            {
                builder.Append("- ");
                builder.Append($"`{entry.Metadata.PackageId}`");
                builder.Append(": ");
                builder.Append(entry.Manifest.Note);
                AppendReleaseSummary(builder, entry.Manifest, request);
                if (!string.IsNullOrWhiteSpace(entry.Manifest.StartHerePath))
                {
                    builder.Append(" Start here: ");
                    builder.Append(FormatMarkdownLink(entry.Manifest.StartHereLabel ?? "README", GetRelativeDocPath(request, entry.Manifest.StartHerePath!)));
                }

                builder.AppendLine();
            }

            builder.AppendLine();
        }

        if (proofHostEntries.Length > 0)
        {
            builder.AppendLine("### Docs and proof hosts");
            builder.AppendLine();
            foreach (var entry in proofHostEntries)
            {
                builder.Append("- ");
                builder.Append($"`{entry.Metadata.PackageId}`");
                builder.Append(": ");
                builder.Append(entry.Manifest.Note);
                AppendReleaseSummary(builder, entry.Manifest, request);
                if (!string.IsNullOrWhiteSpace(entry.Manifest.StartHerePath))
                {
                    builder.Append(" Start here: ");
                    builder.Append(FormatMarkdownLink(entry.Manifest.StartHereLabel ?? "README", GetRelativeDocPath(request, entry.Manifest.StartHerePath!)));
                }

                builder.AppendLine();
            }

            builder.AppendLine();
        }

        if (excludedEntries.Length > 0)
        {
            builder.AppendLine("### Not in the direct-install matrix");
            builder.AppendLine();
            foreach (var entry in excludedEntries)
            {
                builder.Append("- ");
                builder.Append($"`{entry.Metadata.PackageId}`");
                builder.Append(": ");
                builder.Append(entry.Manifest.Note);
                AppendReleaseSummary(builder, entry.Manifest, request);
                builder.AppendLine();
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Maintainer notes");
        builder.AppendLine();
        builder.AppendLine($"- Edit `packages/package-index.yml` when the public package story changes.");
        builder.AppendLine("- Keep `publish_decision` and `expected_dependency_package_ids` in `packages/package-index.yml` aligned with the package artifact workflow so the chooser and release contract share one package source of truth.");
        builder.AppendLine($"- Run `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- generate` after changing package classifications or package READMEs.");
        builder.AppendLine("- Run `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify-packages --package-version 0.0.0-ci.local` before publishing changes that affect package metadata, project references, or Tailwind runtime payloads.");
        builder.AppendLine("- Run `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- gate` before publishing rebrand or release metadata changes.");
        builder.AppendLine("- Keep `packages/README.md.yml` hand-authored so RazorDocs metadata, trust-bar copy, and section placement stay intentional.");

        return NormalizeMarkdownNewlines(builder.ToString()).TrimEnd('\n') + "\n";
    }

    private static string GetRelativeDocPath(PackageIndexRequest request, string repositoryRelativePath)
    {
        var outputDirectory = Path.GetDirectoryName(request.OutputPath)
            ?? throw new PackageIndexException($"Output path '{request.OutputPath}' does not have a parent directory.");
        var targetPath = ResolveRepositoryFilePath(
            request.RepositoryRoot,
            repositoryRelativePath,
            $"Chooser link target '{repositoryRelativePath}'");
        return Path.GetRelativePath(outputDirectory, targetPath)
            .Replace('\\', '/');
    }

    internal static string ResolveRepositoryFilePath(string repositoryRoot, string repositoryRelativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(repositoryRelativePath))
        {
            throw new PackageIndexException($"{description} must define a repository-relative file path.");
        }

        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var resolvedPath = Path.GetFullPath(
            Path.Combine(normalizedRoot, repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        var pathComparison = RepositoryPathComparison;

        if (!string.Equals(resolvedPath, normalizedRoot, pathComparison)
            && !resolvedPath.StartsWith(rootPrefix, pathComparison))
        {
            throw new PackageIndexException(
                $"{description} points outside the repository root: '{repositoryRelativePath}'.");
        }

        if (!File.Exists(resolvedPath))
        {
            throw new PackageIndexException(
                $"{description} points at missing documentation '{repositoryRelativePath}'.");
        }

        return resolvedPath;
    }

    /// <summary>
    /// Gets the path-comparison rule used when enforcing repository-boundary checks for chooser links.
    /// </summary>
    /// <remarks>
    /// Windows paths are treated case-insensitively. Other platforms stay ordinal so chooser validation does not
    /// assume a case-insensitive filesystem on Linux or macOS.
    /// </remarks>
    internal static StringComparison RepositoryPathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizeMarkdownNewlines(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }

    private static string EscapeTableCell(string value)
    {
        return value
            .Replace("\r\n", "<br />", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string FormatMarkdownLink(string label, string relativePath)
    {
        return $"[{label}]({relativePath})";
    }

    private static string FormatReleaseCell(PackageIndexRequest request, PackageManifestEntry entry)
    {
        var parts = new List<string>();
        if (entry.ReleaseStatus != PackageReleaseStatus.Unknown)
        {
            parts.Add(FormatEnumLabel(entry.ReleaseStatus));
        }

        if (entry.CommercialStatus != PackageCommercialStatus.Unknown)
        {
            parts.Add(FormatEnumLabel(entry.CommercialStatus));
        }

        if (!string.IsNullOrWhiteSpace(entry.ReleaseNotesPath))
        {
            var releaseNotesFullPath = Path.Combine(
                request.RepositoryRoot,
                entry.ReleaseNotesPath.Replace('/', Path.DirectorySeparatorChar));
            parts.Add(File.Exists(releaseNotesFullPath)
                ? FormatMarkdownLink("notes", GetRelativeDocPath(request, entry.ReleaseNotesPath))
                : "notes pending");
        }

        return parts.Count == 0 ? "Not declared" : string.Join("<br />", parts);
    }

    private static void AppendReleaseSummary(
        StringBuilder builder,
        PackageManifestEntry entry,
        PackageIndexRequest request)
    {
        var summary = FormatReleaseCell(request, entry);
        if (!string.Equals(summary, "Not declared", StringComparison.Ordinal))
        {
            builder.Append(" Release: ");
            builder.Append(summary.Replace("<br />", "; ", StringComparison.Ordinal));
            builder.Append('.');
        }
    }

    private static string FormatEnumLabel<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        return value.ToString()
            .Aggregate(
                new StringBuilder(),
                (builder, character) =>
                {
                    if (builder.Length > 0 && char.IsUpper(character))
                    {
                        builder.Append(' ');
                    }

                    builder.Append(char.ToLowerInvariant(character));
                    return builder;
                })
            .ToString();
    }
}

/// <summary>
/// Describes one package chooser generation or verification request.
/// </summary>
/// <param name="RepositoryRoot">Absolute repository root that contains the manifest, docs, and project files.</param>
/// <param name="ManifestPath">Absolute path to the chooser manifest file.</param>
/// <param name="OutputPath">Absolute path to the generated chooser markdown file.</param>
internal sealed record PackageIndexRequest(string RepositoryRoot, string ManifestPath, string OutputPath);

/// <summary>
/// Couples one manifest row with the evaluated package metadata used to render the chooser.
/// </summary>
/// <param name="Manifest">The manifest row that provides classification, prose, and docs pointers.</param>
/// <param name="Metadata">The evaluated project metadata that provides package identity and install details.</param>
internal sealed record ResolvedPackageEntry(PackageManifestEntry Manifest, PackageProjectMetadata Metadata);

/// <summary>
/// Summarizes the package gate coverage used by CI and local release checks.
/// </summary>
/// <param name="PackageCount">Number of manifest entries validated by package class and release metadata rules.</param>
/// <param name="ScannedFileCount">Number of source files scanned for stale brand strings.</param>
internal sealed record PackageGateReport(int PackageCount, int ScannedFileCount);

/// <summary>
/// Validates package release metadata, package-class invariants, and stale brand drift before packages are published.
/// </summary>
internal static class PackageGateValidator
{
    private const string AllowlistPath = "rebrand/stale-brand-allowlist.txt";

    /// <summary>
    /// Validates all gate rules against resolved package entries and repository files.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root to scan.</param>
    /// <param name="entries">Resolved package entries from the chooser manifest.</param>
    /// <returns>A compact report describing gate coverage.</returns>
    internal static PackageGateReport Validate(string repositoryRoot, IReadOnlyList<ResolvedPackageEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var entry in entries)
        {
            ValidateReleaseMetadata(repositoryRoot, entry);
            ValidatePackageClass(entry);
        }

        var scannedFiles = StaleBrandScanner.Scan(repositoryRoot);
        return new PackageGateReport(entries.Count, scannedFiles);
    }

    private static void ValidateReleaseMetadata(string repositoryRoot, ResolvedPackageEntry entry)
    {
        if (entry.Manifest.ReleaseStatus == PackageReleaseStatus.Unknown)
        {
            throw new PackageIndexException($"Manifest entry '{entry.Manifest.Project}' must define 'release_status'.");
        }

        if (entry.Manifest.CommercialStatus == PackageCommercialStatus.Unknown)
        {
            throw new PackageIndexException($"Manifest entry '{entry.Manifest.Project}' must define 'commercial_status'.");
        }

        if (string.IsNullOrWhiteSpace(entry.Manifest.ReleaseNotesPath))
        {
            throw new PackageIndexException($"Manifest entry '{entry.Manifest.Project}' must define 'release_notes_path'.");
        }

        PackageIndexGenerator.ResolveRepositoryFilePath(
            repositoryRoot,
            entry.Manifest.ReleaseNotesPath,
            $"Manifest entry '{entry.Manifest.Project}' release_notes_path");

        var expectedReleaseStatus = entry.Manifest.Classification switch
        {
            PackageClassification.Public => PackageReleaseStatus.PublicPreview,
            PackageClassification.Support => PackageReleaseStatus.SupportRuntime,
            PackageClassification.ProofHost => PackageReleaseStatus.ProofHost,
            PackageClassification.Excluded => PackageReleaseStatus.Excluded,
            _ => PackageReleaseStatus.Unknown
        };

        if (entry.Manifest.ReleaseStatus != expectedReleaseStatus)
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Manifest.Project}' has release_status '{entry.Manifest.ReleaseStatus}', but '{entry.Manifest.Classification}' entries must use '{expectedReleaseStatus}'.");
        }

        var expectedCommercialStatus = entry.Manifest.Classification == PackageClassification.Public
            ? PackageCommercialStatus.CommercialReady
            : PackageCommercialStatus.NotApplicable;
        if (entry.Manifest.CommercialStatus != expectedCommercialStatus)
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Manifest.Project}' has commercial_status '{entry.Manifest.CommercialStatus}', but '{entry.Manifest.Classification}' entries must use '{expectedCommercialStatus}'.");
        }
    }

    private static void ValidatePackageClass(ResolvedPackageEntry entry)
    {
        if (entry.Manifest.Classification is PackageClassification.Public or PackageClassification.Support
            && !entry.Metadata.IsPackable)
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Manifest.Project}' is '{entry.Manifest.Classification}' but the project is not packable.");
        }

        if (entry.Manifest.Classification == PackageClassification.Public
            && !string.Equals(entry.Metadata.OutputType, "Library", StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Manifest.Project}' is public but reports output type '{entry.Metadata.OutputType}'. Public direct-install packages must be libraries.");
        }

        if (entry.Manifest.Classification == PackageClassification.Excluded
            && string.Equals(entry.Metadata.PackageId, "ForgeTrust.RazorWire.Cli", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entry.Metadata.OutputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIndexException("ForgeTrust.RazorWire.Cli must remain an executable tool while excluded.");
        }
    }

    private static class StaleBrandScanner
    {
        private static readonly string[] Terms =
        [
            "Run" + "nable",
            "run" + "nable",
            "RUN" + "NABLE"
        ];

        internal static int Scan(string repositoryRoot)
        {
            var allowlist = LoadAllowlist(repositoryRoot);
            var scannedFiles = 0;

            foreach (var filePath in Directory.EnumerateFiles(repositoryRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repositoryRoot, filePath).Replace('\\', '/');
                if (ShouldSkip(relativePath))
                {
                    continue;
                }

                scannedFiles++;
                var content = File.ReadAllText(filePath);
                foreach (var term in Terms)
                {
                    if (!content.Contains(term, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!allowlist.Contains((relativePath, term)))
                    {
                        throw new PackageIndexException(
                            $"Stale brand string '{term}' remains in '{relativePath}'. Rename it or add a dated allowlist entry.");
                    }
                }
            }

            return scannedFiles;
        }

        private static bool ShouldSkip(string relativePath)
        {
            var normalized = "/" + relativePath.ToLowerInvariant();
            var extension = Path.GetExtension(relativePath).ToLowerInvariant();
            return normalized.Contains("/.git/", StringComparison.Ordinal)
                || normalized.Contains("/.gstack/", StringComparison.Ordinal)
                || normalized.Contains("/.agent/", StringComparison.Ordinal)
                || normalized.Contains("/.claude/", StringComparison.Ordinal)
                || normalized.Contains("/.codex/", StringComparison.Ordinal)
                || normalized.Contains("/bin/", StringComparison.Ordinal)
                || normalized.Contains("/obj/", StringComparison.Ordinal)
                || normalized.Contains("/node_modules/", StringComparison.Ordinal)
                || normalized.Contains("/testresults/", StringComparison.Ordinal)
                || normalized.EndsWith("/junit.xml", StringComparison.Ordinal)
                || normalized.EndsWith("/coverage.json", StringComparison.Ordinal)
                || normalized.EndsWith("/coverage.cobertura.xml", StringComparison.Ordinal)
                || !IsTextExtension(extension)
                || string.Equals(relativePath, AllowlistPath, StringComparison.Ordinal);
        }

        private static bool IsTextExtension(string extension)
        {
            return extension is ".cs" or ".cshtml" or ".css" or ".editorconfig" or ".html" or ".js" or ".json"
                or ".csproj" or ".md" or ".mjs" or ".props" or ".razor" or ".sh" or ".slnx" or ".targets"
                or ".txt" or ".xml" or ".yml";
        }

        private static HashSet<(string RelativePath, string Term)> LoadAllowlist(string repositoryRoot)
        {
            var allowlist = new HashSet<(string RelativePath, string Term)>();
            var path = Path.Combine(repositoryRoot, AllowlistPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                throw new PackageIndexException($"Missing stale brand allowlist '{AllowlistPath}'.");
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                var parts = line.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length != 4
                    || string.IsNullOrWhiteSpace(parts[0])
                    || string.IsNullOrWhiteSpace(parts[1])
                    || !DateOnly.TryParse(parts[2], out var expiresOn)
                    || string.IsNullOrWhiteSpace(parts[3]))
                {
                    throw new PackageIndexException(
                        $"Stale brand allowlist entries must use 'path|term|yyyy-mm-dd|reason': {line}");
                }

                if (expiresOn < today)
                {
                    throw new PackageIndexException(
                        $"Stale brand allowlist entry for '{parts[0]}' expired on {expiresOn:yyyy-MM-dd}.");
                }

                allowlist.Add((parts[0].Replace('\\', '/'), parts[1]));
            }

            return allowlist;
        }
    }
}

/// <summary>
/// Represents a package chooser generation or verification failure.
/// </summary>
/// <remarks>
/// These exceptions are written directly to CLI stderr, so messages should stay actionable and user-facing.
/// </remarks>
internal sealed class PackageIndexException : Exception
{
    /// <summary>
    /// Creates a new package chooser exception with an actionable message.
    /// </summary>
    /// <param name="message">User-facing description of the failed chooser precondition or generation step.</param>
    internal PackageIndexException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new package chooser exception with an actionable message and underlying cause.
    /// </summary>
    /// <param name="message">User-facing description of the failed chooser precondition or generation step.</param>
    /// <param name="innerException">Original exception that caused the package chooser failure.</param>
    internal PackageIndexException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Contract for evaluating one discovered project into package metadata suitable for chooser rendering.
/// </summary>
internal interface IProjectMetadataProvider
{
    /// <summary>
    /// Evaluates one project file and returns the package metadata used by the chooser.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root used as the evaluation working directory.</param>
    /// <param name="projectPath">Repository-relative project path for the project being evaluated.</param>
    /// <param name="cancellationToken">Cancellation token that should abort the evaluation when possible.</param>
    /// <returns>The evaluated project metadata for the supplied project.</returns>
    Task<PackageProjectMetadata> GetMetadataAsync(
        string repositoryRoot,
        string projectPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Evaluated package metadata used by the chooser renderer.
/// </summary>
/// <param name="ProjectPath">Repository-relative path to the project that produced this metadata.</param>
/// <param name="PackageId">NuGet package identifier emitted by the project.</param>
/// <param name="TargetFramework">Resolved target framework summary used in chooser copy.</param>
/// <param name="IsPackable">Whether the project reports itself as packable.</param>
/// <param name="IsTool">Whether the project reports itself as a .NET tool package.</param>
/// <param name="OutputType">Resolved output type, such as <c>Library</c> or <c>Exe</c>.</param>
/// <param name="ProjectReferences">Evaluated project reference paths reported by MSBuild.</param>
internal sealed record PackageProjectMetadata(
    string ProjectPath,
    string PackageId,
    string TargetFramework,
    bool IsPackable,
    bool IsTool,
    string OutputType,
    IReadOnlyList<string> ProjectReferences)
{
    /// <summary>
    /// Gets the primary install command shown in the chooser for this package or tool.
    /// </summary>
    internal string InstallCommand => IsTool
        ? $"dotnet tool install --global {PackageId}"
        : $"dotnet package add {PackageId}";
}

/// <summary>
/// Discovers candidate projects that should be classified by the package chooser manifest.
/// </summary>
/// <remarks>
/// The scanner intentionally excludes tests, examples, tooling, and generated directories so the manifest only
/// needs to classify packages that are meaningful to external adopters or package-surface maintainers.
/// </remarks>
internal sealed class PackageProjectScanner
{
    /// <summary>
    /// Enumerates candidate project files under the repository root.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root to scan.</param>
    /// <returns>Repository-relative project paths ordered for stable manifest validation.</returns>
    internal IReadOnlyList<string> DiscoverProjects(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        return Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .Where(IsCandidateProject)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Determines whether a repository-relative project path belongs in the chooser manifest.
    /// </summary>
    /// <param name="relativePath">Repository-relative project path to evaluate.</param>
    /// <returns><c>true</c> when the path should be classified by the chooser manifest; otherwise, <c>false</c>.</returns>
    internal static bool IsCandidateProject(string relativePath)
    {
        var normalizedPath = "/" + relativePath.Replace('\\', '/').Trim('/').ToLowerInvariant();
        var projectName = Path.GetFileNameWithoutExtension(relativePath).ToLowerInvariant();

        if (normalizedPath.Contains("/.git/", StringComparison.Ordinal)
            || normalizedPath.Contains("/.gstack/", StringComparison.Ordinal)
            || normalizedPath.Contains("/.agent/", StringComparison.Ordinal)
            || normalizedPath.Contains("/.claude/", StringComparison.Ordinal)
            || normalizedPath.Contains("/.codex/", StringComparison.Ordinal)
            || normalizedPath.Contains("/bin/", StringComparison.Ordinal)
            || normalizedPath.Contains("/obj/", StringComparison.Ordinal)
            || normalizedPath.Contains("/node_modules/", StringComparison.Ordinal)
            || normalizedPath.Contains("/tools/", StringComparison.Ordinal))
        {
            return false;
        }

        if (normalizedPath.Contains("/examples/", StringComparison.Ordinal)
            || normalizedPath.Contains("/benchmarks/", StringComparison.Ordinal)
            || projectName.Contains("benchmarks", StringComparison.Ordinal))
        {
            return false;
        }

        if (normalizedPath.Contains("/tests/", StringComparison.Ordinal)
            || normalizedPath.Contains(".tests", StringComparison.Ordinal)
            || normalizedPath.Contains("integrationtests", StringComparison.Ordinal)
            || projectName.Contains("tests", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Evaluates project metadata by invoking <c>dotnet msbuild</c> and reading JSON property output.
/// </summary>
/// <remarks>
/// This provider depends on a functioning local .NET SDK and assumes the project can be evaluated from the
/// repository root. Timeouts and malformed output are surfaced as <see cref="PackageIndexException"/> so CLI
/// callers can fail fast in CI.
/// </remarks>
internal sealed class DotNetProjectMetadataProvider : IProjectMetadataProvider
{
    internal const string TargetFrameworksPropertyName = "TargetFrameworks";
    internal const int DefaultProcessTimeoutMilliseconds = 120_000;

    private readonly ICommandRunner _commandRunner;

    /// <summary>
    /// Creates a metadata provider backed by the default process runner.
    /// </summary>
    internal DotNetProjectMetadataProvider()
        : this(new ProcessCommandRunner())
    {
    }

    /// <summary>
    /// Creates a metadata provider backed by the supplied command runner.
    /// </summary>
    /// <param name="commandRunner">External command runner used to invoke <c>dotnet msbuild</c>.</param>
    internal DotNetProjectMetadataProvider(ICommandRunner commandRunner)
    {
        _commandRunner = commandRunner;
    }

    /// <inheritdoc />
    public async Task<PackageProjectMetadata> GetMetadataAsync(
        string repositoryRoot,
        string projectPath,
        CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunAsync(
            new CommandRunRequest(
                "dotnet",
                [
                    "msbuild",
                    projectPath,
                    "-getProperty:PackageId,TargetFramework,TargetFrameworks,IsPackable,PackAsTool,OutputType",
                    "-getItem:ProjectReference"
                ],
                repositoryRoot,
                "dotnet msbuild",
                projectPath,
                "evaluate",
                "evaluating",
                DefaultProcessTimeoutMilliseconds),
            cancellationToken);

        return ParseMetadataJson(projectPath, result.StandardOutput);
    }

    /// <summary>
    /// Parses one <c>dotnet msbuild</c> JSON payload into package metadata.
    /// </summary>
    /// <param name="projectPath">Repository-relative project path used for error reporting.</param>
    /// <param name="standardOutput">Raw JSON payload captured from <c>dotnet msbuild</c>.</param>
    /// <returns>The normalized package metadata derived from the JSON payload.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when the JSON payload is malformed, missing required properties, or reports incomplete metadata.
    /// </exception>
    /// <remarks>
    /// This parsing seam is intentionally internal so tests can verify malformed and incomplete metadata handling
    /// without depending on a real SDK invocation or using reflection against private helpers.
    /// </remarks>
    internal static PackageProjectMetadata ParseMetadataJson(string projectPath, string standardOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(standardOutput);

        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var properties = GetRequiredObjectProperty(document.RootElement, "Properties");

            var packageId = GetRequiredStringProperty(properties, "PackageId");
            var targetFramework = GetRequiredStringProperty(properties, "TargetFramework");
            var targetFrameworks = properties.TryGetProperty(TargetFrameworksPropertyName, out var tfmsElement)
                ? tfmsElement.GetString()
                : null;
            var isPackable = GetRequiredStringProperty(properties, "IsPackable");
            var packAsTool = GetRequiredStringProperty(properties, "PackAsTool");
            var outputType = GetRequiredStringProperty(properties, "OutputType");
            var projectReferences = ReadProjectReferences(document.RootElement);

            var resolvedTargetFramework = !string.IsNullOrWhiteSpace(targetFramework)
                ? targetFramework
                : targetFrameworks;

            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(resolvedTargetFramework) || string.IsNullOrWhiteSpace(outputType))
            {
                throw new PackageIndexException($"dotnet msbuild returned incomplete metadata for '{projectPath}'.");
            }

            return new PackageProjectMetadata(
                projectPath,
                packageId,
                resolvedTargetFramework,
                bool.TryParse(isPackable, out var parsedIsPackable) && parsedIsPackable,
                bool.TryParse(packAsTool, out var parsedPackAsTool) && parsedPackAsTool,
                outputType,
                projectReferences);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new PackageIndexException(
                $"dotnet msbuild returned malformed JSON for '{projectPath}': {ex.Message}");
        }
    }

    private static IReadOnlyList<string> ReadProjectReferences(JsonElement root)
    {
        if (!root.TryGetProperty("Items", out var itemsElement)
            || !itemsElement.TryGetProperty("ProjectReference", out var projectReferencesElement)
            || projectReferencesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return projectReferencesElement.EnumerateArray()
            .Select(element => element.TryGetProperty("FullPath", out var fullPathElement)
                ? fullPathElement.GetString()
                : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray();
    }

    private static JsonElement GetRequiredObjectProperty(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            throw new KeyNotFoundException($"Required property '{propertyName}' was not present.");
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Required property '{propertyName}' must be a JSON object.");
        }

        return property;
    }

    private static string? GetRequiredStringProperty(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            throw new KeyNotFoundException($"Required property '{propertyName}' was not present.");
        }

        return property.GetString();
    }
}

/// <summary>
/// Loads the chooser manifest from YAML into strongly typed manifest models.
/// </summary>
internal sealed class PackageManifestLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>
    /// Reads and parses the chooser manifest file.
    /// </summary>
    /// <param name="manifestPath">Absolute path to the chooser manifest file.</param>
    /// <param name="cancellationToken">Cancellation token used while reading the manifest from disk.</param>
    /// <returns>The parsed chooser manifest.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when the manifest cannot be parsed or does not define any package rows.
    /// </exception>
    internal async Task<PackageManifest> LoadAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        PackageManifest? manifest;
        try
        {
            manifest = _deserializer.Deserialize<PackageManifest>(content);
        }
        catch (YamlException ex)
        {
            throw new PackageIndexException($"Manifest '{manifestPath}' could not be parsed: {ex.Message}");
        }

        if (manifest is null || manifest.Packages.Count == 0)
        {
            throw new PackageIndexException($"Manifest '{manifestPath}' does not define any packages.");
        }

        return manifest;
    }
}

/// <summary>
/// Root manifest model for the chooser YAML file.
/// </summary>
internal sealed class PackageManifest
{
    /// <summary>
    /// Gets the ordered manifest rows that describe each package, support surface, or excluded package entry.
    /// </summary>
    public List<PackageManifestEntry> Packages { get; init; } = [];
}

/// <summary>
/// One manifest row describing how a project should appear in the chooser.
/// </summary>
/// <remarks>
/// Public rows must define install guidance and docs pointers. Non-public rows must define <see cref="Note"/>
/// because their rendered bullets rely on that prose to explain why they are visible but not recommended as
/// first installs.
/// </remarks>
internal sealed class PackageManifestEntry
{
    /// <summary>
    /// Gets the repository-relative project path classified by this manifest entry.
    /// </summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>
    /// Gets the chooser classification that controls which section renders the package.
    /// </summary>
    public PackageClassification Classification { get; init; }

    /// <summary>
    /// Gets the publish decision consumed by the prerelease package artifact workflow.
    /// </summary>
    public PackagePublishDecision? PublishDecision { get; init; }

    /// <summary>
    /// Gets the required maintainer-facing reason for entries that are intentionally not published.
    /// </summary>
    public string? PublishReason { get; init; }

    /// <summary>
    /// Gets the stable display order within the chooser section.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Gets the adopter-focused “use when” guidance for public package rows.
    /// </summary>
    public string? UseWhen { get; init; }

    /// <summary>
    /// Gets the concise statement describing what the package includes for public rows.
    /// </summary>
    public string? Includes { get; init; }

    /// <summary>
    /// Gets the concise statement describing what the package intentionally does not include for public rows.
    /// </summary>
    public string? DoesNotInclude { get; init; }

    /// <summary>
    /// Gets the repository-relative documentation file linked from this chooser row.
    /// </summary>
    public string? StartHerePath { get; init; }

    /// <summary>
    /// Gets the optional chooser label used for the linked documentation target.
    /// </summary>
    public string? StartHereLabel { get; init; }

    /// <summary>
    /// Gets the optional recipe summary shown in the “Also building...” section.
    /// </summary>
    public string? RecipeSummary { get; init; }

    /// <summary>
    /// Gets the explanatory note rendered for non-public package rows.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// Gets the optional package ids that this row depends on for install guidance.
    /// </summary>
    public List<string> DependsOn { get; init; } = [];

    /// <summary>
    /// Gets the exact package ids expected from project-reference dependencies in the produced package.
    /// </summary>
    public List<string> ExpectedDependencyPackageIds { get; init; } = [];

    /// <summary>
    /// Gets the release status expected by package-gate for this package classification.
    /// </summary>
    public PackageReleaseStatus ReleaseStatus { get; init; }

    /// <summary>
    /// Gets whether this package row is part of the commercial-ready public surface.
    /// </summary>
    public PackageCommercialStatus CommercialStatus { get; init; }

    /// <summary>
    /// Gets the repository-relative release notes file that explains current readiness for this package.
    /// </summary>
    public string? ReleaseNotesPath { get; init; }
}

/// <summary>
/// Chooser section classifications for manifest entries.
/// </summary>
internal enum PackageClassification
{
    /// <summary>
    /// A direct-install package that appears in the main package matrix.
    /// </summary>
    Public,

    /// <summary>
    /// A support or runtime package that should stay visible but usually should not be installed directly.
    /// </summary>
    Support,

    /// <summary>
    /// A proof host or docs host that explains supporting surfaces without treating them as the first install path.
    /// </summary>
    ProofHost,

    /// <summary>
    /// A package intentionally omitted from direct-install guidance but still documented for maintainers.
    /// </summary>
    Excluded
}

/// <summary>
/// Publish decisions for package artifact verification and later prerelease publishing.
/// </summary>
internal enum PackagePublishDecision
{
    /// <summary>
    /// A public package that should be packed and eventually published as a direct install surface.
    /// </summary>
    Publish,

    /// <summary>
    /// A support package that should be packed and eventually published for transitive restore.
    /// </summary>
    SupportPublish,

    /// <summary>
    /// A project that should stay visible in the chooser but must not be packed or published.
    /// </summary>
    DoNotPublish
}

/// <summary>
/// Release status values package-gate expects for each chooser classification.
/// </summary>
internal enum PackageReleaseStatus
{
    /// <summary>
    /// Missing or unknown release status; rejected by package-gate.
    /// </summary>
    Unknown,

    /// <summary>
    /// Public preview package that can be installed directly by OSS adopters.
    /// </summary>
    PublicPreview,

    /// <summary>
    /// Support package restored transitively or used by infrastructure packages.
    /// </summary>
    SupportRuntime,

    /// <summary>
    /// Proof-host surface used to validate docs, export, or examples.
    /// </summary>
    ProofHost,

    /// <summary>
    /// Intentionally excluded package that is not part of direct-install guidance.
    /// </summary>
    Excluded
}

/// <summary>
/// Commercial readiness status values used by package-gate.
/// </summary>
internal enum PackageCommercialStatus
{
    /// <summary>
    /// Missing or unknown commercial status; rejected by package-gate.
    /// </summary>
    Unknown,

    /// <summary>
    /// Public package is intended to be available for commercial purchase/support.
    /// </summary>
    CommercialReady,

    /// <summary>
    /// Non-public package row is not itself sold or positioned as the commercial surface.
    /// </summary>
    NotApplicable
}
