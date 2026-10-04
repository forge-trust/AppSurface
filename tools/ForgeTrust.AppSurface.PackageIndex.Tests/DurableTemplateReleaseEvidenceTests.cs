using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateReleaseEvidenceTests : IDisposable
{
    private const string Version = "0.2.0-preview.806";
    private static readonly string Source = new('a', 40);
    private const string ProviderId = "ForgeTrust.AppSurface.Durable.PostgreSql";
    private readonly string _root = TestPathUtils.PathUnder(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "template-receipt-tests", Guid.NewGuid().ToString("N"));
    private readonly PackageArtifactManifest _manifest;
    private readonly DurableTemplateProofReceipt _receipt;

    public DurableTemplateReleaseEvidenceTests()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Join(_root, "Fixture.0.2.0-preview.806.nupkg");
        File.WriteAllText(archive, "exact producer bytes");
        var hash = PackageHash.ComputeSha512(archive);
        _manifest = new(1, Version, DateTimeOffset.UtcNow, [new("Fixture", "Fixture.csproj", "publish", Path.GetFileName(archive), hash, false)]);
        _receipt = new(1, Source, Version, "linux-x64", "10.0.102", DurableTemplateConsumerProof.PostgreSqlImage,
            "candidate-local-feed-correctness", [new("Fixture", Version, hash)], CompletePhases(true),
            true, true, true, true, true, true, true, true, true, true, "", "", new string('b', 64),
            new("/native/bin", "/native/bin/initdb", "/native/bin/postgres", "/native/bin/pg_ctl", "/native/bin/psql", "16.5", 16, "16.5", 160005,
                new Dictionary<string, string> { ["initdb"] = new string('c', 64), ["postgres"] = new string('d', 64), ["pg_ctl"] = new string('e', 64), ["psql"] = new string('f', 64) }), true, "ubuntu24/20261004.1");
    }

    [Fact]
    public void ThreeSuccessfulOsReceiptsCannotSubstituteForRequiredTiming()
    {
        WriteAll();
        // Missing timing evidence cannot be replaced by successful OS proofs.
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.Require(_root, Source, _manifest, _root));
    }

    [Fact]
    public void RequireAcceptsCompleteOsAndTimingEvidenceBoundToTheSameCandidate()
    {
        var artifactDirectory = Path.Join(_root, "candidate");
        Directory.CreateDirectory(artifactDirectory);
        var templateArchive = Path.Join(artifactDirectory, $"{DurableTemplateStaging.PackageId}.{Version}.nupkg");
        var providerArchive = Path.Join(artifactDirectory, $"{ProviderId}.{Version}.nupkg");
        CreateTemplateArchive(templateArchive);
        File.WriteAllText(providerArchive, "exact provider candidate bytes");
        var entries = new[]
        {
            new PackageArtifactManifestEntry(DurableTemplateStaging.PackageId, "Durable.Templates.csproj", "publish",
                Path.GetFileName(templateArchive), PackageHash.ComputeSha512(templateArchive), false),
            new PackageArtifactManifestEntry(ProviderId, "Durable.PostgreSql.csproj", "publish",
                Path.GetFileName(providerArchive), PackageHash.ComputeSha512(providerArchive), false)
        };
        var manifest = new PackageArtifactManifest(1, Version, DateTimeOffset.UtcNow, entries);
        var osReceipt = _receipt with
        {
            Artifacts = entries.Select(entry => new DurableTemplateProofArtifact(entry.PackageId, Version, entry.Sha512)).ToArray(),
            GeneratedContentSha256 = DurableTemplateArtifactContract.ComputeGeneratedContentSha256(templateArchive, "FirstDurableWorker")
        };
        foreach (var rid in new[] { "linux-x64", "osx-arm64", "win-x64" })
            DurableTemplateConsumerProof.WriteReceipt(Path.Join(_root, rid + ".json"),
                osReceipt with { RuntimeIdentifier = rid, Phases = CompletePhases(rid == "linux-x64") });
        foreach (var mode in new[] { DurableTemplateTimingMode.Primed, DurableTemplateTimingMode.Cold })
        {
            var (_, timingReceipt) = TimingFixture(mode, manifest);
            DurableTemplateTimingWorkflow.WriteReceipt(Path.Join(_root,
                mode == DurableTemplateTimingMode.Primed ? "timing-primed.json" : "timing-cold.json"), timingReceipt);
        }

        DurableTemplateReleaseEvidence.Require(_root, Source, manifest, artifactDirectory);
    }

    [Fact]
    public void RequireRejectsMalformedTimingReceiptAfterAllOsReceiptsPass()
    {
        WriteAll();
        File.WriteAllText(Path.Join(_root, "timing-primed.json"), "{\"SchemaVersion\":1,\"SchemaVersion\":1}");

        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.Require(_root, Source, _manifest, _root));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("version")]
    [InlineData("rid")]
    [InlineData("image")]
    [InlineData("mode")]
    [InlineData("failure")]
    [InlineData("cleanup")]
    [InlineData("path")]
    [InlineData("feed")]
    [InlineData("authored")]
    [InlineData("native")]
    [InlineData("replacement")]
    [InlineData("activation")]
    [InlineData("terminal")]
    [InlineData("readiness")]
    [InlineData("export")]
    [InlineData("phase")]
    [InlineData("truncated")]
    [InlineData("duration")]
    [InlineData("hash")]
    [InlineData("empty-sdk")]
    [InlineData("unsupported-sdk")]
    [InlineData("missing-runner-image")]
    [InlineData("unsafe-runner-image")]
    [InlineData("missing-phase")]
    [InlineData("phase-order")]
    [InlineData("missing-artifact")]
    [InlineData("wrong-artifact")]
    [InlineData("changed-archive")]
    [InlineData("duplicate-artifact")]
    public void FailedIncompleteOrSubstitutedEvidenceRejects(string fault)
    {
        var receipt = fault switch
        {
            "source" => _receipt with { SourceCommit = new string('c', 40) },
            "version" => _receipt with { PackageVersion = "1.2.3" },
            "rid" => _receipt with { RuntimeIdentifier = "osx-arm64" },
            "image" => _receipt with { Image = "postgres:latest" },
            "mode" => _receipt with { Mode = "public" },
            "failure" => _receipt with { Succeeded = false },
            "cleanup" => _receipt with { CleanupComplete = false },
            "path" => _receipt with { ExactArchiveInstall = false },
            "feed" => _receipt with { FeedInstall = false },
            "authored" => _receipt with { AuthoredSource = false },
            "native" => _receipt with { NativeSmoke = false },
            "replacement" => _receipt with { SampleReplacement = false },
            "activation" => _receipt with { AuthorizedActivation = false },
            "terminal" => _receipt with { TerminalWork = false },
            "readiness" => _receipt with { ReadinessTransition = false },
            "export" => _receipt with { ExportedActivity = false },
            "phase" => _receipt with { Phases = [new("restore", 1, 1, false)] },
            "truncated" => _receipt with { Phases = [new("first-work", 1, 0, true)] },
            "duration" => _receipt with { Phases = [new("first-work", -1, 0, false)] },
            "hash" => _receipt with { GeneratedContentSha256 = "wrong" },
            "empty-sdk" => _receipt with { SdkVersion = "" },
            "unsupported-sdk" => _receipt with { SdkVersion = "9.0.100" },
            "missing-runner-image" => _receipt with { RunnerImage = "" },
            "unsafe-runner-image" => _receipt with { RunnerImage = "image\nrevision" },
            "missing-phase" => _receipt with { Phases = _receipt.Phases.Skip(1).ToArray() },
            "phase-order" => _receipt with { Phases = _receipt.Phases.Reverse().ToArray() },
            "missing-artifact" => _receipt with { Artifacts = [] },
            "wrong-artifact" => _receipt with { Artifacts = [new("Fixture", Version, "substituted")] },
            "duplicate-artifact" => _receipt with { Artifacts = [_receipt.Artifacts[0], _receipt.Artifacts[0]] },
            _ => _receipt
        };
        if (fault == "changed-archive") File.AppendAllText(TestPathUtils.PathUnder(_root, _manifest.Entries[0].ArtifactFileName), "changed");
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.Validate(receipt, Source, _manifest, _root, "linux-x64", true));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1}")]
    [InlineData("{\"unknown\":true}")]
    public void TimingReaderRejectsMalformedDuplicateUnknownAndEmptyRecords(string json)
    {
        var path = Path.Join(_root, "timing.json");
        File.WriteAllText(path, json);
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.ReadTiming(path));
    }

    [Fact]
    public void TimingReaderRequiresBoundedExistingFile()
    {
        var path = Path.Join(_root, "timing.json");
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.ReadTiming(path));
        File.WriteAllText(path, new string(' ', 64 * 1024 + 1));
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.ReadTiming(path));
    }

    [Theory]
    [InlineData("[native-cleanup] elapsed-ms=0\n", 20000)]
    [InlineData("[native-cleanup] elapsed-ms=19999\r\n", 1)]
    public void NativeCleanupSharesOnlyRemainingTotalBudget(string output, int expected)
        => Assert.Equal(expected, DurableTemplateCommand.ReadCleanupRemaining(output));

    [Theory]
    [InlineData("")]
    [InlineData("[native-cleanup] elapsed-ms=20000\n")]
    [InlineData("[native-cleanup] elapsed-ms=9999999999999\n")]
    [InlineData("[native-cleanup] elapsed-ms=1\n[native-cleanup] elapsed-ms=1\n")]
    public void NativeCleanupMissingAmbiguousOrExhaustedObservationFails(string output)
        => Assert.Throws<PackageIndexException>(() => DurableTemplateCommand.ReadCleanupRemaining(output));

    [Fact]
    public void NativeOnlyMacProofCannotReplaceLinuxFirstWork()
    {
        var mac = _receipt with
        {
            RuntimeIdentifier = "osx-arm64",
            AuthorizedActivation = false,
            TerminalWork = false,
            ReadinessTransition = false,
            ExportedActivity = false,
            Phases = CompletePhases(false)
        };
        DurableTemplateReleaseEvidence.Validate(mac, Source, _manifest, _root, "osx-arm64", false);
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.Validate(mac, Source, _manifest, _root, "linux-x64", true));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "invalid")]
    [InlineData("missing", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MissingTrustedInputsFailClosed(string? directory, string? source)
        => Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.Require(directory, source, _manifest, _root));

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1}")]
    [InlineData("{\"unexpected\":true}")]
    public void MalformedEmptyDuplicateAndUnknownJsonRejects(string json)
    {
        var path = Path.Join(_root, "bad.json");
        File.WriteAllText(path, json);
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.Read(path));
    }

    [Fact]
    public void MissingOrOversizeReceiptRejects()
    {
        var path = Path.Join(_root, "bad.json");
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.Read(path));
        File.WriteAllText(path, new string(' ', 64 * 1024 + 1));
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.Read(path));
    }

    [Fact]
    public void SafeReceiptRoundTripsWithoutArbitraryOutput()
    {
        var path = Path.Join(_root, "safe.json");
        DurableTemplateConsumerProof.WriteReceipt(path, _receipt);
        var receipt = DurableTemplateReleaseEvidence.Read(path);
        Assert.Equal(Source, receipt.SourceCommit);
        Assert.Equal(_receipt.Artifacts[0], Assert.Single(receipt.Artifacts));
        Assert.DoesNotContain("credential", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandParserResolvesExactInputsAndRejectsAmbiguity()
    {
        var args = new[] { "--package-version", Version, "--source-commit", Source, "--artifacts-input", "archives",
            "--artifact-manifest", "archives/manifest.json", "--report", "receipt.json" };
        var parsed = DurableTemplateCommandOptions.Parse(args, _root);
        Assert.Equal(Path.Join(_root, "archives"), parsed.Artifacts);
        Assert.Null(parsed.NativePgBin);
        Assert.Throws<PackageIndexException>(() => DurableTemplateCommandOptions.Parse([.. args, "--report", "other"], _root));
        Assert.Throws<PackageIndexException>(() => DurableTemplateCommandOptions.Parse([.. args, "--unknown", "x"], _root));
        Assert.Throws<PackageIndexException>(() => DurableTemplateCommandOptions.Parse(["--report"], _root));
        Assert.Throws<PackageIndexException>(() => DurableTemplateCommandOptions.Parse([], _root));
    }

    [Fact]
    public void HashExcludesBuildOutputButBindsAuthoredPathsAndBytes()
    {
        var content = Path.Join(_root, "content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Join(content, "one.cs"), "one");
        var initial = DurableTemplateConsumerProof.HashGeneratedContent(content);
        Directory.CreateDirectory(Path.Join(content, "bin"));
        File.WriteAllText(Path.Join(content, "bin", "output.dll"), "build");
        Assert.Equal(initial, DurableTemplateConsumerProof.HashGeneratedContent(content));
        File.WriteAllText(Path.Join(content, "one.cs"), "two");
        Assert.NotEqual(initial, DurableTemplateConsumerProof.HashGeneratedContent(content));
    }

    [Fact]
    public void TimingPublisherRecomputesSuccessAndSummaryFromAllFiveSamples()
    {
        var (manifest, receipt) = TimingFixture(DurableTemplateTimingMode.Primed);
        var path = Path.Join(_root, "timing.json");
        DurableTemplateTimingWorkflow.WriteReceipt(path, receipt);
        var read = DurableTemplateReleaseEvidence.ReadTiming(path);
        DurableTemplateReleaseEvidence.ValidateTiming(read, Source, manifest, DurableTemplateTimingMode.Primed);
        Assert.Equal(1000, read.StopwatchFrequency);
        Assert.Equal(100, read.MedianSeconds);
        Assert.Equal(5, read.Samples.Count);
    }

    [Fact]
    public void ColdDiagnosticsRequireFiveIndependentDaemonsWithoutPerformanceSlo()
    {
        var (manifest, receipt) = TimingFixture(DurableTemplateTimingMode.Cold);
        DurableTemplateReleaseEvidence.ValidateTiming(receipt, Source, manifest, DurableTemplateTimingMode.Cold);
        Assert.Null(receipt.PerformanceGatePassed);
        var samples = receipt.Samples.ToArray();
        samples[1] = samples[1] with { Sample = samples[1].Sample! with { DockerDaemonIdentity = samples[0].Sample!.DockerDaemonIdentity } };
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.ValidateTiming(receipt with { Samples = samples }, Source, manifest, DurableTemplateTimingMode.Cold));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("version")]
    [InlineData("mode")]
    [InlineData("feed")]
    [InlineData("identity")]
    [InlineData("rid")]
    [InlineData("image")]
    [InlineData("frequency")]
    [InlineData("failed")]
    [InlineData("failure-code")]
    [InlineData("missing-sample")]
    [InlineData("null-sample")]
    [InlineData("artifact-hash")]
    [InlineData("missing-artifact")]
    [InlineData("replaced-artifact")]
    [InlineData("median")]
    [InlineData("p95")]
    [InlineData("gate")]
    [InlineData("sample-duration")]
    [InlineData("sample-failure")]
    [InlineData("sample-checkpoint")]
    public void TimingEvidenceRejectsStaleSubstitutedAndForgedSummaries(string fault)
    {
        var (manifest, receipt) = TimingFixture(DurableTemplateTimingMode.Primed);
        var samples = receipt.Samples.ToArray();
        receipt = fault switch
        {
            "source" => receipt with { SourceCommit = new string('b', 40) },
            "version" => receipt with { PackageVersion = "1.2.3" },
            "mode" => receipt with { Mode = "Cold" },
            "feed" => receipt with { FeedKind = "PromotedPublic" },
            "identity" => receipt with { FeedIdentity = "other" },
            "rid" => receipt with { RuntimeIdentifier = "osx-arm64" },
            "image" => receipt with { PostgreSqlImage = "postgres:latest" },
            "frequency" => receipt with { StopwatchFrequency = 0 },
            "failed" => receipt with { Succeeded = false },
            "failure-code" => receipt with { FailureCode = "failed" },
            "missing-sample" => receipt with { Samples = samples.Skip(1).ToArray() },
            "null-sample" => receipt with { Samples = [samples[0] with { Sample = null }, .. samples.Skip(1)] },
            "artifact-hash" => receipt with { ArtifactSetSha256 = new string('d', 64) },
            "missing-artifact" => receipt with { Artifacts = receipt.Artifacts.Skip(1).ToArray() },
            "replaced-artifact" => receipt with { Artifacts = [receipt.Artifacts[0] with { Sha512 = new string('b', 128) }, receipt.Artifacts[1]] },
            "median" => receipt with { MedianSeconds = 1 },
            "p95" => receipt with { NearestRankP95Seconds = 1 },
            "gate" => receipt with { PerformanceGatePassed = false },
            _ => receipt
        };
        if (fault == "sample-duration") samples[0] = samples[0] with { ElapsedSeconds = 1 };
        if (fault == "sample-failure") samples[0] = samples[0] with { ValidationFailures = ["ignored-failure"] };
        if (fault == "sample-checkpoint") samples[0] = samples[0] with { Sample = samples[0].Sample! with { Checkpoints = new(false, true, true, true) } };
        if (fault.StartsWith("sample-", StringComparison.Ordinal)) receipt = receipt with { Samples = samples };
        Assert.Throws<PackageIndexException>(() => DurableTemplateReleaseEvidence.ValidateTiming(receipt, Source, manifest, DurableTemplateTimingMode.Primed));
    }

    private static (PackageArtifactManifest Manifest, DurableTemplateTimingProofReceipt Receipt) TimingFixture(
        DurableTemplateTimingMode mode, PackageArtifactManifest? candidateManifest = null)
    {
        var defaultArtifacts = new DurableTemplateTimingArtifact[]
        {
            new(DurableTemplateStaging.PackageId, Version, new string('a', 128)),
            new(ProviderId, Version, new string('b', 128))
        };
        var manifest = candidateManifest ?? new PackageArtifactManifest(1, Version, DateTimeOffset.UtcNow,
            defaultArtifacts.Select(item => new PackageArtifactManifestEntry(item.PackageId, item.PackageId + ".csproj", "publish", item.PackageId + "." + Version + ".nupkg", item.Sha512, false)).ToArray());
        var artifacts = manifest.Entries.Select(entry => new DurableTemplateTimingArtifact(entry.PackageId, manifest.PackageVersion, entry.Sha512)).ToArray();
        var cache = new string('c', 64);
        var request = new DurableTemplateTimingProofRequest("release-series", mode, DurableTemplateTimingFeedKind.CandidateLocal,
            "candidate-local-feed", Source, Version, "linux-x64", "ubuntu-latest", "10.0.102", DurableTemplateConsumerProof.PostgreSqlImage,
            mode == DurableTemplateTimingMode.Primed ? cache : null, 1000, artifacts);
        var digest = DurableTemplateConsumerProof.PostgreSqlImage.Split('@')[1];
        var samples = Enumerable.Range(1, 5).Select(index => new DurableTemplateTimingSample(index, "sample-" + index, request.SeriesId,
            mode, request.FeedKind, request.FeedIdentity, Source, Version, DurableTemplateTimingProof.ComputeArtifactSetSha256(artifacts),
            request.RuntimeIdentifier, request.RunnerImage, request.SdkVersion, new string((char)('0' + index), 64),
            mode == DurableTemplateTimingMode.Primed, mode == DurableTemplateTimingMode.Primed ? digest : "", digest,
            new string((char)('0' + index), 64), mode == DurableTemplateTimingMode.Primed ? cache : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())),
            mode == DurableTemplateTimingMode.Cold, true, false, new string((char)('0' + index), 64), new string((char)('1' + index), 64),
            index * 110000L, index * 110000L + 100000L, 45,
            [new(DurableTemplateTimingCommandPhase.TemplateInstall, DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", ["new", "install", $"{DurableTemplateStaging.PackageId}@{Version}"]), 10, 0, false),
             new(DurableTemplateTimingCommandPhase.ProjectCreate, DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", ["new", DurableTemplateArtifactContract.ShortName, "-n", "FirstDurableWorker"]), 10, 0, false),
             new(DurableTemplateTimingCommandPhase.FirstDurableWorkTest, DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", ["test", "FirstDurableWorker", "--filter", "FullyQualifiedName~FirstDurableWork", "--logger", "console;verbosity=detailed"]), 20, 0, false)],
            new(true, true, true, true), true, true, "")).ToArray();
        return (manifest, DurableTemplateTimingProof.Evaluate(request, samples));
    }

    private static void CreateTemplateArchive(string archivePath)
    {
        var repositoryRoot = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
        var contentRoot = TestPathUtils.PathUnder(repositoryRoot, DurableTemplateStaging.ContentPath);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        WriteArchiveText(archive, "[Content_Types].xml", "<Types />");
        WriteArchiveText(archive, "_rels/.rels", "<Relationships />");
        WriteArchiveText(archive, "README.md", "Release evidence test fixture.");
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
            using var reader = new StreamReader(input, new UTF8Encoding(false, true));
            var candidateText = reader.ReadToEnd().Replace("0.2.0-preview.13", Version, StringComparison.Ordinal);
            var candidateBytes = new UTF8Encoding(false, true).GetBytes(candidateText);
            output.Write(candidateBytes);
        }
    }

    private static void WriteArchiveText(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private void WriteAll()
    {
        foreach (var rid in new[] { "linux-x64", "osx-arm64", "win-x64" })
            DurableTemplateConsumerProof.WriteReceipt(Path.Join(_root, rid + ".json"), _receipt with { RuntimeIdentifier = rid, Phases = CompletePhases(rid == "linux-x64") });
    }

    private static DurableTemplateProofPhase[] CompletePhases(bool firstWork)
    {
        var ids = new List<string>
        {
            "sdk", "archive-install", "acquisition-create", "acquisition-restore", "uninstall", "absence",
            "authored-restore", "authored-build", "authored-format", "authored-tests",
            "archive-install", "archive-discover", "archive-create", "archive-restore", "archive-build", "archive-format", "archive-tests", "uninstall", "absence",
            "feed-install", "feed-discover", "feed-create", "feed-restore", "feed-build", "feed-format", "feed-tests"
        };
        if (firstWork) ids.Add("first-work");
        ids.AddRange(["replacement-build", "replacement-tests"]);
        if (firstWork) ids.Add("replacement-first-work");
        ids.AddRange(["uninstall", "absence"]);
        return ids.Select(id => new DurableTemplateProofPhase(id, 1, 0, false)).ToArray();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
