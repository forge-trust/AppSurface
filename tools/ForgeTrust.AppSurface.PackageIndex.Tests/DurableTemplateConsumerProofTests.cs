using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateConsumerProofTests : IDisposable
{
    [Theory]
    [InlineData("ubuntu24", "20261004.1", "ubuntu24/20261004.1")]
    [InlineData("macos15-arm64", "20261004.1", "macos15-arm64/20261004.1")]
    [InlineData("win25", "20261004.1", "win25/20261004.1")]
    [InlineData(null, "20261004.1", "")]
    [InlineData("ubuntu24", null, "")]
    [InlineData("ubuntu24", "", "")]
    [InlineData("ubuntu24", "private\nvalue", "")]
    [InlineData("private/value", "20261004.1", "")]
    public void RunnerImageRetainsOnlyCompleteSafeObservedIdentity(string? os, string? version, string expected)
        => Assert.Equal(expected, DurableTemplateConsumerProof.ReadRunnerImage(os, version));

    private const string Version = "0.2.0-preview.13";
    private const string SourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ProviderId = "ForgeTrust.AppSurface.Durable.PostgreSql";
    private const string TestingId = "ForgeTrust.AppSurface.Durable.Testing";
    private const string SecretSentinel = "consumer-proof-secret-must-not-be-retained";
    private readonly string _root = CreateTestRoot();
    private readonly string _repositoryRoot;
    private readonly string _stagedTemplate;
    private readonly string _templateArchive;
    private readonly string _providerArchive;
    private readonly string _testingArchive;

    public DurableTemplateConsumerProofTests()
    {
        Directory.CreateDirectory(_root);
        _repositoryRoot = FindRepositoryRoot();
        _stagedTemplate = Path.Join(_root, "staged-template");
        DurableTemplateStaging.Stage(
            TestPathUtils.PathUnder(_repositoryRoot, DurableTemplateStaging.ContentPath), _stagedTemplate, Version);

        var artifacts = Path.Join(_root, "artifacts");
        Directory.CreateDirectory(artifacts);
        _templateArchive = Path.Join(artifacts, $"{DurableTemplateStaging.PackageId}.{Version}.nupkg");
        _providerArchive = Path.Join(artifacts, $"{ProviderId}.{Version}.nupkg");
        _testingArchive = Path.Join(artifacts, $"{TestingId}.{Version}.nupkg");
        CreateTemplateArchive(_stagedTemplate, _templateArchive, Version);
        CreateProviderArchive(_providerArchive, Path.Join(_repositoryRoot, "Durable", "configure-postgresql-roles.sql"));
        File.WriteAllText(_testingArchive, "candidate-testing-package", new UTF8Encoding(false));
    }

    [Fact]
    public async Task SuccessSimulatesInstalledConsumersWithIsolatedRootsSafeReceiptAndSqlIdentity()
    {
        var runner = new DeterministicRunner(_stagedTemplate, _providerArchive);
        var receiptPath = Path.Join(_root, "receipt.json");
        var nativeIdentity = SafeNativeIdentity();
        var nativeSmokeCalls = 0;
        var request = CreateRequest(receiptPath, runNativeSmoke: true);

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(
            request,
            CandidateArtifacts(),
            nativeSmoke: (generatedRoot, environment, _) =>
            {
                nativeSmokeCalls++;
                Assert.True(Directory.Exists(generatedRoot));
                Assert.True(environment["DOTNET_CLI_HOME"] is not null);
                return Task.CompletedTask;
            },
            cancellationToken: CancellationToken.None,
            nativeToolIdentity: () => nativeIdentity);

        Assert.True(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.True(receipt.AuthoredSource);
        Assert.True(receipt.ExactArchiveInstall);
        Assert.True(receipt.FeedInstall);
        Assert.True(receipt.NativeSmoke);
        Assert.True(receipt.AuthorizedActivation);
        Assert.True(receipt.TerminalWork);
        Assert.True(receipt.ReadinessTransition);
        Assert.True(receipt.ExportedActivity);
        Assert.True(receipt.SampleReplacement);
        Assert.Equal(SourceCommit, receipt.SourceCommit);
        Assert.Equal(nativeIdentity, receipt.NativeTools);
        Assert.Equal(1, nativeSmokeCalls);
        Assert.True(runner.ReplacementBuildUsedInheritedLocalFeed);
        Assert.True(runner.ReplacementSqlOutputMatchesProvider);

        var dotnetRequests = runner.Requests.Where(command => command.FileName == "dotnet").ToArray();
        Assert.NotEmpty(dotnetRequests);
        Assert.InRange(dotnetRequests.Length, 1, 32);
        var templateCreates = dotnetRequests.Where(command => command.OperationName.EndsWith("-create", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, templateCreates.Length);
        Assert.All(templateCreates, command => Assert.DoesNotContain("--no-restore", command.Arguments));
        var cliHomes = dotnetRequests.Select(command => Assert.IsType<string>(command.Environment!["DOTNET_CLI_HOME"]))
            .Distinct(StringComparer.Ordinal).ToArray();
        var caches = dotnetRequests.Select(command => Assert.IsType<string>(command.Environment!["NUGET_PACKAGES"]))
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(5, cliHomes.Length);
        Assert.Equal(5, caches.Length);
        var feedBuild = Assert.Single(dotnetRequests, command => command.OperationName == "feed-build");
        var replacementBuild = Assert.Single(dotnetRequests, command => command.OperationName == "replacement-build");
        Assert.Equal(feedBuild.Environment!["DOTNET_CLI_HOME"], replacementBuild.Environment!["DOTNET_CLI_HOME"]);
        Assert.Equal(feedBuild.Environment["NUGET_PACKAGES"], replacementBuild.Environment["NUGET_PACKAGES"]);
        var proofRoot = Path.GetDirectoryName(cliHomes[0]);
        Assert.NotNull(proofRoot);
        Assert.All(dotnetRequests, command =>
        {
            Assert.True(command.TimeoutMilliseconds is > 0 and <= 300_000);
            Assert.Equal(ExternalCapturePolicy.ReleaseProof, command.CapturePolicy);
            var environment = command.Environment ?? throw new InvalidOperationException("Dotnet proof environment was not isolated.");
            Assert.Null(environment["PGPASSWORD"]);
            Assert.Null(environment["PGDATA"]);
            Assert.Null(environment["APPSURFACE_TEMPLATE_ACTIVATION_TOKEN"]);
            Assert.Null(environment["MSBuildSDKsPath"]);
            Assert.Null(environment["MSBUILD_EXE_PATH"]);
            Assert.Null(environment["NuGetPackageRoot"]);
            Assert.Equal(proofRoot, Directory.GetParent(environment["DOTNET_CLI_HOME"]!)!.FullName);
            Assert.Equal(proofRoot, Directory.GetParent(environment["NUGET_PACKAGES"]!)!.FullName);
        });

        Assert.False(Directory.Exists(proofRoot!));
        var receiptJson = File.ReadAllText(receiptPath);
        Assert.DoesNotContain(SecretSentinel, receiptJson, StringComparison.Ordinal);
        Assert.Contains("NativeTools", receiptJson, StringComparison.Ordinal);
        Assert.Contains("postgres", receiptJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("launch", -1, false, false)]
    [InlineData("nonzero", 23, false, false)]
    [InlineData("stdout-truncated", 0, true, false)]
    [InlineData("stderr-truncated", 0, false, true)]
    public async Task FailedOrTruncatedCommandsProduceSafeReceiptAndCleanOwnedRoot(
        string failureKind, int exitCode, bool stdoutTruncated, bool stderrTruncated)
    {
        var runner = new DeterministicRunner(
            _stagedTemplate,
            _providerArchive,
            failOperation: "sdk",
            failedResult: new ExternalCommandResult(
                exitCode, SecretSentinel, SecretSentinel, stdoutTruncated, stderrTruncated));
        var receiptPath = Path.Join(_root, failureKind + ".json");

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(
            CreateRequest(receiptPath), CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("TemplateProofFailed", receipt.FailureCode);
        Assert.Equal("sdk", receipt.FailurePhase);
        var phase = Assert.Single(receipt.Phases);
        Assert.Equal("sdk", phase.Id);
        Assert.Equal(exitCode, phase.ExitCode);
        Assert.Equal(stdoutTruncated || stderrTruncated, phase.Truncated);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Requests, command => command.OperationName == "source-revision");
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Theory]
    [InlineData("sdk")]
    [InlineData("source-revision")]
    [InlineData("source-mismatch")]
    public async Task InvalidSdkOrSourceRevisionOutputIsNeverRetainedInReceipt(string invalidIdentity)
    {
        var runner = new DeterministicRunner(
            _stagedTemplate,
            _providerArchive,
            sdkOutput: invalidIdentity == "sdk" ? SecretSentinel : "10.0.102\n",
            sourceRevisionOutput: invalidIdentity switch
            {
                "source-revision" => SecretSentinel,
                "source-mismatch" => new string('b', 40),
                _ => SourceCommit + "\n"
            });
        var receiptPath = Path.Join(_root, $"invalid-{invalidIdentity}.json");
        var request = CreateRequest(receiptPath, sourceCommit: invalidIdentity == "source-revision" ? null : SourceCommit);

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(request, CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal(invalidIdentity == "source-mismatch" ? "source-revision" : invalidIdentity, receipt.FailurePhase);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        if (invalidIdentity == "sdk") Assert.Equal(string.Empty, receipt.SdkVersion);
        if (invalidIdentity == "source-revision") Assert.Equal(string.Empty, receipt.SourceCommit);
        if (invalidIdentity == "source-mismatch") Assert.Equal(SourceCommit, receipt.SourceCommit);
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Fact]
    public async Task MalformedRequestedSourceIdentityIsRejectedBeforeAnyExternalCommand()
    {
        var runner = new DeterministicRunner(_stagedTemplate, _providerArchive);
        var request = CreateRequest(Path.Join(_root, "malformed-source.json"), sourceCommit: SecretSentinel);

        var exception = await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurableTemplateConsumerProof(runner).RunAsync(request, CandidateArtifacts()));

        Assert.DoesNotContain(SecretSentinel, exception.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
        Assert.False(File.Exists(request.ReceiptPath));
    }

    [Fact]
    public async Task NativeSmokeFailureDoesNotCertifyNativeIdentityOrRetainCallbackOutput()
    {
        var runner = new DeterministicRunner(_stagedTemplate, _providerArchive);
        var receiptPath = Path.Join(_root, "native-smoke-failure.json");
        var nativeIdentityCalls = 0;

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(
            CreateRequest(receiptPath, runNativeSmoke: true),
            CandidateArtifacts(),
            nativeSmoke: (_, _, _) => throw new InvalidOperationException(SecretSentinel),
            nativeToolIdentity: () =>
            {
                nativeIdentityCalls++;
                return SafeNativeIdentity();
            });

        Assert.False(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("native-smoke", receipt.FailurePhase);
        Assert.False(receipt.NativeSmoke);
        Assert.Null(receipt.NativeTools);
        Assert.False(receipt.SampleReplacement);
        Assert.Equal(0, nativeIdentityCalls);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Theory]
    [InlineData("authored-restore")]
    [InlineData("authored-build")]
    [InlineData("authored-format")]
    [InlineData("authored-tests")]
    [InlineData("feed-install")]
    [InlineData("feed-discover")]
    [InlineData("feed-create")]
    [InlineData("feed-restore")]
    [InlineData("feed-build")]
    [InlineData("feed-format")]
    [InlineData("feed-tests")]
    public async Task AuthoredAndFeedCommandFailuresAreRedactedAndCleanOwnedInstall(string operation)
    {
        var runner = new DeterministicRunner(
            _stagedTemplate,
            _providerArchive,
            failOperation: operation,
            failedResult: new ExternalCommandResult(29, "", SecretSentinel));
        var receiptPath = Path.Join(_root, $"{operation}-failure.json");

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(
            CreateRequest(receiptPath), CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("TemplateProofFailed", receipt.FailureCode);
        Assert.Equal(operation, receipt.FailurePhase);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.False(Directory.Exists(runner.ProofRoot));
        if (operation.StartsWith("feed-", StringComparison.Ordinal))
        {
            Assert.Contains(runner.Requests, command => command.OperationName == "uninstall");
            Assert.Contains(runner.Requests, command => command.OperationName == "absence");
        }
    }

    [Theory]
    [InlineData(nameof(GraphFault.SourceProjectSubstitution))]
    [InlineData(nameof(GraphFault.MissingCandidateArchive))]
    [InlineData(nameof(GraphFault.VersionMismatch))]
    public async Task FeedRestoredGraphSubstitutionAndCandidateMismatchFailClosed(string faultName)
    {
        var fault = Enum.Parse<GraphFault>(faultName, ignoreCase: false);
        var runner = new DeterministicRunner(
            _stagedTemplate,
            _providerArchive,
            graphFault: fault,
            graphFaultOperation: "feed-restore");
        var receiptPath = Path.Join(_root, $"feed-graph-{fault}.json");

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(
            CreateRequest(receiptPath), CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("feed-restore", receipt.FailurePhase);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.Contains(runner.Requests, command => command.OperationName == "uninstall");
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Fact]
    public async Task FeedGeneratedInventoryRejectsUnexpectedFileAndCleansInstalledTemplate()
    {
        var runner = new DeterministicRunner(
            _stagedTemplate,
            _providerArchive,
            addUnexpectedGeneratedFile: true);
        var receiptPath = Path.Join(_root, "unexpected-generated-file.json");

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(
            CreateRequest(receiptPath), CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("feed-create", receipt.FailurePhase);
        Assert.False(receipt.FeedInstall);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.Contains(runner.Requests, command => command.OperationName == "uninstall");
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Fact]
    public async Task TemplateRemovalFailureIsReportedWhileOwnedTemporaryRootIsStillDeleted()
    {
        var runner = new DeterministicRunner(
            _stagedTemplate,
            _providerArchive,
            failOperation: "absence",
            failedResult: new ExternalCommandResult(1, "", SecretSentinel));
        var receiptPath = Path.Join(_root, "cleanup-failure.json");

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(
            CreateRequest(receiptPath), CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("TemplateCleanupFailed", receipt.FailureCode);
        Assert.Equal("absence", receipt.FailurePhase);
        Assert.True(runner.Requests.Count(command => command.OperationName == "uninstall") >= 2);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Fact]
    public async Task PartialInstallFailureUninstallsTheOwnedTemplateBeforeDeletingItsRoot()
    {
        var runner = new DeterministicRunner(
            _stagedTemplate,
            _providerArchive,
            failOperation: "archive-install",
            failedResult: new ExternalCommandResult(1, "", SecretSentinel));
        var receiptPath = Path.Join(_root, "partial-install.json");

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(
            CreateRequest(receiptPath), CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.False(receipt.ExactArchiveInstall);
        Assert.Contains(runner.Requests, command => command.OperationName == "uninstall");
        Assert.Contains(runner.Requests, command => command.OperationName == "absence");
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Theory]
    [InlineData("replacement-build", -1, false, false)]
    [InlineData("replacement-build", 23, false, false)]
    [InlineData("replacement-build", 0, true, false)]
    [InlineData("replacement-tests", 23, false, false)]
    [InlineData("replacement-tests", 0, false, true)]
    [InlineData("replacement-first-work", 23, false, false)]
    [InlineData("replacement-first-work", 0, true, false)]
    public async Task ReplacementCommandFailureOrTruncationKeepsReceiptFailedAndCleansOwnedRoot(
        string operation, int exitCode, bool stdoutTruncated, bool stderrTruncated)
    {
        var runner = new DeterministicRunner(
            _stagedTemplate,
            _providerArchive,
            failOperation: operation,
            failedResult: new ExternalCommandResult(exitCode, SecretSentinel, SecretSentinel, stdoutTruncated, stderrTruncated));
        var receiptPath = Path.Join(_root, $"{operation}-{exitCode}-{stdoutTruncated}-{stderrTruncated}.json");

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(CreateRequest(receiptPath), CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.False(receipt.SampleReplacement);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("TemplateProofFailed", receipt.FailureCode);
        Assert.Equal(operation, receipt.FailurePhase);
        var phase = Assert.Single(receipt.Phases, item => item.Id == operation);
        Assert.Equal(exitCode, phase.ExitCode);
        Assert.Equal(stdoutTruncated || stderrTruncated, phase.Truncated);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Fact]
    public async Task ReplacementFirstWorkMissingCheckpointDoesNotCertifySampleReplacement()
    {
        var runner = new DeterministicRunner(
            _stagedTemplate,
            _providerArchive,
            omittedReplacementCheckpoint: "[first-work] Work reached terminal completion");
        var receiptPath = Path.Join(_root, "replacement-missing-checkpoint.json");

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(CreateRequest(receiptPath), CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.False(receipt.SampleReplacement);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("replacement-first-work", receipt.FailurePhase);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Theory]
    [InlineData(nameof(GraphFault.SourceProjectSubstitution))]
    [InlineData(nameof(GraphFault.MissingProvider))]
    [InlineData(nameof(GraphFault.MissingCandidateArchive))]
    [InlineData(nameof(GraphFault.VersionMismatch))]
    [InlineData(nameof(GraphFault.OversizedAssets))]
    [InlineData(nameof(GraphFault.ExcessiveJsonDepth))]
    public async Task RestoredGraphSubstitutionMissingCandidateAndOutOfBoundsAssetsFailClosed(string faultName)
    {
        var fault = Enum.Parse<GraphFault>(faultName, ignoreCase: false);
        var runner = new DeterministicRunner(_stagedTemplate, _providerArchive, graphFault: fault);
        var receiptPath = Path.Join(_root, $"graph-{fault}.json");

        var receipt = await new DurableTemplateConsumerProof(runner).RunAsync(
            CreateRequest(receiptPath), CandidateArtifacts());

        Assert.False(receipt.Succeeded);
        Assert.True(receipt.CleanupComplete);
        Assert.Equal("authored-restore", receipt.FailurePhase);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(receiptPath), StringComparison.Ordinal);
        Assert.False(Directory.Exists(runner.ProofRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrVersionMismatchedProviderCandidateRejectsBeforeStartingProof(bool versionMismatch)
    {
        var proof = new DurableTemplateConsumerProof(new DeterministicRunner(_stagedTemplate, _providerArchive));
        var request = CreateRequest(Path.Join(_root, "invalid-candidate.json"));
        var artifacts = CandidateArtifacts();

        var invalidArtifacts = artifacts.Where(artifact => artifact.PackageId != ProviderId).ToArray();
        if (versionMismatch)
        {
            var wrongVersionPath = Path.Join(_root, "artifacts", $"{ProviderId}.0.2.0-preview.12.nupkg");
            File.Copy(_providerArchive, wrongVersionPath);
            invalidArtifacts = artifacts.Select(artifact => artifact.PackageId == ProviderId
                ? artifact with { ArtifactPath = wrongVersionPath }
                : artifact).ToArray();
        }
        await Assert.ThrowsAsync<PackageIndexException>(() => proof.RunAsync(request, invalidArtifacts));
        Assert.False(File.Exists(request.ReceiptPath));
    }

    [Fact]
    public async Task ArtifactHashMismatchFailsBeforeAnyExternalCommandRuns()
    {
        var manifestPath = Path.Join(_root, "package-artifact-manifest.json");
        var entries = CandidateArtifacts().Select(artifact => new PackageArtifactManifestEntry(
            artifact.PackageId,
            artifact.ProjectPath,
            "publish",
            Path.GetFileName(artifact.ArtifactPath),
            PackageHash.ComputeSha512(artifact.ArtifactPath),
            false)).ToArray();
        var manifest = new PackageArtifactManifest(1, Version, DateTimeOffset.UtcNow, entries);
        await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, PackageArtifactJson.Options));
        await File.AppendAllTextAsync(_providerArchive, "changed after manifest hash was frozen", new UTF8Encoding(false));

        var runner = new DeterministicRunner(_stagedTemplate, _providerArchive);
        var args = new[]
        {
            "--repo-root", _repositoryRoot,
            "--artifacts-input", Path.Join(_root, "artifacts"),
            "--artifact-manifest", manifestPath,
            "--package-version", Version,
            "--source-commit", SourceCommit,
            "--report", Path.Join(_root, "hash-mismatch-receipt.json")
        };

        await Assert.ThrowsAsync<PackageIndexException>(() => DurableTemplateCommand.RunAsync(
            args, _repositoryRoot, runner, CancellationToken.None));

        Assert.Empty(runner.Requests);
        Assert.False(File.Exists(Path.Join(_root, "hash-mismatch-receipt.json")));
    }

    [Fact]
    public void SqlOutputMustMatchCanonicalProviderArchiveAtEveryGeneratedTestOutput()
    {
        var generatedRoot = Path.Join(_root, "generated-sql-identity");
        var canonical = File.ReadAllBytes(Path.Join(_repositoryRoot, "Durable", "configure-postgresql-roles.sql"));
        WriteSqlOutput(generatedRoot, "FirstDurableWorker.Tests", "Debug", canonical);
        WriteSqlOutput(generatedRoot, "FirstDurableWorker.Tests", "Release", canonical);

        DurableTemplateConsumerProof.RequireSqlIdentity(_providerArchive, generatedRoot);

        var changed = Path.Join(generatedRoot, "tests", "FirstDurableWorker.Tests", "bin", "Release", "net10.0", "configure-postgresql-roles.sql");
        File.AppendAllText(changed, "-- substituted output\n", new UTF8Encoding(false));
        Assert.Throws<PackageIndexException>(() => DurableTemplateConsumerProof.RequireSqlIdentity(_providerArchive, generatedRoot));
    }

    [Fact]
    public void SqlOutputRequiresAnAssetAndEnforcesOneMiBOnArchiveAndOutput()
    {
        var emptyOutput = Path.Join(_root, "missing-sql-output");
        Directory.CreateDirectory(Path.Join(emptyOutput, "tests"));
        Assert.Throws<PackageIndexException>(() => DurableTemplateConsumerProof.RequireSqlIdentity(_providerArchive, emptyOutput));

        var oversizedArchive = Path.Join(_root, "oversized-provider.nupkg");
        CreateOversizedProviderArchive(oversizedArchive);
        var output = Path.Join(_root, "oversized-sql-output");
        WriteSqlOutput(output, "FirstDurableWorker.Tests", "Debug", File.ReadAllBytes(Path.Join(_repositoryRoot, "Durable", "configure-postgresql-roles.sql")));
        Assert.Throws<PackageIndexException>(() => DurableTemplateConsumerProof.RequireSqlIdentity(oversizedArchive, output));

        var oversizedOutput = Path.Join(_root, "oversized-consumer-output");
        var oversizedPath = Path.Join(oversizedOutput, "tests", "FirstDurableWorker.Tests", "bin", "Debug", "net10.0", "configure-postgresql-roles.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(oversizedPath)!);
        using (var stream = File.Create(oversizedPath)) stream.SetLength(1024 * 1024 + 1);
        Assert.Throws<PackageIndexException>(() => DurableTemplateConsumerProof.RequireSqlIdentity(_providerArchive, oversizedOutput));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private DurableTemplateConsumerProofRequest CreateRequest(
        string receiptPath, bool runNativeSmoke = false, string? sourceCommit = SourceCommit)
        => new(_repositoryRoot, Path.Join(_root, "artifacts"), Version, receiptPath,
            "https://feed.example.test/v3/index.json", sourceCommit, RunFirstWork: true, RunNativeSmoke: runNativeSmoke);

    private PackageArtifactValidationReportEntry[] CandidateArtifacts()
        =>
        [
            new(DurableTemplateStaging.PackageId, "Durable/ForgeTrust.AppSurface.Durable.Templates.csproj", PackagePublishDecision.Publish, [], _templateArchive),
            new(ProviderId, "Durable/ForgeTrust.AppSurface.Durable.PostgreSql.csproj", PackagePublishDecision.Publish, [], _providerArchive),
            new(TestingId, "Durable/ForgeTrust.AppSurface.Durable.Testing.csproj", PackagePublishDecision.Publish, [], _testingArchive)
        ];

    private NativePostgreSqlToolIdentity SafeNativeIdentity()
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["initdb"] = new('b', 64),
            ["postgres"] = new('c', 64),
            ["pg_ctl"] = new('d', 64),
            ["psql"] = new('e', 64)
        };
        return new NativePostgreSqlToolIdentity(
            Path.Join(_root, "native-bin"),
            Path.Join(_root, "native-bin", "initdb"),
            Path.Join(_root, "native-bin", "postgres"),
            Path.Join(_root, "native-bin", "pg_ctl"),
            Path.Join(_root, "native-bin", "psql"),
            "16.4", 16, "16.4", 160004, hashes);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "ForgeTrust.AppSurface.slnx"))
                && Directory.Exists(TestPathUtils.PathUnder(directory.FullName, DurableTemplateStaging.ContentPath)))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the AppSurface repository root from the test assembly.");
    }

    private static string CreateTestRoot()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/var/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        if (OperatingSystem.IsMacOS() && temporary.StartsWith("/tmp/", StringComparison.Ordinal)) temporary = "/private" + temporary;
        return TestPathUtils.PathUnder(temporary, "template-consumer-proof-tests", Guid.NewGuid().ToString("N"));
    }

    private static void CreateTemplateArchive(string contentRoot, string archivePath, string version)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        WriteArchiveText(archive, "[Content_Types].xml", "<Types />");
        WriteArchiveText(archive, "_rels/.rels", "<Relationships />");
        WriteArchiveText(archive, "README.md", "Temporary candidate archive for the isolated proof test.");
        WriteArchiveText(archive, "LICENSE", "Test fixture metadata only.");
        WriteArchiveText(archive, DurableTemplateStaging.PackageId + ".nuspec",
            $"<package><metadata><id>{DurableTemplateStaging.PackageId}</id><version>{version}</version><authors>Forge Trust</authors><description>Test candidate</description><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>");
        WriteArchiveText(archive, $"package/services/metadata/core-properties/{Guid.NewGuid():N}.psmdcp", "<metadata />");
        foreach (var file in DurableTemplateStaging.EnumerateContent(contentRoot))
        {
            var relative = Path.GetRelativePath(contentRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = archive.CreateEntry("content/durable-worker/" + relative, CompressionLevel.NoCompression);
            using var source = File.OpenRead(file);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }
    }

    private static void CreateProviderArchive(string archivePath, string canonicalSqlPath)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("contentFiles/any/any/configure-postgresql-roles.sql", CompressionLevel.NoCompression);
        using var input = File.OpenRead(canonicalSqlPath);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static void CreateOversizedProviderArchive(string archivePath)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("contentFiles/any/any/configure-postgresql-roles.sql", CompressionLevel.NoCompression);
        using var output = entry.Open();
        output.Write(new byte[1024 * 1024 + 1]);
    }

    private static void WriteArchiveText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteSqlOutput(string root, string projectName, string configuration, byte[] bytes)
    {
        var path = Path.Join(root, "tests", projectName, "bin", configuration, "net10.0", "configure-postgresql-roles.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private enum GraphFault
    {
        None,
        SourceProjectSubstitution,
        MissingProvider,
        MissingCandidateArchive,
        VersionMismatch,
        OversizedAssets,
        ExcessiveJsonDepth
    }

    private sealed class DeterministicRunner(
        string stagedTemplate,
        string providerArchive,
        string? failOperation = null,
        ExternalCommandResult? failedResult = null,
        GraphFault graphFault = GraphFault.None,
        string sdkOutput = "10.0.102\n",
        string sourceRevisionOutput = SourceCommit + "\n",
        string? omittedReplacementCheckpoint = null,
        string graphFaultOperation = "authored-restore",
        bool addUnexpectedGeneratedFile = false) : IExternalCommandRunner
    {
        private const string ProjectName = "FirstDurableWorker";
        private static readonly string[] FirstWorkCheckpoints =
        [
            "[first-work] authorized activation accepted",
            "[first-work] Work reached terminal completion",
            "[first-work] readiness: NotStarted -> Healthy",
            "[first-work] exported appsurface.durable.runtime.activation"
        ];
        private readonly List<ExternalCommandRequest> _requests = [];

        internal IReadOnlyList<ExternalCommandRequest> Requests => _requests;
        internal bool ReplacementBuildUsedInheritedLocalFeed { get; private set; }
        internal bool ReplacementSqlOutputMatchesProvider { get; private set; }
        internal string ProofRoot => Path.GetDirectoryName(
            _requests.Select(request => request.Environment)
                .Where(environment => environment is not null && environment.ContainsKey("DOTNET_CLI_HOME"))
                .Select(environment => environment!["DOTNET_CLI_HOME"]!)
                .First())!;

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(request);
            if (request.OperationName == failOperation)
            {
                return Task.FromResult(failedResult ?? new ExternalCommandResult(1, "", SecretSentinel));
            }

            if (request.FileName == "git")
            {
                return Task.FromResult(new ExternalCommandResult(0, sourceRevisionOutput, ""));
            }

            switch (request.OperationName)
            {
                case "sdk":
                    return Task.FromResult(new ExternalCommandResult(0, sdkOutput, ""));
                case "acquisition-create":
                case "archive-create":
                case "feed-create":
                    var generatedRoot = Path.Join(request.WorkingDirectory, ProjectName);
                    MaterializeTemplate(generatedRoot);
                    if (request.OperationName == "feed-create" && addUnexpectedGeneratedFile)
                    {
                        var unexpected = Path.Join(generatedRoot, "src", ProjectName, "Unexpected.cs");
                        Directory.CreateDirectory(Path.GetDirectoryName(unexpected)!);
                        File.WriteAllText(unexpected, "namespace FirstDurableWorker;\n", new UTF8Encoding(false));
                    }
                    break;
                case "acquisition-restore":
                    SimulateAcquisitionRestore(request);
                    break;
                case "authored-restore":
                case "archive-restore":
                case "feed-restore":
                    SimulateConsumerRestore(request);
                    break;
                case "authored-build":
                case "archive-build":
                case "feed-build":
                    WriteSqlOutput(request.Arguments[1], request.OperationName == "authored-build"
                        ? "AppSurfaceDurableWorker.Tests"
                        : "FirstDurableWorker.Tests", "Debug", ReadProviderSql());
                    break;
                case "replacement-build":
                    SimulateReplacementBuild(request);
                    break;
                case "archive-discover":
                case "feed-discover":
                    return Task.FromResult(new ExternalCommandResult(0, "Template short name: appsurface-durable-worker\n", ""));
                case "first-work":
                case "replacement-first-work":
                    var checkpoints = request.OperationName == "replacement-first-work" && omittedReplacementCheckpoint is not null
                        ? FirstWorkCheckpoints.Where(checkpoint => checkpoint != omittedReplacementCheckpoint)
                        : FirstWorkCheckpoints;
                    return Task.FromResult(new ExternalCommandResult(0, string.Join('\n', checkpoints) + "\n", ""));
            }

            return Task.FromResult(new ExternalCommandResult(0, "", ""));
        }

        private void SimulateAcquisitionRestore(ExternalCommandRequest request)
        {
            var cache = request.Environment!["NUGET_PACKAGES"]!;
            var thirdParty = Path.Join(cache, "xunit", "2.9.3", "xunit.2.9.3.nupkg");
            Directory.CreateDirectory(Path.GetDirectoryName(thirdParty)!);
            File.WriteAllText(thirdParty, "acquisition-only third-party package");
        }

        private void SimulateConsumerRestore(ExternalCommandRequest request)
        {
            var activeGraphFault = request.OperationName == graphFaultOperation ? graphFault : GraphFault.None;
            var environment = request.Environment!;
            var cache = environment["NUGET_PACKAGES"]!;
            var configIndex = request.Arguments.ToList().IndexOf("--configfile");
            var configPath = configIndex >= 0
                ? request.Arguments[configIndex + 1]
                : FindInheritedNuGetConfig(request.WorkingDirectory);
            var document = XDocument.Load(configPath);
            var feed = document.Descendants("add")
                .Single(element => (string?)element.Attribute("key") == "candidate")
                .Attribute("value")!.Value;
            if (request.OperationName == "replacement-build")
            {
                var sourceEntries = document.Root?.Element("packageSources")?.Elements("add").ToArray() ?? [];
                var inheritedPath = Path.GetFullPath(Path.Join(ProofRoot, "NuGet.config"));
                ReplacementBuildUsedInheritedLocalFeed = configIndex < 0
                    && Path.GetFullPath(configPath).Equals(inheritedPath, StringComparison.Ordinal)
                    && sourceEntries.Length == 1
                    && (string?)sourceEntries[0].Attribute("key") == "candidate"
                    && Path.GetFullPath((string?)sourceEntries[0].Attribute("value") ?? "")
                        .Equals(Path.GetFullPath(feed), StringComparison.Ordinal)
                    && !(document.Root?.Element("fallbackPackageFolders")?.Elements("add").Any() ?? false);
            }

            var generatedRoot = request.Arguments[1];
            var projectName = Directory.EnumerateFiles(Path.Join(generatedRoot, "src"), "*.csproj", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension).Single();
            var providerGraphName = ProviderId;
            var providerGraphVersion = Version;
            var providerType = "package";
            if (activeGraphFault == GraphFault.SourceProjectSubstitution)
                providerType = "project";
            else if (activeGraphFault == GraphFault.VersionMismatch)
                providerGraphVersion = "0.2.0-preview.12";
            else if (activeGraphFault == GraphFault.MissingCandidateArchive)
                File.Delete(Path.Join(feed, $"{ProviderId}.{Version}.nupkg"));

            var libraries = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [$"{projectName}/1.0.0"] = new { type = "project" }
            };
            if (activeGraphFault != GraphFault.MissingProvider)
                libraries[$"{providerGraphName}/{providerGraphVersion}"] = new { type = providerType };
            libraries[$"{TestingId}/{Version}"] = new { type = "package" };

            var assetFiles = new[]
            {
                Path.Join(generatedRoot, "src", projectName, "obj", "project.assets.json"),
                Path.Join(generatedRoot, "tests", projectName + ".Tests", "obj", "project.assets.json")
            };
            var graphJson = JsonSerializer.Serialize(new { libraries });
            foreach (var assetPath in assetFiles)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
                if (activeGraphFault == GraphFault.OversizedAssets)
                {
                    using var stream = File.Create(assetPath);
                    stream.SetLength(1024 * 1024 + 1);
                }
                else if (activeGraphFault == GraphFault.ExcessiveJsonDepth)
                {
                    File.WriteAllText(assetPath, WithExcessiveDepth(graphJson), new UTF8Encoding(false));
                }
                else
                {
                    File.WriteAllText(assetPath, graphJson, new UTF8Encoding(false));
                }
            }

            CopyCandidateToCache(feed, cache, TestingId);
            if (providerType == "package" && providerGraphVersion == Version && activeGraphFault != GraphFault.MissingCandidateArchive)
                CopyCandidateToCache(feed, cache, ProviderId);
        }

        private void SimulateReplacementBuild(ExternalCommandRequest request)
        {
            SimulateConsumerRestore(request);
            var replacementRoot = request.Arguments[1];
            var projectName = Directory.EnumerateFiles(Path.Join(replacementRoot, "src"), "*.csproj", SearchOption.AllDirectories)
                .Select(Path.GetFileNameWithoutExtension).Single();
            var expectedSql = ReadProviderSql();
            WriteSqlOutput(replacementRoot, projectName + ".Tests", "Debug", expectedSql);
            var sqlPath = Path.Join(replacementRoot, "tests", projectName + ".Tests", "bin", "Debug", "net10.0", "configure-postgresql-roles.sql");
            ReplacementSqlOutputMatchesProvider = File.Exists(sqlPath)
                && File.ReadAllBytes(sqlPath).AsSpan().SequenceEqual(expectedSql);
        }

        private static string FindInheritedNuGetConfig(string workingDirectory)
        {
            for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
            {
                var configPath = Path.Join(directory.FullName, "NuGet.config");
                if (File.Exists(configPath)) return configPath;
            }
            throw new InvalidOperationException("The generated replacement build has no inherited local NuGet.config.");
        }

        private byte[] ReadProviderSql()
        {
            using var archive = ZipFile.OpenRead(providerArchive);
            using var stream = archive.GetEntry("contentFiles/any/any/configure-postgresql-roles.sql")!.Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }

        private static void CopyCandidateToCache(string feed, string cache, string packageId)
        {
            var lowerId = packageId.ToLowerInvariant();
            var lowerVersion = Version.ToLowerInvariant();
            var source = Path.Join(feed, $"{packageId}.{Version}.nupkg");
            var destination = Path.Join(cache, lowerId, lowerVersion, $"{lowerId}.{lowerVersion}.nupkg");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        private static string WithExcessiveDepth(string json)
        {
            var nested = string.Concat(Enumerable.Repeat("{\"child\":", 20)) + "null" + new string('}', 20);
            return json[..^1] + ",\"nested\":" + nested + "}";
        }

        private void MaterializeTemplate(string destinationRoot)
        {
            Directory.CreateDirectory(destinationRoot);
            foreach (var source in DurableTemplateStaging.EnumerateContent(stagedTemplate))
            {
                var relative = Path.GetRelativePath(stagedTemplate, source).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith(".template.config/", StringComparison.Ordinal)) continue;
                var generatedRelative = relative.Replace("AppSurfaceDurableWorker", ProjectName, StringComparison.Ordinal);
                var destination = TestPathUtils.PathUnder(destinationRoot, generatedRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var text = File.ReadAllText(source, Encoding.UTF8)
                    .Replace("AppSurfaceDurableWorker", ProjectName, StringComparison.Ordinal);
                File.WriteAllText(destination, text, new UTF8Encoding(false));
            }
        }
    }
}
