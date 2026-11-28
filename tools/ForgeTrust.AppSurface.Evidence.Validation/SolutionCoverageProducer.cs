using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Runs the complete repository solution coverage workflow and closes its aggregate and optional patch gates.</summary>
internal sealed class SolutionCoverageProducer : IEvidenceProducer
{
    private const string ProducerId = "solution-coverage";
    private const int MaxJson = 2 * 1024 * 1024;
    private static readonly string[] Assertions = ["coverage/solution-complete@1", "coverage/thresholds@1"];
    private static readonly string[] ExclusiveProjects =
    [
        "ForgeTrust.AppSurface.Config.Tests.csproj", "ForgeTrust.AppSurface.Aspire.Tests.csproj",
        "AuthAspNetCoreDevAuthExample.Tests.csproj", "AuthWebRazorWireProofExample.Tests.csproj",
        "ForgeTrust.AppSurface.Durable.GoogleCloudTasks.Tests.csproj",
        "ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj", "ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj",
        "ForgeTrust.RazorWire.Cli.Tests.csproj", "ForgeTrust.AppSurface.Web.Tailwind.Tests.csproj",
    ];
    private readonly RepositoryCheckSettings _settings;
    private readonly Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> _command;

    /// <summary>The fixed repository gate policy used for declaration, CLI arguments and report validation.</summary>
    internal static EvidenceCoverageGateRequirements GateRequirements { get; } = new(95, 85, 95, 85, "codecov", 0.5m);

    /// <summary>Creates a solution coverage producer with an optional bounded command seam.</summary>
    public SolutionCoverageProducer(RepositoryCheckSettings settings,
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>>? command = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _command = command ?? new EvidenceCommandRunner().RunAsync;
    }

    /// <inheritdoc />
    public string Id => ProducerId;

    /// <summary>Runs collection, verifies the exact solution test inventory and timings, then evaluates coverage gates.</summary>
    public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Producer.Id != Id) return Failed("Solution coverage declaration mismatch.");
        if (context.Artifacts is null) return Invalid("Solution coverage producer has no artifact writer.");
        var root = Path.GetFullPath(_settings.RepositoryRoot);
        var output = Path.GetFullPath(_settings.OutputDirectory);
        var coverage = Path.Join(output, "coverage");
        var proof = new List<CommandProof>();
        var stage = "settings";
        try
        {
            if (!Directory.Exists(root) || !File.Exists(Path.Join(root, "ForgeTrust.AppSurface.slnx"))
                || !Path.IsPathFullyQualified(_settings.DotnetPath) || !File.Exists(_settings.DotnetPath)
                || !Path.IsPathFullyQualified(output) || !IsContained(output, coverage)
                || (Directory.Exists(coverage) && Directory.EnumerateFileSystemEntries(coverage).Any()))
                throw new InvalidOperationException("Invalid or non-fresh solution coverage paths.");
            var expected = ReadInventory(Path.Join(root, "ForgeTrust.AppSurface.slnx"));
            if (expected.Count == 0) throw new InvalidDataException("The solution contains no test projects.");
            var nativeFanIn = !string.IsNullOrWhiteSpace(_settings.NativeCoverageDirectory);
            var nativeContract = _settings.NativeCoverageContractVersion;
            if (nativeContract is not (2 or 3) || (nativeContract == 3 && (!nativeFanIn || !IsRepositoryCommit(_settings.DiffBase))))
                throw new InvalidDataException("Native v3 requires native captures and a full pinned SHA-1 Git commit ID.");
            if (_settings.CoverageCliPath is { } coverageCli
                && (!Path.IsPathFullyQualified(coverageCli) || !File.Exists(coverageCli)))
                throw new InvalidOperationException("The installed coverage CLI must be an existing absolute executable.");
            if (nativeFanIn && string.IsNullOrWhiteSpace(_settings.CoverageCliPath))
                throw new InvalidOperationException("Native fan-in requires the installed candidate --coverage-cli.");
            if (!string.IsNullOrWhiteSpace(_settings.DiffFile) && _settings.VerifyLocalSourceAsync is null)
                throw new InvalidDataException("A local patch requires in-process source verification; use the local operation.");
            if (nativeFanIn && (string.IsNullOrWhiteSpace(_settings.DiffBase) || !string.IsNullOrWhiteSpace(_settings.DiffFile)))
                throw new InvalidDataException("Native fan-in requires a pinned Git diff base and no local patch override.");
            stage = "identity";
            var identity = await ReadIdentityAsync(root, proof, cancellationToken).ConfigureAwait(false);
            if (identity.Commit.Length != 40 || identity.Tree.Length != 40 || identity.Sdk.Length is 0 or > 64)
                throw new InvalidDataException("Repository or SDK identity is invalid.");
            await RequireUnchangedSourceAsync(root, identity.Commit, proof, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(coverage);

            stage = "collection";
            if (_settings.CoverageCollectionDirectory is { } collection)
            {
                if (!string.IsNullOrWhiteSpace(_settings.DiffFile))
                    throw new InvalidDataException("Collected CI coverage cannot be used for a local patch.");
                await SolutionCoverageHandoff.ImportAsync(collection, coverage, identity, WorkflowRun(), cancellationToken).ConfigureAwait(false);
            }
            else
                await CollectCoverageAsync(root, coverage, proof, cancellationToken).ConfigureAwait(false);
            var report = Path.Join(coverage, "coverage.cobertura.xml");
            var timings = Path.Join(coverage, "timings.json");
            ValidateTimings(timings, expected, cancellationToken);
            if (!File.Exists(report) || new FileInfo(report).Length == 0) throw new InvalidDataException("The merged solution coverage report is missing.");

            await RequireUnchangedSourceAsync(root, identity.Commit, proof, cancellationToken).ConfigureAwait(false);

            stage = "aggregate-gate";
            var aggregateGate = Path.Join(coverage, "aggregate-gate");
            var aggregateJson = Path.Join(aggregateGate, "coverage-gate.json");
            Directory.CreateDirectory(aggregateGate);
            var aggregateArgs = GateArgs(report, root, aggregateGate, null, null, patch: false,
                installed: _settings.CoverageCliPath is not null);
            await ExecuteAsync("aggregate-gate", aggregateArgs, root, proof, cancellationToken,
                _settings.CoverageCliPath ?? _settings.DotnetPath).ConfigureAwait(false);
            _ = ReadValidatedGate(aggregateJson, cancellationToken, patch: false);

            string? patchJson = null;
            NativeGateProof? nativeProof = null;
            if (nativeFanIn)
            {
                stage = "native-fan-in";
                var fanInOutput = Path.Join(output, "coverage-fanin");
                var nativeDirs = Directory.GetDirectories(_settings.NativeCoverageDirectory!).Order(StringComparer.Ordinal).ToArray();
                if (nativeDirs.Length != 3) throw new InvalidDataException("Exactly three native coverage captures are required.");
                var run = WorkflowRun();
                if (run.Values.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(_settings.DiffBase))
                    throw new InvalidDataException("Native fan-in requires complete workflow provenance and a pinned diff base.");
                var dogfoodArgs = new List<string>
                {
                    "run", "--project", Path.Join(root, "tools", "ForgeTrust.AppSurface.Coverage.Dogfood", "ForgeTrust.AppSurface.Coverage.Dogfood.csproj"),
                    "--configuration", "Release", "--", "--operation", "fanin", "--repository", root,
                    "--cli", _settings.CoverageCliPath!, "--output", fanInOutput, "--configuration", "Release",
                    "--contract-version", nativeContract.ToString(CultureInfo.InvariantCulture),
                    "--primary", report, "--timings", timings, "--native-inputs", _settings.NativeCoverageDirectory!,
                    "--diff-base", _settings.DiffBase!, "--run-scope", run["GITHUB_REPOSITORY"]!,
                    "--run-id", run["GITHUB_RUN_ID"]!, "--attempt", run["GITHUB_RUN_ATTEMPT"]!,
                };
                if (nativeContract == 3)
                    dogfoodArgs.AddRange(["--diff-policy-id", CoverageFanInPolicy.VisualBaselinePngDiffPolicyId]);
                await ExecuteAsync("coverage-fanin", dogfoodArgs, root, proof, cancellationToken, _settings.DotnetPath).ConfigureAwait(false);
                nativeProof = await ValidateNativeFanInAsync(fanInOutput, nativeContract, _settings.DiffBase!,
                    identity, run, cancellationToken).ConfigureAwait(false);
                var primaryGateDir = PathUtils.PathUnder(coverage, "primary-gate");
                Directory.CreateDirectory(primaryGateDir);
                var primaryGate = PathUtils.PathUnder(primaryGateDir, "coverage-gate.json");
                await WriteNewFileAsync(primaryGate, nativeProof.Primary, cancellationToken).ConfigureAwait(false);
                var patchGateDir = PathUtils.PathUnder(coverage, "patch-gate");
                Directory.CreateDirectory(patchGateDir);
                patchJson = PathUtils.PathUnder(patchGateDir, "coverage-gate.json");
                await WriteNewFileAsync(patchJson, nativeProof.Composed, cancellationToken).ConfigureAwait(false);
                ValidateGate(nativeProof.Primary, patch: false);
            }
            else if (!string.IsNullOrWhiteSpace(_settings.DiffBase) || !string.IsNullOrWhiteSpace(_settings.DiffFile))
            {
                stage = "patch-gate";
                var patchGate = Path.Join(coverage, "patch-gate");
                patchJson = Path.Join(patchGate, "coverage-gate.json");
                Directory.CreateDirectory(patchGate);
                await ExecuteAsync("patch-gate", GateArgs(report, root, patchGate, _settings.DiffBase, _settings.DiffFile,
                    patch: true, installed: _settings.CoverageCliPath is not null), root, proof, cancellationToken,
                    _settings.CoverageCliPath ?? _settings.DotnetPath).ConfigureAwait(false);
                _ = ReadValidatedGate(patchJson, cancellationToken, patch: true);
            }

            await RequireUnchangedSourceAsync(root, identity.Commit, proof, cancellationToken).ConfigureAwait(false);
            // Check and publish the same bounded bytes after the final source check.
            var aggregateBytes = ReadValidatedGate(aggregateJson, cancellationToken, patch: false);
            ReadOnlyMemory<byte>? patchBytes = null;
            if (patchJson is not null)
            {
                var captured = ReadValidatedGate(patchJson, cancellationToken, patch: true, typedPatch: nativeFanIn && nativeContract == 3);
                if (nativeProof is not null && !captured.Span.SequenceEqual(nativeProof.Composed.Span))
                    throw new InvalidDataException("The staged patch gate differs from native acceptance.");
                patchBytes = captured;
            }
            await context.Artifacts.WriteAsync("aggregate-gate", "proof/aggregate-gate.json", aggregateBytes, cancellationToken).ConfigureAwait(false);
            var patchArtifact = patchBytes is { } capturedPatch
                ? await context.Artifacts.WriteAsync("patch-gate", "proof/patch-gate.json", capturedPatch, cancellationToken).ConfigureAwait(false)
                : null;
            await context.Artifacts.WriteAsync("transcript", "proof/transcript.json", JsonSerializer.SerializeToUtf8Bytes(proof), cancellationToken);
            await context.Artifacts.WriteAsync("result", "proof/result.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                completed = true,
                commit = identity.Commit,
                tree = identity.Tree,
                sdk = identity.Sdk,
                configuration = "Release",
                fullSolution = true,
                nativeFanInContractVersion = nativeFanIn ? (int?)nativeContract : null,
                nativeFanInProofSha256 = nativeProof?.ReceiptSha256,
                testProjects = expected.Order(StringComparer.Ordinal).ToArray(),
                reportSha256 = await HashFileAsync(report, cancellationToken),
                aggregateGatePassed = true,
                patchGatePassed = patchArtifact is not null,
                patchGateSha256 = patchArtifact?.Sha256,
                assertions = Assertions,
            }), cancellationToken);
            return new(Id, EvidenceProducerOutcome.Passed, Assertions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var artifactDiagnostic = await FailureArtifactDiagnostics.WriteAsync(context.Artifacts,
                [("transcript", JsonSerializer.SerializeToUtf8Bytes(proof)),
                    ("result", JsonSerializer.SerializeToUtf8Bytes(new { completed = false, stage, failure = exception.GetType().Name, commands = proof }))]).ConfigureAwait(false);
            var commandDiagnostic = RepositoryCommandSettlement.IsUnconfirmedCleanup(exception) ? " cleanup_failed." : string.Empty;
            return Failed($"Solution coverage failed during {stage}." + commandDiagnostic + artifactDiagnostic);
        }
    }

    /// <summary>
    /// Collects the complete solution without closing coverage obligations. The same-run handoff must later pass
    /// the normal producer's aggregate, applicable patch and native fan-in gates before any acceptance claim.
    /// Collection uses the explicitly selected installed CLI when supplied, otherwise the source CLI.
    /// It does not require native inputs and cannot satisfy final acceptance.
    /// </summary>
    internal async Task CollectAsync(CancellationToken cancellationToken)
    {
        if (_settings.DiffFile is not null || _settings.NativeCoverageDirectory is not null || _settings.CoverageCollectionDirectory is not null)
            throw new ArgumentException("Collection accepts no patch override, native captures or previous collection.");
        if (_settings.CoverageCliPath is { } coverageCli
            && (!Path.IsPathFullyQualified(coverageCli) || !File.Exists(coverageCli)))
            throw new ArgumentException("The installed coverage CLI must be an existing absolute executable.");
        var root = Path.GetFullPath(_settings.RepositoryRoot);
        var coverage = Path.Join(Path.GetFullPath(_settings.OutputDirectory), "coverage");
        if (!File.Exists(Path.Join(root, "ForgeTrust.AppSurface.slnx")) || ReadInventory(Path.Join(root, "ForgeTrust.AppSurface.slnx")).Count == 0
            || !Path.IsPathFullyQualified(_settings.DotnetPath) || !File.Exists(_settings.DotnetPath)
            || Directory.Exists(_settings.OutputDirectory) || File.Exists(_settings.OutputDirectory))
            throw new ArgumentException("Collection requires a solution, an absolute SDK host and fresh output.");
        var run = WorkflowRun();
        SolutionCoverageHandoff.RequireRun(run);
        var proof = new List<CommandProof>();
        var identity = await ReadIdentityAsync(root, proof, cancellationToken).ConfigureAwait(false);
        identity.Validate();
        await RequireUnchangedSourceAsync(root, identity.Commit, proof, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(coverage);
        await CollectCoverageAsync(root, coverage, proof, cancellationToken).ConfigureAwait(false);
        await RequireUnchangedSourceAsync(root, identity.Commit, proof, cancellationToken).ConfigureAwait(false);
        await SolutionCoverageHandoff.WriteAsync(coverage, identity, run, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SolutionCoverageIdentity> ReadIdentityAsync(string root, List<CommandProof> proof, CancellationToken cancellationToken)
    {
        var commit = (await ExecuteTextAsync("git", ["rev-parse", "--verify", "HEAD^{commit}"], root, proof, cancellationToken)).Trim();
        var tree = (await ExecuteTextAsync("git", ["rev-parse", "--verify", "HEAD^{tree}"], root, proof, cancellationToken)).Trim();
        var sdk = (await ExecuteTextAsync(_settings.DotnetPath, ["--version"], root, proof, cancellationToken)).Trim();
        return new SolutionCoverageIdentity(commit, tree, sdk);
    }

    private async Task CollectCoverageAsync(string root, string coverage, List<CommandProof> proof,
        CancellationToken cancellationToken)
    {
        var installedCli = _settings.CoverageCliPath;
        List<string> runArgs = installedCli is null
            ? [
                "run", "--project", Path.Join(root, "Cli", "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Cli.csproj"),
                "--configuration", "Release", "--", "coverage", "run", "--solution", Path.Join(root, "ForgeTrust.AppSurface.slnx"),
                "--output", coverage, "--configuration", "Release", "--parallelism", "2",
            ]
            : ["coverage", "run", "--solution", Path.Join(root, "ForgeTrust.AppSurface.slnx"),
                "--output", coverage, "--configuration", "Release", "--parallelism", "2"];
        runArgs.AddRange(["--schedule", "longest-first"]);
        if (_settings.CoverageScheduleTimingsPath is { } timings)
            runArgs.AddRange(["--schedule-timings", timings]);
        foreach (var project in ExclusiveProjects) { runArgs.Add("--exclusive-test-project"); runArgs.Add(project); }
        runArgs.AddRange(["--logger", "console;verbosity=normal", "--test-results", "junit", "--slow-test-diagnostics", "--require-non-sandbox"]);
        await ExecuteAsync("coverage-run", runArgs, root, proof, cancellationToken,
            installedCli ?? _settings.DotnetPath).ConfigureAwait(false);
        ValidateTimings(Path.Join(coverage, "timings.json"), ReadInventory(Path.Join(root, "ForgeTrust.AppSurface.slnx")), cancellationToken);
        var report = Path.Join(coverage, "coverage.cobertura.xml");
        if (!File.Exists(report) || new FileInfo(report).Length == 0)
            throw new InvalidDataException("The merged solution coverage report is missing.");
    }

    private async Task ExecuteAsync(string name, IReadOnlyList<string> args, string root, List<CommandProof> proof,
        CancellationToken ct, string? executable = null)
    {
        var result = await _command(new EvidenceCommandRequest(executable ?? _settings.DotnetPath, args, root, TimeSpan.FromMinutes(90))
        { MaximumOutputBytes = 16 * 1024 * 1024, Environment = _settings.CommandEnvironment }, ct).ConfigureAwait(false);
        proof.Add(new(name, result.ExitCode, result.ElapsedMilliseconds, result.OutputTruncated, Hash(result.StandardOutput), Hash(result.StandardError)));
        await File.WriteAllTextAsync(Path.Join(_settings.OutputDirectory, "coverage", name + ".log"),
            result.StandardOutput + "\n" + result.StandardError, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.OutputTruncated) throw new InvalidOperationException("A solution coverage command failed or exceeded its output bound.");
    }

    private async Task<string> ExecuteTextAsync(string file, IReadOnlyList<string> args, string root, List<CommandProof> proof, CancellationToken ct)
    {
        var result = await _command(new EvidenceCommandRequest(file, args, root, TimeSpan.FromSeconds(30))
        { MaximumOutputBytes = 4096, Environment = _settings.CommandEnvironment }, ct).ConfigureAwait(false);
        proof.Add(new(file, result.ExitCode, result.ElapsedMilliseconds, result.OutputTruncated, Hash(result.StandardOutput), Hash(result.StandardError)));
        if (result.ExitCode != 0 || result.OutputTruncated || !string.IsNullOrWhiteSpace(result.StandardError)) throw new InvalidOperationException("A bounded identity command failed.");
        return result.StandardOutput;
    }

    private async Task RequireUnchangedSourceAsync(string root, string commit, List<CommandProof> proof, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_settings.DiffFile))
        {
            await _settings.VerifyLocalSourceAsync!(ct).ConfigureAwait(false);
            return;
        }
        var head = (await ExecuteTextAsync("git", ["rev-parse", "--verify", "HEAD^{commit}"], root, proof, ct)).Trim();
        var status = await ExecuteTextAsync("git", ["status", "--porcelain=v1", "--untracked-files=all"], root, proof, ct);
        if (head != commit || status.Length != 0) throw new InvalidDataException("CI coverage requires the unchanged checked-out commit.");
    }

    private static IReadOnlyList<string> ReadInventory(string solution) => XDocument.Load(solution).Descendants("Project")
        .Select(x => x.Attribute("Path")?.Value).Where(x => !string.IsNullOrWhiteSpace(x)
            && (x.EndsWith(".Tests.csproj", StringComparison.Ordinal) || x.EndsWith(".IntegrationTests.csproj", StringComparison.Ordinal)))
        .Select(x => x!.Replace('\\', '/')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static void ValidateTimings(string path, IReadOnlyList<string> expected, CancellationToken cancellationToken)
    {
        using var document = ReadJson(path, 16 * 1024 * 1024, cancellationToken);
        var root = document.RootElement;
        if (root.GetProperty("configuration").GetString() != "Release"
            || root.GetProperty("coverageDriver").GetString() != "collector"
            || root.GetProperty("merge").GetProperty("exitCode").GetInt32() != 0
            || root.GetProperty("artifacts").GetProperty("coverageFiles").GetInt32() != expected.Count)
            throw new InvalidDataException("Coverage timings do not prove a successful Release collector run.");
        var projects = root.GetProperty("projects").EnumerateArray().ToArray();
        var observed = projects.Select(x => x.GetProperty("project").GetString()?.Replace('\\', '/') ?? "").ToArray();
        if (observed.Length != expected.Count || !observed.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Coverage timings do not contain the exact full solution test-project inventory.");
        foreach (var project in projects)
            if (project.GetProperty("executionStatus").GetString() != "completed" || project.GetProperty("exitCode").GetInt32() != 0
                || project.GetProperty("coverageArtifactStatus").GetString() != "produced")
                throw new InvalidDataException("A solution test project did not complete with coverage artifacts.");
    }

    private static List<string> GateArgs(string report, string root, string output, string? diffBase, string? diffFile, bool patch, bool installed)
    {
        static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

        var args = installed ? new List<string> { "coverage", "gate" } : new List<string> { "run", "--project", Path.Join(root, "Cli", "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Cli.csproj"), "--configuration", "Release", "--no-build", "--no-restore", "--", "coverage", "gate" };
        args.AddRange([
            "--coverage", report, "--repository-root", root, "--output", output,
            "--min-line", Number(patch ? 0 : GateRequirements.MinLinePercent),
            "--min-branch", Number(patch ? 0 : GateRequirements.MinBranchPercent),
            "--tolerance", Number(GateRequirements.TolerancePercent), "--no-github-summary"]);
        if (!string.IsNullOrWhiteSpace(diffBase) && string.IsNullOrWhiteSpace(diffFile))
            args.AddRange(["--diff-base", diffBase, "--diff-label", diffBase]);
        if (!string.IsNullOrWhiteSpace(diffBase) || !string.IsNullOrWhiteSpace(diffFile))
            args.AddRange(["--patch-line-mode", GateRequirements.PatchLineMode]);
        if (patch)
        {
            if (!string.IsNullOrWhiteSpace(diffFile)) args.AddRange(["--diff-file", diffFile, "--diff-label", "pinned local source snapshot"]);
            args.AddRange(["--min-patch-line", Number(GateRequirements.MinPatchLinePercent!.Value),
                "--min-patch-branch", Number(GateRequirements.MinPatchBranchPercent!.Value)]);
        }
        return args;
    }

    /// <summary>Requires the installed adapter's delivered acceptance and separate verification, retaining their artifact binding.</summary>
    private static async Task<NativeGateProof> ValidateNativeFanInAsync(string output, int contractVersion, string diffBase, SolutionCoverageIdentity identity,
        IReadOnlyDictionary<string, string?> run, CancellationToken cancellationToken)
    {
        string? policyDigest = null;
        JsonElement? applicability = null;
        foreach (var operation in new[] { "accept", "verify" })
        {
            using var document = ReadJson(Path.Join(output, operation + "-result.json"), MaxJson, cancellationToken);
            var terminal = document.RootElement;
            var result = terminal.GetProperty("result");
            var delivery = terminal.GetProperty("delivery");
            if (terminal.GetProperty("schemaVersion").GetInt32() != contractVersion
                || terminal.GetProperty("operation").GetString() != operation
                || terminal.GetProperty("status").GetString() != "succeeded"
                || terminal.GetProperty("operationOutcome").GetString() != "succeeded"
                || !delivery.GetProperty("resultFileCommitted").GetBoolean()
                || result.GetProperty("operation").GetString() != operation
                || !result.GetProperty("verified").GetBoolean()
                || (operation == "accept" && !result.GetProperty("accepted").GetBoolean())
                || !result.GetProperty("effects").GetProperty("cleanupComplete").GetBoolean()
                || result.GetProperty("patchOutcome").GetString() != "PatchAccepted"
                || result.GetProperty("evaluationIntent").GetString() != "currentTrustedRun")
                throw new InvalidDataException("Native acceptance or independent verification did not complete and deliver its result.");
            var digest = result.GetProperty("policyDigest").GetString();
            if (string.IsNullOrWhiteSpace(digest) || (policyDigest is not null && digest != policyDigest))
                throw new InvalidDataException("Native acceptance and verification refer to different policies.");
            policyDigest = digest;
            if (contractVersion == 3)
            {
                var claim = result.GetProperty("patchApplicability");
                if (result.GetProperty("outcome").GetString() != "succeeded" || claim.ValueKind != JsonValueKind.Object ||
                    (applicability is { } prior && !JsonElement.DeepEquals(prior, claim)))
                    throw new InvalidDataException("Native v3 acceptance and verification disagree on patch applicability.");
                applicability = claim.Clone();
            }
        }
        var accepted = PathUtils.PathUnder(output, "accepted");
        var receiptBytes = ReadJsonBytes(PathUtils.PathUnder(accepted, "acceptance.json"), MaxJson, cancellationToken);
        using var acceptance = JsonDocument.Parse(receiptBytes);
        var receipt = acceptance.RootElement;
        var source = receipt.GetProperty("source");
        var workflow = receipt.GetProperty("run");
        if (receipt.GetProperty("schemaVersion").GetInt32() != contractVersion)
            throw new InvalidDataException("Native acceptance receipt does not match the verified contract version.");
        if (receipt.GetProperty("policyDigest").GetString() != policyDigest
            || source.GetProperty("commit").GetString() != identity.Commit || source.GetProperty("tree").GetString() != identity.Tree
            || source.GetProperty("sdk").GetString() != identity.Sdk || source.GetProperty("configuration").GetString() != "Release"
            || workflow.GetProperty("runScope").GetString() != run["GITHUB_REPOSITORY"]
            || workflow.GetProperty("runId").GetString() != run["GITHUB_RUN_ID"]
            || workflow.GetProperty("attempt").GetInt32() != int.Parse(run["GITHUB_RUN_ATTEMPT"]!, CultureInfo.InvariantCulture))
            throw new InvalidDataException("Native acceptance does not bind the current source and workflow attempt.");
        // The installed verifier owns semantic composition, custody and denominator checks.
        // Retain the exact gate bytes named by the acceptance receipt for staging and v3 verification.
        var proof = new NativeGateProof(EvidenceDigest.Sha256(receiptBytes.Span),
            ReadJsonBytes(PathUtils.PathUnder(accepted, "primary-gate.json"), MaxJson, cancellationToken),
            ReadJsonBytes(PathUtils.PathUnder(accepted, "composed-gate.json"), MaxJson, cancellationToken));
        if (receipt.GetProperty("primaryGateSha256").GetString() != EvidenceDigest.Sha256(proof.Primary.Span)
            || receipt.GetProperty("composedGateSha256").GetString() != EvidenceDigest.Sha256(proof.Composed.Span)
            || receipt.GetProperty("compositionReceiptSha256").GetString() != await HashFileAsync(
                PathUtils.PathUnder(output, "composed", "verification.json"), cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Native acceptance differs from its retained gate or composition receipt.");
        if (applicability is { } verified)
            await ValidateV3ApplicabilityAsync(accepted, receipt, verified, proof, diffBase, identity.Commit, cancellationToken).ConfigureAwait(false);
        return proof;
    }

    /// <summary>Binds the independent v3 result to the applicability, diff and gate reports retained by the producer.</summary>
    private static async Task ValidateV3ApplicabilityAsync(string accepted, JsonElement receipt, JsonElement verified,
        NativeGateProof proof, string diffBase, string commit, CancellationToken cancellationToken)
    {
        if (receipt.GetProperty("evaluationIntent").GetString() != "currentTrustedRun"
            || receipt.GetProperty("patchOutcome").GetString() != "PatchAccepted")
            throw new InvalidDataException("Native v3 did not retain a current patch acceptance receipt.");

        var applicabilityBytes = ReadJsonBytes(PathUtils.PathUnder(accepted, "patch-applicability.json"), MaxJson, cancellationToken);
        var metadataBytes = ReadJsonBytes(PathUtils.PathUnder(accepted, "diff-metadata.json"), MaxJson, cancellationToken);
        if (receipt.GetProperty("applicabilitySha256").GetString() != EvidenceDigest.Sha256(applicabilityBytes.Span)
            || receipt.GetProperty("diffSha256").GetString() != await HashFileAsync(
                PathUtils.PathUnder(accepted, "diff.patch"), cancellationToken).ConfigureAwait(false)
            || receipt.GetProperty("diffMetadataSha256").GetString() != EvidenceDigest.Sha256(metadataBytes.Span))
            throw new InvalidDataException("Native v3 acceptance differs from its retained patch proof.");

        using var metadata = JsonDocument.Parse(metadataBytes);
        var diff = metadata.RootElement;
        if (diff.GetProperty("schemaVersion").GetInt32() != 3
            || diff.GetProperty("requestedBaseCommit").GetString() != diffBase
            || diff.GetProperty("headCommit").GetString() != commit
            || !IsRepositoryCommit(diff.GetProperty("mergeBaseCommit").GetString()))
            throw new InvalidDataException("Native v3 patch proof is not bound to the requested base and head.");
        if (diff.GetProperty("diffPolicyId").GetString() != CoverageFanInPolicy.VisualBaselinePngDiffPolicyId
            || diff.GetProperty("diffSha256").GetString() != receipt.GetProperty("diffSha256").GetString())
            throw new InvalidDataException("Native v3 diff metadata does not bind the required diff policy and accepted patch.");

        using var applicability = JsonDocument.Parse(applicabilityBytes);
        var claim = applicability.RootElement;
        if (claim.GetProperty("schemaVersion").GetInt32() != 3
            || claim.GetProperty("reportProfileId").GetString() != "coverlet-collector-cobertura-v1"
            || new[] { "policyDigest", "source", "run", "diffSha256", "diffMetadataSha256" }
                .Any(property => !JsonElement.DeepEquals(claim.GetProperty(property), receipt.GetProperty(property))))
            throw new InvalidDataException("Native v3 applicability does not bind the accepted policy, source, run and diff.");

        foreach (var (dimension, gateBytes) in new[] { ("primary", proof.Primary), ("composed", proof.Composed) })
        {
            var metrics = claim.GetProperty(dimension);
            using var gate = JsonDocument.Parse(gateBytes);
            if (!JsonElement.DeepEquals(metrics, verified.GetProperty(dimension))
                || !JsonElement.DeepEquals(metrics.GetProperty("line"), gate.RootElement.GetProperty("patchLine"))
                || !JsonElement.DeepEquals(metrics.GetProperty("branch"), gate.RootElement.GetProperty("patchBranch"))
                || !V3PatchMetricPasses(metrics.GetProperty("line"), 0m)
                || !V3PatchMetricPasses(metrics.GetProperty("branch"), 0m))
                throw new InvalidDataException("Native v3 applicability disagrees with the verified result or gate report.");
        }
    }

    private static ReadOnlyMemory<byte> ReadValidatedGate(string path, CancellationToken cancellationToken, bool patch, bool typedPatch = false)
    {
        var bytes = ReadJsonBytes(path, MaxJson, cancellationToken);
        ValidateGate(bytes, patch, typedPatch);
        return bytes;
    }

    private static void ValidateGate(ReadOnlyMemory<byte> bytes, bool patch, bool typedPatch = false)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var tolerance = GateRequirements.TolerancePercent;
        if (!root.GetProperty("passed").GetBoolean() || root.GetProperty("tolerancePercent").GetDecimal() != tolerance)
            throw new InvalidDataException("The coverage gate did not satisfy the declared policy.");
        if (!patch)
        {
            if (root.GetProperty("thresholds").GetProperty("line").GetDecimal() != GateRequirements.MinLinePercent
                || root.GetProperty("thresholds").GetProperty("branch").GetDecimal() != GateRequirements.MinBranchPercent
                || root.GetProperty("line").GetProperty("percent").GetDecimal() < GateRequirements.MinLinePercent - tolerance
                || root.GetProperty("branch").GetProperty("percent").GetDecimal() < GateRequirements.MinBranchPercent - tolerance)
                throw new InvalidDataException("The aggregate coverage gate did not satisfy its thresholds.");
            return;
        }

        var minimumLine = GateRequirements.MinPatchLinePercent!.Value;
        var minimumBranch = GateRequirements.MinPatchBranchPercent!.Value;
        if (root.GetProperty("thresholds").GetProperty("patchLine").GetDecimal() != minimumLine
            || root.GetProperty("thresholds").GetProperty("patchBranch").GetDecimal() != minimumBranch
            || root.GetProperty("patchLineMode").GetString() != GateRequirements.PatchLineMode
            || !PatchMetricPasses(root.GetProperty("patchLine"), minimumLine - tolerance, typedPatch)
            || !PatchMetricPasses(root.GetProperty("patchBranch"), minimumBranch - tolerance, typedPatch))
            throw new InvalidDataException("The patch coverage gate did not satisfy the declared policy.");
    }

    private static bool PatchMetricPasses(JsonElement metric, decimal threshold, bool typedPatch) => typedPatch
        ? V3PatchMetricPasses(metric, threshold)
        : metric.GetProperty("percent").GetDecimal() >= threshold;

    private static bool V3PatchMetricPasses(JsonElement metric, decimal threshold)
    {
        var changed = metric.GetProperty("changedLines").GetInt32();
        var measurable = metric.GetProperty("measurable").GetInt32();
        var covered = metric.GetProperty("covered").GetInt32();
        var percent = metric.GetProperty("percent");
        if (changed < 0 || covered < 0 || covered > measurable) return false;
        return (metric.GetProperty("outcome").GetString(), metric.GetProperty("reason").GetString()) switch
        {
            ("notApplicable", "no-reported-patch-points") => measurable == 0 && percent.ValueKind == JsonValueKind.Null,
            ("measured", "reported-points") => changed > 0 && measurable > 0 && percent.ValueKind == JsonValueKind.Number
                && percent.GetDecimal() >= threshold
                && percent.GetDecimal() == Math.Round(covered * 100m / measurable, 4, MidpointRounding.AwayFromZero),
            _ => false,
        };
    }

    private static bool IsRepositoryCommit(string? value) => value is { Length: 40 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task WriteNewFileAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static JsonDocument ReadJson(string path, int maximumBytes, CancellationToken cancellationToken) =>
        JsonDocument.Parse(ReadJsonBytes(path, maximumBytes, cancellationToken));

    private static ReadOnlyMemory<byte> ReadJsonBytes(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
            throw new InvalidDataException("A required coverage JSON artifact is missing or oversized.");
        return ValidationProof.ReadBoundedFile(path, maximumBytes, cancellationToken);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    { await using var stream = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)); }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static bool IsContained(string root, string path) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static IReadOnlyDictionary<string, string?> WorkflowRun() => new Dictionary<string, string?>(StringComparer.Ordinal)
    { ["GITHUB_REPOSITORY"] = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"), ["GITHUB_RUN_ID"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), ["GITHUB_RUN_ATTEMPT"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") };
    private static EvidenceProducerResult Failed(string message) => new(ProducerId, EvidenceProducerOutcome.Failed, [], message);
    private static EvidenceProducerResult Invalid(string message) => new(ProducerId, EvidenceProducerOutcome.Invalid, [], message);
    private sealed record NativeGateProof(string ReceiptSha256, ReadOnlyMemory<byte> Primary, ReadOnlyMemory<byte> Composed);
    private sealed record CommandProof(string Name, int ExitCode, long ElapsedMilliseconds, bool Truncated, string StandardOutputSha256, string StandardErrorSha256);
}
