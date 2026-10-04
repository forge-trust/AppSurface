using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Inputs to the isolated installed-template proof; no input contains a credential.</summary>
/// <param name="RepositoryRoot">Source checkout used only for authored-source and source-revision evidence.</param>
/// <param name="ArtifactsDirectory">Exact first-party candidate archives; remote first-party fallback is prohibited.</param>
/// <param name="PackageVersion">Exact coordinated candidate version.</param>
/// <param name="ReceiptPath">Safe JSON report destination outside the temporary workspace.</param>
/// <param name="ThirdPartySource">Acquisition-only third-party feed; never used by final consumer restore.</param>
/// <param name="SourceCommit">Full revision bound by the producer; omitted only for local diagnostic runs.</param>
/// <param name="RunFirstWork">Whether to run the real pinned-container certificate, required on Linux.</param>
/// <param name="RunNativeSmoke">Whether to run the separately owned native PostgreSQL ordinary-startup smoke.</param>
/// <param name="Mode">Candidate correctness or independently labeled public-feed replay; public receipts never authorize candidate publication.</param>
internal sealed record DurableTemplateConsumerProofRequest(
    string RepositoryRoot, string ArtifactsDirectory, string PackageVersion, string ReceiptPath,
    string ThirdPartySource = "https://api.nuget.org/v3/index.json", string? SourceCommit = null,
    bool RunFirstWork = true, bool RunNativeSmoke = false, string Mode = "candidate-local-feed-correctness");

/// <summary>Safe artifact identity; contains no consumer credentials, Work identity or arbitrary command output.</summary>
internal sealed record DurableTemplateProofArtifact(string PackageId, string Version, string Sha512);

/// <summary>A bounded phase observation; command arguments and captured output are never retained in receipts.</summary>
internal sealed record DurableTemplateProofPhase(string Id, double ElapsedSeconds, int ExitCode, bool Truncated);

/// <summary>The version-one local receipt, emitted successfully only after assertions and owned cleanup finish.</summary>
/// <remarks>Local receipts reuse trusted workflow provenance; this format asserts no portable authenticity.
/// RunnerImage contains the hosted runner's ImageOS/ImageVersion identity, or is empty for a local diagnostic
/// without those observations. An empty identity cannot authorize publication.</remarks>
internal sealed record DurableTemplateProofReceipt(
    int SchemaVersion, string SourceCommit, string PackageVersion, string RuntimeIdentifier, string SdkVersion,
    string Image, string Mode, IReadOnlyList<DurableTemplateProofArtifact> Artifacts,
    IReadOnlyList<DurableTemplateProofPhase> Phases, bool ExactArchiveInstall, bool FeedInstall, bool AuthoredSource,
    bool NativeSmoke, bool AuthorizedActivation, bool TerminalWork, bool ReadinessTransition, bool ExportedActivity,
    bool CleanupComplete, bool Succeeded, string FailureCode, string FailurePhase = "",
    string GeneratedContentSha256 = "", NativePostgreSqlToolIdentity? NativeTools = null,
    bool SampleReplacement = false, string RunnerImage = "");

/// <summary>Verifies the installed artifact in fresh CLI/cache/output roots and emits bounded safe evidence.</summary>
/// <remarks>
/// A first generated acquisition copy stages the third-party closure into a local feed. Final authored/archive/feed
/// consumers then restore only from that feed with empty independent caches. The acquisition copy cannot satisfy
/// either install proof. This workflow owns its temporary roots and never deletes parent/user cache directories.
/// </remarks>
internal sealed class DurableTemplateConsumerProof(IExternalCommandRunner commandRunner)
{
    /// <summary>Projects bounded hosted-runner OS/image revision labels. Missing or unsafe observations
    /// produce an empty diagnostic identity rather than an invented runner revision.</summary>
    internal static string ReadRunnerImage(string? imageOs, string? imageVersion)
        => Regex.IsMatch(imageOs ?? string.Empty, @"\A[A-Za-z0-9._-]{1,63}\z", RegexOptions.CultureInvariant)
            && Regex.IsMatch(imageVersion ?? string.Empty, @"\A[A-Za-z0-9._-]{1,63}\z", RegexOptions.CultureInvariant)
                ? imageOs + "/" + imageVersion : string.Empty;

    /// <summary>The pinned first-Work image; pack validation must compare it with the provider release input.</summary>
    internal const string PostgreSqlImage = "postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877";
    private const string ShortName = "appsurface-durable-worker";
    private const string ProjectName = "FirstDurableWorker";
    private const string ProviderId = "ForgeTrust.AppSurface.Durable.PostgreSql";
    private static readonly string[] Checkpoints =
    [
        "[first-work] authorized activation accepted", "[first-work] Work reached terminal completion",
        "[first-work] readiness: NotStarted -> Healthy", "[first-work] exported appsurface.durable.runtime.activation"
    ];

    /// <summary>Runs acquisition, independent authored/installed proofs and cleanup; failed evidence never certifies publication.</summary>
    /// <param name="request">Exact candidate and safe receipt destination.</param>
    /// <param name="artifacts">Validated candidate rows, whose archive identity is checked again.</param>
    /// <param name="nativeSmoke">Optional separately owned native smoke callback; required when requested.</param>
    /// <param name="cancellationToken">Cancels work; independent bounded cleanup is still attempted.</param>
    /// <param name="nativeToolIdentity">Optional safe native tool observation, collected only after native smoke finishes.</param>
    /// <returns>Successful or failed safe receipt. Callers must require Succeeded before advancing release.</returns>
    internal async Task<DurableTemplateProofReceipt> RunAsync(
        DurableTemplateConsumerProofRequest request, IReadOnlyList<PackageArtifactValidationReportEntry> artifacts,
        Func<string, IReadOnlyDictionary<string, string?>, CancellationToken, Task>? nativeSmoke = null,
        CancellationToken cancellationToken = default,
        Func<NativePostgreSqlToolIdentity?>? nativeToolIdentity = null)
    {
        PackageVersionValidator.Require(request.PackageVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        DurableTemplateStaging.RequireRegularPath(request.ReceiptPath);
        var template = RequireArtifact(artifacts, DurableTemplateStaging.PackageId, request.PackageVersion);
        var provider = RequireArtifact(artifacts, ProviderId, request.PackageVersion);
        DurableTemplateArtifactContract.ValidateArchive(template.ArtifactPath, request.PackageVersion,
            allowSigningEnvelope: request.Mode == "promoted-public-feed-replay");
        var identities = artifacts.OrderBy(a => a.PackageId, StringComparer.Ordinal).Select(a =>
            new DurableTemplateProofArtifact(a.PackageId, request.PackageVersion, PackageHash.ComputeSha512(a.ArtifactPath))).ToArray();
        if (request.SourceCommit is not null) RequireSource(request.SourceCommit);
        var source = request.SourceCommit ?? string.Empty;
        var sdk = string.Empty;
        var phases = new List<DurableTemplateProofPhase>();
        var root = CanonicalTemporaryRoot();
        Directory.CreateDirectory(root);
        var feed = Path.Join(root, "feed");
        Directory.CreateDirectory(feed);
        var succeeded = false;
        var failure = string.Empty;
        var failurePhase = string.Empty;
        var currentPhase = "prepare";
        var contentHash = string.Empty;
        var cleanup = false;
        var pathInstalled = false;
        var feedInstalled = false;
        var authored = false;
        var native = false;
        var replacementVerified = false;
        var checkpointFlags = new bool[4];
        var inCleanup = false;
        var installed = new List<(string Directory, Dictionary<string, string?> Environment)>();
        try
        {
            var toolEnvironment = EnvironmentFor(root, "tools");
            var sdkOutput = (await Run("sdk", ["--version"], root, toolEnvironment, 5_000)).StandardOutput.Trim();
            if (!Regex.IsMatch(sdkOutput, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?\z", RegexOptions.CultureInvariant))
                throw new PackageIndexException("SDK identity is invalid.");
            sdk = sdkOutput;
            currentPhase = "source-revision";
            var sourceResult = await commandRunner.RunAsync(new ExternalCommandRequest("git", ["rev-parse", "HEAD"], request.RepositoryRoot,
                "source-revision", "reading source revision", 5_000, CapturePolicy: ExternalCapturePolicy.ReleaseProof), cancellationToken);
            RequireResult(sourceResult);
            var actualSource = sourceResult.StandardOutput.Trim();
            if (source.Length != 0 && source != actualSource)
                throw new PackageIndexException("Template proof source revision differs from the checked-out source.");
            RequireSource(actualSource);
            source = actualSource;
            foreach (var artifact in artifacts)
            {
                DurableTemplateStaging.RequireRegularPath(artifact.ArtifactPath);
                if (!File.Exists(artifact.ArtifactPath)) throw new PackageIndexException("Template closure is missing a candidate archive.");
                File.Copy(artifact.ArtifactPath, Path.Join(feed, Path.GetFileName(artifact.ArtifactPath)), overwrite: false);
            }
            var acquisition = Path.Join(root, "acquisition");
            Directory.CreateDirectory(acquisition);
            var acquisitionEnvironment = EnvironmentFor(root, "acquisition");
            await InstallArchive(acquisition, acquisitionEnvironment);
            await Run("acquisition-create", ["new", ShortName, "-n", ProjectName], acquisition, acquisitionEnvironment, 180_000);
            var acquisitionProject = Path.Join(acquisition, ProjectName);
            var acquisitionConfig = Path.Join(acquisition, "NuGet.config");
            File.WriteAllText(acquisitionConfig, MappedConfiguration(feed, request.ThirdPartySource));
            await Run("acquisition-restore", ["restore", acquisitionProject, "--configfile", acquisitionConfig, "--force-evaluate"], acquisition, acquisitionEnvironment, 300_000);
            StageRestoredArchives(acquisitionEnvironment["NUGET_PACKAGES"]!, feed);
            await Uninstall(acquisition, acquisitionEnvironment);
            installed.RemoveAll(x => x.Directory == acquisition);

            var localConfig = Path.Join(root, "NuGet.config");
            File.WriteAllText(localConfig, LocalConfiguration(feed));
            var authoredRoot = Path.Join(root, "authored");
            DurableTemplateStaging.Stage(Path.Join(request.RepositoryRoot, DurableTemplateStaging.ContentPath), authoredRoot, request.PackageVersion);
            var authoredEnvironment = EnvironmentFor(root, "authored");
            await VerifyConsumer(authoredRoot, authoredEnvironment, "authored", localConfig, "AppSurfaceDurableWorker");
            authored = true;

            var archiveRoot = Path.Join(root, "archive-install");
            Directory.CreateDirectory(archiveRoot);
            var archiveEnvironment = EnvironmentFor(root, "archive-install");
            await InstallArchive(archiveRoot, archiveEnvironment);
            await VerifyInstall(archiveRoot, archiveEnvironment, "archive", localConfig);
            pathInstalled = true;
            await Uninstall(archiveRoot, archiveEnvironment);
            installed.RemoveAll(x => x.Directory == archiveRoot);

            var feedRoot = Path.Join(root, "feed-install");
            Directory.CreateDirectory(feedRoot);
            var feedEnvironment = EnvironmentFor(root, "feed-install");
            installed.Add((feedRoot, feedEnvironment));
            await Run("feed-install", ["new", "install", $"{DurableTemplateStaging.PackageId}@{request.PackageVersion}", "--nuget-source", feed, "--force"], feedRoot, feedEnvironment, 180_000);
            await VerifyInstall(feedRoot, feedEnvironment, "feed", localConfig);
            feedInstalled = true;
            var generatedRoot = Path.Join(feedRoot, ProjectName);
            contentHash = HashGeneratedContent(generatedRoot);
            if (contentHash != DurableTemplateArtifactContract.ComputeGeneratedContentSha256(template.ArtifactPath, ProjectName,
                    allowSigningEnvelope: request.Mode == "promoted-public-feed-replay"))
                throw new PackageIndexException("Native generated content differs from the exact template candidate payload.");
            if (request.RunFirstWork)
            {
                var result = await Run("first-work", ["test", generatedRoot, "--no-restore", "--filter", "FullyQualifiedName~FirstDurableWork", "--logger", "console;verbosity=detailed"], feedRoot, feedEnvironment, 180_000);
                for (var i = 0; i < Checkpoints.Length; i++)
                {
                    checkpointFlags[i] = result.StandardOutput.Contains(Checkpoints[i], StringComparison.Ordinal);
                    if (!checkpointFlags[i]) throw new PackageIndexException("A required first-Work assertion checkpoint is missing.");
                }
            }
            if (request.RunNativeSmoke)
            {
                if (nativeSmoke is null) throw new PackageIndexException("Native smoke owner is required.");
                currentPhase = "native-smoke";
                await nativeSmoke(generatedRoot, feedEnvironment, cancellationToken);
                native = true;
            }
            // The replacement is a separate source tree and database. Reuse only this proof's owned candidate cache.
            var replacementRoot = Path.Join(root, "replacement", ProjectName);
            DurableTemplateStaging.Stage(generatedRoot, replacementRoot, request.PackageVersion);
            DurableTemplateSampleReplacement.Apply(replacementRoot, ProjectName);
            await Run("replacement-build", ["build", replacementRoot, "-warnaserror"], replacementRoot, feedEnvironment, 300_000);
            VerifyRestoredGraph(replacementRoot, feedEnvironment["NUGET_PACKAGES"]!, feed, request.PackageVersion);
            await Run("replacement-tests", ["test", replacementRoot, "--no-build", "--no-restore", "--filter", "Category!=PostgreSql&Category!=NativePostgreSql"], replacementRoot, feedEnvironment, 180_000);
            if (request.RunFirstWork)
            {
                var replacement = await Run("replacement-first-work", ["test", replacementRoot, "--no-build", "--no-restore", "--filter", "FullyQualifiedName~FirstDurableWork", "--logger", "console;verbosity=detailed"], replacementRoot, feedEnvironment, 180_000);
                if (Checkpoints.Any(checkpoint => !replacement.StandardOutput.Contains(checkpoint, StringComparison.Ordinal)))
                    throw new PackageIndexException("A required replacement first-Work assertion checkpoint is missing.");
            }
            replacementVerified = true;
            VerifyRestoredGraph(generatedRoot, feedEnvironment["NUGET_PACKAGES"]!, feed, request.PackageVersion);
            RequireSqlIdentity(provider.ArtifactPath, generatedRoot);
            await Uninstall(feedRoot, feedEnvironment);
            installed.RemoveAll(x => x.Directory == feedRoot);
            succeeded = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            failure = cancellationToken.IsCancellationRequested ? "TemplateProofCancelled" : "TemplateProofFailed";
            failurePhase = currentPhase;
            // Raw process/provider errors can carry credentials. Receipt records only a stable phase/code and retry link.
            succeeded = false;
        }
        finally
        {
            inCleanup = true;
            using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var (directory, environment) in installed)
            {
                try { await Uninstall(directory, environment, cleanupBudget.Token); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                { succeeded = false; failure = "TemplateCleanupFailed"; }
            }
            try
            {
                RequireCleanupTree(root);
                DurableTemplateStaging.RequireRegularPath(root);
                Directory.Delete(root, recursive: true);
                cleanup = !Directory.Exists(root) && !cleanupBudget.IsCancellationRequested;
                if (!cleanup) { succeeded = false; failure = "TemplateCleanupFailed"; }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PackageIndexException)
            { succeeded = false; failure = "TemplateCleanupFailed"; }
        }
        var receipt = new DurableTemplateProofReceipt(1, source, request.PackageVersion, RuntimeInformation.RuntimeIdentifier, sdk,
            PostgreSqlImage, request.Mode, identities, phases, pathInstalled, feedInstalled, authored, native,
            checkpointFlags[0], checkpointFlags[1], checkpointFlags[2], checkpointFlags[3], cleanup, succeeded && cleanup, failure, failurePhase, contentHash, native ? nativeToolIdentity?.Invoke() : null,
            replacementVerified, ReadRunnerImage(Environment.GetEnvironmentVariable("ImageOS"), Environment.GetEnvironmentVariable("ImageVersion")));
        WriteReceipt(request.ReceiptPath, receipt);
        return receipt;

        async Task<ExternalCommandResult> Run(string id, IReadOnlyList<string> arguments, string directory,
            IReadOnlyDictionary<string, string?> environment, int timeout, CancellationToken? token = null)
        {
            currentPhase = id;
            if (phases.Count >= 32 && !inCleanup) throw new PackageIndexException("Template correctness command-record budget exceeded.");
            var started = Stopwatch.GetTimestamp();
            var result = await commandRunner.RunAsync(new ExternalCommandRequest("dotnet", arguments, directory, id,
                "verifying installed Durable template", inCleanup ? Math.Min(timeout, 20_000) : timeout, environment, ExternalCapturePolicy.ReleaseProof), token ?? cancellationToken);
            if (phases.Count < 32)
                phases.Add(new(id, Stopwatch.GetElapsedTime(started).TotalSeconds, result.ExitCode, result.StandardOutputTruncated || result.StandardErrorTruncated));
            RequireResult(result);
            return result;
        }
        async Task InstallArchive(string directory, Dictionary<string, string?> environment)
        {
            installed.Add((directory, environment));
            await Run("archive-install", ["new", "install", template.ArtifactPath, "--force"], directory, environment, 180_000);
        }
        async Task VerifyInstall(string directory, Dictionary<string, string?> environment, string label, string config)
        {
            var list = await Run(label + "-discover", ["new", "list", ShortName], directory, environment, 180_000);
            if (!list.StandardOutput.Contains(ShortName, StringComparison.Ordinal)) throw new PackageIndexException("Template short name was not discovered.");
            await Run(label + "-create", ["new", ShortName, "-n", ProjectName], directory, environment, 180_000);
            var generated = Path.Join(directory, ProjectName);
            DurableTemplateArtifactContract.ValidateGenerated(generated, ProjectName, request.PackageVersion);
            await VerifyConsumer(generated, environment, label, config, ProjectName);
        }
        async Task VerifyConsumer(string directory, Dictionary<string, string?> environment, string label, string config, string name)
        {
            DurableTemplateArtifactContract.ValidateProjectVersions(directory, request.PackageVersion);
            await Run(label + "-restore", ["restore", directory, "--configfile", config, "--force-evaluate"], directory, environment, 300_000);
            VerifyRestoredGraph(directory, environment["NUGET_PACKAGES"]!, feed, request.PackageVersion);
            await Run(label + "-build", ["build", directory, "--no-restore", "-warnaserror"], directory, environment, 300_000);
            await Run(label + "-format", ["format", Path.Join(directory, name + ".slnx"), "--no-restore", "--verify-no-changes"], directory, environment, 180_000);
            await Run(label + "-tests", ["test", directory, "--no-build", "--no-restore", "--filter", "Category!=PostgreSql&Category!=NativePostgreSql"], directory, environment, 180_000);
        }
        async Task Uninstall(string directory, Dictionary<string, string?> environment, CancellationToken? token = null)
        {
            await Run("uninstall", ["new", "uninstall", DurableTemplateStaging.PackageId], directory, environment, 180_000, token);
            var absent = await Run("absence", ["new", "list"], directory, environment, 180_000, token);
            if (absent.StandardOutput.Contains(ShortName, StringComparison.Ordinal)) throw new PackageIndexException("Candidate short name remained after uninstall.");
        }
    }

    /// <summary>Rejects nonzero, launch/timeout and truncated results before interpreting structured evidence.</summary>
    internal static void RequireResult(ExternalCommandResult result)
    {
        if (result.ExitCode != 0 || result.StandardOutputTruncated || result.StandardErrorTruncated)
            throw new PackageIndexException("Template command failed or its bounded capture was truncated; retry the named proof phase.");
    }

    /// <summary>Requires one exact candidate archive, without reading secrets from its content.</summary>
    internal static PackageArtifactValidationReportEntry RequireArtifact(IReadOnlyList<PackageArtifactValidationReportEntry> rows, string id, string version)
    {
        var matching = rows.Where(row => row.PackageId.Equals(id, StringComparison.Ordinal)).ToArray();
        if (matching.Length != 1 || !File.Exists(matching[0].ArtifactPath)
            || !Path.GetFileName(matching[0].ArtifactPath).Equals($"{id}.{version}.nupkg", StringComparison.Ordinal))
            throw new PackageIndexException("Template proof requires exactly one matching candidate artifact per subject.");
        return matching[0];
    }

    /// <summary>Checks every restored first-party archive against the exact local candidate and forbids project substitution.</summary>
    internal static void VerifyRestoredGraph(string root, string cache, string feed, string version)
    {
        var assetFiles = Directory.EnumerateFiles(root, "project.assets.json", SearchOption.AllDirectories).ToArray();
        if (assetFiles.Length != 2) throw new PackageIndexException("Standalone proof must restore exactly the host and test graphs.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in assetFiles)
        {
            DurableTemplateStaging.RequireRegularPath(path);
            if (new FileInfo(path).Length > 1024 * 1024) throw new PackageIndexException("Restored graph exceeds its one-MiB bound.");
            using var json = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 });
            var graph = json.RootElement.GetProperty("libraries");
            foreach (var library in graph.EnumerateObject())
            {
                var split = library.Name.Split('/');
                if (split.Length != 2) throw new PackageIndexException("Restored graph has malformed identity.");
                var type = library.Value.GetProperty("type").GetString();
                if (type == "project")
                {
                    if (split[0] != "FirstDurableWorker" && split[0] != "AppSurfaceDurableWorker")
                        throw new PackageIndexException("Restored graph contains a source-project substitute.");
                    continue;
                }
                if (!split[0].StartsWith("ForgeTrust.", StringComparison.OrdinalIgnoreCase)) continue;
                if (type != "package" || split[1] != version) throw new PackageIndexException("Restored first-party version is not the candidate.");
                var candidate = Path.Join(feed, $"{split[0]}.{version}.nupkg");
                var restored = Path.Join(cache, split[0].ToLowerInvariant(), version.ToLowerInvariant(), $"{split[0].ToLowerInvariant()}.{version.ToLowerInvariant()}.nupkg");
                if (!File.Exists(candidate) || !File.Exists(restored) || PackageHash.ComputeSha512(candidate) != PackageHash.ComputeSha512(restored))
                    throw new PackageIndexException("Restored archive differs from the exact first-party candidate.");
                seen.Add(split[0]);
            }
        }
        if (!seen.Contains(ProviderId) || !seen.Contains("ForgeTrust.AppSurface.Durable.Testing"))
            throw new PackageIndexException("Restored graph is missing the provider or deterministic-testing candidate.");
    }

    /// <summary>Requires canonical provider SQL bytes in each generated test output, never a template-owned recipe.</summary>
    internal static void RequireSqlIdentity(string providerArchive, string generatedRoot)
    {
        using var archive = ZipFile.OpenRead(providerArchive);
        var entry = archive.GetEntry("contentFiles/any/any/configure-postgresql-roles.sql")
            ?? throw new PackageIndexException("Provider recipe asset is missing.");
        if (entry.Length > 1024 * 1024) throw new PackageIndexException("Provider recipe exceeds its one-MiB bound.");
        using var input = entry.Open();
        var expected = SHA256.HashData(input);
        var outputs = Directory.EnumerateFiles(Path.Join(generatedRoot, "tests"), "configure-postgresql-roles.sql", SearchOption.AllDirectories)
            .Where(path => path.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal)).ToArray();
        if (outputs.Length == 0) throw new PackageIndexException("Direct generated consumer did not receive the provider recipe.");
        foreach (var path in outputs)
        {
            DurableTemplateStaging.RequireRegularPath(path);
            if (new FileInfo(path).Length > 1024 * 1024) throw new PackageIndexException("Consumer recipe exceeds its one-MiB bound.");
            using var stream = File.OpenRead(path);
            if (!SHA256.HashData(stream).AsSpan().SequenceEqual(expected)) throw new PackageIndexException("Provider recipe changed between archive and consumer output.");
        }
    }

    /// <summary>Writes only the safe receipt model, capped at 64KiB, after proof cleanup.</summary>
    internal static void WriteReceipt(string path, DurableTemplateProofReceipt receipt)
    {
        DurableTemplateStaging.RequireRegularPath(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, PackageArtifactJson.Options);
        if (bytes.Length > 64 * 1024) throw new PackageIndexException("Template safe receipt exceeds its retention bound.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Hashes the reviewed generated inventory in deterministic path/byte order, excluding build output.</summary>
    internal static string HashGeneratedContent(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in DurableTemplateStaging.EnumerateContent(root))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/') + "\0"));
            hash.AppendData(File.ReadAllBytes(file));
            hash.AppendData([0]);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void RequireCleanupTree(string root)
    {
        DurableTemplateStaging.RequireRegularPath(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            DurableTemplateStaging.RequireRegularPath(path);
            if (Directory.Exists(path)) RequireCleanupTree(path);
        }
    }

    private static void RequireSource(string source)
    {
        if (source.Length != 40 || !source.All(Uri.IsHexDigit)) throw new PackageIndexException("Template receipt requires a full source revision.");
    }
    private static string CanonicalTemporaryRoot()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/var/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/tmp/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        return Path.Join(temporary, "appsurface-durable-template-" + Guid.NewGuid().ToString("N"));
    }
    /// <summary>Creates owned CLI/cache roots and removes inherited credentials from a consumer child.</summary>
    /// <param name="root">Unique proof-owned root, already validated before mutation.</param>
    /// <param name="label">Fixed workflow phase label; callers never use arbitrary user input here.</param>
    /// <returns>Per-child overrides; the parent process environment is never changed.</returns>
    internal static Dictionary<string, string?> EnvironmentFor(string root, string label)
    {
        var cache = Path.Join(root, label + "-cache");
        var home = Path.Join(root, label + "-cli");
        Directory.CreateDirectory(cache); Directory.CreateDirectory(home);
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_CLI_HOME"] = home,
            ["NUGET_PACKAGES"] = cache,
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            // Proof children must terminate with their owned workspace, rather than leaving reusable build servers.
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["UseSharedCompilation"] = "false",
            ["MSBuildSDKsPath"] = null,
            ["MSBUILD_EXE_PATH"] = null,
            ["NuGetPackageRoot"] = null,
            ["PGPASSWORD"] = null,
            ["PGDATA"] = null,
            ["PGHOST"] = null,
            ["PGUSER"] = null,
            ["PGSERVICE"] = null,
            ["PGSERVICEFILE"] = null,
            ["PGPASSFILE"] = null,
            ["APPSURFACE_TEMPLATE_NATIVE_ADMIN_CONNECTION"] = null,
            ["APPSURFACE_TEMPLATE_NATIVE_PG_BIN"] = null,
            ["APPSURFACE_TEMPLATE_ACTIVATION_TOKEN"] = null
        };
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name) continue;
            if (name.StartsWith("PG", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("APPSURFACE_", StringComparison.OrdinalIgnoreCase)
                || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                || name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("API_KEY", StringComparison.OrdinalIgnoreCase))
                environment[name] = null;
        }
        return environment;
    }
    private static string LocalConfiguration(string feed) => new XDocument(new XElement("configuration",
        new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", feed))),
        new XElement("fallbackPackageFolders", new XElement("clear")))).ToString();
    private static string MappedConfiguration(string feed, string source) => new XDocument(new XElement("configuration",
        new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "candidate"), new XAttribute("value", feed)),
            new XElement("add", new XAttribute("key", "third-party"), new XAttribute("value", source))),
        new XElement("packageSourceMapping", new XElement("packageSource", new XAttribute("key", "candidate"), new XElement("package", new XAttribute("pattern", "ForgeTrust.*"))),
            new XElement("packageSource", new XAttribute("key", "third-party"), new XElement("package", new XAttribute("pattern", "*")))),
        new XElement("fallbackPackageFolders", new XElement("clear")))).ToString();
    private static void StageRestoredArchives(string cache, string feed)
    {
        foreach (var archive in Directory.EnumerateFiles(cache, "*.nupkg", SearchOption.AllDirectories))
        {
            DurableTemplateStaging.RequireRegularPath(archive);
            if (Path.GetFileName(archive).StartsWith("forgetrust.", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(archive, Path.Join(feed, Path.GetFileName(archive)), overwrite: false);
        }
    }
}
