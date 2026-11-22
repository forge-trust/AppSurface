using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

internal static partial class CoverageFanInEngine
{
    private const string ApplicabilityName = "patch-applicability.json";
    private const string DiffMetadataName = "diff-metadata.json";

    /// <summary>V3 writer. Original reports and exact Git objects remain authoritative; N/A asserts only report absence.</summary>
    private static async Task<AcceptanceCandidate> AcceptV3Async(CoverageFanInEngineRequest r, CoverageFanInIO io,
        IReadOnlyList<CoverageFanInBundle> bundles, InvocationState state, CancellationToken token)
    {
        var composed = ReadComposition(r, io, bundles);
        var patchRequired = r.Expected.Gate.GetProperty("mode").GetString() == "aggregateAndPatch";
        if (!patchRequired)
        {
            // Aggregate-only is deliberately not a patch applicability claim.
            var baseline = await GateAsync(r, composed.Primary, PrimaryName, false, true, null, token).ConfigureAwait(false);
            var bytes = GateJson(baseline);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [PrimaryGateName] = bytes,
                ["primary-gate.md"] = Encoding.UTF8.GetBytes(CoverageGateReportWriter.RenderMarkdown(baseline))
            };
            if (baseline.Passed)
            {
                CoverageFanInIndependentVerifier.VerifyGates(Parse(bytes), Parse(bytes), r.Expected.Policy, composed.PrimaryDocument, composed.Document);
                files["acceptance.json"] = V3Receipt(r, io, bundles, bytes, null, null, null, null);
            }
            else files["rejection.json"] = Serialize(new { schemaVersion = 3, policyDigest = r.Expected.Digest, source = r.Expected.Source, run = r.Expected.Run, accepted = false, reason = "gate-failed" });
            return new(files, baseline.Passed);
        }

        var input = await r.DiffContext!.ReadAsync(token).ConfigureAwait(false);
        if (r.ExpectedPatchSha256 is null || r.ExpectedPatchSha256 != CoverageFanInIO.Hash(input.Patch))
            throw new CoverageFanInEvidenceException("identity-mismatch", "diff", "Derive the expected patch hash from the exact canonical v3 recipe and regenerate fresh evidence.");
        var changes = CoverageFanInDiffParser.Parse(input);
        var metadata = V3Metadata(input, changes);
        var metadataBytes = CoverageFanInV3Encoding.Canonical(metadata);
        state.PatchApplicability = UnverifiedApplicability();
        var completedGates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            // Reuse the existing gate's bounded report/aggregate analysis. Writer patch classifications are derived
            // separately so a legitimate exact-empty/no-addition patch never weakens the direct gate's strictness.
            var primaryGate = await GateAsync(r, composed.Primary, PrimaryName, false, true, null, token).ConfigureAwait(false);
            completedGates[PrimaryGateName] = GateJson(primaryGate);
            var composedGate = await GateAsync(r, composed.Coverage, CoverageName, false, false, null, token).ConfigureAwait(false);
            completedGates[ComposedGateName] = GateJson(composedGate);
            var primaryMetrics = V3Metrics(composed.PrimaryDocument, changes, r, input.QualifiedPngPaths);
            var composedMetrics = V3Metrics(composed.Document, changes, r, input.QualifiedPngPaths);
            var primaryJson = V3Gate(primaryGate, primaryMetrics, input, r, isComposed: false);
            var composedJson = V3Gate(composedGate, composedMetrics, input, r, isComposed: true);
            var claim = CoverageFanInV3Encoding.Element(new
            {
                schemaVersion = 3,
                reportProfileId = r.Expected.Policy.GetProperty("reportProfileId").GetString(),
                policyDigest = r.Expected.Digest,
                source = r.Expected.Source,
                run = r.Expected.Run,
                diffSha256 = CoverageFanInIO.Hash(input.Patch),
                diffMetadataSha256 = CoverageFanInIO.Hash(metadataBytes),
                captureManifestHashes = bundles.Select(x => x.ManifestHash).ToArray(),
                primaryReportSha256 = CoverageFanInIO.Hash(composed.Primary),
                composedReportSha256 = CoverageFanInIO.Hash(composed.Coverage),
                primary = primaryMetrics,
                composed = composedMetrics
            });
            state.PatchApplicability = CoverageFanInV3Encoding.Element(new { primary = primaryMetrics, composed = composedMetrics });
            var primaryBytes = CoverageFanInV3Encoding.Canonical(primaryJson);
            var composedBytes = CoverageFanInV3Encoding.Canonical(composedJson);
            completedGates[PrimaryGateName] = primaryBytes;
            completedGates[ComposedGateName] = composedBytes;
            var claimBytes = CoverageFanInV3Encoding.Canonical(claim);
            var passed = primaryJson.GetProperty("passed").GetBoolean() && composedJson.GetProperty("passed").GetBoolean();
            var output = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [PrimaryGateName] = primaryBytes,
                [ComposedGateName] = composedBytes,
                [ApplicabilityName] = claimBytes,
                ["primary-gate.md"] = Encoding.UTF8.GetBytes(V3Summary("Primary", primaryMetrics, passed)),
                ["composed-gate.md"] = Encoding.UTF8.GetBytes(V3Summary("Composed", composedMetrics, passed)),
                ["diff.patch"] = input.Patch,
                [DiffMetadataName] = metadataBytes
            };
            CoverageFanInV3Verifier.Verify(primaryJson, composedJson, claim, metadata, r.Expected, composed.PrimaryDocument,
                composed.Document, input, bundles.Select(x => x.ManifestHash).ToArray(), CoverageFanInIO.Hash(composed.Primary),
                CoverageFanInIO.Hash(composed.Coverage), r.DiffContext.TrackedPaths);
            if (passed) output["acceptance.json"] = V3Receipt(r, io, bundles, primaryBytes, composedBytes,
                CoverageFanInIO.Hash(input.Patch), CoverageFanInIO.Hash(metadataBytes), CoverageFanInIO.Hash(claimBytes));
            else output["rejection.json"] = CoverageFanInV3Encoding.Canonical(new
            {
                schemaVersion = 3,
                policyDigest = r.Expected.Digest,
                source = r.Expected.Source,
                run = r.Expected.Run,
                accepted = false,
                reason = "gate-failed",
                applicabilitySha256 = CoverageFanInIO.Hash(claimBytes),
                diffSha256 = CoverageFanInIO.Hash(input.Patch),
                diffMetadataSha256 = CoverageFanInIO.Hash(metadataBytes),
                primaryGateSha256 = CoverageFanInIO.Hash(primaryBytes),
                composedGateSha256 = CoverageFanInIO.Hash(composedBytes)
            });
            return new(output, passed);
        }
        catch (Exception exception) when (exception is CoverageFanInEvidenceException or InvalidDataException or FormatException or OverflowException)
        {
            state.PatchApplicability = UnverifiedApplicability();
            var failure = exception as CoverageFanInEvidenceException ?? ReportInvalid();
            var metrics = state.PatchApplicability.Value;
            var rejectedClaim = CoverageFanInV3Encoding.Canonical(new
            {
                schemaVersion = 3,
                reportProfileId = r.Expected.Policy.GetProperty("reportProfileId").GetString(),
                policyDigest = r.Expected.Digest,
                source = r.Expected.Source,
                run = r.Expected.Run,
                diffSha256 = CoverageFanInIO.Hash(input.Patch),
                diffMetadataSha256 = CoverageFanInIO.Hash(metadataBytes),
                captureManifestHashes = bundles.Select(x => x.ManifestHash).ToArray(),
                primaryReportSha256 = CoverageFanInIO.Hash(composed.Primary),
                composedReportSha256 = CoverageFanInIO.Hash(composed.Coverage),
                primary = metrics.GetProperty("primary"),
                composed = metrics.GetProperty("composed")
            });
            var rejectedFiles = new Dictionary<string, byte[]>(completedGates, StringComparer.Ordinal)
            {
                [ApplicabilityName] = rejectedClaim,
                ["diff.patch"] = input.Patch,
                [DiffMetadataName] = metadataBytes,
                ["rejection.json"] = CoverageFanInV3Encoding.Canonical(new
                {
                    schemaVersion = 3,
                    policyDigest = r.Expected.Digest,
                    source = r.Expected.Source,
                    run = r.Expected.Run,
                    accepted = false,
                    reason = "invalid-evidence",
                    applicabilitySha256 = CoverageFanInIO.Hash(rejectedClaim),
                    diffSha256 = CoverageFanInIO.Hash(input.Patch),
                    diffMetadataSha256 = CoverageFanInIO.Hash(metadataBytes),
                    primaryGateSha256 = completedGates.TryGetValue(PrimaryGateName, out var completedPrimary) ? CoverageFanInIO.Hash(completedPrimary) : null,
                    composedGateSha256 = completedGates.TryGetValue(ComposedGateName, out var completedComposed) ? CoverageFanInIO.Hash(completedComposed) : null
                })
            };
            return new(rejectedFiles, false, failure);
        }
    }

    private static JsonElement UnverifiedApplicability()
    {
        var metric = new
        {
            outcome = "unverified",
            reason = "invalid-evidence",
            changedLines = (int?)null,
            measurable = (int?)null,
            covered = (int?)null,
            percent = (decimal?)null
        };
        return CoverageFanInV3Encoding.Element(new { primary = new { line = metric, branch = metric }, composed = new { line = metric, branch = metric } });
    }

    private static byte[] V3Receipt(CoverageFanInEngineRequest r, CoverageFanInIO io, IReadOnlyList<CoverageFanInBundle> bundles,
        byte[] primary, byte[]? composed, string? diffHash, string? metadataHash, string? applicabilityHash) =>
        CoverageFanInV3Encoding.Canonical(new
        {
            schemaVersion = 3,
            policyDigest = r.Expected.Digest,
            source = r.Expected.Source,
            run = r.Expected.Run,
            evaluationIntent = r.HistoricalReplay ? "historicalReplay" : "currentTrustedRun",
            compositionReceiptSha256 = CoverageFanInIO.Hash(io.Read(Path.Join(r.ComposedOutputDirectory!, ReceiptName))),
            inputs = bundles.Select(x => x.ManifestHash).ToArray(),
            patchOutcome = PatchOutcome(r),
            primaryGateSha256 = CoverageFanInIO.Hash(primary),
            composedGateSha256 = composed is null ? null : CoverageFanInIO.Hash(composed),
            diffSha256 = diffHash,
            diffMetadataSha256 = metadataHash,
            applicabilitySha256 = applicabilityHash
        });

    private static JsonElement V3Metadata(CoverageFanInDiffInput input, IReadOnlyList<CoverageFanInDiffChange> changes) =>
        CoverageFanInV3Encoding.Element(new
        {
            schemaVersion = 3,
            requestedBaseCommit = input.RequestedBase,
            mergeBaseCommit = input.MergeBase,
            headCommit = input.Head,
            diffPolicyId = input.DiffPolicyId,
            diffSha256 = CoverageFanInIO.Hash(input.Patch),
            empty = input.Patch.Length == 0,
            changes = changes.Select(x => new { kind = x.Kind, oldPath = x.OldPath, newPath = x.NewPath, addedRanges = x.AddedRanges })
        });

    private static JsonElement V3Metrics(XDocument report, IReadOnlyList<CoverageFanInDiffChange> changes,
        CoverageFanInEngineRequest r, IReadOnlySet<string> qualifiedPngPaths)
    {
        var points = new Dictionary<(string File, int Line), (bool Hit, int Covered, int Valid)>();
        var tracked = r.DiffContext!.TrackedPaths;
        var aliases = tracked.ToLookup(x => x.Normalize(NormalizationForm.FormC), StringComparer.OrdinalIgnoreCase);
        foreach (var cls in report.Descendants().Where(x => x.Name.LocalName == "class"))
        {
            var file = CoverageFanInDiffParser.Path(cls.Attribute("filename")?.Value ?? throw ReportInvalid());
            if (!tracked.Contains(file) && aliases[file.Normalize(NormalizationForm.FormC)].Any()) throw ReportInvalid();
            if (qualifiedPngPaths.Contains(file))
                throw new CoverageFanInEvidenceException("report-invalid", "report",
                    "The collector identifies a changed screenshot as source. Resolve the conflicting report before retrying.");
            foreach (var line in cls.Descendants().Where(x => x.Name.LocalName == "line"))
            {
                if (!int.TryParse(line.Attribute("number")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0
                    || !int.TryParse(line.Attribute("hits")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hits)) throw ReportInvalid();
                var covered = 0; var valid = 0;
                if (line.Attribute("condition-coverage") is { } condition)
                {
                    var match = Regex.Match(condition.Value, @"\A[0-9.]+% \(([0-9]+)/([0-9]+)\)\z", RegexOptions.CultureInvariant);
                    if (!match.Success || !int.TryParse(match.Groups[1].Value, out covered) || !int.TryParse(match.Groups[2].Value, out valid)
                        || valid <= 0 || covered > valid) throw ReportInvalid();
                }
                else if (string.Equals(line.Attribute("branch")?.Value, "true", StringComparison.OrdinalIgnoreCase)) throw ReportInvalid();
                var key = (file, number);
                if (points.TryGetValue(key, out var previous)) points[key] = (previous.Hit || hits > 0, checked(previous.Covered + covered), checked(previous.Valid + valid));
                else points.Add(key, (hits > 0, covered, valid));
            }
        }
        var changed = 0; var lineCovered = 0; var lineValid = 0; var branchCovered = 0; var branchValid = 0;
        foreach (var change in changes)
            foreach (var range in change.AddedRanges)
            {
                changed = checked(changed + range.Count);
                for (var offset = 0; offset < range.Count; offset++)
                    if (points.TryGetValue((change.NewPath!, checked(range.Start + offset)), out var point))
                    {
                        lineValid++;
                        if (point.Hit && (r.Expected.Gate.GetProperty("patchLineMode").GetString() == "measurable" || point.Valid == 0 || point.Covered == point.Valid)) lineCovered++;
                        branchCovered = checked(branchCovered + point.Covered); branchValid = checked(branchValid + point.Valid);
                    }
            }
        return CoverageFanInV3Encoding.Element(new { line = V3Metric(changed, lineValid, lineCovered), branch = V3Metric(changed, branchValid, branchCovered) });
    }

    private static object V3Metric(int changed, int measurable, int covered) => new
    {
        outcome = measurable == 0 ? "notApplicable" : "measured",
        reason = measurable == 0 ? "no-reported-patch-points" : "reported-points",
        changedLines = (int?)changed,
        measurable = (int?)measurable,
        covered = (int?)covered,
        percent = measurable == 0 ? null : (decimal?)Math.Round(covered * 100m / measurable, 4, MidpointRounding.AwayFromZero)
    };

    private static JsonElement V3Gate(CoverageGateResult aggregate, JsonElement metrics, CoverageFanInDiffInput input,
        CoverageFanInEngineRequest r, bool isComposed)
    {
        var root = JsonNode.Parse(GateJson(aggregate))!;
        var gate = r.Expected.Gate;
        var tolerance = gate.GetProperty("tolerance").GetDecimal();
        var lineThreshold = isComposed ? (decimal?)gate.GetProperty("minPatchLine").GetDecimal() : null;
        var branchThreshold = isComposed ? (decimal?)gate.GetProperty("minPatchBranch").GetDecimal() : null;
        root["thresholds"]!["patchLine"] = lineThreshold; root["thresholds"]!["patchBranch"] = branchThreshold;
        root["effectiveThresholds"]!["patchLine"] = lineThreshold is null ? null : Math.Max(0, lineThreshold.Value - tolerance);
        root["effectiveThresholds"]!["patchBranch"] = branchThreshold is null ? null : Math.Max(0, branchThreshold.Value - tolerance);
        root["patchLineMode"] = gate.GetProperty("patchLineMode").GetString();
        root["patchLine"] = JsonNode.Parse(metrics.GetProperty("line").GetRawText());
        root["patchBranch"] = JsonNode.Parse(metrics.GetProperty("branch").GetRawText());
        root["patchDiffSource"] = JsonSerializer.SerializeToNode(new
        {
            kind = "git-base",
            label = input.RequestedBase,
            strictness = "local-git",
            diffBase = input.RequestedBase,
            path = (string?)null,
            bytes = input.Patch.Length,
            sha256 = CoverageFanInIO.Hash(input.Patch),
            empty = input.Patch.Length == 0
        });
        root["passed"] = aggregate.Passed && MetricPass(metrics.GetProperty("line"), lineThreshold, tolerance)
            && MetricPass(metrics.GetProperty("branch"), branchThreshold, tolerance);
        return JsonSerializer.SerializeToElement(root);
    }

    private static bool MetricPass(JsonElement metric, decimal? threshold, decimal tolerance) => threshold is null
        || metric.GetProperty("outcome").GetString() == "notApplicable"
        || metric.GetProperty("percent").GetDecimal() >= Math.Max(0, threshold.Value - tolerance);

    private static string V3Summary(string role, JsonElement metrics, bool passed) =>
        $"# {role} fan-in v3\n\nAccepted gates: {passed.ToString().ToLowerInvariant()}\n\n"
        + string.Join("\n", new[] { "line", "branch" }.Select(name =>
            $"- Patch {name}: {(metrics.GetProperty(name).GetProperty("percent").ValueKind == JsonValueKind.Null ? "N/A" : metrics.GetProperty(name).GetProperty("percent").GetDecimal().ToString(CultureInfo.InvariantCulture) + "%")} ({metrics.GetProperty(name).GetProperty("reason").GetString()})"))
        + "\n\nN/A means no changed measurements were reported under the accepted collector/policy. Silent collector omissions or honored exclusions can produce N/A or a mixed pass.\n";

    private static CoverageFanInEvidenceException ReportInvalid() => new("report-invalid", "report", "Regenerate original supported reports; correct detectable record/path corruption rather than editing N/A flags.");

    /// <summary>Reads an accepted v3 generation without promotion, repair, writes or legacy reinterpretation.</summary>
    private static async Task VerifyAcceptedV3Async(CoverageFanInEngineRequest r, CoverageFanInIO io,
        IReadOnlyList<CoverageFanInBundle> bundles, InvocationState state, CancellationToken token)
    {
        var composed = ReadComposition(r, io, bundles);
        var patch = r.Expected.Gate.GetProperty("mode").GetString() == "aggregateAndPatch";
        var directory = r.AcceptedOutputDirectory!;
        VerifyGeneration(r, io, directory, "acceptance", patch
            ? [PrimaryGateName, "primary-gate.md", ComposedGateName, "composed-gate.md", "acceptance.json", "diff.patch", DiffMetadataName, ApplicabilityName]
            : [PrimaryGateName, "primary-gate.md", "acceptance.json"]);
        var receipt = Parse(io.Read(Path.Join(directory, "acceptance.json")));
        ExactProperties(receipt, ["schemaVersion", "policyDigest", "source", "run", "evaluationIntent", "compositionReceiptSha256", "inputs", "patchOutcome", "primaryGateSha256", "composedGateSha256", "diffSha256", "diffMetadataSha256", "applicabilitySha256"]);
        var primaryBytes = io.Read(Path.Join(directory, PrimaryGateName));
        var composedBytes = patch ? io.Read(Path.Join(directory, ComposedGateName)) : null;
        var diffBytes = patch ? io.Read(Path.Join(directory, "diff.patch")) : null;
        var metadataBytes = patch ? io.Read(Path.Join(directory, DiffMetadataName)) : null;
        var claimBytes = patch ? io.Read(Path.Join(directory, ApplicabilityName)) : null;
        var expected = Parse(V3Receipt(r, io, bundles, primaryBytes, composedBytes,
            diffBytes is null ? null : CoverageFanInIO.Hash(diffBytes), metadataBytes is null ? null : CoverageFanInIO.Hash(metadataBytes), claimBytes is null ? null : CoverageFanInIO.Hash(claimBytes)));
        if (!Same(receipt, expected)) throw new CoverageFanInEvidenceException("applicability-mismatch", "receipt", "Regenerate fresh current evidence with the matching v3 tools; receipt claims disagree.");
        if (!patch)
        {
            CoverageFanInIndependentVerifier.VerifyGates(Parse(primaryBytes), Parse(primaryBytes), r.Expected.Policy, composed.PrimaryDocument, composed.Document);
            return;
        }
        var input = await r.DiffContext!.ReadAsync(token).ConfigureAwait(false);
        if (!input.Patch.AsSpan().SequenceEqual(diffBytes)) throw new CoverageFanInEvidenceException("identity-mismatch", "diff", "Restore the matching pinned source context before replay; retained bytes differ.");
        var primary = Parse(primaryBytes); var gate = Parse(composedBytes!); var claim = Parse(claimBytes!);
        if (!primary.GetProperty("passed").GetBoolean() || !gate.GetProperty("passed").GetBoolean()) throw ReportInvalid();
        CoverageFanInV3Verifier.Verify(primary, gate, claim, Parse(metadataBytes!), r.Expected,
            composed.PrimaryDocument, composed.Document, input, bundles.Select(x => x.ManifestHash).ToArray(),
            CoverageFanInIO.Hash(composed.Primary), CoverageFanInIO.Hash(composed.Coverage), r.DiffContext.TrackedPaths);
        state.PatchApplicability = CoverageFanInV3Encoding.Element(new { primary = claim.GetProperty("primary"), composed = claim.GetProperty("composed") });
    }
}
