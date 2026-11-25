using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

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

    /// <summary>Gets the namespace prefix used by candidate package identities.</summary>
    internal const string Prefix = "ForgeTrust.AppSurface.";
    /// <summary>Gets the candidate tool package identity.</summary>
    internal const string ToolId = Prefix + "Cli";
    /// <summary>Gets the candidate evidence support package identity.</summary>
    internal const string SupportId = Prefix + "Evidence.Coverage";

    /// <summary>Gets the minimum candidate feed closure in its stable admission order.</summary>
    internal static IReadOnlyList<string> CandidateMinimumPackageIds { get; } = Array.AsReadOnly(new[]
    {
        ToolId, Prefix + "Evidence.Contracts", Prefix + "Evidence.Planner", Prefix + "Evidence.Readiness",
        SupportId, Prefix + "Coverage", Prefix + "Evidence.Aspire"
    });

    /// <summary>Gets the bundled tool package identities attested by the reuse proof.</summary>
    internal static IReadOnlyList<string> BundledToolPackageIds { get; } = Array.AsReadOnly(new[]
    {
        ToolId, Prefix + "Evidence.Cli", Prefix + "Evidence.Contracts", Prefix + "Evidence.Planner", SupportId, Prefix + "Coverage"
    });

    private static IReadOnlyList<string> OptionalSeparatePackageIds { get; } = Array.AsReadOnly(new[]
    {
        Prefix + "Evidence.Cli"
    });

    /// <summary>Validates a candidate feed and records package and assembly hashes from its actual archive bytes.</summary>
    /// <param name="feed">Local candidate feed containing the complete same-version family.</param>
    /// <param name="version">Exact candidate package and dependency-graph version.</param>
    /// <param name="expectedSourceCommit">Trusted full source commit matching every candidate nuspec.</param>
    /// <param name="cancellationToken">Cancels archive reading and hashing.</param>
    /// <returns>Admitted package records keyed by package identity.</returns>
    /// <exception cref="InvalidOperationException">A candidate package violates a stable admission contract.</exception>
    internal static async Task<IReadOnlyDictionary<string, PackageProof>> InspectFeedAsync(string feed, string version,
        string expectedSourceCommit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feed);
        Require(version.Length is > 0 and <= 128 && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'), "candidate-version");
        Require(expectedSourceCommit.Length is 40 or 64 && expectedSourceCommit.All(char.IsAsciiHexDigit), "source-commit-pin");
        var result = new Dictionary<string, PackageProof>(StringComparer.Ordinal);
        var bundledAssemblyHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var ids = CandidateMinimumPackageIds.ToList();
        foreach (var optionalId in OptionalSeparatePackageIds)
            if (File.Exists(Path.Join(feed, $"{optionalId}.{version}.nupkg"))) ids.Add(optionalId);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Join(feed, $"{id}.{version}.nupkg"));
            Require(File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                && new FileInfo(path).Length <= 512L * 1024 * 1024, "bounded-regular-candidate-archive");
            var archiveBytes = await ReadArchiveBytesAsync(path, cancellationToken).ConfigureAwait(false);
            using var archiveStream = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            Require(archive.Entries.Count <= 10000 && archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == archive.Entries.Count,
                "bounded-unambiguous-archive");
            var specification = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            Require(specification.Length is > 0 and <= 1024 * 1024, "bounded-nuspec");
            using var contents = specification.Open();
            using var reader = XmlReader.Create(contents, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var metadata = XDocument.Load(reader).Root!.Elements().Single(element => element.Name.LocalName == "metadata");
            string Value(string name) => metadata.Elements().Single(element => element.Name.LocalName == name).Value;
            Require(Value("id") == id && Value("version") == version, "exact-candidate-identity");
            Require((string?)metadata.Elements().Single(element => element.Name.LocalName == "repository").Attribute("commit") == expectedSourceCommit,
                "exact-candidate-source-commit");
            var privateTarget = id switch
            {
                SupportId => Prefix + "Evidence.Contracts",
                Prefix + "Coverage" or Prefix + "Evidence.Aspire" or Prefix + "Evidence.Cli" => SupportId,
                _ => null
            };
            var dependencies = metadata.Elements().SingleOrDefault(element => element.Name.LocalName == "dependencies");
            if (privateTarget is not null)
            {
                Require(dependencies is not null, "private-dependency-required");
                var groups = dependencies!.Elements().Where(element => element.Name.LocalName == "group").ToArray();
                foreach (var group in groups.Length == 0 ? [dependencies] : groups)
                {
                    var edges = group!.Elements().Where(element => element.Name.LocalName == "dependency"
                        && string.Equals((string?)element.Attribute("id"), privateTarget, StringComparison.OrdinalIgnoreCase)).ToArray();
                    Require(edges.Length == 1 && (string?)edges[0].Attribute("id") == privateTarget
                        && (string?)edges[0].Attribute("version") == $"[{version}]", PrivateDependencyContract);
                }
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
            {
                var deps = archive.Entries.Single(entry => entry.FullName.StartsWith("tools/", StringComparison.Ordinal) && Path.GetFileName(entry.FullName) == ToolId + ".deps.json");
                Require(deps.Length <= 8L * 1024 * 1024, "bounded-tool-deps");
                using var stream = deps.Open();
                using var document = JsonDocument.Parse(stream);
                var libraries = document.RootElement.GetProperty("libraries").EnumerateObject().ToArray();
                // A tool bundles project outputs. PackageVersion can legitimately differ from their coherent
                // build version; exact package ABI is anchored independently by the candidate archive DLL hashes.
                var toolLibrary = libraries.Single(library => library.Name.StartsWith(ToolId + "/", StringComparison.Ordinal));
                var buildVersion = toolLibrary.Name[(ToolId.Length + 1)..];
                Require(buildVersion.Length > 0, "tool-build-version");
                var bundledLibraries = libraries.Where(library => BundledToolPackageIds.Contains(
                    library.Name.Split('/')[0], StringComparer.Ordinal)).ToArray();
                var bundledIds = bundledLibraries.Select(library => library.Name.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
                var candidateComponentIds = ids.Append(Prefix + "Evidence.Cli");
                var componentLibraries = libraries.Where(library => library.Name.Split('/')[0] != ToolId
                    && candidateComponentIds.Contains(library.Name.Split('/')[0], StringComparer.OrdinalIgnoreCase)).ToArray();
                var componentVersions = componentLibraries.Select(library => library.Name[(library.Name.LastIndexOf('/') + 1)..])
                    .Distinct(StringComparer.Ordinal).ToArray();
                Require(componentVersions.Length == 1 && (componentVersions[0] == version || componentVersions[0] == buildVersion),
                    ToolBundleVersionContract);
                foreach (var library in componentLibraries)
                    Require(candidateComponentIds.Contains(library.Name.Split('/')[0], StringComparer.Ordinal)
                        && library.Name.EndsWith("/" + componentVersions[0], StringComparison.Ordinal), ToolBundleVersionContract);
                foreach (var required in BundledToolPackageIds)
                    Require(libraries.Any(library => library.Name == required + "/" + (required == ToolId ? buildVersion : componentVersions[0])),
                        "tool-required-bundled-graph");
                Require(bundledLibraries.Length == BundledToolPackageIds.Count && bundledIds.SetEquals(BundledToolPackageIds),
                    "tool-exact-bundled-graph");
                foreach (var bundledId in BundledToolPackageIds.Where(id => id != ToolId))
                {
                    var bundled = archive.Entries.Single(entry => entry.FullName.StartsWith("tools/", StringComparison.Ordinal)
                        && Path.GetFileName(entry.FullName) == bundledId + ".dll");
                    bundledAssemblyHashes.Add(bundledId, await HashEntryAsync(bundled, cancellationToken).ConfigureAwait(false));
                }
            }
            result.Add(id, new(id, version, expectedSourceCommit, path,
                Convert.ToHexStringLower(SHA256.HashData(archiveBytes)), assemblyEntry.FullName, assemblySha));
        }
        foreach (var (id, hash) in bundledAssemblyHashes)
        {
            if (result.TryGetValue(id, out var package))
                Require(hash == package.AssemblySha256,
                    id == Prefix + "Evidence.Cli" ? "tool-bundled-cli-candidate-sha-before-output" : "tool-bundled-candidate-sha-before-output");
        }
        return result;
    }

    /// <summary>Hashes one archive entry without materializing its contents in memory.</summary>
    internal static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using var input = entry.Open();
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadArchiveBytesAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Require(input.Length <= 512L * 1024 * 1024, "bounded-regular-candidate-archive");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        Require(input.ReadByte() == -1, "stable-candidate-archive-length");
        return bytes;
    }

    private static void Require(bool condition, string contract) => ValidationProof.Require(condition, ContractScope + contract);

    /// <summary>Candidate archive and assembly byte identity retained in the proof report.</summary>
    internal sealed record PackageProof(string Id, string Version, string SourceCommit, string Archive, string Sha256,
        string AssemblyEntry, string AssemblySha256);
}
