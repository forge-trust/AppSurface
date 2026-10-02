using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Exercises the public carrier dispatch through its injected command boundary and the smoke workflow.</summary>
public sealed class PreflightCarrierDispatchTests : IDisposable
{
    private const string PackageVersion = "1.2.3-ci.4";
    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string RunId = "run-845-dispatch";
    private const string ArtifactId = "artifact-845-dispatch";
    private const string Recipe = "create role appsurface_durable_owner;";
    private readonly string _root = TestPathUtils.PathUnder(Path.GetTempPath(), "preflight-carrier-dispatch", Guid.NewGuid().ToString("N"));

    public PreflightCarrierDispatchTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task CandidateCommand_UsesInjectedRunnerAndPromotesOnlyTheValidatedReceipt()
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        var runner = fixture.CreateControllerRunner();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            fixture.Arguments("candidate"), stdout, stderr, fixture.Request.RepositoryRoot,
            preflightCommandRunner: runner);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("Issue #845 candidate artifact proof passed", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("scenarios=4", stdout.ToString(), StringComparison.Ordinal);
        var command = Assert.Single(runner.Requests);
        AssertControllerRequest(fixture, command, DurablePreflightArtifactProof.CandidateProofTimeoutMilliseconds);
        Assert.False(File.Exists(fixture.Request.ArtifactManifestPath));
        Assert.True(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.True(File.Exists(fixture.Request.ReceiptPath));
    }

    [Theory]
    [InlineData(7, "issue-845-proof=passed\n")]
    [InlineData(0, "")]
    public async Task CandidateCommand_FailedOrIncompleteRunnerWithholdsApprovalAndReceipt(int exit, string output)
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        var manifestBytes = await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath);
        var runner = fixture.CreateControllerRunner(exit, output);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            fixture.Arguments("candidate"), stdout, stderr, fixture.Request.RepositoryRoot,
            preflightCommandRunner: runner);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("PostgreSQL preflight artifact proof failed", stderr.ToString(), StringComparison.Ordinal);
        Assert.Single(runner.Requests);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(fixture.Request.ArtifactManifestPath));
        Assert.False(File.Exists(fixture.Request.ApprovedManifestPath));
        Assert.False(File.Exists(fixture.Request.ReceiptPath));
        Assert.Empty(Directory.GetFiles(fixture.ArtifactDirectory, "*.tmp"));
    }

    [Fact]
    public async Task PromoteCommand_ValidatesRetainedCandidateAndCopiesTheExactManifest()
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        var candidateRunner = fixture.CreateControllerRunner();
        Assert.Equal(0, await RunProgramAsync(fixture, "candidate", candidateRunner));
        var promotedInput = TestPathUtils.PathUnder(fixture.ArtifactDirectory, "unapproved-copy.json");
        var expectedBytes = await File.ReadAllBytesAsync(fixture.Request.ApprovedManifestPath);
        await File.WriteAllBytesAsync(promotedInput, expectedBytes);
        var unusedRunner = new RecordingCommandRunner((_, _) => throw new InvalidOperationException("Promote must not launch a consumer."));

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await Program.RunAsync(
            fixture.Arguments("promote", promotedInput), stdout, stderr, fixture.Request.RepositoryRoot,
            preflightCommandRunner: unusedRunner);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("candidate receipt validated and approved manifest promoted", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(Path.Combine(fixture.ArtifactDirectory, "package-artifact-manifest.json")));
        Assert.Empty(unusedRunner.Requests);
    }

    [Fact]
    public async Task PromoteCommand_RejectsStaleRetainedReceiptWithoutPublishingManifest()
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        Assert.Equal(0, await RunProgramAsync(fixture, "candidate", fixture.CreateControllerRunner()));
        var promotedInput = TestPathUtils.PathUnder(fixture.ArtifactDirectory, "unapproved-copy.json");
        File.Copy(fixture.Request.ApprovedManifestPath, promotedInput);
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Request.ReceiptPath))!.AsObject();
        receipt["sourceCommit"] = new string('a', 40);
        await File.WriteAllTextAsync(fixture.Request.ReceiptPath, receipt.ToJsonString(PackageArtifactJson.Options));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            fixture.Arguments("promote", promotedInput), stdout, stderr, fixture.Request.RepositoryRoot,
            preflightCommandRunner: new RecordingCommandRunner((_, _) => throw new InvalidOperationException()));

        Assert.Equal(1, exitCode);
        Assert.Contains("does not match expected 'sourceCommit'", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.ArtifactDirectory, "package-artifact-manifest.json")));
    }

    [Fact]
    public async Task PublishedCommand_RunsTheSameProofAgainstByteIdenticalRestoredPackages()
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        await fixture.WriteCompleteReceiptAsync(fixture.Request.ReceiptPath);
        var restoredPackages = await fixture.CreateRestoredPackagesAsync();
        var publishedReceipt = TestPathUtils.PathUnder(_root, "published", "proof.json");
        var runner = fixture.CreateControllerRunner();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            fixture.Arguments("published", candidateReceipt: fixture.Request.ReceiptPath,
                restoredPackages: restoredPackages, publishedReceipt: publishedReceipt),
            stdout, stderr, fixture.Request.RepositoryRoot, preflightCommandRunner: runner);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("Issue #845 published artifact proof passed", stdout.ToString(), StringComparison.Ordinal);
        Assert.Single(runner.Requests);
        AssertControllerRequest(fixture, runner.Requests[0], DurablePreflightArtifactProof.PublishedProofTimeoutMilliseconds);
        Assert.True(File.Exists(publishedReceipt));
        Assert.True(File.Exists(publishedReceipt + ".carrier.json"));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(publishedReceipt));
        Assert.Equal(4, receipt.RootElement.GetProperty("scenarios").GetArrayLength());
    }

    [Fact]
    public async Task PublishedCommand_FailedRunnerRemovesBothPublicReceiptsAndOwnedScratch()
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        await fixture.WriteCompleteReceiptAsync(fixture.Request.ReceiptPath);
        var restoredPackages = await fixture.CreateRestoredPackagesAsync();
        var publishedReceipt = TestPathUtils.PathUnder(_root, "published", "proof.json");
        var runner = fixture.CreateControllerRunner(exit: 9, output: "issue-845-proof=passed\n");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunAsync(
            fixture.Arguments("published", candidateReceipt: fixture.Request.ReceiptPath,
                restoredPackages: restoredPackages, publishedReceipt: publishedReceipt),
            stdout, stderr, fixture.Request.RepositoryRoot, preflightCommandRunner: runner);

        Assert.Equal(1, exitCode);
        Assert.Contains("PostgreSQL preflight artifact proof failed", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(publishedReceipt));
        Assert.False(File.Exists(publishedReceipt + ".carrier.json"));
        var command = Assert.Single(runner.Requests);
        var scratch = command.Arguments[Array.IndexOf(command.Arguments.ToArray(), "--artifact-dir") + 1];
        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public async Task SmokeWorkflow_RestoredPackagesReachTheActualPublishedCarrier()
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        var smoke = await SmokeFixture.CreateAsync(fixture, _root);
        await fixture.WriteCompleteReceiptAsync(fixture.Request.ReceiptPath);

        var report = await smoke.Workflow.RunAsync(
            smoke.Request(fixture.Request.ReceiptPath, smoke.PublishedReceiptPath), CancellationToken.None);

        Assert.All(report.Entries, entry => Assert.Equal(PackageSmokeInstallStatus.Restored, entry.Status));
        Assert.Equal(fixture.Entries.Count, report.Entries.Count);
        var restore = Assert.Single(smoke.Runner.Requests, request => request.Arguments.FirstOrDefault() == "restore");
        var smokeProject = await File.ReadAllTextAsync(restore.Arguments[1]);
        foreach (var entry in fixture.Entries.Where(entry => !entry.IsTool))
            Assert.Contains($"Include=\"{entry.PackageId}\"", smokeProject, StringComparison.Ordinal);
        Assert.Contains(smoke.Runner.Requests, request => request.Arguments.FirstOrDefault() == "restore");
        Assert.Contains(smoke.Runner.Requests, request => request.Arguments.Take(2).SequenceEqual(["tool", "install"]));
        Assert.Contains(smoke.Runner.Requests, request => request.OperationName == "PostgreSQL preflight artifact proof");
        Assert.True(File.Exists(smoke.PublishedReceiptPath));
        Assert.True(File.Exists(smoke.PublishedReceiptPath + ".carrier.json"));
    }

    [Fact]
    public async Task SmokeWorkflow_FailedPackageRestoreStopsBeforePublishedProof()
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        var smoke = await SmokeFixture.CreateAsync(fixture, _root, restoreExitCode: 1);
        await fixture.WriteCompleteReceiptAsync(fixture.Request.ReceiptPath);

        var error = await Assert.ThrowsAsync<PackageIndexException>(
            () => smoke.Workflow.RunAsync(
                smoke.Request(fixture.Request.ReceiptPath, smoke.PublishedReceiptPath), CancellationToken.None));

        Assert.Contains("Public-feed smoke failed", error.Message, StringComparison.Ordinal);
        Assert.Equal(5, smoke.Runner.Requests.Count(request => request.Arguments.FirstOrDefault() == "restore"));
        Assert.DoesNotContain(smoke.Runner.Requests, request => request.OperationName == "PostgreSQL preflight artifact proof");
        Assert.False(File.Exists(smoke.PublishedReceiptPath));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SmokeWorkflow_MissingRetainedReceiptPathsStopBeforePublishedProof(bool omitCandidate, bool omitPublished)
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        var smoke = await SmokeFixture.CreateAsync(fixture, _root);
        await fixture.WriteCompleteReceiptAsync(fixture.Request.ReceiptPath);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => smoke.Workflow.RunAsync(
            smoke.Request(
                omitCandidate ? null : fixture.Request.ReceiptPath,
                omitPublished ? null : smoke.PublishedReceiptPath),
            CancellationToken.None));

        Assert.Contains("requires candidate and published receipt paths", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(smoke.Runner.Requests, request => request.OperationName == "PostgreSQL preflight artifact proof");
        Assert.False(File.Exists(smoke.PublishedReceiptPath));
    }

    [Fact]
    public async Task SmokeWorkflow_PropagatesActualCarrierFailureWithoutWritingPublicReceipts()
    {
        var fixture = await CarrierFixture.CreateAsync(_root);
        var smoke = await SmokeFixture.CreateAsync(fixture, _root, controllerExitCode: 3);
        await fixture.WriteCompleteReceiptAsync(fixture.Request.ReceiptPath);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() =>
            smoke.Workflow.RunAsync(
                smoke.Request(fixture.Request.ReceiptPath, smoke.PublishedReceiptPath), CancellationToken.None));

        Assert.Contains("PostgreSQL preflight artifact proof failed", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, smoke.Runner.Requests.Count(request => request.OperationName == "PostgreSQL preflight artifact proof"));
        Assert.False(File.Exists(smoke.PublishedReceiptPath));
        Assert.False(File.Exists(smoke.PublishedReceiptPath + ".carrier.json"));
    }

    private static void AssertControllerRequest(CarrierFixture fixture, ExternalCommandRequest request, int timeout)
    {
        Assert.Equal("bash", request.FileName);
        Assert.Equal("PostgreSQL preflight artifact proof", request.OperationName);
        Assert.Equal(timeout, request.TimeoutMilliseconds);
        Assert.Equal(ExternalCapturePolicy.ReleaseProof, request.CapturePolicy);
        Assert.Contains(Path.Combine(fixture.Request.RepositoryRoot, "Durable", "verify-preflight-artifacts.sh"), request.Arguments);
        Assert.Equal(fixture.Request.RepositoryRoot, request.WorkingDirectory);
        Assert.Equal("true", request.Environment!["CI"]);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450", request.Environment["PREFLIGHT_STORE_ID"]);
        Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450", request.Environment["PREFLIGHT_ACTIVE_EPOCH"]);
        Assert.Contains("--consumer-project", request.Arguments);
        Assert.Contains("--receipt", request.Arguments);
    }

    private static async Task<int> RunProgramAsync(CarrierFixture fixture, string mode, IExternalCommandRunner runner)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        return await Program.RunAsync(fixture.Arguments(mode), stdout, stderr, fixture.Request.RepositoryRoot,
            preflightCommandRunner: runner);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CarrierFixture
    {
        private static readonly string[] ProjectPaths =
        [
            "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
            "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
            "Durable/ForgeTrust.AppSurface.Durable.PostgreSql/ForgeTrust.AppSurface.Durable.PostgreSql.csproj",
            "Durable/ForgeTrust.AppSurface.Durable/ForgeTrust.AppSurface.Durable.csproj"
        ];

        private readonly string _root;

        private CarrierFixture(string root, string repositoryRoot, string artifactDirectory, string manifestPath,
            string cliSha256, string providerSha256, string recipeSha256, string rolePairsSha256,
            string schema10Sha256, string onePairSha256, IReadOnlyList<PackageArtifactManifestEntry> entries)
        {
            _root = root;
            ArtifactDirectory = artifactDirectory;
            Entries = entries;
            Request = new DurablePreflightArtifactProofRequest(
                repositoryRoot,
                artifactDirectory,
                manifestPath,
                TestPathUtils.PathUnder(root, "approved", "package-artifact-manifest.json"),
                TestPathUtils.PathUnder(root, "candidate.receipt.json"),
                SourceCommit,
                RunId,
                ArtifactId);
            CliSha256 = cliSha256;
            ProviderSha256 = providerSha256;
            RecipeSha256 = recipeSha256;
            RolePairsSha256 = rolePairsSha256;
            Schema10Sha256 = schema10Sha256;
            OnePairSha256 = onePairSha256;
        }

        public DurablePreflightArtifactProofRequest Request { get; }
        public string ArtifactDirectory { get; }
        public string CliSha256 { get; }
        public string ProviderSha256 { get; }
        public string RecipeSha256 { get; }
        public string RolePairsSha256 { get; }
        public string Schema10Sha256 { get; }
        public string OnePairSha256 { get; }
        public IReadOnlyList<PackageArtifactManifestEntry> Entries { get; }

        public static async Task<CarrierFixture> CreateAsync(string root)
        {
            var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
            var artifactDirectory = TestPathUtils.PathUnder(root, "candidate-bundle");
            Directory.CreateDirectory(artifactDirectory);
            var artifactSpecs = new[]
            {
                ("ForgeTrust.AppSurface.Web", ProjectPaths[0], false, "", (string?)null),
                ("ForgeTrust.AppSurface.Cli", ProjectPaths[1], true, "appsurface", (string?)null),
                ("ForgeTrust.AppSurface.Durable.PostgreSql", ProjectPaths[2], false, "", Recipe),
                ("ForgeTrust.AppSurface.Durable", ProjectPaths[3], false, "", (string?)null)
            };
            var reportEntries = new List<PackageArtifactValidationReportEntry>();
            foreach (var (packageId, projectPath, isTool, commandName, recipe) in artifactSpecs)
            {
                var packagePath = TestPathUtils.PathUnder(artifactDirectory, $"{packageId}.{PackageVersion}.nupkg");
                await WritePackageAsync(packagePath, packageId, recipe);
                var decision = packageId is "ForgeTrust.AppSurface.Web" or "ForgeTrust.AppSurface.Cli"
                    ? PackagePublishDecision.Publish
                    : PackagePublishDecision.SupportPublish;
                reportEntries.Add(new PackageArtifactValidationReportEntry(
                    packageId,
                    projectPath,
                    decision,
                    [],
                    packagePath,
                    isTool,
                    commandName));
            }

            var manifestPath = TestPathUtils.PathUnder(artifactDirectory, "unapproved.json");
            await new PackageArtifactManifestWriter().WriteAsync(
                new PackageArtifactValidationReport(PackageVersion, reportEntries), artifactDirectory, manifestPath, CancellationToken.None);
            var manifest = await new PackageArtifactManifestReader().ReadAsync(manifestPath, CancellationToken.None);
            using var roles = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(
                repositoryRoot, "examples/durable-postgresql/role-pairs-full-and-work-only.example.json")));
            var firstPair = roles.RootElement.GetProperty("pairs")[0];
            var onePairBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                pairs = new[]
                {
                    new
                    {
                        dispatcher = firstPair.GetProperty("dispatcher").GetString(),
                        runtime = firstPair.GetProperty("runtime").GetString(),
                        dispatcher_profile = "full"
                    }
                }
            });
            return new CarrierFixture(
                root,
                repositoryRoot,
                artifactDirectory,
                manifestPath,
                Hash256(TestPathUtils.PathUnder(artifactDirectory, $"ForgeTrust.AppSurface.Cli.{PackageVersion}.nupkg")),
                Hash256(TestPathUtils.PathUnder(artifactDirectory, $"ForgeTrust.AppSurface.Durable.PostgreSql.{PackageVersion}.nupkg")),
                Hash256(Encoding.UTF8.GetBytes(Recipe)),
                Hash256(TestPathUtils.PathUnder(repositoryRoot, "examples/durable-postgresql/role-pairs-full-and-work-only.example.json")),
                Hash256(TestPathUtils.PathUnder(repositoryRoot, "Durable/consumers/PostgreSqlPreflightConsumer/schema10-two-pair.sql")),
                Hash256(onePairBytes),
                manifest.Entries);
        }

        public string[] Arguments(
            string mode,
            string? artifactManifest = null,
            string? candidateReceipt = null,
            string? restoredPackages = null,
            string? publishedReceipt = null)
        {
            var args = new List<string>
            {
                "verify-preflight-artifact", "--mode", mode,
                "--repo-root", Request.RepositoryRoot,
                "--artifact-dir", Request.ArtifactDirectory,
                "--artifact-manifest", artifactManifest ?? Request.ArtifactManifestPath,
                "--approved-manifest", Request.ApprovedManifestPath,
                "--receipt", Request.ReceiptPath,
                "--source-commit", Request.SourceCommit,
                "--run-id", Request.RunId,
                "--artifact-id", Request.ArtifactId
            };
            if (mode == "published")
            {
                args.AddRange(["--candidate-receipt", candidateReceipt!, "--restored-packages", restoredPackages!,
                    "--published-receipt", publishedReceipt!]);
            }
            return args.ToArray();
        }

        public RecordingCommandRunner CreateControllerRunner(int exit = 0, string output = "issue-845-proof=passed\n")
            => new RecordingCommandRunner(async (request, token) =>
            {
                await WriteCompleteReceiptAsync(ValueAfter(request.Arguments, "--receipt"), token);
                return new ExternalCommandResult(exit, output, string.Empty);
            });

        public RecordingCommandRunner CreateSmokeRunner(int restoreExitCode, int controllerExitCode = 0)
            => new RecordingCommandRunner(async (request, token) =>
            {
                if (request.Arguments.FirstOrDefault() == "restore")
                {
                    if (restoreExitCode == 0)
                    {
                        await CreateRestoredPackagesAsync(ValueAfter(request.Arguments, "--packages"));
                    }
                    return new ExternalCommandResult(restoreExitCode, "restore output", string.Empty);
                }
                if (request.Arguments.Take(2).SequenceEqual(["tool", "install"]))
                    return new ExternalCommandResult(0, "installed", string.Empty);
                if (request.Arguments.SequenceEqual(["--help"]))
                    return new ExternalCommandResult(0, "appsurface commands", string.Empty);
                if (request.Arguments.SequenceEqual(["--version"]))
                    return new ExternalCommandResult(0, PackageVersion + "\n", string.Empty);
                if (request.OperationName == "PostgreSQL preflight artifact proof")
                {
                    await WriteCompleteReceiptAsync(ValueAfter(request.Arguments, "--receipt"), token);
                    return new ExternalCommandResult(controllerExitCode, "issue-845-proof=passed\n", string.Empty);
                }
                throw new InvalidOperationException($"Unexpected smoke command '{request.OperationName}'.");
            });

        public async Task WriteCompleteReceiptAsync(string path, CancellationToken cancellationToken = default)
        {
            var runtimeRoles = ReadRuntimeRoles();
            var scenarios = new[]
            {
                Scenario("one-pair", 1, false, runtimeRoles),
                Scenario("pair-enrollment", 2, false, runtimeRoles),
                Scenario("identical-rerun-stable-catalog", 2, false, runtimeRoles),
                Scenario("schema10-to-11-two-pair", 2, true, runtimeRoles)
            };
            var cliResults = new List<object>();
            AddResults(cliResults, "one-pair", 1, OnePairSha256, runtimeRoles, includeSource: false);
            foreach (var scenario in new[] { "pair-enrollment", "identical-rerun-stable-catalog", "schema10-to-11-two-pair" })
                AddResults(cliResults, scenario, 2, RolePairsSha256, runtimeRoles, includeSource: true);
            const string writerScenario = "queued-writer-prewriter-complete";
            AddResults(cliResults, writerScenario, 2, RolePairsSha256, runtimeRoles, includeSource: true);

            var receipt = new
            {
                schemaVersion = 1,
                proofKind = "issue-845-exact-package-disposable-postgresql-consumer",
                sourceCommit = Request.SourceCommit,
                runId = Request.RunId,
                artifactId = Request.ArtifactId,
                packageVersion = PackageVersion,
                artifactManifestSha256 = Hash256(Request.ArtifactManifestPath),
                packages = Entries.Select(entry => new
                {
                    packageId = entry.PackageId,
                    version = PackageVersion,
                    sha256 = Hash256(TestPathUtils.PathUnder(ArtifactDirectory, entry.ArtifactFileName))
                }).ToArray(),
                exactCliPackageSha256 = CliSha256,
                exactProviderPackageSha256 = ProviderSha256,
                extractedRoleRecipeSha256 = RecipeSha256,
                schema10FixtureSha256 = Schema10Sha256,
                completeRoleManifestSha256 = RolePairsSha256,
                onePairManifestSha256 = OnePairSha256,
                migrationOwnerRole = "appsurface_durable_owner",
                storeId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                activeEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                postgresImage = "postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877",
                scenarios,
                cliResults,
                migrationChecksums = Enumerable.Range(1, 10)
                    .Select(version => new { Version = version, Name = $"migration_{version:0000}", sha256 = new string('e', 64) })
                    .ToArray(),
                guardWindow = new { intact = true, backendPids = new[] { 41, 42, 43, 44 } },
                activationCallbackCompleted = true,
                schema10BaselineSnapshotSha256 = new string('8', 64),
                catalogSnapshotSha256BeforeAndAfterIdenticalRerun = new[] { new string('7', 64), new string('7', 64) },
                queuedWriter = new
                {
                    outcome = "completed-before-writer",
                    authoritativeScenario = writerScenario,
                    queuedWriterObserved = true,
                    writerFinished = true,
                    authoritativeWindowCompletedBeforeWriter = true,
                    writerAcquiredAfterWindow = true,
                    boundedFailure = (object?)null,
                    writerReleasedAfterFailure = false,
                    writerFinishedBeforeRerun = false,
                    authoritativeWindow = Scenario(writerScenario, 2, false, runtimeRoles)
                },
                guardLossNegativeProof = new
                {
                    lanePassBackendPid = 51,
                    lanePassExecutingBackendPid = 53,
                    lanePassStoreId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                    lanePassActiveEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                    lanePassStarted = true,
                    lanePassObservedExecuting = true,
                    lanePassBackendTerminated = true,
                    lanePassCancellationObserved = true,
                    lanePassChildDrained = true,
                    lanePassReceiptWithheld = true,
                    activationBackendTerminated = true,
                    activationCancellationObserved = true,
                    activationHostDrainPersisted = true,
                    activationAdmissionClosed = true,
                    activationSessionsReleased = true,
                    activationReceiptWithheld = true,
                    activationBackendPid = 52,
                    activationStoreId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                    activationActiveEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                    activationWorkInvocationStartedBeforeCompletion = true,
                    activationDrainVerifiedCheckpointObserved = true,
                    activationHostedServicesStopped = true,
                    activationChildDrained = true,
                    activationIdentityUnchanged = true,
                    finalReceiptAbsentBeforeAndAfter = true
                },
                stageDurationsMilliseconds = new
                {
                    one_pair_bootstrap_ms = 1.0,
                    pair_enrollment_recipe_ms = 1.0,
                    identical_rerun_ms = 1.0,
                    schema10_to_11_upgrade_ms = 1.0,
                    queued_writer_fresh_rerun_ms = 1.0
                },
                totalDurationMilliseconds = 10.0
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(receipt, PackageArtifactJson.Options), cancellationToken);
        }

        public async Task<string> CreateRestoredPackagesAsync()
        {
            var path = TestPathUtils.PathUnder(_root, "restored-packages");
            await CreateRestoredPackagesAsync(path);
            return path;
        }

        private async Task CreateRestoredPackagesAsync(string root)
        {
            foreach (var entry in Entries)
            {
                var packageDirectory = TestPathUtils.PathUnder(root, entry.PackageId.ToLowerInvariant(), PackageVersion.ToLowerInvariant());
                Directory.CreateDirectory(packageDirectory);
                File.Copy(TestPathUtils.PathUnder(ArtifactDirectory, entry.ArtifactFileName),
                    TestPathUtils.PathUnder(packageDirectory, $"{entry.PackageId.ToLowerInvariant()}.{PackageVersion.ToLowerInvariant()}.nupkg"), true);
            }
            var recipePath = TestPathUtils.PathUnder(root, "forgetrust.appsurface.durable.postgresql", PackageVersion.ToLowerInvariant(),
                "contentFiles/any/any/configure-postgresql-roles.sql");
            Directory.CreateDirectory(Path.GetDirectoryName(recipePath)!);
            await File.WriteAllTextAsync(recipePath, Recipe);
        }

        public string[] RuntimeRoles()
        {
            using var roles = JsonDocument.Parse(File.ReadAllText(TestPathUtils.PathUnder(
                Request.RepositoryRoot, "examples/durable-postgresql/role-pairs-full-and-work-only.example.json")));
            return roles.RootElement.GetProperty("pairs").EnumerateArray()
                .Select(pair => pair.GetProperty("runtime").GetString()!)
                .ToArray();
        }

        private string[] ReadRuntimeRoles() => RuntimeRoles();

        private static object Scenario(string name, int count, bool schema10, IReadOnlyList<string> roles)
        {
            var sourceDenials = count == 2;
            var summary = count == 1
                ? "forwarder-work-flow-schedule-passed"
                : "forwarder-work-flow-schedule-and-source-work-only-denials-passed";
            return new
            {
                name,
                scenario = name,
                ownerGuard = "continuous-shared-session-lock",
                guardBackendPid = 42,
                runtimePreflightCount = count,
                ownerDiagnostic = "separate-and-never-counted",
                laneEvidence = summary,
                laneEvidenceBefore = LaneEvidence(name, roles, sourceDenials),
                laneEvidenceAfter = LaneEvidence(name, roles, sourceDenials),
                fixtureActivationCallbackCompleted = true,
                storeId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                activeEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                modeledSchema10BaselinePresent = schema10
            };
        }

        private static object LaneEvidence(string scenario, IReadOnlyList<string> roles, bool sourceDenials)
            => new
            {
                scenario,
                storeId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                activeEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                guardBackendPid = 42,
                forwarder = new
                {
                    runtimeRole = roles[0],
                    workResult = "completed",
                    flowResult = "completed",
                    scheduleResult = "completed"
                },
                sourceWorkOnly = sourceDenials ? new
                {
                    runtimeRole = roles[1],
                    workResult = "completed",
                    flowResult = "denied",
                    scheduleResult = "denied",
                    allResult = "denied"
                } : null
            };

        private static void AddResults(List<object> results, string scenario, int count, string manifestHash,
            IReadOnlyList<string> roles, bool includeSource)
        {
            results.Add(new
            {
                Scenario = scenario,
                Caller = "runtime",
                Pair = (int?)1,
                Role = roles[0],
                Owner = "appsurface_durable_owner",
                RuntimeCount = count,
                ManifestSha256 = manifestHash,
                StoreId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                ActiveEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                ElapsedMilliseconds = 3.1
            });
            if (includeSource)
                results.Add(new
                {
                    Scenario = scenario,
                    Caller = "runtime",
                    Pair = (int?)2,
                    Role = roles[1],
                    Owner = "appsurface_durable_owner",
                    RuntimeCount = count,
                    ManifestSha256 = manifestHash,
                    StoreId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                    ActiveEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                    ElapsedMilliseconds = 3.2
                });
            results.Add(new
            {
                Scenario = scenario,
                Caller = "owner-diagnostic",
                Pair = (int?)null,
                Role = "appsurface_durable_owner",
                Owner = "appsurface_durable_owner",
                RuntimeCount = count,
                ManifestSha256 = manifestHash,
                StoreId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa8450",
                ActiveEpoch = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb8450",
                ElapsedMilliseconds = 4.0
            });
        }

        private static async Task WritePackageAsync(string path, string packageId, string? recipe)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = File.Create(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var nuspec = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>{packageId}</id><version>{PackageVersion}</version><repository type=\"git\" url=\"https://github.com/forge-trust/AppSurface\" commit=\"{SourceCommit}\" /></metadata></package>";
            await using (var nuspecStream = archive.CreateEntry($"{packageId}.nuspec").Open())
                await nuspecStream.WriteAsync(Encoding.UTF8.GetBytes(nuspec));
            if (recipe is not null)
                await using (var recipeStream = archive.CreateEntry("contentFiles/any/any/configure-postgresql-roles.sql").Open())
                    await recipeStream.WriteAsync(Encoding.UTF8.GetBytes(recipe));
        }

        private static string Hash256(string path) => Hash256(File.ReadAllBytes(path));
        private static string Hash256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed class SmokeFixture
    {
        private readonly string _root;
        private readonly CarrierFixture _carrier;

        private SmokeFixture(string root, CarrierFixture carrier,
            PackageSmokeInstallWorkflow workflow, string publishedReceiptPath, RecordingCommandRunner runner)
        {
            _root = root;
            _carrier = carrier;
            Workflow = workflow;
            PublishedReceiptPath = publishedReceiptPath;
            Runner = runner;
        }

        public PackageSmokeInstallWorkflow Workflow { get; }
        public string PublishedReceiptPath { get; }
        public RecordingCommandRunner Runner { get; }

        public static async Task<SmokeFixture> CreateAsync(
            CarrierFixture carrier,
            string root,
            int restoreExitCode = 0,
            int controllerExitCode = 0)
        {
            var smokeRoot = TestPathUtils.PathUnder(root, "smoke-repository");
            var projectMetadata = new Dictionary<string, PackageProjectMetadata>(StringComparer.OrdinalIgnoreCase);
            var packageRows = new List<string> { "packages:" };
            var row = new StringBuilder();
            foreach (var entry in carrier.Entries)
            {
                var projectPath = entry.ProjectPath;
                var packageId = entry.PackageId;
                var isWeb = packageId == "ForgeTrust.AppSurface.Web";
                var isCli = packageId == "ForgeTrust.AppSurface.Cli";
                await WriteFileAsync(smokeRoot, projectPath, "<Project />");
                row.Clear()
                    .AppendLine($"  - project: {projectPath}")
                    .AppendLine("    product_family: appsurface")
                    .AppendLine($"    classification: {(isWeb || isCli ? "public" : "support")}")
                    .AppendLine($"    publish_decision: {(isWeb || isCli ? "publish" : "support_publish")}")
                    .AppendLine($"    order: {(isWeb ? 10 : isCli ? 20 : packageId.EndsWith("PostgreSql", StringComparison.Ordinal) ? 30 : 40)}");
                if (isWeb || isCli)
                {
                    var readmePath = Path.Join(Path.GetDirectoryName(projectPath), "README.md").Replace('\\', '/');
                    await WriteFileAsync(smokeRoot, readmePath, "# Package");
                    row.AppendLine("    use_when: Install this package for the documented public workflow.")
                        .AppendLine("    includes: The documented package surface.")
                        .AppendLine("    does_not_include: Other package surfaces.")
                        .AppendLine($"    start_here_path: {readmePath}");
                }
                else
                {
                    row.AppendLine("    note: Supporting runtime package.");
                }
                if (isCli) row.AppendLine("    tool_command_name: appsurface");
                packageRows.Add(row.ToString().TrimEnd());
                projectMetadata[projectPath] = new PackageProjectMetadata(
                    projectPath, packageId, "net10.0", true, isCli, isCli ? "Exe" : "Library", []);
            }
            await WriteFileAsync(smokeRoot, "packages/package-index.yml", string.Join(Environment.NewLine, packageRows));
            var metadataProvider = new TestMetadataProvider(projectMetadata);
            var runner = carrier.CreateSmokeRunner(restoreExitCode, controllerExitCode);
            var workflow = new PackageSmokeInstallWorkflow(
                new PackageArtifactManifestReader(),
                new PackagePublishPlanResolver(new PackageProjectScanner(), metadataProvider, new PackageManifestLoader()),
                runner,
                new PackageSmokeInstallReportRenderer(),
                (_, _) => Task.CompletedTask);
            return new SmokeFixture(root, carrier, workflow,
                TestPathUtils.PathUnder(root, "smoke", "published.receipt.json"), runner);
        }

        public PackageSmokeInstallRequest Request(string? candidateReceipt, string? publishedReceipt)
            => new(
                TestPathUtils.PathUnder(_root, "smoke-repository"),
                TestPathUtils.PathUnder(_root, "smoke-repository", "packages/package-index.yml"),
                _carrier.Request.ArtifactManifestPath,
                TestPathUtils.PathUnder(_root, "smoke", "work"),
                TestPathUtils.PathUnder(_root, "smoke", "report.md"),
                "https://api.nuget.org/v3/index.json",
                _carrier.Request with { ReceiptPath = PublishedReceiptPath },
                candidateReceipt,
                publishedReceipt);

        private static async Task WriteFileAsync(string root, string relativePath, string text)
        {
            var path = TestPathUtils.PathUnder(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text);
        }
    }

    private sealed class TestMetadataProvider(IReadOnlyDictionary<string, PackageProjectMetadata> metadata)
        : IProjectMetadataProvider
    {
        public Task<PackageProjectMetadata> GetMetadataAsync(string repositoryRoot, string projectPath, CancellationToken cancellationToken)
            => Task.FromResult(metadata[projectPath]);
    }

    private sealed class RecordingCommandRunner(Func<ExternalCommandRequest, CancellationToken, Task<ExternalCommandResult>> runAsync)
        : IExternalCommandRunner
    {
        public List<ExternalCommandRequest> Requests { get; } = [];

        public async Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await runAsync(request, cancellationToken);
        }
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
        => arguments[Array.IndexOf(arguments.ToArray(), option) + 1];
}
