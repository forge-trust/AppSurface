using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Checks the durable-worker template package and generated project against its reviewed content contract.</summary>
/// <remarks>
/// This validator is deliberately separate from staging: staging creates a private candidate copy, while this type
/// verifies the exact packed artifact and a native template-engine output. The artifact boundary is bounded before
/// parsing or inflation, and the generated boundary ignores only ordinary build/test output directories.
/// </remarks>
internal static class DurableTemplateArtifactContract
{
    /// <summary>The coordinated NuGet package identity.</summary>
    internal const string PackageId = "ForgeTrust.AppSurface.Durable.Templates";

    /// <summary>The stable <c>dotnet new</c> short name.</summary>
    internal const string ShortName = "appsurface-durable-worker";

    /// <summary>The source token expanded by the native template engine.</summary>
    internal const string SourceName = "AppSurfaceDurableWorker";

    private const string ContentRoot = "content/durable-worker/";
    private const string ManifestPath = ".template.config/template.json";
    private const string StableIdentity = "ForgeTrust.AppSurface.Durable.Templates.DurableWorker";
    private const long MaxArchiveBytes = 32L * 1024 * 1024;
    private const int MaxArchiveEntries = 256;
    private const long MaxInflatedBytes = 16L * 1024 * 1024;
    private const long MaxEntryBytes = 2L * 1024 * 1024;
    private const int MaxPathBytes = 1_024;
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxJsonDepth = 16;
    private sealed record ValidatedTemplateArchive(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Content);

    private static readonly string[] CoordinatedAppSurfacePackageIds =
    [
        "ForgeTrust.AppSurface.Durable",
        "ForgeTrust.AppSurface.Durable.Provider",
        "ForgeTrust.AppSurface.Durable.PostgreSql",
        "ForgeTrust.AppSurface.Durable.Testing",
        "ForgeTrust.AppSurface.Observability",
        "ForgeTrust.AppSurface.Workers"
    ];
    private static readonly Regex CredentialAssignment = new(
        @"(?:\A|[;,&?\s])(?:password|pwd|passwd|access\s*token|client\s*secret|api\s*key|apikey|access\s*key|sharedaccesssignature)\s*=\s*([^;,&?\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // This list is the approved generated shape in docs/designs/issue-806-durable-worker-template.md. Keep it
    // synchronized with a reviewed template-shape change; version staging is not permission to add arbitrary files.
    private static readonly string[] GeneratedPaths =
    [
        "AppSurfaceDurableWorker.slnx",
        ".editorconfig",
        ".gitignore",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "README.md",
        "docs/replace-sample.md",
        "src/AppSurfaceDurableWorker/AppSurfaceDurableWorker.csproj",
        "src/AppSurfaceDurableWorker/AssemblyInfo.cs",
        "src/AppSurfaceDurableWorker/Hosting/ActivationEndpoints.cs",
        "src/AppSurfaceDurableWorker/Hosting/DevelopmentBearerHandler.cs",
        "src/AppSurfaceDurableWorker/Hosting/Telemetry.cs",
        "src/AppSurfaceDurableWorker/Program.cs",
        "src/AppSurfaceDurableWorker/WorkerApplication.cs",
        "src/AppSurfaceDurableWorker/Work/SampleWork.cs",
        "src/AppSurfaceDurableWorker/Work/SampleWorkExecutor.cs",
        "src/AppSurfaceDurableWorker/Work/WorkRegistration.cs",
        "src/AppSurfaceDurableWorker/Work/SampleWorkProducer.cs",
        "appsettings.json",
        "tests/AppSurfaceDurableWorker.Tests/AppSurfaceDurableWorker.Tests.csproj",
        "tests/AppSurfaceDurableWorker.Tests/NativePostgreSqlSmokeTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/PostgreSqlFixture.cs",
        "tests/AppSurfaceDurableWorker.Tests/ActivityExporter.cs",
        "tests/AppSurfaceDurableWorker.Tests/BoundedProcessRunner.cs",
        "tests/AppSurfaceDurableWorker.Tests/FixtureBudgets.cs",
        "tests/AppSurfaceDurableWorker.Tests/FirstDurableWorkTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/HostContractTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/PostgreSqlTestContainerImage.cs",
        "tests/AppSurfaceDurableWorker.Tests/SetupOperationLifetime.cs",
        "tests/AppSurfaceDurableWorker.Tests/SetupOperationLifetimeTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/TypedWorkScenarioTests.cs",
        "tests/AppSurfaceDurableWorker.Tests/xunit.runner.json"
    ];

    private static readonly string[] PackagedContentPaths = [.. GeneratedPaths, ManifestPath];

    /// <summary>Validates the exact NuGet archive, template manifest, and authored project graph.</summary>
    /// <param name="nupkgPath">Path to the candidate <c>.nupkg</c>; it is opened read-only and never extracted.</param>
    /// <param name="exactVersion">The coordinated candidate version required in package metadata and AppSurface references.</param>
    /// <param name="allowSigningEnvelope">Allows one bounded root <c>.signature.p7s</c> entry for public replay only; it does not verify authenticity.</param>
    /// <exception cref="PackageIndexException">The file is unsafe, malformed, outside a bound, or differs from the reviewed shape.</exception>
    /// <remarks>
    /// The archive is read-only and never extracted. Defaults are 32 MiB compressed, 256 entries, 16 MiB total actual
    /// inflation, 2 MiB per entry, 1,024 UTF-8 bytes per path, and 1 MiB/depth 16 for JSON. The exact allowlist is the
    /// generated content shape plus the native manifest and required NuGet metadata; the manifest is archive-only
    /// because <c>dotnet new</c> consumes it instead of copying it to generated output. Paths compare
    /// case-insensitively, and optional directory records must be ancestors of an allowlisted file.
    /// </remarks>
    internal static void ValidateArchive(string nupkgPath, string exactVersion, bool allowSigningEnvelope = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nupkgPath);
        PackageVersionValidator.Require(exactVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        _ = ReadAndValidateArchive(nupkgPath, exactVersion, allowSigningEnvelope);
    }

    /// <summary>Computes the native generated-content receipt hash from one validated archive snapshot.</summary>
    /// <param name="templateArchive">Candidate template <c>.nupkg</c>; archive bytes are read within the contract bounds.</param>
    /// <param name="projectName">The simple C# identifier passed to <c>dotnet new -n</c>.</param>
    /// <param name="allowSigningEnvelope">Allows one bounded root signing envelope for replay; does not verify authenticity.</param>
    /// <returns>The lowercase SHA-256 used by the consumer proof over generated relative paths and bytes.</returns>
    /// <exception cref="PackageIndexException">The archive, requested name, or reviewed generated content is invalid.</exception>
    /// <remarks>
    /// The nuspec version supplies the graph's exact AppSurface pin for this API because its signature intentionally
    /// has no separate version argument. The bounded archive reader validates the manifest and full content contract
    /// before hashing. Hash order mirrors the consumer's sorted depth-first relative-path enumeration; only
    /// <see cref="GeneratedPaths"/> is included, so the manifest and package metadata never affect generated identity.
    /// UTF-8 source text is decoded strictly and re-encoded without newline normalization, preserving authored BOM and
    /// CR/LF bytes while applying the same source-name replacement to file paths and content. When
    /// <paramref name="allowSigningEnvelope"/> is true, the envelope is ignored for generated-content hashing;
    /// callers must bind the complete archive digest separately, and this method makes no authenticity claim.
    /// </remarks>
    internal static string ComputeGeneratedContentSha256(
        string templateArchive,
        string projectName,
        bool allowSigningEnvelope = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateArchive);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        RequireProjectName(projectName);
        var archive = ReadAndValidateArchive(templateArchive, expectedVersion: null, allowSigningEnvelope);
        var generatedPaths = GeneratedPaths
            .Select(path => (SourcePath: path, GeneratedPath: path.Replace(SourceName, projectName, StringComparison.Ordinal)))
            .ToArray();
        var entriesByGeneratedPath = generatedPaths.ToDictionary(
            item => item.GeneratedPath,
            item => item.SourcePath,
            StringComparer.Ordinal);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var generatedPath in EnumerateSortedContentPaths(entriesByGeneratedPath.Keys))
        {
            var sourcePath = entriesByGeneratedPath[generatedPath];
            if (!archive.Content.TryGetValue(sourcePath, out var sourceBytes))
            {
                throw ContractFailure($"validated archive is missing generated content '{sourcePath}'");
            }
            var generatedBytes = ReplaceSourceName(sourceBytes.Span, projectName, sourcePath);
            hash.AppendData(Encoding.UTF8.GetBytes(generatedPath + "\0"));
            hash.AppendData(generatedBytes);
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static ValidatedTemplateArchive ReadAndValidateArchive(
        string nupkgPath,
        string? expectedVersion,
        bool allowSigningEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nupkgPath);
        if (expectedVersion is not null)
        {
            PackageVersionValidator.Require(expectedVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        }
        DurableTemplateStaging.RequireRegularPath(nupkgPath);
        var archiveInfo = new FileInfo(nupkgPath);
        if (!archiveInfo.Exists || archiveInfo.Length > MaxArchiveBytes)
        {
            throw ContractFailure("candidate archive is missing or exceeds the 32-MiB compressed limit");
        }

        var content = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long inflated = 0;
        var metadataCount = 0;
        var signingEnvelopeCount = 0;
        string? archiveVersion = null;
        try
        {
            using var archive = ZipFile.OpenRead(nupkgPath);
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                throw ContractFailure("candidate archive exceeds the 256-entry limit");
            }

            foreach (var entry in archive.Entries)
            {
                var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                var rawPath = isDirectory ? entry.FullName[..^1] : entry.FullName;
                var path = NormalizeArchivePath(rawPath);
                if (!archivePaths.Add(path))
                {
                    throw ContractFailure($"candidate archive contains a duplicate or case-colliding path '{path}'");
                }

                RejectLinkedOrSpecialEntry(entry, isDirectory, path);
                if (entry.Length > MaxEntryBytes || entry.Length < 0 || entry.CompressedLength < 0)
                {
                    throw ContractFailure($"archive entry '{path}' exceeds the 2-MiB declared-length limit");
                }

                if (isDirectory)
                {
                    if (entry.Length != 0 || !IsKnownDirectory(path))
                    {
                        throw ContractFailure($"archive contains an unexpected directory entry '{path}'");
                    }
                    directoryPaths.Add(path);
                    continue;
                }

                var bytes = ReadBoundedEntry(entry, ref inflated);
                if (path.StartsWith(ContentRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var relativePath = path[ContentRoot.Length..];
                    if (!content.TryAdd(relativePath, bytes))
                    {
                        throw ContractFailure($"candidate archive repeats template content '{relativePath}'");
                    }
                }
                else if (IsPackageMetadataPath(path))
                {
                    if (path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!path.Equals(PackageId + ".nuspec", StringComparison.OrdinalIgnoreCase))
                        {
                            throw ContractFailure("candidate archive has an unexpected nuspec name");
                        }
                        archiveVersion = ValidateNuspec(bytes, expectedVersion);
                    }
                    if (path.StartsWith("package/services/metadata/core-properties/", StringComparison.OrdinalIgnoreCase))
                    {
                        metadataCount++;
                        if (!Regex.IsMatch(path, @"\Apackage/services/metadata/core-properties/[0-9A-Fa-f-]+\.psmdcp\z", RegexOptions.CultureInvariant))
                        {
                            throw ContractFailure("candidate archive has malformed NuGet core metadata");
                        }
                    }
                }
                else if (string.Equals(path, ".signature.p7s", StringComparison.Ordinal))
                {
                    if (!allowSigningEnvelope)
                    {
                        throw ContractFailure("candidate archive contains a signing envelope outside public replay mode");
                    }
                    if (++signingEnvelopeCount > 1)
                    {
                        throw ContractFailure("candidate archive contains more than one signing envelope");
                    }
                }
                else
                {
                    throw ContractFailure($"candidate archive contains unexpected package content '{path}'");
                }
            }
        }
        catch (PackageIndexException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or XmlException or InvalidOperationException)
        {
            throw new PackageIndexException($"Durable template archive is malformed: {exception.GetType().Name}.");
        }

        RequireExactArchivePaths(
            archivePaths,
            directoryPaths,
            content.Keys,
            metadataCount,
            signingEnvelopeCount,
            allowSigningEnvelope);
        if (archiveVersion is null)
        {
            throw ContractFailure("candidate archive has no package nuspec");
        }
        ValidateTemplateContent(content, archiveVersion);
        return new ValidatedTemplateArchive(content);
    }

    /// <summary>Validates a generated project after native template expansion and any ordinary build/test run.</summary>
    /// <param name="root">Generated solution root; reads only paths within this root.</param>
    /// <param name="projectName">The simple C# identifier supplied to <c>dotnet new -n</c>.</param>
    /// <param name="exactVersion">The exact AppSurface package version expected in the generated graph.</param>
    /// <exception cref="PackageIndexException">The generated inventory, paths, secrets, or project graph violate the contract.</exception>
    /// <remarks>
    /// The exact generated allowlist excludes the native manifest. Only <c>bin</c>, <c>obj</c>, and
    /// <c>TestResults</c> directories are ignored, and only after checking them for reparse points. This permits a built
    /// consumer without hiding authored local settings, repository references, or packaging debris. Per-file and total
    /// authored bytes use the same 2 MiB and 16 MiB limits as archive inflation.
    /// </remarks>
    internal static void ValidateGenerated(string root, string projectName, string exactVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        PackageVersionValidator.Require(exactVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        RequireProjectName(projectName);

        var fullRoot = Path.GetFullPath(root);
        DurableTemplateStaging.RequireRegularPath(fullRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw ContractFailure("generated project root does not exist");
        }

        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in EnumerateGeneratedFiles(fullRoot))
        {
            var relative = Path.GetRelativePath(fullRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var normalized = NormalizeArchivePath(relative);
            if (files.Count >= MaxArchiveEntries)
            {
                throw ContractFailure("generated project exceeds the 256-file limit");
            }
            if (files.ContainsKey(normalized))
            {
                throw ContractFailure($"generated project contains duplicate or case-colliding path '{normalized}'");
            }
            var info = new FileInfo(file);
            if (info.Length > MaxEntryBytes || (total += info.Length) > MaxInflatedBytes)
            {
                throw ContractFailure("generated authored content exceeds a template size bound");
            }
            files.Add(normalized, File.ReadAllBytes(file));
        }

        var expected = GeneratedPaths
            .Select(path => path.Replace(SourceName, projectName, StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RequireExactFileSet(files.Keys, expected, "generated project");
        ValidateTextInventory(files, SourceName, projectName);
        ValidateProjectVersions(files, exactVersion);
    }

    /// <summary>Validates coordinated and third-party package pins and confines project references to the supplied root.</summary>
    /// <param name="projectRoot">Authored, staged, or generated content root.</param>
    /// <param name="exactVersion">Exact candidate version required for each direct AppSurface package reference.</param>
    /// <exception cref="PackageIndexException">A reference is unpinned, mismatched, malformed, or escapes the root.</exception>
    /// <remarks>
    /// The graph must enable Central Package Management in its root <c>Directory.Packages.props</c>; project-level
    /// <c>Version</c> and <c>VersionOverride</c> values are rejected, including when they match a central pin. All
    /// package versions are fixed semantic versions, and every AppSurface central pin equals
    /// <paramref name="exactVersion"/>. Project references resolve only to files in the supplied content root. This
    /// method validates the graph only; the release stager owns the allowlisted central-pin substitutions.
    /// </remarks>
    internal static void ValidateProjectVersions(string projectRoot, string exactVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        PackageVersionValidator.Require(exactVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        var fullRoot = Path.GetFullPath(projectRoot);
        DurableTemplateStaging.RequireRegularPath(fullRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw ContractFailure("project-version root does not exist");
        }

        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var path in EnumerateGeneratedFiles(fullRoot))
        {
            var relative = Path.GetRelativePath(fullRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            var normalized = NormalizeArchivePath(relative);
            var info = new FileInfo(path);
            if (files.Count >= MaxArchiveEntries || info.Length > MaxEntryBytes || (total += info.Length) > MaxInflatedBytes)
            {
                throw ContractFailure("project graph exceeds a 256-file, 2-MiB-per-file, or 16-MiB total-content bound");
            }
            if (!files.TryAdd(normalized, File.ReadAllBytes(path)))
            {
                throw ContractFailure($"project graph contains duplicate or case-colliding path '{normalized}'");
            }
        }

        ValidateProjectVersions(files, exactVersion);
    }

    /// <summary>Normalizes one untrusted slash-separated archive path and rejects portable filesystem aliases.</summary>
    /// <param name="path">ZIP entry path without a trailing directory slash.</param>
    /// <returns>The validated path in slash-separated form.</returns>
    /// <exception cref="PackageIndexException">The path is rooted, aliased, too long, or invalid on a supported OS.</exception>
    /// <remarks>Backslashes, empty/dot segments, parent traversal, colon, trailing dot/space, control characters, and device names are rejected.</remarks>
    internal static string NormalizeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw ContractFailure("archive path is empty, rooted, aliased, non-normalized, or exceeds 1,024 UTF-8 bytes");
        }

        int pathBytes;
        bool isNormalized;
        try
        {
            pathBytes = new UTF8Encoding(false, true).GetByteCount(path);
            isNormalized = path.IsNormalized(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw ContractFailure("archive path contains invalid Unicode");
        }
        if (pathBytes > MaxPathBytes || !isNormalized || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\') || path.Contains('\0') || path.Contains(':'))
        {
            throw ContractFailure("archive path is rooted, aliased, non-normalized, or exceeds 1,024 UTF-8 bytes");
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.Any(static character => char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*'))
            {
                throw ContractFailure("archive path contains an empty, traversal, or non-portable segment");
            }
            var deviceName = segment.Split('.')[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(deviceName, @"\A(?:COM|LPT)[1-9]\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                throw ContractFailure("archive path contains a reserved Windows device name");
            }
        }

        return string.Join('/', segments);
    }

    private static void RequireProjectName(string projectName)
    {
        if (!Regex.IsMatch(projectName, @"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant))
        {
            throw ContractFailure("generated project name must be one C# identifier");
        }
    }

    private static IEnumerable<string> EnumerateSortedContentPaths(IEnumerable<string> paths)
    {
        var allPaths = paths.ToHashSet(StringComparer.Ordinal);
        return EnumerateDirectory(string.Empty);

        IEnumerable<string> EnumerateDirectory(string prefix)
        {
            var children = allPaths
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(path => path[prefix.Length..].Split('/')[0])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            foreach (var child in children)
            {
                var childPath = prefix + child;
                if (allPaths.Contains(childPath))
                {
                    yield return childPath;
                }
                else
                {
                    foreach (var descendant in EnumerateDirectory(childPath + "/")) yield return descendant;
                }
            }
        }
    }

    private static byte[] ReplaceSourceName(ReadOnlySpan<byte> source, string projectName, string path)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(source);
        }
        catch (DecoderFallbackException)
        {
            throw ContractFailure($"template content '{path}' is not UTF-8 text");
        }
        return new UTF8Encoding(false, true).GetBytes(text.Replace(SourceName, projectName, StringComparison.Ordinal));
    }

    private static byte[] ReadBoundedEntry(ZipArchiveEntry entry, ref long totalInflated)
    {
        using var input = entry.Open();
        using var output = new MemoryStream(entry.Length is > 0 and <= MaxEntryBytes ? (int)entry.Length : 0);
        var buffer = new byte[16 * 1024];
        long entryInflated = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            entryInflated += read;
            totalInflated += read;
            if (entryInflated > MaxEntryBytes || totalInflated > MaxInflatedBytes)
            {
                throw ContractFailure("candidate archive exceeds an actual inflation limit");
            }
            output.Write(buffer, 0, read);
        }
        if (entryInflated != entry.Length)
        {
            throw ContractFailure($"archive entry '{entry.FullName}' inflated to a length different from its ZIP declaration");
        }
        return output.ToArray();
    }

    private static void ValidateTemplateContent(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, string exactVersion)
    {
        var expected = PackagedContentPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        RequireExactFileSet(files.Keys, expected, "template content");
        var manifest = files[ManifestPath];
        ValidateManifest(manifest.Span);
        ValidateTextInventory(files, SourceName, SourceName);
        ValidateProjectVersions(files, exactVersion);
    }

    private static void ValidateManifest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxJsonBytes)
        {
            throw ContractFailure("template manifest exceeds the 1-MiB JSON limit");
        }
        using var document = ParseJson(bytes, "template manifest");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || GetRequiredString(root, "identity") != StableIdentity
            || GetRequiredString(root, "shortName") != ShortName
            || GetRequiredString(root, "sourceName") != SourceName
            || !root.TryGetProperty("preferNameDirectory", out var preferNameDirectory)
            || preferNameDirectory.ValueKind != JsonValueKind.True)
        {
            throw ContractFailure("template manifest has an unexpected identity, short name, source name, or directory preference");
        }

        if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object
            || !tags.TryGetProperty("language", out var language) || language.ValueKind != JsonValueKind.String || language.GetString() != "C#"
            || !tags.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "project")
        {
            throw ContractFailure("template manifest must identify a C# project template");
        }
        if (tags.EnumerateObject().Count() != 2
            || !root.TryGetProperty("classifications", out var classifications)
            || classifications.ValueKind != JsonValueKind.Array
            || !classifications.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
            || !classifications.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["AppSurface", "Durable", "Worker", "PostgreSQL"])
            || classifications.GetArrayLength() != 4)
        {
            throw ContractFailure("template manifest classifications and tags must match the reviewed worker shape");
        }
        if (!root.TryGetProperty("primaryOutputs", out var primaryOutputs)
            || primaryOutputs.ValueKind != JsonValueKind.Array
            || primaryOutputs.GetArrayLength() != 1
            || primaryOutputs[0].ValueKind != JsonValueKind.Object
            || GetRequiredString(primaryOutputs[0], "path") != "src/AppSurfaceDurableWorker/AppSurfaceDurableWorker.csproj")
        {
            throw ContractFailure("template manifest must identify the generated host project as its sole primary output");
        }
        if (root.TryGetProperty("postActions", out var postActions)
            && (postActions.ValueKind != JsonValueKind.Array || postActions.GetArrayLength() != 0))
        {
            throw ContractFailure("template manifest must not run post-actions");
        }
        RejectUnsafeTemplateSwitches(root);
    }

    private static JsonDocument ParseJson(ReadOnlySpan<byte> bytes, string description)
    {
        try
        {
            var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = MaxJsonDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            ValidateNoDuplicateJsonFields(document.RootElement, description);
            return document;
        }
        catch (JsonException exception)
        {
            throw new PackageIndexException($"{description} is malformed or exceeds the depth-16 JSON limit: {exception.GetType().Name}.");
        }
    }

    private static void ValidateNoDuplicateJsonFields(JsonElement element, string description)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw ContractFailure($"{description} contains a duplicate or case-colliding field '{property.Name}'");
                }
                ValidateNoDuplicateJsonFields(property.Value, description);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateNoDuplicateJsonFields(item, description);
        }
    }

    private static void RejectUnsafeTemplateSwitches(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Contains("symbol", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var symbol in property.Value.EnumerateObject())
                {
                    if (symbol.Name.Contains("auth", StringComparison.OrdinalIgnoreCase)
                        || symbol.Name.Contains("migration", StringComparison.OrdinalIgnoreCase)
                        || symbol.Name.Contains("privilege", StringComparison.OrdinalIgnoreCase)
                        || symbol.Name.Contains("sql", StringComparison.OrdinalIgnoreCase))
                    {
                        throw ContractFailure($"template manifest exposes an unsafe opt-out symbol '{symbol.Name}'");
                    }
                }
            }
        }
    }

    private static string GetRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return string.Empty;
        }
        return value.GetString()!;
    }

    private static string ValidateNuspec(ReadOnlyMemory<byte> bytes, string? expectedVersion)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxEntryBytes });
        var package = XDocument.Load(reader);
        var metadata = package.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata");
        var packageId = metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == "id")?.Value;
        var archiveVersion = metadata?.Elements().SingleOrDefault(element => element.Name.LocalName == "version")?.Value;
        if (metadata is null
            || packageId != PackageId
            || string.IsNullOrWhiteSpace(archiveVersion)
            || (expectedVersion is not null && archiveVersion != expectedVersion))
        {
            throw ContractFailure("template nuspec package identity or version does not match the candidate");
        }
        var packageTypes = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "packageTypes")?
            .Elements().Where(element => element.Name.LocalName == "packageType").ToArray() ?? [];
        if (packageTypes.Length != 1 || (string?)packageTypes[0].Attribute("name") != "Template")
        {
            throw ContractFailure("durable template package must declare only package type Template");
        }
        if (metadata.Descendants().Any(element => element.Name.LocalName == "dependency"))
        {
            throw ContractFailure("template package must not acquire a runtime dependency graph");
        }
        PackageVersionValidator.Require(archiveVersion!, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        return archiveVersion;
    }

    private static void ValidateTextInventory(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, string sourceName, string projectName)
    {
        foreach (var (path, content) in files)
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                throw ContractFailure($"template content must not include compiled artifact '{path}'");
            }
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(content.Span);
            }
            catch (DecoderFallbackException)
            {
                throw ContractFailure($"template content '{path}' is not UTF-8 text");
            }
            if (text.Contains('\0')) throw ContractFailure($"template content '{path}' contains binary data");
            if (sourceName != projectName && text.Contains(sourceName, StringComparison.Ordinal))
            {
                throw ContractFailure($"generated content '{path}' retains the source-name token");
            }
            if (path.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase)) ValidateSettingsJson(content.Span, path);
        }
    }

    private static void ValidateSettingsJson(ReadOnlySpan<byte> bytes, string path)
    {
        if (bytes.Length > MaxJsonBytes) throw ContractFailure($"settings file '{path}' exceeds the JSON size limit");
        using var document = ParseJson(bytes, $"settings file '{path}'");
        RejectNonemptySecretSettings(document.RootElement, path);
    }

    private static void RejectNonemptySecretSettings(JsonElement element, string path, string propertyName = "")
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) RejectNonemptySecretSettings(property.Value, path, property.Name);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectNonemptySecretSettings(item, path, propertyName);
        }
        else if (element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
        {
            var value = element.GetString()!;
            var secretSetting = propertyName.Contains("password", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("token", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("apikey", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("connectionstring", StringComparison.OrdinalIgnoreCase);
            var literalConnectionCredential = CredentialAssignment.Matches(value).Any(match =>
                !IsTemplatePlaceholder(match.Groups[1].Value));
            if ((secretSetting && !IsTemplatePlaceholder(value)) || literalConnectionCredential)
            {
                throw ContractFailure($"settings file '{path}' contains a nonempty secret-like setting '{propertyName}'");
            }
        }
    }

    private static bool IsTemplatePlaceholder(string value)
    {
        var trimmed = value.Trim();
        return (trimmed.StartsWith("${", StringComparison.Ordinal)
                && trimmed.EndsWith('}')
                && trimmed.IndexOf('}', 2) == trimmed.Length - 1
                && !trimmed.AsSpan(2, trimmed.Length - 3).ContainsAny('{', '}'))
            || (trimmed.StartsWith('<')
                && trimmed.EndsWith('>')
                && trimmed.IndexOf('>', 1) == trimmed.Length - 1
                && !trimmed.AsSpan(1, trimmed.Length - 2).ContainsAny('<', '>'));
    }

    private static void ValidateProjectVersions(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, string exactVersion)
    {
        if (!files.TryGetValue("Directory.Packages.props", out var centralPropsBytes))
        {
            throw ContractFailure("template graph has no root Directory.Packages.props");
        }
        var centralProps = ParseXml(centralPropsBytes, "Directory.Packages.props");
        var centralManagement = centralProps.Descendants().Where(element => element.Name.LocalName == "ManagePackageVersionsCentrally").ToArray();
        if (centralManagement.Length != 1
            || centralManagement[0].Value.Trim() != "true"
            || centralManagement[0].AncestorsAndSelf().Any(element => element.Attribute("Condition") is not null))
        {
            throw ContractFailure("root Directory.Packages.props must unconditionally enable Central Package Management");
        }

        var centralVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in files.Where(item => item.Key.EndsWith(".props", StringComparison.OrdinalIgnoreCase)))
        {
            var document = ParseXml(content, path);
            var packageVersions = document.Descendants().Where(element => element.Name.LocalName == "PackageVersion").ToArray();
            if (!path.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase) && packageVersions.Length != 0)
            {
                throw ContractFailure($"central package pins must be owned by the root Directory.Packages.props, not '{path}'");
            }
            foreach (var packageVersion in packageVersions)
            {
                var id = ((string?)packageVersion.Attribute("Include"))?.Trim();
                var version = ((string?)packageVersion.Attribute("Version"))?.Trim();
                if (string.IsNullOrWhiteSpace(id) || id.Contains('$') || id.Contains(';')
                    || string.IsNullOrWhiteSpace(version)
                    || packageVersion.Attribute("Condition") is not null
                    || packageVersion.Attribute("Update") is not null
                    || packageVersion.Elements().Any(element => element.Name.LocalName is "Version" or "VersionOverride")
                    || packageVersion.Ancestors().Any(element => element.Attribute("Condition") is not null)
                    || !centralVersions.TryAdd(id, version))
                {
                    throw ContractFailure($"central package version entry in '{path}' is malformed or duplicated");
                }
                ValidateFixedVersion(id, version, exactVersion);
            }
        }
        var coordinatedPins = centralVersions.Keys
            .Where(id => id.StartsWith("ForgeTrust.AppSurface.", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!coordinatedPins.SetEquals(CoordinatedAppSurfacePackageIds))
        {
            throw ContractFailure("central package graph must contain exactly the six coordinated AppSurface package pins");
        }

        var projectFiles = files.Where(item => item.Key.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (projectFiles.Length == 0) throw ContractFailure("template graph contains no project files");
        foreach (var (path, content) in projectFiles)
        {
            var document = ParseXml(content, path);
            if (document.Descendants().Any(element => element.Name.LocalName == "Import"))
            {
                throw ContractFailure($"project '{path}' imports a file outside the reviewed self-contained graph");
            }
            var packageReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "PackageReference"))
            {
                var include = ((string?)reference.Attribute("Include"))?.Trim();
                var update = ((string?)reference.Attribute("Update"))?.Trim();
                var id = include ?? update;
                if (string.IsNullOrWhiteSpace(id) || id.Contains('$') || id.Contains(';')
                    || (include is not null && update is not null)
                    || reference.Attribute("Condition") is not null
                    || reference.Ancestors().Any(element => element.Attribute("Condition") is not null)
                    || !packageReferences.Add(id))
                {
                    throw ContractFailure($"project '{path}' contains a dynamic or malformed package reference");
                }
                if (reference.Attribute("Version") is not null
                    || reference.Attribute("VersionOverride") is not null
                    || reference.Elements().Any(element => element.Name.LocalName is "Version" or "VersionOverride"))
                {
                    throw ContractFailure($"project '{path}' must resolve '{id}' through its central package pin, without a project-level version override");
                }
                if (!centralVersions.TryGetValue(id, out var version))
                {
                    throw ContractFailure($"project '{path}' has a package reference '{id}' with no central version pin");
                }
                ValidateFixedVersion(id, version, exactVersion);
            }

            var projectReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                var include = ((string?)reference.Attribute("Include"))?.Trim();
                if (string.IsNullOrWhiteSpace(include) || include.Contains('$') || include.Contains(';') || include.Contains('*') || include.Contains('?')
                    || reference.Attribute("Condition") is not null
                    || reference.Ancestors().Any(element => element.Attribute("Condition") is not null))
                {
                    throw ContractFailure($"project '{path}' contains a dynamic or malformed project reference");
                }
                var target = ResolveRootRelativeProject(path, include);
                if (!projectReferences.Add(target) || !files.ContainsKey(target))
                {
                    throw ContractFailure($"project reference '{include}' in '{path}' escapes the template graph or has no target");
                }
            }
        }
    }

    private static void ValidateFixedVersion(string packageId, string version, string exactVersion)
    {
        try
        {
            PackageVersionValidator.Require(version, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        }
        catch (PackageIndexException)
        {
            throw ContractFailure($"package '{packageId}' does not have a fixed semantic version pin");
        }
        if (packageId.StartsWith("ForgeTrust.AppSurface.", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(version, exactVersion, StringComparison.Ordinal))
        {
            throw ContractFailure($"AppSurface package '{packageId}' is pinned to '{version}', expected exact candidate '{exactVersion}'");
        }
    }

    private static XDocument ParseXml(ReadOnlyMemory<byte> content, string path)
    {
        try
        {
            using var input = new MemoryStream(content.ToArray(), writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxEntryBytes });
            return XDocument.Load(reader);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new PackageIndexException($"Template project file '{path}' is malformed XML: {exception.GetType().Name}.");
        }
    }

    private static string ResolveRootRelativeProject(string projectPath, string include)
    {
        if (Path.IsPathRooted(include) || include.Contains('\\') || !include.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw ContractFailure($"project reference '{include}' is rooted, aliased, or not a project file");
        }
        var parent = Path.GetDirectoryName(projectPath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var resolved = Path.GetFullPath(Path.Join("/template-root", parent, include.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath("/template-root", resolved).Replace(Path.DirectorySeparatorChar, '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw ContractFailure($"project reference '{include}' escapes the template root");
        }
        return NormalizeArchivePath(relative);
    }

    private static IEnumerable<string> EnumerateGeneratedFiles(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            DurableTemplateStaging.RequireRegularPath(entry);
            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("TestResults", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (var file in EnumerateGeneratedFiles(entry)) yield return file;
            }
            else if (File.Exists(entry))
            {
                yield return entry;
            }
            else
            {
                throw ContractFailure($"generated content contains an unsupported filesystem entry '{entry}'");
            }
        }
    }

    private static void RequireExactArchivePaths(
        IReadOnlySet<string> archivePaths,
        IReadOnlySet<string> directoryPaths,
        IEnumerable<string> contentPaths,
        int metadataCount,
        int signingEnvelopeCount,
        bool allowSigningEnvelope)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[Content_Types].xml", "_rels/.rels", PackageId + ".nuspec", "README.md", "LICENSE"
        };
        expected.UnionWith(contentPaths.Select(path => ContentRoot + path));
        if (signingEnvelopeCount > 0 && allowSigningEnvelope) expected.Add(".signature.p7s");
        var metadata = archivePaths.Where(path => path.StartsWith("package/services/metadata/core-properties/", StringComparison.OrdinalIgnoreCase)
            && !directoryPaths.Contains(path)).ToArray();
        if (metadata.Length != 1 || metadataCount != 1) throw ContractFailure("candidate archive must contain one NuGet core-properties record");
        expected.Add(metadata[0]);

        RequireExactFileSet(archivePaths.Where(path => !directoryPaths.Contains(path)), expected, "candidate archive");
        foreach (var directory in directoryPaths)
        {
            if (!expected.Any(path => path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)))
            {
                throw ContractFailure($"candidate archive contains an unexpected directory '{directory}'");
            }
        }
    }

    private static void RequireExactFileSet(IEnumerable<string> actualPaths, IReadOnlySet<string> expectedPaths, string description)
    {
        var actual = actualPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = expectedPaths.Where(path => !actual.Contains(path)).Order(StringComparer.Ordinal).ToArray();
        var extra = actual.Where(path => !expectedPaths.Contains(path)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
        {
            var missingText = missing.Length == 0 ? "none" : string.Join(", ", missing);
            var extraText = extra.Length == 0 ? "none" : string.Join(", ", extra);
            throw ContractFailure($"{description} file inventory differs from the reviewed shape (missing: {missingText}; unexpected: {extraText})");
        }
    }

    private static bool IsPackageMetadataPath(string path) =>
        path is "[Content_Types].xml" or "_rels/.rels" or "README.md" or "LICENSE"
        || path.Equals(PackageId + ".nuspec", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("package/services/metadata/core-properties/", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownDirectory(string path) =>
        path is "content" or "content/durable-worker" or "package" or "package/services"
            or "package/services/metadata" or "package/services/metadata/core-properties" or "_rels"
        || PackagedContentPaths.Any(file => (ContentRoot + file).StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

    private static void RejectLinkedOrSpecialEntry(ZipArchiveEntry entry, bool isDirectory, string path)
    {
        var attributes = entry.ExternalAttributes;
        var windowsAttributes = attributes & 0xFFFF;
        var unixType = (attributes >> 16) & 0xF000;
        var hasWindowsDirectoryAttribute = (windowsAttributes & (int)FileAttributes.Directory) != 0;
        var allowedUnixType = unixType == 0
            || (isDirectory && unixType == 0x4000)
            || (!isDirectory && unixType == 0x8000);
        if ((windowsAttributes & (int)FileAttributes.ReparsePoint) != 0
            || (hasWindowsDirectoryAttribute && !isDirectory)
            || !allowedUnixType)
        {
            throw ContractFailure($"candidate archive entry '{path}' is a link or unsupported special file");
        }
    }

    private static PackageIndexException ContractFailure(string message) =>
        new($"Durable template contract failed: {message}.");
}
