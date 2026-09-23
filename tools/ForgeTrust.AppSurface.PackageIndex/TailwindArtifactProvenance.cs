using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Immutable producer identity written beside the package artifact manifest.</summary>
internal sealed record TailwindProofSubject(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("repositoryId")] string RepositoryId,
    [property: JsonPropertyName("sourceCommit")] string SourceCommit,
    [property: JsonPropertyName("producerRunId")] string ProducerRunId,
    [property: JsonPropertyName("producerAttempt")] string ProducerAttempt,
    [property: JsonPropertyName("packageVersion")] string PackageVersion,
    [property: JsonPropertyName("artifactManifestSha256")] string ArtifactManifestSha256,
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("artifactFileName")] string ArtifactFileName,
    [property: JsonPropertyName("packageSha512")] string PackageSha512,
    [property: JsonPropertyName("tailwindManifestSha256")] string TailwindManifestSha256,
    [property: JsonPropertyName("consumerFramework")] string ConsumerFramework,
    [property: JsonPropertyName("firstPartyPackages")] IReadOnlyList<TailwindSubjectPackage> FirstPartyPackages,
    [property: JsonPropertyName("payloadProjectionVersion")] int PayloadProjectionVersion = 1);

/// <summary>One producer package in the fixed first-party consumer closure.</summary>
internal sealed record TailwindSubjectPackage(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("packageVersion")] string PackageVersion,
    [property: JsonPropertyName("artifactFileName")] string ArtifactFileName,
    [property: JsonPropertyName("packageSha512")] string PackageSha512);

/// <summary>Strict producer subject creation and trusted bundle validation.</summary>
internal static partial class TailwindProofSubjectService
{
    internal const string FileName = "tailwind-proof-subject.json";
    internal const string SchemaName = "appsurface-tailwind-proof-subject-v1";
    internal const int MaximumDocumentBytes = 16 * 1024 * 1024;
    private const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    private const int MaximumArchiveEntries = 100_000;
    private const string TailwindId = "ForgeTrust.AppSurface.Web.Tailwind";

    internal static void ValidateProducerContext(string repositoryId, string producerRunId, string producerAttempt, string sourceCommit)
        => ValidateIdentity(repositoryId, producerRunId, producerAttempt, sourceCommit);

    internal static string ResolvePath(string? value, string root, string defaultName)
    {
        var selected = string.IsNullOrWhiteSpace(value) ? defaultName : value;
        return Path.GetFullPath(Path.IsPathRooted(selected) ? selected : Path.Combine(root, selected));
    }

    internal static async Task<TailwindProofSubject> CreateAsync(
        string artifactsDirectory,
        string manifestPath,
        string repositoryId,
        string producerRunId,
        string producerAttempt,
        string sourceCommit,
        IReadOnlyList<TailwindSubjectPackage> resolvedClosure,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(repositoryId, producerRunId, producerAttempt, sourceCommit);
        var manifestBytes = await ReadBoundedAsync(manifestPath, MaximumDocumentBytes, cancellationToken);
        var manifest = await new PackageArtifactManifestReader().ReadAsync(manifestPath, cancellationToken);
        var tailwind = manifest.Entries.SingleOrDefault(entry => string.Equals(entry.PackageId, TailwindId, StringComparison.OrdinalIgnoreCase))
            ?? throw new PackageIndexException($"Validated package manifest does not contain '{TailwindId}'.");
        ValidateBasename(tailwind.ArtifactFileName, "Tailwind artifact filename");
        var packagePath = Path.GetFullPath(Path.Combine(artifactsDirectory, tailwind.ArtifactFileName));
        RequireDirectChild(artifactsDirectory, packagePath);
        var packageHash = await PackageHash.ComputeSha512Async(packagePath, cancellationToken);
        if (!string.Equals(packageHash, tailwind.Sha512, StringComparison.Ordinal))
        {
            throw new PackageIndexException($"Tailwind package '{tailwind.ArtifactFileName}' changed after artifact-manifest validation.");
        }

        var archive = ReadArchive(packagePath);
        var releaseManifest = archive.ReleaseManifestBytes
            ?? throw new PackageIndexException("Tailwind archive is missing build/tailwind.release.json.");
        var closure = ValidateClosure(manifest, artifactsDirectory, resolvedClosure, cancellationToken);
        var subject = new TailwindProofSubject(
            SchemaName,
            repositoryId,
            sourceCommit.ToLowerInvariant(),
            producerRunId,
            producerAttempt,
            manifest.PackageVersion,
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            tailwind.PackageId,
            tailwind.ArtifactFileName,
            packageHash,
            Convert.ToHexString(SHA256.HashData(releaseManifest)).ToLowerInvariant(),
            "net10.0",
            closure);
        ValidateSubject(subject);
        return subject;
    }

    internal static async Task<string> WriteAsync(
        TailwindProofSubject subject,
        string artifactsDirectory,
        CancellationToken cancellationToken)
    {
        ValidateSubject(subject);
        var path = Path.Combine(artifactsDirectory, FileName);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(subject, PackageArtifactJson.Options);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: false);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal static async Task<TailwindProofSubject> ValidateAsync(
        string subjectPath,
        string artifactsDirectory,
        string manifestPath,
        string expectedSubjectSha256,
        string expectedRepositoryId,
        string expectedProducerRunId,
        string expectedSourceCommit,
        string expectedArtifactId,
        CancellationToken cancellationToken)
    {
        RequireDigest(expectedSubjectSha256, 64, "expected subject SHA-256");
        RequireDecimal(expectedArtifactId, "producer artifact ID");
        var root = Path.GetFullPath(artifactsDirectory);
        RequireDirectChild(root, Path.GetFullPath(subjectPath));
        RequireDirectChild(root, Path.GetFullPath(manifestPath));
        if (!string.Equals(Path.GetFileName(subjectPath), FileName, StringComparison.Ordinal))
            throw new PackageIndexException($"Producer subject must be named '{FileName}'.");
        var bytes = await ReadBoundedAsync(subjectPath, MaximumDocumentBytes, cancellationToken);
        var actualSubjectHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualSubjectHash), Convert.FromHexString(expectedSubjectSha256)))
            throw new PackageIndexException("Producer subject SHA-256 does not match the trusted workflow output.");

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        RejectDuplicateAndUnknownProperties(document.RootElement, SubjectProperties, "producer subject");
        var subject = JsonSerializer.Deserialize<TailwindProofSubject>(bytes)
            ?? throw new PackageIndexException("Producer subject is empty or invalid.");
        ValidateSubject(subject);
        if (!string.Equals(subject.RepositoryId, expectedRepositoryId, StringComparison.Ordinal)
            || !string.Equals(subject.ProducerRunId, expectedProducerRunId, StringComparison.Ordinal)
            || !string.Equals(subject.SourceCommit, expectedSourceCommit, StringComparison.OrdinalIgnoreCase))
            throw new PackageIndexException("Producer subject repository, run, or source commit differs from trusted workflow identity.");

        var manifestBytes = await ReadBoundedAsync(manifestPath, MaximumDocumentBytes, cancellationToken);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (!string.Equals(manifestHash, subject.ArtifactManifestSha256, StringComparison.Ordinal))
            throw new PackageIndexException("Package artifact manifest bytes do not match the producer subject.");
        var manifest = await new PackageArtifactManifestReader().ReadAsync(manifestPath, cancellationToken);
        if (!string.Equals(manifest.PackageVersion, subject.PackageVersion, StringComparison.Ordinal))
            throw new PackageIndexException("Producer subject package version does not match the artifact manifest.");
        var entry = manifest.Entries.SingleOrDefault(item => string.Equals(item.PackageId, TailwindId, StringComparison.OrdinalIgnoreCase))
            ?? throw new PackageIndexException("Trusted artifact manifest has no Tailwind package entry.");
        if (!string.Equals(entry.ArtifactFileName, subject.ArtifactFileName, StringComparison.Ordinal)
            || !string.Equals(entry.Sha512, subject.PackageSha512, StringComparison.Ordinal))
            throw new PackageIndexException("Producer subject Tailwind identity does not match the artifact manifest.");
        var archivePath = Path.GetFullPath(Path.Combine(root, entry.ArtifactFileName));
        RequireDirectChild(root, archivePath);
        if (!string.Equals(await PackageHash.ComputeSha512Async(archivePath, cancellationToken), entry.Sha512, StringComparison.Ordinal))
            throw new PackageIndexException("Tailwind package archive SHA-512 does not match the producer manifest.");
        var entries = ReadArchive(archivePath);
        var releaseManifest = entries.ReleaseManifestBytes
            ?? throw new PackageIndexException("Tailwind archive is missing build/tailwind.release.json.");
        var releaseHash = Convert.ToHexString(SHA256.HashData(releaseManifest)).ToLowerInvariant();
        if (!string.Equals(releaseHash, subject.TailwindManifestSha256, StringComparison.Ordinal))
            throw new PackageIndexException("Tailwind release manifest bytes do not match the producer subject.");
        foreach (var package in subject.FirstPartyPackages)
        {
            var packagePath = Path.GetFullPath(Path.Combine(root, package.ArtifactFileName));
            RequireDirectChild(root, packagePath);
            if (!string.Equals(await PackageHash.ComputeSha512Async(packagePath, cancellationToken), package.PackageSha512, StringComparison.Ordinal))
                throw new PackageIndexException($"First-party archive '{package.PackageId}' SHA-512 does not match the producer subject.");
            _ = ReadArchive(packagePath);
        }
        _ = expectedArtifactId; // Transport identity is supplied by the trusted workflow and preserved by receipts.
        return subject;
    }

    internal static void ValidateSubject(TailwindProofSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!string.Equals(subject.Schema, SchemaName, StringComparison.Ordinal)) throw new PackageIndexException("Unsupported Tailwind producer subject schema.");
        ValidateIdentity(subject.RepositoryId, subject.ProducerRunId, subject.ProducerAttempt, subject.SourceCommit);
        PackageVersionValidator.Require(subject.PackageVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        RequireDigest(subject.ArtifactManifestSha256, 64, "artifactManifestSha256");
        RequireDigest(subject.PackageSha512, 128, "packageSha512");
        RequireDigest(subject.TailwindManifestSha256, 64, "tailwindManifestSha256");
        ValidateBasename(subject.ArtifactFileName, "artifactFileName");
        if (!string.Equals(subject.PackageId, TailwindId, StringComparison.Ordinal) || subject.ConsumerFramework != "net10.0" || subject.PayloadProjectionVersion != 1)
            throw new PackageIndexException("Producer subject has an unsupported Tailwind package, framework, or payload projection version.");
        if (subject.FirstPartyPackages is null || subject.FirstPartyPackages.Count == 0) throw new PackageIndexException("Producer subject first-party package closure is empty.");
        var ordered = subject.FirstPartyPackages.OrderBy(item => item.PackageId, StringComparer.Ordinal).ToArray();
        if (!subject.FirstPartyPackages.SequenceEqual(ordered)) throw new PackageIndexException("Producer subject first-party packages must be ordinal-sorted.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in subject.FirstPartyPackages)
        {
            if (!ids.Add(package.PackageId)) throw new PackageIndexException($"Duplicate first-party package '{package.PackageId}' in producer subject.");
            PackageVersionValidator.Require(package.PackageVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
            ValidateBasename(package.ArtifactFileName, "first-party artifactFileName");
            RequireDigest(package.PackageSha512, 128, "first-party packageSha512");
            if (package.PackageVersion != subject.PackageVersion) throw new PackageIndexException("First-party package version differs from the coordinated producer version.");
        }
        if (!ids.Contains(TailwindId)) throw new PackageIndexException("Producer subject closure does not include Tailwind.");
    }

    internal static void ValidateBasename(string name, string field)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            throw new PackageIndexException($"{field} must be a confined filename basename.");
    }

    internal static IReadOnlyList<TailwindSubjectPackage> ReadResolvedClosure(string assetsPath, PackageArtifactManifest manifest)
    {
        if (!File.Exists(assetsPath)) throw new PackageIndexException($"Local consumer did not produce '{assetsPath}'.");
        using var stream = new FileStream(assetsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumDocumentBytes) throw new PackageIndexException("Local consumer assets graph exceeds 16 MiB.");
        using var bounded = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (bounded.Length + count > MaximumDocumentBytes) throw new PackageIndexException("Local consumer assets graph exceeds 16 MiB.");
            bounded.Write(buffer, 0, count);
        }
        var bytes = bounded.ToArray();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        RejectDuplicatePropertiesRecursively(document.RootElement, "project.assets.json");
        var root = document.RootElement;
        var targets = RequireObject(root, "targets");
        if (targets.EnumerateObject().Count() != 1) throw new PackageIndexException("Local consumer assets graph must contain exactly one target and no RID-qualified targets.");
        var target = targets.EnumerateObject().Single();
        if (!(string.Equals(target.Name, "net10.0", StringComparison.OrdinalIgnoreCase)
            || target.Name.Contains("Version=v10.0", StringComparison.OrdinalIgnoreCase)))
            throw new PackageIndexException($"Local consumer target '{target.Name}' is not the fixed net10.0 target.");
        var project = RequireObject(root, "project");
        var frameworks = RequireObject(project, "frameworks");
        if (frameworks.EnumerateObject().Count() != 1 || !frameworks.EnumerateObject().Any(item => string.Equals(item.Name, "net10.0", StringComparison.OrdinalIgnoreCase)))
            throw new PackageIndexException("Local consumer project must target only net10.0.");
        var restore = RequireObject(project, "restore");
        if (restore.TryGetProperty("runtimeIdentifier", out _) || restore.TryGetProperty("runtimeIdentifiers", out _))
            throw new PackageIndexException("Local consumer project.assets.json contains a runtime identifier.");

        var libraries = RequireObject(root, "libraries");
        var expected = manifest.Entries.ToDictionary(entry => entry.PackageId, StringComparer.OrdinalIgnoreCase);
        var selected = new List<TailwindSubjectPackage>();
        var targetIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in target.Value.EnumerateObject())
        {
            var split = node.Name.LastIndexOf('/');
            if (split <= 0 || split == node.Name.Length - 1) throw new PackageIndexException($"Malformed target package identity '{node.Name}'.");
            var id = node.Name[..split];
            var version = node.Name[(split + 1)..];
            targetIds.Add(id, version);
            if (!id.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)) continue;
            if (!expected.TryGetValue(id, out var package)) throw new PackageIndexException($"Actual consumer graph contains unplanned first-party package '{id}'.");
            if (!string.Equals(version, manifest.PackageVersion, StringComparison.Ordinal)) throw new PackageIndexException($"Actual first-party package '{id}' resolved to '{version}', expected '{manifest.PackageVersion}'.");
            var libraryKey = libraries.EnumerateObject().SingleOrDefault(item => string.Equals(item.Name, node.Name, StringComparison.OrdinalIgnoreCase));
            if (libraryKey.Equals(default(JsonProperty))) throw new PackageIndexException($"Assets graph is missing library metadata for '{node.Name}'.");
            var library = libraryKey.Value;
            if (!library.TryGetProperty("type", out var libraryType) || libraryType.ValueKind != JsonValueKind.String || libraryType.GetString() != "package")
                throw new PackageIndexException($"First-party node '{node.Name}' is not a restored package; project-reference substitutes are forbidden.");
            if (!string.Equals(package.ArtifactFileName, $"{package.PackageId}.{manifest.PackageVersion}.nupkg", StringComparison.OrdinalIgnoreCase))
                throw new PackageIndexException($"Package plan filename for '{id}' does not match the coordinated version.");
            selected.Add(new TailwindSubjectPackage(package.PackageId, manifest.PackageVersion, package.ArtifactFileName, package.Sha512));
        }
        foreach (var node in target.Value.EnumerateObject())
        {
            var slash = node.Name.LastIndexOf('/');
            var nodeId = node.Name[..slash];
            if (!nodeId.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)) continue;
            var nodeObject = node.Value;
            if (nodeObject.TryGetProperty("dependencies", out var dependencies))
            {
                if (dependencies.ValueKind != JsonValueKind.Object) throw new PackageIndexException($"Dependency metadata for '{node.Name}' is malformed.");
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    if (!dependency.Name.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!expected.ContainsKey(dependency.Name) || !targetIds.ContainsKey(dependency.Name))
                        throw new PackageIndexException($"First-party dependency '{dependency.Name}' referenced by '{nodeId}' is missing from the resolved graph or producer plan.");
                    if (dependency.Value.ValueKind != JsonValueKind.String || !string.Equals(dependency.Value.GetString(), targetIds[dependency.Name], StringComparison.Ordinal))
                        throw new PackageIndexException($"First-party dependency version metadata for '{dependency.Name}' in '{nodeId}' differs from the resolved package node.");
                }
            }
        }
        if (selected.Count == 0 || !selected.Any(item => string.Equals(item.PackageId, "ForgeTrust.AppSurface.Web.Tailwind", StringComparison.OrdinalIgnoreCase)))
            throw new PackageIndexException("Actual consumer graph did not resolve the Tailwind package.");
        return selected.OrderBy(item => item.PackageId, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<TailwindSubjectPackage> ValidateClosure(
        PackageArtifactManifest manifest,
        string directory,
        IReadOnlyList<TailwindSubjectPackage> resolvedClosure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolvedClosure);
        var known = manifest.Entries.ToDictionary(item => item.PackageId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in resolvedClosure)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(package.PackageId)) throw new PackageIndexException($"Duplicate resolved first-party package '{package.PackageId}'.");
            if (!known.TryGetValue(package.PackageId, out var entry)) throw new PackageIndexException($"Resolved first-party package '{package.PackageId}' is not in the producer plan.");
            if (package.PackageVersion != manifest.PackageVersion || entry.ArtifactFileName != package.ArtifactFileName || entry.Sha512 != package.PackageSha512)
                throw new PackageIndexException($"Resolved first-party package '{package.PackageId}' does not match the coordinated producer plan and artifact manifest.");
            ValidateBasename(entry.ArtifactFileName, "first-party artifact filename");
            var path = Path.GetFullPath(Path.Combine(directory, entry.ArtifactFileName));
            RequireDirectChild(directory, path);
            if (!string.Equals(awaitHash(path, cancellationToken), entry.Sha512, StringComparison.Ordinal))
                throw new PackageIndexException($"First-party package '{package.PackageId}' raw SHA-512 differs from the producer manifest.");
            _ = ReadArchive(path);
        }

        if (!seen.Contains(TailwindId)) throw new PackageIndexException("Resolved first-party closure is missing Tailwind.");
        return resolvedClosure.OrderBy(item => item.PackageId, StringComparer.Ordinal).ToArray();

        static string awaitHash(string path, CancellationToken token)
        {
            return PackageHash.ComputeSha512(path);
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new PackageIndexException($"Consumer assets graph is missing object '{name}'.");
        return value;
    }

    private static void RejectDuplicatePropertiesRecursively(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PackageIndexException($"'{path}' contains duplicate property '{property.Name}'.");
                RejectDuplicatePropertiesRecursively(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) RejectDuplicatePropertiesRecursively(child, path + "[]");
        }
    }

    private static ArchiveInventory ReadArchive(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumArchiveBytes) throw new PackageIndexException($"Package archive exceeds {MaximumArchiveBytes} bytes.");
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > MaximumArchiveEntries) throw new PackageIndexException($"Package archive exceeds {MaximumArchiveEntries} entries.");
        var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        byte[]? releaseManifest = null;
        long expandedBytes = 0;
        foreach (var entry in zip.Entries)
        {
            var raw = entry.FullName;
            var isDirectory = raw.EndsWith("/", StringComparison.Ordinal);
            var clean = isDirectory ? raw[..^1] : raw;
            var normalized = NormalizeArchivePath(clean);
            if (!names.TryAdd(normalized, isDirectory)) throw new PackageIndexException($"Package archive contains duplicate or case-colliding path '{raw}'.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!isDirectory && unixType == 0xA000) throw new PackageIndexException($"Package archive contains symlink '{raw}'.");
            foreach (var ancestor in GetAncestors(normalized))
                if (names.TryGetValue(ancestor, out var ancestorIsDirectory) && !ancestorIsDirectory)
                    throw new PackageIndexException($"Package archive contains file/directory collision at '{ancestor}'.");
            if (isDirectory) continue;
            if (entry.Length < 0) throw new PackageIndexException($"Package archive entry '{raw}' has an invalid length.");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > 4L * 1024 * 1024 * 1024) throw new PackageIndexException("Package archive expanded data exceeds the 4 GiB bound.");
            using var stream = entry.Open();
            var capture = string.Equals(normalized, "build/tailwind.release.json", StringComparison.OrdinalIgnoreCase);
            using var captured = capture ? new MemoryStream() : null;
            var buffer = new byte[64 * 1024];
            long actual = 0;
            while (true)
            {
                var count = stream.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                actual = checked(actual + count);
                if (actual > entry.Length || actual + expandedBytes - entry.Length > 4L * 1024 * 1024 * 1024)
                    throw new PackageIndexException($"Package archive entry '{raw}' exceeded its declared or aggregate expansion limit.");
                if (capture)
                {
                    if (actual > MaximumDocumentBytes) throw new PackageIndexException("Tailwind release manifest exceeds the 16 MiB document limit.");
                    captured!.Write(buffer, 0, count);
                }
            }
            if (actual != entry.Length) throw new PackageIndexException($"Package archive entry '{raw}' expanded to a different length than its ZIP declaration.");
            if (capture) releaseManifest = captured!.ToArray();
        }
        foreach (var pair in names)
            if (!pair.Value && names.Keys.Any(name => name.StartsWith(pair.Key + "/", StringComparison.OrdinalIgnoreCase)))
                throw new PackageIndexException($"Package archive contains a file/directory collision at '{pair.Key}'.");
        return new ArchiveInventory(releaseManifest);
    }

    private static IEnumerable<string> GetAncestors(string value)
    {
        var slash = value.IndexOf('/');
        while (slash >= 0)
        {
            yield return value[..slash];
            slash = value.IndexOf('/', slash + 1);
        }
    }

    internal static string NormalizeArchivePath(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw[0] == '/' || raw.Contains('\\') || raw.Contains('\0') || raw.Contains(':')) throw new PackageIndexException($"Unsafe raw package path '{raw}'.");
        if (Regex.IsMatch(raw, "^[A-Za-z]:") || raw.StartsWith("//", StringComparison.Ordinal)) throw new PackageIndexException($"Rooted package path '{raw}' is forbidden.");
        var pieces = raw.Split('/');
        if (pieces.Any(piece => piece.Length == 0 || piece is "." or ".." || piece.EndsWith(' ') || piece.EndsWith('.') || Regex.IsMatch(piece, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\..*)?$", RegexOptions.IgnoreCase)))
            throw new PackageIndexException($"Unsafe package path component in '{raw}'.");
        return string.Join('/', pieces);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new PackageIndexException($"Evidence document '{path}' exceeds {maximum} bytes.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static void ValidateIdentity(string repositoryId, string runId, string attempt, string commit)
    {
        RequireDecimal(repositoryId, "repository ID"); RequireDecimal(runId, "producer run ID"); RequireDecimal(attempt, "producer attempt");
        if (!Regex.IsMatch(commit, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)) throw new PackageIndexException("Source commit must be a full 40-character hexadecimal commit ID.");
    }

    private static void RequireDecimal(string value, string field)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 20 || value.Length > 1 && value[0] == '0' || value.Any(ch => ch is < '0' or > '9')) throw new PackageIndexException($"{field} must be a canonical decimal string.");
    }

    private static void RequireDigest(string value, int length, string field)
    {
        if (value is null || value.Length != length || value.Any(ch => !(ch is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new PackageIndexException($"{field} must be {length} lowercase hexadecimal characters.");
    }

    private static void RequireDirectChild(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new PackageIndexException("Evidence file must be a direct child of the trusted producer artifact directory.");
    }

    private static void RejectDuplicateAndUnknownProperties(JsonElement element, IReadOnlySet<string> allowed, string description)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new PackageIndexException($"{description} must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new PackageIndexException($"{description} contains duplicate property '{property.Name}'.");
            if (!allowed.Contains(property.Name)) throw new PackageIndexException($"{description} contains unknown property '{property.Name}'.");
        }
        if (!allowed.SetEquals(seen)) throw new PackageIndexException($"{description} is missing one or more required properties.");
    }

    private static readonly IReadOnlySet<string> SubjectProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "schema", "repositoryId", "sourceCommit", "producerRunId", "producerAttempt", "packageVersion", "artifactManifestSha256", "packageId", "artifactFileName", "packageSha512", "tailwindManifestSha256", "consumerFramework", "firstPartyPackages", "payloadProjectionVersion"
    };

    private sealed record ArchiveInventory(byte[]? ReleaseManifestBytes);
}
