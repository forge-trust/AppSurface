using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Validates local receipts supplied by the existing trusted release workflow before credentials are read.</summary>
/// <remarks>
/// These records are assertions within the immutable producer/workflow download boundary, not portable signatures.
/// The caller supplies the expected full source revision independently; the receipt cannot select its own source.
/// Every package hash must match the producer manifest and current archive, so evidence for a different candidate fails.
/// </remarks>
internal static class DurableTemplateReleaseEvidence
{
    /// <summary>Requires three distinct successful native OS proofs and the Linux first-Work certificate.</summary>
    /// <param name="directory">Exact trusted workflow evidence download directory.</param>
    /// <param name="sourceCommit">Full source revision from the protected workflow context.</param>
    /// <param name="manifest">Validated immutable producer package manifest.</param>
    /// <param name="artifactDirectory">Exact producer archives; hashes are rechecked before publication.</param>
    internal static void Require(string? directory, string? sourceCommit, PackageArtifactManifest manifest, string artifactDirectory)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(sourceCommit)
            || sourceCommit.Length != 40 || !sourceCommit.All(Uri.IsHexDigit))
            throw new PackageIndexException("Durable template publication requires source-bound Linux, macOS and Windows receipts before credentials.");
        foreach (var (file, rid, firstWork) in new[]
        {
            ("linux-x64.json", "linux-x64", true), ("osx-arm64.json", "osx-arm64", false), ("win-x64.json", "win-x64", false)
        })
        {
            var receipt = Read(Path.Join(directory, file));
            Validate(receipt, sourceCommit, manifest, artifactDirectory, rid, firstWork);
        }
        foreach (var mode in new[] { DurableTemplateTimingMode.Primed, DurableTemplateTimingMode.Cold })
            ValidateTiming(ReadTiming(Path.Join(directory, mode == DurableTemplateTimingMode.Primed ? "timing-primed.json" : "timing-cold.json")),
                sourceCommit, manifest, mode);
    }

    /// <summary>Reads a bounded timing receipt; duplicate, unknown and malformed fields fail closed.</summary>
    internal static DurableTemplateTimingProofReceipt ReadTiming(string path)
    {
        DurableTemplateStaging.RequireRegularPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
            throw new PackageIndexException("Durable template timing receipt is missing or exceeds its retention bound.");
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<DurableTemplateTimingProofReceipt>(bytes, new JsonSerializerOptions(PackageArtifactJson.Options)
            {
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 16
            }) ?? throw new PackageIndexException("Durable template timing receipt is empty.");
        }
        catch (JsonException)
        {
            throw new PackageIndexException("Durable template timing JSON is malformed.");
        }
    }

    /// <summary>Reevaluates every sample against the independently expected producer and unchanged timing policy.</summary>
    internal static void ValidateTiming(DurableTemplateTimingProofReceipt receipt, string source,
        PackageArtifactManifest manifest, DurableTemplateTimingMode mode)
    {
        if (receipt.SchemaVersion != 1 || !receipt.Succeeded || receipt.FailureCode != string.Empty
            || receipt.SourceCommit != source || receipt.PackageVersion != manifest.PackageVersion
            || receipt.Mode != mode.ToString()
            || receipt.FeedKind != DurableTemplateTimingFeedKind.CandidateLocal.ToString() || receipt.FeedIdentity != "candidate-local-feed"
            || receipt.RuntimeIdentifier != "linux-x64" || receipt.PostgreSqlImage != DurableTemplateConsumerProof.PostgreSqlImage
            || receipt.Artifacts is null || receipt.Artifacts.Count != manifest.Entries.Count
            || receipt.Samples is null || receipt.Samples.Count != 5 || receipt.Samples.Any(item => item is null || item.Sample is null)
            || receipt.StopwatchFrequency <= 0)
            throw new PackageIndexException("Durable template timing evidence is failed, incomplete or bound to another candidate.");
        var expected = manifest.Entries.Select(entry => new DurableTemplateTimingArtifact(entry.PackageId, manifest.PackageVersion, entry.Sha512)).ToArray();
        if (DurableTemplateTimingProof.ComputeArtifactSetSha256(expected) != receipt.ArtifactSetSha256
            || !receipt.Artifacts.OrderBy(item => item.PackageId, StringComparer.Ordinal).SequenceEqual(expected.OrderBy(item => item.PackageId, StringComparer.Ordinal)))
            throw new PackageIndexException("Durable template timing artifact set differs from the producer.");
        var samples = receipt.Samples.Select(item => item.Sample!).ToArray();
        var request = new DurableTemplateTimingProofRequest(receipt.SeriesId, mode, DurableTemplateTimingFeedKind.CandidateLocal,
            receipt.FeedIdentity, source, manifest.PackageVersion, receipt.RuntimeIdentifier, receipt.RunnerImage, receipt.SdkVersion,
            receipt.PostgreSqlImage, mode == DurableTemplateTimingMode.Primed ? samples[0].PackageCacheClosureSha256 : null,
            receipt.StopwatchFrequency, expected);
        var evaluated = DurableTemplateTimingProof.Evaluate(request, samples);
        if (!evaluated.Succeeded || receipt.MedianSeconds != evaluated.MedianSeconds
            || receipt.NearestRankP95Seconds != evaluated.NearestRankP95Seconds || receipt.PerformanceGatePassed != evaluated.PerformanceGatePassed
            || receipt.Samples.Any(item => item.ValidationFailures is null || item.ValidationFailures.Count != 0)
            || !receipt.Samples.Select(item => item.ElapsedSeconds).SequenceEqual(evaluated.Samples.Select(item => item.ElapsedSeconds)))
            throw new PackageIndexException("Durable template timing assertions or measured summaries do not pass their policy.");
    }

    /// <summary>Reads at most 64KiB of schema-v1 evidence with depth16 and rejects duplicate or unknown fields.</summary>
    internal static DurableTemplateProofReceipt Read(string path)
    {
        DurableTemplateStaging.RequireRegularPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
            throw new PackageIndexException("Durable template receipt is missing or exceeds its retention bound.");
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<DurableTemplateProofReceipt>(bytes, new JsonSerializerOptions(PackageArtifactJson.Options)
            {
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 16
            }) ?? throw new PackageIndexException("Durable template receipt is empty.");
        }
        catch (JsonException)
        {
            throw new PackageIndexException("Durable template receipt JSON is malformed.");
        }
    }

    /// <summary>Checks successful assertions, exact source/platform/content identity and every candidate archive.</summary>
    internal static void Validate(DurableTemplateProofReceipt receipt, string source, PackageArtifactManifest manifest,
        string artifacts, string rid, bool firstWork)
    {
        if (receipt.SchemaVersion != 1 || receipt.SourceCommit != source || receipt.PackageVersion != manifest.PackageVersion
            || receipt.RuntimeIdentifier != rid || receipt.Image != DurableTemplateConsumerProof.PostgreSqlImage
            || receipt.Mode != "candidate-local-feed-correctness" || !receipt.Succeeded || !receipt.CleanupComplete
            || !receipt.ExactArchiveInstall || !receipt.FeedInstall || !receipt.AuthoredSource || !receipt.NativeSmoke || !receipt.SampleReplacement
            || receipt.FailureCode != string.Empty || receipt.FailurePhase != string.Empty
            || (receipt.GeneratedContentSha256?.Length ?? 0) != 64 || !receipt.GeneratedContentSha256!.All(Uri.IsHexDigit)
            || !Regex.IsMatch(receipt.SdkVersion ?? string.Empty, @"\A10\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?\z", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(receipt.RunnerImage ?? string.Empty, @"\A[A-Za-z0-9._-]{1,63}/[A-Za-z0-9._-]{1,63}\z", RegexOptions.CultureInvariant)
            || receipt.Phases is null || receipt.Phases.Count is 0 or > 32
            || receipt.Phases.Any(p => p is null || p.ExitCode != 0 || p.Truncated || !double.IsFinite(p.ElapsedSeconds) || p.ElapsedSeconds < 0)
            || (firstWork && (!receipt.AuthorizedActivation || !receipt.TerminalWork || !receipt.ReadinessTransition || !receipt.ExportedActivity)))
            throw new PackageIndexException("Durable template evidence is failed, stale, incomplete or bound to a different source/platform.");
        var requiredPhases = new List<string>
        {
            "sdk", "archive-install", "acquisition-create", "acquisition-restore", "uninstall", "absence",
            "authored-restore", "authored-build", "authored-format", "authored-tests",
            "archive-install", "archive-discover", "archive-create", "archive-restore", "archive-build", "archive-format", "archive-tests", "uninstall", "absence",
            "feed-install", "feed-discover", "feed-create", "feed-restore", "feed-build", "feed-format", "feed-tests"
        };
        if (firstWork) requiredPhases.Add("first-work");
        requiredPhases.AddRange(["replacement-build", "replacement-tests"]);
        if (firstWork) requiredPhases.Add("replacement-first-work");
        requiredPhases.AddRange(["uninstall", "absence"]);
        if (!receipt.Phases.Select(phase => phase.Id).SequenceEqual(requiredPhases, StringComparer.Ordinal))
            throw new PackageIndexException("Durable template evidence omitted or reordered an installed-consumer phase.");
        if (receipt.Artifacts is null || receipt.Artifacts.Count != manifest.Entries.Count
            || receipt.Artifacts.Select(a => a?.PackageId).Distinct(StringComparer.Ordinal).Count() != manifest.Entries.Count)
            throw new PackageIndexException("Durable template receipt does not bind the complete candidate archive set.");
        var tools = receipt.NativeTools;
        if (tools is null || tools.MajorVersion < 16 || tools.ServerVersionNumber is not >= 160000
            || string.IsNullOrWhiteSpace(tools.ServerVersion) || tools.Sha256 is null || tools.Sha256.Count != 4
            || !new[] { "initdb", "postgres", "pg_ctl", "psql" }.All(tools.Sha256.ContainsKey)
            || tools.Sha256.Values.Any(hash => hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new PackageIndexException("Native PostgreSQL tool/server identity is missing or unsupported.");
        foreach (var entry in manifest.Entries)
        {
            var matches = receipt.Artifacts.Where(a => a is not null && a.PackageId == entry.PackageId).ToArray();
            var path = Path.Join(artifacts, entry.ArtifactFileName);
            DurableTemplateStaging.RequireRegularPath(path);
            if (matches.Length != 1 || matches[0].Version != manifest.PackageVersion || matches[0].Sha512 != entry.Sha512
                || !File.Exists(path) || PackageHash.ComputeSha512(path) != entry.Sha512)
                throw new PackageIndexException("Durable template receipt archive identity differs from the producer.");
        }
        var templateEntry = manifest.Entries.SingleOrDefault(entry => entry.PackageId == DurableTemplateStaging.PackageId);
        if (templateEntry is not null && receipt.GeneratedContentSha256 != DurableTemplateArtifactContract.ComputeGeneratedContentSha256(
                Path.Join(artifacts, templateEntry.ArtifactFileName), "FirstDurableWorker"))
            throw new PackageIndexException("Durable template generated content identity differs from the exact candidate payload.");
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new PackageIndexException("Durable template receipt contains duplicate fields.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }
}
