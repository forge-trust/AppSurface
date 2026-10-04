using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Identifies the required cache state for a timing series.</summary>
internal enum DurableTemplateTimingMode
{
    /// <summary>Uses the verified pinned package closure and PostgreSQL image.</summary>
    Primed,

    /// <summary>Starts with empty NuGet caches and an absent image on a fresh isolated daemon.</summary>
    Cold
}

/// <summary>Identifies the package source used by the exact install/new/test workload.</summary>
internal enum DurableTemplateTimingFeedKind
{
    /// <summary>Candidate artifacts are installed from the workflow's local candidate feed.</summary>
    CandidateLocal,

    /// <summary>The exact promoted version is installed from the public NuGet feed.</summary>
    PromotedPublic
}

/// <summary>Immutable package identity bound to one timing series.</summary>
/// <param name="PackageId">First-party package ID.</param>
/// <param name="Version">Exact coordinated version.</param>
/// <param name="Sha512">Lowercase hexadecimal SHA-512 digest of the exact package archive (128 characters).</param>
internal sealed record DurableTemplateTimingArtifact(string PackageId, string Version, string Sha512);

/// <summary>Expected source, package, cache, feed, and runner identity for one five-sample series.</summary>
/// <param name="SeriesId">Safe identifier unique to this complete series; corrected reruns use a new value.</param>
/// <param name="Mode">Primed or cold cache protocol.</param>
/// <param name="FeedKind">Candidate-local or promoted-public feed.</param>
/// <param name="FeedIdentity">Opaque safe feed identifier; never a URL containing credentials.</param>
/// <param name="SourceCommit">Full source revision bound by the workflow.</param>
/// <param name="PackageVersion">Exact coordinated template and package version.</param>
/// <param name="RuntimeIdentifier">Runner runtime identifier.</param>
/// <param name="RunnerImage">Workflow runner image identity.</param>
/// <param name="SdkVersion">Observed .NET 10 SDK version.</param>
/// <param name="PostgreSqlImage">Exact pinned PostgreSQL image reference.</param>
/// <param name="PinnedPackageCacheClosureSha256">Required primed package-cache inventory hash; null for cold.</param>
/// <param name="StopwatchFrequency">Monotonic timestamp frequency used for all sample timestamps.</param>
/// <param name="Artifacts">Complete first-party candidate/promoted artifact identities, without file paths.</param>
internal sealed record DurableTemplateTimingProofRequest(
    string SeriesId,
    DurableTemplateTimingMode Mode,
    DurableTemplateTimingFeedKind FeedKind,
    string FeedIdentity,
    string SourceCommit,
    string PackageVersion,
    string RuntimeIdentifier,
    string RunnerImage,
    string SdkVersion,
    string PostgreSqlImage,
    string? PinnedPackageCacheClosureSha256,
    long StopwatchFrequency,
    IReadOnlyList<DurableTemplateTimingArtifact> Artifacts);

/// <summary>One exact dotnet command observation, retaining only the argument-vector hash.</summary>
/// <param name="Phase">Fixed command phase; arbitrary phase text is not accepted.</param>
/// <param name="ArgumentsSha256">SHA-256 over the normalized executable and exact argument vector.</param>
/// <param name="ElapsedSeconds">Monotonic command duration.</param>
/// <param name="ExitCode">Observed child exit code.</param>
/// <param name="OutputTruncated">Whether either bounded output stream was truncated.</param>
internal sealed record DurableTemplateTimingCommand(
    DurableTemplateTimingCommandPhase Phase,
    string ArgumentsSha256,
    double ElapsedSeconds,
    int ExitCode,
    bool OutputTruncated);

/// <summary>One phase in the fixed three-command timing workload.</summary>
internal enum DurableTemplateTimingCommandPhase
{
    /// <summary>Install the exact template package and version with dotnet new.</summary>
    TemplateInstall,

    /// <summary>Create FirstDurableWorker with the documented short name.</summary>
    ProjectCreate,

    /// <summary>Run the documented FirstDurableWork test and detailed logger.</summary>
    FirstDurableWorkTest
}

/// <summary>Checkpoint assertions emitted only after the real generated first-Work test proves each condition.</summary>
/// <param name="AuthorizedActivation">Authorized empty activation was accepted.</param>
/// <param name="TerminalWork">Persisted Work reached terminal completion.</param>
/// <param name="ReadinessTransition">Readiness changed from NotStarted to Healthy.</param>
/// <param name="ExportedActivity">The activation activity was exported by the SDK.</param>
internal sealed record DurableTemplateTimingCheckpoints(
    bool AuthorizedActivation,
    bool TerminalWork,
    bool ReadinessTransition,
    bool ExportedActivity);

/// <summary>Safe, source-bound observation from one serial install/create/test sample.</summary>
/// <param name="Ordinal">One-based position in the five-sample serial run.</param>
/// <param name="SampleId">Unique safe sample identity.</param>
/// <param name="SeriesId">Series identity matching the request.</param>
/// <param name="Mode">Cache mode matching the request.</param>
/// <param name="FeedKind">Feed kind matching the request.</param>
/// <param name="FeedIdentity">Opaque feed identity matching the request.</param>
/// <param name="SourceCommit">Full source revision observed for this sample.</param>
/// <param name="PackageVersion">Exact coordinated package version observed for this sample.</param>
/// <param name="ArtifactSetSha256">Stable hash of the complete package ID/version/SHA-512 set.</param>
/// <param name="RuntimeIdentifier">Runner runtime identifier observed for this sample.</param>
/// <param name="RunnerImage">Runner image identity observed for this sample.</param>
/// <param name="SdkVersion">.NET SDK version observed for this sample.</param>
/// <param name="DockerDaemonIdentity">SHA-256 identity of the Docker daemon used by this sample.</param>
/// <param name="DockerImagePresentAtStart">Whether the pinned image existed before the sample clock started.</param>
/// <param name="ImageDigestAtStart">Observed pinned image digest before the clock, empty when absent.</param>
/// <param name="ImageDigestAfter">Observed pinned image digest after the proof.</param>
/// <param name="PackageCacheRootIdentity">Opaque SHA-256 identity of this sample's private package cache root.</param>
/// <param name="PackageCacheClosureSha256">Hash of the package-cache inventory before the sample clock.</param>
/// <param name="PackageCacheEmptyAtStart">Whether the private global-packages cache was empty at start.</param>
/// <param name="HttpCacheEmptyAtStart">Whether the private NuGet HTTP cache was empty at start.</param>
/// <param name="SharedCachePurged">Must remain false; shared caches are never purged by timing evidence.</param>
/// <param name="ProjectIdentity">Opaque SHA-256 identity of the generated project root.</param>
/// <param name="DatabaseIdentity">Opaque SHA-256 identity of the disposable database.</param>
/// <param name="StartTimestamp">Monotonic sample start timestamp.</param>
/// <param name="EndTimestamp">Monotonic sample end timestamp, after owned cleanup.</param>
/// <param name="PackageSetupRestoreBuildSeconds">Measured install/create/restore/build group duration.</param>
/// <param name="Commands">The three documented command observations in exact order.</param>
/// <param name="Checkpoints">Four first-Work assertions from the final test command.</param>
/// <param name="CleanupComplete">Whether every sample-owned resource was cleaned up.</param>
/// <param name="Succeeded">Whether the workload and proof assertions completed successfully.</param>
/// <param name="FailureCode">Empty on success; otherwise a short safe code without diagnostics or secrets.</param>
internal sealed record DurableTemplateTimingSample(
    int Ordinal,
    string SampleId,
    string SeriesId,
    DurableTemplateTimingMode Mode,
    DurableTemplateTimingFeedKind FeedKind,
    string FeedIdentity,
    string SourceCommit,
    string PackageVersion,
    string ArtifactSetSha256,
    string RuntimeIdentifier,
    string RunnerImage,
    string SdkVersion,
    string DockerDaemonIdentity,
    bool DockerImagePresentAtStart,
    string ImageDigestAtStart,
    string ImageDigestAfter,
    string PackageCacheRootIdentity,
    string PackageCacheClosureSha256,
    bool PackageCacheEmptyAtStart,
    bool HttpCacheEmptyAtStart,
    bool SharedCachePurged,
    string ProjectIdentity,
    string DatabaseIdentity,
    long StartTimestamp,
    long EndTimestamp,
    double PackageSetupRestoreBuildSeconds,
    IReadOnlyList<DurableTemplateTimingCommand> Commands,
    DurableTemplateTimingCheckpoints Checkpoints,
    bool CleanupComplete,
    bool Succeeded,
    string FailureCode);

/// <summary>One retained attempt and its fixed safe validation codes.</summary>
/// <param name="Sample">Original bounded observation, including failed attempts.</param>
/// <param name="ElapsedSeconds">Elapsed monotonic wall time, or null for invalid timestamps.</param>
/// <param name="ValidationFailures">Stable failure codes; never raw command output or exception text.</param>
internal sealed record DurableTemplateTimingSampleResult(
    DurableTemplateTimingSample? Sample,
    double? ElapsedSeconds,
    IReadOnlyList<string> ValidationFailures);

/// <summary>Safe evaluation of a complete five-sample timing series.</summary>
/// <param name="SchemaVersion">Receipt schema version.</param>
/// <param name="SeriesId">Unique series identity.</param>
/// <param name="Mode">Cache mode used.</param>
/// <param name="FeedKind">Candidate-local or promoted-public feed.</param>
/// <param name="FeedIdentity">Opaque feed identity, without credentials or raw URL.</param>
/// <param name="SourceCommit">Full source revision bound by every sample.</param>
/// <param name="PackageVersion">Exact coordinated package version.</param>
/// <param name="ArtifactSetSha256">Stable hash of all artifact identities.</param>
/// <param name="RuntimeIdentifier">Runner runtime identifier.</param>
/// <param name="RunnerImage">Workflow runner image identity.</param>
/// <param name="SdkVersion">Observed .NET 10 SDK version.</param>
/// <param name="PostgreSqlImage">Pinned PostgreSQL image reference.</param>
/// <param name="Artifacts">Exact artifact IDs, versions, and SHA-512 hashes.</param>
/// <param name="Samples">Every supplied attempt, including failed and malformed observations.</param>
/// <param name="MedianSeconds">Third sorted elapsed time when five valid durations exist.</param>
/// <param name="NearestRankP95Seconds">Fifth sorted elapsed time when five valid durations exist.</param>
/// <param name="PerformanceGatePassed">Primed numerical gate result; null for cold diagnostic series.</param>
/// <param name="Succeeded">True only for five valid samples and all applicable proof gates.</param>
/// <param name="FailureCode">Stable summary code; never raw diagnostics.</param>
/// <param name="StopwatchFrequency">Monotonic clock frequency used to re-evaluate sample timestamps.</param>
internal sealed record DurableTemplateTimingProofReceipt(
    int SchemaVersion,
    string SeriesId,
    string Mode,
    string FeedKind,
    string FeedIdentity,
    string SourceCommit,
    string PackageVersion,
    string ArtifactSetSha256,
    string RuntimeIdentifier,
    string RunnerImage,
    string SdkVersion,
    string PostgreSqlImage,
    IReadOnlyList<DurableTemplateTimingArtifact> Artifacts,
    IReadOnlyList<DurableTemplateTimingSampleResult> Samples,
    double? MedianSeconds,
    double? NearestRankP95Seconds,
    bool? PerformanceGatePassed,
    bool Succeeded,
    string FailureCode,
    long StopwatchFrequency = 0);

/// <summary>
/// Validates the five serial timing observations collected by the release workflow. It never launches a command,
/// provisions a daemon, purges a cache, or writes a receipt; the workflow owns those actions and supplies safe records.
/// </summary>
internal static class DurableTemplateTimingProof
{
    /// <summary>Required number of independent serial samples in each series.</summary>
    internal const int RequiredSampleCount = 5;

    /// <summary>Inclusive cap for install/create/restore/build setup duration in the primed series.</summary>
    internal const double PrimedSetupRestoreBuildLimitSeconds = 55;

    /// <summary>Strict upper bound for the primed median duration.</summary>
    internal const double PrimedMedianLimitSeconds = 120;

    /// <summary>Strict upper bound for each primed root duration.</summary>
    internal const double PrimedRootLimitSeconds = 180;

    /// <summary>Cold diagnostic root watchdog; reaching this value fails the sample.</summary>
    internal const double ColdRootWatchdogSeconds = 900;

    private static readonly Regex CommitPattern = new(@"\A[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SdkPattern = new(@"\A10\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?\z", RegexOptions.CultureInvariant);
    private static readonly string EmptyCacheInventorySha256 =
        Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));

    /// <summary>
    /// Evaluates one series while retaining all supplied attempts. Failed runs remain in the receipt and are never
    /// removed or retried into the same series.
    /// </summary>
    /// <param name="request">Expected immutable provenance and clock contract.</param>
    /// <param name="samples">Safe observations in execution order; the workflow must run them serially.</param>
    /// <returns>A safe receipt. A failed or incomplete series can never pass the primed gate.</returns>
    internal static DurableTemplateTimingProofReceipt Evaluate(
        DurableTemplateTimingProofRequest request,
        IReadOnlyList<DurableTemplateTimingSample> samples)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(samples);
        ValidateRequest(request);

        var artifactSetHash = ComputeArtifactSetSha256(request.Artifacts);
        var sampleResults = new List<DurableTemplateTimingSampleResult>(samples.Count);
        var cacheRoots = new HashSet<string>(StringComparer.Ordinal);
        var projectRoots = new HashSet<string>(StringComparer.Ordinal);
        var databases = new HashSet<string>(StringComparer.Ordinal);
        var dockerDaemons = new HashSet<string>(StringComparer.Ordinal);
        var previousEnd = -1L;

        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var failures = new List<string>();
            if (sample is null)
            {
                sampleResults.Add(new(null, null, ["missing-sample"]));
                continue;
            }

            var elapsed = GetElapsedSeconds(sample, request.StopwatchFrequency);
            if (sample.Ordinal != index + 1) failures.Add("sample-order");
            if (!string.Equals(sample.SeriesId, request.SeriesId, StringComparison.Ordinal)) failures.Add("series-id");
            if (sample.Mode != request.Mode || sample.FeedKind != request.FeedKind) failures.Add("mode");
            if (!string.Equals(sample.FeedIdentity, request.FeedIdentity, StringComparison.Ordinal)) failures.Add("feed-identity");
            if (!string.Equals(sample.SourceCommit, request.SourceCommit, StringComparison.OrdinalIgnoreCase)) failures.Add("source-revision");
            if (!string.Equals(sample.PackageVersion, request.PackageVersion, StringComparison.Ordinal)) failures.Add("package-version");
            if (!string.Equals(sample.ArtifactSetSha256, artifactSetHash, StringComparison.OrdinalIgnoreCase)) failures.Add("artifact-set");
            if (!string.Equals(sample.RuntimeIdentifier, request.RuntimeIdentifier, StringComparison.Ordinal)) failures.Add("runtime-identifier");
            if (!string.Equals(sample.RunnerImage, request.RunnerImage, StringComparison.Ordinal)) failures.Add("runner-image");
            if (!string.Equals(sample.SdkVersion, request.SdkVersion, StringComparison.Ordinal)) failures.Add("sdk-version");
            AddUniqueIdentity(sample.PackageCacheRootIdentity, cacheRoots, "cache-root", failures);
            AddUniqueIdentity(sample.ProjectIdentity, projectRoots, "project", failures);
            AddUniqueIdentity(sample.DatabaseIdentity, databases, "database", failures);
            if (!HashPattern.IsMatch(sample.DockerDaemonIdentity ?? string.Empty)) failures.Add("docker-identity");
            if (sample.StartTimestamp < 0 || sample.EndTimestamp <= sample.StartTimestamp) failures.Add("clock-range");
            if (sample.StartTimestamp < previousEnd) failures.Add("samples-overlap");
            if (sample.EndTimestamp > sample.StartTimestamp) previousEnd = sample.EndTimestamp;
            if (sample.SharedCachePurged) failures.Add("shared-cache-purged");
            if (!HashPattern.IsMatch(sample.PackageCacheClosureSha256 ?? string.Empty)) failures.Add("cache-closure-hash");
            if (!double.IsFinite(sample.PackageSetupRestoreBuildSeconds) || sample.PackageSetupRestoreBuildSeconds < 0)
                failures.Add("setup-restore-build-duration");
            else if (request.Mode == DurableTemplateTimingMode.Primed
                && sample.PackageSetupRestoreBuildSeconds > PrimedSetupRestoreBuildLimitSeconds)
                failures.Add("primed-setup-restore-build-limit");
            if (elapsed is null) failures.Add("root-duration");
            else if (request.Mode == DurableTemplateTimingMode.Primed && elapsed >= PrimedRootLimitSeconds)
                failures.Add("primed-root-limit");
            else if (request.Mode == DurableTemplateTimingMode.Cold && elapsed >= ColdRootWatchdogSeconds)
                failures.Add("cold-root-watchdog");
            if (!sample.CleanupComplete) failures.Add("cleanup-incomplete");
            if ((sample.Succeeded && !string.IsNullOrEmpty(sample.FailureCode))
                || (!sample.Succeeded && !IsSafeFailureCode(sample.FailureCode)))
                failures.Add("failure-code");

            ValidateImageAndCache(request, sample, failures, dockerDaemons);
            ValidateCommands(sample, failures);
            if (elapsed is not null && sample.Commands is not null
                && sample.Commands.All(command => command is not null && double.IsFinite(command.ElapsedSeconds) && command.ElapsedSeconds >= 0)
                && sample.Commands.Sum(command => command.ElapsedSeconds) > elapsed.Value + 0.05)
                failures.Add("command-duration-exceeds-root");
            ValidateCheckpoints(sample.Checkpoints, failures);
            if (!sample.Succeeded) failures.Add("sample-failed");
            sampleResults.Add(new(sample, elapsed, failures.Distinct(StringComparer.Ordinal).ToArray()));
        }

        var durations = sampleResults.Select(result => result.ElapsedSeconds).ToArray();
        var completeDurations = durations.Length == RequiredSampleCount && durations.All(value => value is not null);
        double? median = null;
        double? p95 = null;
        if (completeDurations)
        {
            var sorted = durations.Select(value => value!.Value).Order().ToArray();
            median = sorted[2];
            p95 = sorted[4];
        }

        bool? primedGate = null;
        if (request.Mode == DurableTemplateTimingMode.Primed)
        {
            primedGate = completeDurations
                && sampleResults.All(result => result.ElapsedSeconds < PrimedRootLimitSeconds
                    && result.Sample is not null
                    && double.IsFinite(result.Sample.PackageSetupRestoreBuildSeconds)
                    && result.Sample.PackageSetupRestoreBuildSeconds <= PrimedSetupRestoreBuildLimitSeconds)
                && median < PrimedMedianLimitSeconds
                && p95 < PrimedRootLimitSeconds;
        }

        var failureCodes = sampleResults.SelectMany(result => result.ValidationFailures)
            .Distinct(StringComparer.Ordinal).ToList();
        if (samples.Count != RequiredSampleCount) failureCodes.Add("incomplete-series");
        if (primedGate == false) failureCodes.Add("primed-gate-missed");
        var succeeded = samples.Count == RequiredSampleCount
            && sampleResults.All(result => result.ValidationFailures.Count == 0)
            && (request.Mode == DurableTemplateTimingMode.Cold || primedGate == true);
        return new DurableTemplateTimingProofReceipt(
            1,
            request.SeriesId,
            request.Mode.ToString(),
            request.FeedKind.ToString(),
            request.FeedIdentity,
            request.SourceCommit,
            request.PackageVersion,
            artifactSetHash,
            request.RuntimeIdentifier,
            request.RunnerImage,
            request.SdkVersion,
            request.PostgreSqlImage,
            request.Artifacts.ToArray(),
            sampleResults,
            median,
            p95,
            primedGate,
            succeeded,
            succeeded ? string.Empty : string.Join(",", failureCodes.Distinct(StringComparer.Ordinal)),
            request.StopwatchFrequency);
    }

    /// <summary>Hashes a command identity and exact argument vector without retaining the original tokens.</summary>
    /// <param name="fileName">Executable path or name.</param>
    /// <param name="arguments">Exact child argument tokens; only their SHA-256 is returned.</param>
    /// <returns>Lowercase SHA-256 of a stable JSON array containing normalized executable and arguments.</returns>
    internal static string ComputeCommandArgumentsSha256(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var executable = Path.GetFileNameWithoutExtension(fileName);
        var values = new string[arguments.Count + 1];
        values[0] = executable;
        for (var index = 0; index < arguments.Count; index++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arguments[index]);
            values[index + 1] = arguments[index];
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    }

    /// <summary>Computes stable SHA-256 identity for the complete exact archive set.</summary>
    /// <param name="artifacts">Artifacts sorted by package ID before hashing.</param>
    /// <returns>Lowercase SHA-256 of package ID, version, and SHA-512 rows.</returns>
    internal static string ComputeArtifactSetSha256(IReadOnlyList<DurableTemplateTimingArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var canonical = string.Join('\n', artifacts
            .OrderBy(artifact => artifact.PackageId, StringComparer.Ordinal)
            .Select(artifact => $"{artifact.PackageId}\t{artifact.Version}\t{artifact.Sha512}")) + "\n";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateRequest(DurableTemplateTimingProofRequest request)
    {
        if (!IsSafeLabel(request.SeriesId, 80)
            || !Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.FeedKind))
            throw new PackageIndexException("Timing series identity or mode is invalid.");
        if (!IsSafeLabel(request.FeedIdentity, 128)
            || !CommitPattern.IsMatch(request.SourceCommit ?? string.Empty))
            throw new PackageIndexException("Timing feed or source identity is invalid.");
        PackageVersionValidator.Require(request.PackageVersion, PackageVersionPolicy.StableOrPrereleaseNoBuildMetadata);
        if (!IsSafeLabel(request.RuntimeIdentifier, 64) || !IsSafeLabel(request.RunnerImage, 128)
            || !SdkPattern.IsMatch(request.SdkVersion ?? string.Empty))
            throw new PackageIndexException("Timing runner or SDK identity is invalid.");
        if (request.PostgreSqlImage != DurableTemplateConsumerProof.PostgreSqlImage)
            throw new PackageIndexException("Timing proof is not bound to the pinned PostgreSQL image.");
        if (request.StopwatchFrequency <= 0)
            throw new PackageIndexException("Timing proof requires a positive monotonic clock frequency.");
        if (request.Artifacts is null || request.Artifacts.Count == 0)
            throw new PackageIndexException("Timing proof requires the complete artifact set.");

        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in request.Artifacts)
        {
            if (artifact is null || !IsSafeLabel(artifact.PackageId, 128)
                || !string.Equals(artifact.Version, request.PackageVersion, StringComparison.Ordinal)
                || !packageIds.Add(artifact.PackageId) || !IsSha512(artifact.Sha512))
                throw new PackageIndexException("Timing artifact identity is invalid or duplicated.");
        }
        if (!packageIds.Contains(DurableTemplateStaging.PackageId)
            || !packageIds.Contains("ForgeTrust.AppSurface.Durable.PostgreSql"))
            throw new PackageIndexException("Timing artifact set is missing the template or PostgreSQL provider package.");

        if (request.Mode == DurableTemplateTimingMode.Primed)
        {
            if (!HashPattern.IsMatch(request.PinnedPackageCacheClosureSha256 ?? string.Empty))
                throw new PackageIndexException("Primed timing requires the hashed pinned package-cache closure.");
        }
        else if (request.PinnedPackageCacheClosureSha256 is not null)
        {
            throw new PackageIndexException("Cold timing cannot claim a primed package-cache closure.");
        }
    }

    private static void ValidateImageAndCache(
        DurableTemplateTimingProofRequest request,
        DurableTemplateTimingSample sample,
        ICollection<string> failures,
        ISet<string> dockerDaemons)
    {
        var digest = GetPinnedDigest(request.PostgreSqlImage);
        if (request.Mode == DurableTemplateTimingMode.Primed)
        {
            if (!sample.DockerImagePresentAtStart
                || !string.Equals(sample.ImageDigestAtStart, digest, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(sample.ImageDigestAfter, digest, StringComparison.OrdinalIgnoreCase))
                failures.Add("primed-image");
            if (sample.PackageCacheEmptyAtStart
                || !string.Equals(sample.PackageCacheClosureSha256, request.PinnedPackageCacheClosureSha256, StringComparison.OrdinalIgnoreCase)
                || !sample.HttpCacheEmptyAtStart)
                failures.Add("primed-cache-closure");
        }
        else
        {
            if (sample.DockerImagePresentAtStart || !string.IsNullOrEmpty(sample.ImageDigestAtStart)
                || !string.Equals(sample.ImageDigestAfter, digest, StringComparison.OrdinalIgnoreCase))
                failures.Add("cold-image-state");
            if (!sample.PackageCacheEmptyAtStart || !sample.HttpCacheEmptyAtStart
                || !string.Equals(sample.PackageCacheClosureSha256, EmptyCacheInventorySha256, StringComparison.OrdinalIgnoreCase))
                failures.Add("cold-cache-state");
            if (HashPattern.IsMatch(sample.DockerDaemonIdentity ?? string.Empty)
                && !dockerDaemons.Add(sample.DockerDaemonIdentity!))
                failures.Add("cold-daemon-reused");
        }
    }

    private static void ValidateCommands(DurableTemplateTimingSample sample, ICollection<string> failures)
    {
        if (sample.Commands is null || sample.Commands.Count != 3)
        {
            failures.Add("command-sequence");
            return;
        }

        var expected = new[]
        {
            (DurableTemplateTimingCommandPhase.TemplateInstall,
                new[] { "new", "install", $"{DurableTemplateStaging.PackageId}@{sample.PackageVersion}" }),
            (DurableTemplateTimingCommandPhase.ProjectCreate,
                new[] { "new", "appsurface-durable-worker", "-n", "FirstDurableWorker" }),
            (DurableTemplateTimingCommandPhase.FirstDurableWorkTest,
                new[] { "test", "FirstDurableWorker", "--filter", "FullyQualifiedName~FirstDurableWork", "--logger", "console;verbosity=detailed" })
        };
        for (var index = 0; index < expected.Length; index++)
        {
            var command = sample.Commands[index];
            if (command is null || command.Phase != expected[index].Item1
                || !HashPattern.IsMatch(command.ArgumentsSha256 ?? string.Empty)
                || !string.Equals(
                    command.ArgumentsSha256,
                    ComputeCommandArgumentsSha256("dotnet", expected[index].Item2),
                    StringComparison.OrdinalIgnoreCase)
                || !double.IsFinite(command.ElapsedSeconds) || command.ElapsedSeconds < 0
                || command.ExitCode != 0 || command.OutputTruncated)
                failures.Add($"command-{expected[index].Item1}");
        }
    }

    private static void ValidateCheckpoints(DurableTemplateTimingCheckpoints? checkpoints, ICollection<string> failures)
    {
        if (checkpoints is null || !checkpoints.AuthorizedActivation || !checkpoints.TerminalWork
            || !checkpoints.ReadinessTransition || !checkpoints.ExportedActivity)
            failures.Add("first-work-checkpoints");
    }

    private static double? GetElapsedSeconds(DurableTemplateTimingSample sample, long frequency)
    {
        if (sample.StartTimestamp < 0 || sample.EndTimestamp <= sample.StartTimestamp) return null;
        var elapsed = (sample.EndTimestamp - (double)sample.StartTimestamp) / frequency;
        return double.IsFinite(elapsed) && elapsed > 0 ? elapsed : null;
    }

    private static void AddUniqueIdentity(string? value, ISet<string> seen, string kind, ICollection<string> failures)
    {
        if (!HashPattern.IsMatch(value ?? string.Empty)) failures.Add($"{kind}-identity");
        else if (!seen.Add(value!)) failures.Add($"{kind}-reused");
    }

    private static string GetPinnedDigest(string image) => image.Split('@', 2)[1];

    private static bool IsSha512(string? value) =>
        value is { Length: 128 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeLabel(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maxLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static bool IsSafeFailureCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 48
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
