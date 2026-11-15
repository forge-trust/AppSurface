using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Inputs needed to compute a private, local producer identity without executing resources or producers.</summary>
internal sealed record EvidenceProducerIdentityRequest(
    string RepositoryRoot,
    EvidencePlan Plan,
    EvidenceProducerDeclaration Producer,
    string ImplementationIdentity,
    IReadOnlyDictionary<string, string> ResourceIdentities,
    IReadOnlyDictionary<string, string> EffectiveToolIdentity,
    IReadOnlyDictionary<string, string> ExtraMaterial,
    string? CapturedPatchSha256);

/// <summary>A deterministic producer key and the exact input inventory used to compute it.</summary>
internal sealed record EvidenceProducerIdentity(
    string Key,
    string RepositoryNamespace,
    IReadOnlyList<EvidenceProducerInputFile> Files,
    IReadOnlyDictionary<string, string> ComponentHashes);

/// <summary>One regular input file, represented by its repository path, platform-available mode, length and content digest.</summary>
/// <remarks>
/// Unix includes all file-mode permission bits returned by the working-tree file mode, including execute bits.
/// Windows represents the available read-only attribute in its normalized regular-file mode; it has no Unix
/// permission-bit set. Index-only mode changes are excluded, but an actual Unix working-tree mode change affects identity.
/// </remarks>
internal sealed record EvidenceProducerInputFile(string RelativePath, int Mode, long LengthBytes, string Sha256);

/// <summary>Preparation result; ineligible results never carry a partial or usable key.</summary>
internal sealed record EvidenceProducerIdentityResult(EvidenceProducerIdentity? Identity, string Reason, string? Diagnostic)
{
    internal bool IsEligible => Identity is not null;
}

/// <summary>Builds stable private producer identities from declared inputs and effective execution identity.</summary>
internal static class EvidenceProducerIdentityBuilder
{
    internal const int MaximumFiles = 100_000;
    internal const int MaximumMetadataBytes = 16 * 1024 * 1024;
    internal const int MaximumMaterialKeys = 64;
    internal const int MaximumMaterialKeyLength = 128;
    internal const int MaximumMaterialValueBytes = 4 * 1024;
    internal const int MaximumMaterialTotalBytes = 256 * 1024;
    internal static readonly TimeSpan PreparationBudget = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions CanonicalJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Computes the canonical producer declaration hash used for identity keys and receipt comparisons.
    /// </summary>
    /// <param name="producer">The producer declaration to normalize.</param>
    /// <returns>The lower-case SHA-256 hash of the normalized declaration.</returns>
    /// <remarks>
    /// Required resources, assertion identifiers, artifact slots, input includes and input excludes do not gain
    /// meaning from list order. This method sorts those values using ordinal ordering (artifact slots by logical
    /// name) while preserving every other declaration value. A missing input declaration remains missing, and a
    /// missing exclude list is represented as empty when inputs are present. Identity construction and receipt
    /// validation both call this method so they use the same normalization and serializer; raw declaration JSON
    /// comparison would invalidate receipts whose arrays differ only in order.
    /// </remarks>
    internal static string CanonicalDeclarationHash(EvidenceProducerDeclaration producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        return Hash(JsonSerializer.SerializeToUtf8Bytes(CanonicalizeDeclaration(producer), CanonicalJson));
    }

    private static EvidenceProducerDeclaration CanonicalizeDeclaration(EvidenceProducerDeclaration producer)
    {
        return producer with
        {
            RequiredResources = producer.RequiredResources.Order(StringComparer.Ordinal).ToArray(),
            AssertionIds = producer.AssertionIds.Order(StringComparer.Ordinal).ToArray(),
            ArtifactSlots = producer.ArtifactSlots
                .OrderBy(static slot => slot.LogicalName, StringComparer.Ordinal).ToArray(),
            Inputs = producer.Inputs is null
                ? null
                : new EvidenceProducerInputs(producer.Inputs.Include.Order(StringComparer.Ordinal).ToArray(),
                    (producer.Inputs.Exclude ?? []).Order(StringComparer.Ordinal).ToArray())
        };
    }

    /// <summary>Computes a content key from an exact Git worktree inventory; all expected optimization failures are safe ineligibility.</summary>
    internal static async Task<EvidenceProducerIdentityResult> BuildAsync(EvidenceProducerIdentityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stage = "validation";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(PreparationBudget);
            ValidateRequest(request);
            var root = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(request.RepositoryRoot));
            stage = "repository";
            var repositoryRoot = await GitAsync(root, ["rev-parse", "--show-toplevel"], deadline.Token).ConfigureAwait(false);
            repositoryRoot = CoverageRunOutputLease.NormalizePlatformPath(Path.GetFullPath(repositoryRoot));
            if (!string.Equals(repositoryRoot.TrimEnd(Path.DirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return Ineligible("reuse.identity.repository_mismatch", "The requested directory is not the repository root.");
            var commonDirectoryPath = await GitAsync(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], deadline.Token).ConfigureAwait(false);
            var commonPath = Path.GetFullPath(commonDirectoryPath);
            stage = "namespace";
            var commonInfo = new DirectoryInfo(commonPath);
            if (!commonInfo.Exists || (commonInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return Ineligible("reuse.identity.repository_unverified", "The Git common directory could not be verified safely.");
            commonPath = commonInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? commonPath;
            using var commonDirectory = CoverageRunOutputLease.AcquireArtifactDirectory(commonPath, false, false)
                ?? throw new InvalidOperationException("Git common directory is unavailable.");
            var namespaceId = Hash(Encoding.UTF8.GetBytes(commonDirectory.ArtifactDirectoryIdentity));
            stage = "inventory";
            var listed = await GitAsync(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], deadline.Token, raw: true).ConfigureAwait(false);
            var paths = ExistingPaths(root, ParsePaths(listed));
            var includes = request.Producer.Inputs!.Include;
            var excludes = request.Producer.Inputs.Exclude ?? [];
            var indexedPaths = await GitAsync(root, ["ls-files", "--stage", "-z"], deadline.Token, raw: true).ConfigureAwait(false);
            if (HasSelectedNestedRepository(root, paths, indexedPaths, includes, deadline.Token))
                return Ineligible("reuse.identity.nested_repository", "A declared include pattern may select files inside a nested Git repository or submodule.");
            var selected = paths.Where(path => includes.Any(pattern => Matches(pattern, path, deadline.Token))
                && !excludes.Any(pattern => Matches(pattern, path, deadline.Token))).Order(StringComparer.Ordinal).ToArray();
            if (CheckFileCountLimit(selected.Length) is { } fileCountLimit) return fileCountLimit;
            if (selected.Length == 0) return Ineligible("reuse.identity.empty", "The declared input patterns select no files.");
            var metadataBytes = selected.Sum(static path => (long)Encoding.UTF8.GetByteCount(path) + 64);
            if (CheckEstimatedMetadataLimit(selected.Length, metadataBytes) is { } estimatedMetadataLimit) return estimatedMetadataLimit;
            if (HasIdentityCollision(selected)) return Ineligible("reuse.identity.path_collision", "Selected paths have ambiguous filesystem identity.");

            var files = new List<EvidenceProducerInputFile>(selected.Length);
            long encodedMetadataBytes = 2;
            stage = "hashing";
            foreach (var relativePath in selected)
            {
                deadline.Token.ThrowIfCancellationRequested();
                stage = "path validation";
                var fullPath = ResolveContainedPath(root, relativePath);
                stage = "secure file open";
                await using var stream = CoverageRunArtifactReader.OpenRegularFile(Path.GetDirectoryName(root)!, root, fullPath);
                stage = "native file state";
                var before = CoverageRunOutputLease.GetArtifactObjectState(stream.SafeFileHandle);
                stage = "file hashing";
                var digest = await SHA256.HashDataAsync(stream, deadline.Token).ConfigureAwait(false);
                var after = CoverageRunOutputLease.GetArtifactObjectState(stream.SafeFileHandle);
                if (after != before || stream.Length != before.Length)
                    return Ineligible("reuse.identity.changed_during_read", "An input changed while its identity was being prepared.");
                stage = "file mode";
                var mode = GetMode(fullPath);
                var file = new EvidenceProducerInputFile(relativePath, mode, before.Length, Convert.ToHexString(digest).ToLowerInvariant());
                encodedMetadataBytes += JsonSerializer.SerializeToUtf8Bytes(file, CanonicalJson).Length + (files.Count == 0 ? 0 : 1);
                if (CheckEncodedMetadataLimit(files.Count + 1, encodedMetadataBytes) is { } encodedMetadataLimit)
                    return encodedMetadataLimit;
                files.Add(file);
            }

            // Re-enumeration catches additions, deletions, renames and changed selection while hashing.
            var finalListing = await GitAsync(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], deadline.Token, raw: true).ConfigureAwait(false);
            if (!paths.SequenceEqual(ExistingPaths(root, ParsePaths(finalListing)), StringComparer.Ordinal))
                return Ineligible("reuse.identity.changed_during_read", "The repository input inventory changed during identity preparation.");

            var materialError = ValidateMaterial(request.ExtraMaterial);
            stage = "key";
            if (materialError is not null) return Ineligible("reuse.identity.material_invalid", materialError);
            if (RequiresPatch(request.Producer) && (!ValidDigest(request.CapturedPatchSha256) || !ValidDigest(request.Plan.PatchSha256)))
                return Ineligible("reuse.identity.patch_unavailable", "The producer requires the exact captured patch digest.");
            if ((RequiresPatch(request.Producer) || request.Plan.PatchSha256 is not null)
                && !string.Equals(request.Plan.PatchSha256, request.CapturedPatchSha256, StringComparison.OrdinalIgnoreCase))
                return Ineligible("reuse.identity.patch_mismatch", "The captured patch digest does not match the planned patch.");

            var resourceDeclarations = request.Plan.Profile.Resources.ToDictionary(static resource => resource.Id, StringComparer.Ordinal);
            var requiredIds = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(request.Producer.RequiredResources);
            while (pending.TryPop(out var id))
            {
                if (!requiredIds.Add(id)) continue;
                if (!resourceDeclarations.TryGetValue(id, out var declaration)) return Ineligible("reuse.identity.resource_unavailable", "A required resource declaration is unavailable.");
                foreach (var dependency in declaration.Requires) pending.Push(dependency);
            }
            var resources = request.Plan.Profile.Resources
                .Where(resource => requiredIds.Contains(resource.Id))
                .OrderBy(static resource => resource.Id, StringComparer.Ordinal)
                .Select(resource => new { declaration = resource with { Requires = resource.Requires.Order(StringComparer.Ordinal).ToArray() }, identity = request.ResourceIdentities.GetValueOrDefault(resource.Id) }).ToArray();
            if (resources.Any(static resource => string.IsNullOrWhiteSpace(resource.identity)))
                return Ineligible("reuse.identity.resource_unavailable", "An effective required-resource identity is unavailable.");

            var components = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["declaration"] = CanonicalDeclarationHash(request.Producer),
                ["implementation"] = Hash(Encoding.UTF8.GetBytes(request.ImplementationIdentity)),
                ["resources"] = Hash(JsonSerializer.SerializeToUtf8Bytes(resources, CanonicalJson)),
                ["tools"] = Hash(JsonSerializer.SerializeToUtf8Bytes(Sorted(request.EffectiveToolIdentity), CanonicalJson)),
                ["material"] = Hash(JsonSerializer.SerializeToUtf8Bytes(Sorted(request.ExtraMaterial), CanonicalJson)),
                ["platform"] = Hash(Encoding.UTF8.GetBytes($"{System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}|{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}|{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}")),
                ["patch"] = Hash(Encoding.UTF8.GetBytes(RequiresPatch(request.Producer) ? request.CapturedPatchSha256!.ToLowerInvariant() : string.Empty)),
                ["files"] = Hash(JsonSerializer.SerializeToUtf8Bytes(files, CanonicalJson))
            };
            var key = Hash(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, components }, CanonicalJson));
            return new EvidenceProducerIdentityResult(new EvidenceProducerIdentity(key, namespaceId, files, components), "reuse.identity.ready", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimeLimitResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or Win32Exception)
        {
            return Ineligible("reuse.identity.unavailable", $"Repository identity could not be safely prepared during {stage} ({exception.GetType().Name}).");
        }
    }

    /// <summary>Returns a safe, actionable ineligibility when the selected input file count exceeds its bound.</summary>
    internal static EvidenceProducerIdentityResult? CheckFileCountLimit(int fileCount)
    {
        return fileCount > MaximumFiles
            ? Ineligible("reuse.identity.files_limit",
                $"Selected input inventory contains {FormatNumber(fileCount)} files; limit is {FormatNumber(MaximumFiles)}. Narrow declared include/exclude patterns and retry.")
            : null;
    }

    /// <summary>Returns a safe, actionable ineligibility when estimated input metadata exceeds its byte bound.</summary>
    internal static EvidenceProducerIdentityResult? CheckEstimatedMetadataLimit(int fileCount, long metadataBytes)
    {
        return metadataBytes > MaximumMetadataBytes
            ? Ineligible("reuse.identity.metadata_limit",
                $"Estimated input metadata is {FormatNumber(metadataBytes)} bytes across {FormatNumber(fileCount)} files; limit is {FormatNumber(MaximumMetadataBytes)} bytes. Narrow declared include/exclude patterns and retry.")
            : null;
    }

    /// <summary>Returns a safe, actionable ineligibility when encoded input metadata exceeds its byte bound.</summary>
    internal static EvidenceProducerIdentityResult? CheckEncodedMetadataLimit(int fileCount, long metadataBytes)
    {
        return metadataBytes > MaximumMetadataBytes
            ? Ineligible("reuse.identity.metadata_limit",
                $"Encoded input metadata is {FormatNumber(metadataBytes)} bytes after {FormatNumber(fileCount)} files; limit is {FormatNumber(MaximumMetadataBytes)} bytes. Narrow declared include/exclude patterns and retry.")
            : null;
    }

    /// <summary>Creates the stable time-limit result with its configured millisecond bound and next action.</summary>
    internal static EvidenceProducerIdentityResult TimeLimitResult()
        => Ineligible("reuse.identity.time_limit",
            $"Producer identity preparation exceeded its {FormatNumber((long)PreparationBudget.TotalMilliseconds)} ms time budget. Narrow declared inputs or reduce repository activity and retry.");

    /// <summary>Returns the first full relative path that differs between ordered inventories, or null when equal.</summary>
    internal static string? FirstDifferingPath(IReadOnlyList<EvidenceProducerInputFile> left, IReadOnlyList<EvidenceProducerInputFile> right)
    {
        var count = Math.Min(left.Count, right.Count);
        for (var index = 0; index < count; index++)
        {
            if (left[index] != right[index])
            {
                var first = left[index].RelativePath;
                var second = right[index].RelativePath;
                return StringComparer.Ordinal.Compare(first, second) <= 0 ? first : second;
            }
        }
        if (left.Count != right.Count) return (left.Count > count ? left[count] : right[count]).RelativePath;
        return null;
    }

    /// <summary>Validates an input inventory path while preserving its exact Unicode and whitespace spelling.</summary>
    internal static bool IsValidInputRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains('\0')
            || path.Any(char.IsControl) || path[0] == '/'
            || Path.IsPathRooted(path) || path.Contains(':')) return false;
        return path.Split('/').All(static segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static void ValidateRequest(EvidenceProducerIdentityRequest request)
    {
        if (request.Producer.Inputs is null) throw new InvalidOperationException("Inputs are not declared.");
        if (string.IsNullOrWhiteSpace(request.ImplementationIdentity)) throw new InvalidOperationException("Implementation identity is required.");
        if (request.Producer.Inputs.Include is null || request.Producer.Inputs.Include.Count == 0) throw new InvalidOperationException("At least one include pattern is required.");
        foreach (var pattern in request.Producer.Inputs.Include.Concat(request.Producer.Inputs.Exclude ?? [])) ValidatePattern(pattern);
    }

    private static void ValidatePattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern.StartsWith('/') || pattern.Contains('\\') || Path.IsPathRooted(pattern)) throw new ArgumentException("Invalid path pattern.");
        var segments = pattern.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".." || (segment.Contains("**", StringComparison.Ordinal) && segment != "**"))) throw new ArgumentException("Invalid path pattern.");
        if (pattern.Any(static c => c is '\0' or '?' or '[' or ']')) throw new ArgumentException("Invalid path pattern.");
    }

    private static bool Matches(string pattern, string path, CancellationToken token)
    {
        var segments = path.Split('/');
        var reachable = new bool[segments.Length + 1];
        reachable[0] = true;
        foreach (var part in pattern.Split('/'))
        {
            token.ThrowIfCancellationRequested();
            var next = new bool[reachable.Length];
            if (part == "**")
            {
                var reachablePrefix = false;
                for (var index = 0; index < reachable.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    reachablePrefix |= reachable[index];
                    next[index] = reachablePrefix;
                }
            }
            else
                for (var index = 0; index < segments.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    next[index + 1] = reachable[index] && SegmentMatches(part, segments[index], token);
                }
            reachable = next;
        }
        return reachable[^1];
    }
    private static bool SegmentMatches(string pattern, string value, CancellationToken token)
    {
        var p = 0; var v = 0; var star = -1; var retry = 0;
        while (v < value.Length)
        {
            token.ThrowIfCancellationRequested();
            if (p < pattern.Length && (pattern[p] == value[v] || pattern[p] == '*'))
            { if (pattern[p] == '*') { star = p++; retry = v; } else { p++; v++; } }
            else if (star >= 0) { p = star + 1; v = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static string ResolveContainedPath(string root, string relative)
    {
        var current = root;
        var segments = relative.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked input is ineligible.");
            if (i < segments.Length - 1 && (attributes & FileAttributes.Directory) == 0) throw new InvalidOperationException("Invalid input ancestor.");
        }
        var full = Path.GetFullPath(current);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Input escaped repository root.");
        return full;
    }

    private static bool HasIdentityCollision(string[] paths)
    {
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var identities = paths.Select(path => path.Normalize(NormalizationForm.FormC));
        return identities.Distinct(comparer).Count() != paths.Length;
    }

    private static bool HasSelectedNestedRepository(string root, IReadOnlyList<string> listedPaths, string indexedPaths,
        IReadOnlyList<string> includePatterns, CancellationToken token)
    {
        var nestedRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in indexedPaths.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            var separator = entry.IndexOf('\t');
            if (separator > 0 && entry.StartsWith("160000 ", StringComparison.Ordinal))
                nestedRoots.Add(entry[(separator + 1)..]);
        }

        foreach (var listedPath in listedPaths)
        {
            token.ThrowIfCancellationRequested();
            var current = Path.Combine(root, listedPath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.Directory) == 0)
                    current = Path.GetDirectoryName(current)!;
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            while (!string.Equals(current.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) break;
                    if (File.Exists(Path.Combine(current, ".git")) || Directory.Exists(Path.Combine(current, ".git")))
                        nestedRoots.Add(Path.GetRelativePath(root, current).Replace(Path.DirectorySeparatorChar, '/'));
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current)!;
            }
        }

        foreach (var nestedRoot in nestedRoots)
        {
            token.ThrowIfCancellationRequested();
            if (includePatterns.Any(pattern => CouldSelectDescendant(pattern, nestedRoot, token))) return true;
        }
        return false;
    }

    private static bool CouldSelectDescendant(string pattern, string directory, CancellationToken token)
    {
        var patternSegments = pattern.Split('/');
        var directorySegments = directory.Split('/');
        var reachable = new bool[patternSegments.Length + 1, directorySegments.Length + 1];
        reachable[0, 0] = true;
        for (var patternIndex = 0; patternIndex < patternSegments.Length; patternIndex++)
        {
            for (var directoryIndex = 0; directoryIndex <= directorySegments.Length; directoryIndex++)
            {
                token.ThrowIfCancellationRequested();
                if (!reachable[patternIndex, directoryIndex]) continue;
                if (patternSegments[patternIndex] == "**")
                {
                    reachable[patternIndex + 1, directoryIndex] = true;
                    if (directoryIndex < directorySegments.Length)
                        reachable[patternIndex, directoryIndex + 1] = true;
                }
                else if (directoryIndex < directorySegments.Length
                    && SegmentMatches(patternSegments[patternIndex], directorySegments[directoryIndex], token))
                {
                    reachable[patternIndex + 1, directoryIndex + 1] = true;
                }
            }
        }
        return Enumerable.Range(0, patternSegments.Length).Any(index => reachable[index, directorySegments.Length]);
    }

    private static int GetMode(string path)
    {
        // Include all available permission bits: a producer can observe read-only or non-executable mode changes.
        // Windows has no Unix executable mode; its available read-only attribute still distinguishes file mode.
        if (OperatingSystem.IsWindows()) return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0 ? 0x8124 : 0x81B6;
        return 0x8000 | (int)File.GetUnixFileMode(path);
    }

    private static List<string> ParsePaths(string nulList) => nulList.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    private static List<string> ExistingPaths(string root, IEnumerable<string> paths) => paths.Where(path =>
    {
        try { _ = File.GetAttributes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }).ToList();
    private static Dictionary<string, string> Sorted(IReadOnlyDictionary<string, string> values) => values.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool ValidDigest(string? digest) => digest is { Length: 64 } && digest.All(Uri.IsHexDigit);
    private static bool RequiresPatch(EvidenceProducerDeclaration producer) => producer.CoverageGate is { MinPatchLinePercent: not null } or { MinPatchBranchPercent: not null };
    private static string? ValidateMaterial(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count > MaximumMaterialKeys)
            return $"Producer key material contains {FormatNumber(values.Count)} keys; limit is {FormatNumber(MaximumMaterialKeys)}. Remove keys and retry.";
        var observedCount = 0;
        var total = 0L;
        foreach (var (key, value) in values)
        {
            observedCount++;
            if (observedCount > MaximumMaterialKeys)
                return $"More than {FormatNumber(MaximumMaterialKeys)} producer key-material entries were observed ({FormatNumber(observedCount)} observed). Remove keys and retry.";
            if (string.IsNullOrEmpty(key))
                return "A producer key-material key name has length 0; minimum is 1. Provide a non-empty key name.";
            if (key.Length > MaximumMaterialKeyLength)
                return $"A producer key-material key name is {FormatNumber(key.Length)} UTF-16 code units; limit is {FormatNumber(MaximumMaterialKeyLength)}. Shorten key names and retry.";
            if (value is null)
                return "A producer key-material entry has a null value. Return non-null values within the declared limits.";
            var size = GetUtf8ByteCount(value);
            if (size > MaximumMaterialValueBytes)
                return $"A producer key-material value is {FormatNumber(size)} UTF-8 bytes; limit is {FormatNumber(MaximumMaterialValueBytes)}. Reduce the value and retry.";
            total += GetUtf8ByteCount(key) + size;
            if (total > MaximumMaterialTotalBytes)
                return $"Producer key material totals {FormatNumber(total)} UTF-8 bytes; limit is {FormatNumber(MaximumMaterialTotalBytes)}. Reduce key and value sizes and retry.";
        }
        return null;
    }

    private static long GetUtf8ByteCount(string value)
    {
        long bytes = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character <= 0x7f) bytes++;
            else if (character <= 0x7ff) bytes += 2;
            else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                bytes += 4;
                index++;
            }
            else bytes += 3;
        }

        return bytes;
    }

    private static string FormatNumber(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static EvidenceProducerIdentityResult Ineligible(string reason, string diagnostic) => new(null, reason, diagnostic);

    private static async Task<string> GitAsync(string root, string[] arguments, CancellationToken token, bool raw = false)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.StartInfo.StandardOutputEncoding = new UTF8Encoding(false, true);
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR" })
            process.StartInfo.Environment.Remove(variable);
        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "color.ui=false" })
            process.StartInfo.ArgumentList.Add(argument);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumMetadataBytes + 1, token);
        var stderr = ReadBoundedAsync(process.StandardError, 64 * 1024, token);
        var exit = process.WaitForExitAsync(token);
        try
        {
            await Task.WhenAll(exit, stdout, stderr).ConfigureAwait(false);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(exit, stdout, stderr).ConfigureAwait(false); } catch { /* Preserve the original process/reader/cancellation fault. */ }
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        if (output.Length > MaximumMetadataBytes) throw new InvalidOperationException("Git inventory output exceeded its limit.");
        if (process.ExitCode != 0) throw new InvalidOperationException("Git identity query failed.");
        return raw ? output : output.TrimEnd('\r', '\n');
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var buffer = new char[8192]; var output = new StringBuilder(Math.Min(limit, 8192)); var exceeded = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) break;
            var remaining = limit - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining) exceeded = true;
        }
        if (exceeded) throw new InvalidOperationException("Command output exceeded its limit.");
        return output.ToString();
    }

}
