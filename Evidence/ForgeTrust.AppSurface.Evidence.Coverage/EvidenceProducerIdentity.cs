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
    IReadOnlyDictionary<string, string> ComponentHashes)
{
    /// <summary>Checks whether another identity addresses the same receipt.</summary>
    /// <param name="other">The identity obtained by revalidating the producer's inputs.</param>
    /// <returns>True when both the content key and repository namespace match exactly.</returns>
    internal bool HasSameReceiptKey(EvidenceProducerIdentity? other) =>
        other is not null && Key == other.Key && RepositoryNamespace == other.RepositoryNamespace;
}

/// <summary>Stable version 1 names shared by the producer key and the persisted receipt inventory.</summary>
/// <remarks>Changing a wire name changes existing keys and can make stored inventories unreadable.</remarks>
internal static class EvidenceProducerIdentityComponents
{
    internal const string Declaration = "declaration";
    internal const string Implementation = "implementation";
    internal const string Resources = "resources";
    internal const string Tools = "tools";
    internal const string Material = "material";
    internal const string Platform = "platform";
    internal const string Patch = "patch";
    internal const string Files = "files";

    /// <summary>Recognizes an exact version 1 name. Persisted inventories may contain a subset of these names.</summary>
    internal static bool IsVersionOneName(string name) =>
        name is Declaration or Implementation or Resources or Tools or Material or Platform or Patch or Files;
}

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
            ValidateRequest(request, deadline.Token);
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
            using var commonDirectory = CoverageRunOutputLease.AcquireArtifactDirectory(commonPath, false, false)
                ?? throw new InvalidOperationException("Git common directory is unavailable.");
            var namespaceId = Hash(Encoding.UTF8.GetBytes(commonDirectory.ArtifactDirectoryIdentity));
            stage = "inventory";
            var paths = await ReadExistingInventoryAsync(root, deadline.Token).ConfigureAwait(false);
            var includes = request.Producer.Inputs!.Include;
            var excludes = request.Producer.Inputs.Exclude ?? [];
            var indexedPaths = await GitAsync(root, ["ls-files", "--stage", "-z"], deadline.Token, raw: true).ConfigureAwait(false);
            if (HasSelectedNestedRepository(root, paths, indexedPaths, includes, deadline.Token))
                return Ineligible("reuse.identity.nested_repository", "A declared include pattern may select files inside a nested Git repository or submodule.");
            if (HasSelectedLinkedDirectory(root, paths, includes, deadline.Token))
                return Ineligible("reuse.identity.linked_input", "A declared include pattern may select files through a linked directory.");
            var selected = paths.Where(path => includes.Any(pattern => EvidenceProducerInputPattern.MatchesPath(pattern, path, deadline.Token))
                && !excludes.Any(pattern => EvidenceProducerInputPattern.MatchesPath(pattern, path, deadline.Token))).ToArray();
            if (CheckFileCountLimit(selected.Length) is { } fileCountLimit) return fileCountLimit;
            if (selected.Length == 0) return Ineligible("reuse.identity.empty", "The declared input patterns select no files.");
            long metadataBytes = 0;
            foreach (var path in selected)
            {
                deadline.Token.ThrowIfCancellationRequested();
                metadataBytes += (long)Encoding.UTF8.GetByteCount(path) + 64;
            }
            if (CheckEstimatedMetadataLimit(selected.Length, metadataBytes) is { } estimatedMetadataLimit) return estimatedMetadataLimit;
            if (HasIdentityCollision(selected, deadline.Token)) return Ineligible("reuse.identity.path_collision", "Selected paths have ambiguous filesystem identity.");

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
            var finalPaths = await ReadExistingInventoryAsync(root, deadline.Token).ConfigureAwait(false);
            if (!paths.SequenceEqual(finalPaths, StringComparer.Ordinal))
                return Ineligible("reuse.identity.changed_during_read", "The repository input inventory changed during identity preparation.");

            ArgumentNullException.ThrowIfNull(request.ExtraMaterial);
            var material = EvidenceKeyMaterialSnapshot.Capture(request.ExtraMaterial, deadline.Token);
            stage = "key";
            var requiresPatch = request.Producer.CoverageGate?.RequiresPatch == true;
            if (requiresPatch && (!ValidDigest(request.CapturedPatchSha256) || !ValidDigest(request.Plan.PatchSha256)))
                return Ineligible("reuse.identity.patch_unavailable", "The producer requires the exact captured patch digest.");
            if ((requiresPatch || request.Plan.PatchSha256 is not null)
                && !string.Equals(request.Plan.PatchSha256, request.CapturedPatchSha256, StringComparison.OrdinalIgnoreCase))
                return Ineligible("reuse.identity.patch_mismatch", "The captured patch digest does not match the planned patch.");

            var resourceDeclarations = new Dictionary<string, EvidenceResourceDeclaration>(StringComparer.Ordinal);
            foreach (var resource in request.Plan.Profile.Resources)
            {
                deadline.Token.ThrowIfCancellationRequested();
                resourceDeclarations.Add(resource.Id, resource);
            }
            var requiredResources = new List<EvidenceResourceDeclaration>();
            foreach (var (_, declaration) in EvidenceResourceReachability.Enumerate(
                resourceDeclarations, request.Producer.RequiredResources, deadline.Token))
            {
                if (declaration is null)
                    return Ineligible("reuse.identity.resource_unavailable", "A required resource declaration is unavailable.");
                requiredResources.Add(declaration);
            }
            var resources = requiredResources
                .OrderBy(static resource => resource.Id, StringComparer.Ordinal)
                .Select(resource => new { declaration = resource with { Requires = resource.Requires.Order(StringComparer.Ordinal).ToArray() }, identity = request.ResourceIdentities.GetValueOrDefault(resource.Id) }).ToArray();
            deadline.Token.ThrowIfCancellationRequested();
            if (resources.Any(static resource => string.IsNullOrWhiteSpace(resource.identity)))
                return Ineligible("reuse.identity.resource_unavailable", "An effective required-resource identity is unavailable.");

            var components = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                [EvidenceProducerIdentityComponents.Declaration] = CanonicalDeclarationHash(request.Producer),
                [EvidenceProducerIdentityComponents.Implementation] = Hash(Encoding.UTF8.GetBytes(request.ImplementationIdentity)),
                [EvidenceProducerIdentityComponents.Resources] = Hash(JsonSerializer.SerializeToUtf8Bytes(resources, CanonicalJson)),
                [EvidenceProducerIdentityComponents.Tools] = Hash(JsonSerializer.SerializeToUtf8Bytes(Sorted(request.EffectiveToolIdentity), CanonicalJson)),
                [EvidenceProducerIdentityComponents.Material] = Hash(JsonSerializer.SerializeToUtf8Bytes<IReadOnlyDictionary<string, string>>(material, CanonicalJson)),
                [EvidenceProducerIdentityComponents.Platform] = Hash(Encoding.UTF8.GetBytes($"{System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}|{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}|{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}")),
                [EvidenceProducerIdentityComponents.Patch] = Hash(Encoding.UTF8.GetBytes(requiresPatch ? request.CapturedPatchSha256!.ToLowerInvariant() : string.Empty)),
                [EvidenceProducerIdentityComponents.Files] = Hash(JsonSerializer.SerializeToUtf8Bytes(files, CanonicalJson))
            };
            var key = Hash(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, components }, CanonicalJson));
            deadline.Token.ThrowIfCancellationRequested();
            return new EvidenceProducerIdentityResult(new EvidenceProducerIdentity(key, namespaceId, files, components), "reuse.identity.ready", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimeLimitResult();
        }
        catch (EvidenceKeyMaterialValidationException exception)
        {
            return Ineligible("reuse.identity.material_invalid", exception.Message);
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

    private static void ValidateRequest(EvidenceProducerIdentityRequest request, CancellationToken token)
    {
        if (request.Producer.Inputs is null) throw new InvalidOperationException("Inputs are not declared.");
        if (string.IsNullOrWhiteSpace(request.ImplementationIdentity)) throw new InvalidOperationException("Implementation identity is required.");
        if (!IsWellFormedUtf16(request.ImplementationIdentity, token)) throw new ArgumentException("Implementation identity contains malformed UTF-16.");
        if (request.Producer.Inputs.Include is null || request.Producer.Inputs.Include.Count == 0) throw new InvalidOperationException("At least one include pattern is required.");
        var producer = request.Producer;
        if (!IsWellFormedUtf16(producer.Id, token) || !IsWellFormedUtf16(producer.Kind, token)
            || !IsWellFormedUtf16(producer.Version, token))
            throw new ArgumentException("Producer declaration contains malformed UTF-16.");
        foreach (var value in producer.RequiredResources.Concat(producer.AssertionIds))
        {
            token.ThrowIfCancellationRequested();
            if (!IsWellFormedUtf16(value, token)) throw new ArgumentException("Producer declaration contains malformed UTF-16.");
        }

        foreach (var slot in producer.ArtifactSlots)
        {
            token.ThrowIfCancellationRequested();
            if (!IsWellFormedUtf16(slot.LogicalName, token) || !IsWellFormedUtf16(slot.RelativeRoot, token)
                || !IsWellFormedUtf16(slot.MediaType, token))
                throw new ArgumentException("Producer declaration contains malformed UTF-16.");
        }

        if (producer.CoverageGate?.PatchLineMode is { } patchLineMode && !IsWellFormedUtf16(patchLineMode, token))
            throw new ArgumentException("Producer declaration contains malformed UTF-16.");
        if (producer.CoverageExecution?.Configuration is { } configuration && !IsWellFormedUtf16(configuration, token))
            throw new ArgumentException("Producer declaration contains malformed UTF-16.");
        if (producer.CoverageArtifactRetention is { } retention && !IsWellFormedUtf16(retention, token))
            throw new ArgumentException("Producer declaration contains malformed UTF-16.");
        foreach (var (key, value) in request.ResourceIdentities.Concat(request.EffectiveToolIdentity))
        {
            token.ThrowIfCancellationRequested();
            if (key is null || !IsWellFormedUtf16(key, token) || value is null || !IsWellFormedUtf16(value, token))
                throw new ArgumentException("Execution identity contains malformed UTF-16.");
        }

        foreach (var pattern in request.Producer.Inputs.Include.Concat(request.Producer.Inputs.Exclude ?? []))
        {
            if (!EvidenceProducerInputPattern.IsValid(pattern, token))
                throw new ArgumentException("Invalid path pattern.");
        }

        foreach (var resource in request.Plan.Profile.Resources)
        {
            token.ThrowIfCancellationRequested();
            if (!IsWellFormedUtf16(resource.Id, token) || !IsWellFormedUtf16(resource.Readiness, token)
                || resource.Requires.Any(dependency => !IsWellFormedUtf16(dependency, token)))
                throw new ArgumentException("Resource declaration contains malformed UTF-16.");
        }
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

    private static bool HasIdentityCollision(string[] paths, CancellationToken token)
    {
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var identities = new HashSet<string>(comparer);
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!identities.Add(path.Normalize(NormalizationForm.FormC))) return true;
        }
        return false;
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
            if (IncludeMaySelectPathOrDescendant(nestedRoot, includePatterns, token)) return true;
        }
        return false;
    }

    private static bool HasSelectedLinkedDirectory(string root, IReadOnlyList<string> listedPaths,
        IReadOnlyList<string> includePatterns, CancellationToken token)
    {
        foreach (var path in listedPaths)
        {
            token.ThrowIfCancellationRequested();
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0)
                continue;
            if (IncludeMaySelectPathOrDescendant(path, includePatterns, token))
                return true;
        }
        return false;
    }

    /// <summary>Determines whether an include pattern selects a path or could select content beneath it.</summary>
    /// <param name="path">Repository-relative path to test, such as a gitlink or directory link.</param>
    /// <param name="includePatterns">Validated producer include patterns.</param>
    /// <param name="token">Cancellation token for bounded pattern matching.</param>
    /// <returns><see langword="true"/> when a pattern selects the path or a possible descendant.</returns>
    internal static bool IncludeMaySelectPathOrDescendant(string path, IReadOnlyList<string> includePatterns, CancellationToken token)
        => includePatterns.Any(pattern => EvidenceProducerInputPattern.MayMatchPathOrDescendant(pattern, path, token));

    private static int GetMode(string path)
    {
        // Include all available permission bits: a producer can observe read-only or non-executable mode changes.
        // Windows has no Unix executable mode; its available read-only attribute still distinguishes file mode.
        if (OperatingSystem.IsWindows()) return (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0 ? 0x8124 : 0x81B6;
        return 0x8000 | (int)File.GetUnixFileMode(path);
    }

    private static async Task<string[]> ReadExistingInventoryAsync(string root, CancellationToken token)
    {
        var listed = await GitAsync(root, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], token, raw: true).ConfigureAwait(false);
        return listed.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(path =>
            {
                try
                {
                    _ = File.GetAttributes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
                    return true;
                }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
            })
            .ToArray();
    }

    private static Dictionary<string, string> Sorted(IReadOnlyDictionary<string, string> values) => values.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool ValidDigest(string? digest) => digest is { Length: 64 } && digest.All(Uri.IsHexDigit);
    /// <summary>Checks whether a string contains only Unicode scalar values encoded as well-formed UTF-16.</summary>
    /// <param name="value">String to inspect.</param>
    /// <param name="token">Optional cancellation token checked during long inputs.</param>
    /// <returns><see langword="true"/> when every surrogate belongs to a valid pair or is absent.</returns>
    private static bool IsWellFormedUtf16(string value, CancellationToken token)
    {
        for (var index = 0; index < value.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
                index++;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
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
