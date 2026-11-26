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
    private const string CoverageDirectoryName = "coverage";
    private const string CoverageReportName = "coverage.cobertura.xml";
    private const string TimingsName = "timings.json";
    private const string GateReportName = "coverage-gate.json";
    private const string LogExtension = ".log";
    private const int LineThreshold = 95;
    private const int BranchThreshold = 85;
    private const decimal TolerancePercent = 0.5m;
    private const string PatchLineMode = "codecov";
    private const long MaxJson = 2 * 1024 * 1024;
    private static readonly string[] Assertions = ["coverage/solution-complete@1", "coverage/thresholds@1"];
    private static readonly HashSet<string> DiagnosticFiles =
    [
        CoverageReportName, TimingsName, GateReportName, "coverage-gate.md", "summary.txt",
        "reportgenerator-summary.txt", "slow-test-diagnostics.json", "slow-test-diagnostics.md",
        "coverage-patch-targets.json", "coverage-patch-targets.md",
    ];
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

    /// <summary>Creates a solution coverage producer with an optional bounded command seam.</summary>
    public SolutionCoverageProducer(RepositoryCheckSettings settings,
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>>? command = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _command = command ?? new EvidenceCommandRunner().RunAsync;
    }

    /// <inheritdoc />
    public string Id => ProducerId;

    /// <summary>Selects coverage diagnostics from a slash-normalized path in the repository proof directory.</summary>
    internal static bool RetainsLocalDiagnostic(string proofRelativePath)
    {
        if (!proofRelativePath.StartsWith(CoverageDirectoryName + "/", StringComparison.Ordinal))
            return false;

        var name = Path.GetFileName(proofRelativePath);
        return name.EndsWith(LogExtension, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("junit-", StringComparison.Ordinal)
            || DiagnosticFiles.Contains(name);
    }

    /// <summary>Runs collection, verifies the exact solution test inventory and timings, then evaluates coverage gates.</summary>
    public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Producer.Id != Id) return Failed("Solution coverage declaration mismatch.");
        if (context.Artifacts is null) return Invalid("Solution coverage producer has no artifact writer.");
        var root = Path.GetFullPath(_settings.RepositoryRoot);
        var output = Path.GetFullPath(_settings.OutputDirectory);
        var coverage = PathUtils.PathUnder(output, CoverageDirectoryName);
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
            var commit = (await ExecuteTextAsync("git", ["rev-parse", "--verify", "HEAD^{commit}"], root, proof, cancellationToken)).Trim();
            var tree = (await ExecuteTextAsync("git", ["rev-parse", "--verify", "HEAD^{tree}"], root, proof, cancellationToken)).Trim();
            var sdk = (await ExecuteTextAsync(_settings.DotnetPath, ["--version"], root, proof, cancellationToken)).Trim();
            if (commit.Length != 40 || tree.Length != 40 || sdk.Length is 0 or > 64) throw new InvalidDataException("Repository or SDK identity is invalid.");
            await RequireUnchangedSourceAsync(root, commit, proof, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(coverage);

            stage = "collection";
            if (_settings.CoverageCollectionDirectory is { } collection)
            {
                if (!string.IsNullOrWhiteSpace(_settings.DiffFile))
                    throw new InvalidDataException("Collected CI coverage cannot be used for a local patch.");
                await SolutionCoverageHandoff.ImportAsync(collection, coverage, new(commit, tree, sdk), WorkflowRun(), cancellationToken).ConfigureAwait(false);
            }
            else
                await CollectCoverageAsync(root, coverage, proof, cancellationToken).ConfigureAwait(false);
            var report = PathUtils.PathUnder(coverage, CoverageReportName);
            var timings = PathUtils.PathUnder(coverage, TimingsName);
            ValidateTimings(timings, expected);
            if (!File.Exists(report) || new FileInfo(report).Length == 0) throw new InvalidDataException("The merged solution coverage report is missing.");

            await RequireUnchangedSourceAsync(root, commit, proof, cancellationToken).ConfigureAwait(false);

            stage = "aggregate-gate";
            var aggregateGate = Path.Join(coverage, "aggregate-gate");
            var aggregateJson = PathUtils.PathUnder(aggregateGate, GateReportName);
            Directory.CreateDirectory(aggregateGate);
            var aggregateArgs = GateArgs(report, root, aggregateGate, null, null, patch: false,
                installed: _settings.CoverageCliPath is not null);
            await ExecuteAsync("aggregate-gate", aggregateArgs, root, proof, cancellationToken,
                _settings.CoverageCliPath ?? _settings.DotnetPath).ConfigureAwait(false);
            ValidateGate(aggregateJson, patch: false);

            string? patchJson = null;
            string? nativeProofHash = null;
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
                nativeProofHash = await ValidateNativeFanInAsync(fanInOutput, nativeContract, _settings.DiffBase!,
                    commit, tree, sdk, run, cancellationToken).ConfigureAwait(false);
                var primaryGateDir = Path.Join(coverage, "primary-gate");
                Directory.CreateDirectory(primaryGateDir);
                var primaryGate = PathUtils.PathUnder(primaryGateDir, GateReportName);
                File.Copy(Path.Join(fanInOutput, "accepted", "primary-gate.json"), primaryGate, overwrite: false);
                var patchGateDir = Path.Join(coverage, "patch-gate");
                Directory.CreateDirectory(patchGateDir);
                patchJson = PathUtils.PathUnder(patchGateDir, GateReportName);
                File.Copy(Path.Join(fanInOutput, "accepted", "composed-gate.json"), patchJson, overwrite: false);
                ValidateGate(primaryGate, patch: false);
            }
            else if (!string.IsNullOrWhiteSpace(_settings.DiffBase) || !string.IsNullOrWhiteSpace(_settings.DiffFile))
            {
                stage = "patch-gate";
                var patchGate = Path.Join(coverage, "patch-gate");
                patchJson = PathUtils.PathUnder(patchGate, GateReportName);
                Directory.CreateDirectory(patchGate);
                await ExecuteAsync("patch-gate", GateArgs(report, root, patchGate, _settings.DiffBase, _settings.DiffFile,
                    patch: true, installed: _settings.CoverageCliPath is not null), root, proof, cancellationToken,
                    _settings.CoverageCliPath ?? _settings.DotnetPath).ConfigureAwait(false);
                ValidateGate(patchJson, patch: true);
            }

            ValidateGate(aggregateJson, patch: false);
            if (patchJson is not null) ValidateGate(patchJson, patch: true, typedPatch: nativeFanIn && nativeContract == 3);
            await RequireUnchangedSourceAsync(root, commit, proof, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(context.Artifacts, "aggregate-gate", "proof/aggregate-gate.json", aggregateJson, cancellationToken);
            if (patchJson is not null) await WriteJsonAsync(context.Artifacts, "patch-gate", "proof/patch-gate.json", patchJson, cancellationToken);
            await context.Artifacts.WriteAsync("transcript", "proof/transcript.json", JsonSerializer.SerializeToUtf8Bytes(proof), cancellationToken);
            await context.Artifacts.WriteAsync("result", "proof/result.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                completed = true,
                commit,
                tree,
                sdk,
                configuration = "Release",
                fullSolution = true,
                nativeFanInContractVersion = nativeFanIn ? (int?)nativeContract : null,
                nativeFanInProofSha256 = nativeProofHash,
                testProjects = expected.Order(StringComparer.Ordinal).ToArray(),
                reportSha256 = await HashFileAsync(report, cancellationToken),
                aggregateGatePassed = true,
                patchGatePassed = patchJson is not null,
                patchGateSha256 = patchJson is null ? null : await HashFileAsync(patchJson, cancellationToken),
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
            var commandDiagnostic = exception is EvidenceCommandException { CleanupUnconfirmed: true }
                ? " Command cleanup could not be confirmed." : string.Empty;
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
        var coverage = PathUtils.PathUnder(Path.GetFullPath(_settings.OutputDirectory), CoverageDirectoryName);
        if (!File.Exists(Path.Join(root, "ForgeTrust.AppSurface.slnx")) || ReadInventory(Path.Join(root, "ForgeTrust.AppSurface.slnx")).Count == 0
            || !Path.IsPathFullyQualified(_settings.DotnetPath) || !File.Exists(_settings.DotnetPath)
            || Directory.Exists(_settings.OutputDirectory) || File.Exists(_settings.OutputDirectory))
            throw new ArgumentException("Collection requires a solution, an absolute SDK host and fresh output.");
        var run = WorkflowRun();
        SolutionCoverageHandoff.RequireRun(run);
        var proof = new List<CommandProof>();
        var commit = (await ExecuteTextAsync("git", ["rev-parse", "--verify", "HEAD^{commit}"], root, proof, cancellationToken)).Trim();
        var tree = (await ExecuteTextAsync("git", ["rev-parse", "--verify", "HEAD^{tree}"], root, proof, cancellationToken)).Trim();
        var sdk = (await ExecuteTextAsync(_settings.DotnetPath, ["--version"], root, proof, cancellationToken)).Trim();
        var identity = new SolutionCoverageIdentity(commit, tree, sdk);
        identity.Validate();
        await RequireUnchangedSourceAsync(root, commit, proof, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(coverage);
        await CollectCoverageAsync(root, coverage, proof, cancellationToken).ConfigureAwait(false);
        await RequireUnchangedSourceAsync(root, commit, proof, cancellationToken).ConfigureAwait(false);
        await SolutionCoverageHandoff.WriteAsync(coverage, identity, run, cancellationToken).ConfigureAwait(false);
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
        ValidateTimings(PathUtils.PathUnder(coverage, TimingsName), ReadInventory(Path.Join(root, "ForgeTrust.AppSurface.slnx")));
        var report = PathUtils.PathUnder(coverage, CoverageReportName);
        if (!File.Exists(report) || new FileInfo(report).Length == 0)
            throw new InvalidDataException("The merged solution coverage report is missing.");
    }

    private async Task ExecuteAsync(string name, IReadOnlyList<string> args, string root, List<CommandProof> proof,
        CancellationToken ct, string? executable = null)
    {
        var result = await _command(new EvidenceCommandRequest(executable ?? _settings.DotnetPath, args, root, TimeSpan.FromMinutes(90))
        { MaximumOutputBytes = 16 * 1024 * 1024, Environment = _settings.CommandEnvironment }, ct).ConfigureAwait(false);
        proof.Add(new(name, result.ExitCode, result.ElapsedMilliseconds, result.OutputTruncated, Hash(result.StandardOutput), Hash(result.StandardError)));
        await File.WriteAllTextAsync(PathUtils.PathUnder(_settings.OutputDirectory, CoverageDirectoryName, name + LogExtension),
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

    private static void ValidateTimings(string path, IReadOnlyList<string> expected)
    {
        using var document = ReadJson(path, 16 * 1024 * 1024);
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
        var args = installed ? new List<string> { "coverage", "gate" } : new List<string> { "run", "--project", Path.Join(root, "Cli", "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Cli.csproj"), "--configuration", "Release", "--no-build", "--no-restore", "--", "coverage", "gate" };
        var minLine = LineThreshold.ToString(CultureInfo.InvariantCulture);
        var minBranch = BranchThreshold.ToString(CultureInfo.InvariantCulture);
        args.AddRange([
            "--coverage", report, "--repository-root", root, "--output", output, "--min-line", patch ? "0" : minLine,
            "--min-branch", patch ? "0" : minBranch, "--tolerance", TolerancePercent.ToString(CultureInfo.InvariantCulture), "--no-github-summary"]);
        if (!string.IsNullOrWhiteSpace(diffBase) && string.IsNullOrWhiteSpace(diffFile))
            args.AddRange(["--diff-base", diffBase, "--diff-label", diffBase]);
        if (!string.IsNullOrWhiteSpace(diffBase) || !string.IsNullOrWhiteSpace(diffFile))
            args.AddRange(["--patch-line-mode", PatchLineMode]);
        if (patch)
        {
            if (!string.IsNullOrWhiteSpace(diffFile)) args.AddRange(["--diff-file", diffFile, "--diff-label", "pinned local source snapshot"]);
            args.AddRange(["--min-patch-line", minLine, "--min-patch-branch", minBranch]);
        }
        return args;
    }

    /// <summary>Requires the installed adapter's delivered acceptance and separate verification, retaining their artifact binding.</summary>
    private static async Task<string> ValidateNativeFanInAsync(string output, int contractVersion, string diffBase, string commit, string tree, string sdk,
        IReadOnlyDictionary<string, string?> run, CancellationToken cancellationToken)
    {
        string? policyDigest = null;
        JsonElement? applicability = null;
        foreach (var operation in new[] { "accept", "verify" })
        {
            using var document = ReadJson(Path.Join(output, operation + "-result.json"), MaxJson);
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
        var accepted = Path.Join(output, "accepted");
        using var acceptance = ReadJson(Path.Join(accepted, "acceptance.json"), MaxJson);
        var receipt = acceptance.RootElement;
        var source = receipt.GetProperty("source");
        var workflow = receipt.GetProperty("run");
        if (receipt.GetProperty("schemaVersion").GetInt32() != contractVersion)
            throw new InvalidDataException("Native acceptance receipt does not match the verified contract version.");
        if (receipt.GetProperty("policyDigest").GetString() != policyDigest
            || source.GetProperty("commit").GetString() != commit || source.GetProperty("tree").GetString() != tree
            || source.GetProperty("sdk").GetString() != sdk || source.GetProperty("configuration").GetString() != "Release"
            || workflow.GetProperty("runScope").GetString() != run["GITHUB_REPOSITORY"]
            || workflow.GetProperty("runId").GetString() != run["GITHUB_RUN_ID"]
            || workflow.GetProperty("attempt").GetInt32() != int.Parse(run["GITHUB_RUN_ATTEMPT"]!, CultureInfo.InvariantCulture))
            throw new InvalidDataException("Native acceptance does not bind the current source and workflow attempt.");
        // The installed verifier owns semantic composition, custody and denominator checks.
        // Bind its retained receipts and the two reports copied into EvidenceHost's artifact slots.
        foreach (var (property, file) in new[]
        {
            ("primaryGateSha256", Path.Join(accepted, "primary-gate.json")),
            ("composedGateSha256", Path.Join(accepted, "composed-gate.json")),
            ("compositionReceiptSha256", Path.Join(output, "composed", "verification.json")),
        })
            if (receipt.GetProperty(property).GetString() != await HashFileAsync(file, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Native acceptance differs from its retained gate or composition receipt.");
        if (applicability is { } verified)
            await ValidateV3ApplicabilityAsync(accepted, receipt, verified, diffBase, commit, cancellationToken).ConfigureAwait(false);
        return await HashFileAsync(Path.Join(accepted, "acceptance.json"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Binds the independent v3 result to the applicability, diff and gate reports retained by the producer.</summary>
    private static async Task ValidateV3ApplicabilityAsync(string accepted, JsonElement receipt, JsonElement verified,
        string diffBase, string commit, CancellationToken cancellationToken)
    {
        if (receipt.GetProperty("evaluationIntent").GetString() != "currentTrustedRun"
            || receipt.GetProperty("patchOutcome").GetString() != "PatchAccepted")
            throw new InvalidDataException("Native v3 did not retain a current patch acceptance receipt.");

        foreach (var (property, file) in new[]
        {
            ("applicabilitySha256", "patch-applicability.json"),
            ("diffSha256", "diff.patch"),
            ("diffMetadataSha256", "diff-metadata.json"),
        })
            if (receipt.GetProperty(property).GetString() != await HashFileAsync(Path.Join(accepted, file), cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Native v3 acceptance differs from its retained patch proof.");

        using var metadata = ReadJson(Path.Join(accepted, "diff-metadata.json"), MaxJson);
        var diff = metadata.RootElement;
        if (diff.GetProperty("schemaVersion").GetInt32() != 3
            || diff.GetProperty("requestedBaseCommit").GetString() != diffBase
            || diff.GetProperty("headCommit").GetString() != commit
            || !IsRepositoryCommit(diff.GetProperty("mergeBaseCommit").GetString()))
            throw new InvalidDataException("Native v3 patch proof is not bound to the requested base and head.");
        if (diff.GetProperty("diffPolicyId").GetString() != CoverageFanInPolicy.VisualBaselinePngDiffPolicyId
            || diff.GetProperty("diffSha256").GetString() != receipt.GetProperty("diffSha256").GetString())
            throw new InvalidDataException("Native v3 diff metadata does not bind the required diff policy and accepted patch.");

        using var applicability = ReadJson(Path.Join(accepted, "patch-applicability.json"), MaxJson);
        var claim = applicability.RootElement;
        if (claim.GetProperty("schemaVersion").GetInt32() != 3
            || claim.GetProperty("reportProfileId").GetString() != "coverlet-collector-cobertura-v1"
            || new[] { "policyDigest", "source", "run", "diffSha256", "diffMetadataSha256" }
                .Any(property => !JsonElement.DeepEquals(claim.GetProperty(property), receipt.GetProperty(property))))
            throw new InvalidDataException("Native v3 applicability does not bind the accepted policy, source, run and diff.");

        foreach (var (dimension, gateFile) in new[] { ("primary", "primary-gate.json"), ("composed", "composed-gate.json") })
        {
            var metrics = claim.GetProperty(dimension);
            using var gate = ReadJson(Path.Join(accepted, gateFile), MaxJson);
            if (!JsonElement.DeepEquals(metrics, verified.GetProperty(dimension))
                || !JsonElement.DeepEquals(metrics.GetProperty("line"), gate.RootElement.GetProperty("patchLine"))
                || !JsonElement.DeepEquals(metrics.GetProperty("branch"), gate.RootElement.GetProperty("patchBranch"))
                || !V3PatchMetricPasses(metrics.GetProperty("line"), 0m)
                || !V3PatchMetricPasses(metrics.GetProperty("branch"), 0m))
                throw new InvalidDataException("Native v3 applicability disagrees with the verified result or gate report.");
        }
    }

    private static void ValidateGate(string path, bool patch, bool typedPatch = false)
    {
        using var document = ReadJson(path, MaxJson);
        var root = document.RootElement;
        if (!root.GetProperty("passed").GetBoolean() || root.GetProperty("tolerancePercent").GetDecimal() != TolerancePercent)
            throw new InvalidDataException("The coverage gate did not satisfy the declared policy.");
        if (!patch && (root.GetProperty("thresholds").GetProperty("line").GetDecimal() != LineThreshold
            || root.GetProperty("thresholds").GetProperty("branch").GetDecimal() != BranchThreshold
            || root.GetProperty("line").GetProperty("percent").GetDecimal() < LineThreshold - TolerancePercent
            || root.GetProperty("branch").GetProperty("percent").GetDecimal() < BranchThreshold - TolerancePercent))
            throw new InvalidDataException("The aggregate coverage gate did not satisfy its thresholds.");
        if (patch && (root.GetProperty("thresholds").GetProperty("patchLine").GetDecimal() != LineThreshold
            || root.GetProperty("thresholds").GetProperty("patchBranch").GetDecimal() != BranchThreshold
            || root.GetProperty("patchLineMode").GetString() != PatchLineMode
            || !PatchMetricPasses(root.GetProperty("patchLine"), LineThreshold - TolerancePercent, typedPatch)
            || !PatchMetricPasses(root.GetProperty("patchBranch"), BranchThreshold - TolerancePercent, typedPatch)))
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

    private static async Task WriteJsonAsync(EvidenceArtifactWriter writer, string name, string path, string source, CancellationToken ct)
    { var bytes = await File.ReadAllBytesAsync(source, ct); if (bytes.Length > MaxJson) throw new InvalidDataException("Coverage gate artifact exceeded its bound."); await writer.WriteAsync(name, path, bytes, ct); }
    private static JsonDocument ReadJson(string path, long max) { var info = new FileInfo(path); if (!info.Exists || info.Length is <= 0 || info.Length > max) throw new InvalidDataException("A required coverage JSON artifact is missing or oversized."); return JsonDocument.Parse(File.ReadAllBytes(path)); }
    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    { await using var stream = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)); }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static bool IsContained(string root, string path) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static IReadOnlyDictionary<string, string?> WorkflowRun() => new Dictionary<string, string?>(StringComparer.Ordinal)
    { ["GITHUB_REPOSITORY"] = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"), ["GITHUB_RUN_ID"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), ["GITHUB_RUN_ATTEMPT"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") };
    private static EvidenceProducerResult Failed(string message) => new(ProducerId, EvidenceProducerOutcome.Failed, [], message);
    private static EvidenceProducerResult Invalid(string message) => new(ProducerId, EvidenceProducerOutcome.Invalid, [], message);
    private sealed record CommandProof(string Name, int ExitCode, long ElapsedMilliseconds, bool Truncated, string StandardOutputSha256, string StandardErrorSha256);
}
