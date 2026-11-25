using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Runs ordinary package recipes and routes policy-backed repository EvidenceHost operations.</summary>
internal static class Program
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await RunAsync(args, cancellation.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    /// <summary>Dispatches validated operations; tests may replace only the collection command boundary.</summary>
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default,
        Func<RepositoryCheckSettings, CancellationToken, Task>? collectCoverage = null)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("""
                AppSurface package recipes and repository EvidenceHost operations
                --package <candidate.nupkg> --version <exact-version> --source-commit <sha>
                --dotnet <absolute-dotnet-host> --output <fresh-evidence-directory>
                [--repository <checkout>] [--scratch-parent <existing-directory>]
                [--mode all|storage|consumer] [--consumer-profile <json>]
                [--maximum-scratch-bytes <bytes>] [--minimum-free-bytes <bytes>]
                [--fan-in-request <trusted-request.json>]

                Local development: --operation local|local-plan [--base origin/main]
                [--repository <checkout>] [--output <fresh-directory>] [--dotnet <absolute-host>]
                [--policy <policy.json>] [--scope all|native|coverage|packages]
                Local includes committed, staged, unstaged and untracked changes; only selected producers run.

                CI diff: --operation collect --repository <checkout> --event-file <event.json>
                --event-name pull_request|push|workflow_dispatch --workflow-sha <sha> --output <fresh-directory>

                Repository evidence: --operation plan|run|verify --policy <policy.json>
                --changed-paths <git-paths.json> [--plan <planned/evidence-plan.json>]
                [--scope all|native|coverage|packages]
                plan/run: --output <fresh-directory>
                run: [--repository <checkout>] [--dotnet <absolute-host>]
                [--coverage-cli <absolute-installed-appsurface>]
                run coverage: [--diff-base <commit>] [--diff-file <snapshot.patch>]
                [--native-coverage-directory <same-run-native-captures> [--native-coverage-contract-version 2|3 (default 2)]]
                Native fan-in requires --coverage-cli; without it, coverage uses the source CLI through --dotnet.
                [--coverage-collection <same-run-collection>] [--coverage-schedule-timings <prior-timings.json>]

                CI coverage collection: --operation collect-coverage --repository <checkout>
                --output <fresh-directory> --dotnet <absolute-host> [--coverage-cli <absolute-installed-appsurface>]
                [--coverage-schedule-timings <prior-timings.json>]
                Collection alone never closes coverage obligations.
                verify: --manifest <proof/evidence-manifest.json> --artifact-root <proof/artifacts>

                Packed local-reuse proof: --operation reuse-package-proof --feed <candidate-feed>
                --version <exact-version> --source-commit <sha> --dotnet <absolute-host> --output <fresh-directory>
                This installs the candidate main tool and an independent package-only prepared-host consumer.

                Native coverage capture: --operation capture-native --repository <checkout>
                --report <normalized.xml> --raw-report <collector.xml>
                --output <fresh-capture> --dotnet <absolute-host>

                Defaults: all journeys, 2 GiB scratch per producer, 1 GiB free-space floor.
                Direct --package mode emits a native-validation-receipt with no EvidenceHost claim or authority.
                Repository --operation paths retain their policy-backed manifest and verification contracts.
                Output is caller-owned durable data; only freshly owned scratch is cleaned.
                Direct --package mode: exit zero requires every selected journey, assertion, artifact and cleanup to pass.
                See tools/ForgeTrust.AppSurface.Evidence.Validation/README.md.
                """);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var options = Parse(args);
            if (options.ContainsKey("--feed") && options.GetValueOrDefault("--operation") != "reuse-package-proof")
                throw new ArgumentException("--feed requires --operation reuse-package-proof.");
            if (options.TryGetValue("--operation", out var operation))
            {
                if (operation == "reuse-package-proof")
                {
                    string[] allowed = ["--operation", "--feed", "--version", "--source-commit", "--dotnet", "--output"];
                    if (options.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new ArgumentException("Unsupported reuse package proof option.");
                    return await EvidenceReusePackageProof.RunAsync(Path.GetFullPath(Required(options, "--feed")), Required(options, "--version"),
                        Path.GetFullPath(Required(options, "--output")), Path.GetFullPath(Required(options, "--dotnet")),
                        Required(options, "--source-commit"), cancellationToken);
                }
                if (operation is "local" or "local-plan") return await new LocalEvidenceWorkflow().RunAsync(options, cancellationToken);
                if (operation == "collect-coverage") return await CollectCoverageAsync(options, cancellationToken, collectCoverage);
                if (operation == "collect") return await CollectChangesAsync(options, cancellationToken);
                if (operation == "capture-native") return await CaptureNativeAsync(options, cancellationToken);
            }
            if (options.Keys.Any(key => key is "--policy" or "--operation" or "--changed-paths" or "--plan" or "--manifest" or "--artifact-root"))
                return await RepositoryEvidenceWorkflow.RunAsync(options, cancellationToken);
            if (options.ContainsKey("--native-coverage-contract-version"))
                throw new ArgumentException("Native coverage contract selection requires the repository evidence run operation.");
            var repository = Path.GetFullPath(options.GetValueOrDefault("--repository", Directory.GetCurrentDirectory()));
            var output = Path.GetFullPath(Required(options, "--output"));
            if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Evidence output must be fresh.");
            var dotnet = Path.GetFullPath(Required(options, "--dotnet"));
            if (!File.Exists(dotnet)) throw new ArgumentException("The selected dotnet host does not exist.");
            var mode = options.GetValueOrDefault("--mode", "all");
            if (mode is not ("all" or "storage" or "consumer")) throw new ArgumentException("Mode must be all, storage or consumer.");
            var consumer = options.TryGetValue("--consumer-profile", out var profile)
                ? JsonSerializer.Deserialize<ConsumerValidationOptions>(await File.ReadAllTextAsync(profile, cancellationToken), JsonOptions)
                    ?? throw new ArgumentException("Consumer profile must be an object.") : null;
            var settings = new ValidationSettings(repository, Path.GetFullPath(Required(options, "--package")),
                Required(options, "--version"), Required(options, "--source-commit"), dotnet,
                Path.GetFullPath(options.GetValueOrDefault("--scratch-parent", Path.GetTempPath())), output,
                options.TryGetValue("--maximum-scratch-bytes", out var maximum) ? long.Parse(maximum, System.Globalization.CultureInfo.InvariantCulture) : 2L * 1024 * 1024 * 1024,
                options.TryGetValue("--minimum-free-bytes", out var minimum) ? long.Parse(minimum, System.Globalization.CultureInfo.InvariantCulture) : 1024L * 1024 * 1024, consumer);
            var declarations = new List<EvidenceProducerDeclaration>();
            var producers = new List<IEvidenceProducer>();
            if (mode is "all" or "storage")
            {
                declarations.Add(RepositoryEvidenceWorkflow.Declaration("packed-storage"));
                producers.Add(new PackedStorageProducer(settings));
            }
            if (mode is "all" or "consumer")
            {
                declarations.Add(RepositoryEvidenceWorkflow.Declaration("installed-consumer"));
                producers.Add(new InstalledConsumerProducer(settings));
            }
            EvidenceCoverageFanInRequest? fanIn = null;
            if (options.TryGetValue("--fan-in-request", out var fanInPath))
            {
                fanIn = JsonSerializer.Deserialize<EvidenceCoverageFanInRequest>(await File.ReadAllTextAsync(fanInPath, cancellationToken), JsonOptions)
                    ?? throw new ArgumentException("Fan-in request must be an object.");
                declarations.Add(Declaration("native-fan-in", 300, ["coverage/verified-native-fan-in@1"]));
                producers.Add(new FanInProducer(fanIn));
            }
            var obligations = declarations.SelectMany(producer => producer.AssertionIds.Select((assertion, index) =>
                new EvidenceObligation(producer.Id + "-" + index, "native-acceptance", "Verify the declared native acceptance contract.", [producer.Id], assertion))).ToArray();
            var selected = new EvidenceProfile("native-acceptance", EvidenceProfileScope.Targeted, [], declarations, obligations);
            var inputDigest = EvidenceDigest.Sha256(JsonSerializer.SerializeToUtf8Bytes(new
            {
                package = await ValidationProof.HashAsync(settings.PackagePath, cancellationToken),
                settings.PackageVersion,
                settings.ExpectedSourceCommit,
                settings.MaximumScratchBytes,
                settings.MinimumFreeBytes,
                consumer = settings.Consumer,
                fanIn,
            }));
            var policy = new EvidencePolicy("appsurface-native-validation-" + inputDigest, "1.0.0", selected.Id, [selected], []);
            var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("tools/ForgeTrust.AppSurface.Evidence.Validation/Program.cs")]);
            Directory.CreateDirectory(output);
            await File.WriteAllBytesAsync(Path.Join(output, "evidence-plan.json"), EvidenceCanonicalJson.Serialize(plan), cancellationToken);
            Console.WriteLine("Native package journeys selected: " + string.Join(", ", declarations.Select(d => d.Id)));
            var receipt = await NativeValidationExecution.ExecuteAsync(
                plan, producers, PathUtils.PathUnder(output, "artifacts"), inputDigest, cancellationToken);
            var receiptPath = PathUtils.PathUnder(output, "native-validation-receipt.json");
            await File.WriteAllBytesAsync(receiptPath, NativeValidationReceiptCodec.Serialize(receipt), CancellationToken.None);
            ValidationProof.Require(NativeValidationReceiptCodec.Verify(plan, receipt), "native validation receipt verification");
            Console.WriteLine($"Recipe: {receipt.RunOutcome}; cleanup: {receipt.CleanupCompleted}; receipt: {receiptPath}");
            foreach (var journey in receipt.Journeys)
                Console.WriteLine($"{journey.ProducerId}: {journey.Outcome} {journey.Diagnostic}");
            return receipt.RunOutcome switch { "passed" => 0, "cancelled" => 130, _ => 1 };
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Validation cancelled; no package recipe was marked passed."); return 130; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException
            and not AccessViolationException and not AppDomainUnloadedException)
        {
            Console.Error.WriteLine("Validation failed: " + exception.GetType().Name + ". See retained receipt, manifest or journey artifacts when available.");
            return 1;
        }
    }

    internal static EvidenceProducerDeclaration Declaration(string id, int timeout, string[] assertions, bool transcript = false) =>
        new(id, "native-validation", "1.0.0", [], assertions,
            transcript ? [new("result", "proof", "application/json", true, 1024 * 1024), new("transcript", "proof", "application/json", true, 16 * 1024 * 1024)]
                : [new("result", "proof", "application/json", true, 1024 * 1024)], timeout);

    private static Dictionary<string, string> Parse(string[] args)
    {
        string[] allowed = ["--package", "--feed", "--version", "--source-commit", "--dotnet", "--output", "--repository", "--scratch-parent", "--mode", "--consumer-profile", "--maximum-scratch-bytes", "--minimum-free-bytes", "--fan-in-request", "--operation", "--policy", "--changed-paths", "--plan", "--manifest", "--artifact-root", "--base", "--event-file", "--event-name", "--workflow-sha", "--scope", "--diff-base", "--diff-file", "--native-coverage-directory", "--native-coverage-contract-version", "--coverage-cli", "--report", "--raw-report", "--coverage-collection", "--coverage-schedule-timings"];
        if (args.Length % 2 != 0) throw new ArgumentException("Options require values.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (!allowed.Contains(args[i], StringComparer.Ordinal) || !result.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown or duplicate validation option.");
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Missing required validation option: " + key);

    private static async Task<int> CollectCoverageAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken,
        Func<RepositoryCheckSettings, CancellationToken, Task>? collectCoverage)
    {
        string[] allowed = ["--operation", "--repository", "--output", "--dotnet", "--coverage-cli", "--coverage-schedule-timings"];
        if (options.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new ArgumentException("Unsupported coverage collection option.");
        string? coverageCli = null;
        if (options.TryGetValue("--coverage-cli", out var selectedCoverageCli))
        {
            if (!Path.IsPathFullyQualified(selectedCoverageCli) || !File.Exists(selectedCoverageCli))
                throw new ArgumentException("The selected coverage CLI must be an existing absolute executable.");
            coverageCli = Path.GetFullPath(selectedCoverageCli);
        }
        var settings = new RepositoryCheckSettings(Path.GetFullPath(Required(options, "--repository")),
            Required(options, "--dotnet"), Path.GetFullPath(Required(options, "--output")))
        {
            CoverageCliPath = coverageCli,
            CoverageScheduleTimingsPath = options.TryGetValue("--coverage-schedule-timings", out var timings) ? Path.GetFullPath(timings) : null,
        };
        if (collectCoverage is null) await new SolutionCoverageProducer(settings).CollectAsync(cancellationToken);
        else await collectCoverage(settings, cancellationToken);
        Console.WriteLine("Solution coverage collected; final gate verification is still required.");
        return 0;
    }

    private static async Task<int> CollectChangesAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        string[] allowed = ["--operation", "--repository", "--event-file", "--event-name", "--workflow-sha", "--output"];
        if (options.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new ArgumentException("Unsupported collection option.");
        var output = Path.GetFullPath(Required(options, "--output"));
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Evidence output must be fresh.");
        var changes = await new RepositoryGitChanges().CollectEventAsync(Path.GetFullPath(Required(options, "--repository")),
            Required(options, "--event-file"), Required(options, "--event-name"), options.GetValueOrDefault("--workflow-sha"), cancellationToken);
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Join(output, "changed-paths.json"), EvidenceCanonicalJson.Serialize(changes.Paths), cancellationToken);
        await File.WriteAllTextAsync(Path.Join(output, "source-state.json"), JsonSerializer.Serialize(changes, JsonOptions), cancellationToken);
        Console.WriteLine($"Collected {changes.Paths.Count} changed paths at {changes.HeadCommit}.");
        return 0;
    }

    private static async Task<int> CaptureNativeAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        string[] allowed = ["--operation", "--repository", "--report", "--raw-report", "--output", "--dotnet"];
        if (options.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new ArgumentException("Unsupported native capture option.");
        var repository = Path.GetFullPath(Required(options, "--repository"));
        var head = await RepositoryGitChanges.ReadIdentityAsync(repository, cancellationToken, "rev-parse", "HEAD");
        var tree = await RepositoryGitChanges.ReadIdentityAsync(repository, cancellationToken, "rev-parse", "HEAD^{tree}");
        var clean = await new RepositoryGitChanges().CollectLocalAsync(repository, head, cancellationToken);
        ValidationProof.Require(clean.Paths.Count == 0, "native capture requires an unchanged checkout");
        var dotnet = Required(options, "--dotnet");
        var sdk = await new EvidenceCommandRunner().RunAsync(new EvidenceCommandRequest(dotnet, ["--version"], repository, TimeSpan.FromSeconds(30)), cancellationToken);
        ValidationProof.Require(sdk.ExitCode == 0 && !sdk.OutputTruncated, "native capture SDK identity");
        var expected = System.Xml.Linq.XDocument.Load(Path.Join(repository, "ForgeTrust.AppSurface.slnx"))
            .Descendants("Project").Select(project => project.Attribute("Path")?.Value)
            .Where(path => path is not null && (path.EndsWith(".Tests.csproj", StringComparison.Ordinal) || path.EndsWith(".IntegrationTests.csproj", StringComparison.Ordinal)))
            .Select(path => path!.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var run = new[] { "GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT" }
            .ToDictionary(key => key, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        await EvidenceCoverageFanIn.CaptureAsync(new EvidenceCoverageCaptureRequest(repository,
            Path.GetFullPath(Required(options, "--output")), Path.GetFullPath(Required(options, "--report")),
            Path.GetFullPath(Required(options, "--raw-report")), null, "native", head, tree, sdk.StandardOutput.Trim(), "Release", run,
            OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "Darwin" : "Linux", expected), cancellationToken);
        return 0;
    }

    private sealed class FanInProducer(EvidenceCoverageFanInRequest request) : IEvidenceProducer
    {
        public string Id => "native-fan-in";

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            EvidenceCoverageFanInResult result;
            try { result = await EvidenceCoverageFanIn.VerifyAsync(request, cancellationToken); }
            catch (InvalidDataException exception)
            {
                var diagnostic = exception.Message;
                foreach (var directory in request.NativeCaptureDirectories.Append(request.PrimaryCaptureDirectory).Append(request.ComposedOutputDirectory).Append(request.RepositoryRoot))
                    diagnostic = diagnostic.Replace(directory, "<input>", StringComparison.Ordinal);
                if (diagnostic.Length > 1024) diagnostic = diagnostic[..1024];
                await context.Artifacts!.WriteAsync("result", "proof/result.json", JsonSerializer.SerializeToUtf8Bytes(new { verified = false, diagnostic }), cancellationToken);
                return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Failed, [], "Native coverage fan-in verification failed: " + diagnostic);
            }
            await context.Artifacts!.WriteAsync("result", "proof/result.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                result.Verified,
                result.RetainedClassLines,
                result.RetainedMethodLines,
                result.LinesCovered,
                result.BranchesCovered,
                result.BranchesValid,
                sourceCommit = request.Commit,
                tree = request.Tree,
                sdk = request.Sdk,
            }), cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["coverage/verified-native-fan-in@1"]);
        }
    }
}
