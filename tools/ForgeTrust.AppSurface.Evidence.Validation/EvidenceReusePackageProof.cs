using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Exercises the installed candidate CLI and an independent package-only prepared host in owned scratch.</summary>
/// <remarks>
/// Feed admission precedes output creation. Consumer restores intentionally use private caches and source mapping;
/// normal developer caches and checkout outputs are not provenance. Only nested synthetic-local executions remove
/// GITHUB_ACTIONS. The proof process and normal CI admission remain unchanged. No container runtime is required.
/// Stage logs start with a diagnostic marker before launch and are replaced by captured output only after joining
/// the process. A starting marker is not settlement or acceptance evidence. Logs and proof.json survive failure.
/// No caller-owned files, feeds, or shared processes are removed.
/// </remarks>
internal static class EvidenceReusePackageProof
{
    private const string Prefix = "ForgeTrust.AppSurface.";
    private const string ToolId = Prefix + "Cli";
    private const string SupportId = Prefix + "Evidence.Coverage";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Gets the minimum candidate feed closure in its stable admission order.</summary>
    internal static IReadOnlyList<string> CandidateMinimumPackageIds { get; } = Array.AsReadOnly(new[]
    {
        ToolId, Prefix + "Evidence.Contracts", Prefix + "Evidence.Planner", Prefix + "Evidence.Readiness",
        SupportId, Prefix + "Coverage", Prefix + "Evidence.Aspire"
    });
    /// <summary>Gets packages admitted only when a separate candidate archive is present.</summary>
    private static IReadOnlyList<string> OptionalSeparatePackageIds { get; } = Array.AsReadOnly(new[]
    {
        Prefix + "Evidence.Cli"
    });
    /// <summary>Gets the bundled tool package identities attested by the reuse proof.</summary>
    private static IReadOnlyList<string> BundledToolPackageIds { get; } = Array.AsReadOnly(new[]
    {
        ToolId, Prefix + "Evidence.Cli", Prefix + "Evidence.Contracts", Prefix + "Evidence.Planner", SupportId, Prefix + "Coverage"
    });
    /// <summary>Gets the exact package identities restored by the independent prepared host.</summary>
    private static IReadOnlyList<string> PreparedHostPackageIds { get; } = Array.AsReadOnly(new[]
    {
        Prefix + "Evidence.Aspire", Prefix + "Evidence.Contracts", Prefix + "Evidence.Planner",
        Prefix + "Evidence.Readiness", SupportId, Prefix + "Coverage"
    });

    /// <summary>Runs the bounded packed CLI/host journey, returning zero only after every recorded assertion passes.</summary>
    /// <param name="feed">Local candidate feed with the complete same-version family.</param>
    /// <param name="version">Exact candidate package and dependency-graph version.</param>
    /// <param name="output">Fresh directory outside the source checkout; retained for diagnostics.</param>
    /// <param name="dotnet">Absolute SDK host; SDK 10.0.402 is required.</param>
    /// <param name="expectedSourceCommit">Trusted full source commit matching every candidate nuspec.</param>
    /// <param name="cancellationToken">Cancellation joins owned process cleanup before propagating.</param>
    internal static async Task<int> RunAsync(string feed, string version, string output, string dotnet,
        string expectedSourceCommit, CancellationToken cancellationToken)
    {
        var stage = "admission";
        string? admittedOutput = null;
        FileStream? outputClaim = null;
        var stages = new List<StageResult>();
        try
        {
            var packages = await InspectFeedAsync(feed, version, expectedSourceCommit, cancellationToken).ConfigureAwait(false);
            var repository = ResolveExistingPath(FindRepository());
            var feedRoot = ResolveExistingPath(feed);
            output = Path.GetFullPath(output);
            var resolvedOutput = ResolveExistingPath(output);
            dotnet = Path.GetFullPath(dotnet);
            Require(File.Exists(dotnet) && !PathEntryExists(output), "fresh-output-and-sdk");
            // Scratch admission is conservatively case-insensitive on all platforms. Assembly provenance below
            // retains its exact platform path comparison; this guard only rejects ambiguous output locations.
            static bool ScratchWithin(string root, string candidate) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            Require(!ScratchWithin(repository, resolvedOutput) && !ScratchWithin(feedRoot, resolvedOutput), "scratch-outside-source-and-feed");
            output = resolvedOutput;
            var outputParent = Path.GetDirectoryName(output) ?? throw new ArgumentException("Output must have a parent directory.");
            Directory.CreateDirectory(outputParent);
            outputClaim = new FileStream(output + ".packed-reuse.claim", FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            Require(!PathEntryExists(output), "fresh-output-and-sdk");
            Directory.CreateDirectory(output);
            admittedOutput = output;
            var runner = new ProofRunner(output, dotnet, stages);
            await File.WriteAllTextAsync(Path.Join(output, "global.json"), """{"sdk":{"version":"10.0.402","rollForward":"disable","allowPrerelease":false}}""", cancellationToken).ConfigureAwait(false);
            var sdk = await runner.RunAsync("sdk", dotnet, ["--version"], output, false, cancellationToken).ConfigureAwait(false);
            Require(sdk.StandardOutput.Trim() == "10.0.402", "pinned-sdk-10.0.402");
            var config = Path.Join(output, "NuGet.Config");
            WriteNuGetConfig(config, Path.GetFullPath(feed));
            stage = "negative-admission";
            var negatives = await ProveNegativeAdmissionAsync(packages, version, expectedSourceCommit, output, dotnet, cancellationToken).ConfigureAwait(false);
            stage = "tool-install";
            var toolRoot = Path.Join(output, "installed-tool");
            await runner.RunAsync(stage, dotnet, ["tool", "install", ToolId, "--tool-path", toolRoot, "--version", version,
                "--configfile", config, "--no-cache"], output, false, cancellationToken).ConfigureAwait(false);
            var toolArchive = Directory.GetFiles(Path.Join(toolRoot, ".store"), "*.nupkg", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Equals($"{ToolId}.{version}.nupkg", StringComparison.OrdinalIgnoreCase)).Single();
            Require(await HashAsync(toolArchive, cancellationToken).ConfigureAwait(false) == packages[ToolId].Sha256, "installed-tool-archive-sha");
            var toolDll = Directory.GetFiles(Path.Join(toolRoot, ".store"), ToolId + ".dll", SearchOption.AllDirectories).Single();
            var toolAssemblies = await VerifyToolAssembliesAsync(Path.GetDirectoryName(toolDll)!, packages, cancellationToken).ConfigureAwait(false);
            var command = Path.Join(toolRoot, OperatingSystem.IsWindows() ? "appsurface.exe" : "appsurface");
            Require(File.Exists(command), "installed-tool-shim");
            stage = "host";
            var host = await ProveHostAsync(output, version, packages, runner, cancellationToken).ConfigureAwait(false);
            stage = "cli";
            var cli = await ProveCliAsync(output, command, config, version, runner, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Join(output, "proof.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                passed = true,
                version,
                expectedSourceCommit,
                sdk = "10.0.402",
                repository,
                packages = packages.Values.OrderBy(package => package.Id, StringComparer.Ordinal),
                installedTool = new { command, toolDll, assemblies = toolAssemblies },
                negatives,
                host,
                cli,
                stages,
                limitations = "Cooperative local workload proof on the current OS; no containment or unregistered-child settlement claim."
            }, JsonOptions), cancellationToken).ConfigureAwait(false);
            Console.WriteLine("Packed Evidence CLI and prepared Kestrel host reuse proof passed.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException
            or JsonException or XmlException or UnauthorizedAccessException or TimeoutException or OperationCanceledException
            or KeyNotFoundException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            if (admittedOutput is not null)
                await File.WriteAllBytesAsync(Path.Join(admittedOutput, "proof.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    passed = false,
                    stage,
                    failureType = exception.GetType().Name,
                    contract = exception.Message.StartsWith("Native validation contract failed: packed-reuse/", StringComparison.Ordinal) ? exception.Message : null,
                    stages
                }, JsonOptions), CancellationToken.None).ConfigureAwait(false);
            var reason = exception.Message.StartsWith("Native validation contract failed: packed-reuse/", StringComparison.Ordinal)
                ? exception.Message : exception.GetType().Name;
            Console.Error.WriteLine($"ASEVD917 Packed reuse proof failed at {stage}: {reason}. Inspect retained stage logs and candidate closure.");
            return 1;
        }
        finally { outputClaim?.Dispose(); }
    }

    /// <summary>Reads actual archive bytes and validates all framework-group private edges before any consumer output exists.</summary>
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
                        && (string?)edges[0].Attribute("version") == $"[{version}]", "exact-private-edge-in-every-framework-group");
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
                    "tool-bundled-graph-version");
                foreach (var library in componentLibraries)
                    Require(candidateComponentIds.Contains(library.Name.Split('/')[0], StringComparer.Ordinal)
                        && library.Name.EndsWith("/" + componentVersions[0], StringComparison.Ordinal), "tool-bundled-graph-version");
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

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string ResolveExistingPath(string path, int linkDepth = 0)
    {
        if (linkDepth > 64) throw new IOException("Path link resolution exceeds its bound.");
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("Path has no filesystem root.", nameof(path));
        var resolved = root;
        foreach (var component in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Join(resolved, component);
            try
            {
                var attributes = File.GetAttributes(candidate);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    FileSystemInfo link = attributes.HasFlag(FileAttributes.Directory)
                        ? new DirectoryInfo(candidate) : new FileInfo(candidate);
                    var target = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? throw new IOException("Path link has no resolvable target.");
                    // A final link target can itself name symlinked ancestors (for example /var on macOS).
                    candidate = ResolveExistingPath(target, linkDepth + 1);
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            resolved = Path.GetFullPath(candidate);
        }
        return resolved;
    }

    private static async Task<object> ProveNegativeAdmissionAsync(IReadOnlyDictionary<string, PackageProof> packages,
        string version, string commit, string output, string dotnet, CancellationToken token)
    {
        var results = new List<object>();
        foreach (var kind in new[] { "tool-graph-version", "host-private-edge" })
        {
            var root = Path.Join(output, "negative-" + kind);
            var feed = Path.Join(root, "feed");
            Directory.CreateDirectory(feed);
            foreach (var package in packages.Values)
            {
                var destination = Path.Join(feed, Path.GetFileName(package.Archive));
                if (package.Id == (kind == "tool-graph-version" ? ToolId : Prefix + "Evidence.Aspire"))
                {
                    using var source = ZipFile.OpenRead(package.Archive);
                    using var target = ZipFile.Open(destination, ZipArchiveMode.Create);
                    foreach (var entry in source.Entries)
                    {
                        var created = target.CreateEntry(entry.FullName);
                        using var input = entry.Open();
                        using var written = created.Open();
                        if ((kind == "tool-graph-version" && entry.FullName.EndsWith(ToolId + ".deps.json", StringComparison.Ordinal))
                            || (kind == "host-private-edge" && entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)))
                        {
                            using var text = new StreamReader(input);
                            var value = await text.ReadToEndAsync(token).ConfigureAwait(false);
                            var original = value;
                            if (kind == "tool-graph-version")
                            {
                                using var deps = JsonDocument.Parse(value);
                                var supportLibrary = deps.RootElement.GetProperty("libraries").EnumerateObject()
                                    .Single(library => library.Name.StartsWith(SupportId + "/", StringComparison.Ordinal)).Name;
                                value = value.Replace(supportLibrary, SupportId + "/0.0.0-mismatch", StringComparison.Ordinal);
                            }
                            else value = value.Replace($"[{version}]", "[0.0.0-mismatch]", StringComparison.Ordinal);
                            Require(value != original, "negative-archive-mutation-applied");
                            await written.WriteAsync(Encoding.UTF8.GetBytes(value), token).ConfigureAwait(false);
                        }
                        else await input.CopyToAsync(written, token).ConfigureAwait(false);
                    }
                }
                else File.Copy(package.Archive, destination);
            }
            var rejectedOutput = Path.Join(root, "must-not-exist");
            var code = await RunAsync(feed, version, rejectedOutput, dotnet, commit, token).ConfigureAwait(false);
            Require(code == 1 && !Directory.Exists(rejectedOutput) && !File.Exists(rejectedOutput), "negative-admission-before-output");
            results.Add(new { kind, exitCode = code, outputCreated = false });
        }
        return results;
    }

    private static async Task<IReadOnlyList<AssemblyProof>> VerifyToolAssembliesAsync(string root,
        IReadOnlyDictionary<string, PackageProof> packages, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(packages[ToolId].Archive);
        var results = new List<AssemblyProof>();
        foreach (var id in BundledToolPackageIds)
        {
            var entry = archive.Entries.Single(item => item.FullName.StartsWith("tools/", StringComparison.Ordinal) && Path.GetFileName(item.FullName) == id + ".dll");
            var actual = Path.Join(root, id + ".dll");
            var expected = await HashEntryAsync(entry, token).ConfigureAwait(false);
            Require(await HashAsync(actual, token).ConfigureAwait(false) == expected, "installed-tool-bundled-dll-sha");
            if (packages.TryGetValue(id, out var library)) Require(expected == library.AssemblySha256, "tool-and-library-same-candidate-bytes");
            results.Add(new(id, actual, expected));
        }
        return results;
    }

    private static async Task<object> ProveHostAsync(string output, string version,
        IReadOnlyDictionary<string, PackageProof> packages, ProofRunner runner, CancellationToken token)
    {
        var app = Path.Join(output, "host-app");
        Directory.CreateDirectory(app);
        var project = Path.Join(app, "PackedHost.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><PublishTrimmed>false</PublishTrimmed><PublishSingleFile>false</PublishSingleFile><SelfContained>false</SelfContained></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="ForgeTrust.AppSurface.Evidence.Aspire" Version="[{{version}}]" />
                <PackageReference Include="ForgeTrust.AppSurface.Evidence.Contracts" Version="[{{version}}]" />
                <PackageReference Include="ForgeTrust.AppSurface.Evidence.Planner" Version="[{{version}}]" />
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>
            </Project>
            """, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(app, "Program.cs"), HostProgram, token).ConfigureAwait(false);
        await runner.RunAsync("host-restore", runner.Dotnet, ["restore", project, "--configfile", Path.Join(output, "NuGet.Config"), "--use-lock-file"], output, false, token).ConfigureAwait(false);
        await VerifyAssetsAsync(Path.Join(app, "obj", "project.assets.json"), packages, runner.Packages, token).ConfigureAwait(false);
        await runner.RunAsync("host-build", runner.Dotnet, ["build", project, "--no-restore", "-c", "Release", "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false", "-warnaserror"], output, false, token).ConfigureAwait(false);
        var buildRoot = Path.Join(app, "bin", "Release", "net10.0");
        var buildProof = Path.Join(output, "host-built-proof");
        await PrepareGitAsync(buildProof, runner, "host-built", token).ConfigureAwait(false);
        await runner.RunAsync("host-run", runner.Dotnet, [Path.Join(buildRoot, "PackedHost.dll"), buildProof], output, true, token).ConfigureAwait(false);
        var built = await VerifyHostRunAsync(buildRoot, buildProof, packages, token).ConfigureAwait(false);
        var published = Path.Join(output, "host-published");
        await runner.RunAsync("host-publish", runner.Dotnet, ["publish", project, "--no-restore", "-c", "Release", "-o", published,
            "--self-contained", "false", "-p:PublishTrimmed=false", "-p:PublishSingleFile=false", "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false", "-warnaserror"], output, false, token).ConfigureAwait(false);
        var publishedProof = Path.Join(output, "host-published-proof");
        await PrepareGitAsync(publishedProof, runner, "host-published", token).ConfigureAwait(false);
        await runner.RunAsync("host-published-run", runner.Dotnet, [Path.Join(published, "PackedHost.dll"), publishedProof], output, true, token).ConfigureAwait(false);
        var publish = await VerifyHostRunAsync(published, publishedProof, packages, token).ConfigureAwait(false);
        return new { built, publish };
    }

    private static async Task<JsonElement> VerifyHostRunAsync(string assemblyRoot, string proofRoot,
        IReadOnlyDictionary<string, PackageProof> packages, CancellationToken token)
    {
        using var proof = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(proofRoot, "host-proof.json"), token).ConfigureAwait(false));
        var data = proof.RootElement;
        Require(data.GetProperty("passed").GetBoolean() && data.GetProperty("firstFactoryCount").GetInt32() == 1
            && data.GetProperty("secondFactoryCount").GetInt32() == 0 && data.GetProperty("firstProducerRuns").GetInt32() == 1
            && data.GetProperty("secondProducerRuns").GetInt32() == 0 && data.GetProperty("cleanedApplications").GetInt32() == 1,
            "prepared-real-http-and-selective-startup");
        var assemblies = data.GetProperty("assemblies").EnumerateArray().ToArray();
        var assemblyIds = assemblies.Select(assembly => assembly.GetProperty("name").GetString()!).ToArray();
        Require(assemblies.Length == PreparedHostPackageIds.Count
            && new HashSet<string>(assemblyIds, StringComparer.Ordinal).SetEquals(PreparedHostPackageIds), "complete-host-assembly-closure");
        foreach (var assembly in assemblies)
        {
            var id = assembly.GetProperty("name").GetString()!;
            var path = assembly.GetProperty("path").GetString()!;
            Require(PreparedHostPackageIds.Contains(id, StringComparer.Ordinal) && packages.ContainsKey(id)
                && Within(assemblyRoot, path) && await HashAsync(path, token).ConfigureAwait(false) == packages[id].AssemblySha256,
                "independent-host-loaded-candidate-assembly");
            Require(assembly.GetProperty("sha256").GetString() == packages[id].AssemblySha256, "consumer-reported-loaded-sha");
        }
        using var deps = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(assemblyRoot, "PackedHost.deps.json"), token).ConfigureAwait(false));
        _ = VerifyPackageClosure(deps.RootElement.GetProperty("libraries"), packages,
            "published-host-exact-package-closure", "published-host-package-only-deps");
        return data.Clone();
    }

    private static IReadOnlyList<PackageProof> VerifyPackageClosure(JsonElement libraries,
        IReadOnlyDictionary<string, PackageProof> packages, string closureDiagnostic, string packageDiagnostic)
    {
        var family = libraries.EnumerateObject()
            .Where(library => library.Name.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase))
            .Select(library => (Library: library, Id: library.Name.Split('/')[0]))
            .ToArray();
        Require(family.Length == PreparedHostPackageIds.Count
            && new HashSet<string>(family.Select(library => library.Id), StringComparer.Ordinal).SetEquals(PreparedHostPackageIds),
            closureDiagnostic);

        var verifiedPackages = new List<PackageProof>(family.Length);
        foreach (var library in family)
        {
            var type = library.Library.Value.GetProperty("type").GetString();
            var package = packages.GetValueOrDefault(library.Id);
            Require(type == "package" && package is not null && library.Id == package.Id
                && library.Library.Name == package.Id + "/" + package.Version, packageDiagnostic);
            verifiedPackages.Add(package!);
        }
        return verifiedPackages;
    }

    private static async Task VerifyAssetsAsync(string path, IReadOnlyDictionary<string, PackageProof> packages, string cache, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
        var folders = document.RootElement.GetProperty("packageFolders").EnumerateObject().Select(item => Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Name))).ToArray();
        Require(folders.Length == 1 && folders[0] == Path.TrimEndingDirectorySeparator(cache), "isolated-package-cache-no-fallback");
        var verifiedPackages = VerifyPackageClosure(document.RootElement.GetProperty("libraries"), packages,
            "host-six-package-closure", "exact-package-assets-no-project-fallback");
        foreach (var package in verifiedPackages)
        {
            var archive = Path.Join(cache, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant(),
                $"{package.Id}.{package.Version}.nupkg".ToLowerInvariant());
            Require(await HashAsync(archive, token).ConfigureAwait(false) == package.Sha256, "restored-candidate-archive-sha");
        }
    }

    private static async Task<object> ProveCliAsync(string output, string command, string config, string version,
        ProofRunner runner, CancellationToken token)
    {
        var repository = Path.Join(output, "cli-inputs");
        CopyFixture(Path.Join(AppContext.BaseDirectory, "fixtures", "consumer"), repository);
        File.Copy(config, Path.Join(repository, "NuGet.Config"));
        File.Copy(Path.Join(output, "global.json"), Path.Join(repository, "global.json"));
        var testsProject = Path.Join(repository, "Consumer.Alpha.Tests", "Consumer.Alpha.Tests.csproj");
        var testsDocument = XDocument.Load(testsProject);
        testsDocument.Root!.Add(new XElement("ItemGroup", new XElement("PackageReference",
            new XAttribute("Include", Prefix + "Evidence.Contracts"), new XAttribute("VersionOverride", "[" + version + "]"))));
        testsDocument.Save(testsProject);
        await File.WriteAllTextAsync(Path.Join(repository, "Consumer.Alpha.Tests", "OwnedChildTests.cs"), """
            using ForgeTrust.AppSurface.Evidence.Contracts;
            using Xunit;
            public sealed class OwnedChildTests
            {
                [Fact]
                public async Task CandidateHelperEnrollsAndJoinsActualChildWork()
                {
                    Assert.True(new[] { "APPSURFACE_EVIDENCE_CHILD_PIPE", "APPSURFACE_EVIDENCE_CHILD_EXECUTION_ID", "APPSURFACE_EVIDENCE_CHILD_NONCE" }
                        .All(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))), "An invocation-private channel must be present.");
                    var completed = 0;
                    await EvidenceOwnedChild.RunAsync(async _ =>
                    {
                        await Task.Yield();
                        Interlocked.Increment(ref completed);
                    }, CancellationToken.None);
                    Assert.Equal(1, completed);
                }
            }
            """, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(repository, ".gitignore"), "**/bin/\n**/obj/\n", token).ConfigureAwait(false);
        var producerDeclaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/complete@1"],
            [new EvidenceArtifactSlot("coverage-reports", "coverage", "application/octet-stream", false, 256L * 1024 * 1024) { MaximumFiles = 128 }],
            240, new EvidenceCoverageGateRequirements(95, 85, TolerancePercent: 0))
        {
            Inputs = new(["**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/packages.lock.json", "Consumer.slnx", "NuGet.Config", "global.json", "evidence.policy.json"], ["**/bin/**", "**/obj/**"]),
            CoverageExecution = new("Release", EvidenceCoverageTestResultFormat.Junit, 1) { CooperativeChildLifetime = true }
        };
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [producerDeclaration],
            [new EvidenceObligation("coverage", "coverage", "Packed consumer coverage", ["coverage"], "coverage/complete@1")]);
        var policy = new EvidencePolicy("packed-coverage", "1", "local", [profile], [new EvidencePolicyRule("all", "**", "local")]);
        await File.WriteAllBytesAsync(Path.Join(repository, "evidence.policy.json"), EvidenceCanonicalJson.Serialize(policy), token).ConfigureAwait(false);
        await runner.RunAsync("fixture-lock", runner.Dotnet, ["restore", Path.Join(repository, "Consumer.slnx"), "--configfile", Path.Join(repository, "NuGet.Config"), "--use-lock-file", "--force-evaluate"], repository, false, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(repository, "Directory.Build.props"), "<Project><PropertyGroup><RestoreLockedMode>true</RestoreLockedMode></PropertyGroup></Project>", token).ConfigureAwait(false);
        await GitInitAsync(repository, runner, "cli", token).ConfigureAwait(false);
        var inputHashes = await FixtureInputsAsync(repository, token).ConfigureAwait(false);
        var results = new List<object>();
        string? originSha = null;
        foreach (var mode in new[] { "fresh", "warm", "audit" })
        {
            var root = Path.Join(output, "cli-" + mode);
            var arguments = new List<string> { "evidence", "run", "--policy", "evidence.policy.json", "--solution", "Consumer.slnx", "--path", "Consumer.Alpha/AlphaDecision.cs", "--reuse", "--receipt-store", Path.Join(output, "cli-receipts"), "--output", root };
            if (mode == "audit") arguments.Add("--verify-reuse");
            await runner.RunAsync("cli-" + mode, command, arguments, repository, true, token).ConfigureAwait(false);
            var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(await File.ReadAllBytesAsync(Path.Join(root, "evidence-manifest.json"), token).ConfigureAwait(false));
            Require(manifest.ExecutionVerdict == EvidenceExecutionVerdict.Passed, "cli-manifest-passed");
            var producer = manifest.ProducerResults.Single();
            var expected = mode == "warm" ? EvidenceProducerOutcome.Reused : EvidenceProducerOutcome.Passed;
            Require(producer.Outcome == expected, "cli-fresh-warm-audit-outcome");
            if (mode == "audit")
            {
                using var summary = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(root, "evidence-summary.json"), token).ConfigureAwait(false));
                Require(summary.RootElement.GetProperty("reuseDecisions").EnumerateArray().Any(decision =>
                    decision.GetProperty("ProducerId").GetString() == producer.ProducerId
                    && decision.TryGetProperty("VerifiedReceiptCompared", out var compared) && compared.GetBoolean()),
                    "cli-audit-compared-verified-receipt");
            }
            if (mode == "warm")
            {
                Require(manifest.ClaimKind == EvidenceClaimKind.LocalReusedComplete
                    && manifest.Eligibility == EvidenceClaimEligibility.Informational, "cli-reuse-informational");
                Require(producer.Diagnostic is { } diagnostic
                    && diagnostic.Contains("coverage/coverage-gate.md", StringComparison.Ordinal)
                    && diagnostic.Contains("relative to the producer artifact root", StringComparison.Ordinal)
                    && !diagnostic.Contains(Path.Join(output, "cli-fresh"), StringComparison.Ordinal),
                    "cli-reuse-diagnostic-current-artifacts");
            }
            var artifacts = producer.Artifacts?.ToArray() ?? [];
            Require(artifacts.Length is > 5 and <= 128, "cli-complete-bounded-custody");
            long bytes = 0;
            foreach (var artifact in artifacts)
            {
                var relative = artifact.RelativePath;
                Require(relative.StartsWith("coverage/", StringComparison.Ordinal) && !relative.Split('/').Any(part => part is "." or "..") && !Path.IsPathRooted(relative), "cli-established-coverage-layout");
                var file = Path.Join(root, relative);
                Require(File.Exists(file) && new FileInfo(file).Length == artifact.LengthBytes
                    && await HashAsync(file, token).ConfigureAwait(false) == artifact.Sha256, "cli-original-report-custody");
                bytes += new FileInfo(file).Length;
            }
            Require(bytes <= 256L * 1024 * 1024, "cli-existing-aggregate-limit");
            foreach (var required in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json" })
                Require(artifacts.Any(artifact => artifact.RelativePath == "coverage/" + required)
                    && File.Exists(Path.Join(root, "coverage", required)), "cli-required-managed-reports-in-inventory");
            using var gate = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(root, "coverage", "coverage-gate.json"), token).ConfigureAwait(false));
            var thresholds = gate.RootElement.GetProperty("thresholds");
            var effectiveThresholds = gate.RootElement.GetProperty("effectiveThresholds");
            Require(gate.RootElement.GetProperty("passed").GetBoolean() && gate.RootElement.GetProperty("tolerancePercent").GetDecimal() == 0m
                && thresholds.GetProperty("line").GetDecimal() == 95m && thresholds.GetProperty("branch").GetDecimal() == 85m
                && effectiveThresholds.GetProperty("line").GetDecimal() == 95m && effectiveThresholds.GetProperty("branch").GetDecimal() == 85m,
                "unchanged-95-85-coverage-gate");
            var testCases = 0;
            foreach (var report in artifacts.Where(artifact => artifact.RelativePath.StartsWith("coverage/junit-coverage-", StringComparison.Ordinal)
                && artifact.RelativePath.EndsWith(".xml", StringComparison.Ordinal)))
            {
                using var xml = XmlReader.Create(Path.Join(root, report.RelativePath), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                testCases += XDocument.Load(xml).Descendants().Count(element => element.Name.LocalName == "testcase");
            }
            Require(testCases == 6, "actual-five-fixture-tests-and-packed-child-helper");
            using var timings = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(root, "coverage", "timings.json"), token).ConfigureAwait(false));
            Require(timings.RootElement.GetProperty("projects").GetArrayLength() == 2, "real-two-test-project-fixture");
            var coverageSha = await HashAsync(Path.Join(root, "coverage", "coverage.cobertura.xml"), token).ConfigureAwait(false);
            if (mode == "fresh") originSha = coverageSha;
            if (mode == "warm") Require(coverageSha == originSha, "warm-original-report-byte-identity");
            if (mode == "warm")
                await runner.RunAsync("restored-coverage-reader", command,
                    ["coverage", "gate", "--coverage", Path.Join(root, "coverage", "coverage.cobertura.xml"),
                        "--min-line", "95", "--min-branch", "85", "--tolerance", "0", "--output", Path.Join(output, "restored-reader-reports")],
                    repository, true, token).ConfigureAwait(false);
            Require(ValidationProof.Equal(inputHashes, await FixtureInputsAsync(repository, token).ConfigureAwait(false)), "locked-complete-inputs-unchanged");
            results.Add(new
            {
                mode,
                outcome = expected.ToString(),
                claim = manifest.ClaimKind.ToString(),
                testCaseCount = testCases,
                testProjectCount = 2,
                artifactCount = artifacts.Length,
                artifactBytes = bytes,
                coverageSha256 = coverageSha,
                inventory = artifacts
            });
        }
        return new { inputs = inputHashes, runs = results };
    }

    private static async Task PrepareGitAsync(string root, ProofRunner runner, string stage, CancellationToken token)
    {
        var repository = Path.Join(root, "repo");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Join(repository, "input.txt"), "fixed packed Kestrel fixture v1", token).ConfigureAwait(false);
        await GitInitAsync(repository, runner, stage, token).ConfigureAwait(false);
    }

    private static async Task GitInitAsync(string root, ProofRunner runner, string stage, CancellationToken token)
    {
        await runner.RunAsync(stage + "-git-init", "git", ["init", "-q"], root, false, token).ConfigureAwait(false);
        await runner.RunAsync(stage + "-git-add", "git", ["-c", "core.hooksPath=" + Path.Join(root, "no-hooks"), "add", "."], root, false, token).ConfigureAwait(false);
    }

    private static async Task<SortedDictionary<string, string>> FixtureInputsAsync(string root, CancellationToken token)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is ".git" or "obj" or "bin")) continue;
            result.Add(relative, await HashAsync(file, token).ConfigureAwait(false));
        }
        return result;
    }

    private static void CopyFixture(string source, string target)
    {
        Require(Directory.Exists(source), "bundled-closed-fixture");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git")) continue;
            var destination = Path.Join(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static void WriteNuGetConfig(string path, string feed) => new XDocument(new XElement("configuration",
        new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", feed)),
            new XElement("add", new XAttribute("key", "nuget"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
        new XElement("packageSourceMapping", new XElement("clear"), new XElement("packageSource", new XAttribute("key", "candidate"), new XElement("package", new XAttribute("pattern", "ForgeTrust.*"))),
            new XElement("packageSource", new XAttribute("key", "nuget"), new XElement("package", new XAttribute("pattern", "*")))),
        new XElement("fallbackPackageFolders", new XElement("clear")))).Save(path);

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx"))) return directory.FullName;
        // A copied validator can still carry the closed fixture. Its current directory is recorded, never referenced by the consumer.
        return Directory.GetCurrentDirectory();
    }

    private static bool Within(string root, string path) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || Path.GetFullPath(path) == Path.GetFullPath(root);
    private static void Require(bool condition, string contract) => ValidationProof.Require(condition, "packed-reuse/" + contract);
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { await using var input = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)); }
    private static async Task<string> HashEntryAsync(ZipArchiveEntry entry, CancellationToken token)
    { using var input = entry.Open(); return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)); }

    /// <summary>Candidate archive and assembly byte identity retained in the proof report.</summary>
    internal sealed record PackageProof(string Id, string Version, string SourceCommit, string Archive, string Sha256, string AssemblyEntry, string AssemblySha256);
    private sealed record AssemblyProof(string Name, string Path, string Sha256);
    private sealed record StageResult(string Stage, int ExitCode, long ElapsedMilliseconds, bool SyntheticLocal);
    private sealed record ProcessResult(int ExitCode, string StandardOutput);

    private sealed class ProofRunner(string root, string dotnet, List<StageResult> stages)
    {
        internal string Dotnet { get; } = dotnet;
        internal string Packages { get; } = Path.Join(root, "packages");
        internal async Task<ProcessResult> RunAsync(string stage, string executable, IReadOnlyList<string> arguments,
            string workingDirectory, bool syntheticLocal, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(stage is "host-run" or "host-published-run" ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5));
            using var process = new Process
            {
                StartInfo = new(executable)
                { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            var environment = process.StartInfo.Environment;
            environment["NUGET_PACKAGES"] = Packages;
            environment["NUGET_HTTP_CACHE_PATH"] = Path.Join(root, "http-cache");
            environment["NUGET_SCRATCH"] = Path.Join(root, "nuget-scratch");
            environment["DOTNET_CLI_HOME"] = Path.Join(root, "cli-home");
            environment["APPSURFACE_ARTIFACT_HOME"] = Path.Join(root, "coverage-artifact-home");
            environment["DOTNET_ROOT"] = Path.GetDirectoryName(Dotnet)!;
            environment["PATH"] = Path.GetDirectoryName(Dotnet) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            environment["MSBUILDDISABLENODEREUSE"] = "1";
            environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            foreach (var key in new[] { "APPSURFACE_EVIDENCE_CHILD_PIPE", "APPSURFACE_EVIDENCE_CHILD_EXECUTION_ID", "APPSURFACE_EVIDENCE_CHILD_NONCE" }) environment.Remove(key);
            var gitOverrides = new HashSet<string>([
                "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
                "GIT_OBJECT_DIRECTORY_RELATIVE", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE",
                "GIT_CEILING_DIRECTORIES", "GIT_DISCOVERY_ACROSS_FILESYSTEM", "GIT_PREFIX", "GIT_CONFIG",
                "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM"
            ], StringComparer.OrdinalIgnoreCase);
            foreach (var key in environment.Keys.Where(key => gitOverrides.Contains(key)
                || key.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)).ToArray()) environment.Remove(key);
            if (syntheticLocal) environment.Remove("GITHUB_ACTIONS");
            await File.WriteAllTextAsync(Path.Join(root, stage + ".log"), JsonSerializer.Serialize(new
            {
                stage,
                state = "starting",
                startedAtUtc = DateTimeOffset.UtcNow
            }), timeout.Token).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                process.Start();
            }
            catch
            {
                // A rejected launch has no active stage; preserve the existing failure-proof contract.
                File.Delete(Path.Join(root, stage + ".log"));
                throw;
            }
            var outputCapture = new StringBuilder();
            var errorCapture = new StringBuilder();
            using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var stdout = DrainAsync(process.StandardOutput, outputCapture, drainCancellation.Token);
            var stderr = DrainAsync(process.StandardError, errorCapture, drainCancellation.Token);
            string output = "", error = "";
            try
            {
                var processExit = process.WaitForExitAsync(timeout.Token);
                while (!processExit.IsCompleted)
                {
                    if (stdout.IsFaulted || stdout.IsCanceled) await stdout.ConfigureAwait(false);
                    if (stderr.IsFaulted || stderr.IsCanceled) await stderr.ConfigureAwait(false);
                    var pending = new List<Task> { processExit };
                    if (!stdout.IsCompleted) pending.Add(stdout);
                    if (!stderr.IsCompleted) pending.Add(stderr);
                    await Task.WhenAny(pending).ConfigureAwait(false);
                }
                await processExit.ConfigureAwait(false);
                output = await stdout.ConfigureAwait(false);
                error = await stderr.ConfigureAwait(false);
            }
            catch
            {
                drainCancellation.Cancel();
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch (Exception) { }
                throw;
            }
            finally
            {
                // No invocation channel values or raw environment are rendered. Only the closed public fixture executes here.
                output = outputCapture.ToString();
                error = errorCapture.ToString();
                if (process.HasExited) stages.Add(new(stage, process.ExitCode, stopwatch.ElapsedMilliseconds, syntheticLocal));
                var log = output + error;
                foreach (var pair in process.StartInfo.Environment.Where(pair => pair.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)))
                    if (!string.IsNullOrEmpty(pair.Value)) log = log.Replace(pair.Value, "[redacted]", StringComparison.Ordinal);
                await File.WriteAllTextAsync(Path.Join(root, stage + ".log"), log, CancellationToken.None).ConfigureAwait(false);
            }
            Require(process.ExitCode == 0, "stage-" + stage);
            return new(process.ExitCode, output);
        }

        private static async Task<string> DrainAsync(StreamReader reader, StringBuilder result, CancellationToken token)
        {
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Bounded proof output exceeded.");
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
    }

    private static string HostProgram => $$"""
        using System.Reflection;
        using System.Security.Cryptography;
        using System.Text;
        using System.Text.Json;
        using ForgeTrust.AppSurface.Evidence.Aspire;
        using ForgeTrust.AppSurface.Evidence.Contracts;
        using ForgeTrust.AppSurface.Evidence.Planner;
        using ForgeTrust.AppSurface.Evidence.Readiness;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Hosting;
        using Microsoft.Extensions.Logging;
        using Microsoft.Extensions.DependencyInjection;

        var root = Path.GetFullPath(args.Single());
        Check(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, "synthetic local child only");
        var ids = new[] { {{string.Join(", ", PreparedHostPackageIds.Select(id => JsonSerializer.Serialize(id)))}} };
        var assemblies = ids.Select(id => Assembly.Load(id)).Select(assembly => new
        {
            name = assembly.GetName().Name!, path = assembly.Location,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location)))
        }).ToArray();
        var declaration = new EvidenceProducerDeclaration("http", "custom", "1", ["web"], ["http/ok@1"],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 4096)], 30)
        { Inputs = new EvidenceProducerInputs(["input.txt"], []) };
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted,
            [new EvidenceResourceDeclaration("web", "completion", 30, [])], [declaration],
            [new EvidenceObligation("http", "http", "Real Kestrel response", ["http"], "http/ok@1")]);
        var policy = new EvidencePolicy("packed-host", "1", profile.Id, [profile], [new EvidencePolicyRule("all", "**", profile.Id)]);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("input.txt")]);
        var state = new State();
        EvidenceHostOptions Options(string run) => new() { Reuse = true, RepositoryRoot = Path.Join(root, "repo"),
            ReceiptStore = Path.Join(root, "receipts"), ArtifactDirectory = Path.Join(root, run),
            ProducerCleanupTimeout = TimeSpan.FromSeconds(10), ReceiptWaitTimeout = TimeSpan.FromSeconds(30) };
        var firstProducer = new Producer(state);
        var firstFactoryCount = 0;
        EvidenceManifest first;
        Console.WriteLine("packed-host: preparing fresh run");
        await using (var prepared = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            registration.AddProducer(firstProducer, "packed-http-v1");
            registration.AddResource("web", "kestrel-body-and-health-v1");
        }, Options("first")))
        {
            Check(prepared.RequiredResourceIds.SequenceEqual(new[] { "web" }), "first exact resource closure");
            first = await prepared.RunAsync(async (requested, token) =>
            {
                Check(requested.SequenceEqual(new[] { "web" }), "first factory exact web");
                firstFactoryCount++;
                Console.WriteLine("packed-host: starting Kestrel");
                var contentRoot = Path.Join(root, "web-root");
                Directory.CreateDirectory(contentRoot);
                var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
                { ContentRootPath = contentRoot, EnvironmentName = "Production", Args = [] });
                Console.WriteLine("packed-host: Kestrel builder created");
                builder.Logging.ClearProviders();
                builder.WebHost.UseKestrel();
                builder.Services.AddRouting();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                var app = builder.Build();
                Console.WriteLine("packed-host: Kestrel application built");
                app.MapGet("/health", () => "ready");
                app.MapGet("/evidence", () => "owned-http-evidence");
                try
                {
                    Console.WriteLine("packed-host: Kestrel StartAsync entered");
                    await app.StartAsync(token);
                    state.Endpoint = new Uri(app.Urls.Single());
                    Console.WriteLine("packed-host: Kestrel started");
                    return new EvidenceHostResourceScope([new Readiness(state)], new Owner(app, state));
                }
                catch { await app.DisposeAsync(); throw; }
            });
            Console.WriteLine("packed-host: fresh RunAsync completed");
            Check(first.ExecutionVerdict == EvidenceExecutionVerdict.Passed && first.ProducerResults.Single().Outcome == EvidenceProducerOutcome.Passed,
                "fresh host passed");
        }
        Check(state.Cleaned == 1 && firstProducer.Runs == 1 && first.Metrics.CleanupCompleted, "fresh actual application cleanup");
        Console.WriteLine("packed-host: fresh prepared owner disposed");
        var secondProducer = new Producer(state);
        var secondFactoryCount = 0;
        EvidenceManifest second;
        string restored;
        Console.WriteLine("packed-host: preparing warm run");
        await using (var prepared = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            registration.AddProducer(secondProducer, "packed-http-v1");
            registration.AddResource("web", "kestrel-body-and-health-v1");
        }, Options("second")))
        {
            Check(prepared.RequiredResourceIds.Count == 0, "all hit empty resource closure");
            second = await prepared.RunAsync((_, _) =>
            { secondFactoryCount++; throw new InvalidOperationException("All-hit factory must not start Kestrel"); });
            Check(second.ExecutionVerdict == EvidenceExecutionVerdict.Passed && second.ClaimKind == EvidenceClaimKind.LocalReusedComplete
                && second.Eligibility == EvidenceClaimEligibility.Informational && second.ProducerResults.Single().Outcome == EvidenceProducerOutcome.Reused,
                "warm host informational reuse");
            Check(EvidenceManifestBuilder.Verify(prepared.Plan, second) && !EvidenceManifestBuilder.VerifyForGate(prepared.Plan, second), "current claim verification");
            restored = Path.Join(prepared.ArtifactDirectories["http"], "reports", "result.txt");
            Check(await File.ReadAllTextAsync(restored) == "owned-http-evidence", "restored real HTTP artifact");
            var artifact = second.ProducerResults.Single().Artifacts!.Single();
            Check(artifact.Sha256 == Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(restored))), "restored artifact digest");
        }
        Check(firstFactoryCount == 1 && secondFactoryCount == 0 && secondProducer.Runs == 0 && state.Cleaned == 1, "selective Kestrel startup");
        await File.WriteAllTextAsync(Path.Join(root, "host-proof.json"), JsonSerializer.Serialize(new
        {
            passed = true, firstFactoryCount, secondFactoryCount, firstProducerRuns = firstProducer.Runs,
            secondProducerRuns = secondProducer.Runs, cleanedApplications = state.Cleaned,
            firstOutcome = first.ProducerResults.Single().Outcome.ToString(), secondOutcome = second.ProducerResults.Single().Outcome.ToString(),
            claim = second.ClaimKind.ToString(), eligibility = second.Eligibility.ToString(), restoredArtifact = restored, assemblies
        }, new JsonSerializerOptions { WriteIndented = true }));
        static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        sealed class State { internal Uri? Endpoint; internal int Cleaned; }
        sealed class Producer(State state) : IEvidenceProducer
        {
            public string Id => "http";
            internal int Runs;
            public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken token)
            {
                Runs++;
                Console.WriteLine("packed-host: HTTP evidence starting");
                using var client = new HttpClient();
                var value = await client.GetStringAsync(new Uri(state.Endpoint!, "/evidence"), token);
                Console.WriteLine("packed-host: HTTP evidence received");
                if (value != "owned-http-evidence") throw new InvalidOperationException("Real HTTP response mismatch");
                await context.Artifacts!.WriteAsync("report", "reports/result.txt", Encoding.UTF8.GetBytes(value), token);
                return new(Id, EvidenceProducerOutcome.Passed, ["http/ok@1"], Artifacts: context.Artifacts.WrittenArtifacts);
            }
        }
        sealed class Readiness(State state) : IEvidenceResourceReadiness
        {
            public string Id => "web";
            public async Task WaitUntilReadyAsync(CancellationToken token)
            {
                using var client = new HttpClient();
                await HttpReadinessProbe.WaitAsync(client, state.Endpoint!, "/health", TimeSpan.FromSeconds(10), cancellationToken: token);
            }
        }
        sealed class Owner(WebApplication app, State state) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Console.WriteLine("packed-host: stopping Kestrel");
                await app.StopAsync(deadline.Token);
                await app.DisposeAsync();
                state.Cleaned++;
                Console.WriteLine("packed-host: Kestrel cleanup completed");
            }
        }
        """;
}
