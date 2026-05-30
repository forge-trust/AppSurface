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
    private const string V01PreviewPath = "releases/v0.1-preview.md";
    private const string UnreleasedPath = "releases/unreleased.md";
    private const string ChangelogPath = "CHANGELOG.md";
    private const string UpgradePolicyPath = "releases/upgrade-policy.md";
    private const string WebExamplePath = "examples/web-app/README.md";
    private const string RepositoryIssuePrefix = "https://github.com/forge-trust/AppSurface/issues/";
    private const string RepositoryPullPrefix = "https://github.com/forge-trust/AppSurface/pull/";
    private static readonly string[] ProductFamilyValues =
    [
        "appsurface",
        "razorwire",
        "forge_trust",
        "internal_support"
    ];

    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con",
        "prn",
        "aux",
        "nul",
        "clock$",
        "com1",
        "com2",
        "com3",
        "com4",
        "com5",
        "com6",
        "com7",
        "com8",
        "com9",
        "lpt1",
        "lpt2",
        "lpt3",
        "lpt4",
        "lpt5",
        "lpt6",
        "lpt7",
        "lpt8",
        "lpt9"
    };

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
    /// Generates package chooser and readiness dashboard markdown and writes both configured output paths.
    /// </summary>
    /// <param name="request">Generation request describing the repository root, manifest path, chooser output, and readiness output.</param>
    /// <param name="cancellationToken">Cancellation token used for manifest loading, metadata evaluation, and file writes.</param>
    /// <returns>A task that completes when the generated files have been written.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when the repository layout is invalid, required docs are missing, or the manifest cannot be rendered safely.
    /// </exception>
    /// <remarks>
    /// This method creates output directories when they do not already exist and overwrites the generated files from the
    /// generated markdown payloads.
    /// </remarks>
    internal async Task GenerateToFileAsync(PackageIndexRequest request, CancellationToken cancellationToken = default)
    {
        var documents = await GenerateDocumentsAsync(request, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(request.ChooserOutputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(request.ReadinessOutputPath)!);
        await File.WriteAllTextAsync(request.ChooserOutputPath, documents.ChooserMarkdown, cancellationToken);
        await File.WriteAllTextAsync(request.ReadinessOutputPath, documents.ReadinessMarkdown, cancellationToken);
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
        var documents = await GenerateDocumentsAsync(request, cancellationToken);
        return documents.ChooserMarkdown;
    }

    /// <summary>
    /// Generates the package chooser and package-index evidence dashboard without writing either file to disk.
    /// </summary>
    /// <param name="request">Generation request describing repository inputs and output path contexts.</param>
    /// <param name="cancellationToken">Cancellation token used while loading the manifest and project metadata.</param>
    /// <returns>Generated chooser and readiness markdown payloads.</returns>
    internal async Task<PackageIndexDocuments> GenerateDocumentsAsync(
        PackageIndexRequest request,
        CancellationToken cancellationToken = default)
    {
        var entries = await ResolveGenerationEntriesAsync(request, cancellationToken);
        var readiness = PackageReadinessEvaluator.Evaluate(request.RepositoryRoot, entries);
        return new PackageIndexDocuments(
            RenderMarkdown(request, entries),
            RenderReadinessMarkdown(request, entries, readiness));
    }

    /// <summary>
    /// Verifies that the checked-in chooser and readiness dashboard files match the current repository truth.
    /// </summary>
    /// <param name="request">Verification request describing the repository root, manifest path, and generated files.</param>
    /// <param name="cancellationToken">Cancellation token used while regenerating and reading the existing generated files.</param>
    /// <returns>A task that completes when verification succeeds.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when either generated file is missing or differs from freshly generated markdown.
    /// </exception>
    internal async Task VerifyAsync(PackageIndexRequest request, CancellationToken cancellationToken = default)
    {
        var documents = await GenerateDocumentsAsync(request, cancellationToken);
        await VerifyGeneratedFileAsync(
            request.RepositoryRoot,
            request.ChooserOutputPath,
            documents.ChooserMarkdown,
            "package chooser",
            cancellationToken);
        await VerifyGeneratedFileAsync(
            request.RepositoryRoot,
            request.ReadinessOutputPath,
            documents.ReadinessMarkdown,
            "package readiness dashboard",
            cancellationToken);
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
        var entries = await ResolveGenerationEntriesAsync(request, cancellationToken);

        return PackageGateValidator.Validate(request.RepositoryRoot, entries);
    }

    private async Task<IReadOnlyList<ResolvedPackageEntry>> ResolveGenerationEntriesAsync(
        PackageIndexRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var manifest = await _manifestLoader.LoadAsync(request.ManifestPath, cancellationToken);
        var candidateProjects = _scanner.DiscoverProjects(request.RepositoryRoot);
        var metadata = await LoadMetadataAsync(request.RepositoryRoot, candidateProjects, cancellationToken);
        var entries = ResolveEntries(request.RepositoryRoot, manifest, candidateProjects, metadata);
        ValidateStaticDocumentationTargets(request.RepositoryRoot);
        return entries;
    }

    private static async Task VerifyGeneratedFileAsync(
        string repositoryRoot,
        string outputPath,
        string expected,
        string documentLabel,
        CancellationToken cancellationToken)
    {
        var displayPath = Path.GetRelativePath(repositoryRoot, outputPath).Replace('\\', '/');
        if (!File.Exists(outputPath))
        {
            throw new PackageIndexException(
                $"Missing generated {documentLabel} '{displayPath}'. Run the package index generator.");
        }

        var current = await File.ReadAllTextAsync(outputPath, cancellationToken);
        if (!string.Equals(current, expected, StringComparison.Ordinal))
        {
            throw new PackageIndexException(
                $"Generated {documentLabel} '{displayPath}' is stale. Run the package index generator.");
        }
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

        var sidecarPath = Path.Join(Path.GetDirectoryName(request.ChooserOutputPath)!, "README.md.yml");
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
        ValidateProductFamily(entry);
        ValidateReadinessAnnotations(entry);

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

        ValidateToolCommandName(entry, metadata);
        ValidatePublishContract(entry, knownPackageIds);
    }

    private static void ValidateProductFamily(PackageManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ProductFamily))
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Project}' must define 'product_family'. Allowed values: {string.Join(", ", ProductFamilyValues)}.");
        }

        if (!ProductFamilyValues.Contains(entry.ProductFamily, StringComparer.Ordinal))
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Project}' has product_family '{entry.ProductFamily}'. Allowed values: {string.Join(", ", ProductFamilyValues)}. Choose the family that owns this package surface.");
        }
    }

    private static void ValidateReadinessAnnotations(PackageManifestEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ReadinessBlocker)
            && !IsValidReadinessBlocker(entry.ReadinessBlocker))
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Project}' has invalid readiness_blocker '{entry.ReadinessBlocker}'. Use '#123' or a same-repository GitHub issue/PR URL under forge-trust/AppSurface.");
        }

        if (!string.IsNullOrWhiteSpace(entry.ReadinessNote)
            && entry.ReadinessNote.Any(IsUnsafeReadinessNoteCharacter))
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Project}' has invalid readiness_note. Notes must be plain text without raw HTML angle brackets or control characters.");
        }
    }

    private static bool IsValidReadinessBlocker(string blocker)
    {
        var trimmed = blocker.Trim();
        if (trimmed.Length > 1
            && trimmed[0] == '#'
            && trimmed.Skip(1).All(char.IsAsciiDigit))
        {
            return true;
        }

        return IsSameRepositoryIssueOrPullRequestUrl(trimmed, RepositoryIssuePrefix)
            || IsSameRepositoryIssueOrPullRequestUrl(trimmed, RepositoryPullPrefix);
    }

    private static bool IsSameRepositoryIssueOrPullRequestUrl(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = value[prefix.Length..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
    }

    private static bool IsUnsafeReadinessNoteCharacter(char value)
    {
        return char.IsControl(value) || value is '<' or '>';
    }

    /// <summary>
    /// Validates the manifest command-name contract against project metadata for one package row.
    /// </summary>
    /// <param name="entry">Manifest row that may declare <c>tool_command_name</c> and a publish decision.</param>
    /// <param name="metadata">Evaluated project metadata that reports whether the project packs as a .NET tool.</param>
    /// <exception cref="PackageIndexException">
    /// Thrown when a tool selected for <see cref="PackagePublishDecision.Publish"/> or
    /// <see cref="PackagePublishDecision.SupportPublish"/> omits or mis-shapes <c>tool_command_name</c>, or when a
    /// non-tool project declares a command name despite not setting <c>PackAsTool=true</c>.
    /// </exception>
    private static void ValidateToolCommandName(PackageManifestEntry entry, PackageProjectMetadata metadata)
    {
        if (metadata.IsTool
            && entry.PublishDecision is PackagePublishDecision.Publish or PackagePublishDecision.SupportPublish)
        {
            RequireValue(entry.Project, "tool_command_name", entry.ToolCommandName);
            ValidateToolCommandNameValue(entry.Project, entry.ToolCommandName!);
            return;
        }

        if (metadata.IsTool
            && !string.IsNullOrWhiteSpace(entry.ToolCommandName))
        {
            ValidateToolCommandNameValue(entry.Project, entry.ToolCommandName);
            return;
        }

        if (!metadata.IsTool
            && !string.IsNullOrWhiteSpace(entry.ToolCommandName))
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Project}' defines 'tool_command_name' but the project does not report PackAsTool=true.");
        }
    }

    /// <summary>
    /// Validates the manifest-declared tool command token for one project.
    /// </summary>
    /// <param name="projectPath">Repository-relative project path used in actionable error messages.</param>
    /// <param name="commandName">Command token read from <c>tool_command_name</c>.</param>
    /// <exception cref="PackageIndexException">
    /// Thrown when the command token is missing or uses a value that cannot safely resolve to one command shim file.
    /// </exception>
    /// <remarks>
    /// The value must be a single file-name-safe token. It must not be blank, <c>.</c>, <c>..</c>,
    /// a Windows reserved device name or dotted alias such as <c>con.txt</c>, end with a period, or contain
    /// whitespace, path separators, control characters, or portable file-name-invalid characters.
    /// </remarks>
    internal static void ValidateToolCommandNameValue(string projectPath, string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            throw new PackageIndexException($"Tool command name ('tool_command_name') for '{projectPath}' must be provided.");
        }

        if (IsWindowsReservedDeviceName(commandName))
        {
            throw new PackageIndexException(
                $"Tool command name ('tool_command_name') for '{projectPath}' is invalid: '{commandName}'. Tool command names must not use Windows reserved device names or dotted aliases.");
        }

        if (string.Equals(commandName, ".", StringComparison.Ordinal)
            || string.Equals(commandName, "..", StringComparison.Ordinal)
            || commandName.EndsWith(".", StringComparison.Ordinal)
            || commandName.Any(IsInvalidToolCommandNameCharacter))
        {
            throw new PackageIndexException(
                $"Tool command name ('tool_command_name') for '{projectPath}' is invalid: '{commandName}'. Tool command names must not be reserved path segments, Windows reserved device names or dotted aliases, end with a period, or contain whitespace, path separators, control characters, or characters invalid in file names.");
        }
    }

    private static bool IsInvalidToolCommandNameCharacter(char value)
    {
        return char.IsWhiteSpace(value)
            || char.IsControl(value)
            || value is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*';
    }

    private static bool IsWindowsReservedDeviceName(string commandName)
    {
        var extensionStart = commandName.IndexOf('.', StringComparison.Ordinal);
        var deviceName = extensionStart < 0 ? commandName : commandName[..extensionStart];
        return ReservedWindowsDeviceNames.Contains(deviceName);
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
        var currentReleaseNotePath = GetSharedPublicReleaseNotePath(publicEntries);
        if (!string.IsNullOrWhiteSpace(currentReleaseNotePath)
            && !string.Equals(currentReleaseNotePath, UnreleasedPath, StringComparison.Ordinal)
            && RepositoryFileExists(repositoryRoot, currentReleaseNotePath))
        {
            if (string.Equals(currentReleaseNotePath, V01PreviewPath, StringComparison.Ordinal))
            {
                builder.AppendLine($"- {FormatMarkdownLink("v0.1.0 Release Preview", GetRelativeDocPath(request, V01PreviewPath))} is the consumer-facing story for the first coordinated release. It stays provisional until the tag is cut.");
            }
            else
            {
                var label = $"{Path.GetFileNameWithoutExtension(currentReleaseNotePath)} release note";
                builder.AppendLine($"- {FormatMarkdownLink(label, GetRelativeDocPath(request, currentReleaseNotePath))} is the current package-facing story for this coordinated release.");
            }
        }
        else if (RepositoryFileExists(repositoryRoot, V01PreviewPath))
        {
            builder.AppendLine($"- {FormatMarkdownLink("v0.1.0 Release Preview", GetRelativeDocPath(request, V01PreviewPath))} is the consumer-facing story for the first coordinated release. It stays provisional until the tag is cut.");
        }

        if (RepositoryFileExists(repositoryRoot, UnreleasedPath))
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
        builder.AppendLine($"- Review {FormatMarkdownLink("package readiness evidence", GetRelativeOutputPath(request.ChooserOutputPath, request.ReadinessOutputPath))} when deciding whether the package manifest, release metadata, blockers, and dependency evidence are ready for release review.");
        builder.AppendLine("- Keep `publish_decision` and `expected_dependency_package_ids` in `packages/package-index.yml` aligned with the package artifact workflow so the chooser and release contract share one package source of truth.");
        builder.AppendLine("- Keep `tool_command_name` aligned with each public .NET tool project's `ToolCommandName` so package validation and post-publish smoke tests run the command users will type. The value must be one file-name-safe command token, not a path: no whitespace, path separators, reserved `.`/`..` segments, trailing periods, Windows reserved device names or dotted aliases, control characters, or Windows-invalid file-name characters.");
        builder.AppendLine($"- Run `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- generate` after changing package classifications, package READMEs, product families, readiness blockers, or readiness notes.");
        builder.AppendLine("- Run `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify-packages --package-version 0.0.0-ci.local` before publishing changes that affect package metadata, project references, or Tailwind runtime payloads.");
        builder.AppendLine("- Run `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- gate` before publishing rebrand or release metadata changes.");
        builder.AppendLine("- Keep `packages/README.md.yml` hand-authored so AppSurface Docs metadata, trust-bar copy, and section placement stay intentional.");

        return NormalizeMarkdownNewlines(builder.ToString()).TrimEnd('\n') + "\n";
    }

    private static string RenderReadinessMarkdown(
        PackageIndexRequest request,
        IReadOnlyList<ResolvedPackageEntry> entries,
        IReadOnlyList<PackageReadinessEvidence> readiness)
    {
        var readinessByProject = readiness.ToDictionary(item => item.ProjectPath, StringComparer.OrdinalIgnoreCase);
        var statusCounts = readiness
            .GroupBy(item => item.Status)
            .OrderBy(group => group.Key)
            .Select(group => $"{FormatReadinessStatus(group.Key)}: {group.Count()}")
            .ToArray();
        var familyCounts = entries
            .GroupBy(entry => entry.Manifest.ProductFamily!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{FormatProductFamily(group.Key)}: {group.Count()}")
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("# Package readiness evidence");
        builder.AppendLine();
        builder.AppendLine("> Generated from `packages/package-index.yml`, evaluated project metadata, and package-index validation evidence. Do not edit this file by hand.");
        builder.AppendLine();
        builder.AppendLine("This dashboard is a maintainer review surface for package-index evidence. It is not a live NuGet publish, artifact, or smoke-install status board; use the release cockpit and protected package workflows for per-version publish proof.");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Packages: {entries.Count}");
        builder.AppendLine($"- Evidence status: {string.Join("; ", statusCounts)}");
        builder.AppendLine($"- Product families: {string.Join("; ", familyCounts)}");
        builder.AppendLine();
        builder.AppendLine("## Package evidence matrix");
        builder.AppendLine();
        builder.AppendLine("| Family | Package | Classification | Publish decision | Package-index evidence | Blockers and notes | Conceptual deps | Expected package deps | Start here | Release notes |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (var entry in entries)
        {
            var evidence = readinessByProject[entry.Manifest.Project];
            builder.Append("| ");
            builder.Append(EscapeTableCell(FormatProductFamily(entry.Manifest.ProductFamily!)));
            builder.Append(" | ");
            builder.Append(EscapeTableCell($"`{entry.Metadata.PackageId}`"));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(FormatEnumLabel(entry.Manifest.Classification)));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(entry.Manifest.PublishDecision is null ? "Not declared" : FormatEnumLabel(entry.Manifest.PublishDecision.Value)));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(FormatReadinessStatus(evidence.Status)));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(FormatBlockersAndNotes(entry.Manifest, evidence)));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(FormatPackageIds(entry.Manifest.DependsOn)));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(FormatPackageIds(entry.Manifest.ExpectedDependencyPackageIds)));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(FormatStartHereCell(request, entry.Manifest, request.ReadinessOutputPath)));
            builder.Append(" | ");
            builder.Append(EscapeTableCell(FormatReleaseNotesCell(request, entry.Manifest, request.ReadinessOutputPath)));
            builder.AppendLine(" |");
        }

        var blocked = readiness.Where(entry => entry.Status == PackageReadinessStatus.Blocked).ToArray();
        if (blocked.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Blocking evidence");
            builder.AppendLine();
            foreach (var item in blocked)
            {
                builder.AppendLine($"- `{item.PackageId}`: {string.Join("; ", item.BlockingReasons)} Fix: {string.Join("; ", item.FixHints)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Maintainer workflow");
        builder.AppendLine();
        builder.AppendLine("- Edit `packages/package-index.yml` for product family, publish decision, release metadata, dependency expectations, blockers, and notes.");
        builder.AppendLine("- Use `readiness_blocker` only for same-repository ownership. When the underlying blocker is external, create a local tracking issue and put the external context in `readiness_note`.");
        builder.AppendLine("- Run `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- generate` to refresh this dashboard and the adopter-facing package chooser.");
        builder.AppendLine("- Run `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify` before review to confirm both generated files are current.");
        builder.AppendLine("- Run `dotnet run --project tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj -- verify-packages --package-version 0.0.0-ci.local` for package artifact proof; this dashboard does not replace that workflow.");

        return NormalizeMarkdownNewlines(builder.ToString()).TrimEnd('\n') + "\n";
    }

    private static string FormatBlockersAndNotes(
        PackageManifestEntry entry,
        PackageReadinessEvidence evidence)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.ReadinessBlocker))
        {
            parts.Add($"Blocker: {entry.ReadinessBlocker}");
        }

        if (!string.IsNullOrWhiteSpace(entry.ReadinessNote))
        {
            parts.Add($"Note: {entry.ReadinessNote}");
        }

        parts.AddRange(evidence.BlockingReasons);
        return parts.Count == 0 ? "None" : string.Join("<br />", parts);
    }

    private static string FormatPackageIds(IReadOnlyList<string> packageIds)
    {
        return packageIds.Count == 0
            ? "None"
            : string.Join("<br />", packageIds.Select(packageId => $"`{packageId}`"));
    }

    private static string FormatStartHereCell(
        PackageIndexRequest request,
        PackageManifestEntry entry,
        string outputPath)
    {
        return string.IsNullOrWhiteSpace(entry.StartHerePath)
            ? "Not applicable"
            : FormatMarkdownLink(entry.StartHereLabel ?? "README", GetRelativeDocPath(request, entry.StartHerePath, outputPath));
    }

    private static string FormatReleaseNotesCell(
        PackageIndexRequest request,
        PackageManifestEntry entry,
        string outputPath)
    {
        return string.IsNullOrWhiteSpace(entry.ReleaseNotesPath)
            ? "Not declared"
            : FormatMarkdownLink("notes", GetRelativeDocPath(request, entry.ReleaseNotesPath, outputPath));
    }

    private static string FormatReadinessStatus(PackageReadinessStatus status)
    {
        return status switch
        {
            PackageReadinessStatus.ManifestReady => "manifest evidence complete",
            PackageReadinessStatus.TransitiveReady => "transitive package evidence complete",
            PackageReadinessStatus.ProofReady => "proof-host evidence complete",
            PackageReadinessStatus.Excluded => "excluded by publish decision",
            PackageReadinessStatus.Blocked => "blocked",
            _ => status.ToString()
        };
    }

    private static string FormatProductFamily(string productFamily)
    {
        return productFamily switch
        {
            "appsurface" => "AppSurface",
            "razorwire" => "RazorWire",
            "forge_trust" => "Forge Trust",
            "internal_support" => "Internal support",
            _ => productFamily.Replace('_', ' ')
        };
    }

    /// <summary>
    /// Finds the release note all publishable public package rows currently share.
    /// </summary>
    /// <param name="publicEntries">Public chooser rows resolved from the package manifest.</param>
    /// <returns>The shared release-note path, or <see langword="null" /> when rows point at mixed or missing release notes.</returns>
    private static string? GetSharedPublicReleaseNotePath(IReadOnlyList<ResolvedPackageEntry> publicEntries)
    {
        var releaseNotePaths = publicEntries
            .Where(entry => entry.Manifest.PublishDecision == PackagePublishDecision.Publish)
            .Select(entry => entry.Manifest.ReleaseNotesPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return releaseNotePaths.Length == 1 ? releaseNotePaths[0] : null;
    }

    private static string GetRelativeDocPath(PackageIndexRequest request, string repositoryRelativePath)
    {
        return GetRelativeDocPath(request, repositoryRelativePath, request.ChooserOutputPath);
    }

    private static string GetRelativeDocPath(PackageIndexRequest request, string repositoryRelativePath, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new PackageIndexException($"Output path '{outputPath}' does not have a parent directory.");
        var targetPath = ResolveRepositoryFilePath(
            request.RepositoryRoot,
            repositoryRelativePath,
            $"Chooser link target '{repositoryRelativePath}'");
        return Path.GetRelativePath(outputDirectory, targetPath)
            .Replace('\\', '/');
    }

    private static string GetRelativeOutputPath(string fromOutputPath, string toOutputPath)
    {
        var outputDirectory = Path.GetDirectoryName(fromOutputPath)
            ?? throw new PackageIndexException($"Output path '{fromOutputPath}' does not have a parent directory.");

        return Path.GetRelativePath(outputDirectory, toOutputPath)
            .Replace('\\', '/');
    }

    /// <summary>
    /// Checks whether a known repository-relative chooser support file exists under the repository root.
    /// </summary>
    /// <param name="repositoryRoot">Repository root used as the path-resolution base; relative roots are normalized with <see cref="Path.GetFullPath(string)" />.</param>
    /// <param name="repositoryRelativePath">Repository-relative file path, conventionally using <c>/</c> separators.</param>
    /// <returns><c>true</c> when the normalized file exists; otherwise, <c>false</c>.</returns>
    /// <remarks>
    /// Use this only for trusted, optional chooser links. It normalizes separators and resolves <c>..</c> segments before
    /// checking existence, but missing files are not errors. Manifest-supplied or required documentation targets should
    /// use <see cref="ResolveRepositoryFilePath" /> so rooted, escaping, or missing paths fail loudly.
    /// </remarks>
    private static bool RepositoryFileExists(string repositoryRoot, string repositoryRelativePath)
    {
        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var normalizedRelativePath = repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar);
        return File.Exists(Path.GetFullPath(normalizedRelativePath, normalizedRoot));
    }

    /// <summary>
    /// Resolves a required repository-relative documentation target and validates that it exists under the repository root.
    /// </summary>
    /// <param name="repositoryRoot">Repository root used as the resolution boundary; relative roots are normalized with <see cref="Path.GetFullPath(string)" />.</param>
    /// <param name="repositoryRelativePath">Repository-relative file path supplied by chooser metadata, conventionally using <c>/</c> separators.</param>
    /// <param name="description">Human-readable label included in validation errors.</param>
    /// <returns>The normalized absolute path to the validated file.</returns>
    /// <exception cref="PackageIndexException">
    /// Thrown when <paramref name="repositoryRelativePath" /> is blank, rooted, escapes the repository root after
    /// normalization, or points at missing documentation.
    /// </exception>
    /// <remarks>
    /// The resolver replaces <c>/</c> with <see cref="Path.DirectorySeparatorChar" /> and normalizes <c>..</c> segments.
    /// Callers should keep inputs repository-relative and avoid leading separators, because absolute-looking paths are
    /// rejected before they can bypass the repository boundary.
    /// </remarks>
    internal static string ResolveRepositoryFilePath(string repositoryRoot, string repositoryRelativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(repositoryRelativePath))
        {
            throw new PackageIndexException($"{description} must define a repository-relative file path.");
        }

        var normalizedRoot = Path.GetFullPath(repositoryRoot);
        var normalizedRelativePath = repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath))
        {
            throw new PackageIndexException(
                $"{description} must be repository-relative: '{repositoryRelativePath}'.");
        }

        var resolvedPath = Path.GetFullPath(normalizedRelativePath, normalizedRoot);
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
            parts.Add(FormatMarkdownLink("notes", GetRelativeDocPath(request, entry.ReleaseNotesPath)));
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
/// Describes one package chooser and readiness dashboard generation or verification request.
/// </summary>
/// <param name="RepositoryRoot">Absolute repository root that contains the manifest, docs, and project files.</param>
/// <param name="ManifestPath">Absolute path to the chooser manifest file.</param>
/// <param name="ChooserOutputPath">Absolute path to the generated chooser markdown file.</param>
/// <param name="ReadinessOutputPath">Absolute path to the generated package readiness dashboard markdown file.</param>
internal sealed record PackageIndexRequest(
    string RepositoryRoot,
    string ManifestPath,
    string ChooserOutputPath,
    string ReadinessOutputPath)
{
    /// <summary>
    /// Creates a request with the readiness dashboard beside the chooser output.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root that contains the manifest, docs, and project files.</param>
    /// <param name="manifestPath">Absolute path to the chooser manifest file.</param>
    /// <param name="outputPath">Absolute path to the generated chooser markdown file.</param>
    internal PackageIndexRequest(string repositoryRoot, string manifestPath, string outputPath)
        : this(
            repositoryRoot,
            manifestPath,
            outputPath,
            Path.Join(GetOutputDirectory(outputPath), "readiness.md"))
    {
    }

    /// <summary>
    /// Gets the chooser output path retained for compatibility with existing tests and callers.
    /// </summary>
    internal string OutputPath => ChooserOutputPath;

    private static string GetOutputDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        return string.IsNullOrEmpty(directory) ? "." : directory;
    }
}

/// <summary>
/// Generated markdown documents produced from one package index resolution pass.
/// </summary>
/// <param name="ChooserMarkdown">Generated adopter-facing package chooser markdown.</param>
/// <param name="ReadinessMarkdown">Generated maintainer-facing package readiness evidence markdown.</param>
internal sealed record PackageIndexDocuments(string ChooserMarkdown, string ReadinessMarkdown);

/// <summary>
/// Couples one manifest row with the evaluated package metadata used to render the chooser.
/// </summary>
/// <param name="Manifest">The manifest row that provides classification, prose, and docs pointers.</param>
/// <param name="Metadata">The evaluated project metadata that provides package identity and install details.</param>
internal sealed record ResolvedPackageEntry(PackageManifestEntry Manifest, PackageProjectMetadata Metadata);

/// <summary>
/// Computes package-index readiness evidence for the maintainer dashboard without treating the dashboard as live
/// publish proof.
/// </summary>
internal static class PackageReadinessEvaluator
{
    /// <summary>
    /// Evaluates package-index evidence for resolved package rows.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root used to validate release-note paths.</param>
    /// <param name="entries">Resolved package rows.</param>
    /// <returns>Per-package readiness evidence with blocking reasons and fix hints.</returns>
    internal static IReadOnlyList<PackageReadinessEvidence> Evaluate(
        string repositoryRoot,
        IReadOnlyList<ResolvedPackageEntry> entries)
    {
        var metadataByProjectPath = entries.ToDictionary(
            entry => NormalizeRepositoryPath(entry.Manifest.Project),
            entry => entry.Metadata,
            StringComparer.OrdinalIgnoreCase);

        return entries
            .Select(entry => EvaluateEntry(repositoryRoot, entry, metadataByProjectPath))
            .ToArray();
    }

    private static PackageReadinessEvidence EvaluateEntry(
        string repositoryRoot,
        ResolvedPackageEntry entry,
        IReadOnlyDictionary<string, PackageProjectMetadata> metadataByProjectPath)
    {
        var blockingReasons = new List<string>();
        var fixHints = new List<string>();
        var evidence = new List<string>();

        AddReleaseMetadataEvidence(repositoryRoot, entry, evidence, blockingReasons, fixHints);
        AddPackageClassEvidence(entry, evidence, blockingReasons, fixHints);
        AddPublishDecisionEvidence(repositoryRoot, entry, metadataByProjectPath, evidence, blockingReasons, fixHints);

        if (!string.IsNullOrWhiteSpace(entry.Manifest.ReadinessBlocker))
        {
            blockingReasons.Add($"Maintainer blocker {entry.Manifest.ReadinessBlocker} is open.");
            fixHints.Add("Resolve the linked issue or PR, then remove readiness_blocker from packages/package-index.yml.");
        }

        var status = DetermineStatus(entry, blockingReasons);
        return new PackageReadinessEvidence(
            entry.Manifest.Project,
            entry.Metadata.PackageId,
            status,
            evidence,
            blockingReasons,
            fixHints);
    }

    private static void AddReleaseMetadataEvidence(
        string repositoryRoot,
        ResolvedPackageEntry entry,
        List<string> evidence,
        List<string> blockingReasons,
        List<string> fixHints)
    {
        var expectedReleaseStatus = entry.Manifest.Classification switch
        {
            PackageClassification.Public => PackageReleaseStatus.PublicPreview,
            PackageClassification.Support => PackageReleaseStatus.SupportRuntime,
            PackageClassification.ProofHost => PackageReleaseStatus.ProofHost,
            PackageClassification.Excluded => PackageReleaseStatus.Excluded,
            _ => PackageReleaseStatus.Unknown
        };
        if (entry.Manifest.ReleaseStatus == expectedReleaseStatus
            && expectedReleaseStatus != PackageReleaseStatus.Unknown)
        {
            evidence.Add($"release_status is {FormatReadinessEnum(entry.Manifest.ReleaseStatus)}");
        }
        else
        {
            blockingReasons.Add($"release_status is {FormatReadinessEnum(entry.Manifest.ReleaseStatus)}, expected {FormatReadinessEnum(expectedReleaseStatus)}.");
            fixHints.Add($"Set release_status: {FormatManifestEnum(expectedReleaseStatus)} for {entry.Manifest.Project}.");
        }

        var expectedCommercialStatus = entry.Manifest.Classification == PackageClassification.Public
            ? PackageCommercialStatus.CommercialReady
            : PackageCommercialStatus.NotApplicable;
        if (entry.Manifest.CommercialStatus == expectedCommercialStatus)
        {
            evidence.Add($"commercial_status is {FormatReadinessEnum(entry.Manifest.CommercialStatus)}");
        }
        else
        {
            blockingReasons.Add($"commercial_status is {FormatReadinessEnum(entry.Manifest.CommercialStatus)}, expected {FormatReadinessEnum(expectedCommercialStatus)}.");
            fixHints.Add($"Set commercial_status: {FormatManifestEnum(expectedCommercialStatus)} for {entry.Manifest.Project}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Manifest.ReleaseNotesPath))
        {
            blockingReasons.Add("release_notes_path is missing.");
            fixHints.Add($"Add release_notes_path to {entry.Manifest.Project} in packages/package-index.yml.");
            return;
        }

        try
        {
            PackageIndexGenerator.ResolveRepositoryFilePath(
                repositoryRoot,
                entry.Manifest.ReleaseNotesPath,
                $"Manifest entry '{entry.Manifest.Project}' release_notes_path");
            evidence.Add("release_notes_path resolves inside the repository");
        }
        catch (PackageIndexException ex)
        {
            blockingReasons.Add(ex.Message);
            fixHints.Add($"Point release_notes_path for {entry.Manifest.Project} at an existing repository Markdown file.");
        }
    }

    private static void AddPackageClassEvidence(
        ResolvedPackageEntry entry,
        List<string> evidence,
        List<string> blockingReasons,
        List<string> fixHints)
    {
        if (entry.Manifest.Classification is PackageClassification.Public or PackageClassification.Support)
        {
            if (entry.Metadata.IsPackable)
            {
                evidence.Add("project is packable");
            }
            else
            {
                blockingReasons.Add("Project is selected for publish evidence but is not packable.");
                fixHints.Add($"Make {entry.Manifest.Project} packable or change its classification/publish_decision.");
            }
        }

        if (entry.Manifest.Classification == PackageClassification.Public
            && entry.Metadata.IsTool
            && !string.Equals(entry.Metadata.OutputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            blockingReasons.Add($"Public tool package output type is {entry.Metadata.OutputType}, expected Exe.");
            fixHints.Add($"Set OutputType Exe for {entry.Manifest.Project} or remove PackAsTool.");
        }
        else if (entry.Manifest.Classification == PackageClassification.Public
                 && !entry.Metadata.IsTool
                 && !string.Equals(entry.Metadata.OutputType, "Library", StringComparison.OrdinalIgnoreCase))
        {
            blockingReasons.Add($"Public direct-install package output type is {entry.Metadata.OutputType}, expected Library.");
            fixHints.Add($"Set OutputType Library for {entry.Manifest.Project} or move it out of the public classification.");
        }
        else
        {
            evidence.Add($"output type {entry.Metadata.OutputType} matches classification");
        }
    }

    private static void AddPublishDecisionEvidence(
        string repositoryRoot,
        ResolvedPackageEntry entry,
        IReadOnlyDictionary<string, PackageProjectMetadata> metadataByProjectPath,
        List<string> evidence,
        List<string> blockingReasons,
        List<string> fixHints)
    {
        if (entry.Manifest.Classification == PackageClassification.Public
            && entry.Manifest.PublishDecision != PackagePublishDecision.Publish)
        {
            blockingReasons.Add("Public package is not marked publish.");
            fixHints.Add($"Set publish_decision: publish for {entry.Manifest.Project}.");
        }
        else if (entry.Manifest.Classification == PackageClassification.Support
                 && entry.Manifest.PublishDecision == PackagePublishDecision.Publish)
        {
            blockingReasons.Add("Support package uses direct publish instead of support_publish or do_not_publish.");
            fixHints.Add($"Set publish_decision: support_publish or do_not_publish for {entry.Manifest.Project}.");
        }
        else if (entry.Manifest.Classification == PackageClassification.ProofHost
                 && entry.Manifest.PublishDecision == PackagePublishDecision.Publish)
        {
            blockingReasons.Add("Proof-host package uses direct publish instead of support_publish or do_not_publish.");
            fixHints.Add($"Set publish_decision: support_publish or do_not_publish for {entry.Manifest.Project}.");
        }
        else if (entry.Manifest.Classification == PackageClassification.Excluded
                 && entry.Manifest.PublishDecision != PackagePublishDecision.DoNotPublish)
        {
            blockingReasons.Add("Excluded package is not marked do_not_publish.");
            fixHints.Add($"Set publish_decision: do_not_publish for {entry.Manifest.Project}.");
        }
        else if (entry.Manifest.PublishDecision is not null)
        {
            evidence.Add($"publish_decision is {FormatReadinessEnum(entry.Manifest.PublishDecision.Value)}");
        }

        if (entry.Manifest.PublishDecision == PackagePublishDecision.DoNotPublish)
        {
            if (string.IsNullOrWhiteSpace(entry.Manifest.PublishReason))
            {
                blockingReasons.Add("publish_reason is missing for do_not_publish.");
                fixHints.Add($"Add publish_reason for {entry.Manifest.Project}.");
            }
            else
            {
                evidence.Add("publish_reason explains why this row is not published");
            }

            return;
        }

        if (entry.Manifest.PublishDecision is not (PackagePublishDecision.Publish or PackagePublishDecision.SupportPublish))
        {
            return;
        }

        if (entry.Metadata.IsTool)
        {
            if (entry.Manifest.ExpectedDependencyPackageIds.Count > 0)
            {
                blockingReasons.Add("Tool packages must not define expected package dependencies because project references are embedded.");
                fixHints.Add($"Remove expected_dependency_package_ids from {entry.Manifest.Project}.");
            }
            else
            {
                evidence.Add("tool package has no expected package dependencies");
            }

            return;
        }

        var actualPackageIds = entry.Metadata.ProjectReferences
            .Select(reference => NormalizeProjectReferencePath(repositoryRoot, reference))
            .Where(metadataByProjectPath.ContainsKey)
            .Select(referencePath => metadataByProjectPath[referencePath].PackageId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedPackageIds = entry.Manifest.ExpectedDependencyPackageIds
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (actualPackageIds.SequenceEqual(expectedPackageIds, StringComparer.OrdinalIgnoreCase))
        {
            evidence.Add("expected_dependency_package_ids match project references");
            return;
        }

        blockingReasons.Add(
            $"expected_dependency_package_ids [{string.Join(", ", expectedPackageIds)}] do not match project references [{string.Join(", ", actualPackageIds)}].");
        fixHints.Add($"Update expected_dependency_package_ids for {entry.Manifest.Project} to match first-party project references.");
    }

    private static PackageReadinessStatus DetermineStatus(
        ResolvedPackageEntry entry,
        IReadOnlyCollection<string> blockingReasons)
    {
        if (blockingReasons.Count > 0)
        {
            return PackageReadinessStatus.Blocked;
        }

        if (entry.Manifest.Classification == PackageClassification.Support
            && entry.Manifest.PublishDecision == PackagePublishDecision.SupportPublish)
        {
            return PackageReadinessStatus.TransitiveReady;
        }

        if (entry.Manifest.Classification == PackageClassification.ProofHost)
        {
            return PackageReadinessStatus.ProofReady;
        }

        if (entry.Manifest.PublishDecision == PackagePublishDecision.DoNotPublish)
        {
            return PackageReadinessStatus.Excluded;
        }

        return PackageReadinessStatus.ManifestReady;
    }

    private static string NormalizeProjectReferencePath(string repositoryRoot, string referencePath)
    {
        var path = Path.IsPathRooted(referencePath)
            ? Path.GetRelativePath(repositoryRoot, referencePath)
            : referencePath;
        return NormalizeRepositoryPath(path);
    }

    private static string NormalizeRepositoryPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string FormatReadinessEnum<TEnum>(TEnum value)
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

    private static string FormatManifestEnum<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        return value.ToString()
            .Aggregate(
                new StringBuilder(),
                (builder, character) =>
                {
                    if (builder.Length > 0 && char.IsUpper(character))
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(character));
                    return builder;
                })
            .ToString();
    }
}

/// <summary>
/// Package-index evidence status rendered in the maintainer readiness dashboard.
/// </summary>
internal enum PackageReadinessStatus
{
    /// <summary>
    /// Public or directly published package evidence is complete for package-index review.
    /// </summary>
    ManifestReady,

    /// <summary>
    /// Support package evidence is complete for transitive package restore.
    /// </summary>
    TransitiveReady,

    /// <summary>
    /// Proof-host evidence is complete, but the package is not positioned as a first-install surface.
    /// </summary>
    ProofReady,

    /// <summary>
    /// The package is intentionally excluded from publishing.
    /// </summary>
    Excluded,

    /// <summary>
    /// The package has maintainer blockers or incomplete package-index evidence.
    /// </summary>
    Blocked
}

/// <summary>
/// Per-package readiness evidence rendered in the generated maintainer dashboard.
/// </summary>
/// <param name="ProjectPath">Repository-relative project path from the package manifest.</param>
/// <param name="PackageId">Evaluated package id.</param>
/// <param name="Status">Computed package-index evidence status.</param>
/// <param name="Evidence">Non-blocking evidence that passed.</param>
/// <param name="BlockingReasons">Reasons the package-index evidence is blocked.</param>
/// <param name="FixHints">Maintainer-facing fix hints for blocking reasons.</param>
internal sealed record PackageReadinessEvidence(
    string ProjectPath,
    string PackageId,
    PackageReadinessStatus Status,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> FixHints);

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
            && entry.Metadata.IsTool
            && !string.Equals(entry.Metadata.OutputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIndexException(
                $"Manifest entry '{entry.Manifest.Project}' is public and reports PackAsTool=true, but its output type is '{entry.Metadata.OutputType}'. Public .NET tool packages must be executable projects.");
        }

        if (entry.Manifest.Classification == PackageClassification.Public
            && !entry.Metadata.IsTool
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
/// <param name="ProjectReferences">Evaluated project reference paths that contribute package dependency assets.</param>
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
        ? $"dotnet tool install --global {PackageId} --prerelease"
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
            .Where(IsPackageDependencyReference)
            .Select(element => element.TryGetProperty("FullPath", out var fullPathElement)
                ? fullPathElement.GetString()
                : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray();
    }

    private static bool IsPackageDependencyReference(JsonElement projectReference)
    {
        return !projectReference.TryGetProperty("ReferenceOutputAssembly", out var referenceOutputAssemblyElement)
            || !string.Equals(referenceOutputAssemblyElement.GetString(), "false", StringComparison.OrdinalIgnoreCase);
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
    /// Gets the product family that owns this package row for maintainer readiness review.
    /// </summary>
    public string? ProductFamily { get; init; }

    /// <summary>
    /// Gets the required maintainer-facing reason for entries that are intentionally not published.
    /// </summary>
    public string? PublishReason { get; init; }

    /// <summary>
    /// Gets the optional same-repository issue or pull request that blocks package-index evidence completion.
    /// </summary>
    public string? ReadinessBlocker { get; init; }

    /// <summary>
    /// Gets optional escaped plain-text maintainer context for package readiness evidence.
    /// </summary>
    public string? ReadinessNote { get; init; }

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
    /// Gets the command shim expected from a .NET tool package.
    /// </summary>
    /// <remarks>
    /// This field is only valid for projects that set <c>PackAsTool=true</c>. Public tool packages must provide a
    /// value so package validation, publish planning, and post-publish smoke tests can execute the command users type;
    /// non-tool packages must leave it unset. The value must be one file-name-safe command token rather than a path:
    /// no whitespace, path separators, reserved <c>.</c>/<c>..</c> segments, trailing periods, Windows reserved device
    /// names or dotted aliases, control characters, or Windows-invalid file-name characters.
    /// </remarks>
    public string? ToolCommandName { get; init; }

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
    /// A direct-install package or tool that appears in the main package matrix.
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
    /// A public package or tool that should be packed and eventually published as a direct install surface.
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
    /// Public preview package or tool that can be installed directly by OSS adopters.
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
