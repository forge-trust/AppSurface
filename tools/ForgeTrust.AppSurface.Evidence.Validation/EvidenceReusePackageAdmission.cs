using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using static ForgeTrust.AppSurface.Evidence.Validation.PackedCandidatePackages;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Validates candidate package archives before the packed reuse proof creates consumer output.</summary>
/// <remarks>
/// Admission reads a bounded set of candidate archives, verifies their package metadata and dependency graph,
/// and records hashes for package and bundled assembly bytes. It does not create proof or consumer output.
/// </remarks>
internal static class EvidenceReusePackageAdmission
{
    /// <summary>Gets the diagnostic scope shared with the packed reuse proof.</summary>
    internal const string ContractScope = "packed-reuse/";
    /// <summary>Gets the diagnostic code for an incoherent bundled tool graph version.</summary>
    internal const string ToolBundleVersionContract = "tool-bundled-graph-version";
    /// <summary>Gets the diagnostic code for a missing or inexact private dependency edge.</summary>
    internal const string PrivateDependencyContract = "exact-private-edge-in-every-framework-group";

    /// <summary>Gets the minimum candidate feed closure in its stable admission order.</summary>
    internal static IReadOnlyList<string> CandidateMinimumPackageIds { get; } =
        Array.AsReadOnly(RequiredArchives.Select(package => package.Id).ToArray());

    /// <summary>Gets the bundled tool package identities attested by the reuse proof.</summary>
    internal static IReadOnlyList<string> BundledToolPackageIds { get; } = Array.AsReadOnly(new[]
    {
        ToolId, EvidenceCliId, ContractsId, PlannerId, SupportId, CoverageId
    });

    /// <summary>Validates a candidate feed and records package and assembly hashes from its actual archive bytes.</summary>
    /// <param name="feed">Local candidate feed containing the complete same-version family.</param>
    /// <param name="version">Exact candidate package and dependency-graph version.</param>
    /// <param name="expectedSourceCommit">Trusted full source commit matching every candidate nuspec.</param>
    /// <param name="cancellationToken">Cancels archive reading and hashing.</param>
    /// <returns>Admitted archives and the verified assembly hashes from the tool archive.</returns>
    /// <exception cref="InvalidOperationException">A candidate package violates a stable admission contract.</exception>
    internal static async Task<AdmittedFeed> InspectFeedAsync(string feed, string version,
        string expectedSourceCommit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feed);
        Require(PackedCandidateIdentity.IsVersion(version), "candidate-version");
        Require(PackedCandidateIdentity.IsSourceCommit(expectedSourceCommit), "source-commit-pin");
        var result = new Dictionary<string, PackageProof>(StringComparer.Ordinal);
        Dictionary<string, string>? bundledAssemblyHashes = null;
        var packages = RequiredArchives.Concat(OptionalArchives.Where(package =>
            File.Exists(PathUtils.PathUnder(feed, package.ArchiveName(version))))).ToArray();
        var ids = packages.Select(package => package.Id).ToArray();
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = package.Id;
            var path = PathUtils.PathUnder(feed, package.ArchiveName(version));
            Require(File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                && new FileInfo(path).Length <= PackedCandidateArchive.MaximumBytes, "bounded-regular-candidate-archive");
            var (capture, archiveBytes) = await PackedCandidateArchive.CaptureAsync(path, cancellationToken).ConfigureAwait(false);
            Require(capture != PackedCandidateArchive.CaptureStatus.TooLarge, "bounded-regular-candidate-archive");
            Require(capture == PackedCandidateArchive.CaptureStatus.Complete, "stable-candidate-archive-length");
            using var archiveStream = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            Require(archive.Entries.Count <= 10000 && archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == archive.Entries.Count,
                "bounded-unambiguous-archive");
            var specification = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            var parsedMetadata = PackedCandidateArchive.ReadBoundedMetadata(specification);
            Require(parsedMetadata is not null, "bounded-nuspec");
            var metadata = parsedMetadata!;
            string Value(string name) => metadata.Elements().Single(element => element.Name.LocalName == name).Value;
            Require(Value("id") == id && Value("version") == version, "exact-candidate-identity");
            Require((string?)metadata.Elements().Single(element => element.Name.LocalName == "repository").Attribute("commit") == expectedSourceCommit,
                "exact-candidate-source-commit");
            var privateTarget = PrivatePackageEdges.RequiredDependency(id);
            var dependencies = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "dependencies");
            if (privateTarget is not null)
            {
                Require(dependencies is not null, "private-dependency-required");
                Require(PrivatePackageEdges.HasSingleExactPin(dependencies!, privateTarget, version), PrivateDependencyContract);
            }
            foreach (var dependency in dependencies?.Descendants().Where(element => element.Name.LocalName == "dependency") ?? [])
            {
                var dependencyId = (string?)dependency.Attribute("id") ?? "";
                if (!dependencyId.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)) continue;
                Require(ids.Contains(dependencyId, StringComparer.Ordinal) && (string?)dependency.Attribute("version") is { } range
                    && (range == version || range == $"[{version}]"), "same-family-dependency-graph");
            }
            Require(archive.Entries.Sum(entry => entry.Length) <= 1024L * 1024 * 1024, "bounded-uncompressed-package");
            var assemblyEntry = id == ToolId
                ? archive.Entries.Single(entry => entry.FullName.StartsWith("tools/", StringComparison.Ordinal) && Path.GetFileName(entry.FullName) == id + ".dll")
                : archive.Entries.Single(entry => entry.FullName == $"lib/net10.0/{id}.dll");
            var assemblySha = await HashEntryAsync(assemblyEntry, cancellationToken).ConfigureAwait(false);
            if (id == ToolId)
                bundledAssemblyHashes = await ReadVerifiedBundledAssemblyHashesAsync(archive, ids, version, cancellationToken).ConfigureAwait(false);
            result.Add(id, new(id, version, expectedSourceCommit, path,
                Convert.ToHexStringLower(SHA256.HashData(archiveBytes)), assemblyEntry.FullName, assemblySha));
        }
        var toolAssemblyHashes = bundledAssemblyHashes ?? throw new InvalidOperationException("The required tool archive was not inspected.");
        foreach (var (id, hash) in toolAssemblyHashes)
        {
            if (result.TryGetValue(id, out var package))
                Require(hash == package.AssemblySha256,
                    id == EvidenceCliId ? "tool-bundled-cli-candidate-sha-before-output" : "tool-bundled-candidate-sha-before-output");
        }
        toolAssemblyHashes.Add(ToolId, result[ToolId].AssemblySha256);
        return new(result, toolAssemblyHashes);
    }

    private static async Task<Dictionary<string, string>> ReadVerifiedBundledAssemblyHashesAsync(
        ZipArchive archive, IReadOnlyList<string> candidatePackageIds, string version, CancellationToken cancellationToken)
    {
        var deps = archive.Entries.Single(entry => entry.FullName.StartsWith("tools/", StringComparison.Ordinal) && Path.GetFileName(entry.FullName) == ToolId + ".deps.json");
        Require(deps.Length <= 8L * 1024 * 1024, "bounded-tool-deps");
        using var stream = deps.Open();
        using var document = JsonDocument.Parse(stream);
        var libraries = PackedDependencyLibrary.Read(document.RootElement.GetProperty("libraries")).ToArray();
        // A tool bundles project outputs. PackageVersion can legitimately differ from their coherent
        // build version; exact package ABI is anchored independently by the candidate archive DLL hashes.
        var toolLibrary = libraries.Single(library => library.Entry.Name.StartsWith(ToolId + "/", StringComparison.Ordinal));
        var buildVersion = toolLibrary.Version;
        Require(buildVersion is not null, "tool-build-version");
        var bundledLibraries = libraries.Where(library => BundledToolPackageIds.Contains(
            library.Id, StringComparer.Ordinal)).ToArray();
        var bundledIds = bundledLibraries.Select(library => library.Id).ToHashSet(StringComparer.Ordinal);
        var candidateComponentIds = candidatePackageIds.Append(EvidenceCliId);
        var componentLibraries = libraries.Where(library => library.Id != ToolId
            && candidateComponentIds.Contains(library.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        var componentVersions = componentLibraries.Select(library => library.Version).Distinct(StringComparer.Ordinal).ToArray();
        Require(componentVersions.Length == 1 && componentVersions[0] is not null
            && (componentVersions[0] == version || componentVersions[0] == buildVersion),
            ToolBundleVersionContract);
        foreach (var library in componentLibraries)
            Require(candidateComponentIds.Contains(library.Id, StringComparer.Ordinal), ToolBundleVersionContract);
        foreach (var required in BundledToolPackageIds)
            Require(libraries.Any(library => library.Matches(required, required == ToolId ? buildVersion : componentVersions[0])),
                "tool-required-bundled-graph");
        Require(bundledLibraries.Length == BundledToolPackageIds.Count && bundledIds.SetEquals(BundledToolPackageIds),
            "tool-exact-bundled-graph");
        var bundledAssemblyHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bundledId in BundledToolPackageIds.Where(id => id != ToolId))
        {
            var bundled = archive.Entries.Single(entry => entry.FullName.StartsWith("tools/", StringComparison.Ordinal)
                && Path.GetFileName(entry.FullName) == bundledId + ".dll");
            bundledAssemblyHashes.Add(bundledId, await HashEntryAsync(bundled, cancellationToken).ConfigureAwait(false));
        }
        return bundledAssemblyHashes;
    }

    /// <summary>Hashes one archive entry without materializing its contents in memory.</summary>
    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var input = entry.Open();
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
    }

    private static void Require(bool condition, string contract) => ValidationProof.Require(condition, ContractScope + contract);

    /// <summary>Identity and byte evidence captured from the admitted feed.</summary>
    /// <param name="Packages">Verified archive records keyed by exact package ID.</param>
    /// <param name="ToolAssemblyHashes">Verified SHA-256 hashes for the tool and its bundled DLLs keyed by exact package ID.</param>
    internal sealed record AdmittedFeed(IReadOnlyDictionary<string, PackageProof> Packages,
        IReadOnlyDictionary<string, string> ToolAssemblyHashes);

    /// <summary>Candidate archive and assembly byte identity retained in the proof report.</summary>
    internal sealed record PackageProof(string Id, string Version, string SourceCommit, string Archive, string Sha256,
        string AssemblyEntry, string AssemblySha256);
}
