using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Runs the complete repository solution coverage workflow and closes its aggregate and optional patch gates.</summary>
internal sealed class SolutionCoverageProducer : IEvidenceProducer
{
    private const string ProducerId = "solution-coverage";
    private const long MaxJson = 2 * 1024 * 1024;
    private static readonly string[] Assertions = ["coverage/solution-complete@1", "coverage/thresholds@1"];
    private static readonly string[] ExclusiveProjects =
    [
        "ForgeTrust.AppSurface.Config.Tests.csproj", "ForgeTrust.AppSurface.Aspire.Tests.csproj",
        "AuthAspNetCoreDevAuthExample.Tests.csproj", "AuthWebRazorWireProofExample.Tests.csproj",
        "ForgeTrust.AppSurface.Durable.CloudTasks.Tests.csproj",
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
            List<string> runArgs = _settings.CoverageCliPath is null
            ? [
                "run", "--project", Path.Join(root, "Cli", "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Cli.csproj"),
                "--configuration", "Release", "--", "coverage", "run", "--solution", Path.Join(root, "ForgeTrust.AppSurface.slnx"),
                "--output", coverage, "--configuration", "Release", "--parallelism", "2",
            ]
            : ["coverage", "run", "--solution", Path.Join(root, "ForgeTrust.AppSurface.slnx"),
                "--output", coverage, "--configuration", "Release", "--parallelism", "2"];
            foreach (var project in ExclusiveProjects) { runArgs.Add("--exclusive-test-project"); runArgs.Add(project); }
            runArgs.AddRange(["--logger", "console;verbosity=normal", "--test-results", "junit", "--slow-test-diagnostics", "--require-non-sandbox"]);
            await ExecuteAsync("coverage-run", runArgs, root, proof, cancellationToken,
                _settings.CoverageCliPath ?? _settings.DotnetPath).ConfigureAwait(false);
            var report = Path.Join(coverage, "coverage.cobertura.xml");
            var timings = Path.Join(coverage, "timings.json");
            ValidateTimings(timings, expected);
            if (!File.Exists(report) || new FileInfo(report).Length == 0) throw new InvalidDataException("The merged solution coverage report is missing.");

            await RequireUnchangedSourceAsync(root, commit, proof, cancellationToken).ConfigureAwait(false);

            stage = "aggregate-gate";
            var aggregateGate = Path.Join(coverage, "aggregate-gate");
            var aggregateJson = Path.Join(aggregateGate, "coverage-gate.json");
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
                    "--primary", report, "--timings", timings, "--native-inputs", _settings.NativeCoverageDirectory!,
                    "--diff-base", _settings.DiffBase!, "--run-scope", run["GITHUB_REPOSITORY"]!,
                    "--run-id", run["GITHUB_RUN_ID"]!, "--attempt", run["GITHUB_RUN_ATTEMPT"]!,
                };
                await ExecuteAsync("coverage-fanin", dogfoodArgs, root, proof, cancellationToken, _settings.DotnetPath).ConfigureAwait(false);
                nativeProofHash = await ValidateNativeFanInAsync(fanInOutput, commit, tree, sdk, run, cancellationToken).ConfigureAwait(false);
                var primaryGateDir = Path.Join(coverage, "primary-gate");
                Directory.CreateDirectory(primaryGateDir);
                var primaryGate = Path.Join(primaryGateDir, "coverage-gate.json");
                File.Copy(Path.Join(fanInOutput, "accepted", "primary-gate.json"), primaryGate, overwrite: false);
                var patchGateDir = Path.Join(coverage, "patch-gate");
                Directory.CreateDirectory(patchGateDir);
                patchJson = Path.Join(patchGateDir, "coverage-gate.json");
                File.Copy(Path.Join(fanInOutput, "accepted", "composed-gate.json"), patchJson, overwrite: false);
                ValidateGate(primaryGate, patch: false);
                ValidateGate(patchJson, patch: true);
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
                ValidateGate(patchJson, patch: true);
            }

            ValidateGate(aggregateJson, patch: false);
            if (patchJson is not null) ValidateGate(patchJson, patch: true);
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
            return Failed($"Solution coverage failed during {stage}." + artifactDiagnostic);
        }
    }

    private async Task ExecuteAsync(string name, IReadOnlyList<string> args, string root, List<CommandProof> proof, CancellationToken ct, string? executable = null)
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
        args.AddRange([
            "--coverage", report, "--repository-root", root, "--output", output, "--min-line", patch ? "0" : "95", "--min-branch", patch ? "0" : "85", "--tolerance", "0.5", "--no-github-summary"]);
        if (!string.IsNullOrWhiteSpace(diffBase) && string.IsNullOrWhiteSpace(diffFile))
            args.AddRange(["--diff-base", diffBase, "--diff-label", diffBase]);
        if (!string.IsNullOrWhiteSpace(diffBase) || !string.IsNullOrWhiteSpace(diffFile))
            args.AddRange(["--patch-line-mode", "codecov"]);
        if (patch)
        {
            if (!string.IsNullOrWhiteSpace(diffFile)) args.AddRange(["--diff-file", diffFile, "--diff-label", "pinned local source snapshot"]);
            else if (!string.IsNullOrWhiteSpace(diffBase)) { /* diff source already added above */ }
            args.AddRange(["--min-patch-line", "95", "--min-patch-branch", "85"]);
        }
        return args;
    }

    /// <summary>Requires the installed adapter's delivered acceptance and separate verification, retaining their artifact binding.</summary>
    private static async Task<string> ValidateNativeFanInAsync(string output, string commit, string tree, string sdk,
        IReadOnlyDictionary<string, string?> run, CancellationToken cancellationToken)
    {
        string? policyDigest = null;
        foreach (var operation in new[] { "accept", "verify" })
        {
            using var document = ReadJson(Path.Join(output, operation + "-result.json"), MaxJson);
            var terminal = document.RootElement;
            var result = terminal.GetProperty("result");
            var delivery = terminal.GetProperty("delivery");
            if (terminal.GetProperty("schemaVersion").GetInt32() != 2
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
        }
        var accepted = Path.Join(output, "accepted");
        using var acceptance = ReadJson(Path.Join(accepted, "acceptance.json"), MaxJson);
        var receipt = acceptance.RootElement;
        var source = receipt.GetProperty("source");
        var workflow = receipt.GetProperty("run");
        if (receipt.GetProperty("policyDigest").GetString() != policyDigest
            || source.GetProperty("commit").GetString() != commit || source.GetProperty("tree").GetString() != tree
            || source.GetProperty("sdk").GetString() != sdk || source.GetProperty("configuration").GetString() != "Release"
            || workflow.GetProperty("runScope").GetString() != run["GITHUB_REPOSITORY"]
            || workflow.GetProperty("runId").GetString() != run["GITHUB_RUN_ID"]
            || workflow.GetProperty("attempt").GetInt32() != int.Parse(run["GITHUB_RUN_ATTEMPT"]!, System.Globalization.CultureInfo.InvariantCulture))
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
        return await HashFileAsync(Path.Join(accepted, "acceptance.json"), cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateGate(string path, bool patch)
    {
        using var document = ReadJson(path, MaxJson);
        var root = document.RootElement;
        if (!root.GetProperty("passed").GetBoolean() || root.GetProperty("tolerancePercent").GetDecimal() != .5m)
            throw new InvalidDataException("The coverage gate did not satisfy the declared policy.");
        if (!patch && (root.GetProperty("thresholds").GetProperty("line").GetDecimal() != 95m
            || root.GetProperty("thresholds").GetProperty("branch").GetDecimal() != 85m
            || root.GetProperty("line").GetProperty("percent").GetDecimal() < 94.5m
            || root.GetProperty("branch").GetProperty("percent").GetDecimal() < 84.5m))
            throw new InvalidDataException("The aggregate coverage gate did not satisfy its thresholds.");
        if (patch && (root.GetProperty("thresholds").GetProperty("patchLine").GetDecimal() != 95m
            || root.GetProperty("thresholds").GetProperty("patchBranch").GetDecimal() != 85m
            || root.GetProperty("patchLineMode").GetString() != "codecov"
            || root.GetProperty("patchLine").GetProperty("percent").GetDecimal() < 94.5m
            || root.GetProperty("patchBranch").GetProperty("percent").GetDecimal() < 84.5m))
            throw new InvalidDataException("The patch coverage gate did not satisfy the declared policy.");
    }

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
