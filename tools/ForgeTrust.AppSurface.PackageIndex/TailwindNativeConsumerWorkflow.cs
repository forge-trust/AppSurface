using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>
/// Validates the complete coordinated producer package plan and artifact payloads before native consumer restore.
/// The injectable form exists only to let focused native-workflow tests supply a small producer fixture; ordinary
/// callers use the full repository-backed validation implemented by <see cref="TailwindNativeConsumerWorkflow"/>.
/// </summary>
/// <param name="repositoryRoot">Clean, source-stamped repository checkout.</param>
/// <param name="artifactsInputPath">Producer bundle directory containing every coordinated package.</param>
/// <param name="manifest">Parsed producer artifact manifest.</param>
/// <param name="producerClosure">First-party package closure already bound by the producer subject.</param>
/// <param name="cancellationToken">Cancellation token for validation.</param>
internal delegate Task TailwindNativeProducerArtifactValidator(
    string repositoryRoot,
    string artifactsInputPath,
    PackageArtifactManifest manifest,
    IReadOnlyList<TailwindSubjectPackage> producerClosure,
    CancellationToken cancellationToken);

/// <summary>Produces release-bound native consumer evidence from the actual NuGet cache restore.</summary>
internal static class TailwindNativeConsumerWorkflow
{
    private const string TailwindId = "ForgeTrust.AppSurface.Web.Tailwind";
    private const int CommandTimeout = 300_000;
    private static readonly string[] Rids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"];

    internal static Task<TailwindEvidenceCommandResult> RunAsync(
        string repositoryRoot, string artifactsInputPath, string artifactManifestPath,
        TailwindCommandOptions options, CancellationToken cancellationToken)
        => RunAsync(repositoryRoot, artifactsInputPath, artifactManifestPath, options,
            new TailwindBoundedCommandRunner(new CliWrapCommandRunner()), cancellationToken);

    /// <summary>
    /// Runs the native proof with an injectable runner and producer validator for focused tests. Production uses the
    /// source-stamped <see cref="TailwindConsumerLock.RelativeTemplatePath"/>; a test may supply a fixture template
    /// for its deliberately smaller producer closure.
    /// </summary>
    internal static async Task<TailwindEvidenceCommandResult> RunAsync(
        string repositoryRoot, string artifactsInputPath, string artifactManifestPath,
        TailwindCommandOptions options, ICommandRunner runner, CancellationToken cancellationToken,
        TailwindNativeProducerArtifactValidator? producerArtifactValidator = null,
        string? consumerLockTemplatePath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        var mode = TailwindCommandOptions.Require(options.Mode, "--mode");
        var work = Path.GetFullPath(TailwindCommandOptions.Require(options.WorkDirectory, "--work-directory"));
        var report = Path.GetFullPath(TailwindCommandOptions.Require(options.ReportDirectory, "--report-directory"));
        var receiptPath = Path.Combine(report, "tailwind-native-host-proof.json");
        var completed = new List<string>();
        string? failedStage = null;
        try
        {
            if (mode == "local")
                return await RunLocalAsync(repositoryRoot, artifactsInputPath, artifactManifestPath, work, report, runner, cancellationToken);
            if (mode != "release") throw new PackageIndexException("verify-tailwind-consumer --mode must be local or release.");
            RequireFresh(work, "native consumer work");
            RequireFresh(report, "native consumer report");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(report);

            failedStage = "producer-binding";
            var binding = await TailwindEvidenceWorkflow.ValidateProducerBindingAsync(repositoryRoot, artifactsInputPath, artifactManifestPath, options, cancellationToken);
            var expectedRid = TailwindCommandOptions.Require(options.ExpectedRid, "--expected-rid");
            if (!Rids.Contains(expectedRid, StringComparer.Ordinal)) throw new PackageIndexException($"Unsupported expected RID '{expectedRid}'.");
            var observed = ObserveHost();
            if (!string.Equals(expectedRid, observed.Rid, StringComparison.Ordinal))
                throw new PackageIndexException($"Native host mismatch: expected '{expectedRid}', observed '{observed.Rid ?? "unsupported"}' ({observed.Os}/{observed.ProcessArchitecture}).");
            var producerManifest = await new PackageArtifactManifestReader().ReadAsync(artifactManifestPath, cancellationToken);
            await (producerArtifactValidator ?? ValidateProducerArtifactsAsync)(repositoryRoot, artifactsInputPath,
                producerManifest, binding.Subject.FirstPartyPackages, cancellationToken);

            failedStage = "private-cache-setup";
            var consumer = Path.Combine(work, "consumer");
            var packageCache = Path.Combine(work, "nuget-packages");
            var httpCache = Path.Combine(work, "nuget-http-cache");
            var cliHome = Path.Combine(work, "dotnet-home");
            var tailwindCache = Path.Combine(work, "tailwind-cache");
            Directory.CreateDirectory(consumer);
            Directory.CreateDirectory(Path.Combine(consumer, "wwwroot", "css"));
            Directory.CreateDirectory(tailwindCache);
            WriteConsumerFiles(consumer, artifactsInputPath, producerManifest.PackageVersion, tailwindCache);
            failedStage = "consumer-lock-preparation";
            var lockPath = Path.Combine(consumer, "packages.lock.json");
            var templatePath = consumerLockTemplatePath ?? Path.Combine(repositoryRoot, TailwindConsumerLock.RelativeTemplatePath);
            var expectedLock = TailwindConsumerLock.Materialize(templatePath, lockPath, binding.Subject.FirstPartyPackages);
            var environment = new Dictionary<string, string?>
            {
                ["NUGET_PACKAGES"] = packageCache,
                ["NUGET_HTTP_CACHE_PATH"] = httpCache,
                ["DOTNET_CLI_HOME"] = cliHome,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["CI"] = "true"
            };
            var project = Path.Combine(consumer, "Tailwind.PackageConsumerProof.csproj");
            failedStage = "sdk-version";
            var sdk = await runner.RunAsync(new CommandRunRequest("dotnet", ["--version"], consumer,
                "dotnet --version", "Tailwind native consumer", "sdk-version", "reading the host .NET SDK version",
                CommandTimeout, environment), cancellationToken);
            var sdkVersion = sdk.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(sdkVersion)) throw new PackageIndexException("Native host .NET SDK version is empty.");
            failedStage = "initial-restore";
            await RunCommand(runner, "restore", [project, "--configfile", Path.Combine(consumer, "NuGet.config"), "--locked-mode"], consumer, environment, cancellationToken);
            TailwindConsumerLock.RequireUnchanged(lockPath, expectedLock);
            completed.Add("restore");
            failedStage = "locked-restore";
            await RunCommand(runner, "locked restore", [project, "--configfile", Path.Combine(consumer, "NuGet.config"), "--locked-mode"], consumer, environment, cancellationToken);
            TailwindConsumerLock.RequireUnchanged(lockPath, expectedLock);
            completed.Add("locked-restore");

            failedStage = "restored-graph-and-payload";
            var assetsPath = Path.Combine(consumer, "obj", "project.assets.json");
            var closure = TailwindProofSubjectService.ReadResolvedClosure(assetsPath, producerManifest);
            RequireSameClosure(binding.Subject.FirstPartyPackages, closure);
            var assetsBytes = await ReadBoundedAsync(assetsPath, cancellationToken);
            using var assets = JsonDocument.Parse(assetsBytes, new JsonDocumentOptions { MaxDepth = 64 });
            var target = assets.RootElement.GetProperty("targets").EnumerateObject().Single().Value;
            var packageEvidence = new List<object>();
            var beforePayload = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string? binaryName = null;
            string? binaryHash = null;
            string? internalManifestHash = null;
            string? expectedCacheBinary = null;
            foreach (var expected in binding.Subject.FirstPartyPackages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nodeKey = target.EnumerateObject().Select(item => item.Name).Single(name => name.StartsWith(expected.PackageId + "/", StringComparison.OrdinalIgnoreCase));
                var node = target.GetProperty(nodeKey);
                var libraries = assets.RootElement.GetProperty("libraries");
                var library = libraries.GetProperty(nodeKey);
                if (library.GetProperty("type").GetString() != "package") throw new PackageIndexException($"Resolved first-party dependency '{expected.PackageId}' is not a NuGet package.");
                var relativePackagePath = library.GetProperty("path").GetString() ?? throw new PackageIndexException($"Assets graph has no package path for '{expected.PackageId}'.");
                var packageDirectory = ResolvePackageDirectory(assets.RootElement, packageCache, relativePackagePath);
                var expectedArchiveName = expected.ArtifactFileName;
                var cachedArchives = Directory.EnumerateFiles(packageDirectory, "*.nupkg", SearchOption.TopDirectoryOnly).ToArray();
                if (cachedArchives.Length != 1 || !string.Equals(Path.GetFileName(cachedArchives[0]), expectedArchiveName, StringComparison.OrdinalIgnoreCase))
                    throw new PackageIndexException($"Private NuGet cache does not contain exactly the expected archive '{expectedArchiveName}' for '{expected.PackageId}'.");
                var restoredArchive = cachedArchives[0];
                var restoredHash = await PackageHash.ComputeSha512Async(restoredArchive, cancellationToken);
                if (!string.Equals(restoredHash, expected.PackageSha512, StringComparison.Ordinal)) throw new PackageIndexException($"Actual restored archive SHA-512 for '{expected.PackageId}' differs from producer bytes.");
                var payload = TailwindPayloadProjection.Verify(restoredArchive, packageDirectory, node, cancellationToken);
                beforePayload.Add(expected.PackageId, payload);
                var archiveEvidenceRelative = $"packages/{expected.PackageId}.nupkg";
                CopyRegularFile(restoredArchive, Path.Combine(report, archiveEvidenceRelative));
                var payloadEvidence = new List<object>();
                foreach (var (relative, sha256) in payload.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    var source = ResolveConfined(packageDirectory, relative);
                    var evidenceRelative = $"payload/{expected.PackageId}/{relative}";
                    CopyRegularFile(source, Path.Combine(report, evidenceRelative));
                    payloadEvidence.Add(new { packageRelativePath = relative, evidencePath = evidenceRelative, sha256 });
                    if (expected.PackageId.Equals(TailwindId, StringComparison.OrdinalIgnoreCase) && relative.Equals("build/tailwind.release.json", StringComparison.OrdinalIgnoreCase))
                        internalManifestHash = sha256;
                }
                packageEvidence.Add(new
                {
                    packageId = expected.PackageId,
                    packageVersion = expected.PackageVersion,
                    producerSha512 = expected.PackageSha512,
                    restoredSha512 = restoredHash,
                    archivePath = archiveEvidenceRelative,
                    payloadVerified = true,
                    payloadFiles = payloadEvidence
                });
                if (expected.PackageId.Equals(TailwindId, StringComparison.OrdinalIgnoreCase))
                {
                    (binaryName, var expectedBinaryHash, var tailwindVersion) = ReadRidAsset(restoredArchive, expectedRid);
                    expectedCacheBinary = Path.Combine(tailwindCache, $"tailwind-{tailwindVersion}", expectedRid, binaryName);
                    // The target runs this cache acquisition during build. The trusted manifest hash is checked afterward.
                    binaryHash = expectedBinaryHash;
                }
            }
            if (string.IsNullOrWhiteSpace(binaryName) || string.IsNullOrWhiteSpace(binaryHash) || string.IsNullOrWhiteSpace(internalManifestHash))
                throw new PackageIndexException("Restored Tailwind package did not yield the expected host binary and internal manifest evidence.");
            CopyRegularFile(assetsPath, Path.Combine(report, "consumer", "project.assets.json"));
            CopyRegularFile(lockPath, Path.Combine(report, "consumer", "packages.lock.json"));

            failedStage = "native-build";
            await File.WriteAllTextAsync(Path.Combine(consumer, "wwwroot", "css", "app.css"), "@import \"tailwindcss\";\n", cancellationToken);
            await RunCommand(runner, "build", [project, "--configuration", "Release", "--no-restore"], consumer, environment, cancellationToken);
            completed.Add("build");
            var generatedCss = Path.Combine(consumer, "wwwroot", "css", "site.gen.css");
            if (!File.Exists(generatedCss) || new FileInfo(generatedCss).Length == 0) throw new PackageIndexException("Tailwind consumer build did not generate fresh nonempty wwwroot/css/site.gen.css.");
            if (expectedCacheBinary is null || !File.Exists(expectedCacheBinary))
                throw new PackageIndexException($"Tailwind build did not acquire the expected host cache binary '{binaryName}'.");
            RejectLinkAncestors(expectedCacheBinary, tailwindCache);
            var actualBinaryHash = HashFile(expectedCacheBinary);
            if (!string.Equals(actualBinaryHash, binaryHash, StringComparison.Ordinal)) throw new PackageIndexException("Acquired Tailwind host cache binary SHA-256 differs from the internal release manifest.");
            var postBuildAssets = await ReadBoundedAsync(assetsPath, cancellationToken);
            if (!assetsBytes.AsSpan().SequenceEqual(postBuildAssets))
                throw new PackageIndexException("Consumer assets graph changed during the no-restore build.");
            var assetsText = Encoding.UTF8.GetString(postBuildAssets);
            if (assetsText.Contains("ForgeTrust.AppSurface.Web.Tailwind.Runtime.", StringComparison.OrdinalIgnoreCase)) throw new PackageIndexException("Consumer assets graph contains a Tailwind runtime companion dependency.");
            var outputRoots = new[] { Path.Combine(consumer, "bin"), Path.Combine(consumer, "obj") };
            if (outputRoots.Any(root => !Directory.Exists(root))) throw new PackageIndexException("Tailwind consumer build did not produce both bin and obj directories.");
            if (outputRoots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)).Any(path => Path.GetFileName(path).StartsWith("tailwindcss-", StringComparison.OrdinalIgnoreCase)))
                throw new PackageIndexException("Tailwind native executable was copied into consumer build output.");

            failedStage = "post-build-revalidation";
            foreach (var expected in binding.Subject.FirstPartyPackages)
            {
                var nodeKey = target.EnumerateObject().Select(item => item.Name).Single(name => name.StartsWith(expected.PackageId + "/", StringComparison.OrdinalIgnoreCase));
                var packagePath = ResolvePackageDirectory(assets.RootElement, packageCache, assets.RootElement.GetProperty("libraries").GetProperty(nodeKey).GetProperty("path").GetString()!);
                var archivePaths = Directory.EnumerateFiles(packagePath, "*.nupkg", SearchOption.TopDirectoryOnly).ToArray();
                if (archivePaths.Length != 1 || !string.Equals(Path.GetFileName(archivePaths[0]), expected.ArtifactFileName, StringComparison.OrdinalIgnoreCase))
                    throw new PackageIndexException($"Restored cache archive for '{expected.PackageId}' changed its expected basename during build.");
                var archivePath = archivePaths[0];
                if (!string.Equals(await PackageHash.ComputeSha512Async(archivePath, cancellationToken), expected.PackageSha512, StringComparison.Ordinal)) throw new PackageIndexException($"Restored archive '{expected.PackageId}' changed during build.");
                var after = TailwindPayloadProjection.Verify(archivePath, packagePath, target.GetProperty(nodeKey), cancellationToken);
                if (!DictionaryEqual(beforePayload[expected.PackageId], after)) throw new PackageIndexException($"Protected extracted payload '{expected.PackageId}' changed during build.");
            }
            var cssRelative = "consumer/wwwroot/css/site.gen.css";
            CopyRegularFile(generatedCss, Path.Combine(report, cssRelative));
            var evidenceFiles = TailwindEvidenceWorkflow.EnumerateInventory(report, "tailwind-native-host-proof.json", "native-consumer-report.md");
            var receipt = new
            {
                schema = "appsurface-tailwind-native-host-proof-v2",
                status = "succeeded",
                repositoryId = binding.Subject.RepositoryId,
                sourceCommit = binding.Subject.SourceCommit,
                producerRunId = binding.Subject.ProducerRunId,
                producerAttempt = binding.Subject.ProducerAttempt,
                producerArtifactId = binding.Request.ProducerArtifactId,
                subjectSha256 = binding.Request.ExpectedSubjectSha256,
                artifactManifestSha256 = binding.Subject.ArtifactManifestSha256,
                packageVersion = binding.Subject.PackageVersion,
                payloadProjectionVersion = 1,
                nativeInvocationId = TailwindCommandOptions.Require(options.NativeInvocationId, "--native-invocation-id"),
                nativeRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") is { Length: > 0 } run ? run : binding.Subject.ProducerRunId,
                nativeAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") is { Length: > 0 } attempt ? attempt : binding.Subject.ProducerAttempt,
                expectedRid,
                observedRid = observed.Rid!,
                hostOs = observed.Os,
                osArchitecture = observed.OsArchitecture,
                processArchitecture = observed.ProcessArchitecture,
                runnerLabel = Environment.GetEnvironmentVariable("RUNNER_NAME") ?? Environment.MachineName,
                sdkVersion,
                firstPartyPackages = packageEvidence,
                tailwindManifestSha256 = binding.Subject.TailwindManifestSha256,
                restoredTailwindManifestSha256 = internalManifestHash,
                binaryName,
                binarySha256 = actualBinaryHash,
                checks = new { generatedCss = true, hostCacheBinary = true, noRuntimeCompanionDependency = true, noNativeConsumerOutput = true, postBuildPayloadUnchanged = true },
                files = evidenceFiles,
                diagnosticPath = "native-consumer-report.md"
            };
            await File.WriteAllTextAsync(Path.Combine(report, "native-consumer-report.md"),
                $"# Tailwind native consumer proof\n\nStatus: **succeeded**\n\nRID: `{expectedRid}`\n\nProducer artifact: `{binding.Request.ProducerArtifactId}`\n\nNative invocation: `{options.NativeInvocationId}`\n\nRestored package archives and protected payloads matched the producer; CSS and host-cache checks passed.\n", cancellationToken);
            await TailwindEvidenceWorkflow.WriteDiagnosticReportAsync(report, "succeeded", "native-consumer", true, [],
                $"Native Tailwind proof passed for `{expectedRid}`.", cancellationToken);
            evidenceFiles = TailwindEvidenceWorkflow.EnumerateInventory(report, "tailwind-native-host-proof.json");
            receipt = receipt with { files = evidenceFiles };
            await TailwindEvidenceWorkflow.WriteAtomicCreateNewJsonAsync(receiptPath, receipt, cancellationToken);
            return new TailwindEvidenceCommandResult(true, "succeeded", receiptPath);
        }
        catch (Exception ex) when (ex is PackageIndexException or IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or OperationCanceledException or KeyNotFoundException or InvalidDataException or ArgumentException)
        {
            await TailwindEvidenceWorkflow.WriteFailureReportBestEffortAsync(report, failedStage ?? "prerequisites", ex, cancellationToken,
                new { failedStage, completedStages = completed, expectedRid = options.ExpectedRid, observedHost = ObserveHost() });
            return new TailwindEvidenceCommandResult(false, ex is OperationCanceledException ? "cancelled" : "failed", receiptPath);
        }
    }

    /// <summary>Applies the complete checked-in package plan and package payload validation used in production.</summary>
    private static async Task ValidateProducerArtifactsAsync(
        string repositoryRoot,
        string artifactsInputPath,
        PackageArtifactManifest manifest,
        IReadOnlyList<TailwindSubjectPackage> producerClosure,
        CancellationToken cancellationToken)
    {
        var plan = await new PackagePublishPlanResolver(new PackageProjectScanner(), new DotNetProjectMetadataProvider(), new PackageManifestLoader())
            .ResolveAsync(repositoryRoot, Path.Combine(repositoryRoot, "packages", "package-index.yml"), cancellationToken);
        var planned = PackageArtifactManifestPlanValidator.Validate(plan, manifest, artifactsInputPath);
        _ = new PackageArtifactValidator().Validate(plan, artifactsInputPath, manifest.PackageVersion, repositoryRoot);
        ValidatePlannedProducerClosure(planned, producerClosure);
    }

    /// <summary>Checks that each bound producer package has the same filename and SHA-512 as the validated package plan.</summary>
    /// <param name="planned">Manifest entries already validated against the checked-in package plan.</param>
    /// <param name="producerClosure">The subset of first-party packages bound by the producer subject.</param>
    /// <exception cref="PackageIndexException">A closure package is missing from the plan or has different bytes or identity.</exception>
    internal static void ValidatePlannedProducerClosure(
        IReadOnlyList<PlannedPackageArtifact> planned,
        IReadOnlyList<TailwindSubjectPackage> producerClosure)
    {
        foreach (var package in producerClosure)
        {
            var match = planned.SingleOrDefault(item => item.ManifestEntry.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase));
            if (match is null || !string.Equals(match.ManifestEntry.ArtifactFileName, package.ArtifactFileName, StringComparison.Ordinal)
                || !string.Equals(match.ManifestEntry.Sha512, package.PackageSha512, StringComparison.Ordinal))
                throw new PackageIndexException($"Producer subject closure package '{package.PackageId}' does not match the validated package plan and artifact manifest.");
        }
    }

    private static async Task<TailwindEvidenceCommandResult> RunLocalAsync(string repositoryRoot, string artifactsDirectory, string manifestPath,
        string work, string report, ICommandRunner runner, CancellationToken token)
    {
        RequireFresh(work, "local consumer work");
        RequireFresh(report, "local consumer report");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(report);
        var manifest = await new PackageArtifactManifestReader().ReadAsync(manifestPath, token);
        var reportPath = Path.Combine(report, "tailwind-package-consumer-proof.md");
        await runner.RunAsync(new CommandRunRequest("bash",
            ["scripts/verify-tailwind-package-consumer.sh", "--artifacts", artifactsDirectory, "--package-version", manifest.PackageVersion,
             "--work-directory", work, "--report-path", reportPath], repositoryRoot,
            "Tailwind packed consumer proof", TailwindId, "verify", "verifying the packed Tailwind consumer", CommandTimeout,
            new Dictionary<string, string?> { ["CI"] = "true", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" }), token);
        var summary = await File.ReadAllTextAsync(reportPath, token);
        var jsonPath = Path.Combine(report, "tailwind-local-proof.json");
        await TailwindEvidenceWorkflow.WriteDiagnosticReportAsync(report, "succeeded", "local-consumer", false, [],
            "Local packed consumer proof passed; this evidence is not release eligible.", token);
        await TailwindEvidenceWorkflow.WriteAtomicCreateNewJsonAsync(jsonPath, new
        {
            schema = "appsurface-tailwind-local-proof-v1",
            status = "succeeded",
            releaseEligible = false,
            packageVersion = manifest.PackageVersion,
            hostRid = ObserveHost().Rid,
            reportPath = Path.GetFileName(reportPath),
            summarySha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(summary))).ToLowerInvariant(),
            elapsedMilliseconds = 0,
            generatedUtc = DateTimeOffset.UtcNow
        }, token);
        return new TailwindEvidenceCommandResult(true, "local-only", jsonPath);
    }

    private static void WriteConsumerFiles(string consumer, string artifactsDirectory, string version, string cache)
    {
        var artifacts = EscapeXml(Path.GetFullPath(artifactsDirectory));
        var cacheXml = EscapeXml(Path.GetFullPath(cache));
        File.WriteAllText(Path.Combine(consumer, "NuGet.config"), $"""<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="local-artifacts" value="{artifacts}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="local-artifacts"><package pattern="ForgeTrust.*"/></packageSource><packageSource key="nuget.org"><package pattern="CliWrap"/><package pattern="Microsoft.Extensions.*"/><package pattern="System.Diagnostics.EventLog"/></packageSource></packageSourceMapping></configuration>""");
        File.WriteAllText(Path.Combine(consumer, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(consumer, "Directory.Build.props"), "<Project><PropertyGroup><RestoreFallbackFolders></RestoreFallbackFolders><RestoreAdditionalProjectSources></RestoreAdditionalProjectSources><RestoreAdditionalProjectFallbackFolders></RestoreAdditionalProjectFallbackFolders></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(consumer, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(consumer, "Tailwind.PackageConsumerProof.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk.Razor\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><TailwindDownloadCacheRoot>{cacheXml}</TailwindDownloadCacheRoot></PropertyGroup><ItemGroup><PackageReference Include=\"{TailwindId}\" Version=\"{version}\" /></ItemGroup></Project>");
    }

    private static async Task RunCommand(ICommandRunner runner, string stage, IReadOnlyList<string> args, string cwd,
        IReadOnlyDictionary<string, string?> environment, CancellationToken token)
        => _ = await runner.RunAsync(new CommandRunRequest("dotnet", args, cwd, $"dotnet {stage}", "Tailwind native consumer",
            stage, $"running {stage}", CommandTimeout, environment), token);

    /// <summary>
    /// Adapts non-throwing external command results to the native proof runner contract while enforcing bounded capture.
    /// Internal visibility provides a focused test seam for verifying that proof commands retain the release capture
    /// policy and that unsuccessful child processes are rejected before their output is treated as successful evidence.
    /// </summary>
    internal sealed class TailwindBoundedCommandRunner(IExternalCommandRunner inner) : ICommandRunner
    {
        public async Task<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(new ExternalCommandRequest(request.FileName, request.Arguments, request.WorkingDirectory,
                request.OperationName, request.TimeoutDescription, request.TimeoutMilliseconds, request.Environment, ExternalCapturePolicy.ReleaseProof), cancellationToken);
            if (result.ExitCode != 0)
                throw new PackageIndexException($"{request.OperationName} failed for '{request.Subject}' (exit {result.ExitCode}).\n{result.StandardError}\n{result.StandardOutput}".Trim());
            return new CommandRunResult(result.StandardOutput, result.StandardError);
        }
    }

    private static string ResolvePackageDirectory(JsonElement assets, string expectedCache, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part is ".." or "." or "")) throw new PackageIndexException("Assets graph contains an unsafe package path.");
        var folders = assets.GetProperty("packageFolders").EnumerateObject().Select(item => Path.GetFullPath(item.Name)).ToArray();
        if (folders.Length != 1 || !SamePath(folders[0], expectedCache)) throw new PackageIndexException("Assets graph does not resolve exclusively from the designated fresh private NuGet cache.");
        var path = Path.GetFullPath(Path.Combine(folders[0], relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(expectedCache, path) || !Directory.Exists(path)) throw new PackageIndexException("Resolved NuGet package directory escapes the private cache or is absent.");
        RejectLinkAncestors(path, expectedCache);
        return path;
    }

    private static (string Rid, string Os, string OsArchitecture, string ProcessArchitecture) ObserveHost()
    {
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Unknown";
        var arch = RuntimeInformation.OSArchitecture.ToString();
        var process = RuntimeInformation.ProcessArchitecture.ToString();
        var rid = (os, arch) switch { ("Linux", "X64") => "linux-x64", ("Linux", "Arm64") => "linux-arm64", ("macOS", "X64") => "osx-x64", ("macOS", "Arm64") => "osx-arm64", ("Windows", "X64" or "Arm64") when process == "X64" => "win-x64", _ => null };
        return (rid!, os, arch, process);
    }

    /// <summary>Reads the selected host binary identity from a restored Tailwind archive's release manifest.</summary>
    /// <param name="packageArchive">Path to the restored Tailwind NuGet archive.</param>
    /// <param name="rid">Expected host RID, which must identify exactly one manifest asset.</param>
    /// <returns>The binary filename, SHA-256, and Tailwind version recorded by the package.</returns>
    /// <exception cref="PackageIndexException">The archive lacks a usable internal release manifest or selected asset.</exception>
    internal static (string Name, string Sha256, string Version) ReadRidAsset(string packageArchive, string rid)
    {
        using var archive = ZipFile.OpenRead(packageArchive);
        var entries = archive.Entries.Where(item => string.Equals(item.FullName, "build/tailwind.release.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1) throw new PackageIndexException("Restored Tailwind archive must contain exactly one build/tailwind.release.json.");
        using var document = TailwindEvidenceWorkflow.ParseStrict(TailwindEvidenceWorkflow.ReadZipEntryBounded(entries[0]), "restored Tailwind release manifest");
        if (!document.RootElement.TryGetProperty("assets", out var assetArray) || assetArray.ValueKind != JsonValueKind.Array)
            throw new PackageIndexException("Tailwind release manifest has no assets array.");
        var assets = assetArray.EnumerateArray().Where(item => item.TryGetProperty("rid", out var ridValue) && ridValue.ValueKind == JsonValueKind.String && ridValue.GetString() == rid).ToArray();
        if (assets.Length != 1) throw new PackageIndexException($"Tailwind manifest does not define exactly one asset for '{rid}'.");
        var version = document.RootElement.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String
            ? versionElement.GetString()! : throw new PackageIndexException("Tailwind manifest version is absent.");
        var asset = assets[0];
        if (!asset.TryGetProperty("binaryName", out var name) || name.ValueKind != JsonValueKind.String
            || !asset.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String)
            throw new PackageIndexException($"Tailwind manifest asset for '{rid}' is missing its binary identity.");
        return (name.GetString()!, sha.GetString()!, version);
    }

    /// <summary>Requires the restored first-party closure to have exactly the producer's packages and package hashes.</summary>
    /// <param name="expected">Packages from the bound producer subject.</param>
    /// <param name="actual">Packages resolved by the native consumer's NuGet assets graph.</param>
    /// <exception cref="PackageIndexException">The closure count or any package identity differs.</exception>
    internal static void RequireSameClosure(IReadOnlyList<TailwindSubjectPackage> expected, IReadOnlyList<TailwindSubjectPackage> actual)
    {
        if (expected.Count != actual.Count) throw new PackageIndexException("Restored first-party package closure differs from producer subject.");
        foreach (var item in expected)
        {
            var match = actual.SingleOrDefault(value => value.PackageId.Equals(item.PackageId, StringComparison.OrdinalIgnoreCase));
            if (match is null || match.PackageVersion != item.PackageVersion || match.PackageSha512 != item.PackageSha512)
                throw new PackageIndexException($"Restored package closure for '{item.PackageId}' differs from producer subject.");
        }
    }

    private static string ResolveConfined(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(root, path)) throw new PackageIndexException($"Protected payload path '{relative}' escapes its package directory.");
        return path;
    }

    private static bool DictionaryEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);
    private static void CopyRegularFile(string source, string destination)
    {
        var info = new FileInfo(source);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new PackageIndexException($"Evidence source '{source}' is missing or not a regular file.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static void RequireFresh(string path, string label)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new PackageIndexException($"{label} directory '{path}' must be fresh and absent.");
        var parent = Path.GetDirectoryName(path) ?? throw new PackageIndexException($"{label} path has no parent.");
        Directory.CreateDirectory(parent);
        var current = new DirectoryInfo(parent);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new PackageIndexException($"{label} path has a reparse-point ancestor '{current.FullName}'.");
            current = current.Parent;
        }
    }
    private static void RejectLinkAncestors(string path, string root)
    {
        var file = new FileInfo(path);
        if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new PackageIndexException($"NuGet cache contains a link/reparse point at '{file.FullName}'.");
        var current = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (current is not null && IsWithin(root, current.FullName))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new PackageIndexException($"NuGet cache contains a link/reparse point at '{current.FullName}'.");
            if (SamePath(current.FullName, root)) break;
            current = current.Parent;
        }
    }
    private static bool IsWithin(string root, string candidate)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string EscapeXml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > TailwindProofSubjectService.MaximumDocumentBytes) throw new PackageIndexException($"'{path}' exceeds the 16 MiB evidence limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }
}
