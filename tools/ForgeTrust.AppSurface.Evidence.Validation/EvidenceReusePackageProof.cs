using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Evidence.Contracts;
using static ForgeTrust.AppSurface.Evidence.Validation.PackedCandidatePackages;
using AdmittedFeed = ForgeTrust.AppSurface.Evidence.Validation.EvidenceReusePackageAdmission.AdmittedFeed;
using PackageProof = ForgeTrust.AppSurface.Evidence.Validation.EvidenceReusePackageAdmission.PackageProof;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Exercises the installed candidate CLI and an independent package-only prepared host in owned scratch.</summary>
/// <remarks>
/// Feed admission precedes output creation. Consumer restores intentionally use private caches and source mapping;
/// normal developer caches and checkout outputs are not provenance. Only nested synthetic-local executions remove
/// GITHUB_ACTIONS. The proof process and normal CI admission remain unchanged. No container runtime is required.
/// Stage logs start with a diagnostic marker before launch and are replaced by captured output only after joining
/// the process. A starting marker is not settlement or acceptance evidence. Failures are reported even if
/// proof.json cannot be written.
/// No caller-owned files, feeds, or shared processes are removed.
/// </remarks>
internal static class EvidenceReusePackageProof
{
    private const string PinnedSdkConfigurationResource = Prefix + "Evidence.Validation.PinnedSdk.global.json";
    private const string ContractScope = EvidenceReusePackageAdmission.ContractScope;
    private const string ToolBundleVersionContract = EvidenceReusePackageAdmission.ToolBundleVersionContract;
    private const string PrivateDependencyContract = EvidenceReusePackageAdmission.PrivateDependencyContract;
    private const int RejectedProofExitCode = 1;
    private const int MaximumProofDocumentBytes = 16 * 1024 * 1024;
    private const int MaximumCliArtifacts = 128;
    private const long MaximumCliArtifactBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan HostRunTimeout = TimeSpan.FromMinutes(1);
    private static readonly EvidenceCoverageGateRequirements CliProofGate = new(95, 85, TolerancePercent: 0);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Gets the exact package identities restored by the independent prepared host.</summary>
    private static IReadOnlyList<string> PreparedHostPackageIds { get; } = Array.AsReadOnly(new[]
    {
        AspireId, ContractsId, PlannerId, ReadinessId, SupportId, CoverageId
    });

    /// <summary>Runs the bounded packed CLI/host journey, returning zero only after every recorded assertion passes.</summary>
    /// <param name="feed">Local candidate feed with the complete same-version family.</param>
    /// <param name="version">Exact candidate package and dependency-graph version.</param>
    /// <param name="output">Fresh directory outside the source checkout; retained for diagnostics.</param>
    /// <param name="dotnet">Absolute SDK host matching the repository pin embedded at build time.</param>
    /// <param name="expectedSourceCommit">Trusted full source commit matching every candidate nuspec.</param>
    /// <param name="cancellationToken">Cancellation joins owned process cleanup before propagating.</param>
    internal static async Task<int> RunAsync(string feed, string version, string output, string dotnet,
        string expectedSourceCommit, CancellationToken cancellationToken)
    {
        var (admissionExitCode, admission, rejection) = await TryAdmitAsync(
            feed, version, output, dotnet, expectedSourceCommit, cancellationToken).ConfigureAwait(false);
        if (admission is null)
        {
            WriteFailure("admission", rejection!);
            return admissionExitCode;
        }

        using var outputClaim = admission.OutputClaim;
        output = admission.Output;
        dotnet = admission.Dotnet;
        var packages = admission.Feed.Packages;
        var stages = new List<StageResult>();
        var stage = "admission";
        var reportPath = PathUtils.PathUnder(output, "proof.json");
        using var progress = PackedProofProgress.TryCreate(PathUtils.PathUnder(output, "packed-progress.jsonl"));
        try
        {
            progress?.Record("admission", "completed");
            var runner = new ProofRunner(output, dotnet, stages, progress);
            var pinnedSdkVersion = await WritePinnedSdkConfigurationAsync(output, cancellationToken).ConfigureAwait(false);
            var sdk = await runner.RunAsync("sdk", dotnet, ["--version"], output, cancellationToken).ConfigureAwait(false);
            var sdkVersion = sdk.StandardOutput.Trim();
            Require(sdkVersion == pinnedSdkVersion, $"pinned-sdk-{pinnedSdkVersion}");
            var config = Path.Join(output, "NuGet.Config");
            WriteNuGetConfig(config, Path.GetFullPath(feed));
            stage = "negative-admission";
            var negatives = await ProveNegativeAdmissionAsync(packages, version, expectedSourceCommit, output, dotnet, cancellationToken).ConfigureAwait(false);
            stage = "tool-install";
            var toolRoot = Path.Join(output, "installed-tool");
            await runner.RunAsync(stage, dotnet, ["tool", "install", ToolId, "--tool-path", toolRoot, "--version", version,
                "--configfile", config, "--no-cache"], output, cancellationToken).ConfigureAwait(false);
            var toolArchive = Directory.GetFiles(Path.Join(toolRoot, ".store"), "*.nupkg", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Equals(Tool.ArchiveName(version), StringComparison.OrdinalIgnoreCase)).Single();
            Require(await HashAsync(toolArchive, cancellationToken).ConfigureAwait(false) == packages[ToolId].Sha256, "installed-tool-archive-sha");
            var toolDll = Directory.GetFiles(Path.Join(toolRoot, ".store"), ToolId + ".dll", SearchOption.AllDirectories).Single();
            var toolAssemblies = await VerifyToolAssembliesAsync(Path.GetDirectoryName(toolDll)!, admission.Feed.ToolAssemblyHashes, cancellationToken).ConfigureAwait(false);
            var command = Path.Join(toolRoot, OperatingSystem.IsWindows() ? "appsurface.exe" : "appsurface");
            Require(File.Exists(command), "installed-tool-shim");
            stage = "host";
            var host = await ProveHostAsync(output, version, packages, runner, cancellationToken).ConfigureAwait(false);
            stage = "cli";
            var cli = await ProveCliAsync(output, command, config, version, runner, cancellationToken).ConfigureAwait(false);
            progress?.Record("proof-write", "started");
            await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                passed = true,
                version,
                expectedSourceCommit,
                sdk = sdkVersion,
                repository = admission.Repository,
                packages = packages.Values.OrderBy(package => package.Id, StringComparer.Ordinal),
                installedTool = new { command, toolDll, assemblies = toolAssemblies },
                negatives,
                host,
                cli,
                stages,
                limitations = "Cooperative local workload proof on the current OS; no containment or unregistered-child settlement claim."
            }, JsonOptions), cancellationToken).ConfigureAwait(false);
            progress?.Record("proof-write", "completed");
            Console.WriteLine("Packed Evidence CLI and prepared Kestrel host reuse proof passed.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsProofFailure(exception))
        {
            var contract = GetContractFailure(exception);
            WriteFailure(stage, contract ?? exception.GetType().Name);
            try
            {
                progress?.Record("failure-proof-write", "started");
                await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    passed = false,
                    stage,
                    failureType = exception.GetType().Name,
                    contract,
                    stages
                }, JsonOptions), CancellationToken.None).ConfigureAwait(false);
                progress?.Record("failure-proof-write", "completed");
            }
            catch (Exception reportException) when (reportException is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("ASEVD917 Could not write the packed reuse failure report 'proof.json'.");
            }
            return RejectedProofExitCode;
        }
    }

    /// <summary>Admits the feed and claims fresh output before either proof can start any consumer work.</summary>
    /// <remarks>The caller owns the returned claim until its actual work ends.</remarks>
    private static async Task<(int ExitCode, AdmittedProof? Proof, string? Rejection)> TryAdmitAsync(
        string feed, string version, string output, string dotnet, string expectedSourceCommit, CancellationToken cancellationToken)
    {
        try
        {
            var admittedFeed = await EvidenceReusePackageAdmission.InspectFeedAsync(feed, version, expectedSourceCommit, cancellationToken).ConfigureAwait(false);
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
            var outputClaim = new FileStream(output + ".packed-reuse.claim", FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            try
            {
                Require(!PathEntryExists(output), "fresh-output-and-sdk");
                Directory.CreateDirectory(output);
                return (0, new AdmittedProof(admittedFeed, repository, output, dotnet, outputClaim), null);
            }
            catch
            {
                outputClaim.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsProofFailure(exception))
        {
            return (RejectedProofExitCode, null, GetContractFailure(exception) ?? exception.GetType().Name);
        }
    }

    private static bool IsProofFailure(Exception exception)
        => exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException
            or JsonException or XmlException or UnauthorizedAccessException or TimeoutException or OperationCanceledException
            or KeyNotFoundException or System.ComponentModel.Win32Exception or NotSupportedException;

    private static string? GetContractFailure(Exception exception)
        => exception.Message.StartsWith(ValidationProof.ContractFailurePrefix + ContractScope, StringComparison.Ordinal)
            ? exception.Message : null;

    private static void WriteFailure(string stage, string reason)
        => Console.Error.WriteLine($"ASEVD917 Packed reuse proof failed at {stage}: {reason}. Inspect retained stage logs and candidate closure.");

    // The embedded pin keeps copied validators independent of the checkout they are run from.
    private static async Task<string> WritePinnedSdkConfigurationAsync(string output, CancellationToken cancellationToken)
    {
        using var resource = typeof(EvidenceReusePackageProof).Assembly.GetManifestResourceStream(PinnedSdkConfigurationResource)
            ?? throw new InvalidOperationException("The validator SDK configuration is missing.");
        using var reader = new StreamReader(resource);
        var configuration = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(configuration);
        var version = document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("The validator SDK version is missing.");
        await File.WriteAllTextAsync(Path.Join(output, "global.json"), configuration, cancellationToken).ConfigureAwait(false);
        return version;
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
        NegativeAdmissionCase[] cases =
        [
            new("tool-graph-version", ToolId, ToolId + ".deps.json", ToolBundleVersionContract, SkewToolSupportVersion),
            new("host-private-edge", AspireId, ".nuspec", PrivateDependencyContract,
                value => value.Replace($"[{version}]", "[0.0.0-mismatch]", StringComparison.Ordinal)),
        ];
        var results = new List<object>();
        foreach (var scenario in cases)
        {
            var root = PathUtils.PathUnder(output, "negative-" + scenario.Kind);
            var feed = PathUtils.PathUnder(root, "feed");
            Directory.CreateDirectory(feed);
            foreach (var package in packages.Values)
            {
                var destination = PathUtils.PathUnder(feed, Path.GetFileName(package.Archive));
                if (package.Id == scenario.PackageId)
                    await RewriteNegativeArchiveAsync(package.Archive, destination, scenario, token).ConfigureAwait(false);
                else
                    File.Copy(package.Archive, destination);
            }

            var rejectedOutput = PathUtils.PathUnder(root, "must-not-exist");
            var expectedFailure = ValidationProof.ContractFailurePrefix + ContractScope + scenario.ExpectedContract;
            var (exitCode, admission, contract) = await TryAdmitAsync(feed, version, rejectedOutput, dotnet, commit, token).ConfigureAwait(false);
            using var unexpectedClaim = admission?.OutputClaim;
            var outputCreated = PathEntryExists(rejectedOutput);
            Require(!outputCreated, "negative-admission-before-output");
            Require(exitCode == RejectedProofExitCode && contract == expectedFailure, "negative-admission-unexpected-outcome");
            results.Add(new { kind = scenario.Kind, exitCode, outputCreated, contract });
        }
        return results;
    }

    private static async Task RewriteNegativeArchiveAsync(string archive, string destination,
        NegativeAdmissionCase scenario, CancellationToken token)
    {
        using var source = ZipFile.OpenRead(archive);
        using var target = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            var created = target.CreateEntry(entry.FullName);
            using var input = entry.Open();
            using var written = created.Open();
            if (!entry.FullName.EndsWith(scenario.EntrySuffix, StringComparison.Ordinal))
            {
                await input.CopyToAsync(written, token).ConfigureAwait(false);
                continue;
            }

            using var text = new StreamReader(input);
            var original = await text.ReadToEndAsync(token).ConfigureAwait(false);
            var mutated = scenario.Mutate(original);
            Require(mutated != original, "negative-archive-mutation-applied");
            await written.WriteAsync(Encoding.UTF8.GetBytes(mutated), token).ConfigureAwait(false);
        }
    }

    private static string SkewToolSupportVersion(string value)
    {
        using var deps = JsonDocument.Parse(value);
        var supportLibrary = deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Single(library => library.Name.StartsWith(SupportId + "/", StringComparison.Ordinal)).Name;
        return value.Replace(supportLibrary, SupportId + "/0.0.0-mismatch", StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<AssemblyProof>> VerifyToolAssembliesAsync(string root,
        IReadOnlyDictionary<string, string> assemblyHashes, CancellationToken token)
    {
        var results = new List<AssemblyProof>(assemblyHashes.Count);
        foreach (var id in EvidenceReusePackageAdmission.BundledToolPackageIds)
        {
            var actual = PathUtils.PathUnder(root, id + ".dll");
            var expected = assemblyHashes[id];
            Require(await HashAsync(actual, token).ConfigureAwait(false) == expected, "installed-tool-bundled-dll-sha");
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
        var fixture = Path.Join(AppContext.BaseDirectory, "fixtures", "packed-host");
        var projectTemplate = await File.ReadAllTextAsync(Path.Join(fixture, "PackedHost.csproj.template"), token).ConfigureAwait(false);
        await File.WriteAllTextAsync(project, projectTemplate.Replace("CANDIDATE_VERSION", version, StringComparison.Ordinal), token).ConfigureAwait(false);
        File.Copy(Path.Join(fixture, "Program.cs"), Path.Join(app, "Program.cs"));
        await runner.RunAsync("host-restore", runner.Dotnet, ["restore", project, "--configfile", Path.Join(output, "NuGet.Config"), "--use-lock-file"], output, token).ConfigureAwait(false);
        await VerifyAssetsAsync(Path.Join(app, "obj", "project.assets.json"), packages, runner.Packages, token).ConfigureAwait(false);
        await runner.RunAsync("host-build", runner.Dotnet, ["build", project, "--no-restore", "-c", "Release", "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false", "-warnaserror"], output, token).ConfigureAwait(false);
        var buildRoot = Path.Join(app, "bin", "Release", "net10.0");
        var built = await RunHostVariantAsync(buildRoot, "host-built", "host-run").ConfigureAwait(false);
        var published = Path.Join(output, "host-published");
        await runner.RunAsync("host-publish", runner.Dotnet, ["publish", project, "--no-restore", "-c", "Release", "-o", published,
            "--self-contained", "false", "-p:PublishTrimmed=false", "-p:PublishSingleFile=false", "--disable-build-servers", "-p:UseSharedCompilation=false", "-nodeReuse:false", "-warnaserror"], output, token).ConfigureAwait(false);
        var publish = await RunHostVariantAsync(published, "host-published", "host-published-run").ConfigureAwait(false);
        return new { built, publish };

        async Task<JsonElement> RunHostVariantAsync(string assemblyRoot, string fixtureStage, string runStage)
        {
            var proofRoot = Path.Join(output, fixtureStage + "-proof");
            await PrepareGitAsync(proofRoot, runner, fixtureStage, token).ConfigureAwait(false);
            await runner.RunSyntheticLocalAsync(runStage, runner.Dotnet, [Path.Join(assemblyRoot, "PackedHost.dll"), proofRoot, .. PreparedHostPackageIds],
                output, token, stageTimeout: HostRunTimeout).ConfigureAwait(false);
            return await VerifyHostRunAsync(assemblyRoot, proofRoot, packages, token).ConfigureAwait(false);
        }
    }

    private static async Task<JsonElement> VerifyHostRunAsync(string assemblyRoot, string proofRoot,
        IReadOnlyDictionary<string, PackageProof> packages, CancellationToken token)
    {
        using var proof = ValidationProof.ReadJsonFile(PathUtils.PathUnder(proofRoot, "host-proof.json"), MaximumProofDocumentBytes, token);
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
        using var deps = ValidationProof.ReadJsonFile(PathUtils.PathUnder(assemblyRoot, "PackedHost.deps.json"), MaximumProofDocumentBytes, token);
        _ = VerifyPackageClosure(deps.RootElement.GetProperty("libraries"), packages,
            "published-host-exact-package-closure", "published-host-package-only-deps");
        return data.Clone();
    }

    private static IReadOnlyList<PackageProof> VerifyPackageClosure(JsonElement libraries,
        IReadOnlyDictionary<string, PackageProof> packages, string closureDiagnostic, string packageDiagnostic)
    {
        var family = PackedDependencyLibrary.Read(libraries)
            .Where(library => library.Id.StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Require(family.Length == PreparedHostPackageIds.Count
            && new HashSet<string>(family.Select(library => library.Id), StringComparer.Ordinal).SetEquals(PreparedHostPackageIds),
            closureDiagnostic);

        var verifiedPackages = new List<PackageProof>(family.Length);
        foreach (var library in family)
        {
            var type = library.Entry.Value.GetProperty("type").GetString();
            var package = packages.GetValueOrDefault(library.Id);
            Require(type == "package" && package is not null && library.Matches(package.Id, package.Version), packageDiagnostic);
            verifiedPackages.Add(package!);
        }
        return verifiedPackages;
    }

    private static async Task VerifyAssetsAsync(string path, IReadOnlyDictionary<string, PackageProof> packages, string cache, CancellationToken token)
    {
        using var document = ValidationProof.ReadJsonFile(path, MaximumProofDocumentBytes, token);
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
        var fixture = await PrepareCliFixtureAsync(output, config, version, runner, token).ConfigureAwait(false);
        var results = new List<object>();
        string? originSha = null;
        foreach (var mode in new[] { CliProofMode.Fresh, CliProofMode.Warm, CliProofMode.Audit })
        {
            var stage = "cli-" + CliModeLabel(mode);
            var root = PathUtils.PathUnder(output, stage);
            var arguments = new List<string> { "evidence", "run", "--policy", "evidence.policy.json", "--solution", "Consumer.slnx", "--path", "Consumer.Alpha/AlphaDecision.cs", "--reuse", "--receipt-store", Path.Join(output, "cli-receipts"), "--output", root };
            if (mode == CliProofMode.Audit) arguments.Add("--verify-reuse");
            await runner.RunSyntheticLocalAsync(stage, command, arguments, fixture.Repository, token).ConfigureAwait(false);
            runner.Progress?.Record(stage, "artifact-validation-started");
            var run = await ValidateCliRunAsync(root, mode, output, token).ConfigureAwait(false);
            runner.Progress?.Record(stage, "artifact-validation-completed");
            if (mode == CliProofMode.Fresh) originSha = run.CoverageSha;
            else if (mode == CliProofMode.Warm)
            {
                Require(run.CoverageSha == originSha, "warm-original-report-byte-identity");
                await runner.RunSyntheticLocalAsync("restored-coverage-reader", command,
                    ["coverage", "gate", "--coverage", Path.Join(root, "coverage", "coverage.cobertura.xml"),
                        "--min-line", CliProofGate.MinLinePercent.ToString(CultureInfo.InvariantCulture),
                        "--min-branch", CliProofGate.MinBranchPercent.ToString(CultureInfo.InvariantCulture),
                        "--tolerance", CliProofGate.TolerancePercent.ToString(CultureInfo.InvariantCulture),
                        "--output", Path.Join(output, "restored-reader-reports")],
                    fixture.Repository, token).ConfigureAwait(false);
            }
            Require(ValidationProof.Equal(fixture.InputHashes, await FixtureInputsAsync(fixture.Repository, token).ConfigureAwait(false)), "locked-complete-inputs-unchanged");
            results.Add(run.Proof);
        }
        return new { inputs = fixture.InputHashes, runs = results };
    }

    private static async Task<(string Repository, SortedDictionary<string, string> InputHashes)> PrepareCliFixtureAsync(
        string output, string config, string version, ProofRunner runner, CancellationToken token)
    {
        var repository = Path.Join(output, "cli-inputs");
        CopyFixture(Path.Join(AppContext.BaseDirectory, "fixtures", "consumer"), repository);
        File.Copy(config, Path.Join(repository, "NuGet.Config"));
        File.Copy(Path.Join(output, "global.json"), Path.Join(repository, "global.json"));
        var testsProject = Path.Join(repository, "Consumer.Alpha.Tests", "Consumer.Alpha.Tests.csproj");
        var testsDocument = XDocument.Load(testsProject);
        testsDocument.Root!.Add(new XElement("ItemGroup", new XElement("PackageReference",
            new XAttribute("Include", ContractsId), new XAttribute("VersionOverride", "[" + version + "]"))));
        testsDocument.Save(testsProject);
        File.Copy(PathUtils.PathUnder(AppContext.BaseDirectory, "fixtures", "packed-cli", "OwnedChildTests.cs"),
            PathUtils.PathUnder(repository, "Consumer.Alpha.Tests", "OwnedChildTests.cs"));
        await File.WriteAllTextAsync(Path.Join(repository, ".gitignore"), "**/bin/\n**/obj/\n", token).ConfigureAwait(false);
        var producerDeclaration = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["coverage/complete@1"],
            [new EvidenceArtifactSlot("coverage-reports", "coverage", "application/octet-stream", false, MaximumCliArtifactBytes) { MaximumFiles = MaximumCliArtifacts }],
            240, CliProofGate)
        {
            Inputs = new(["**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/packages.lock.json", "Consumer.slnx", "NuGet.Config", "global.json", "evidence.policy.json"], ["**/bin/**", "**/obj/**"]),
            CoverageExecution = new("Release", EvidenceCoverageTestResultFormat.Junit, 1) { CooperativeChildLifetime = true }
        };
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [producerDeclaration],
            [new EvidenceObligation("coverage", "coverage", "Packed consumer coverage", ["coverage"], "coverage/complete@1")]);
        var policy = new EvidencePolicy("packed-coverage", "1", "local", [profile], [new EvidencePolicyRule("all", "**", "local")]);
        await File.WriteAllBytesAsync(Path.Join(repository, "evidence.policy.json"), EvidenceCanonicalJson.Serialize(policy), token).ConfigureAwait(false);
        await runner.RunAsync("fixture-lock", runner.Dotnet, ["restore", Path.Join(repository, "Consumer.slnx"), "--configfile", Path.Join(repository, "NuGet.Config"), "--use-lock-file", "--force-evaluate"], repository, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(repository, "Directory.Build.props"), "<Project><PropertyGroup><RestoreLockedMode>true</RestoreLockedMode></PropertyGroup></Project>", token).ConfigureAwait(false);
        await GitInitAsync(repository, runner, "cli", token).ConfigureAwait(false);
        var inputHashes = await FixtureInputsAsync(repository, token).ConfigureAwait(false);
        return (repository, inputHashes);
    }

    private static async Task<(object Proof, string CoverageSha)> ValidateCliRunAsync(
        string root, CliProofMode mode, string output, CancellationToken token)
    {
        EvidenceManifest manifest;
        await using (var input = File.OpenRead(PathUtils.PathUnder(root, "evidence-manifest.json")))
        {
            manifest = await EvidenceCanonicalJson.DeserializeAsync<EvidenceManifest>(input, cancellationToken: token).ConfigureAwait(false);
        }
        Require(manifest.ExecutionVerdict == EvidenceExecutionVerdict.Passed, "cli-manifest-passed");
        var producer = manifest.ProducerResults.Single();
        var expected = mode == CliProofMode.Warm ? EvidenceProducerOutcome.Reused : EvidenceProducerOutcome.Passed;
        Require(producer.Outcome == expected, "cli-fresh-warm-audit-outcome");
        if (mode == CliProofMode.Audit)
        {
            using var summary = ValidationProof.ReadJsonFile(PathUtils.PathUnder(root, "evidence-summary.json"), MaximumProofDocumentBytes, token);
            Require(summary.RootElement.GetProperty("reuseDecisions").EnumerateArray().Any(decision =>
                decision.GetProperty("ProducerId").GetString() == producer.ProducerId
                && decision.TryGetProperty("VerifiedReceiptCompared", out var compared) && compared.GetBoolean()),
                "cli-audit-compared-verified-receipt");
        }
        if (mode == CliProofMode.Warm)
        {
            Require(manifest.ClaimKind == EvidenceClaimKind.LocalReusedComplete
                && manifest.Eligibility == EvidenceClaimEligibility.Informational, "cli-reuse-informational");
            Require(producer.Diagnostic is { } diagnostic
                && diagnostic.Contains("coverage/coverage-gate.md", StringComparison.Ordinal)
                && diagnostic.Contains("relative to the producer artifact root", StringComparison.Ordinal)
                && !diagnostic.Contains(PathUtils.PathUnder(output, "cli-" + CliModeLabel(CliProofMode.Fresh)), StringComparison.Ordinal),
                "cli-reuse-diagnostic-current-artifacts");
        }
        var artifacts = producer.Artifacts?.ToArray() ?? [];
        var (testCases, testProjects, bytes, coverageSha) = await VerifyCliCoverageReportsAsync(root, artifacts, token).ConfigureAwait(false);
        return (new
        {
            mode = CliModeLabel(mode),
            outcome = expected.ToString(),
            claim = manifest.ClaimKind.ToString(),
            testCaseCount = testCases,
            testProjectCount = testProjects,
            artifactCount = artifacts.Length,
            artifactBytes = bytes,
            coverageSha256 = coverageSha,
            inventory = artifacts
        }, coverageSha);
    }

    /// <summary>Verifies the packed CLI artifact inventory and counts tests in the reports named by the verified timings document.</summary>
    /// <param name="root">The run output root containing the managed coverage directory.</param>
    /// <param name="artifacts">The exact coverage artifacts declared by the producer manifest.</param>
    /// <param name="token">Cancels inventory verification and report reads.</param>
    /// <returns>Verified test and project counts, total inventory bytes, and the merged coverage SHA-256.</returns>
    /// <remarks>Each fixture project must name a distinct JUnit report in the manifest. Unreferenced reports are still hashed.</remarks>
    internal static async Task<(int TestCases, int TestProjects, long ArtifactBytes, string CoverageSha)> VerifyCliCoverageReportsAsync(
        string root, IReadOnlyList<EvidenceArtifactResult> artifacts, CancellationToken token)
    {
        const string coverageReport = "coverage/coverage.cobertura.xml";
        const string gateReport = "coverage/coverage-gate.json";
        const string timingsReport = "coverage/timings.json";
        string[] requiredReports = [coverageReport, gateReport, "coverage/coverage-gate.md", timingsReport];
        Require(artifacts.Count is > 5 and <= MaximumCliArtifacts, "cli-complete-bounded-custody");
        var byPath = new Dictionary<string, EvidenceArtifactResult>(artifacts.Count, StringComparer.Ordinal);
        long bytes = 0;
        foreach (var artifact in artifacts)
        {
            var relative = artifact.RelativePath;
            Require(relative.StartsWith("coverage/", StringComparison.Ordinal) && !relative.Contains('\\')
                && !relative.Split('/').Any(part => part is "" or "." or "..") && !Path.IsPathRooted(relative), "cli-established-coverage-layout");
            Require(byPath.TryAdd(relative, artifact), "cli-unambiguous-coverage-inventory");
            Require(artifact.LengthBytes >= 0 && artifact.LengthBytes <= MaximumCliArtifactBytes - bytes, "cli-existing-aggregate-limit");
            bytes += artifact.LengthBytes;
        }
        foreach (var required in requiredReports)
            Require(byPath.ContainsKey(required), "cli-required-managed-reports-in-inventory");

        var timingsBytes = await ReadVerifiedCliReportAsync(PathUtils.PathUnder(root, timingsReport), byPath[timingsReport], token).ConfigureAwait(false);
        using var timings = JsonDocument.Parse(timingsBytes);
        var projects = timings.RootElement.GetProperty("projects");
        var testProjects = projects.GetArrayLength();
        Require(testProjects == 2, "real-two-test-project-fixture");
        var junitPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects.EnumerateArray())
        {
            var reports = project.GetProperty("testResults").EnumerateArray()
                .Where(report => report.GetProperty("format").GetString() == "junit").ToArray();
            Require(reports.Length == 1, "cli-one-junit-report-per-project");
            var path = reports[0].GetProperty("path").GetString();
            var relative = "coverage/" + path;
            Require(!string.IsNullOrWhiteSpace(path) && byPath.ContainsKey(relative)
                && !requiredReports.Contains(relative, StringComparer.Ordinal), "cli-junit-report-in-inventory");
            Require(junitPaths.Add(relative), "cli-distinct-project-junit-reports");
        }

        var gateBytes = await ReadVerifiedCliReportAsync(PathUtils.PathUnder(root, gateReport), byPath[gateReport], token).ConfigureAwait(false);
        var testCases = 0;
        foreach (var (relative, artifact) in byPath)
        {
            if (relative == timingsReport || relative == gateReport) continue;
            var file = PathUtils.PathUnder(root, relative);
            if (junitPaths.Contains(relative))
            {
                var report = await ReadVerifiedCliReportAsync(file, artifact, token).ConfigureAwait(false);
                testCases += CountCliTestCases(report, token);
            }
            else
                await VerifyCliReportAsync(file, artifact, token).ConfigureAwait(false);
        }
        using var gate = JsonDocument.Parse(gateBytes);
        ValidationProof.RequireOverallCoverageGate(gate.RootElement, CliProofGate, ContractScope + "unchanged-95-85-coverage-gate");
        Require(testCases == 6, "actual-five-fixture-tests-and-packed-child-helper");
        return (testCases, testProjects, bytes, byPath[coverageReport].Sha256);
    }

    /// <summary>Captures one report the packed CLI proof needs to interpret and binds it to its manifest.</summary>
    /// <param name="path">The report path already resolved within the run output.</param>
    /// <param name="artifact">The recorded length and SHA-256 of the report.</param>
    /// <param name="cancellationToken">Cancels the bounded read.</param>
    /// <returns>The exact original bytes for the caller to parse.</returns>
    /// <remarks>Gate, timings and JUnit reports are capped at 16 MiB each. Other artifacts are hashed as streams.</remarks>
    internal static async Task<ReadOnlyMemory<byte>> ReadVerifiedCliReportAsync(
        string path, EvidenceArtifactResult artifact, CancellationToken cancellationToken)
    {
        Require(artifact.LengthBytes is > 0 and <= MaximumProofDocumentBytes, "cli-interpreted-report-byte-limit");
        using var snapshot = new MemoryStream((int)artifact.LengthBytes);
        await VerifyCliReportAsync(path, artifact, cancellationToken, snapshot).ConfigureAwait(false);
        return snapshot.GetBuffer().AsMemory(0, (int)snapshot.Length);
    }

    private static async Task VerifyCliReportAsync(
        string path, EvidenceArtifactResult artifact, CancellationToken cancellationToken, Stream? snapshot = null)
    {
        Require(File.Exists(path), "cli-original-report-custody");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var (status, digest) = await EvidenceArtifactStream.HashAsync(input, artifact.LengthBytes, cancellationToken, snapshot)
            .ConfigureAwait(false);
        Require(status == EvidenceArtifactLengthStatus.Exact && digest == artifact.Sha256, "cli-original-report-custody");
    }

    private static int CountCliTestCases(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes.ToArray(), writable: false);
        using var xml = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var count = 0;
        while (xml.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "testcase")
                count++;
        }
        return count;
    }

    // These labels also appear in proof.json and the on-disk stage paths.
    private static string CliModeLabel(CliProofMode mode) => mode switch
    {
        CliProofMode.Fresh => "fresh",
        CliProofMode.Warm => "warm",
        CliProofMode.Audit => "audit",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private enum CliProofMode { Fresh, Warm, Audit }

    private static async Task PrepareGitAsync(string root, ProofRunner runner, string stage, CancellationToken token)
    {
        var repository = Path.Join(root, "repo");
        Directory.CreateDirectory(repository);
        await File.WriteAllTextAsync(Path.Join(repository, "input.txt"), "fixed packed Kestrel fixture v1", token).ConfigureAwait(false);
        await GitInitAsync(repository, runner, stage, token).ConfigureAwait(false);
    }

    private static async Task GitInitAsync(string root, ProofRunner runner, string stage, CancellationToken token)
    {
        await runner.RunAsync(stage + "-git-init", "git", ["init", "-q"], root, token).ConfigureAwait(false);
        await runner.RunAsync(stage + "-git-add", "git", ["-c", "core.hooksPath=" + Path.Join(root, "no-hooks"), "add", "."], root, token).ConfigureAwait(false);
    }

    private static async Task<SortedDictionary<string, string>> FixtureInputsAsync(string root, CancellationToken token)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, relative) in EnumerateFixtureFiles(root))
            result.Add(relative.Replace('\\', '/'), await HashAsync(file, token).ConfigureAwait(false));
        return result;
    }

    private static void CopyFixture(string source, string target)
    {
        Require(Directory.Exists(source), "bundled-closed-fixture");
        Directory.CreateDirectory(target);
        foreach (var (file, relative) in EnumerateFixtureFiles(source))
        {
            var destination = PathUtils.PathUnder(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static IEnumerable<(string File, string RelativePath)> EnumerateFixtureFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git")) continue;
            yield return (file, relative);
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
    private static void Require(bool condition, string contract) => ValidationProof.Require(condition, ContractScope + contract);
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { await using var input = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)); }
    /// <summary>Carries the verified candidate and exclusive output claim into the consumer proof.</summary>
    private sealed record AdmittedProof(AdmittedFeed Feed, string Repository,
        string Output, string Dotnet, FileStream OutputClaim);
    private sealed record NegativeAdmissionCase(string Kind, string PackageId, string EntrySuffix, string ExpectedContract,
        Func<string, string> Mutate);
    private sealed record AssemblyProof(string Name, string Path, string Sha256);
    private sealed record StageResult(string Stage, int ExitCode, long ElapsedMilliseconds, bool SyntheticLocal,
        string? InterruptedWhile);

    private sealed class ProofRunner(string root, string dotnet, List<StageResult> stages, PackedProofProgress? progress)
    {
        internal PackedProofProgress? Progress { get; } = progress;
        internal string Dotnet { get; } = dotnet;
        internal string Packages { get; } = PathUtils.PathUnder(root, "packages");
        internal Task<PackedProofProcess.Result> RunAsync(string stage, string executable, IReadOnlyList<string> arguments,
            string workingDirectory, CancellationToken cancellationToken)
            => RunStageAsync(stage, executable, arguments, workingDirectory, cancellationToken, syntheticLocal: false);

        internal Task<PackedProofProcess.Result> RunSyntheticLocalAsync(string stage, string executable, IReadOnlyList<string> arguments,
            string workingDirectory, CancellationToken cancellationToken, TimeSpan? stageTimeout = null)
            => RunStageAsync(stage, executable, arguments, workingDirectory, cancellationToken, syntheticLocal: true, stageTimeout: stageTimeout);

        private async Task<PackedProofProcess.Result> RunStageAsync(string stage, string executable, IReadOnlyList<string> arguments,
            string workingDirectory, CancellationToken cancellationToken, bool syntheticLocal, TimeSpan? stageTimeout = null)
        {
            var start = PackedProofProcess.CreateStartInfo(executable, arguments, workingDirectory);
            var environment = start.Environment;
            environment["NUGET_PACKAGES"] = Packages;
            environment["NUGET_HTTP_CACHE_PATH"] = PathUtils.PathUnder(root, "http-cache");
            environment["NUGET_SCRATCH"] = PathUtils.PathUnder(root, "nuget-scratch");
            environment["DOTNET_CLI_HOME"] = PathUtils.PathUnder(root, "cli-home");
            environment["APPSURFACE_ARTIFACT_HOME"] = PathUtils.PathUnder(root, "coverage-artifact-home");
            environment["DOTNET_ROOT"] = Path.GetDirectoryName(Dotnet)!;
            environment["PATH"] = Path.GetDirectoryName(Dotnet) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
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
            var result = await PackedProofProcess.RunAsync(stage, start, PathUtils.PathUnder(root, stage + ".log"), cancellationToken,
                stageTimeout ?? TimeSpan.FromMinutes(5), exit =>
                    stages.Add(new(stage, exit.ExitCode, exit.ElapsedMilliseconds, syntheticLocal, exit.InterruptedWhile)),
                Progress).ConfigureAwait(false);
            Require(result.ExitCode == 0, "stage-" + stage);
            return result;
        }
    }
}
