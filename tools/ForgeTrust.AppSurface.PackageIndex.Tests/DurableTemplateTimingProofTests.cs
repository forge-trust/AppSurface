using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class DurableTemplateTimingProofTests
{
    private const string SourceCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PackageVersion = "0.2.0-preview.13";
    private const long StopwatchFrequency = 1_000;
    private const string PrivateFeed = "candidate-local-feed";
    private const string RunnerImage = "ubuntu-latest";
    private const string RuntimeIdentifier = "linux-x64";
    private const string SdkVersion = "10.0.100";
    private const string PrimedCacheClosure = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SecretArgumentSentinel = "argument-secret-sentinel";

    private static readonly DurableTemplateTimingArtifact[] Artifacts =
    [
        new(DurableTemplateStaging.PackageId, PackageVersion, new string('0', 128)),
        new("ForgeTrust.AppSurface.Durable.PostgreSql", PackageVersion, new string('1', 128))
    ];

    [Fact]
    public void PrimedFiveSampleSeriesUsesMedianAndEnforcesInclusiveSetupCap()
    {
        var receipt = DurableTemplateTimingProof.Evaluate(
            Request(DurableTemplateTimingMode.Primed),
            Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130], setupSeconds: 55));

        Assert.True(receipt.Succeeded);
        Assert.True(receipt.PerformanceGatePassed);
        Assert.Equal(115, receipt.MedianSeconds);
        Assert.Equal(130, receipt.NearestRankP95Seconds);
        Assert.Equal(5, receipt.Samples.Count);
        Assert.All(receipt.Samples, sample => Assert.Equal(55, sample.Sample!.PackageSetupRestoreBuildSeconds));
    }

    [Fact]
    public void SetupRestoreBuildOver55SecondsInvalidatesPrimedSample()
    {
        var receipt = DurableTemplateTimingProof.Evaluate(
            Request(DurableTemplateTimingMode.Primed),
            Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130], setupSeconds: 55.001));

        Assert.False(receipt.Succeeded);
        Assert.False(receipt.PerformanceGatePassed);
        Assert.Contains("primed-setup-restore-build-limit", receipt.Samples[0].ValidationFailures);
        Assert.Equal(5, receipt.Samples.Count);
    }

    [Fact]
    public void MedianExactly120SecondsFailsStrictPrimedLimit()
    {
        var receipt = DurableTemplateTimingProof.Evaluate(
            Request(DurableTemplateTimingMode.Primed),
            Samples(DurableTemplateTimingMode.Primed, [100, 119, 120, 121, 130]));

        Assert.Equal(120, receipt.MedianSeconds);
        Assert.False(receipt.PerformanceGatePassed);
        Assert.False(receipt.Succeeded);
        Assert.Contains("primed-gate-missed", receipt.FailureCode);
    }

    [Fact]
    public void PrimedRootExactly180SecondsFailsAndRemainsInP95()
    {
        var receipt = DurableTemplateTimingProof.Evaluate(
            Request(DurableTemplateTimingMode.Primed),
            Samples(DurableTemplateTimingMode.Primed, [100, 110, 120, 130, 180]));

        Assert.Equal(120, receipt.MedianSeconds);
        Assert.Equal(180, receipt.NearestRankP95Seconds);
        Assert.False(receipt.PerformanceGatePassed);
        Assert.Contains("primed-root-limit", receipt.Samples[4].ValidationFailures);
        Assert.Equal(5, receipt.Samples.Count);
    }

    [Fact]
    public void ColdFiveSampleSeriesUsesNearestRankP95AndHasNoPerformanceSlo()
    {
        var receipt = DurableTemplateTimingProof.Evaluate(
            Request(DurableTemplateTimingMode.Cold),
            Samples(DurableTemplateTimingMode.Cold, [800, 830, 850, 880, 899]));

        Assert.True(receipt.Succeeded);
        Assert.Null(receipt.PerformanceGatePassed);
        Assert.Equal(850, receipt.MedianSeconds);
        Assert.Equal(899, receipt.NearestRankP95Seconds);
        Assert.Equal(5, receipt.Samples.Count);
    }

    [Fact]
    public void ColdRootExactly900SecondsTripsWatchdogWithoutDroppingAttempt()
    {
        var receipt = DurableTemplateTimingProof.Evaluate(
            Request(DurableTemplateTimingMode.Cold),
            Samples(DurableTemplateTimingMode.Cold, [800, 830, 850, 880, 900]));

        Assert.False(receipt.Succeeded);
        Assert.Null(receipt.PerformanceGatePassed);
        Assert.Equal(900, receipt.NearestRankP95Seconds);
        Assert.Equal(5, receipt.Samples.Count);
        Assert.Contains("cold-root-watchdog", receipt.Samples[4].ValidationFailures);
    }

    [Fact]
    public void FailedSampleInvalidatesWholeSeriesAndIsIncludedInStatistics()
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [90, 100, 110, 130, 150]);
        samples[2] = samples[2] with { Succeeded = false, FailureCode = "first-work-failed" };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        Assert.False(receipt.Succeeded);
        Assert.Equal(110, receipt.MedianSeconds);
        Assert.Equal(150, receipt.NearestRankP95Seconds);
        Assert.Equal(5, receipt.Samples.Count);
        Assert.Contains("sample-failed", receipt.Samples[2].ValidationFailures);
        Assert.Equal("first-work-failed", receipt.Samples[2].Sample!.FailureCode);
    }

    [Fact]
    public void IncompleteSeriesRetainsEverySuppliedSampleAndCannotPass()
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 120, 130]).ToArray();

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        Assert.False(receipt.Succeeded);
        Assert.False(receipt.PerformanceGatePassed);
        Assert.Null(receipt.MedianSeconds);
        Assert.Null(receipt.NearestRankP95Seconds);
        Assert.Equal(4, receipt.Samples.Count);
        Assert.Contains("incomplete-series", receipt.FailureCode);
    }

    [Fact]
    public void NullSampleIsRetainedAsMalformedEvidence()
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[2] = null!;

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        Assert.False(receipt.Succeeded);
        Assert.Null(receipt.Samples[2].Sample);
        Assert.Null(receipt.Samples[2].ElapsedSeconds);
        Assert.Contains("missing-sample", receipt.Samples[2].ValidationFailures);
    }

    [Fact]
    public void WrongSourceAndCommandArgumentHashRejectWithoutRetainingRawArguments()
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[1] = samples[1] with
        {
            SourceCommit = new string('c', 40),
            Commands = [samples[1].Commands[0] with { ArgumentsSha256 = Hash("not-the-approved-arguments") }, .. samples[1].Commands.Skip(1)]
        };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);
        var json = JsonSerializer.Serialize(receipt);

        Assert.False(receipt.Succeeded);
        Assert.Contains("source-revision", receipt.Samples[1].ValidationFailures);
        Assert.Contains("command-TemplateInstall", receipt.Samples[1].ValidationFailures);
        Assert.DoesNotContain(SecretArgumentSentinel, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Arguments\"", json, StringComparison.Ordinal);
        Assert.All(receipt.Samples.Where(result => result.Sample is not null), result =>
            Assert.All(result.Sample!.Commands, command => Assert.Matches("^[0-9a-f]{64}$", command.ArgumentsSha256)));
    }

    [Theory]
    [InlineData("ordinal", "sample-order")]
    [InlineData("series", "series-id")]
    [InlineData("mode", "mode")]
    [InlineData("undefined-mode", "mode")]
    [InlineData("feed-kind", "mode")]
    [InlineData("undefined-feed-kind", "mode")]
    [InlineData("feed-identity", "feed-identity")]
    [InlineData("version", "package-version")]
    [InlineData("artifact-set", "artifact-set")]
    [InlineData("missing-artifact-set", "artifact-set")]
    [InlineData("runtime", "runtime-identifier")]
    [InlineData("runner", "runner-image")]
    [InlineData("unsupported-sdk", "sdk-version")]
    [InlineData("missing-sdk", "sdk-version")]
    public void SampleProvenanceDriftInvalidatesTheSeriesWithoutDroppingTheAttempt(string fault, string failureCode)
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[0] = fault switch
        {
            "ordinal" => samples[0] with { Ordinal = 0 },
            "series" => samples[0] with { SeriesId = "different-series" },
            "mode" => samples[0] with { Mode = DurableTemplateTimingMode.Cold },
            "undefined-mode" => samples[0] with { Mode = (DurableTemplateTimingMode)42 },
            "feed-kind" => samples[0] with { FeedKind = DurableTemplateTimingFeedKind.PromotedPublic },
            "undefined-feed-kind" => samples[0] with { FeedKind = (DurableTemplateTimingFeedKind)42 },
            "feed-identity" => samples[0] with { FeedIdentity = "different-feed" },
            "version" => samples[0] with { PackageVersion = "0.2.0-preview.12" },
            "artifact-set" => samples[0] with { ArtifactSetSha256 = Hash("different-artifact-set") },
            "missing-artifact-set" => samples[0] with { ArtifactSetSha256 = null! },
            "runtime" => samples[0] with { RuntimeIdentifier = "linux-arm64" },
            "runner" => samples[0] with { RunnerImage = "ubuntu-24.04" },
            "unsupported-sdk" => samples[0] with { SdkVersion = "9.0.100" },
            "missing-sdk" => samples[0] with { SdkVersion = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        AssertRetainedRejectedSample(samples, receipt, 0, failureCode);
    }

    [Theory]
    [InlineData("cache-root", null, "cache-root-identity")]
    [InlineData("cache-root", "not-a-sha256", "cache-root-identity")]
    [InlineData("project", null, "project-identity")]
    [InlineData("project", "not-a-sha256", "project-identity")]
    [InlineData("database", null, "database-identity")]
    [InlineData("database", "not-a-sha256", "database-identity")]
    [InlineData("daemon", null, "docker-identity")]
    [InlineData("daemon", "not-a-sha256", "docker-identity")]
    [InlineData("cache-closure", null, "cache-closure-hash")]
    [InlineData("cache-closure", "not-a-sha256", "cache-closure-hash")]
    public void SampleResourceIdentitiesRequireHashEvidence(string identity, string? value, string failureCode)
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[0] = identity switch
        {
            "cache-root" => samples[0] with { PackageCacheRootIdentity = value! },
            "project" => samples[0] with { ProjectIdentity = value! },
            "database" => samples[0] with { DatabaseIdentity = value! },
            "daemon" => samples[0] with { DockerDaemonIdentity = value! },
            "cache-closure" => samples[0] with { PackageCacheClosureSha256 = value! },
            _ => throw new ArgumentOutOfRangeException(nameof(identity))
        };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        AssertRetainedRejectedSample(samples, receipt, 0, failureCode);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidSetupDurationCannotBecomeSuccessfulTimingEvidence(double setupSeconds)
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[0] = samples[0] with { PackageSetupRestoreBuildSeconds = setupSeconds };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        AssertRetainedRejectedSample(samples, receipt, 0, "setup-restore-build-duration");
    }

    [Fact]
    public void SharedCachePurgeInvalidatesAnOtherwisePassingSample()
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[0] = samples[0] with { SharedCachePurged = true };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        Assert.True(receipt.PerformanceGatePassed);
        AssertRetainedRejectedSample(samples, receipt, 0, "shared-cache-purged");
    }

    [Fact]
    public void OverlappingSampleClockRangesCannotClaimASerialSeries()
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        var duration = samples[1].EndTimestamp - samples[1].StartTimestamp;
        var overlappingStart = samples[0].EndTimestamp - 1;
        samples[1] = samples[1] with { StartTimestamp = overlappingStart, EndTimestamp = overlappingStart + duration };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        AssertRetainedRejectedSample(samples, receipt, 1, "samples-overlap");
    }

    [Fact]
    public void ColdSamplesRequireDistinctDaemonAndPrivateRoots()
    {
        var samples = Samples(DurableTemplateTimingMode.Cold, [800, 830, 850, 880, 899]);
        samples[4] = samples[4] with { DockerDaemonIdentity = samples[0].DockerDaemonIdentity };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Cold), samples);

        Assert.False(receipt.Succeeded);
        Assert.Contains("cold-daemon-reused", receipt.Samples[4].ValidationFailures);
        Assert.Equal(5, receipt.Samples.Count);
    }

    [Fact]
    public void FailedCommandAndCheckpointAreNotConvertedToSuccessfulTimingEvidence()
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[0] = samples[0] with
        {
            Commands = [samples[0].Commands[0], samples[0].Commands[1], samples[0].Commands[2] with { ExitCode = 1 }],
            Checkpoints = samples[0].Checkpoints with { ExportedActivity = false }
        };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        Assert.False(receipt.Succeeded);
        Assert.Contains("command-FirstDurableWorkTest", receipt.Samples[0].ValidationFailures);
        Assert.Contains("first-work-checkpoints", receipt.Samples[0].ValidationFailures);
        Assert.Equal(5, receipt.Samples.Count);
    }

    [Theory]
    [InlineData(true, "contradictory-success")]
    [InlineData(false, "unsafe failure code")]
    public void FailureCodeMustMatchSampleOutcome(bool succeeded, string failureCode)
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[0] = samples[0] with { Succeeded = succeeded, FailureCode = failureCode };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        Assert.Contains("failure-code", receipt.Samples[0].ValidationFailures);
    }

    [Fact]
    public void CommandDurationsCannotExceedTheMeasuredSampleRoot()
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[0] = samples[0] with
        {
            Commands = samples[0].Commands.Select(command => command with { ElapsedSeconds = 50 }).ToArray()
        };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        Assert.Contains("command-duration-exceeds-root", receipt.Samples[0].ValidationFailures);
    }

    [Theory]
    [InlineData("negative-start")]
    [InlineData("non-increasing-range")]
    [InlineData("double-precision-loss")]
    public void InvalidOrUnrepresentableClockRangesCannotProduceElapsedEvidence(string fault)
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[0] = fault switch
        {
            "negative-start" => samples[0] with { StartTimestamp = -1 },
            "non-increasing-range" => samples[0] with { EndTimestamp = samples[0].StartTimestamp },
            "double-precision-loss" => samples[0] with { StartTimestamp = long.MaxValue - 1, EndTimestamp = long.MaxValue },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        Assert.Null(receipt.Samples[0].ElapsedSeconds);
        Assert.Contains("root-duration", receipt.Samples[0].ValidationFailures);
    }

    [Theory]
    [InlineData("primed-image")]
    [InlineData("primed-cache")]
    [InlineData("cold-image")]
    [InlineData("cold-cache")]
    public void ImageAndCacheObservationsMustMatchTheRequestedMode(string fault)
    {
        var mode = fault.StartsWith("cold-", StringComparison.Ordinal) ? DurableTemplateTimingMode.Cold : DurableTemplateTimingMode.Primed;
        var samples = Samples(mode, mode == DurableTemplateTimingMode.Cold ? [800, 830, 850, 880, 899] : [100, 110, 115, 125, 130]);
        samples[0] = fault switch
        {
            "primed-image" => samples[0] with { ImageDigestAfter = new string('9', 64) },
            "primed-cache" => samples[0] with { PackageCacheEmptyAtStart = true },
            "cold-image" => samples[0] with { DockerImagePresentAtStart = true },
            "cold-cache" => samples[0] with { HttpCacheEmptyAtStart = false },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(mode), samples);
        var expectedFailure = mode == DurableTemplateTimingMode.Primed
            ? fault == "primed-image" ? "primed-image" : "primed-cache-closure"
            : fault == "cold-image" ? "cold-image-state" : "cold-cache-state";

        Assert.Contains(expectedFailure, receipt.Samples[0].ValidationFailures);
    }

    [Theory]
    [InlineData("cache", "cache-root-reused")]
    [InlineData("project", "project-reused")]
    [InlineData("database", "database-reused")]
    public void OwnedSampleIdentitiesMustBeUnique(string identity, string failureCode)
    {
        var samples = Samples(DurableTemplateTimingMode.Cold, [800, 830, 850, 880, 899]);
        samples[4] = identity switch
        {
            "cache" => samples[4] with { PackageCacheRootIdentity = samples[0].PackageCacheRootIdentity },
            "project" => samples[4] with { ProjectIdentity = samples[0].ProjectIdentity },
            "database" => samples[4] with { DatabaseIdentity = samples[0].DatabaseIdentity },
            _ => throw new ArgumentOutOfRangeException(nameof(identity))
        };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Cold), samples);

        Assert.Contains(failureCode, receipt.Samples[4].ValidationFailures);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null-entry")]
    public void MissingOrNullCommandEvidenceFailsClosed(string fault)
    {
        var samples = Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]);
        samples[0] = samples[0] with
        {
            Commands = fault == "missing"
                ? []
                : [samples[0].Commands[0], null!, samples[0].Commands[2]]
        };

        var receipt = DurableTemplateTimingProof.Evaluate(Request(DurableTemplateTimingMode.Primed), samples);

        Assert.Contains(fault == "missing" ? "command-sequence" : "command-ProjectCreate", receipt.Samples[0].ValidationFailures);
    }

    [Theory]
    [InlineData("series")]
    [InlineData("mode")]
    [InlineData("feed-kind")]
    [InlineData("feed-identity")]
    [InlineData("source")]
    [InlineData("version")]
    [InlineData("runtime")]
    [InlineData("sdk")]
    [InlineData("image")]
    [InlineData("clock")]
    [InlineData("null-artifacts")]
    [InlineData("empty-artifacts")]
    [InlineData("missing-provider")]
    [InlineData("missing-primed-cache")]
    [InlineData("cold-cache-claim")]
    public void InvalidRequestProvenanceAndPolicyInputsFailClosed(string fault)
    {
        var mode = fault == "cold-cache-claim" ? DurableTemplateTimingMode.Cold : DurableTemplateTimingMode.Primed;
        var request = Request(mode);
        request = fault switch
        {
            "series" => request with { SeriesId = "bad/series" },
            "mode" => request with { Mode = (DurableTemplateTimingMode)42 },
            "feed-kind" => request with { FeedKind = (DurableTemplateTimingFeedKind)42 },
            "feed-identity" => request with { FeedIdentity = "https://feed.invalid" },
            "source" => request with { SourceCommit = "short" },
            "version" => request with { PackageVersion = "latest" },
            "runtime" => request with { RuntimeIdentifier = "linux x64" },
            "sdk" => request with { SdkVersion = "9.0.100" },
            "image" => request with { PostgreSqlImage = "postgres:latest" },
            "clock" => request with { StopwatchFrequency = 0 },
            "null-artifacts" => request with { Artifacts = null! },
            "empty-artifacts" => request with { Artifacts = [] },
            "missing-provider" => request with { Artifacts = [Artifacts[0]] },
            "missing-primed-cache" => request with { PinnedPackageCacheClosureSha256 = null },
            "cold-cache-claim" => request with { PinnedPackageCacheClosureSha256 = PrimedCacheClosure },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        Assert.Throws<PackageIndexException>(() => DurableTemplateTimingProof.Evaluate(
            request,
            Samples(mode, [100, 110, 115, 125, 130])));
    }

    [Theory]
    [InlineData("null-entry")]
    [InlineData("wrong-version")]
    [InlineData("duplicate-id")]
    [InlineData("unsafe-id")]
    public void MalformedArtifactEntriesFailClosed(string fault)
    {
        DurableTemplateTimingArtifact[] artifacts = fault switch
        {
            "null-entry" => [null!],
            "wrong-version" => [Artifacts[0] with { Version = "1.2.3" }, Artifacts[1]],
            "duplicate-id" => [Artifacts[0], Artifacts[0]],
            "unsafe-id" => [Artifacts[0] with { PackageId = "../template" }, Artifacts[1]],
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        Assert.Throws<PackageIndexException>(() => DurableTemplateTimingProof.Evaluate(
            Request(DurableTemplateTimingMode.Primed) with { Artifacts = artifacts },
            Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130])));
    }

    [Fact]
    public void CommandHashHelperReturnsOnlyHashForArbitraryInputTokens()
    {
        var hash = DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", [SecretArgumentSentinel]);

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.DoesNotContain(SecretArgumentSentinel, hash, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptRoundTripRetainsClockFrequencyAndCanonicalEnumNames()
    {
        var receipt = DurableTemplateTimingProof.Evaluate(
            Request(DurableTemplateTimingMode.Primed),
            Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130]));

        var json = JsonSerializer.Serialize(receipt, PackageArtifactJson.Options);
        var restored = JsonSerializer.Deserialize<DurableTemplateTimingProofReceipt>(json, PackageArtifactJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(StopwatchFrequency, restored.StopwatchFrequency);
        Assert.Equal("Primed", restored.Mode);
        Assert.Equal("CandidateLocal", restored.FeedKind);
    }

    [Theory]
    [InlineData("base64-is-not-the-package-hash-format")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ArtifactSha512RequiresLowercaseHex128WithoutBoundaryConversion(string sha512)
    {
        var artifacts = new[] { Artifacts[0] with { Sha512 = sha512 }, Artifacts[1] };

        Assert.Throws<PackageIndexException>(() => DurableTemplateTimingProof.Evaluate(
            Request(DurableTemplateTimingMode.Primed) with { Artifacts = artifacts },
            Samples(DurableTemplateTimingMode.Primed, [100, 110, 115, 125, 130])));
    }

    internal static DurableTemplateTimingProofRequest Request(
        DurableTemplateTimingMode mode,
        IReadOnlyList<DurableTemplateTimingArtifact>? expectedArtifacts = null) => new(
        "series-20261004-a",
        mode,
        DurableTemplateTimingFeedKind.CandidateLocal,
        PrivateFeed,
        SourceCommit,
        PackageVersion,
        RuntimeIdentifier,
        RunnerImage,
        SdkVersion,
        DurableTemplateConsumerProof.PostgreSqlImage,
        mode == DurableTemplateTimingMode.Primed ? PrimedCacheClosure : null,
        StopwatchFrequency,
        expectedArtifacts ?? Artifacts);

    internal static List<DurableTemplateTimingSample> Samples(
        DurableTemplateTimingMode mode,
        IReadOnlyList<double> durations,
        double setupSeconds = 45,
        IReadOnlyList<DurableTemplateTimingArtifact>? expectedArtifacts = null)
    {
        var artifacts = expectedArtifacts ?? Artifacts;
        var artifactSetHash = DurableTemplateTimingProof.ComputeArtifactSetSha256(artifacts);
        var digest = DurableTemplateConsumerProof.PostgreSqlImage.Split('@', 2)[1];
        var emptyCacheHash = Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));
        var result = new List<DurableTemplateTimingSample>(durations.Count);
        long clock = 0;
        for (var index = 0; index < durations.Count; index++)
        {
            var milliseconds = checked((long)(durations[index] * StopwatchFrequency));
            var start = clock;
            var end = start + milliseconds;
            clock = end + StopwatchFrequency;
            var daemonIdentity = mode == DurableTemplateTimingMode.Cold ? Hash($"daemon-{index}") : Hash("primed-daemon");
            var packageCacheHash = mode == DurableTemplateTimingMode.Primed ? PrimedCacheClosure : emptyCacheHash;
            result.Add(new DurableTemplateTimingSample(
                index + 1,
                $"sample-{index + 1}",
                "series-20261004-a",
                mode,
                DurableTemplateTimingFeedKind.CandidateLocal,
                PrivateFeed,
                SourceCommit,
                PackageVersion,
                artifactSetHash,
                RuntimeIdentifier,
                RunnerImage,
                SdkVersion,
                daemonIdentity,
                mode == DurableTemplateTimingMode.Primed,
                mode == DurableTemplateTimingMode.Primed ? digest : string.Empty,
                digest,
                Hash($"cache-{index}"),
                packageCacheHash,
                mode == DurableTemplateTimingMode.Cold,
                true,
                false,
                Hash($"project-{index}"),
                Hash($"database-{index}"),
                start,
                end,
                setupSeconds,
                Commands(),
                new(true, true, true, true),
                true,
                true,
                string.Empty));
        }

        return result;
    }

    private static DurableTemplateTimingCommand[] Commands() =>
    [
        new(DurableTemplateTimingCommandPhase.TemplateInstall,
            DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", ["new", "install", $"{DurableTemplateStaging.PackageId}@{PackageVersion}"]),
            10, 0, false),
        new(DurableTemplateTimingCommandPhase.ProjectCreate,
            DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", ["new", "appsurface-durable-worker", "-n", "FirstDurableWorker"]),
            10, 0, false),
        new(DurableTemplateTimingCommandPhase.FirstDurableWorkTest,
            DurableTemplateTimingProof.ComputeCommandArgumentsSha256("dotnet", ["test", "FirstDurableWorker", "--filter", "FullyQualifiedName~FirstDurableWork", "--logger", "console;verbosity=detailed"]),
            20, 0, false)
    ];

    private static void AssertRetainedRejectedSample(
        IReadOnlyList<DurableTemplateTimingSample> samples,
        DurableTemplateTimingProofReceipt receipt,
        int rejectedIndex,
        string failureCode)
    {
        Assert.False(receipt.Succeeded);
        Assert.Equal(5, receipt.Samples.Count);
        Assert.Same(samples[rejectedIndex], receipt.Samples[rejectedIndex].Sample);
        Assert.Contains(failureCode, receipt.Samples[rejectedIndex].ValidationFailures);
        Assert.Contains(failureCode, receipt.FailureCode.Split(','));
        Assert.All(receipt.Samples.Where((_, index) => index != rejectedIndex), result =>
            Assert.Empty(result.ValidationFailures));
        Assert.Equal(115, receipt.MedianSeconds);
        Assert.Equal(130, receipt.NearestRankP95Seconds);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
