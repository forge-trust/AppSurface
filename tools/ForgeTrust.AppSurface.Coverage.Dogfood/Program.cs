using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;

// This adapter supplies only AppSurface's checked-in policy and exact paths. All capture,
// composition, ownership, gate and verification behavior belongs to installed native commands.
return args.Length > 0 && args[0] == "--package-proof"
    ? await InstalledPackageProof.RunAsync(args[1..])
    : await CoverageDogfood.RunAsync(args);

internal static class CoverageDogfood
{
    private const string ReportProfileId = "coverlet-collector-cobertura-v1";
    private const string CollectorPackageVersion = "10.0.1";
    private const string ShadowProofFileName = "shadow-proof.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine("Print the ordered test filter with --print-test-filter native|shared-cli|lifecycle-cli.");
            Console.WriteLine("Native inputs: --collection-root <coverage-run-output> or --raw-index <project-to-original-json> --timings <json>.");
            Console.WriteLine("AppSurface policy adapter: --operation native|fanin --repository <clean-root> --cli <installed-appsurface> --output <fresh-parent> --configuration Debug|Release [--contract-version 2|3 (default 2)] [--diff-policy-id appsurface-visual-baseline-png-v1 (v3 only)] [--native-platform Linux|Windows|Darwin] [--primary <report> --timings <json> --native-inputs <directory> --diff-base <ref>] --run-scope <repository> --run-id <id> --attempt <positive-integer>");
            return 0;
        }
        var stage = "options";
        try
        {
            if (args is ["--print-test-filter", var lane])
            {
                Console.WriteLine(NativeCoverageTestInventory.ReadFilter(lane));
                return 0;
            }
            if (args.Length % 2 != 0) throw new ArgumentException();
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var allowed = new[] { "operation", "repository", "cli", "output", "configuration", "contract-version", "diff-policy-id", "native-platform", "collection-root", "raw-index", "primary", "timings", "native-inputs", "diff-base", "run-scope", "run-id", "attempt" }.ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(args[i][2..]) || !options.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException();
            var root = Path.GetFullPath(RequireOption(options, "repository"));
            var cli = Path.GetFullPath(RequireOption(options, "cli"));
            var output = Path.GetFullPath(RequireOption(options, "output"));
            var configuration = RequireOption(options, "configuration");
            var contractVersion = options.TryGetValue("contract-version", out var versionText)
                && int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedVersion)
                ? parsedVersion : versionText is null ? 2 : throw new ArgumentException();
            if (contractVersion is not (2 or 3)) throw new ArgumentException();
            options.TryGetValue("diff-policy-id", out var diffPolicyId);
            if (diffPolicyId is not null)
            {
                if (contractVersion != 3)
                    return ReportFailure("options", "diff-policy-version",
                        "Pass --contract-version 3 when using --diff-policy-id.");
                if (diffPolicyId != CoverageFanInPolicy.VisualBaselinePngDiffPolicyId)
                    return ReportFailure("options", "unsupported-diff-policy-id",
                        $"Use {CoverageFanInPolicy.VisualBaselinePngDiffPolicyId} for --diff-policy-id.");
            }
            var operationArguments = ParseOperationArguments(options);
            if (configuration is not ("Debug" or "Release") || Directory.Exists(output) || File.Exists(output)) throw new ArgumentException();
            stage = "source-inventory";
            var solution = XDocument.Load(Path.Join(root, "ForgeTrust.AppSurface.slnx"));
            // Repository-specific inventory is intentionally supplied by this adapter, never inferred by the reusable API.
            var projects = solution.Descendants("Project").Select(x => x.Attribute("Path")?.Value)
                .Where(x => x is not null && (x.EndsWith(".Tests.csproj", StringComparison.Ordinal) || x.EndsWith(".IntegrationTests.csproj", StringComparison.Ordinal)))
                .Select(x => x!.Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            var nativeProjects = NativeCoverageTestInventory.ReadNativeProjects();
            var nativeFilter = NativeCoverageTestInventory.ReadFilter();
            var limits = new CoverageOperationLimits(TotalRecords: 4_000_000);
            stage = "native-inputs";
            var collectionInputs = operationArguments is CollectionArguments collection
                ? NativeCoverageCaptureInputs.FromCollection(collection.Root, nativeProjects, checked((int)limits.JsonBytes))
                : null;
            var measurements = new InvocationMeasurements();
            string gitVersion = "not-checked-v2";
            string commit;
            string tree;
            stage = "source-identity";
            if (contractVersion == 3)
            {
                gitVersion = (await RunGitAsync(root, "HEAD", ["--version"], measurements, "git-version")).Trim();
                CoverageFanInDiffContext.RequireSupportedVersion(gitVersion);
                commit = (await RunGitAsync(root, "HEAD", ["rev-parse", "--verify", "--end-of-options", "HEAD^{commit}"], measurements, "source-commit")).Trim();
                tree = (await RunGitAsync(root, commit, ["rev-parse", "--verify", "--end-of-options", "HEAD^{tree}"], measurements, "source-tree")).Trim();
            }
            else
            {
                commit = await Command(root, "git", ["rev-parse", "HEAD"], measurements, "source-commit");
                tree = await Command(root, "git", ["rev-parse", "HEAD^{tree}"], measurements, "source-tree");
            }
            var sdk = await Command(root, "dotnet", ["--version"], measurements, "sdk-version");
            string? diffBase = null;
            stage = "diff-base";
            if (options.TryGetValue("diff-base", out var requestedDiffBase) && !string.IsNullOrWhiteSpace(requestedDiffBase))
            {
                diffBase = contractVersion == 3
                    ? await ResolveDiffBaseAsync(root, commit, requestedDiffBase, measurements)
                    : requestedDiffBase;
            }
            stage = "collector-policy";
            var policy = new CoverageFanInPolicy(new(commit, tree, sdk.Trim(), configuration),
                new("github-actions", RequireOption(options, "run-scope"), RequireOption(options, "run-id"),
                    int.Parse(RequireOption(options, "attempt"), CultureInfo.InvariantCulture)),
                projects, new[] { "Linux", "Windows", "Darwin" }.Select(x => new CoverageSupplement(x.ToLowerInvariant(), x, nativeFilter, nativeProjects)).ToArray(),
                diffBase is not null
                    ? new(CoverageGateMode.AggregateAndPatch, DiffBase: diffBase) : new(CoverageGateMode.AggregateOnly),
                Limits: limits)
            {
                ContractVersion = contractVersion,
                ReportProfileId = contractVersion == 3 ? ReportProfileId : null,
                DiffPolicyId = diffPolicyId
            };
            if (contractVersion == 3)
                await ValidateCollectorVersionsAsync(root,
                    (operationArguments is NativeArguments ? nativeProjects : projects).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray(), configuration, measurements);
            var policyElement = ToVersionedPolicyElement(policy, contractVersion);
            stage = "pinned-patch";
            var expectedPatchSha256 = contractVersion == 3 && policy.Gate.Mode == CoverageGateMode.AggregateAndPatch
                ? await DeriveExpectedPatchSha256Async(root, policyElement, policy.Limits!, measurements)
                : null;
            stage = "native-inputs";
            var fanIn = operationArguments as FanInArguments;
            var nativeInputs = fanIn is null
                ? null : CoverageFanInNativeInputs.Discover(fanIn.NativeInputs, policy.Supplements.Select(x => x.Id));
            stage = "prepare-output";
            Directory.CreateDirectory(output);
            var accounting = new List<OperationAccounting>();
            async Task Invoke(string operation, object request, string suffix)
            {
                stage = "command-" + suffix;
                var requestFile = Path.Join(output, suffix + "-request.json");
                var resultFile = Path.Join(output, suffix + "-result.json");
                var requestProjection = RequestProjection(request, policyElement, contractVersion,
                    operation == "accept" ? expectedPatchSha256 : null);
                await File.WriteAllBytesAsync(requestFile,
                    JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = contractVersion, operation, request = requestProjection }, Json));
                _ = await Command(root, cli,
                    ["coverage", "fan-in", operation, "--request", requestFile, "--result", resultFile, "--json"],
                    measurements, "fanin-" + suffix);
                if (contractVersion == 3)
                    accounting.Add(ReadOperationAccounting(resultFile, operation, policy.Limits!.TotalBytes));
            }
            if (operationArguments is NativeArguments native)
            {
                stage = "native-arguments";
                var inputs = native is ReplayArguments replay
                    ? NativeCoverageCaptureInputs.FromReplay(replay.RawIndex, replay.Timings)
                    : collectionInputs!;
                await Invoke("capture", new CoverageCaptureRequest(policy, root, Path.Join(output, "capture"), native.Platform.ToLowerInvariant(),
                    TimingsPath: inputs.TimingsPath, RawCoveragePaths: inputs.RawCoveragePaths), "native");
                stage = "native-proof";
                if (contractVersion == 3)
                    await WriteShadowProofAsync(output, "native", policy, gitVersion, measurements, accounting,
                        expectedPatchSha256, policy.Limits!.TotalBytes);
            }
            else if (fanIn is not null)
            {
                var primary = Path.Join(output, "primary");
                await Invoke("capture", new CoverageCaptureRequest(policy, root, primary, "primary", fanIn.Primary, fanIn.Timings), "primary");
                var imported = new List<string>();
                foreach (var supplement in policy.Supplements)
                {
                    var input = nativeInputs![supplement.Id];
                    var destination = Path.Join(output, "native-" + supplement.Id);
                    await Invoke("capture", new CoverageCaptureRequest(policy, root, destination, supplement.Id,
                        ImportDirectory: input.Directory, OriginalSourceRoot: input.SourceRoot), supplement.Id);
                    imported.Add(destination);
                }
                var composed = Path.Join(output, "composed"); var accepted = Path.Join(output, "accepted");
                await Invoke("compose", new CoverageComposeRequest(policy, root, primary, imported, composed), "compose");
                await Invoke("accept", new CoverageAcceptRequest(policy, root, primary, imported, composed, accepted), "accept");
                if (contractVersion == 3)
                    CoverageDogfoodResultValidation.RequireV3AcceptedResult(
                        Path.Join(output, "accept-result.json"), "accept", policy.Gate.Mode);
                await Invoke("verify", new CoverageVerifyRequest(policy, root, primary, imported, composed, accepted), "verify");
                if (contractVersion == 3)
                {
                    CoverageDogfoodResultValidation.RequireV3AcceptedResult(
                        Path.Join(output, "verify-result.json"), "verify", policy.Gate.Mode);
                    stage = "fan-in-proof";
                    await WriteShadowProofAsync(output, "fanin", policy, gitVersion, measurements, accounting,
                        expectedPatchSha256, policy.Limits!.TotalBytes);
                }
            }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or JsonException
            or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or UnauthorizedAccessException
            or CoverageExecutionException or OperationCanceledException)
        {
            var (reason, recovery) = exception is CoverageFanInEvidenceException evidence
                ? (evidence.Cause, evidence.Recovery)
                : (exception.GetType().Name, "Inspect retained native command result files and correct the explicit repository policy/configuration.");
            return ReportFailure(stage, reason, recovery);
        }
    }

    /// <summary>Resolves the selected operation and its required arguments before any file reads or output creation.</summary>
    private static OperationArguments ParseOperationArguments(IReadOnlyDictionary<string, string> options)
    {
        var operation = RequireOption(options, "operation");
        var hasCollectionRoot = options.ContainsKey("collection-root");
        if (hasCollectionRoot && (operation != "native" || options.ContainsKey("raw-index") || options.ContainsKey("timings")))
            throw new ArgumentException("--collection-root is valid only for native capture and cannot be combined with explicit replay inputs.");

        if (operation == "fanin")
            return new FanInArguments(RequireOption(options, "primary"), RequireOption(options, "timings"),
                RequireOption(options, "native-inputs"));

        if (operation != "native") throw new ArgumentException();
        var platform = RequireOption(options, "native-platform");
        if (platform is not ("Linux" or "Windows" or "Darwin")) throw new ArgumentException();
        return hasCollectionRoot
            ? new CollectionArguments(platform, RequireOption(options, "collection-root"))
            : new ReplayArguments(platform, RequireOption(options, "raw-index"), RequireOption(options, "timings"));
    }

    private static string RequireOption(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException();

    private abstract record OperationArguments;
    private abstract record NativeArguments(string Platform) : OperationArguments;
    private sealed record CollectionArguments(string Platform, string Root) : NativeArguments(Platform);
    private sealed record ReplayArguments(string Platform, string RawIndex, string Timings) : NativeArguments(Platform);
    private sealed record FanInArguments(string Primary, string Timings, string NativeInputs) : OperationArguments;

    private static int ReportFailure(string stage, string reason, string recovery)
    {
        Console.Error.WriteLine($"ASCOV311 Dogfood failed at {stage} ({reason}). {recovery} Preserve partial output.");
        return 1;
    }

    /// <summary>Qualifies the source-bound collector package for every project/framework before labeling fresh reports.</summary>
    private static async Task ValidateCollectorVersionsAsync(string root, IReadOnlyList<string> projects,
        string configuration, InvocationMeasurements measurements)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        await using var supervisor = new CoverageRunWatchdogSupervisor(CoverageRunWatchdogMode.Off,
            TimeSpan.Zero, TimeSpan.Zero, CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null),
            TimeProvider.System, deadline.Token);
        var resolution = new CoverageProjectResolution(null, root, projects.Select((path, index) =>
            new CoverageRunProject(path, Path.GetFullPath(path, root), "profile-" + index, false)).ToArray(), []);
        var started = Stopwatch.GetTimestamp();
        await CoverageRunDriverPreflight.ValidateAsync(CoverageRunDriver.Collector, configuration, resolution,
            new CliWrapCoverageRunProcessRunner(), supervisor, deadline.Token, requiredCollectorVersion: CollectorPackageVersion);
        measurements.Record("collector-profile", Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, 0);
        measurements.VerifiedCollectorVersion = CollectorPackageVersion;
    }

    private static async Task<string> Command(string root, string executable, string[] arguments,
        InvocationMeasurements? measurements = null, string stage = "command")
    {
        var started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token); var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        long? peakWorkingSet = null;
        try
        {
            var exit = process.WaitForExitAsync(deadline.Token);
            while (!exit.IsCompleted)
            {
                // Some runtimes cannot query a reaped child. Resource telemetry is optional and
                // must not turn a successful v2/v3 command into a failed operation.
                if (CoverageProcessTelemetry.TryGetPeakWorkingSet(process) is { } observed)
                    peakWorkingSet = Math.Max(peakWorkingSet ?? 0, observed);
                await Task.WhenAny(exit, Task.Delay(25, deadline.Token));
            }
            await exit;
        }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        var result = await stdout; var errors = await stderr;
        measurements?.Record(stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds, peakWorkingSet, process.ExitCode);
        if (process.ExitCode != 0 || result.Length > 1048576 || errors.Length > 1048576) throw new InvalidDataException();
        return result.Trim();
    }

    private static async Task<string> RunGitAsync(string root, string attributeSource, string[] arguments,
        InvocationMeasurements measurements, string stage)
    {
        var started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new CoverageRunProcessRequest("git", GitPrefix().Concat(arguments).ToArray(), root,
            null, null, CoverageRunProcessLease.Detached())
        {
            EnvironmentOverrides = CoverageFanInDiffContext.ChildEnvironment(attributeSource),
            StandardOutputLimitBytes = CoverageFanInDiffContext.StreamLimit,
            StrictUtf8 = true
        };
        var result = await new CliWrapCoverageRunProcessRunner().RunAsync(request, deadline.Token).ConfigureAwait(false);
        measurements.Record(stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, result.ExitCode);
        if (result.OutputTruncated || result.ExitCode != 0 || !string.IsNullOrEmpty(result.StandardError))
            throw new InvalidDataException("The pinned Git identity could not be resolved under the v3 process policy.");
        return result.StandardOutput ?? result.Output;
    }

    private static string[] GitPrefix()
    {
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        return ["--no-pager", "--no-replace-objects", "-c", "core.fsmonitor=false", "-c", "core.quotePath=true",
            "-c", "core.attributesFile=" + nullDevice, "-c", "diff.renames=true", "-c", "diff.renameLimit=1000",
            "-c", "diff.orderFile=" + nullDevice, "-c", "diff.mnemonicPrefix=false", "-c", "diff.noprefix=false",
            "-c", "diff.suppressBlankEmpty=false"];
    }

    private static async Task<string> ResolveDiffBaseAsync(string root, string commit, string requested,
        InvocationMeasurements measurements)
    {
        var format = (await RunGitAsync(root, commit, ["rev-parse", "--show-object-format"], measurements, "object-format")).Trim();
        var length = format switch { "sha1" => 40, "sha256" => 64, _ => throw new InvalidDataException("Unsupported Git object format.") };
        var resolved = (await RunGitAsync(root, commit,
            ["rev-parse", "--verify", "--end-of-options", requested + "^{commit}"], measurements, "resolve-diff-base")).Trim();
        if (resolved.Length != length || resolved.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("The requested patch base did not resolve to a full lowercase commit ID.");
        return resolved;
    }

    private static async Task<string> DeriveExpectedPatchSha256Async(string root, JsonElement policy,
        CoverageOperationLimits limits, InvocationMeasurements measurements)
    {
        var budget = new CoverageFanInBudget(limits.TotalBytes);
        var expected = new CoverageFanInExpected(policy, string.Empty);
        var context = new CoverageFanInDiffContext(new CoverageFanInEngineRequest(expected, root), budget);
        var started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(limits.OperationSeconds));
        await context.InitializeAsync(deadline.Token).ConfigureAwait(false);
        var diff = await context.ReadAsync(deadline.Token).ConfigureAwait(false);
        measurements.ExpectedPatchBudgetBytes = limits.TotalBytes - budget.Remaining;
        measurements.Record("expected-patch-diff", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            null, 0);
        return Convert.ToHexStringLower(SHA256.HashData(diff.Patch));
    }

    /// <summary>Explicit v2/v3 policy wire whitelist. Never serialize CoverageFanInPolicy itself.</summary>
    private static JsonElement ToVersionedPolicyElement(CoverageFanInPolicy policy, int contractVersion)
    {
        if (policy.ContractVersion != contractVersion || contractVersion is not (2 or 3)
            || contractVersion == 2 && (policy.ReportProfileId is not null || policy.DiffPolicyId is not null)
            || contractVersion == 3 && policy.ReportProfileId != ReportProfileId)
            throw new InvalidDataException("The explicit fan-in policy version/profile selection is inconsistent.");
        var supplements = policy.Supplements.OrderBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x with { TestProjects = x.TestProjects?.Order(StringComparer.Ordinal).ToArray() }).ToArray();
        var sortedPolicy = policy with
        {
            Gate = policy.Gate with { },
            Instrumentation = policy.Instrumentation is null ? new() : policy.Instrumentation with { },
            Limits = policy.Limits is null ? new() : policy.Limits with { },
            Run = policy.Run with { },
            Source = policy.Source with { },
            Supplements = supplements,
            TestProjects = policy.TestProjects.Order(StringComparer.Ordinal).ToArray()
        };
        object projection = contractVersion switch
        {
            2 => new PolicyV2Document(sortedPolicy.Gate, sortedPolicy.Instrumentation!, sortedPolicy.Limits!,
                sortedPolicy.Run, sortedPolicy.Source, sortedPolicy.Supplements, sortedPolicy.TestProjects),
            3 => new PolicyV3Document(3, ReportProfileId, sortedPolicy.Gate, sortedPolicy.Instrumentation!,
                sortedPolicy.Limits!, sortedPolicy.Run, sortedPolicy.Source, sortedPolicy.Supplements, sortedPolicy.TestProjects, sortedPolicy.DiffPolicyId),
            _ => throw new InvalidDataException("Unsupported fan-in contract version.")
        };
        // Keep this explicit v2/v3 whitelist because CoveragePolicyEncoding is internal to
        // the Coverage assembly. The CLI remains the owner of canonical decoding/digest checks.
        return JsonSerializer.SerializeToElement(projection, projection.GetType(), Json);
    }

    private static object RequestProjection(object request, JsonElement policy, int contractVersion,
        string? expectedPatchSha256)
        => request switch
        {
            CoverageCaptureRequest capture => new CaptureRequestProjection(policy, capture.RepositoryRoot,
                capture.OutputDirectory, capture.ProducerId, capture.CoveragePath, capture.TimingsPath,
                capture.RawCoveragePath, capture.ImportDirectory, capture.OriginalSourceRoot, capture.OutputMode,
                capture.RawCoveragePaths),
            CoverageComposeRequest compose => new ComposeRequestProjection(policy, compose.RepositoryRoot,
                compose.PrimaryCaptureDirectory, compose.SupplementaryCaptureDirectories, compose.OutputDirectory,
                compose.OutputMode, compose.EvaluationIntent),
            CoverageAcceptRequest accept => new AcceptRequestProjection(policy, accept.RepositoryRoot,
                accept.PrimaryCaptureDirectory, accept.SupplementaryCaptureDirectories, accept.ComposedOutputDirectory,
                accept.OutputDirectory, accept.OutputMode, accept.EvaluationIntent,
                contractVersion == 3 ? expectedPatchSha256 : accept.ExpectedPatchSha256),
            CoverageVerifyRequest verify => new VerifyRequestProjection(policy, verify.RepositoryRoot,
                verify.PrimaryCaptureDirectory, verify.SupplementaryCaptureDirectories, verify.ComposedOutputDirectory,
                verify.AcceptedOutputDirectory, verify.EvaluationIntent),
            _ => throw new InvalidDataException("Unsupported fan-in request projection.")
        };

    private static OperationAccounting ReadOperationAccounting(string path, string operation, long expectedLimit)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        var result = root.GetProperty("result");
        if (root.GetProperty("schemaVersion").GetInt32() != 3 || root.GetProperty("operation").GetString() != operation
            || root.GetProperty("status").GetString() != "succeeded")
            throw new InvalidDataException("The v3 operation result is missing or has a different contract identity.");
        if (result.GetProperty("outcome").GetString() != "succeeded")
            throw new InvalidDataException("The v3 operation result did not succeed.");
        var accounting = result.GetProperty("accounting");
        var used = accounting.GetProperty("usedBytes").GetInt64();
        var limit = accounting.GetProperty("limitBytes").GetInt64();
        if (used < 0 || limit != expectedLimit || used > limit)
            throw new InvalidDataException("The v3 operation accounting is missing, inconsistent, or exceeds its fixed byte limit.");
        long Counter(string name)
        {
            var value = accounting.GetProperty(name).GetInt64();
            if (value < 0) throw new InvalidDataException("Negative v3 proof accounting.");
            return value;
        }
        var record = new OperationAccounting(operation, used, limit, Counter("xmlBytes"), Counter("jsonBytes"),
            Counter("lineRecords"), Counter("branchRecords"), Counter("stagedBytes"), Counter("stagedFiles"));
        if (record.XmlBytes > used || record.JsonBytes > used || record.StagedBytes > used
            || record.LineRecords + record.BranchRecords > 4_000_000 || record.StagedFiles > 10_000)
            throw new InvalidDataException("V3 proof counters exceed unchanged admission limits.");
        return record;
    }

    private static async Task WriteShadowProofAsync(string output, string operation, CoverageFanInPolicy policy,
        string gitVersion, InvocationMeasurements measurements, IReadOnlyList<OperationAccounting> accounting,
        string? expectedPatchSha256, long totalBytesLimit)
    {
        if (accounting.Count == 0 || accounting.Any(x => x.UsedBytes > x.LimitBytes))
            throw new InvalidDataException("The v3 shadow is missing operation-local decoded/staged budget accounting.");
        var outputFiles = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), ShadowProofFileName, StringComparison.Ordinal))
            .Select(path => new FileInfo(path)).ToArray();
        var outputBytes = outputFiles.Aggregate(0L, (sum, file) => checked(sum + file.Length));
        using var ownProcess = Process.GetCurrentProcess();
        var ownPeak = CoverageProcessTelemetry.TryGetPeakWorkingSet(ownProcess);
        var observedPeaks = measurements.Processes.Where(x => x.PeakWorkingSetBytes is > 0)
            .Select(x => x.PeakWorkingSetBytes!.Value).Concat(ownPeak is { } ownValue ? [ownValue] : Array.Empty<long>()).ToArray();
        var commandsWithinDeadline = measurements.Processes.All(x => x.ElapsedMilliseconds <= policy.Limits!.OperationSeconds * 1000d);
        var proof = new
        {
            schemaVersion = 1,
            contractVersion = policy.ContractVersion,
            reportProfileId = policy.ReportProfileId,
            operation,
            run = new { scope = policy.Run.RunScope, id = policy.Run.RunId, attempt = policy.Run.Attempt },
            source = new { commit = policy.Source.Commit, tree = policy.Source.Tree, sdk = policy.Source.Sdk, configuration = policy.Source.Configuration },
            collector = new { packageId = "coverlet.collector", verifiedVersion = measurements.VerifiedCollectorVersion },
            runtime = new
            {
                operatingSystem = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                gitVersion
            },
            diff = new { baseCommit = policy.Gate.DiffBase, expectedPatchSha256 },
            limits = new { totalBytes = totalBytesLimit, operationSeconds = policy.Limits!.OperationSeconds, identitySeconds = policy.Limits.IdentitySeconds },
            accounting = new
            {
                scope = "UsedBytes is the existing logical operation budget for streams, distinct Git blob work, retained inputs, and staged publication; it does not measure XML DOM heap or peak staged storage.",
                operations = accounting,
                maximumOperationUsedBytes = accounting.Max(x => x.UsedBytes),
                logicalOperationBudgetWithinTotalBytes = accounting.All(x => x.UsedBytes <= x.LimitBytes && x.LimitBytes == totalBytesLimit),
                expectedPatchDiffBytes = measurements.ExpectedPatchBudgetBytes,
                maximumAdmittedXmlBytes = accounting.Max(x => x.XmlBytes),
                maximumAdmittedJsonBytes = accounting.Max(x => x.JsonBytes),
                maximumAdmittedRecords = accounting.Max(x => x.LineRecords + x.BranchRecords),
                maximumStagedBytes = accounting.Max(x => x.StagedBytes),
                maximumStagedFiles = accounting.Max(x => x.StagedFiles)
            },
            retainedOutput = new { bytes = outputBytes, files = outputFiles.Length },
            process = new
            {
                peakWorkingSetMeasurement = "Maximum observed Process.PeakWorkingSet64 for this adapter and its direct child processes; child Git processes supervised inside the CLI are not sampled individually.",
                peakWorkingSetBytes = observedPeaks.Length == 0 ? (long?)null : observedPeaks.Max(),
                measuredProcessCount = observedPeaks.Length,
                elapsedMilliseconds = Stopwatch.GetElapsedTime(measurements.StartedAt).TotalMilliseconds,
                commandsWithinOperationDeadline = commandsWithinDeadline,
                commands = measurements.Processes
            },
            completed = true
        };
        if (!commandsWithinDeadline || !proof.accounting.logicalOperationBudgetWithinTotalBytes)
            throw new InvalidDataException("The v3 shadow exceeded a recorded existing operation limit.");
        await File.WriteAllBytesAsync(Path.Join(output, ShadowProofFileName), JsonSerializer.SerializeToUtf8Bytes(proof, Json));
    }

    private sealed record PolicyV2Document(CoverageGatePolicy Gate, CoverageInstrumentation Instrumentation,
        CoverageOperationLimits Limits, CoverageRunIdentity Run, CoverageSourceIdentity Source,
        IReadOnlyList<CoverageSupplement> Supplements, IReadOnlyList<string> TestProjects);
    private sealed record PolicyV3Document(int ContractVersion, string ReportProfileId, CoverageGatePolicy Gate,
        CoverageInstrumentation Instrumentation, CoverageOperationLimits Limits, CoverageRunIdentity Run,
        CoverageSourceIdentity Source, IReadOnlyList<CoverageSupplement> Supplements, IReadOnlyList<string> TestProjects,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DiffPolicyId);
    private sealed record CaptureRequestProjection(JsonElement Policy, string RepositoryRoot, string OutputDirectory,
        string ProducerId, string? CoveragePath, string? TimingsPath, string? RawCoveragePath, string? ImportDirectory,
        string? OriginalSourceRoot, CoverageOutputMode OutputMode, IReadOnlyDictionary<string, string>? RawCoveragePaths);
    private sealed record ComposeRequestProjection(JsonElement Policy, string RepositoryRoot, string PrimaryCaptureDirectory,
        IReadOnlyList<string> SupplementaryCaptureDirectories, string OutputDirectory, CoverageOutputMode OutputMode,
        CoverageEvaluationIntent EvaluationIntent);
    private sealed record AcceptRequestProjection(JsonElement Policy, string RepositoryRoot, string PrimaryCaptureDirectory,
        IReadOnlyList<string> SupplementaryCaptureDirectories, string ComposedOutputDirectory, string OutputDirectory,
        CoverageOutputMode OutputMode, CoverageEvaluationIntent EvaluationIntent,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedPatchSha256);
    private sealed record VerifyRequestProjection(JsonElement Policy, string RepositoryRoot, string PrimaryCaptureDirectory,
        IReadOnlyList<string> SupplementaryCaptureDirectories, string ComposedOutputDirectory, string AcceptedOutputDirectory,
        CoverageEvaluationIntent EvaluationIntent);
    private sealed record OperationAccounting(string Operation, long UsedBytes, long LimitBytes,
        long XmlBytes, long JsonBytes, long LineRecords, long BranchRecords, long StagedBytes, long StagedFiles);
    private sealed record ProcessMeasurement(string Stage, double ElapsedMilliseconds, long? PeakWorkingSetBytes, int ExitCode);

    private sealed class InvocationMeasurements
    {
        internal long StartedAt { get; } = Stopwatch.GetTimestamp();
        internal List<ProcessMeasurement> Processes { get; } = [];
        internal long ExpectedPatchBudgetBytes { get; set; }
        internal string? VerifiedCollectorVersion { get; set; }
        internal void Record(string stage, double elapsedMilliseconds, long? peakWorkingSetBytes, int exitCode) =>
            Processes.Add(new(stage, elapsedMilliseconds, peakWorkingSetBytes, exitCode));
    }
}
