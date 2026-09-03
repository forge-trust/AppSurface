using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Cli;

/// <summary>
/// Coordinates deterministic EvidenceHost CLI file operations for the AppSurface command assembly.
/// </summary>
internal sealed class EvidenceCliWorkflow
{
    private const string GeneratedMarker = "appsurface-evidence-starter-v1";
    private readonly EvidencePlanner _planner;
    private readonly IEvidenceDiffFileAccess _diffFileAccess;

    /// <summary>
    /// Initializes the workflow with the planner and diff-file access used to resolve policy and changed-path inputs.
    /// </summary>
    /// <param name="planner">The nonnull planner that resolves normalized paths against the selected policy.</param>
    /// <param name="diffFileAccess">
    /// The optional access boundary that opens explicit diff files. Omit this value in production to use the physical
    /// file system; tests can supply a deterministic source that verifies the bytes observed at open time.
    /// </param>
    public EvidenceCliWorkflow(EvidencePlanner planner, IEvidenceDiffFileAccess? diffFileAccess = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _diffFileAccess = diffFileAccess ?? new PhysicalEvidenceDiffFileAccess();
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
    /// Resolves an evidence plan and reports prerequisite readiness without provisioning resources or running producers.
    /// </summary>
    public async Task<EvidenceDoctorReport> DoctorAsync(EvidencePlanningRequest request, CancellationToken cancellationToken)
    {
        var plan = await ExplainAsync(request, cancellationToken).ConfigureAwait(false);
        var checks = new List<EvidenceDoctorCheck>
        {
            new("policy", true, "ready", $"Policy '{plan.PolicyId}' resolved profile '{plan.Profile.Id}'.", null),
            new("diff", true, "ready", $"Resolved {plan.ChangedPaths.Count} normalized changed path(s).", null),
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

        if (plan.Profile.Scope == EvidenceProfileScope.Release)
        {
            var githubActions = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
            checks.Add(new EvidenceDoctorCheck(
                "trusted-envelope",
                githubActions,
                githubActions ? "ready" : "blocked",
                githubActions ? "GitHub Actions environment is available for the consumer envelope verifier." : "Release evidence requires a CI-provided trusted envelope.",
                githubActions ? null : "Run from the consumer's protected CI workflow or request observation-only evidence."));
        }

        var blocked = checks.Any(static check => string.Equals(check.Status, "blocked", StringComparison.Ordinal));
        var external = checks.Any(static check => string.Equals(check.Status, "external-prerequisite", StringComparison.Ordinal));
        return new EvidenceDoctorReport(
            blocked ? "blocked" : external ? "ready_with_external_prerequisites" : "ready",
            plan,
            checks);
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
        var policy = await ReadPolicyAsync(request.PolicyPath, cancellationToken).ConfigureAwait(false);
        var inputs = await ReadPathsAndSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
        return new EvidencePlanningResolution(_planner.Resolve(policy, inputs.Paths), inputs.Snapshot);
    }

    /// <summary>
    /// Writes the canonical plan and its human-readable summary to <paramref name="outputDirectory"/>.
    /// </summary>
    /// <remarks>Replaces <c>evidence-plan.json</c> and <c>evidence-summary.json</c> when they already exist.</remarks>
    public async Task WritePlanAsync(EvidencePlan plan, string outputDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(outputDirectory);
        await WriteCanonicalAsync(Path.Join(outputDirectory, "evidence-plan.json"), plan, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(outputDirectory, "evidence-summary.json"),
            JsonSerializer.Serialize(CreateSummary(plan), new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the canonical manifest and its human-readable summary to <paramref name="outputDirectory"/>.
    /// </summary>
    /// <remarks>Replaces <c>evidence-manifest.json</c> and <c>evidence-summary.json</c> when they already exist.</remarks>
    public async Task WriteManifestAsync(EvidenceManifest manifest, string outputDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(outputDirectory);
        await WriteCanonicalAsync(Path.Join(outputDirectory, "evidence-manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(outputDirectory, "evidence-summary.json"),
            JsonSerializer.Serialize(CreateSummary(manifest), new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies that a manifest is canonical and is bound to the supplied plan without rerunning producers.
    /// </summary>
    /// <remarks>
    /// Missing files produce <c>ASEVD208</c>, malformed canonical JSON produces <c>ASEVD209</c>, and a missing,
    /// unresolvable, or non-binding policy snapshot produces <c>ASEVD203</c>.
    /// </remarks>
    public async Task<(EvidencePlan Plan, EvidenceManifest Manifest)> VerifyAsync(
        string planPath,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var plan = await ReadCanonicalAsync<EvidencePlan>(planPath, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadCanonicalAsync<EvidenceManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
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
            resolvedPlan = _planner.Resolve(plan.PolicySnapshot, plan.ChangedPaths);
        }
        catch (EvidencePlanningException exception)
        {
            throw new EvidenceCliException("ASEVD203", $"Evidence plan policy snapshot cannot be resolved: {exception.Message}", "Regenerate evidence from a valid checked-in policy and explicit diff.");
        }

        if (!string.Equals(resolvedPlan.PlanDigest, plan.PlanDigest, StringComparison.Ordinal)
            || !EvidenceManifestBuilder.Verify(plan, manifest))
        {
            throw new EvidenceCliException(
                "ASEVD203",
                "Evidence manifest does not bind to the supplied plan or canonical manifest content.",
                "Use the plan and manifest from the same evidence output directory; do not edit generated evidence files.");
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
        return string.Join(
            Environment.NewLine,
            $"Evidence plan: {plan.Profile.Id} ({plan.Profile.Scope.ToString().ToLowerInvariant()})",
            $"Why: {string.Join(", ", plan.MatchedRuleIds)}",
            $"Changed paths: {string.Join(", ", plan.ChangedPaths.Select(static path => path.Path))}",
            $"Obligations: {obligations}",
            $"Required producers: {FormatIds(plan.Profile.Producers.Select(static producer => producer.Id))}",
            $"Required resources: {FormatIds(plan.Profile.Resources.Select(static resource => resource.Id))}");
    }

    /// <summary>
    /// Formats a deterministic, human-readable summary of an evidence claim and any remaining obligations.
    /// </summary>
    public static string FormatSummary(EvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var action = manifest.ClaimKind switch
        {
            EvidenceClaimKind.ReleaseComplete => "Release evidence is complete.",
            EvidenceClaimKind.TargetedComplete => "Targeted evidence is complete.",
            EvidenceClaimKind.NoEvidenceRequired => "No evidence is required by the matched policy rule.",
            EvidenceClaimKind.ObservationOnly => "Observation recorded; it is not gate eligible.",
            _ => "Evidence is incomplete. Review unmediated obligations and producer diagnostics.",
        };
        return string.Join(
            Environment.NewLine,
            $"Evidence result: {manifest.ClaimKind}",
            $"Execution: {manifest.ExecutionVerdict}",
            $"Closed obligations: {FormatIds(manifest.ClosedObligationIds)}",
            $"Unmediated obligations: {FormatIds(manifest.UnmediatedObligationIds)}",
            $"Next: {action}");
    }

    private static async Task<EvidencePolicy> ReadPolicyAsync(string policyPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(policyPath))
        {
            throw new EvidenceCliException("ASEVD204", $"Evidence policy '{policyPath}' does not exist.", "Run 'appsurface evidence init --sample' or pass --policy with a checked-in policy path.");
        }

        var bytes = await File.ReadAllBytesAsync(policyPath, cancellationToken).ConfigureAwait(false);
        try
        {
            return EvidenceCanonicalJson.Deserialize<EvidencePolicy>(bytes);
        }
        catch (JsonException exception)
        {
            throw new EvidenceCliException("ASEVD205", $"Evidence policy '{policyPath}' is not valid JSON: {exception.Message}", "Correct the policy JSON and rerun doctor or explain.");
        }
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
            paths.AddRange(EvidenceUnifiedDiffReader.Read(snapshot.Text));
        }

        if (paths.Count == 0)
        {
            throw new EvidenceCliException("ASEVD207", "No changed paths were supplied.", "Pass --path at least once or provide --diff-file with a unified diff.");
        }

        return (paths, snapshot);
    }

    private async Task<EvidenceDiffSnapshot> ReadDiffSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        const long maximumDiffBytes = 20L * 1024 * 1024;
        try
        {
            await using var stream = _diffFileAccess.OpenRead(path);
            var bytes = await ReadBoundedBytesAsync(stream, maximumDiffBytes, path, cancellationToken).ConfigureAwait(false);

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
            available,
            available ? "ready" : "external-prerequisite",
            available ? "Docker capability is available." : $"Producer '{producerId}' requires Docker-backed disposable dependencies.",
            available ? null : "Start Docker Desktop or use the consumer CI runner that provides Docker.");
    }

    private static EvidenceDoctorCheck CheckBrowser(string producerId)
    {
        var available = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"));
        return new EvidenceDoctorCheck(
            $"browser:{producerId}",
            available,
            available ? "ready" : "external-prerequisite",
            available ? "Browser capability is configured." : $"Producer '{producerId}' requires the consumer's browser test runtime.",
            available ? null : "Install the consumer browser runtime or use its CI browser image.");
    }

    private static async Task WriteCanonicalAsync<TValue>(string path, TValue value, CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(path, EvidenceCanonicalJson.Serialize(value), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TValue> ReadCanonicalAsync<TValue>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new EvidenceCliException("ASEVD208", $"Evidence file '{path}' does not exist.", "Pass paths from the same generated evidence output directory.");
        }

        try
        {
            return EvidenceCanonicalJson.Deserialize<TValue>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException exception)
        {
            throw new EvidenceCliException("ASEVD209", $"Evidence file '{path}' is not valid JSON: {exception.Message}", "Regenerate evidence instead of editing the output.");
        }
    }

    private static bool ContainsGeneratedMarker(string path) => File.ReadAllText(path).Contains(GeneratedMarker, StringComparison.Ordinal);

    private static object CreateSummary(EvidencePlan plan) => new
    {
        kind = "plan",
        profile = plan.Profile.Id,
        scope = plan.Profile.Scope.ToString(),
        matchedRules = plan.MatchedRuleIds,
        obligations = plan.Profile.Obligations.Select(static obligation => obligation.Id).ToArray(),
        next = "Run 'appsurface evidence run' only when the selected producer capabilities are ready.",
    };

    private static object CreateSummary(EvidenceManifest manifest) => new
    {
        kind = "manifest",
        claim = manifest.ClaimKind.ToString(),
        verdict = manifest.ExecutionVerdict.ToString(),
        eligibility = manifest.Eligibility.ToString(),
        envelopeStatus = manifest.EnvelopeStatus.ToString(),
        closedObligations = manifest.ClosedObligationIds,
        unmediatedObligations = manifest.UnmediatedObligationIds,
        next = manifest.ClaimKind == EvidenceClaimKind.None ? "Resolve the listed producer or obligation failure before gating." : "Evidence claim is ready for its declared eligible consumer.",
    };

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
/// Opens explicit unified diff files for Evidence planning.
/// </summary>
/// <remarks>
/// This narrow internal boundary keeps the production reader file-backed while allowing tests to prove that the
/// bounded stream reader evaluates the bytes available when a path is actually opened, not stale file metadata.
/// </remarks>
internal interface IEvidenceDiffFileAccess
{
    /// <summary>
    /// Opens <paramref name="path"/> for sequential read access.
    /// </summary>
    /// <param name="path">The explicit path supplied through <c>--diff-file</c>.</param>
    /// <returns>A readable stream owned and disposed by the Evidence workflow.</returns>
    Stream OpenRead(string path);
}

/// <summary>
/// Opens Evidence diff files through the local physical file system.
/// </summary>
internal sealed class PhysicalEvidenceDiffFileAccess : IEvidenceDiffFileAccess
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
internal sealed record EvidenceDoctorCheck(string Id, bool Satisfied, string Status, string Message, string? NextAction);

/// <summary>
/// Describes the plan and prerequisite checks produced by the evidence doctor.
/// </summary>
internal sealed record EvidenceDoctorReport(string Status, EvidencePlan Plan, IReadOnlyList<EvidenceDoctorCheck> Checks);

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
