using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Coverage;

/// <summary>Portable supported fan-in operations over the private coverage engine.</summary>
/// <remarks>Callers own progress/diagnostic writers. Capture and composition are separate from acceptance.
/// Supplied observations establish consistency inside a trusted-producer boundary, never cryptographic authenticity.</remarks>
public static class CoverageFanIn
{
    /// <summary>Collects the explicit complete inventory and captures only a successful required execution.</summary>
    public static ValueTask<CoverageFanInResult> CollectAndCaptureAsync(CoverageCollectRequest request,
        CancellationToken cancellationToken = default, TextWriter? progress = null, TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expected = Expected(request.Policy);
        return Execute("collect", new(expected, request.RepositoryRoot, request.OutputDirectory, request.ProducerId,
            CollectionDirectory: request.CollectionDirectory, SolutionPath: request.SolutionPath,
            DeadlineSeconds: request.DeadlineSeconds, Parallelism: request.Parallelism,
            NoRestore: request.NoRestore, NoBuild: request.NoBuild), cancellationToken, progress, diagnostics);
    }
    /// <summary>Validates a successful producer or imports a homogeneous v2 or v3 portable capture into fresh local custody.</summary>
    public static ValueTask<CoverageFanInResult> CaptureAsync(CoverageCaptureRequest request,
        CancellationToken cancellationToken = default, TextWriter? progress = null, TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.OutputMode)) throw CoveragePolicyEncoding.Invalid("outputMode");
        return Execute("capture", new(Expected(request.Policy), request.RepositoryRoot, request.OutputDirectory,
            request.ProducerId, request.CoveragePath, request.TimingsPath, request.RawCoveragePath,
            request.ImportDirectory, request.OriginalSourceRoot, request.OutputMode == CoverageOutputMode.ReplaceOwned)
        { RawCoveragePaths = request.RawCoveragePaths is null ? null : new Dictionary<string, string>(request.RawCoveragePaths, StringComparer.Ordinal) },
            cancellationToken, progress, diagnostics);
    }
    /// <summary>Composes the exact declared capture set conservatively; does not accept coverage.</summary>
    public static ValueTask<CoverageFanInResult> ComposeAsync(CoverageComposeRequest request,
        CancellationToken cancellationToken = default, TextWriter? progress = null, TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateModes(request.OutputMode, request.EvaluationIntent);
        return Execute("compose", new(Expected(request.Policy), request.RepositoryRoot, request.OutputDirectory,
            ReplaceOwned: request.OutputMode == CoverageOutputMode.ReplaceOwned,
            PrimaryCaptureDirectory: request.PrimaryCaptureDirectory,
            SupplementaryCaptureDirectories: Freeze(request.SupplementaryCaptureDirectories),
            HistoricalReplay: request.EvaluationIntent == CoverageEvaluationIntent.HistoricalReplay), cancellationToken, progress, diagnostics);
    }
    /// <summary>Runs primary aggregate and explicit patch gates, then independently verifies before committing acceptance.</summary>
    /// <remarks>V3 requires an expected canonical patch SHA-256 for patch evaluation. A valid N/A dimension means
    /// no changed measurements were reported under the selected collector profile; source completeness is not proved.
    /// Every required capture and primary aggregate gate remains mandatory.</remarks>
    public static ValueTask<CoverageFanInResult> AcceptAsync(CoverageAcceptRequest request,
        CancellationToken cancellationToken = default, TextWriter? progress = null, TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateModes(request.OutputMode, request.EvaluationIntent);
        var expected = Expected(request.Policy);
        var patchHash = request.ExpectedPatchSha256;
        if (expected.ContractVersion == 3 && request.Policy.Gate.Mode == CoverageGateMode.AggregateAndPatch && patchHash is null
            || patchHash is not null && (request.Policy.Gate.Mode != CoverageGateMode.AggregateAndPatch
            || patchHash.Length != 64 || !patchHash.All(char.IsAsciiHexDigit)))
            throw CoveragePolicyEncoding.Invalid("expectedPatchSha256");
        return Execute("accept", new(expected, request.RepositoryRoot, request.OutputDirectory,
            ReplaceOwned: request.OutputMode == CoverageOutputMode.ReplaceOwned,
            PrimaryCaptureDirectory: request.PrimaryCaptureDirectory,
            SupplementaryCaptureDirectories: Freeze(request.SupplementaryCaptureDirectories),
            ComposedOutputDirectory: request.ComposedOutputDirectory,
            HistoricalReplay: request.EvaluationIntent == CoverageEvaluationIntent.HistoricalReplay)
        { ExpectedPatchSha256 = patchHash?.ToLowerInvariant() }, cancellationToken, progress, diagnostics);
    }
    /// <summary>Verifies existing accepted evidence read-only; cannot create custody or upgrade historical evidence.</summary>
    /// <remarks>V3 requires the matching clean source checkout, complete pinned Git history, supported Git and SDK.
    /// Retaining a diff alone is insufficient to replay applicability; rejected generations cannot be promoted.</remarks>
    public static ValueTask<CoverageFanInResult> VerifyAsync(CoverageVerifyRequest request,
        CancellationToken cancellationToken = default, TextWriter? progress = null, TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.EvaluationIntent)) throw CoveragePolicyEncoding.Invalid("evaluationIntent");
        return Execute("verify", new(Expected(request.Policy), request.RepositoryRoot,
            PrimaryCaptureDirectory: request.PrimaryCaptureDirectory,
            SupplementaryCaptureDirectories: Freeze(request.SupplementaryCaptureDirectories),
            ComposedOutputDirectory: request.ComposedOutputDirectory,
            AcceptedOutputDirectory: request.AcceptedOutputDirectory,
            HistoricalReplay: request.EvaluationIntent == CoverageEvaluationIntent.HistoricalReplay), cancellationToken, progress, diagnostics);
    }
    private static IReadOnlyList<string> Freeze(IReadOnlyList<string> paths) => paths is null
        ? throw CoveragePolicyEncoding.Invalid("supplementaryCaptureDirectories") : Array.AsReadOnly(paths.ToArray());
    private static void ValidateModes(CoverageOutputMode output, CoverageEvaluationIntent intent)
    {
        if (!Enum.IsDefined(output) || !Enum.IsDefined(intent)) throw CoveragePolicyEncoding.Invalid("mode");
    }
    private static CoverageFanInExpected Expected(CoverageFanInPolicy policy)
    {
        var frozen = CoveragePolicyEncoding.Freeze(policy);
        return new(CoveragePolicyEncoding.ToElement(frozen), CoveragePolicyEncoding.Digest(frozen));
    }
    private static async ValueTask<CoverageFanInResult> Execute(string operation, CoverageFanInEngineRequest request,
        CancellationToken token, TextWriter? progress, TextWriter? diagnostics)
    {
        var result = await CoverageFanInEngine.ExecuteAsync(operation, request,
            CoverageTextWriters.Create(progress ?? TextWriter.Null, diagnostics ?? TextWriter.Null), token).ConfigureAwait(false);
        return new(result.Operation, Enum.Parse<CoverageOperationOutcome>(result.Outcome), result.OperationId, result.Digest,
            new(result.Created, result.Committed, result.CleanupComplete, result.Imported, result.Generation),
            result.Artifacts.Select(x => new CoverageArtifactReference(x.Role, x.Path, x.Length, x.Sha256)).ToArray(),
            result.Diagnostics.Select(x => new CoverageDiagnostic(x.Code, x.Stage, x.Field, x.Recovery)
            {
                Cause = x.Cause,
                Context = x.Context is null ? null : new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(x.Context, StringComparer.Ordinal)),
                Trace = x.Trace is null ? null : Array.AsReadOnly(x.Trace.ToArray())
            }).ToArray(),
            result.Accepted, result.Verified, result.PatchOutcome,
            result.Observation is null ? null : Enum.Parse<CoverageObservation>(result.Observation),
            result.HistoricalReplay ? CoverageEvaluationIntent.HistoricalReplay : CoverageEvaluationIntent.CurrentTrustedRun,
            result.PrimaryImported, result.PatchApplicability is { } applicability
                ? applicability.Deserialize<CoveragePatchApplicability>(CoveragePolicyEncoding.Json) : null,
            result.Accounting is { } accounting
                ? new(accounting.UsedBytes, accounting.LimitBytes, accounting.XmlBytes, accounting.JsonBytes,
                    accounting.LineRecords, accounting.BranchRecords, accounting.StagedBytes, accounting.StagedFiles)
                : null);
    }
}
