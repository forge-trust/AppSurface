using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>
/// Coordinates deterministic EvidenceHost CLI file operations for the AppSurface command assembly.
/// </summary>
internal sealed class EvidenceCliWorkflow
{
    /// <summary>The separate provenance and gate checks to perform after reading an evidence report.</summary>
    internal const string ManifestProvenanceNotice = "Check manifest provenance and gate requirements separately.";

    private const string GeneratedMarker = "appsurface-evidence-starter-v1";
    private const string LocalReusedNextAction = "Local reused evidence is complete for this invocation, but it is informational and cannot satisfy a gate. Run fresh evidence for gate admission.";
    private readonly EvidencePlanner _planner;
    private readonly IEvidenceInputFileAccess _inputFileAccess;

    /// <summary>
    /// Initializes the workflow with the planner and the file opener used for Evidence inputs.
    /// </summary>
    /// <param name="planner">The nonnull planner that resolves normalized paths against the selected policy.</param>
    /// <param name="inputFileAccess">
    /// Optional opener for policy, plan, manifest, and explicit diff files. The workflow owns each returned
    /// stream and counts its bytes as they arrive. The default opens files on the physical file system.
    /// </param>
    public EvidenceCliWorkflow(
        EvidencePlanner planner,
        IEvidenceInputFileAccess? inputFileAccess = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _inputFileAccess = inputFileAccess ?? new PhysicalEvidenceInputFileAccess();
    }

    /// <summary>
    /// Writes the marked v1 starter files beneath <paramref name="rootPath"/>.
    /// </summary>
    /// <remarks>
    /// Existing unmarked files are never overwritten and fail with <c>ASEVD201</c>; existing marked files require
    /// <paramref name="force"/> and otherwise fail with <c>ASEVD202</c>.
    /// </remarks>
    public async Task<EvidenceInitializationResult> InitializeAsync(string rootPath, bool force, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = Path.GetFullPath(rootPath);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Path.Join(root, "evidence.policy.json")] = CreateSamplePolicy(),
            [Path.Join(root, "EvidenceHost.cs")] = CreateEvidenceHostStarter(),
            [Path.Join(root, "README.md")] = CreateStarterReadme(),
        };
        var existingUnmarked = files.Keys.Where(path => File.Exists(path) && !ContainsGeneratedMarker(path)).ToArray();
        if (existingUnmarked.Length > 0)
        {
            throw new EvidenceCliException(
                "ASEVD201",
                $"Evidence init will not overwrite unmarked file '{existingUnmarked[0]}'.",
                "Choose an empty --root, remove the file, or use --force only after marking it as an AppSurface Evidence starter.");
        }

        if (!force && files.Keys.Any(File.Exists))
        {
            throw new EvidenceCliException(
                "ASEVD202",
                "Evidence init found an existing generated starter.",
                "Review the starter and rerun with --force only when replacement is intentional.");
        }

        Directory.CreateDirectory(root);
        foreach (var (path, content) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        }

        return new EvidenceInitializationResult(root, files.Keys.OrderBy(static path => path, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Resolves an evidence plan and reports structural prerequisites without provisioning resources or running producers.
    /// </summary>
    /// <remarks>
    /// A registered verifier must validate the execution envelope in the consumer's protected workflow.
    /// Planning and CI environment values provide no admission.
    /// </remarks>
    public async Task<EvidenceDoctorReport> DoctorAsync(EvidencePlanningRequest request, CancellationToken cancellationToken)
    {
        var plan = await ExplainAsync(request, cancellationToken).ConfigureAwait(false);
        var checks = new List<EvidenceDoctorCheck>
        {
            new("policy", "ready", $"Policy '{plan.PolicyId}' resolved profile '{plan.Profile.Id}'.", null),
            new("diff", "ready", $"Resolved {plan.ChangedPaths.Count} normalized changed path(s).", null),
        };
        foreach (var producer in plan.Profile.Producers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (producer.Kind.Contains("postgres", StringComparison.OrdinalIgnoreCase)
                || producer.RequiredResources.Any(static resource => resource.Contains("postgres", StringComparison.OrdinalIgnoreCase)))
            {
                checks.Add(CheckDocker(producer.Id));
            }

            if (producer.Kind.Contains("browser", StringComparison.OrdinalIgnoreCase)
                || producer.Kind.Contains("playwright", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(CheckBrowser(producer.Id));
            }
        }

        checks.Add(new EvidenceDoctorCheck(
            "trusted-envelope",
            "unverified",
            "The execution envelope is unverified. Planning and CI environment values grant no admission.",
            "For release evidence, use the registered verifier in the consumer's protected workflow. See https://github.com/forge-trust/AppSurface/blob/main/guides/evidencehost-cookbook.md#release-evidence"));

        return new EvidenceDoctorReport(plan, checks);
    }

    /// <summary>
    /// Resolves the checked-in policy and explicit changed paths into a deterministic evidence plan.
    /// </summary>
    /// <remarks>
    /// Missing, malformed, or empty CLI inputs produce the stable <c>ASEVD204</c> through <c>ASEVD207</c> diagnostics.
    /// A hunked diff without Git file headers is rejected with <c>ASEVD128</c> rather than planning from incomplete paths.
    /// </remarks>
    public async Task<EvidencePlan> ExplainAsync(EvidencePlanningRequest request, CancellationToken cancellationToken)
    {
        return (await ResolveAsync(request, cancellationToken).ConfigureAwait(false)).Plan;
    }

    /// <summary>
    /// Resolves an Evidence plan and retains the bounded diff snapshot used for planning.
    /// </summary>
    /// <remarks>
    /// Only <c>evidence run</c> retains the snapshot through producer execution. Explain and doctor resolve the same
    /// inputs through this method but discard it after planning because they never evaluate a patch gate.
    /// </remarks>
    internal async Task<EvidencePlanningResolution> ResolveAsync(EvidencePlanningRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var policy = await ReadJsonInputAsync<EvidencePolicy>(request.PolicyPath, JsonInputKind.Policy, cancellationToken)
            .ConfigureAwait(false);
        var inputs = await ReadPathsAndSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
        var plan = inputs.Snapshot is null
            ? _planner.Resolve(policy, inputs.Paths)
            : _planner.ResolvePatch(policy, inputs.Snapshot.Bytes, inputs.Paths);
        return new EvidencePlanningResolution(plan, inputs.Snapshot);
    }

    /// <summary>Builds the CLI refusal for a profile whose resources require a consumer-owned host.</summary>
    /// <param name="plan">The selected resource-backed plan.</param>
    /// <param name="observationOnly">Whether the command requested observation-only execution.</param>
    /// <returns>The manifest with every resource and producer marked unavailable.</returns>
    internal static EvidenceManifest BuildResourceBackedRefusal(EvidencePlan plan, bool observationOnly)
    {
        var resources = plan.Profile.Resources.Select(resource => new EvidenceResourceResult(resource.Id,
            EvidenceResourceOutcome.Unavailable, 0,
            "Resource-backed evidence requires an explicitly registered consumer EvidenceHost; the CLI does not provision or infer consumer resources.")).ToArray();
        var producers = plan.Profile.Producers.Select(producer => new EvidenceProducerResult(producer.Id,
            EvidenceProducerOutcome.Unavailable, [],
            "The selected profile requires consumer-owned resources. Run it through ForgeTrust.AppSurface.Evidence.Aspire with explicit registrations.")).ToArray();
        return EvidenceManifestBuilder.Build(plan, producers, observationOnly, resourceResults: resources);
    }

    /// <summary>
    /// Writes the canonical plan and its human-readable summary to <paramref name="outputDirectory"/>.
    /// </summary>
    /// <remarks>Replaces <c>evidence-plan.json</c> and <c>evidence-summary.json</c> when they already exist.</remarks>
    /// <param name="plan">The resolved plan.</param>
    /// <param name="outputDirectory">The directory that receives the plan and summary.</param>
    /// <param name="cancellationToken">Cancels writing the outputs.</param>
    /// <param name="reuseDecisions">Optional reuse decisions to include in the human-readable summary.</param>
    /// <exception cref="EvidenceCliException">The generated plan exceeds the JSON input ceiling (<c>ASEVD217</c>).</exception>
    public Task WritePlanAsync(
        EvidencePlan plan,
        string outputDirectory,
        CancellationToken cancellationToken,
        IReadOnlyList<EvidenceReuseSummaryDecision>? reuseDecisions = null) =>
        WritePlanAsync(plan, outputDirectory, retirePreviousRun: false, cancellationToken, reuseDecisions);

    /// <summary>
    /// Validates the canonical plan size, retires earlier run output, and writes the new plan and summary.
    /// </summary>
    /// <remarks>Call before starting any producer. An oversized plan leaves the earlier run intact.</remarks>
    /// <exception cref="EvidenceCliException">
    /// The plan exceeds the JSON input ceiling (<c>ASEVD217</c>), or a linked artifact directory cannot be retired
    /// (<c>ASEVD216</c>).
    /// </exception>
    public Task WriteRunPlanAsync(EvidencePlan plan, string outputDirectory, CancellationToken cancellationToken) =>
        WritePlanAsync(plan, outputDirectory, retirePreviousRun: true, cancellationToken, reuseDecisions: null);

    private static async Task WritePlanAsync(
        EvidencePlan plan,
        string outputDirectory,
        bool retirePreviousRun,
        CancellationToken cancellationToken,
        IReadOnlyList<EvidenceReuseSummaryDecision>? reuseDecisions)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var json = SerializeBoundedOutput(plan, "plan");
        if (retirePreviousRun)
        {
            TestEvidenceRunSupport.RetireOutputs(outputDirectory, plan);
        }

        await WriteOutputAsync(
            outputDirectory,
            Path.Join(outputDirectory, "evidence-plan.json"),
            json,
            () => CreateSummary(plan, reuseDecisions),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the canonical manifest and its human-readable summary to <paramref name="outputDirectory"/>.
    /// </summary>
    /// <remarks>Replaces <c>evidence-manifest.json</c> and <c>evidence-summary.json</c> when they already exist.</remarks>
    /// <param name="manifest">The completed run manifest.</param>
    /// <param name="outputDirectory">The directory that receives the manifest and summary.</param>
    /// <param name="cancellationToken">Cancels writing the outputs.</param>
    /// <param name="reuseDecisions">Optional reuse decisions to include in the human-readable summary.</param>
    /// <exception cref="EvidenceCliException">The generated manifest exceeds the JSON input ceiling (<c>ASEVD217</c>).</exception>
    public async Task WriteManifestAsync(
        EvidenceManifest manifest,
        string outputDirectory,
        CancellationToken cancellationToken,
        IReadOnlyList<EvidenceReuseSummaryDecision>? reuseDecisions = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await WriteOutputAsync(
            outputDirectory,
            Path.Join(outputDirectory, "evidence-manifest.json"),
            SerializeBoundedOutput(manifest, "manifest"),
            () => CreateSummary(manifest, reuseDecisions),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that a manifest is canonical and is bound to the supplied plan without rerunning producers.
    /// </summary>
    /// <remarks>
    /// Missing files and invalid paths produce <c>ASEVD208</c>, malformed canonical JSON produces <c>ASEVD209</c>, and a missing,
    /// unresolvable, or non-binding policy snapshot produces <c>ASEVD203</c>.
    /// </remarks>
    public Task<(EvidencePlan Plan, EvidenceManifest Manifest)> VerifyAsync(
        string planPath,
        string manifestPath,
        CancellationToken cancellationToken,
        bool requireGateAdmission = false) =>
        VerifyAsync(planPath, manifestPath, artifactRoot: null, cancellationToken, requireGateAdmission);

    /// <summary>
    /// Verifies the plan and manifest binding, then checks every manifest-listed artifact on disk.
    /// </summary>
    /// <param name="planPath">Generated <c>evidence-plan.json</c> path.</param>
    /// <param name="manifestPath">Generated <c>evidence-manifest.json</c> path.</param>
    /// <param name="artifactRoot">
    /// Directory holding one subdirectory per producer id; <see langword="null"/> or blank means the <c>artifacts</c>
    /// directory beside the manifest file.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="requireGateAdmission">Whether to also require a gate-eligible manifest.</param>
    /// <returns>The verified plan and manifest.</returns>
    /// <remarks>
    /// Adds <c>ASEVD213</c> to the codes of the three-argument overload: a listed artifact is missing, linked, outside
    /// the artifact root, or has a different length or SHA-256. A manifest that lists no artifacts never touches the
    /// artifact root, so manifests written before artifacts existed verify as before. Success proves integrity, not
    /// that a gate passed: an intact manifest whose claim is <c>None</c> verifies.
    /// </remarks>
    public async Task<(EvidencePlan Plan, EvidenceManifest Manifest)> VerifyAsync(
        string planPath,
        string manifestPath,
        string? artifactRoot,
        CancellationToken cancellationToken,
        bool requireGateAdmission = false)
    {
        var verified = await VerifyBindingAsync(planPath, manifestPath, cancellationToken, requireGateAdmission).ConfigureAwait(false);
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var useDefaultRoot = string.IsNullOrWhiteSpace(artifactRoot);
        var root = useDefaultRoot ? Path.Join(outputDirectory, "artifacts") : artifactRoot!;
        foreach (var producer in verified.Manifest.ProducerResults)
        {
            foreach (var artifact in producer.Artifacts ?? [])
            {
                var usesCoverageCustody = useDefaultRoot && artifact.LogicalName == CoverageEvidenceInventory.LogicalName
                    && verified.Plan.Profile.Producers.Any(declaration => declaration.Id == producer.ProducerId
                        && declaration.IsCoverageProducer());
                var status = usesCoverageCustody
                    ? await CheckCoverageCustodyAsync(outputDirectory, verified.Plan, producer.ProducerId, artifact, cancellationToken).ConfigureAwait(false)
                    : await EvidenceArtifactFileValidation.CheckAsync(root, producer.ProducerId, artifact, cancellationToken).ConfigureAwait(false);
                if (status != EvidenceArtifactFileStatus.Valid)
                {
                    throw new EvidenceCliException(
                        "ASEVD213",
                        $"Evidence artifact '{artifact.LogicalName}' of producer '{producer.ProducerId}' failed its on-disk check ({status}) under '{(usesCoverageCustody ? outputDirectory : root)}'.",
                        "Rerun appsurface evidence run and verify the plan, manifest, and artifacts directory from that one run; pass --artifact-root if the artifacts were moved.");
                }
            }
        }

        return verified;
    }

    private static Task<EvidenceArtifactFileStatus> CheckCoverageCustodyAsync(
        string outputDirectory,
        EvidencePlan plan,
        string producerId,
        EvidenceArtifactResult artifact,
        CancellationToken cancellationToken)
    {
        var producerPath = EvidenceReuseOutput.GetCustodyComponents(producerId, plan.Profile.Producers.Count == 1);
        return EvidenceArtifactFileValidation.CheckUnderRootAsync(outputDirectory, producerPath, artifact, cancellationToken);
    }

    private async Task<(EvidencePlan Plan, EvidenceManifest Manifest)> VerifyBindingAsync(
        string planPath,
        string manifestPath,
        CancellationToken cancellationToken,
        bool requireGateAdmission)
    {
        var plan = await ReadJsonInputAsync<EvidencePlan>(planPath, JsonInputKind.Plan, cancellationToken)
            .ConfigureAwait(false);
        var manifest = await ReadJsonInputAsync<EvidenceManifest>(manifestPath, JsonInputKind.Manifest, cancellationToken)
            .ConfigureAwait(false);
        if (plan.PolicySnapshot is null)
        {
            throw new EvidenceCliException(
                "ASEVD203",
                "Evidence plan does not contain the canonical policy snapshot required for verification.",
                "Regenerate evidence-plan.json with the current AppSurface EvidenceHost command.");
        }

        EvidencePlan resolvedPlan;
        try
        {
            resolvedPlan = _planner.ResolveRecordedPlan(plan.PolicySnapshot, plan.ChangedPaths, plan.PatchSha256);
        }
        catch (EvidencePlanningException exception)
        {
            throw new EvidenceCliException("ASEVD203", $"Evidence plan policy snapshot cannot be resolved: {exception.Message}", "Regenerate evidence from a valid checked-in policy and explicit diff.");
        }

        if (!string.Equals(resolvedPlan.PlanDigest, plan.PlanDigest, StringComparison.Ordinal)
            || !(requireGateAdmission
                ? EvidenceManifestBuilder.VerifyForGate(plan, manifest)
                : EvidenceManifestBuilder.Verify(plan, manifest)))
        {
            throw new EvidenceCliException(
                "ASEVD203",
                requireGateAdmission
                    ? "Evidence manifest does not bind to the supplied plan, canonical content, or gate-admission requirements. Reused results are informational and cannot be admitted as gate evidence."
                    : "Evidence manifest does not bind to the supplied plan or canonical manifest content.",
                requireGateAdmission
                    ? "Use fresh evidence for gate admission; use ordinary evidence verify when checking integrity of a local reused manifest."
                    : "Use the plan and manifest from the same evidence output directory; do not edit generated evidence files.");
        }

        return (plan, manifest);
    }

    /// <summary>
    /// Formats a deterministic, human-readable summary of a resolved plan.
    /// </summary>
    public static string FormatSummary(EvidencePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var obligations = plan.Profile.Obligations.Count == 0
            ? "none (explicit no-evidence profile)"
            : string.Join(", ", plan.Profile.Obligations.Select(static obligation => obligation.Id));
        string[] lines =
        [
            $"Evidence plan: {plan.Profile.Id} ({plan.Profile.Scope.ToString().ToLowerInvariant()})",
            $"Why: {string.Join(", ", plan.MatchedRuleIds)}",
            $"Changed paths: {string.Join(", ", plan.ChangedPaths.Select(static path => path.Path))}",
            $"Obligations: {obligations}",
            $"Required producers: {FormatIds(plan.Profile.Producers.Select(static producer => producer.Id))}",
            $"Required resources: {FormatIds(plan.Profile.Resources.Select(static resource => resource.Id))}",
            // Test producers list their projects, filters and minimums so a reviewer sees the selection before it runs.
            .. TestEvidenceRunSupport.FormatPlan(plan),
        ];
        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatSummary(EvidencePlan plan, IReadOnlyList<EvidenceReuseSummaryDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        return string.Join(Environment.NewLine, new[] { FormatSummary(plan) }.Concat(
            decisions.Select(decision => $"Reuse {decision.ProducerId}: {decision.Status} ({decision.Reason}) {decision.Diagnostic}")));
    }

    /// <summary>
    /// Formats a deterministic, human-readable summary of an evidence claim and any remaining obligations.
    /// </summary>
    /// <remarks>The final line directs readers to verify provenance and apply gate requirements separately.</remarks>
    public static string FormatSummary(EvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var guidance = GetManifestGuidance(manifest.ClaimKind);
        return string.Join(
            Environment.NewLine,
            $"Evidence result: {manifest.ClaimKind}",
            $"Execution: {manifest.ExecutionVerdict}",
            $"Closed obligations: {FormatIds(manifest.ClosedObligationIds)}",
            $"Unmediated obligations: {FormatIds(manifest.UnmediatedObligationIds)}",
            $"Next: {guidance.Console}",
            ManifestProvenanceNotice);
    }

    internal static string FormatSummary(EvidenceManifest manifest, IReadOnlyList<EvidenceReuseSummaryDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var baseSummary = FormatSummary(manifest);
        var rows = decisions.Select(decision =>
        {
            var result = manifest.ProducerResults.SingleOrDefault(item => item.ProducerId == decision.ProducerId);
            var reused = result?.Outcome == EvidenceProducerOutcome.Reused;
            var originAge = decision.OriginCompletedAt is { } completed
                ? Math.Max(0, (DateTimeOffset.UtcNow - completed).TotalSeconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s ago"
                : "n/a";
            var restore = reused ? $"; invocation restore {manifest.Metrics.ArtifactRestoreMilliseconds}ms" : string.Empty;
            var historical = reused ? decision.OriginElapsedMilliseconds is { } elapsed
                ? $"; historical elapsed {elapsed}ms" : "; historical elapsed unavailable" : string.Empty;
            var publication = string.IsNullOrWhiteSpace(decision.Publication) ? string.Empty : $"; receipt {decision.Publication}";
            var roots = decision.ArtifactRoot is null ? string.Empty
                : $"; artifact root {decision.ArtifactRoot}" + (decision.ExecutionRoot is null ? string.Empty : $"; execution root {decision.ExecutionRoot}");
            return $"Reuse {decision.ProducerId}: {decision.Status} ({decision.Reason}) {decision.Diagnostic}{(reused ? $"; origin {originAge}{historical}{restore}" : publication)}{roots}";
        });
        return string.Join(Environment.NewLine, new[] { baseSummary }.Concat(rows));
    }

    private async Task<(IReadOnlyList<NormalizedDiffPath> Paths, EvidenceDiffSnapshot? Snapshot)> ReadPathsAndSnapshotAsync(
        EvidencePlanningRequest request,
        CancellationToken cancellationToken)
    {
        var paths = request.Paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => new NormalizedDiffPath(path))
            .ToList();
        EvidenceDiffSnapshot? snapshot = null;
        if (!string.IsNullOrWhiteSpace(request.DiffFile))
        {
            snapshot = await ReadDiffSnapshotAsync(request.DiffFile, cancellationToken).ConfigureAwait(false);
        }

        if (paths.Count == 0 && (snapshot is null || EvidenceUnifiedDiffReader.Read(snapshot.Text).Count == 0))
        {
            throw new EvidenceCliException("ASEVD207", "No changed paths were supplied.", "Pass --path at least once or provide --diff-file with a unified diff.");
        }

        return (paths, snapshot);
    }

    private async Task<EvidenceDiffSnapshot> ReadDiffSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = _inputFileAccess.OpenRead(path);
            var bytes = await ReadBoundedBytesAsync(stream, EvidencePlan.MaximumCapturedPatchBytes, path, cancellationToken).ConfigureAwait(false);

            return new EvidenceDiffSnapshot(bytes, Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (EvidenceCliException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EvidenceCliException("ASEVD206", $"Unified diff file '{path}' could not be read: {exception.Message}", "Pass a readable --diff-file or explicit --path values.");
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        Stream stream,
        long maximumBytes,
        string path,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 80 * 1024;
        var buffer = new byte[bufferSize];
        await using var captured = new MemoryStream();
        long bytesRead = 0;
        while (true)
        {
            var bytesRemainingIncludingLimitProbe = (maximumBytes - bytesRead) + 1;
            var requestedBytes = (int)Math.Min(buffer.Length, bytesRemainingIncludingLimitProbe);
            var count = await stream.ReadAsync(buffer.AsMemory(0, requestedBytes), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return captured.ToArray();
            }

            bytesRead += count;
            if (bytesRead > maximumBytes)
            {
                throw new EvidenceCliException("ASEVD206", $"Unified diff file '{path}' exceeds the {maximumBytes} byte limit.", "Pass a bounded unified diff file or use explicit --path values.");
            }

            await captured.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static EvidenceDoctorCheck CheckDocker(string producerId)
    {
        var available = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST"))
            || File.Exists("/var/run/docker.sock");
        return new EvidenceDoctorCheck(
            $"docker:{producerId}",
            available ? "ready" : "external-prerequisite",
            available ? "Docker capability is available." : $"Producer '{producerId}' requires Docker-backed disposable dependencies.",
            available ? null : "Start Docker Desktop or use the consumer CI runner that provides Docker.");
    }

    private static EvidenceDoctorCheck CheckBrowser(string producerId)
    {
        var available = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"));
        return new EvidenceDoctorCheck(
            $"browser:{producerId}",
            available ? "ready" : "external-prerequisite",
            available ? "Browser capability is configured." : $"Producer '{producerId}' requires the consumer's browser test runtime.",
            available ? null : "Install the consumer browser runtime or use its CI browser image.");
    }

    private static async Task WriteOutputAsync(
        string outputDirectory,
        string canonicalPath,
        byte[] canonicalJson,
        Func<object> createSummary,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(canonicalPath, canonicalJson, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(outputDirectory, "evidence-summary.json"),
            JsonSerializer.Serialize(createSummary(), new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static byte[] SerializeBoundedOutput<TValue>(TValue value, string kind)
    {
        var json = EvidenceCanonicalJson.Serialize(value);
        if (json.Length > EvidenceCanonicalJson.MaximumInputBytes)
        {
            throw new EvidenceCliException(
                "ASEVD217",
                $"The generated Evidence {kind} exceeds the {EvidenceCanonicalJson.MaximumInputBytes} byte verification limit.",
                "Shorten descriptive policy text, changed-path inputs, or evidence diagnostics and rerun.");
        }

        return json;
    }

    private async Task<TValue> ReadJsonInputAsync<TValue>(
        string path,
        JsonInputKind kind,
        CancellationToken cancellationToken)
    {
        var isPolicy = kind == JsonInputKind.Policy;
        var inputName = kind switch
        {
            JsonInputKind.Policy => "Evidence policy",
            JsonInputKind.Plan => "Evidence plan",
            _ => "Evidence manifest",
        };
        var pathCode = isPolicy ? "ASEVD204" : "ASEVD208";

        try
        {
            await using var stream = OpenInput();
            return await EvidenceCanonicalJson.DeserializeAsync<TValue>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException
            or UnauthorizedAccessException or SecurityException)
        {
            const string guide = " See https://github.com/forge-trust/AppSurface/blob/main/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/README.md#bounded-json-input.";
            var fix = isPolicy
                ? "Use a readable policy with unique properties and named enum values within the input limit, then rerun doctor or explain."
                : "Regenerate readable plan and manifest files with the current supported schema within the input limit.";
            throw new EvidenceCliException(
                isPolicy ? "ASEVD205" : "ASEVD209",
                $"{inputName} could not be read as supported, unambiguous JSON within the 20 MiB input limit.",
                fix + guide);
        }

        Stream OpenInput()
        {
            try
            {
                return _inputFileAccess.OpenRead(path);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new EvidenceCliException(
                    pathCode,
                    $"{inputName} '{path}' does not exist.",
                    isPolicy
                        ? "Run 'appsurface evidence init --sample' or pass --policy with a checked-in policy path."
                        : "Pass paths from the same generated evidence output directory.");
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                throw new EvidenceCliException(
                    pathCode,
                    $"{inputName} path is empty or invalid.",
                    isPolicy
                        ? "Run 'appsurface evidence init --sample' or pass --policy with a valid checked-in policy path."
                        : "Pass valid plan and manifest paths from the same generated evidence output directory.");
            }
        }
    }

    private enum JsonInputKind
    {
        Policy,
        Plan,
        Manifest,
    }

    private static bool ContainsGeneratedMarker(string path) => File.ReadAllText(path).Contains(GeneratedMarker, StringComparison.Ordinal);

    private static PlanSummary CreateSummary(EvidencePlan plan, IReadOnlyList<EvidenceReuseSummaryDecision>? reuseDecisions)
    {
        var obligations = plan.Profile.Obligations.Select(static obligation => obligation.Id).ToArray();
        var testProducers = reuseDecisions is null ? TestEvidenceRunSupport.DescribePlan(plan) : null;

        return new("plan", plan.Profile.Id, plan.Profile.Scope.ToString(), plan.MatchedRuleIds, obligations,
            testProducers is { Length: > 0 } ? testProducers : null, reuseDecisions,
            "Run 'appsurface evidence run' only when the selected producer capabilities are ready.");
    }

    private static ManifestSummary CreateSummary(EvidenceManifest manifest, IReadOnlyList<EvidenceReuseSummaryDecision>? reuseDecisions)
        => new("manifest", manifest.ClaimKind.ToString(), manifest.ExecutionVerdict.ToString(),
            manifest.Eligibility.ToString(), manifest.EnvelopeStatus.ToString(), manifest.ClosedObligationIds,
            manifest.UnmediatedObligationIds, reuseDecisions, GetManifestGuidance(manifest.ClaimKind).Json);

    private sealed record PlanSummary(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("profile")] string Profile,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("matchedRules")] IReadOnlyList<string> MatchedRules,
        [property: JsonPropertyName("obligations")] IReadOnlyList<string> Obligations,
        [property: JsonPropertyName("testProducers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object[]? TestProducers,
        [property: JsonPropertyName("reuseDecisions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<EvidenceReuseSummaryDecision>? ReuseDecisions,
        [property: JsonPropertyName("next")] string Next);

    private sealed record ManifestSummary(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("claim")] string Claim,
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("eligibility")] string Eligibility,
        [property: JsonPropertyName("envelopeStatus")] string EnvelopeStatus,
        [property: JsonPropertyName("closedObligations")] IReadOnlyList<string> ClosedObligations,
        [property: JsonPropertyName("unmediatedObligations")] IReadOnlyList<string> UnmediatedObligations,
        [property: JsonPropertyName("reuseDecisions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<EvidenceReuseSummaryDecision>? ReuseDecisions,
        [property: JsonPropertyName("next")] string Next);

    private static (string Console, string Json) GetManifestGuidance(EvidenceClaimKind claim)
    {
        const string gateReady = "Evidence claim is ready for its declared eligible consumer.";
        return claim switch
        {
            EvidenceClaimKind.ReleaseComplete => ("Release evidence is complete.", gateReady),
            EvidenceClaimKind.TargetedComplete => ("Targeted evidence is complete.", gateReady),
            EvidenceClaimKind.NoEvidenceRequired => ("No evidence is required by the matched policy rule.", gateReady),
            EvidenceClaimKind.ObservationOnly => ("Observation recorded; it is not gate eligible.",
                "Observation recorded as informational evidence. Run fresh evidence without --observation-only for gate admission."),
            EvidenceClaimKind.LocalReusedComplete => (LocalReusedNextAction, LocalReusedNextAction),
            _ => ("Evidence is incomplete. Review unmediated obligations and producer diagnostics.",
                "Resolve the listed producer or obligation failure before gating."),
        };
    }

    private static string FormatIds(IEnumerable<string> ids)
    {
        var values = ids.OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        return values.Length == 0 ? "none" : string.Join(", ", values);
    }

    private static string CreateSamplePolicy() =>
        """
        {
          "generatedBy": "appsurface-evidence-starter-v1",
          "id": "sample-evidence-policy",
          "version": "1",
          "conservativeProfileId": "targeted-coverage",
          "profiles": [
            {
              "id": "no-evidence",
              "scope": "Targeted",
              "resources": [],
              "producers": [],
              "obligations": []
            },
            {
              "id": "targeted-coverage",
              "scope": "Targeted",
              "resources": [],
              "producers": [
                {
                  "id": "coverage",
                  "kind": "coverage",
                  "version": "1.0.0",
                  "requiredResources": [],
                  "assertionIds": [ "appsurface/coverage/behavioral-patch@1" ],
                  "artifactSlots": [],
                  "timeoutSeconds": 900,
                  "coverageGate": {
                    "minLinePercent": 95,
                    "minBranchPercent": 85,
                    "minPatchLinePercent": 95,
                    "minPatchBranchPercent": 85,
                    "patchLineMode": "codecov",
                    "tolerancePercent": 0.5
                  }
                }
              ],
              "obligations": [
                {
                  "id": "changed-behavior-covered",
                  "riskClass": "behavioral-change",
                  "rationale": "Default changed code requires behavioral coverage evidence.",
                  "requiredProducerIds": [ "coverage" ],
                  "requiredAssertionId": "appsurface/coverage/behavioral-patch@1"
                }
              ]
            }
          ],
          "rules": [
            {
              "id": "documentation-only",
              "pattern": "docs/**",
              "profileId": "no-evidence",
              "precedence": 0
            }
          ]
        }
        """;

    private static string CreateEvidenceHostStarter() =>
        """
        // appsurface-evidence-starter-v1
        // Keep this EvidenceHost separate from the normal development AppHost.
        // Register consumer-owned PostgreSQL, browser, migration, or contract producers explicitly.
        // The generated policy is the control plane. Never discover producers from loaded assemblies.
        """;

    private static string CreateStarterReadme() =>
        """
        <!-- appsurface-evidence-starter-v1 -->
        # EvidenceHost starter

        1. Edit `evidence.policy.json` to name the consumer's risk profiles and explicit producer registrations.
        2. Run `appsurface evidence doctor --policy .appsurface/evidence/evidence.policy.json --path docs/README.md`.
        3. Run `appsurface evidence explain --policy .appsurface/evidence/evidence.policy.json --path src/Changed.cs`.
        4. Run `appsurface evidence run` only after required producer capabilities are ready.

        An incomplete profile is not complete evidence. Keep normal AppHost composition separate from this EvidenceHost.
        """;
}

/// <summary>
/// Describes a policy and explicit changed-path input for an EvidenceHost planning operation.
/// </summary>
internal sealed record EvidencePlanningRequest(string PolicyPath, IReadOnlyList<string> Paths, string? DiffFile);

/// <summary>
/// Binds a resolved plan to the immutable diff bytes used to derive its changed paths.
/// </summary>
/// <remarks>
/// Keep this pair together from planning through coverage execution. Reopening the source path can observe a replaced
/// diff and break the plan-to-gate integrity boundary; consumers must reuse <see cref="DiffSnapshot"/> when present.
/// </remarks>
internal sealed record EvidencePlanningResolution
{
    /// <summary>
    /// Initializes the resolved planning inputs.
    /// </summary>
    /// <param name="plan">The nonnull plan derived from explicit paths and, when supplied, the captured diff bytes.</param>
    /// <param name="diffSnapshot">
    /// The optional bounded snapshot that supplied diff-derived paths. It is null only when planning used explicit
    /// paths without <c>--diff-file</c>.
    /// </param>
    public EvidencePlanningResolution(EvidencePlan plan, EvidenceDiffSnapshot? diffSnapshot)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        DiffSnapshot = diffSnapshot;
    }

    /// <summary>
    /// Gets the nonnull plan resolved from the exact input set represented by this instance.
    /// </summary>
    public EvidencePlan Plan { get; }

    /// <summary>
    /// Gets the optional immutable diff snapshot from which the plan's diff-derived paths were read.
    /// </summary>
    /// <remarks>
    /// Pass this same value to the coverage producer for patch gates. Do not reopen a source path to obtain a newer
    /// diff, because that would no longer be the diff on which <see cref="Plan"/> was evaluated.
    /// </remarks>
    public EvidenceDiffSnapshot? DiffSnapshot { get; }
}

/// <summary>
/// Holds the bounded unified diff input consumed by an Evidence planning operation.
/// </summary>
/// <remarks>
/// The snapshot owns a defensive copy of the bytes supplied at construction. It is bounded by the planning reader to
/// 20 MiB, and consumers must reuse <see cref="Bytes"/> for planning-adjacent work such as patch coverage instead of
/// reopening the source path. This preserves the SHA-256-integrity boundary when the original file is replaced later.
/// </remarks>
internal sealed class EvidenceDiffSnapshot
{
    private const int Sha256HexLength = 64;
    private readonly byte[] _bytes;

    /// <summary>
    /// Initializes a snapshot from the exact diff bytes captured during planning.
    /// </summary>
    /// <param name="bytes">
    /// The nonnull, bounded diff bytes to copy. Ownership remains with the caller; subsequent caller mutations do not
    /// affect this snapshot.
    /// </param>
    /// <param name="label">The nonempty safe source label used in coverage artifacts and diagnostics.</param>
    /// <param name="sha256">
    /// The nonempty 64-character hexadecimal SHA-256 digest computed from <paramref name="bytes"/> at capture time.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="label"/> is blank, or <paramref name="sha256"/> is blank or not a SHA-256 hexadecimal digest.
    /// </exception>
    public EvidenceDiffSnapshot(byte[] bytes, string label, string sha256)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != Sha256HexLength || !sha256.All(static character => char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("The diff snapshot SHA-256 digest must be a 64-character hexadecimal string.", nameof(sha256));
        }

        _bytes = bytes.ToArray();
        Label = label;
        Sha256 = sha256;
    }

    /// <summary>
    /// Gets a read-only view of the exact bytes defensively copied at construction.
    /// </summary>
    /// <remarks>
    /// This view avoids another copy for internal consumers, but it exposes no writable access. Reuse it for patch
    /// coverage and related planning work; reopening the original file can violate the plan-to-gate integrity rule.
    /// </remarks>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>
    /// Gets the nonempty safe source label used in coverage artifacts and diagnostics.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the nonempty 64-character hexadecimal SHA-256 digest of <see cref="Bytes"/> at capture time.
    /// </summary>
    public string Sha256 { get; }

    /// <summary>
    /// Gets the UTF-8 decoding of <see cref="Bytes"/> used by the planner and coverage core.
    /// </summary>
    /// <remarks>
    /// This value is derived only from the captured bytes. It must not be replaced with a fresh read of the original
    /// diff path after planning.
    /// </remarks>
    public string Text => Encoding.UTF8.GetString(_bytes);
}

/// <summary>
/// Opens explicit JSON or unified diff inputs for Evidence planning and structural verification.
/// </summary>
/// <remarks>
/// This narrow internal boundary keeps the production reader file-backed while allowing tests to prove that the
/// bounded stream reader evaluates the bytes available when a path is actually opened, not stale file metadata.
/// </remarks>
internal interface IEvidenceInputFileAccess
{
    /// <summary>
    /// Opens <paramref name="path"/> for sequential read access.
    /// </summary>
    /// <param name="path">The explicit policy, plan, manifest or <c>--diff-file</c> input path.</param>
    /// <returns>A readable stream owned and disposed by the Evidence workflow.</returns>
    Stream OpenRead(string path);
}

/// <summary>
/// Opens Evidence inputs through the local physical file system.
/// </summary>
internal sealed class PhysicalEvidenceInputFileAccess : IEvidenceInputFileAccess
{
    /// <inheritdoc />
    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 80 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}

/// <summary>
/// Describes starter files created by an EvidenceHost initialization operation.
/// </summary>
internal sealed record EvidenceInitializationResult(string RootPath, IReadOnlyList<string> CreatedFiles);

/// <summary>
/// Describes one environment or capability prerequisite observed by the evidence doctor.
/// </summary>
internal sealed record EvidenceDoctorCheck(string Id, string Status, string Message, string? NextAction);

/// <summary>
/// Describes the plan and prerequisite checks produced by the evidence doctor.
/// </summary>
internal sealed record EvidenceDoctorReport(EvidencePlan Plan, IReadOnlyList<EvidenceDoctorCheck> Checks);

/// <summary>
/// Represents a stable, user-actionable EvidenceHost CLI failure.
/// </summary>
internal sealed class EvidenceCliException : InvalidOperationException
{
    public EvidenceCliException(string code, string problem, string fix)
        : base($"{code}: {problem} Fix: {fix}")
    {
        Code = code;
        Fix = fix;
    }

    /// <summary>Gets the stable EvidenceHost diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the recommended recovery action.</summary>
    public string Fix { get; }
}
