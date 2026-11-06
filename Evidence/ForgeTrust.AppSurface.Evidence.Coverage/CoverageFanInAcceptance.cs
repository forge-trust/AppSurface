using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

internal static partial class CoverageFanInEngine
{
    private static Dictionary<string, byte[]> Compose(CoverageFanInEngineRequest r, CoverageFanInIO io,
        IReadOnlyList<CoverageFanInBundle> bundles)
    {
        var inputs = bundles.Select(x => (x.Document, x.Manifest.GetProperty("sourceRoot").GetString()!)).ToArray();
        var composed = CoverageFanInComposer.Compose(inputs);
        var primary = new XDocument(bundles[0].Document);
        CoverageFanInComposer.NormalizePaths(primary, inputs[0].Item2);
        CoverageFanInIndependentVerifier.VerifyComposition(inputs, composed, primary);
        var coverage = io.EncodeXml(composed); var primaryBytes = io.EncodeXml(primary);
        return new(StringComparer.Ordinal)
        {
            [CoverageName] = coverage,
            [PrimaryName] = primaryBytes,
            [ReceiptName] = Serialize(new
            {
                schemaVersion = 2,
                policyDigest = r.Expected.Digest,
                source = r.Expected.Source,
                run = r.Expected.Run,
                inputs = bundles.Select(x => new { producerId = x.Manifest.GetProperty("producerId").GetString(), manifestSha256 = x.ManifestHash }).ToArray(),
                coverageSha256 = CoverageFanInIO.Hash(coverage),
                primarySha256 = CoverageFanInIO.Hash(primaryBytes)
            })
        };
    }

    private static (byte[] Coverage, byte[] Primary, XDocument Document, XDocument PrimaryDocument, JsonElement Receipt)
        ReadComposition(CoverageFanInEngineRequest r, CoverageFanInIO io, IReadOnlyList<CoverageFanInBundle> bundles)
    {
        var directory = r.ComposedOutputDirectory!;
        VerifyGeneration(r, io, directory, "composition", [CoverageName, PrimaryName, ReceiptName]);
        var receipt = Parse(io.Read(Path.Join(directory, ReceiptName)));
        ExactProperties(receipt, ["schemaVersion", "policyDigest", "source", "run", "inputs", "coverageSha256", "primarySha256"]);
        var coverage = io.Read(Path.Join(directory, CoverageName), xml: true);
        var primary = io.Read(Path.Join(directory, PrimaryName), xml: true);
        if (receipt.GetProperty("schemaVersion").GetInt32() != 2 || receipt.GetProperty("policyDigest").GetString() != r.Expected.Digest
            || !Same(receipt.GetProperty("source"), r.Expected.Source) || !Same(receipt.GetProperty("run"), r.Expected.Run)
            || receipt.GetProperty("coverageSha256").GetString() != CoverageFanInIO.Hash(coverage)
            || receipt.GetProperty("primarySha256").GetString() != CoverageFanInIO.Hash(primary))
            throw new InvalidDataException("Composition receipt differs.");
        var inputList = receipt.GetProperty("inputs").EnumerateArray().ToArray();
        if (inputList.Length != bundles.Count) throw new InvalidDataException("Composition capture inventory differs.");
        for (var i = 0; i < bundles.Count; i++)
            if (inputList[i].GetProperty("manifestSha256").GetString() != bundles[i].ManifestHash
                || inputList[i].GetProperty("producerId").GetString() != bundles[i].Manifest.GetProperty("producerId").GetString())
                throw new InvalidDataException("Composition capture identity differs.");
        var document = io.Xml(coverage); var primaryDocument = io.Xml(primary);
        CoverageFanInIndependentVerifier.VerifyComposition(bundles.Select(x =>
            (x.Document, x.Manifest.GetProperty("sourceRoot").GetString()!)).ToArray(), document, primaryDocument);
        return (coverage, primary, document, primaryDocument, receipt);
    }

    private sealed record AcceptanceCandidate(Dictionary<string, byte[]> Files, bool Passed);

    private static async Task<AcceptanceCandidate> AcceptAsync(CoverageFanInEngineRequest r, CoverageFanInIO io,
        IReadOnlyList<CoverageFanInBundle> bundles, CancellationToken token)
    {
        var composed = ReadComposition(r, io, bundles);
        var patchRequired = r.Expected.Gate.GetProperty("mode").GetString() == "aggregateAndPatch";
        var diff = patchRequired ? await GitDiffReader.ReadDiffAsync(Full(r.RepositoryRoot),
            r.Expected.Gate.GetProperty("diffBase").GetString()!, token).ConfigureAwait(false) : null;
        var diffHash = diff is null ? null : CoverageFanInIO.Hash(Encoding.UTF8.GetBytes(diff));
        if (r.ExpectedPatchSha256 is { } expected && !string.Equals(diffHash, expected, StringComparison.Ordinal))
            throw new InvalidDataException("Coverage patch differs from the planned patch.");
        var primary = await GateAsync(r, composed.Primary, PrimaryName, patch: patchRequired,
            requireAggregate: true, diff, token).ConfigureAwait(false);
        var patch = patchRequired ? await GateAsync(r, composed.Coverage, CoverageName, patch: true,
            requireAggregate: false, diff, token).ConfigureAwait(false) : primary;
        var primaryGate = GateJson(primary); var composedGate = GateJson(patch);
        if (!primary.Passed || !patch.Passed)
        {
            var rejected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [PrimaryGateName] = primaryGate,
                ["primary-gate.md"] = Encoding.UTF8.GetBytes(CoverageGateReportWriter.RenderMarkdown(primary)),
                ["rejection.json"] = Serialize(new
                {
                    schemaVersion = 2,
                    policyDigest = r.Expected.Digest,
                    source = r.Expected.Source,
                    run = r.Expected.Run,
                    accepted = false,
                    reason = "gate-failed"
                })
            };
            if (patchRequired)
            {
                rejected[ComposedGateName] = composedGate;
                rejected["composed-gate.md"] = Encoding.UTF8.GetBytes(CoverageGateReportWriter.RenderMarkdown(patch));
                rejected["diff.patch"] = Encoding.UTF8.GetBytes(diff!);
            }
            return new(rejected, false);
        }
        CoverageFanInIndependentVerifier.VerifyGates(Parse(primaryGate), Parse(composedGate), r.Expected.Policy,
            composed.PrimaryDocument, composed.Document, diff);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PrimaryGateName] = primaryGate,
            ["primary-gate.md"] = Encoding.UTF8.GetBytes(CoverageGateReportWriter.RenderMarkdown(primary)),
            ["acceptance.json"] = Serialize(new
            {
                schemaVersion = 2,
                policyDigest = r.Expected.Digest,
                source = r.Expected.Source,
                run = r.Expected.Run,
                evaluationIntent = r.HistoricalReplay ? "historicalReplay" : "currentTrustedRun",
                compositionReceiptSha256 = CoverageFanInIO.Hash(io.Read(Path.Join(r.ComposedOutputDirectory!, ReceiptName))),
                inputs = bundles.Select(x => x.ManifestHash).ToArray(),
                patchOutcome = PatchOutcome(r),
                primaryGateSha256 = CoverageFanInIO.Hash(primaryGate),
                composedGateSha256 = patchRequired ? CoverageFanInIO.Hash(composedGate) : null,
                diffSha256 = diffHash
            })
        };
        if (patchRequired)
        {
            files[ComposedGateName] = composedGate;
            files["composed-gate.md"] = Encoding.UTF8.GetBytes(CoverageGateReportWriter.RenderMarkdown(patch));
            files["diff.patch"] = Encoding.UTF8.GetBytes(diff!);
        }
        return new(files, true);
    }

    private static async Task<CoverageGateResult> GateAsync(CoverageFanInEngineRequest r, byte[] coverage, string name,
        bool patch, bool requireAggregate, string? diff, CancellationToken token)
    {
        var gate = r.Expected.Gate;
        var tolerance = gate.GetProperty("tolerance").GetDecimal();
        var diffBase = patch ? gate.GetProperty("diffBase").GetString() : null;
        CoveragePatchRequest? patchRequest = patch ? new(Full(r.RepositoryRoot),
            PatchDiffSource.ForGitBase(diffBase!, diffBase!, _ => Task.FromResult(diff!)),
            requireAggregate ? null : gate.GetProperty("minPatchLine").GetDecimal(),
            requireAggregate ? null : gate.GetProperty("minPatchBranch").GetDecimal(),
            gate.GetProperty("patchLineMode").GetString() == "codecov" ? PatchLineMode.Codecov : PatchLineMode.Measurable)
        { CoverageStreamFactory = () => new MemoryStream(coverage, writable: false) } : null;
        var request = new CoverageGateRequest(Path.Join(r.ComposedOutputDirectory!, name), r.OutputDirectory!,
            requireAggregate ? gate.GetProperty("minLine").GetDecimal() : 0,
            requireAggregate ? gate.GetProperty("minBranch").GetDecimal() : 0, patchRequest, tolerance)
        { CoverageStreamFactory = () => new MemoryStream(coverage, writable: false) };
        return await CoverageGateEvaluator.EvaluateAsync(request, token).ConfigureAwait(false);
    }

    private static byte[] GateJson(CoverageGateResult result) => Serialize(new
    {
        passed = result.Passed,
        coverage = Path.GetFileName(result.CoveragePath),
        tolerancePercent = result.TolerancePercent,
        thresholds = new { line = result.MinLinePercent, branch = result.MinBranchPercent, patchLine = result.MinPatchLinePercent, patchBranch = result.MinPatchBranchPercent },
        effectiveThresholds = new
        {
            line = Math.Max(0, result.MinLinePercent - result.TolerancePercent),
            branch = Math.Max(0, result.MinBranchPercent - result.TolerancePercent),
            patchLine = result.MinPatchLinePercent is { } pl ? (decimal?)Math.Max(0, pl - result.TolerancePercent) : null,
            patchBranch = result.MinPatchBranchPercent is { } pb ? (decimal?)Math.Max(0, pb - result.TolerancePercent) : null
        },
        patchLineMode = result.PatchLineMode?.ToString().ToLowerInvariant(),
        patchDiffSource = result.PatchDiffSource is { } source ? new
        {
            kind = "git-base",
            label = source.Label,
            strictness = "local-git",
            diffBase = source.DiffBase,
            path = (string?)null,
            bytes = source.Bytes,
            sha256 = source.Sha256,
            empty = source.Empty
        } : null,
        line = new { covered = result.LineCoverage.Covered, valid = result.LineCoverage.Valid, percent = result.LineCoverage.Percent },
        branch = new { covered = result.BranchCoverage.Covered, valid = result.BranchCoverage.Valid, percent = result.BranchCoverage.Percent },
        patchLine = result.PatchLineCoverage is { } line ? new { diffBase = line.DiffBase, changed = line.ChangedLines, measurable = line.MeasurableLines, covered = line.CoveredLines, percent = line.Percent } : null,
        patchBranch = result.PatchBranchCoverage is { } branch ? new { diffBase = branch.DiffBase, changed = branch.ChangedLines, measurable = branch.MeasurableBranches, covered = branch.CoveredBranches, percent = branch.Percent } : null
    });

    private static async Task VerifyAcceptedAsync(CoverageFanInEngineRequest r, CoverageFanInIO io,
        IReadOnlyList<CoverageFanInBundle> bundles, CancellationToken token)
    {
        var composed = ReadComposition(r, io, bundles);
        var directory = r.AcceptedOutputDirectory!;
        var patchRequired = r.Expected.Gate.GetProperty("mode").GetString() == "aggregateAndPatch";
        VerifyGeneration(r, io, directory, "acceptance", patchRequired
            ? [PrimaryGateName, "primary-gate.md", ComposedGateName, "composed-gate.md", "acceptance.json", "diff.patch"]
            : [PrimaryGateName, "primary-gate.md", "acceptance.json"]);
        var acceptance = Parse(io.Read(Path.Join(directory, "acceptance.json")));
        var primaryBytes = io.Read(Path.Join(directory, PrimaryGateName));
        var patchBytes = patchRequired ? io.Read(Path.Join(directory, ComposedGateName)) : primaryBytes;
        if (acceptance.GetProperty("schemaVersion").GetInt32() != 2
            || acceptance.GetProperty("policyDigest").GetString() != r.Expected.Digest
            || !Same(acceptance.GetProperty("source"), r.Expected.Source) || !Same(acceptance.GetProperty("run"), r.Expected.Run)
            || acceptance.GetProperty("evaluationIntent").GetString() != (r.HistoricalReplay ? "historicalReplay" : "currentTrustedRun")
            || acceptance.GetProperty("compositionReceiptSha256").GetString() != CoverageFanInIO.Hash(io.Read(Path.Join(r.ComposedOutputDirectory!, ReceiptName)))
            || acceptance.GetProperty("primaryGateSha256").GetString() != CoverageFanInIO.Hash(primaryBytes)
            || acceptance.GetProperty("patchOutcome").GetString() != PatchOutcome(r)
            || !acceptance.GetProperty("inputs").EnumerateArray().Select(x => x.GetString()).SequenceEqual(bundles.Select(x => x.ManifestHash)))
            throw new InvalidDataException("Acceptance generation differs.");
        if (patchRequired && (acceptance.GetProperty("composedGateSha256").GetString() != CoverageFanInIO.Hash(patchBytes)
            || acceptance.GetProperty("diffSha256").GetString() != CoverageFanInIO.Hash(io.Read(Path.Join(directory, "diff.patch")))))
            throw new InvalidDataException("Acceptance patch integrity differs.");
        if (patchRequired)
        {
            var expectedDiff = await GitDiffReader.ReadDiffAsync(Full(r.RepositoryRoot),
                r.Expected.Gate.GetProperty("diffBase").GetString()!, token).ConfigureAwait(false);
            if (CoverageFanInIO.Hash(Encoding.UTF8.GetBytes(expectedDiff)) != acceptance.GetProperty("diffSha256").GetString())
                throw new InvalidDataException("Expected diff snapshot differs.");
        }
        CoverageFanInIndependentVerifier.VerifyGates(Parse(primaryBytes), Parse(patchBytes), r.Expected.Policy,
            composed.PrimaryDocument, composed.Document,
            patchRequired ? Encoding.UTF8.GetString(io.Read(Path.Join(directory, "diff.patch"))) : null);
    }
}
