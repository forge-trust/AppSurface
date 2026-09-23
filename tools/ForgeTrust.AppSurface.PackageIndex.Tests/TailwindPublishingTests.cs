using System.Text;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindPublishingTests
{
    private const string Version = "1.2.3-preview.798";

    [Fact]
    public void BoundedCaptureStream_RetainsExactLimitAndDrainsLaterWrites()
    {
        using var stream = new BoundedCaptureStream(5);
        stream.Write(Encoding.UTF8.GetBytes("abc"));
        Assert.False(stream.Truncated);
        Assert.Equal("abc", stream.GetText());

        stream.Write(Encoding.UTF8.GetBytes("defgh"));
        stream.Write(Encoding.UTF8.GetBytes(new string('z', 10_000)));

        Assert.True(stream.Truncated);
        Assert.StartsWith("abcde\n[output truncated", stream.GetText(), StringComparison.Ordinal);
        Assert.Equal(5, stream.Length);
    }

    [Fact]
    public void BoundedCaptureStream_RejectsNonPositiveLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedCaptureStream(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedCaptureStream(-1));
    }

    [Fact]
    public async Task CliWrapRunner_StreamsAndBoundsRealChildOutput()
    {
        var runner = new CliWrapCommandRunner();
        var result = await runner.RunAsync(
            new ExternalCommandRequest(
                "dotnet",
                ["--info"],
                Directory.GetCurrentDirectory(),
                "dotnet --info",
                "collecting SDK information",
                30_000,
                CapturePolicy: new ExternalCapturePolicy(32)),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardOutputTruncated);
        Assert.Contains("[output truncated after configured byte limit]", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(result.StandardOutput.Split('\n')[0]) <= 32);
    }

    [Fact]
    public async Task TailwindPlanWithoutEvidence_RejectsBeforeCredentialReadOrPush()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind");
        var credential = new RecordingCredentialProvider();
        var runner = new RecordingPushRunner();
        var workflow = fixture.CreateWorkflow(runner, credential);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => workflow.RunAsync(fixture.Request, CancellationToken.None));

        Assert.Contains("Tailwind publication requires", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, credential.Reads);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task InvalidTailwindEvidence_RejectsBeforeCredentialReadOrPush()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind");
        var credential = new RecordingCredentialProvider();
        var runner = new RecordingPushRunner();
        var validator = new RecordingEvidenceValidator((_, _) =>
            throw new PackageIndexException("substituted producer subject"));
        var workflow = fixture.CreateWorkflow(runner, credential, validator);

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => workflow.RunAsync(
            fixture.Request with { TailwindEvidence = fixture.CreateEvidence() }, CancellationToken.None));

        Assert.Contains("substituted producer subject", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, validator.Calls);
        Assert.Equal(0, credential.Reads);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("unconfined")]
    [InlineData("changed")]
    [InlineData("missing")]
    public async Task TailwindPublisher_RejectsUnconfinedChangedOrMissingPreparedInputBeforeCredential(string fault)
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind");
        var evidence = fixture.CreateEvidence();
        var credential = new RecordingCredentialProvider();
        var runner = new RecordingPushRunner();
        var validator = new RecordingEvidenceValidator((entries, _) =>
        {
            if (fault == "unconfined") return entries;
            var prepared = CopyPrepared(evidence, entries);
            if (fault == "changed") File.AppendAllText(prepared.Single().ArtifactPath, "substituted bytes");
            else File.Delete(prepared.Single().ArtifactPath);
            return prepared;
        });
        var workflow = fixture.CreateWorkflow(runner, credential, validator);

        await Assert.ThrowsAsync<PackageIndexException>(() => workflow.RunAsync(
            fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None));

        Assert.Equal(0, credential.Reads);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task ValidTailwindEvidence_PushesOnlyPreparedBytesAndRecordsIdentity()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind");
        var credential = new RecordingCredentialProvider();
        var runner = new RecordingPushRunner();
        var evidence = fixture.CreateEvidence();
        var validator = new RecordingEvidenceValidator((entries, _) => CopyPrepared(evidence, entries));
        var workflow = fixture.CreateWorkflow(runner, credential, validator);

        var ledger = await workflow.RunAsync(fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None);

        Assert.Equal(1, validator.Calls);
        Assert.Equal(1, credential.Reads);
        var push = Assert.Single(runner.Requests);
        Assert.Equal(evidence.PublicationDirectory, Path.GetDirectoryName(push.Arguments[2]));
        Assert.Equal(PackagePublishStatus.Pushed, Assert.Single(ledger.Entries).Status);
        Assert.Equal(evidence.ProducerArtifactId, ledger.TailwindIdentity?.ProducerArtifactId);
        Assert.Equal(evidence.PublicationStartArtifactId, ledger.TailwindIdentity?.PublicationStartArtifactId);
        var log = await File.ReadAllTextAsync(fixture.Request.PublishLogPath);
        Assert.Contains($"Producer artifact ID: `{evidence.ProducerArtifactId}`", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TailwindPublisher_RejectsSecondPreparedPackageMutatedAfterFirstPush()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind", includeWebPackage: true);
        var evidence = fixture.CreateEvidence();
        var credential = new RecordingCredentialProvider();
        var validator = new RecordingEvidenceValidator((entries, _) => CopyPrepared(evidence, entries));
        var runner = new RecordingPushRunner(count =>
        {
            if (count == 1)
            {
                var tailwind = Directory.GetFiles(evidence.PublicationDirectory, "*Tailwind*.nupkg").Single();
                File.AppendAllText(tailwind, "mutated after first push");
            }
        });
        var workflow = fixture.CreateWorkflow(runner, credential, validator);

        var ledger = await workflow.RunAsync(fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None);

        Assert.Equal(1, credential.Reads);
        Assert.Single(runner.Requests);
        Assert.Equal([PackagePublishStatus.Pushed, PackagePublishStatus.Failed],
            ledger.Entries.Select(entry => entry.Status).ToArray());
        Assert.Contains("changed before the push boundary", ledger.Entries[1].Output, StringComparison.Ordinal);
        Assert.Equal(evidence.ProducerArtifactId, ledger.TailwindIdentity?.ProducerArtifactId);
        var log = await File.ReadAllTextAsync(fixture.Request.PublishLogPath);
        Assert.Contains("failed", log, StringComparison.Ordinal);
        Assert.Contains($"Publication-start artifact ID: `{evidence.PublicationStartArtifactId}`", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TailwindPublisher_RecordsMissingPreparedPackageBeforeItsPush()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind", includeWebPackage: true);
        var evidence = fixture.CreateEvidence();
        var credential = new RecordingCredentialProvider();
        var validator = new RecordingEvidenceValidator((entries, _) => CopyPrepared(evidence, entries));
        var runner = new RecordingPushRunner(count =>
        {
            if (count == 1)
                File.Delete(Directory.GetFiles(evidence.PublicationDirectory, "*Tailwind*.nupkg").Single());
        });

        var ledger = await fixture.CreateWorkflow(runner, credential, validator).RunAsync(
            fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None);

        Assert.Single(runner.Requests);
        Assert.Equal([PackagePublishStatus.Pushed, PackagePublishStatus.Failed],
            ledger.Entries.Select(entry => entry.Status).ToArray());
        Assert.Contains("path changed before the push boundary", ledger.Entries[1].Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TailwindPublisher_RejectsIncompletePreparedInventoryBeforeCredentialRead()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind", includeWebPackage: true);
        var evidence = fixture.CreateEvidence();
        var credential = new RecordingCredentialProvider();
        var runner = new RecordingPushRunner();
        var validator = new RecordingEvidenceValidator((entries, _) => CopyPrepared(evidence, entries).Take(1).ToArray());

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.CreateWorkflow(runner, credential, validator)
            .RunAsync(fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None));

        Assert.Contains("inventory", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, credential.Reads);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("reordered")]
    [InlineData("tampered")]
    public async Task TailwindPublisher_RejectsReorderedOrTamperedPreparedInventoryBeforeCredentialRead(string mutation)
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind", includeWebPackage: true);
        var evidence = fixture.CreateEvidence();
        var credential = new RecordingCredentialProvider();
        var runner = new RecordingPushRunner();
        var validator = new RecordingEvidenceValidator((entries, _) =>
        {
            var prepared = CopyPrepared(evidence, entries).ToArray();
            if (mutation == "reordered") Array.Reverse(prepared);
            else File.AppendAllText(prepared[0].ArtifactPath, "tampered before preflight");
            return prepared;
        });

        await Assert.ThrowsAsync<PackageIndexException>(() => fixture.CreateWorkflow(runner, credential, validator)
            .RunAsync(fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None));

        Assert.Equal(0, credential.Reads);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("empty-root")]
    [InlineData("empty-file")]
    [InlineData("missing-root")]
    [InlineData("missing-file")]
    [InlineData("nested-file")]
    [InlineData("invalid-path")]
    public async Task TailwindPublisher_RejectsUnsafePreparedPathsBeforeCredentialRead(string fault)
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind");
        var publicationDirectory = TestPathUtils.PathUnder(fixture.Root, "prepared-publication");
        var evidence = fixture.CreateEvidence() with { PublicationDirectory = publicationDirectory };
        var credential = new RecordingCredentialProvider();
        var runner = new RecordingPushRunner();
        var validator = new RecordingEvidenceValidator((entries, _) =>
        {
            if (fault == "empty-root")
                return entries;

            Directory.CreateDirectory(publicationDirectory);
            if (fault == "missing-root")
            {
                Directory.Delete(publicationDirectory);
                return entries.Select(entry => new PlannedPackageArtifact(
                    entry.ManifestEntry,
                    TestPathUtils.PathUnder(publicationDirectory, entry.ManifestEntry.ArtifactFileName))).ToArray();
            }

            if (fault == "missing-file")
                return entries.Select(entry => new PlannedPackageArtifact(
                    entry.ManifestEntry,
                    TestPathUtils.PathUnder(publicationDirectory, entry.ManifestEntry.ArtifactFileName))).ToArray();

            if (fault == "invalid-path")
                return entries.Select(entry => new PlannedPackageArtifact(entry.ManifestEntry, "\0invalid.nupkg")).ToArray();

            var prepared = new List<PlannedPackageArtifact>();
            foreach (var entry in entries)
            {
                var destination = fault == "nested-file"
                    ? TestPathUtils.PathUnder(publicationDirectory, "nested", entry.ManifestEntry.ArtifactFileName)
                    : TestPathUtils.PathUnder(publicationDirectory, entry.ManifestEntry.ArtifactFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(entry.ArtifactPath, destination);
                prepared.Add(new PlannedPackageArtifact(entry.ManifestEntry, destination));
            }

            if (fault == "empty-file")
                return [new PlannedPackageArtifact(prepared[0].ManifestEntry, " ")];

            return prepared;
        });

        var requestedEvidence = fault == "empty-root"
            ? evidence with { PublicationDirectory = string.Empty }
            : evidence;
        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.CreateWorkflow(runner, credential, validator)
            .RunAsync(fixture.Request with { TailwindEvidence = requestedEvidence }, CancellationToken.None));

        Assert.Contains("confined regular package archive", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, credential.Reads);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task TailwindPublisher_RecordsNuGetDuplicateWithoutChangingFrozenIdentity()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind");
        var evidence = fixture.CreateEvidence();
        var credential = new RecordingCredentialProvider();
        var validator = new RecordingEvidenceValidator((entries, _) => CopyPrepared(evidence, entries));
        var runner = new RecordingPushRunner((_, _) =>
            new ExternalCommandResult(0, "Package already exists and was skipped", string.Empty));

        var ledger = await fixture.CreateWorkflow(runner, credential, validator).RunAsync(
            fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None);

        Assert.Equal(PackagePublishStatus.DuplicateReported, Assert.Single(ledger.Entries).Status);
        Assert.Equal(evidence.ExpectedSubjectSha256, ledger.TailwindIdentity?.SubjectSha256);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task TailwindPublisher_StopsAfterFailedPushAndKeepsLaterPackageOutOfNuGet()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind", includeWebPackage: true);
        var evidence = fixture.CreateEvidence();
        var credential = new RecordingCredentialProvider();
        var validator = new RecordingEvidenceValidator((entries, _) => CopyPrepared(evidence, entries));
        var runner = new RecordingPushRunner((_, _) => new ExternalCommandResult(3, "push failed", string.Empty));

        var ledger = await fixture.CreateWorkflow(runner, credential, validator).RunAsync(
            fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None);

        Assert.Single(runner.Requests);
        Assert.Equal([PackagePublishStatus.Failed, PackagePublishStatus.SkippedAfterFailure],
            ledger.Entries.Select(entry => entry.Status).ToArray());
        Assert.Equal(3, ledger.Entries[0].ExitCode);
        Assert.Contains("earlier package failed", ledger.Entries[1].Output, StringComparison.Ordinal);
        Assert.Equal(evidence.ProducerArtifactId, ledger.TailwindIdentity?.ProducerArtifactId);
    }

    [Fact]
    public async Task TailwindPublisher_RequiresCredentialOnlyAfterEvidenceValidation()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web.Tailwind");
        var evidence = fixture.CreateEvidence();
        var credential = new RecordingCredentialProvider(value: null);
        var runner = new RecordingPushRunner();
        var validator = new RecordingEvidenceValidator((entries, _) => CopyPrepared(evidence, entries));

        var error = await Assert.ThrowsAsync<PackageIndexException>(() => fixture.CreateWorkflow(runner, credential, validator)
            .RunAsync(fixture.Request with { TailwindEvidence = evidence }, CancellationToken.None));

        Assert.Contains("TEST_ONLY_NUGET_KEY", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, validator.Calls);
        Assert.Equal(1, credential.Reads);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task NonTailwindPlan_RetainsExistingPublisherPath()
    {
        using var fixture = await PublishFixture.CreateAsync("ForgeTrust.AppSurface.Web");
        var credential = new RecordingCredentialProvider();
        var runner = new RecordingPushRunner();
        var workflow = fixture.CreateWorkflow(runner, credential);

        var ledger = await workflow.RunAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(1, credential.Reads);
        Assert.Single(runner.Requests);
        Assert.Equal(PackagePublishStatus.Pushed, Assert.Single(ledger.Entries).Status);
    }

    private sealed class RecordingCredentialProvider(string? value = "test-only-token") : IReleaseCredentialProvider
    {
        public int Reads { get; private set; }

        public string? Read(string environmentVariable)
        {
            Reads++;
            return value;
        }
    }

    private sealed class RecordingPushRunner(Action<int>? beforeResult = null) : IExternalCommandRunner
    {
        public List<ExternalCommandRequest> Requests { get; } = [];

        public RecordingPushRunner(Func<int, ExternalCommandRequest, ExternalCommandResult> resultFactory)
            : this()
        {
            _resultFactory = resultFactory;
        }

        private readonly Func<int, ExternalCommandRequest, ExternalCommandResult>? _resultFactory;

        public Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            beforeResult?.Invoke(Requests.Count);
            return Task.FromResult(_resultFactory?.Invoke(Requests.Count, request)
                ?? new ExternalCommandResult(0, "pushed", string.Empty));
        }
    }

    private sealed class RecordingEvidenceValidator(
        Func<IReadOnlyList<PlannedPackageArtifact>, TailwindPublicationRequest, IReadOnlyList<PlannedPackageArtifact>> validate)
        : ITailwindPublicationEvidenceValidator
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<PlannedPackageArtifact>> ValidateAsync(
            TailwindPublicationRequest request,
            PackageArtifactManifest manifest,
            IReadOnlyList<PlannedPackageArtifact> originalEntries,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(validate(originalEntries, request));
        }
    }

    private static IReadOnlyList<PlannedPackageArtifact> CopyPrepared(
        TailwindPublicationRequest evidence,
        IReadOnlyList<PlannedPackageArtifact> entries)
    {
        Directory.CreateDirectory(evidence.PublicationDirectory);
        return entries.Select(entry =>
        {
            var prepared = TestPathUtils.PathUnder(evidence.PublicationDirectory, entry.ManifestEntry.ArtifactFileName);
            File.Copy(entry.ArtifactPath, prepared);
            return new PlannedPackageArtifact(entry.ManifestEntry, prepared);
        }).ToArray();
    }

    private sealed class FixtureMetadataProvider(string packageId, string relativeProjectPath) : IProjectMetadataProvider
    {
        public Task<PackageProjectMetadata> GetMetadataAsync(string repositoryRoot, string projectPath, CancellationToken cancellationToken)
        {
            var resolvedId = projectPath == relativeProjectPath ? packageId : "ForgeTrust.AppSurface.Web";
            return Task.FromResult(new PackageProjectMetadata(projectPath, resolvedId, "net10.0", true, false, "Library", []));
        }
    }

    private sealed class PublishFixture : IDisposable
    {
        private readonly string _packageId;
        private readonly string _projectPath;

        private PublishFixture(string root, string packageId, string projectPath, PackagePublishRequest request)
        {
            Root = root;
            _packageId = packageId;
            _projectPath = projectPath;
            Request = request;
        }

        public string Root { get; }
        public PackagePublishRequest Request { get; }

        public TailwindPublicationRequest CreateEvidence()
            => new(Root, Request.ArtifactsInputPath, Request.ArtifactManifestPath,
                Path.Combine(Request.ArtifactsInputPath, "tailwind-proof-subject.json"),
                "123", new string('a', 64), "789", "456", new string('b', 40),
                Path.Combine(Root, "aggregate"), "321", new string('c', 64),
                Path.Combine(Root, "publication"), Path.Combine(Root, "start", "publication-start-receipt.json"),
                "654", Path.Combine(Root, "publish-report"));

        public static async Task<PublishFixture> CreateAsync(string packageId, bool includeWebPackage = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "tailwind-publishing-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var projectPath = packageId == "ForgeTrust.AppSurface.Web.Tailwind"
                ? "Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj"
                : "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj";
            var absoluteProject = TestPathUtils.PathUnder(root, projectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absoluteProject)!);
            await File.WriteAllTextAsync(absoluteProject, "<Project />");
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(absoluteProject)!, "README.md"), "# Fixture");
            if (packageId == "ForgeTrust.AppSurface.Web.Tailwind")
            {
                var webProject = Path.Combine(root, "Web", "ForgeTrust.AppSurface.Web", "ForgeTrust.AppSurface.Web.csproj");
                Directory.CreateDirectory(Path.GetDirectoryName(webProject)!);
                await File.WriteAllTextAsync(webProject, "<Project />");
                await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(webProject)!, "README.md"), "# Web fixture");
            }
            var manifestPath = Path.Combine(root, "packages", "package-index.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var webRow = packageId == "ForgeTrust.AppSurface.Web.Tailwind"
                ? includeWebPackage
                    ? """
                        - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                          product_family: appsurface
                          classification: public
                          publish_decision: publish
                          order: 5
                          use_when: Base web package for tests.
                          includes: Web features for tests.
                          does_not_include: Tailwind proof package.
                          start_here_path: Web/ForgeTrust.AppSurface.Web/README.md

                      """
                    : """
                    - project: Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj
                      product_family: appsurface
                      classification: public
                      publish_decision: do_not_publish
                      publish_reason: Fixture Web is not published.
                      order: 5
                      use_when: Base web package for tests.
                      includes: Web features for tests.
                      does_not_include: Tailwind proof package.
                      start_here_path: Web/ForgeTrust.AppSurface.Web/README.md

                  """
                : string.Empty;
            var targetRow = $"""
                  - project: {projectPath}
                    product_family: appsurface
                    classification: public
                    publish_decision: publish
                    order: 10
                    use_when: Test this package.
                    includes: Package files.
                    does_not_include: Other packages.
                    start_here_path: {Path.GetDirectoryName(projectPath)!.Replace('\\', '/')}/README.md

                """;
            await File.WriteAllTextAsync(manifestPath, "packages:\n" + webRow + targetRow);
            var artifacts = Path.Combine(root, "artifacts");
            Directory.CreateDirectory(artifacts);
            var archive = Path.Combine(artifacts, $"{packageId}.{Version}.nupkg");
            await File.WriteAllTextAsync(archive, "fixture package bytes");
            var entries = new List<PackageArtifactValidationReportEntry>();
            if (includeWebPackage)
            {
                var webArchive = Path.Combine(artifacts, $"ForgeTrust.AppSurface.Web.{Version}.nupkg");
                await File.WriteAllTextAsync(webArchive, "fixture web package bytes");
                entries.Add(new PackageArtifactValidationReportEntry(
                    "ForgeTrust.AppSurface.Web", "Web/ForgeTrust.AppSurface.Web/ForgeTrust.AppSurface.Web.csproj",
                    PackagePublishDecision.Publish, [], webArchive));
            }
            entries.Add(new PackageArtifactValidationReportEntry(packageId, projectPath, PackagePublishDecision.Publish, [], archive));
            var artifactManifest = Path.Combine(artifacts, "package-artifact-manifest.json");
            await new PackageArtifactManifestWriter().WriteAsync(
                new PackageArtifactValidationReport(Version, entries),
                artifacts,
                artifactManifest,
                CancellationToken.None);
            var request = new PackagePublishRequest(root, manifestPath, artifacts, artifactManifest,
                Path.Combine(root, "publish-ledger.md"), "https://api.nuget.org/v3/index.json", "TEST_ONLY_NUGET_KEY");
            return new PublishFixture(root, packageId, projectPath, request);
        }

        public PackagePublishWorkflow CreateWorkflow(
            IExternalCommandRunner runner,
            IReleaseCredentialProvider credential,
            ITailwindPublicationEvidenceValidator? validator = null)
        {
            var resolver = new PackagePublishPlanResolver(
                new PackageProjectScanner(),
                new FixtureMetadataProvider(_packageId, _projectPath),
                new PackageManifestLoader());
            return new PackagePublishWorkflow(resolver, new PackageArtifactManifestReader(), runner,
                new PackagePublishLedgerRenderer(), credential, validator);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
