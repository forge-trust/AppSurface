namespace ForgeTrust.AppSurface.PackageIndex.Tests;

/// <summary>Credential-free rehearsal of interrupted Tailwind publication recovery.</summary>
public sealed class OriginalCandidatePublicationReplayTests
{
    private const string PackageId = "ForgeTrust.AppSurface.Web.Tailwind";
    private const string PackageVersion = "1.2.3-preview.798";
    private static readonly string[] RequiredRids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"];

    [ManualRehearsalFact]
    public async Task ManualRehearsal_ReplaysDownloadedCandidateThroughProductionValidator()
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Manual rehearsal input {name} is missing.");

        var repositoryRoot = Required("TAILWIND_REHEARSAL_REPOSITORY_ROOT");
        var producerDirectory = Required("TAILWIND_REHEARSAL_PRODUCER_DIRECTORY");
        var aggregateDirectory = Required("TAILWIND_REHEARSAL_AGGREGATE_DIRECTORY");
        var startReceiptPath = Required("TAILWIND_REHEARSAL_START_RECEIPT");
        var preparedDirectory = Required("TAILWIND_REHEARSAL_PREPARED_DIRECTORY");
        var manifestPath = Path.Combine(producerDirectory, "package-artifact-manifest.json");
        var manifest = await new PackageArtifactManifestReader().ReadAsync(manifestPath, CancellationToken.None);
        Assert.Contains(manifest.Entries, entry => entry.PackageId == PackageId);

        var scratch = TestPathUtils.PathUnder(Path.GetTempPath(), "tailwind-real-replay", Guid.NewGuid().ToString("N"));
        try
        {
            var credentials = new TestCredentialProvider();
            var publisher = new RealEvidenceRecordingPublisher();
            var workflow = new PackagePublishWorkflow(
                new PackagePublishPlanResolver(new PackageProjectScanner(), new DotNetProjectMetadataProvider(), new PackageManifestLoader()),
                new PackageArtifactManifestReader(), publisher, new PackagePublishLedgerRenderer(), credentials);
            var originalIdentity = new TailwindPublicationRequest(
                repositoryRoot, producerDirectory, manifestPath,
                Path.Combine(producerDirectory, "tailwind-proof-subject.json"),
                Required("TAILWIND_REHEARSAL_PRODUCER_ARTIFACT_ID"),
                Required("TAILWIND_REHEARSAL_SUBJECT_SHA256"),
                Required("TAILWIND_REHEARSAL_REPOSITORY_ID"),
                Required("TAILWIND_REHEARSAL_PRODUCER_RUN_ID"),
                Required("TAILWIND_REHEARSAL_SOURCE_COMMIT"),
                aggregateDirectory,
                Required("TAILWIND_REHEARSAL_AGGREGATE_ARTIFACT_ID"),
                Required("TAILWIND_REHEARSAL_AGGREGATE_SHA256"),
                string.Empty, startReceiptPath,
                Required("TAILWIND_REHEARSAL_START_ARTIFACT_ID"), string.Empty);

            PackagePublishRequest RequestFor(int attempt)
            {
                var publicationDirectory = TestPathUtils.PathUnder(scratch, $"attempt-{attempt}", "publication");
                Directory.CreateDirectory(publicationDirectory);
                foreach (var entry in manifest.Entries)
                {
                    File.Copy(
                        Path.Combine(preparedDirectory, entry.ArtifactFileName),
                        Path.Combine(publicationDirectory, entry.ArtifactFileName));
                }

                var evidence = originalIdentity with
                {
                    PublicationDirectory = publicationDirectory,
                    ReportDirectory = TestPathUtils.PathUnder(scratch, $"attempt-{attempt}", "report")
                };
                return new PackagePublishRequest(
                    repositoryRoot, Path.Combine(repositoryRoot, "packages", "package-index.yml"),
                    producerDirectory, manifestPath,
                    TestPathUtils.PathUnder(scratch, $"attempt-{attempt}", "publish-ledger.md"),
                    "https://api.nuget.org/v3/index.json", "TEST_ONLY_NUGET_KEY", evidence);
            }

            var firstRequest = RequestFor(1);
            publisher.Attempt = 1;
            var interrupted = await workflow.RunAsync(firstRequest, CancellationToken.None);
            Assert.Equal(PackagePublishStatus.Failed, Assert.Single(interrupted.Entries, entry => entry.PackageId == PackageId).Status);
            AssertIdentity(originalIdentity, interrupted.TailwindIdentity);

            var replayRequest = RequestFor(2);
            publisher.Attempt = 2;
            var replayed = await workflow.RunAsync(replayRequest, CancellationToken.None);
            Assert.Equal(PackagePublishStatus.DuplicateReported, Assert.Single(replayed.Entries, entry => entry.PackageId == PackageId).Status);
            AssertIdentity(originalIdentity, replayed.TailwindIdentity);
            Assert.Equal(2, credentials.Reads);

            var tailwindPushes = publisher.Requests
                .Where(request => Path.GetFileName(request.Arguments[2]).StartsWith(PackageId + ".", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Equal(2, tailwindPushes.Length);
            Assert.All(tailwindPushes, request =>
            {
                Assert.Equal("dotnet", request.FileName);
                Assert.Equal("nuget", request.Arguments[0]);
                Assert.Equal("push", request.Arguments[1]);
                Assert.Equal("TEST_ONLY_SYNTHETIC_KEY", request.Arguments[6]);
            });
            Assert.NotEqual(tailwindPushes[0].Arguments[2], tailwindPushes[1].Arguments[2]);
            Assert.Equal(
                await File.ReadAllBytesAsync(tailwindPushes[0].Arguments[2]),
                await File.ReadAllBytesAsync(tailwindPushes[1].Arguments[2]));
            Assert.Equal(
                manifest.Entries.Single(entry => entry.PackageId == PackageId).Sha512,
                PackageHash.ComputeSha512(tailwindPushes[0].Arguments[2]));
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedPublication_ReplaysOriginalCandidateThroughRecordingPublisher()
    {
        using var fixture = await ReplayFixture.CreateAsync();
        var evidence = fixture.CreateEvidence();
        var credentials = new TestCredentialProvider();
        var validator = new ReplayEvidenceValidator(RequiredRids);
        var publisher = new RecordingPublisher();
        var workflow = fixture.CreateWorkflow(publisher, credentials, validator);

        var interrupted = await workflow.RunAsync(fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None);
        Assert.Equal(PackagePublishStatus.Failed, Assert.Single(interrupted.Entries).Status);
        AssertIdentity(evidence, interrupted.TailwindIdentity);

        // A new working directory models the exact-ID downloads used on recovery. The frozen IDs stay unchanged.
        var replayEvidence = evidence with
        {
            PublicationDirectory = TestPathUtils.PathUnder(fixture.Root, "replay-publication"),
            ReportDirectory = TestPathUtils.PathUnder(fixture.Root, "replay-report")
        };
        var replayed = await workflow.RunAsync(fixture.Request with { TailwindEvidence = replayEvidence }, CancellationToken.None);

        Assert.Equal(PackagePublishStatus.DuplicateReported, Assert.Single(replayed.Entries).Status);
        AssertIdentity(evidence, replayed.TailwindIdentity);
        Assert.Equal(2, validator.Requests.Count);
        Assert.All(validator.Requests, request => AssertOriginalEvidence(evidence, request));
        Assert.Equal(2, credentials.Reads);
        Assert.Equal(2, publisher.Requests.Count);
        Assert.All(publisher.Requests, request =>
        {
            Assert.Equal("dotnet", request.FileName);
            Assert.Equal("nuget", request.Arguments[0]);
            Assert.Equal("push", request.Arguments[1]);
            Assert.Equal("TEST_ONLY_SYNTHETIC_KEY", request.Arguments[6]);
            Assert.StartsWith(fixture.Root, request.Arguments[2], StringComparison.Ordinal);
        });
        Assert.Equal(
            await File.ReadAllTextAsync(publisher.Requests[0].Arguments[2]),
            await File.ReadAllTextAsync(publisher.Requests[1].Arguments[2]));
        Assert.Contains("api.nuget.org", publisher.Requests[0].Arguments[4], StringComparison.Ordinal);
        var ledger = await File.ReadAllTextAsync(fixture.Request.PublishLogPath);
        Assert.Contains("duplicate-reported", ledger, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertIdentity(TailwindPublicationRequest evidence, TailwindPublicationIdentity? identity)
    {
        Assert.NotNull(identity);
        Assert.Equal(evidence.ProducerArtifactId, identity.ProducerArtifactId);
        Assert.Equal(evidence.ExpectedSubjectSha256, identity.SubjectSha256);
        Assert.Equal(evidence.AggregateArtifactId, identity.AggregateArtifactId);
        Assert.Equal(evidence.ExpectedAggregateSha256, identity.AggregateSha256);
        Assert.Equal(evidence.PublicationStartArtifactId, identity.PublicationStartArtifactId);
    }

    private static void AssertOriginalEvidence(TailwindPublicationRequest expected, TailwindPublicationRequest actual)
    {
        Assert.Equal(expected.ProducerArtifactId, actual.ProducerArtifactId);
        Assert.Equal(expected.AggregateArtifactId, actual.AggregateArtifactId);
        Assert.Equal(expected.PublicationStartArtifactId, actual.PublicationStartArtifactId);
        Assert.Equal(expected.ExpectedSubjectSha256, actual.ExpectedSubjectSha256);
        Assert.Equal(expected.ExpectedAggregateSha256, actual.ExpectedAggregateSha256);
        Assert.Equal(expected.ProducerRunId, actual.ProducerRunId);
        Assert.Equal(expected.SourceCommit, actual.SourceCommit);
    }

    private sealed class TestCredentialProvider : IReleaseCredentialProvider
    {
        public int Reads { get; private set; }

        public string? Read(string environmentVariable)
        {
            Reads++;
            Assert.Equal("TEST_ONLY_NUGET_KEY", environmentVariable);
            return "TEST_ONLY_SYNTHETIC_KEY";
        }
    }

    private sealed class RecordingPublisher : IExternalCommandRunner
    {
        public List<ExternalCommandRequest> Requests { get; } = [];

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Requests.Count == 1
                ? new ExternalCommandResult(1, string.Empty, "simulated interruption after remote acceptance")
                : new ExternalCommandResult(0, "package already exists", string.Empty));
        }
    }

    private sealed class RealEvidenceRecordingPublisher : IExternalCommandRunner
    {
        public int Attempt { get; set; }
        public List<ExternalCommandRequest> Requests { get; } = [];

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var isTailwind = Path.GetFileName(request.Arguments[2]).StartsWith(PackageId + ".", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(isTailwind && Attempt == 1
                ? new ExternalCommandResult(1, string.Empty, "simulated interruption after remote acceptance")
                : new ExternalCommandResult(0, Attempt == 2 ? "package already exists" : "recorded push", string.Empty));
        }
    }

    private sealed class ReplayEvidenceValidator(string[] requiredRids) : ITailwindPublicationEvidenceValidator
    {
        public List<TailwindPublicationRequest> Requests { get; } = [];

        public async Task<IReadOnlyList<PlannedPackageArtifact>> ValidateAsync(
            TailwindPublicationRequest request,
            PackageArtifactManifest manifest,
            IReadOnlyList<PlannedPackageArtifact> originalEntries,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.Equal(
                requiredRids.Order(StringComparer.Ordinal),
                Directory.EnumerateDirectories(request.AggregateInputPath)
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal));
            Assert.Matches("^[0-9]+$", request.AggregateArtifactId);
            Assert.Matches("^[0-9]+$", request.PublicationStartArtifactId);
            foreach (var rid in requiredRids)
            {
                Assert.True(File.Exists(TestPathUtils.PathUnder(request.AggregateInputPath, rid, "tailwind-native-host-proof.json")));
            }
            Directory.CreateDirectory(request.PublicationDirectory);
            var prepared = new List<PlannedPackageArtifact>(originalEntries.Count);
            foreach (var entry in originalEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = TestPathUtils.PathUnder(request.PublicationDirectory, entry.ManifestEntry.ArtifactFileName);
                File.Copy(entry.ArtifactPath, path, overwrite: false);
                prepared.Add(new PlannedPackageArtifact(entry.ManifestEntry, path));
            }

            return prepared;
        }
    }

    private sealed class FixtureMetadataProvider : IProjectMetadataProvider
    {
        public Task<PackageProjectMetadata> GetMetadataAsync(string repositoryRoot, string projectPath, CancellationToken cancellationToken)
            => Task.FromResult(new PackageProjectMetadata(
                projectPath,
                projectPath.Contains("Web/ForgeTrust.AppSurface.Web/", StringComparison.Ordinal)
                    ? "ForgeTrust.AppSurface.Web"
                    : PackageId,
                "net10.0", true, false, "Library", []));
    }

    private sealed class ReplayFixture : IDisposable
    {
        private ReplayFixture(string root, PackagePublishRequest request)
        {
            Root = root;
            Request = request;
        }

        public string Root { get; }
        public PackagePublishRequest Request { get; }

        public TailwindPublicationRequest CreateEvidence()
        {
            var aggregateDirectory = TestPathUtils.PathUnder(Root, "aggregate");
            Directory.CreateDirectory(aggregateDirectory);
            File.WriteAllText(TestPathUtils.PathUnder(aggregateDirectory, "tailwind-native-host-aggregate.json"), "five-host aggregate fixture");
            foreach (var rid in RequiredRids)
            {
                var hostDirectory = TestPathUtils.PathUnder(aggregateDirectory, rid);
                Directory.CreateDirectory(hostDirectory);
                File.WriteAllText(TestPathUtils.PathUnder(hostDirectory, "tailwind-native-host-proof.json"), $"receipt for {rid}");
            }

            return new TailwindPublicationRequest(
                Root, Request.ArtifactsInputPath, Request.ArtifactManifestPath,
                TestPathUtils.PathUnder(Request.ArtifactsInputPath, "tailwind-proof-subject.json"),
                "123", new string('a', 64), "789", "456", new string('b', 40),
                aggregateDirectory, "321", new string('c', 64),
                TestPathUtils.PathUnder(Root, "publication"), TestPathUtils.PathUnder(Root, "start", "publication-start-receipt.json"),
                "654", TestPathUtils.PathUnder(Root, "publish-report"));
        }

        public static async Task<ReplayFixture> CreateAsync()
        {
            var root = TestPathUtils.PathUnder(Path.GetTempPath(), "original-candidate-replay-tests", Guid.NewGuid().ToString("N"));
            var projectPath = "Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj";
            var projectDirectory = TestPathUtils.PathUnder(root, Path.GetDirectoryName(projectPath)!);
            Directory.CreateDirectory(projectDirectory);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(projectDirectory, Path.GetFileName(projectPath)), "<Project />");
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(projectDirectory, "README.md"), "# Tailwind fixture");
            var webProjectPath = "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj";
            var webProjectDirectory = TestPathUtils.PathUnder(root, Path.GetDirectoryName(webProjectPath)!);
            Directory.CreateDirectory(webProjectDirectory);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(webProjectDirectory, Path.GetFileName(webProjectPath)), "<Project />");
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(webProjectDirectory, "README.md"), "# Web fixture");

            var manifestPath = TestPathUtils.PathUnder(root, "packages", "package-index.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(manifestPath, $$"""
                packages:
                  - project: {{webProjectPath}}
                    product_family: appsurface
                    classification: public
                    publish_decision: do_not_publish
                    publish_reason: Fixture Web is not published.
                    order: 5
                    use_when: Base web package for tests.
                    includes: Web features for tests.
                    does_not_include: Tailwind proof package.
                    start_here_path: Web/ForgeTrust.AppSurface.Web/README.md

                  - project: {{projectPath}}
                    product_family: appsurface
                    classification: public
                    publish_decision: publish
                    order: 10
                    use_when: Test publication replay.
                    includes: Package files.
                    does_not_include: Other packages.
                    start_here_path: Web/ForgeTrust.AppSurface.Web.Tailwind/README.md
                """);

            var artifacts = TestPathUtils.PathUnder(root, "artifacts");
            Directory.CreateDirectory(artifacts);
            var archive = TestPathUtils.PathUnder(artifacts, $"{PackageId}.{PackageVersion}.nupkg");
            await File.WriteAllTextAsync(archive, "frozen package bytes");
            var artifactManifest = TestPathUtils.PathUnder(artifacts, "package-artifact-manifest.json");
            await new PackageArtifactManifestWriter().WriteAsync(
                new PackageArtifactValidationReport(PackageVersion,
                [new PackageArtifactValidationReportEntry(PackageId, projectPath, PackagePublishDecision.Publish, [], archive)]),
                artifacts, artifactManifest, CancellationToken.None);
            var request = new PackagePublishRequest(root, manifestPath, artifacts, artifactManifest,
                TestPathUtils.PathUnder(root, "publish-ledger.md"), "https://api.nuget.org/v3/index.json", "TEST_ONLY_NUGET_KEY");
            return new ReplayFixture(root, request);
        }

        public PackagePublishWorkflow CreateWorkflow(
            IExternalCommandRunner publisher,
            IReleaseCredentialProvider credentials,
            ITailwindPublicationEvidenceValidator validator)
        {
            var resolver = new PackagePublishPlanResolver(new PackageProjectScanner(), new FixtureMetadataProvider(), new PackageManifestLoader());
            return new PackagePublishWorkflow(resolver, new PackageArtifactManifestReader(), publisher,
                new PackagePublishLedgerRenderer(), credentials, validator);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>Discovers the real-artifact replay only in the main-only manual rehearsal job.</summary>
public sealed class ManualRehearsalFactAttribute : FactAttribute
{
    public ManualRehearsalFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TAILWIND_REHEARSAL_ENABLED"), "true", StringComparison.Ordinal))
            Skip = "Requires the main-only manual rehearsal's downloaded producer, aggregate, and start receipt.";
    }
}
