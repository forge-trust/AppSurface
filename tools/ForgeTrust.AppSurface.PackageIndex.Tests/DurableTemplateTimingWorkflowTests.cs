using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateTimingWorkflowTests : IDisposable
{
    private const string Version = "0.2.0-preview.13";
    private const string SourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ProviderId = "ForgeTrust.AppSurface.Durable.PostgreSql";
    private const string TestingId = "ForgeTrust.AppSurface.Durable.Testing";
    private const string SecretSentinel = "timing-runner-diagnostic-secret";
    private static readonly string[] ColdDockerHosts =
    [
        "tcp://172.16.0.1:2375", "tcp://172.16.0.2:2375", "tcp://172.16.0.3:2375",
        "tcp://172.16.0.4:2375", "tcp://172.16.0.5:2375"
    ];

    private readonly string _root = CreateTestRoot();
    private readonly string _repositoryRoot;
    private readonly string _artifacts;
    private readonly string _manifestPath;
    private readonly string _reportPath;
    private readonly DurableTemplateCommandOptions _input;
    private readonly IReadOnlyDictionary<string, string> _artifactPaths;

    public DurableTemplateTimingWorkflowTests()
    {
        Directory.CreateDirectory(_root);
        _repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        _artifacts = TestPathUtils.PathUnder(_root, "candidate");
        Directory.CreateDirectory(_artifacts);
        _manifestPath = TestPathUtils.PathUnder(_root, "package-artifact-manifest.json");
        _reportPath = TestPathUtils.PathUnder(_root, "timing-receipt.json");
        _artifactPaths = CreateCandidateArtifacts();
        _input = new(_repositoryRoot, _artifacts, _manifestPath, Version, SourceCommit, _reportPath, null);
    }

    [Fact]
    public async Task PrimedWorkflowRunsFiveSerialSamplesAndWritesSafeReceipt()
    {
        var runner = new TimingRunner(this);
        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None);

        Assert.True(receipt.Succeeded, receipt.FailureCode);
        Assert.True(receipt.PerformanceGatePassed);
        Assert.True(receipt.StopwatchFrequency > 0);
        Assert.Equal(SourceCommit, receipt.SourceCommit);
        Assert.Equal("10.0.100", receipt.SdkVersion);
        Assert.Equal(5, receipt.Samples.Count);
        Assert.Equal(5, runner.FirstWorkCount);
        Assert.All(receipt.Samples, result =>
        {
            Assert.Empty(result.ValidationFailures);
            Assert.True(result.Sample!.Succeeded);
            Assert.True(result.Sample.CleanupComplete);
            Assert.True(result.Sample.DockerImagePresentAtStart);
            Assert.False(result.Sample.PackageCacheEmptyAtStart);
            Assert.True(result.Sample.HttpCacheEmptyAtStart);
            Assert.Equal(3, result.Sample.Commands.Count);
        });

        var json = File.ReadAllText(_reportPath);
        Assert.DoesNotContain(SecretSentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("--filter", json, StringComparison.Ordinal);
        Assert.DoesNotContain("standardOutput", json, StringComparison.OrdinalIgnoreCase);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
        Assert.Equal(0, runner.ActiveCommands);
    }

    [Fact]
    public async Task ColdWorkflowUsesFivePrivateDaemonsAndHasNoPerformanceSlo()
    {
        var runner = new TimingRunner(this);
        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Cold, ColdDockerHosts, CancellationToken.None);

        Assert.True(receipt.Succeeded, receipt.FailureCode);
        Assert.Null(receipt.PerformanceGatePassed);
        Assert.Equal(5, receipt.Samples.Count);
        Assert.All(receipt.Samples, result =>
        {
            Assert.Empty(result.ValidationFailures);
            Assert.True(result.Sample!.Succeeded);
            Assert.True(result.Sample.CleanupComplete);
            Assert.False(result.Sample.DockerImagePresentAtStart);
            Assert.Empty(result.Sample.ImageDigestAtStart);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), result.Sample.PackageCacheClosureSha256);
            Assert.True(result.Sample.PackageCacheEmptyAtStart);
            Assert.True(result.Sample.HttpCacheEmptyAtStart);
        });
        Assert.Equal(5, receipt.Samples.Select(result => result.Sample!.DockerDaemonIdentity).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ColdDockerHosts.Order(StringComparer.Ordinal), runner.DockerHosts.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(_reportPath), StringComparison.Ordinal);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAttemptIsRetainedWithCleanup(bool invalidMarker)
    {
        var runner = new TimingRunner(this, failFirstWorkOrdinal: invalidMarker ? null : 3, invalidDatabaseOrdinal: invalidMarker ? 3 : null);
        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None);

        Assert.False(receipt.Succeeded);
        Assert.Equal(5, receipt.Samples.Count);
        Assert.Equal(5, runner.FirstWorkCount);
        var failedSample = Assert.IsType<DurableTemplateTimingSample>(receipt.Samples[2].Sample);
        Assert.NotEmpty(receipt.Samples[2].ValidationFailures);
        Assert.False(failedSample.Succeeded);
        Assert.True(failedSample.CleanupComplete);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(_reportPath), StringComparison.Ordinal);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
        Assert.Equal(0, runner.ActiveCommands);
    }

    [Fact]
    public async Task InvalidPrimedReadyFileCancelsAndAwaitsFirstWorkCommandBeforeCleanup()
    {
        var runner = new TimingRunner(this, invalidReadyOrdinal: 1);

        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None);

        Assert.False(receipt.Succeeded);
        Assert.Equal(5, receipt.Samples.Count);
        Assert.Equal(5, runner.FirstWorkCount);
        var timedOutSample = Assert.IsType<DurableTemplateTimingSample>(receipt.Samples[0].Sample);
        Assert.Contains("setup-group-deadline", timedOutSample.FailureCode);
        Assert.True(timedOutSample.CleanupComplete);
        Assert.Equal(0, runner.ActiveCommands);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
    }

    [Theory]
    [InlineData("setup-sdk")]
    [InlineData("setup-restore")]
    [InlineData("setup-pull")]
    public async Task PreparationCommandFailureAbortsBeforeSamplesAndRemovesOwnedRoots(string operation)
    {
        var runner = new TimingRunner(this, failOperation: operation);

        var exception = await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurableTemplateTimingWorkflow(runner, () => true)
                .RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None));

        Assert.DoesNotContain(SecretSentinel, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.FirstWorkCount);
        Assert.NotEmpty(runner.PrivateRoots);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
        Assert.False(File.Exists(_reportPath));
    }

    [Fact]
    public async Task CallerCancellationIsReportedAsDeadlineAndStillRunsIndependentCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new TimingRunner(this, afterRequest: request =>
        {
            if (request.OperationName == nameof(DurableTemplateTimingCommandPhase.ProjectCreate)) cancellation.Cancel();
        });

        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], cancellation.Token);

        Assert.False(receipt.Succeeded);
        Assert.Equal(5, receipt.Samples.Count);
        var first = Assert.IsType<DurableTemplateTimingSample>(receipt.Samples[0].Sample);
        Assert.Equal("sample-deadline", first.FailureCode);
        Assert.True(first.CleanupComplete);
        Assert.Equal(0, runner.ActiveCommands);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
    }

    [Fact]
    public async Task CallerCancellationDuringFirstWorkAwaitsTheChildBeforeCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new TimingRunner(this, afterRequest: request =>
        {
            if (request.OperationName == nameof(DurableTemplateTimingCommandPhase.FirstDurableWorkTest)) cancellation.Cancel();
        });

        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], cancellation.Token);

        Assert.False(receipt.Succeeded);
        var first = Assert.IsType<DurableTemplateTimingSample>(receipt.Samples[0].Sample);
        Assert.Equal("sample-deadline", first.FailureCode);
        Assert.Equal(0, runner.ActiveCommandsAtFirstCleanup);
        Assert.Equal(0, runner.ActiveCommands);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
    }

    [Fact]
    public async Task SampleCleanupCommandFailureIsRetainedAndOuterOwnedRootIsRemoved()
    {
        var runner = new TimingRunner(this, failOperation: "sample-2-uninstall");

        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None);

        Assert.False(receipt.Succeeded);
        var failed = Assert.IsType<DurableTemplateTimingSample>(receipt.Samples[1].Sample);
        Assert.Equal("sample-cleanup-failed", failed.FailureCode);
        Assert.False(failed.CleanupComplete);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
    }

    [Fact]
    public async Task SampleCleanupFailsWhenTheTemplateRemainsInstalledAfterUninstall()
    {
        var runner = new TimingRunner(this, templateRemainsInstalledOrdinal: 2);

        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None);

        var failed = Assert.IsType<DurableTemplateTimingSample>(receipt.Samples[1].Sample);
        Assert.False(receipt.Succeeded);
        Assert.Equal("sample-cleanup-failed", failed.FailureCode);
        Assert.False(failed.CleanupComplete);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
    }

    [Theory]
    [InlineData("empty-daemon")]
    [InlineData("wrong-image-digest")]
    public async Task InvalidDockerIdentityFailsTheSampleAndStillCleansUp(string fault)
    {
        var runner = new TimingRunner(this,
            emptyDaemonOrdinal: fault == "empty-daemon" ? 1 : null,
            wrongImageOrdinal: fault == "wrong-image-digest" ? 1 : null);

        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None);

        Assert.False(receipt.Succeeded);
        var failed = Assert.IsType<DurableTemplateTimingSample>(receipt.Samples[0].Sample);
        Assert.Equal("sample-failed", failed.FailureCode);
        Assert.False(failed.Succeeded);
        Assert.True(failed.CleanupComplete);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
    }

    [Theory]
    [InlineData("missing-started-ticks")]
    [InlineData("duplicate-database-sha256")]
    [InlineData("missing-cleanup-marker")]
    public async Task MalformedFixtureMarkerFailsClosedAndCleansTheSample(string fault)
    {
        var runner = new TimingRunner(this, markerFaultOrdinal: 1, markerFault: fault);

        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None);

        Assert.False(receipt.Succeeded);
        var failed = Assert.IsType<DurableTemplateTimingSample>(receipt.Samples[0].Sample);
        Assert.Equal("sample-failed", failed.FailureCode);
        Assert.True(failed.CleanupComplete);
        Assert.All(runner.PrivateRoots, path => Assert.False(Directory.Exists(path)));
    }

    [Fact]
    public async Task TruncatedFirstWorkOutputIsNotAcceptedAsTimingEvidence()
    {
        var runner = new TimingRunner(this, truncatedFirstWorkOrdinal: 1);

        var receipt = await new DurableTemplateTimingWorkflow(runner, () => true)
            .RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None);

        Assert.False(receipt.Succeeded);
        var failed = Assert.IsType<DurableTemplateTimingSample>(receipt.Samples[0].Sample);
        Assert.NotEmpty(failed.FailureCode);
        Assert.Contains(failed.Commands, command => command.Phase == DurableTemplateTimingCommandPhase.FirstDurableWorkTest && command.OutputTruncated);
        Assert.True(failed.CleanupComplete);
        Assert.DoesNotContain(SecretSentinel, File.ReadAllText(_reportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModeAndColdEndpointGuardsRejectBeforeLaunchingCommands()
    {
        var runner = new TimingRunner(this);
        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurableTemplateTimingWorkflow(runner, () => false).RunAsync(_input, DurableTemplateTimingMode.Primed, [], CancellationToken.None));
        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurableTemplateTimingWorkflow(runner, () => true).RunAsync(_input, (DurableTemplateTimingMode)42, [], CancellationToken.None));
        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurableTemplateTimingWorkflow(runner, () => true).RunAsync(_input, DurableTemplateTimingMode.Cold, ColdDockerHosts[..4], CancellationToken.None));
        await Assert.ThrowsAsync<PackageIndexException>(() =>
            new DurableTemplateTimingWorkflow(runner, () => true).RunAsync(_input, DurableTemplateTimingMode.Primed, [ColdDockerHosts[0]], CancellationToken.None));
        Assert.Empty(runner.Requests);
        Assert.False(File.Exists(_reportPath));
    }

    [Fact]
    public void HashCacheCoversExtractedFilesAndCopiesTheSameRootIndependentTree()
    {
        var left = TestPathUtils.PathUnder(_root, "cache-left");
        var right = TestPathUtils.PathUnder(_root, "cache-right");
        var leftPackage = TestPathUtils.PathUnder(left, "provider", "1.2.3", "provider.1.2.3.nupkg");
        var rightPackage = TestPathUtils.PathUnder(right, "provider", "1.2.3", "provider.1.2.3.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(leftPackage)!);
        Directory.CreateDirectory(Path.GetDirectoryName(rightPackage)!);
        File.WriteAllText(leftPackage, "same package");
        File.WriteAllText(rightPackage, "same package");
        var leftExtracted = TestPathUtils.PathUnder(left, "provider", "1.2.3", "lib", "net10.0", "provider.dll");
        var rightExtracted = TestPathUtils.PathUnder(right, "provider", "1.2.3", "lib", "net10.0", "provider.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(leftExtracted)!);
        Directory.CreateDirectory(Path.GetDirectoryName(rightExtracted)!);
        File.WriteAllText(leftExtracted, "same extracted assembly");
        File.WriteAllText(rightExtracted, "same extracted assembly");
        File.WriteAllText(TestPathUtils.PathUnder(left, ".nupkg.metadata"), "same metadata");
        File.WriteAllText(TestPathUtils.PathUnder(right, ".nupkg.metadata"), "same metadata");
        Directory.CreateDirectory(TestPathUtils.PathUnder(left, "empty-directory"));
        Directory.CreateDirectory(TestPathUtils.PathUnder(right, "empty-directory"));
        var hash = DurableTemplateTimingWorkflow.HashCache(left);
        Assert.Equal(hash, DurableTemplateTimingWorkflow.HashCache(right));

        var copy = TestPathUtils.PathUnder(_root, "cache-copy");
        DurableTemplateTimingWorkflow.CopyTree(left, copy);
        Assert.Equal(hash, DurableTemplateTimingWorkflow.HashCache(copy));

        File.WriteAllText(leftExtracted, "changed extracted assembly");
        Assert.NotEqual(hash, DurableTemplateTimingWorkflow.HashCache(left));
    }

    [Fact]
    public void HashCacheRejectsOversizedEntryBeforeReadingItsContents()
    {
        var cache = TestPathUtils.PathUnder(_root, "cache-with-oversized-entry");
        Directory.CreateDirectory(cache);
        var path = TestPathUtils.PathUnder(cache, "large-package-file");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(DurableTemplateTimingWorkflow.MaximumCacheEntryBytes + 1);

        Assert.Throws<PackageIndexException>(() => DurableTemplateTimingWorkflow.HashCache(cache));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CacheOperationsRejectMissingOrNonDirectoryRootBeforeChangingDestination(bool useNonDirectoryRoot)
    {
        var cache = TestPathUtils.PathUnder(_root, useNonDirectoryRoot ? "cache-root-file" : "missing-cache-root");
        const string sourceSentinel = "caller-owned cache file";
        if (useNonDirectoryRoot) File.WriteAllText(cache, sourceSentinel);

        var destination = TestPathUtils.PathUnder(_root, "existing-cache-copy");
        Directory.CreateDirectory(destination);
        var destinationSentinel = TestPathUtils.PathUnder(destination, "caller-owned.txt");
        File.WriteAllText(destinationSentinel, "preserve destination");

        var hashException = Assert.Throws<PackageIndexException>(() => DurableTemplateTimingWorkflow.HashCache(cache));
        Assert.Contains("NuGet cache root is missing or is not a directory", hashException.Message, StringComparison.Ordinal);

        var copyException = Assert.Throws<PackageIndexException>(() => DurableTemplateTimingWorkflow.CopyTree(cache, destination));
        Assert.Contains("NuGet cache root is missing or is not a directory", copyException.Message, StringComparison.Ordinal);
        Assert.Equal("preserve destination", File.ReadAllText(destinationSentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
        if (useNonDirectoryRoot)
            Assert.Equal(sourceSentinel, File.ReadAllText(cache));
        else
            Assert.False(File.Exists(cache));
    }

    [Fact]
    public void CacheOperationsRejectCumulativeBytesOverOneGiBBeforeCopyingOrChangingCallerData()
    {
        var cache = TestPathUtils.PathUnder(_root, "cache-over-one-gib");
        Directory.CreateDirectory(cache);
        var paths = Enumerable.Range(0, 5)
            .Select(index => TestPathUtils.PathUnder(cache, $"sparse-package-{index}.nupkg"))
            .ToArray();
        foreach (var path in paths)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.SetLength(DurableTemplateTimingWorkflow.MaximumCacheEntryBytes);
        }

        var lengths = paths.Select(path => new FileInfo(path).Length).ToArray();
        Assert.All(lengths, length => Assert.InRange(length, 0, DurableTemplateTimingWorkflow.MaximumCacheEntryBytes));
        Assert.True(lengths.Sum() > DurableTemplateTimingWorkflow.MaximumCacheTreeBytes);

        var destination = TestPathUtils.PathUnder(_root, "existing-cache-copy-over-one-gib");
        Directory.CreateDirectory(destination);
        var destinationSentinel = TestPathUtils.PathUnder(destination, "caller-owned.txt");
        File.WriteAllText(destinationSentinel, "preserve destination");

        var hashException = Assert.Throws<PackageIndexException>(() => DurableTemplateTimingWorkflow.HashCache(cache));
        Assert.Contains("bounded file-read policy", hashException.Message, StringComparison.Ordinal);

        var copyException = Assert.Throws<PackageIndexException>(() => DurableTemplateTimingWorkflow.CopyTree(cache, destination));
        Assert.Contains("bounded file-read policy", copyException.Message, StringComparison.Ordinal);
        Assert.Equal("preserve destination", File.ReadAllText(destinationSentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
        Assert.Equal(lengths, paths.Select(path => new FileInfo(path).Length).ToArray());
    }

    [Fact]
    public void CacheOperationsRejectMoreThanMaximumEntriesWithoutChangingSourceOrDestination()
    {
        var cache = TestPathUtils.PathUnder(_root, "cache-over-entry-limit");
        Directory.CreateDirectory(cache);
        for (var index = 0; index <= DurableTemplateTimingWorkflow.MaximumCacheTreeEntries; index++)
            Directory.CreateDirectory(TestPathUtils.PathUnder(cache, $"empty-directory-{index:D5}"));

        var expectedEntries = DurableTemplateTimingWorkflow.MaximumCacheTreeEntries + 1;
        Assert.Equal(expectedEntries, Directory.EnumerateFileSystemEntries(cache).Count());

        var destination = TestPathUtils.PathUnder(_root, "existing-cache-copy-over-entry-limit");
        Directory.CreateDirectory(destination);
        var destinationSentinel = TestPathUtils.PathUnder(destination, "caller-owned.txt");
        File.WriteAllText(destinationSentinel, "preserve destination");

        var hashException = Assert.Throws<PackageIndexException>(() => DurableTemplateTimingWorkflow.HashCache(cache));
        Assert.Contains("entry-count limit", hashException.Message, StringComparison.Ordinal);

        var copyException = Assert.Throws<PackageIndexException>(() => DurableTemplateTimingWorkflow.CopyTree(cache, destination));
        Assert.Contains("entry-count limit", copyException.Message, StringComparison.Ordinal);
        Assert.Equal(expectedEntries, Directory.EnumerateFileSystemEntries(cache).Count());
        Assert.Equal("preserve destination", File.ReadAllText(destinationSentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheOperationsRejectNullOrEmptySourcePathWithoutChangingDestination(bool useEmptyPath)
    {
        string? cache = useEmptyPath ? string.Empty : null;
        var destination = TestPathUtils.PathUnder(_root, "existing-cache-copy-for-invalid-source");
        Directory.CreateDirectory(destination);
        var destinationSentinel = TestPathUtils.PathUnder(destination, "caller-owned.txt");
        File.WriteAllText(destinationSentinel, "preserve destination");

        if (useEmptyPath)
        {
            Assert.Throws<ArgumentException>(() => DurableTemplateTimingWorkflow.HashCache(cache!));
            Assert.Throws<ArgumentException>(() => DurableTemplateTimingWorkflow.CopyTree(cache!, destination));
        }
        else
        {
            Assert.Throws<ArgumentNullException>(() => DurableTemplateTimingWorkflow.HashCache(cache!));
            Assert.Throws<ArgumentNullException>(() => DurableTemplateTimingWorkflow.CopyTree(cache!, destination));
        }

        Assert.Equal("preserve destination", File.ReadAllText(destinationSentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public void HashCacheRejectsNestedSymbolicLinkWithoutFollowingIt()
    {
        var cache = TestPathUtils.PathUnder(_root, "cache-with-link");
        var outside = TestPathUtils.PathUnder(_root, "cache-link-target");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(outside);
        var sentinel = TestPathUtils.PathUnder(outside, "sentinel.nupkg");
        File.WriteAllText(sentinel, "must remain untouched");
        var link = TestPathUtils.PathUnder(cache, "linked-cache-entry");
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            Assert.Throws<PackageIndexException>(() => DurableTemplateTimingWorkflow.HashCache(cache));
            Assert.Equal("must remain untouched", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void WriteReceiptRejectsOversizedRecord()
    {
        var path = TestPathUtils.PathUnder(_root, "too-large.json");
        var receipt = new DurableTemplateTimingProofReceipt(1, "series", "Primed", "CandidateLocal", "feed",
            SourceCommit, Version, new string('a', 64), "linux-x64", "linux", "10.0.100",
            DurableTemplateConsumerProof.PostgreSqlImage, [], [], null, null, false, false, new string('x', 70_000), Stopwatch.Frequency);
        Assert.Throws<PackageIndexException>(() => DurableTemplateTimingWorkflow.WriteReceipt(path, receipt));
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private IReadOnlyDictionary<string, string> CreateCandidateArtifacts()
    {
        var templatePath = TestPathUtils.PathUnder(_artifacts, $"{DurableTemplateStaging.PackageId}.{Version}.nupkg");
        var providerPath = TestPathUtils.PathUnder(_artifacts, $"{ProviderId}.{Version}.nupkg");
        var testingPath = TestPathUtils.PathUnder(_artifacts, $"{TestingId}.{Version}.nupkg");
        var contentRoot = TestPathUtils.PathUnder(_repositoryRoot, DurableTemplateStaging.ContentPath);
        CreateTemplateArchive(contentRoot, templatePath);
        File.WriteAllText(providerPath, "provider candidate");
        File.WriteAllText(testingPath, "testing candidate");
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DurableTemplateStaging.PackageId] = templatePath,
            [ProviderId] = providerPath,
            [TestingId] = testingPath
        };
        var entries = paths.Select(pair => new PackageArtifactManifestEntry(pair.Key, pair.Key + ".csproj", "publish",
            Path.GetFileName(pair.Value), PackageHash.ComputeSha512(pair.Value), false)).ToArray();
        File.WriteAllBytes(_manifestPath, JsonSerializer.SerializeToUtf8Bytes(
            new PackageArtifactManifest(1, Version, DateTimeOffset.UtcNow, entries), PackageArtifactJson.Options));
        return paths;
    }

    private static void CreateTemplateArchive(string contentRoot, string archivePath)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        WriteArchiveText(archive, "[Content_Types].xml", "<Types />");
        WriteArchiveText(archive, "_rels/.rels", "<Relationships />");
        WriteArchiveText(archive, "README.md", "Timing test fixture.");
        WriteArchiveText(archive, "LICENSE", "Test fixture.");
        WriteArchiveText(archive, DurableTemplateStaging.PackageId + ".nuspec",
            $"<package><metadata><id>{DurableTemplateStaging.PackageId}</id><version>{Version}</version><authors>Forge Trust</authors><description>Test candidate</description><packageTypes><packageType name=\"Template\" /></packageTypes></metadata></package>");
        WriteArchiveText(archive, $"package/services/metadata/core-properties/{Guid.NewGuid():N}.psmdcp", "<metadata />");
        foreach (var file in DurableTemplateStaging.EnumerateContent(contentRoot))
        {
            var relative = Path.GetRelativePath(contentRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var entry = archive.CreateEntry("content/durable-worker/" + relative, CompressionLevel.NoCompression);
            using var input = File.OpenRead(file);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static void WriteArchiveText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string CreateTestRoot()
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && (temporary.StartsWith("/var/", StringComparison.Ordinal) || temporary.StartsWith("/tmp/", StringComparison.Ordinal)))
            temporary = "/private" + temporary;
        return TestPathUtils.PathUnder(temporary, "durable-template-timing-workflow-tests", Guid.NewGuid().ToString("N"));
    }

    private sealed class TimingRunner(
        DurableTemplateTimingWorkflowTests fixture,
        int? failFirstWorkOrdinal = null,
        int? invalidDatabaseOrdinal = null,
        int? invalidReadyOrdinal = null,
        string? failOperation = null,
        int? markerFaultOrdinal = null,
        string? markerFault = null,
        int? truncatedFirstWorkOrdinal = null,
        int? wrongImageOrdinal = null,
        int? emptyDaemonOrdinal = null,
        int? templateRemainsInstalledOrdinal = null,
        Action<ExternalCommandRequest>? afterRequest = null) : IExternalCommandRunner
    {
        private readonly Dictionary<string, int> _imageObservations = new(StringComparer.Ordinal);
        private readonly HashSet<string> _privateRoots = new(StringComparer.Ordinal);
        private int _activeCommands;
        private int _activeCommandsAtFirstCleanup = -1;

        internal List<ExternalCommandRequest> Requests { get; } = [];
        internal IReadOnlyCollection<string> PrivateRoots => _privateRoots;
        internal IReadOnlyList<string> DockerHosts => Requests
            .Where(request => request.FileName == "docker"
                && request.Arguments.SequenceEqual(["info", "--format", "{{.ID}}"], StringComparer.Ordinal))
            .Select(request => request.Environment?.GetValueOrDefault("DOCKER_HOST"))
            .Where(host => host is not null)
            .Select(host => host!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        internal int FirstWorkCount => Requests.Count(request => request.FileName == "dotnet"
            && request.OperationName == nameof(DurableTemplateTimingCommandPhase.FirstDurableWorkTest));
        internal int ActiveCommands => Volatile.Read(ref _activeCommands);
        internal int ActiveCommandsAtFirstCleanup => Volatile.Read(ref _activeCommandsAtFirstCleanup);

        public async Task<ExternalCommandResult> RunAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            CapturePrivateRoots(request.Environment);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.FileName == "dotnet" && StartsWith(request, "new", "uninstall")
                && ParseSampleOrdinal(request.WorkingDirectory) == 1)
                Interlocked.CompareExchange(ref _activeCommandsAtFirstCleanup, Volatile.Read(ref _activeCommands), -1);
            afterRequest?.Invoke(request);
            if (OperationKey(request) == failOperation) return new(1, string.Empty, SecretSentinel);
            if (request.FileName == "git") return Ok(SourceCommit + Environment.NewLine);
            if (request.FileName == "docker") return RunDocker(request);
            if (request.FileName != "dotnet") throw new InvalidOperationException("Unexpected fake executable.");
            if (request.Arguments.SequenceEqual(["--version"], StringComparer.Ordinal)) return Ok("10.0.100\r\n");
            if (StartsWith(request, "new", "install") || StartsWith(request, "new", "uninstall")) return Ok();
            if (StartsWith(request, "new", "list"))
                return Ok(templateRemainsInstalledOrdinal == ParseSampleOrdinal(request.WorkingDirectory)
                    ? DurableTemplateArtifactContract.ShortName
                    : string.Empty);
            if (request.Arguments.Count >= 2 && request.Arguments[0] == "new"
                && request.Arguments[1] == DurableTemplateArtifactContract.ShortName)
            {
                var generated = TestPathUtils.PathUnder(request.WorkingDirectory, "FirstDurableWorker");
                MaterializeGeneratedTree(fixture, generated);
                RestoreGraph(fixture, generated, Assert.IsType<string>(request.Environment!["NUGET_PACKAGES"]));
                return Ok();
            }
            if (request.Arguments.Count > 0 && request.Arguments[0] == "restore")
            {
                var generated = TestPathUtils.PathUnder(request.WorkingDirectory, "FirstDurableWorker");
                RestoreGraph(fixture, generated, Assert.IsType<string>(request.Environment!["NUGET_PACKAGES"]));
                return Ok();
            }
            if (request.Arguments.Count > 0 && request.Arguments[0] == "test") return await RunFirstWorkAsync(request, cancellationToken);
            throw new InvalidOperationException($"Unexpected fake dotnet command: {string.Join(' ', request.Arguments)}");
        }

        private ExternalCommandResult RunDocker(ExternalCommandRequest request)
        {
            if (request.Arguments.Count > 0 && request.Arguments[0] == "pull") return Ok();
            if (request.Arguments.SequenceEqual(["info", "--format", "{{.ID}}"], StringComparer.Ordinal))
            {
                if (emptyDaemonOrdinal == ParseSampleOrdinal(request.WorkingDirectory)) return Ok();
                var endpoint = request.Environment?.GetValueOrDefault("DOCKER_HOST") ?? "primed-daemon";
                return Ok("owned-daemon-" + endpoint + Environment.NewLine);
            }
            if (request.Arguments.Count > 1 && request.Arguments[0] == "image" && request.Arguments[1] == "inspect")
            {
                var endpoint = request.Environment?.GetValueOrDefault("DOCKER_HOST") ?? "primed-daemon";
                var observations = _imageObservations.GetValueOrDefault(endpoint) + 1;
                _imageObservations[endpoint] = observations;
                if (request.Environment?.ContainsKey("DOCKER_HOST") == true && observations == 1)
                    return new(1, string.Empty, "Error: No such image: postgres candidate");
                var digest = DurableTemplateConsumerProof.PostgreSqlImage.Split('@', 2)[1];
                var ordinal = ParseSampleOrdinal(request.WorkingDirectory);
                if (wrongImageOrdinal == ordinal && observations == ordinal * 2) digest = new string('0', digest.Length);
                return Ok("[\"postgres@" + digest + "\"]");
            }
            throw new InvalidOperationException("Unexpected fake Docker command.");
        }

        private async Task<ExternalCommandResult> RunFirstWorkAsync(ExternalCommandRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _activeCommands);
            try
            {
                var root = request.WorkingDirectory;
                var generated = TestPathUtils.PathUnder(root, "FirstDurableWorker");
                MaterializeGeneratedTree(fixture, generated);
                RestoreGraph(fixture, generated, Assert.IsType<string>(request.Environment!["NUGET_PACKAGES"]));
                var ordinal = ParseSampleOrdinal(root);
                var ticks = invalidReadyOrdinal == ordinal ? 1 : Stopwatch.GetTimestamp();
                var readyFile = Assert.IsType<string>(request.Environment["APPSURFACE_TEMPLATE_TIMING_READY_FILE"]);
                File.WriteAllText(readyFile, ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (invalidReadyOrdinal == ordinal) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                await Task.Delay(12, cancellationToken);

                var checkpoints = new[]
                {
                    "[first-work] authorized activation accepted",
                    "[first-work] Work reached terminal completion",
                    "[first-work] readiness: NotStarted -> Healthy",
                    "[first-work] exported appsurface.durable.runtime.activation"
                };
                var output = string.Join(Environment.NewLine, checkpoints)
                    + Environment.NewLine + $"[timing-fixture] started-ticks={ticks}"
                    + Environment.NewLine + $"[timing-fixture] database-sha256={(invalidDatabaseOrdinal == ordinal ? "invalid" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("timing-db-" + ordinal))))}"
                    + Environment.NewLine + "[first-work-cleanup] elapsed-ms=1" + Environment.NewLine;
                var fault = markerFaultOrdinal == ordinal ? markerFault : null;
                if (fault == "missing-started-ticks") output = output.Replace($"[timing-fixture] started-ticks={ticks}{Environment.NewLine}", string.Empty, StringComparison.Ordinal);
                if (fault == "duplicate-database-sha256") output += $"[timing-fixture] database-sha256={Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("duplicate")))}{Environment.NewLine}";
                if (fault == "missing-cleanup-marker") output = output.Replace("[first-work-cleanup] elapsed-ms=1" + Environment.NewLine, string.Empty, StringComparison.Ordinal);
                return new(failFirstWorkOrdinal == ordinal ? 1 : 0, output, SecretSentinel,
                    StandardOutputTruncated: truncatedFirstWorkOrdinal == ordinal);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCommands);
            }
        }

        private void CapturePrivateRoots(IReadOnlyDictionary<string, string?>? environment)
        {
            if (environment is null) return;
            foreach (var key in new[] { "NUGET_PACKAGES", "DOTNET_CLI_HOME", "NUGET_HTTP_CACHE_PATH" })
                if (environment.TryGetValue(key, out var path) && path is not null) _privateRoots.Add(path);
        }

        private static bool Is(ExternalCommandRequest request, params string[] arguments)
            => request.Arguments.SequenceEqual(arguments, StringComparer.Ordinal);

        private static bool StartsWith(ExternalCommandRequest request, string first, string second)
            => request.Arguments.Count >= 2 && request.Arguments[0] == first && request.Arguments[1] == second;

        private static int ParseSampleOrdinal(string root)
            => int.TryParse(Path.GetFileName(root).AsSpan("sample-".Length), out var ordinal) ? ordinal : 0;

        private static string OperationKey(ExternalCommandRequest request)
        {
            var action = request.FileName switch
            {
                "git" => "source",
                "docker" when request.Arguments.Count > 0 && request.Arguments[0] == "pull" => "pull",
                "docker" when request.Arguments.Count > 0 && request.Arguments[0] == "info" => "daemon",
                "docker" when request.Arguments.Count > 1 && request.Arguments[0] == "image" => "image-inspect",
                "dotnet" when request.Arguments.SequenceEqual(["--version"], StringComparer.Ordinal) => "sdk",
                "dotnet" when request.Arguments.Count > 1 && request.Arguments[0] == "new" && request.Arguments[1] == "install" => "install",
                "dotnet" when request.Arguments.Count > 1 && request.Arguments[0] == "new" && request.Arguments[1] == "uninstall" => "uninstall",
                "dotnet" when request.Arguments.Count > 1 && request.Arguments[0] == "new" && request.Arguments[1] == "list" => "list",
                "dotnet" when request.Arguments.Count > 1 && request.Arguments[0] == "new" => "create",
                "dotnet" when request.Arguments.Count > 0 && request.Arguments[0] == "restore" => "restore",
                "dotnet" when request.Arguments.Count > 0 && request.Arguments[0] == "test" => "first-work",
                _ => "unknown"
            };
            var ordinal = ParseSampleOrdinal(request.WorkingDirectory);
            var scope = ordinal == 0 ? "setup" : $"sample-{ordinal}";
            return $"{scope}-{action}";
        }

        private static ExternalCommandResult Ok(string output = "") => new(0, output, SecretSentinel);
    }

    private static void MaterializeGeneratedTree(DurableTemplateTimingWorkflowTests fixture, string generatedRoot)
    {
        var sourceRoot = TestPathUtils.PathUnder(fixture._repositoryRoot, DurableTemplateStaging.ContentPath);
        foreach (var source in DurableTemplateStaging.EnumerateContent(sourceRoot))
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            if (relative.Split(Path.DirectorySeparatorChar).Contains(".template.config", StringComparer.OrdinalIgnoreCase)) continue;
            var generatedRelative = relative.Replace(DurableTemplateArtifactContract.SourceName, "FirstDurableWorker", StringComparison.Ordinal);
            var output = TestPathUtils.PathUnder(generatedRoot, generatedRelative.Split(Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(source));
            File.WriteAllBytes(output, new UTF8Encoding(false, true).GetBytes(
                text.Replace(DurableTemplateArtifactContract.SourceName, "FirstDurableWorker", StringComparison.Ordinal)));
        }
    }

    private static void RestoreGraph(DurableTemplateTimingWorkflowTests fixture, string generatedRoot, string cache)
    {
        var libraries = new Dictionary<string, object>
        {
            ["FirstDurableWorker/1.0.0"] = new { type = "project" },
            ["AppSurfaceDurableWorker/1.0.0"] = new { type = "project" },
            [$"{ProviderId}/{Version}"] = new { type = "package" },
            [$"{TestingId}/{Version}"] = new { type = "package" }
        };
        var assets = JsonSerializer.SerializeToUtf8Bytes(new { libraries });
        foreach (var project in Directory.EnumerateFiles(generatedRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var output = TestPathUtils.PathUnder(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, assets);
        }
        foreach (var packageId in new[] { ProviderId, TestingId })
        {
            var versionRoot = TestPathUtils.PathUnder(cache, packageId.ToLowerInvariant(), Version.ToLowerInvariant());
            Directory.CreateDirectory(versionRoot);
            var packagePath = TestPathUtils.PathUnder(versionRoot,
                $"{packageId.ToLowerInvariant()}.{Version.ToLowerInvariant()}.nupkg");
            if (!File.Exists(packagePath)) File.Copy(fixture._artifactPaths[packageId], packagePath);
        }
    }
}
