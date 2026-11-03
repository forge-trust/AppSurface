using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>Runs local native-validation procedures without issuing protected EvidenceHost claims.</summary>
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

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("""
                Native AppSurface validation procedures (no protected admission)
                --operation procedure|protected
                --package <candidate.nupkg> --version <exact-version> --source-commit <sha>
                --dotnet <absolute-dotnet-host> --output <fresh-evidence-directory>
                [--repository <checkout>] [--scratch-parent <existing-directory>]
                [--mode all|storage|consumer] [--consumer-profile <json>]
                [--maximum-scratch-bytes <bytes>] [--minimum-free-bytes <bytes>]
                [--fan-in-request <trusted-request.json>]

                Defaults: all journeys, 2 GiB scratch per producer, 1 GiB free-space floor.
                Output is caller-owned durable evidence; only freshly owned scratch is cleaned.
                Procedure exit 0 means all selected local checks passed and cleanup completed.
                Procedure reports never grant admission, native acceptance, or gate eligibility.
                Protected execution remains unavailable until its compiled provider and capabilities are registered.
                See tools/ForgeTrust.AppSurface.Evidence.Validation/README.md.
                """);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var options = Parse(args);
            cancellationToken.ThrowIfCancellationRequested();
            RequireProcedureOperation(options);
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
                declarations.Add(Declaration("packed-storage", 600,
                    ["storage/published-cli@1", "storage/byte-preservation@1", "storage/explicit-retirement@1"], transcript: true));
                producers.Add(new PackedStorageProducer(settings));
            }
            if (mode is "all" or "consumer")
            {
                declarations.Add(Declaration("installed-consumer", 1800,
                    ["consumer/package-identity@1", "consumer/executed-tests@1", "consumer/coverage-gate@1", "consumer/source-integrity@1"]));
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
                new EvidenceObligation(producer.Id + "-" + index, "native-procedure", "Verify the declared local validation contract.", [producer.Id], assertion))).ToArray();
            var selected = new EvidenceProfile("native-procedure", EvidenceProfileScope.Targeted, [], declarations, obligations);
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
            var report = await NativeValidationProcedure.RunAsync(plan, producers, output, cancellationToken);
            await File.WriteAllBytesAsync(Path.Join(output, "procedure-plan.json"), EvidenceCanonicalJson.Serialize(plan), cancellationToken);
            await File.WriteAllBytesAsync(Path.Join(output, "native-validation-procedure.json"),
                JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions), cancellationToken);
            Console.WriteLine("Native validation procedures selected: " + string.Join(", ", declarations.Select(d => d.Id)));
            Console.WriteLine($"Procedure passed: {report.ProcedurePassed}; cleanup: {report.CleanupCompleted}; protected admission: false; native acceptance: false; gate eligibility: false.");
            foreach (var producer in report.ProducerResults)
                Console.WriteLine($"{producer.ProducerId}: {producer.Outcome} {producer.Diagnostic}");
            return report.ProcedurePassed ? 0 : 1;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Native validation cancelled; no protected claim."); return 130; }
        catch (EvidenceHostException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine("Native validation failed: " + exception.GetType().Name + ". See retained procedure report and artifacts when available.");
            return 1;
        }
    }

    /// <summary>Separates ordinary procedure execution from protected claims before input or output I/O.</summary>
    /// <remarks>A procedure result is not an execution mode or admission credential. The compiled protected
    /// provider/capability registry does not currently admit these procedures; no CLI value can enroll them.</remarks>
    private static void RequireProcedureOperation(IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("--operation", out var operation))
            throw new EvidenceHostException("ASEVD401", "No native validation operation was selected.", "Select --operation procedure explicitly for local checks. Protected execution requires registered support; see docs/evidence/evidencehost-migration.md.");
        if (operation == "protected")
            throw new EvidenceHostException("ASEVD404", "Native validation has no registered protected producer capability.", "Complete protected qualification and registration; procedure reports cannot grant admission or gate eligibility. See docs/evidence/evidencehost-migration.md.");
        if (operation != "procedure")
            throw new EvidenceHostException("ASEVD401", "The native validation operation is unknown.", "Select --operation procedure explicitly for local checks; protected execution requires registered support.");
    }

    private static EvidenceProducerDeclaration Declaration(string id, int timeout, string[] assertions, bool transcript = false) =>
        new(id, "native-validation", "1.0.0", [], assertions,
            transcript ? [new("result", "proof", "application/json", true, 1024 * 1024), new("transcript", "proof", "application/json", true, 16 * 1024 * 1024)]
                : [new("result", "proof", "application/json", true, 1024 * 1024)], timeout);

    private static Dictionary<string, string> Parse(string[] args)
    {
        string[] allowed = ["--operation", "--package", "--version", "--source-commit", "--dotnet", "--output", "--repository", "--scratch-parent", "--mode", "--consumer-profile", "--maximum-scratch-bytes", "--minimum-free-bytes", "--fan-in-request"];
        if (args.Length % 2 != 0) throw new ArgumentException("Options require values.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (!allowed.Contains(args[i], StringComparer.Ordinal) || !result.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown or duplicate validation option.");
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Missing required validation option: " + key);

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
