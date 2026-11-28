using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>
/// Resolves the repository's real changed-path inventory, executes only its selected acceptance producers,
/// and verifies gate eligibility against the original plan and retained artifact bytes.
/// </summary>
/// <remarks>
/// This internal adapter accepts the native, solution coverage and package-check producer contracts.
/// It does not provision resources, aggregate job manifests, or authenticate provenance. Inputs and outputs
/// must come from the current trusted workflow workspace. See this tool's README for command contracts.
/// </remarks>
internal static class RepositoryEvidenceWorkflow
{
    private const int MaximumJsonBytes = 16 * 1024 * 1024;

    private sealed record LocalExecution(
        LocalEvidenceRun RunState,
        Func<CancellationToken, Task> VerifySourceAsync,
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>>? RunProducerCommand);

    /// <summary>Runs one explicit repository planning, execution, or verification operation.</summary>
    /// <param name="options">Validated command options and trusted plan inputs.</param>
    /// <param name="cancellationToken">Cancels execution and artifact verification.</param>
    /// <returns>Zero after the operation completes successfully.</returns>
    internal static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        await RunCoreAsync(options, Required(options, "--operation"), cancellationToken, null);
        return 0;
    }

    /// <summary>Runs local producers and publishes a gate claim only after verifying the local source and artifacts.</summary>
    /// <param name="options">Execution options with `--operation run` and trusted plan inputs.</param>
    /// <param name="cancellationToken">Cancels execution and artifact verification.</param>
    /// <param name="verifyLocalSourceAsync">Checks the pinned source and policy and records unconfirmed cleanup.</param>
    /// <param name="localRun">The local run whose command and host cleanup are tracked.</param>
    /// <param name="runProducerCommand">Optional override for executing registered producers' commands.</param>
    /// <returns>The published manifest after all local checks pass.</returns>
    /// <exception cref="ArgumentException">The operation is missing or differs from <c>run</c>.</exception>
    internal static async Task<EvidenceManifest> RunLocalAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken,
        Func<CancellationToken, Task> verifyLocalSourceAsync, LocalEvidenceRun localRun,
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>>? runProducerCommand = null)
    {
        var operation = Required(options, "--operation");
        if (operation != "run")
            throw new ArgumentException("Local evidence execution requires --operation run.", nameof(options));

        return await RunCoreAsync(options, operation, cancellationToken, new(localRun, verifyLocalSourceAsync, runProducerCommand))
            ?? throw new InvalidOperationException("Local execution must produce an evidence manifest.");
    }

    private static async Task<EvidenceManifest?> RunCoreAsync(IReadOnlyDictionary<string, string> options, string operation,
        CancellationToken cancellationToken, LocalExecution? local)
    {
        if (operation is not ("plan" or "run" or "verify")) throw new ArgumentException("Operation must be plan, run or verify.");
        ValidateOptions(options, operation);
        var policy = Read<EvidencePolicy>(Required(options, "--policy"), cancellationToken);
        var paths = Read<NormalizedDiffPath[]>(Required(options, "--changed-paths"), cancellationToken);
        var rootPlan = new EvidencePlanner().Resolve(policy, paths);
        foreach (var profile in policy.Profiles) ValidateProfile(profile);
        var conservative = policy.Profiles.Single(profile => profile.Id == policy.ConservativeProfileId);
        ValidationProof.Require(new[] { "packed-storage", "installed-consumer", "solution-coverage", "package-checks" }.All(id =>
            conservative.Producers.Any(producer => producer.Id == id)), "conservative profile retains all repository checks");
        ValidationProof.Require(!paths.Any(path => path.Path == ".github/evidence.policy.json"
                || path.PreviousPath == ".github/evidence.policy.json")
            || conservative.Producers.All(producer => rootPlan.Profile.Producers.Any(selected => selected.Id == producer.Id)), "policy changes require conservative evidence");
        var scope = options.GetValueOrDefault("--scope", "all");
        var plan = ResolveScope(policy, paths, scope);
        Console.WriteLine($"Evidence profile: {plan.Profile.Id}; rules: {string.Join(", ", plan.MatchedRuleIds)}");
        Console.WriteLine("Required producers: " + string.Join(", ", plan.Profile.Producers.Select(producer => producer.Id)));

        if (operation != "plan")
        {
            var original = Read<EvidencePlan>(Required(options, "--plan"), cancellationToken);
            ValidationProof.Require(EvidenceCanonicalJson.Serialize(original).AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(plan)),
                "original plan matches current policy and changed paths");
        }

        if (operation == "verify")
        {
            var manifest = Read<EvidenceManifest>(Required(options, "--manifest"), cancellationToken);
            await VerifyAsync(plan, manifest, Required(options, "--artifact-root"), cancellationToken);
            Console.WriteLine($"Evidence gate passed: {manifest.ClaimKind} ({plan.PlanDigest})");
            return null;
        }

        var output = Path.GetFullPath(Required(options, "--output"));
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Evidence output must be fresh.");
        var nativeCoverageDirectory = options.GetValueOrDefault("--native-coverage-directory");
        var nativeCoverageContractVersion = 2;
        if (options.TryGetValue("--native-coverage-contract-version", out var nativeContract))
        {
            if (string.IsNullOrWhiteSpace(nativeCoverageDirectory) || nativeContract is not ("2" or "3")
                || !plan.Profile.Producers.Any(producer => producer.Id == "solution-coverage"))
                throw new ArgumentException("Select native coverage contract 2 or 3 only for a selected solution coverage producer with native captures.");
            nativeCoverageContractVersion = nativeContract == "3" ? 3 : 2;
        }
        ValidationSettings? settings = null;
        RepositoryCheckSettings? checks = null;
        if (operation == "run" && plan.Profile.Producers.Count > 0)
        {
            var dotnet = Path.GetFullPath(Required(options, "--dotnet"));
            if (!File.Exists(dotnet)) throw new ArgumentException("The selected dotnet host does not exist.");
            var repository = Path.GetFullPath(options.GetValueOrDefault("--repository", Directory.GetCurrentDirectory()));
            string? coverageCli = null;
            if (options.TryGetValue("--coverage-cli", out var selectedCoverageCli))
            {
                if (!Path.IsPathFullyQualified(selectedCoverageCli) || !File.Exists(selectedCoverageCli))
                    throw new ArgumentException("The selected coverage CLI must be an existing absolute executable.");
                coverageCli = Path.GetFullPath(selectedCoverageCli);
            }
            if (!string.IsNullOrWhiteSpace(nativeCoverageDirectory) && coverageCli is null)
                throw new ArgumentException("Native coverage requires --coverage-cli with the installed candidate executable.");
            if (RequiresPackage(plan.Profile))
                settings = new ValidationSettings(repository, Path.GetFullPath(Required(options, "--package")), Required(options, "--version"),
                    Required(options, "--source-commit"), dotnet,
                    Path.GetFullPath(options.GetValueOrDefault("--scratch-parent", Path.GetTempPath())), output);
            checks = new RepositoryCheckSettings(repository, dotnet, output,
                options.GetValueOrDefault("--diff-base"), nativeCoverageDirectory, options.GetValueOrDefault("--diff-file"), coverageCli)
            {
                NativeCoverageContractVersion = nativeCoverageContractVersion,
                VerifyLocalSourceAsync = local?.VerifySourceAsync,
                PackageRestoreCache = local?.RunState.PackageRestoreCache,
                CoverageCollectionDirectory = options.GetValueOrDefault("--coverage-collection"),
                CoverageScheduleTimingsPath = options.TryGetValue("--coverage-schedule-timings", out var timings) ? Path.GetFullPath(timings) : null,
            };
        }

        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(PathUtils.PathUnder(output, RepositoryEvidenceLayout.PlanFile), EvidenceCanonicalJson.Serialize(plan), cancellationToken);
        await File.WriteAllTextAsync(PathUtils.PathUnder(output, RepositoryEvidenceLayout.SelectionFile), JsonSerializer.Serialize(new
        {
            profileId = plan.Profile.Id,
            requiresPackage = RequiresPackage(plan.Profile),
            hasProducers = plan.Profile.Producers.Count > 0,
            requiresCoverage = plan.Profile.Producers.Any(producer => producer.Id == "solution-coverage"),
            requiresNativeCoverage = rootPlan.Profile.Producers.Any(producer => producer.Id == "solution-coverage")
                && rootPlan.Profile.Producers.Any(producer => producer.Id != "solution-coverage")
                && !IsToolManifestOnlyChange(rootPlan.ChangedPaths),
            requiresPackageChecks = plan.Profile.Producers.Any(producer => producer.Id == "package-checks"),
            producerIds = plan.Profile.Producers.Select(producer => producer.Id).ToArray(),
        }, Program.JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(PathUtils.PathUnder(output, RepositoryEvidenceLayout.SummaryFile), Summary(plan), cancellationToken);
        if (operation == "plan") return null;

        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>>? command = null;
        if (local is not null)
        {
            var runner = local.RunProducerCommand ?? new EvidenceCommandRunner().RunAsync;
            command = (request, token) => local.RunState.ObserveCommandAsync(runner, request, token);
        }

        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            foreach (var declaration in plan.Profile.Producers)
            {
                IEvidenceProducer producer = declaration.Id switch
                {
                    "packed-storage" => new PackedStorageProducer(settings!, command),
                    "installed-consumer" => new InstalledConsumerProducer(settings!, command),
                    "solution-coverage" => new SolutionCoverageProducer(checks!, command),
                    "package-checks" => new PackageChecksProducer(checks!, command),
                    _ => throw new InvalidOperationException("Unsupported repository evidence producer."),
                };
                registration.AddProducer(producer);
            }
        }, new EvidenceHostOptions(ArtifactDirectory: PathUtils.PathUnder(output, RepositoryEvidenceLayout.ArtifactsDirectory)));
        cancellationToken.ThrowIfCancellationRequested();
        local?.RunState.BeginHostExecution();
        var result = await host.RunAsync(cancellationToken: cancellationToken);
        local?.RunState.RecordHostCleanup(result.Metrics.CleanupCompleted);
        foreach (var producer in result.ProducerResults) Console.WriteLine($"{producer.ProducerId}: {producer.Outcome} {producer.Diagnostic}");
        if (local is not null)
        {
            await FinalizeLocalManifestAsync(plan, result, output, local.VerifySourceAsync, local.RunState, cancellationToken);
            return result;
        }

        await File.WriteAllBytesAsync(PathUtils.PathUnder(output, RepositoryEvidenceLayout.ManifestFile), EvidenceCanonicalJson.Serialize(result), CancellationToken.None);
        await File.AppendAllTextAsync(PathUtils.PathUnder(output, RepositoryEvidenceLayout.SummaryFile), ClaimSummary(result), CancellationToken.None);
        await VerifyAsync(plan, result, PathUtils.PathUnder(output, RepositoryEvidenceLayout.ArtifactsDirectory), cancellationToken);
        return result;
    }

    /// <summary>Saves producer observations, then promotes the manifest after the local gate checks pass.</summary>
    /// <param name="plan">The pinned local plan.</param>
    /// <param name="result">The candidate manifest returned by the producers.</param>
    /// <param name="output">The proof directory containing their artifacts.</param>
    /// <param name="verifyLocalSourceAsync">Checks the pinned local source and policy.</param>
    /// <param name="localRun">The run whose cleanup must be settled before publishing a claim.</param>
    /// <param name="cancellationToken">Cancels verification and promotion before the atomic replacement.</param>
    internal static async Task FinalizeLocalManifestAsync(EvidencePlan plan, EvidenceManifest result, string output,
        Func<CancellationToken, Task> verifyLocalSourceAsync, LocalEvidenceRun localRun, CancellationToken cancellationToken)
    {
        var manifestPath = PathUtils.PathUnder(output, RepositoryEvidenceLayout.ManifestFile);
        var summaryPath = PathUtils.PathUnder(output, RepositoryEvidenceLayout.SummaryFile);
        var observation = result.Eligibility == EvidenceClaimEligibility.PullRequestGate
            ? EvidenceManifestBuilder.Build(plan, result.ProducerResults, observationOnly: true,
                envelopeStatus: result.EnvelopeStatus, resourceResults: result.ResourceResults, metrics: result.Metrics)
            : result;
        await File.WriteAllBytesAsync(manifestPath, EvidenceCanonicalJson.Serialize(observation), CancellationToken.None);
        await AppendLocalSummaryAsync(summaryPath,
            $"\nProvisional claim: `{observation.ClaimKind}`; cleanup: `{observation.Metrics.CleanupCompleted}`.\n");

        ValidationProof.Require(!localRun.CleanupUnconfirmed, "confirmed local evidence cleanup");
        await VerifyAsync(plan, result, PathUtils.PathUnder(output, RepositoryEvidenceLayout.ArtifactsDirectory), cancellationToken);
        try
        {
            await verifyLocalSourceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await AppendLocalSummaryAsync(summaryPath,
                "\nSource verification failed during execution. Claim downgraded to ObservationOnly.\n");
            throw;
        }

        await PromoteLocalManifestAsync(output, manifestPath, result, cancellationToken);
        await AppendLocalSummaryAsync(summaryPath, ClaimSummary(result));
    }

    private static async Task PromoteLocalManifestAsync(string output, string manifestPath, EvidenceManifest result,
        CancellationToken cancellationToken)
    {
        var temporary = PathUtils.PathUnder(output, $".manifest-{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        try
        {
            await using (stream)
                await stream.WriteAsync(EvidenceCanonicalJson.Serialize(result), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, manifestPath, overwrite: true);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Could not remove the staged local evidence manifest: " + cleanupFailure.Message);
            }
            throw;
        }
    }

    private static string ClaimSummary(EvidenceManifest manifest) =>
        $"\nClaim: `{manifest.ClaimKind}`; cleanup: `{manifest.Metrics.CleanupCompleted}`.\n";

    private static async Task AppendLocalSummaryAsync(string path, string status)
    {
        try { await File.AppendAllTextAsync(path, status, CancellationToken.None); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not update the local evidence summary: " + failure.Message);
        }
    }

    /// <summary>Returns the closed declaration implemented by each repository producer; unknown IDs fail closed.</summary>
    internal static EvidenceProducerDeclaration Declaration(string id) => id switch
    {
        "packed-storage" => Program.Declaration(id, 600,
            ["storage/published-cli@1", "storage/byte-preservation@1", "storage/explicit-retirement@1"], transcript: true),
        "installed-consumer" => Program.Declaration(id, 1800,
            ["consumer/package-identity@1", "consumer/executed-tests@1", "consumer/coverage-gate@1", "consumer/source-integrity@1"]),
        "solution-coverage" => new(id, "repository-validation", "1.0.0", [],
            ["coverage/solution-complete@1", "coverage/thresholds@1"],
            [new("result", "proof", "application/json", true, 1024 * 1024),
                new("transcript", "proof", "application/json", true, 16 * 1024 * 1024),
                new("aggregate-gate", "proof", "application/json", true, 2 * 1024 * 1024),
                new("patch-gate", "proof", "application/json", false, 2 * 1024 * 1024)], 5400,
            new EvidenceCoverageGateRequirements(95, 85, 95, 85, "codecov", 0.5m)),
        "package-checks" => new(id, "repository-validation", "1.0.0", [],
            ["package/manifest@1", "package/generated-docs@1", "package/archives-and-consumers@1"],
            [new("result", "proof", "application/json", true, 1024 * 1024),
                new("transcript", "proof", "application/json", true, 16 * 1024 * 1024),
                new("package-manifest", "proof", "application/json", true, 2 * 1024 * 1024),
                new("package-report", "proof", "text/markdown", true, 2 * 1024 * 1024)], 3600),
        _ => throw new ArgumentException("Unsupported repository evidence producer."),
    };

    /// <summary>Rejects edited claims, missing obligations, unsuccessful cleanup, and missing or altered retained artifacts.</summary>
    internal static async Task VerifyAsync(EvidencePlan plan, EvidenceManifest manifest, string artifactRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateProfile(plan.Profile);
        ValidationProof.Require(plan.PolicySnapshot is not null
            && new EvidencePlanner().Resolve(plan.PolicySnapshot, plan.ChangedPaths).PlanDigest == plan.PlanDigest,
            "plan resolves from its policy snapshot");
        ValidationProof.Require(EvidenceManifestBuilder.Verify(plan, manifest), "canonical plan/manifest binding");
        ValidationProof.Require(manifest.ExecutionVerdict == EvidenceExecutionVerdict.Passed
            && manifest.Eligibility == EvidenceClaimEligibility.PullRequestGate
            && manifest.ClaimKind is EvidenceClaimKind.TargetedComplete or EvidenceClaimKind.NoEvidenceRequired
            && manifest.Metrics.CleanupCompleted, "gate-eligible complete evidence");
        foreach (var producer in manifest.ProducerResults)
        {
            foreach (var artifact in producer.Artifacts ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = await EvidenceArtifactFileValidation.CheckAsync(artifactRoot, producer.ProducerId, artifact, cancellationToken);
                ValidationProof.Require(status != EvidenceArtifactFileStatus.InvalidPath, "normalized retained artifact path");
                ValidationProof.Require(status != EvidenceArtifactFileStatus.Linked, "unlinked retained artifact path");
                ValidationProof.Require(status != EvidenceArtifactFileStatus.Missing, "retained artifact present");
                ValidationProof.Require(status != EvidenceArtifactFileStatus.LengthMismatch, "retained artifact length");
                ValidationProof.Require(status == EvidenceArtifactFileStatus.Valid, "retained artifact digest");
            }
        }
    }

    private static void ValidateProfile(EvidenceProfile profile)
    {
        ValidationProof.Require(profile.Scope == EvidenceProfileScope.Targeted && profile.Resources.Count == 0, "native targeted profile without resources");
        foreach (var producer in profile.Producers)
        {
            ValidationProof.Require(EvidenceDigest.CanonicalSha256(producer) == EvidenceDigest.CanonicalSha256(Declaration(producer.Id)),
                "repository producer contract matches implementation");
            foreach (var assertion in producer.AssertionIds)
                ValidationProof.Require(profile.Obligations.Any(obligation => obligation.RequiredAssertionId == assertion
                    && obligation.RequiredProducerIds.Contains(producer.Id, StringComparer.Ordinal)), "every native assertion is required");
        }
    }

    private static T Read<T>(string path, CancellationToken cancellationToken)
    {
        using var json = ValidationProof.ReadJsonFile(path, MaximumJsonBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Reuse the bounded snapshot rather than reopening a caller-controlled input.
        return EvidenceCanonicalJson.Deserialize<T>(Encoding.UTF8.GetBytes(json.RootElement.GetRawText()));
    }

    /// <summary>Whether the selected profile needs the separately packed CLI used by native acceptance journeys.</summary>
    internal static bool RequiresPackage(EvidenceProfile profile) =>
        profile.Producers.Any(producer => producer.Id is "packed-storage" or "installed-consumer");

    /// <summary>
    /// Identifies an existing local tool manifest update with optional new, direct Markdown release entries.
    /// Only the native coverage supplement is omitted; all selected producers and direct coverage gates remain required.
    /// Renames, deletions and any other changed path retain native coverage, including either name of a rename.
    /// </summary>
    private static bool IsToolManifestOnlyChange(IReadOnlyList<NormalizedDiffPath> paths)
    {
        const string manifest = ".config/dotnet-tools.json";
        const string entries = "releases/unreleased.entries/";
        return paths.Any(path => path.Path == manifest && path.Kind == "modified" && path.PreviousPath is null)
            && paths.All(path => path.PreviousPath is null
                && ((path.Path == manifest && path.Kind == "modified")
                    || (path.Kind == "added" && path.Path.StartsWith(entries, StringComparison.Ordinal)
                        && path.Path.EndsWith(".md", StringComparison.Ordinal)
                        && !path.Path[entries.Length..].Contains('/'))));
    }

    /// <summary>Projects a validated shared policy into one execution lane without reclassifying changed paths.</summary>
    internal static EvidencePlan ResolveScope(EvidencePolicy policy, IReadOnlyList<NormalizedDiffPath> paths, string scope)
    {
        if (scope == "all") return new EvidencePlanner().Resolve(policy, paths);
        if (scope is not ("native" or "coverage" or "packages")) throw new ArgumentException("Scope must be all, native, coverage or packages.");
        bool Includes(string id) => scope switch
        {
            "native" => id is "packed-storage" or "installed-consumer",
            "coverage" => id == "solution-coverage",
            _ => id == "package-checks",
        };
        // Resolve the shared decision first, then represent its projection as a deterministic
        // one-profile policy so the normal planner and manifest verifier can rederive it later.
        var root = new EvidencePlanner().Resolve(policy, paths);
        var selected = Project(root.Profile, Includes);
        var fallback = Project(policy.Profiles.Single(profile => profile.Id == policy.ConservativeProfileId), Includes);
        var profiles = selected.Id == fallback.Id ? new[] { fallback } : new[] { selected, fallback };
        // Every path has already participated in the shared decision. Bind the actual inventory
        // through the plan's diff digest without interpreting file names as bounded rule patterns.
        EvidencePolicyRule[] rules = [new("selected", "**", selected.Id)];
        var derived = new EvidencePolicy("lane-" + EvidenceDigest.CanonicalSha256(policy)[..32] + "-" + scope,
            policy.Version, fallback.Id, profiles, rules);
        return new EvidencePlanner().Resolve(derived, paths);
    }

    private static EvidenceProfile Project(EvidenceProfile profile, Func<string, bool> includes)
    {
        var producers = profile.Producers.Where(producer => includes(producer.Id)).ToArray();
        var ids = producers.Select(producer => producer.Id).ToHashSet(StringComparer.Ordinal);
        return profile with
        {
            Producers = producers,
            Obligations = profile.Obligations.Where(obligation => obligation.RequiredProducerIds.All(ids.Contains)).ToArray(),
        };
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Missing required validation option: " + key);

    private static void ValidateOptions(IReadOnlyDictionary<string, string> options, string operation)
    {
        string[] common = ["--operation", "--policy", "--changed-paths", "--scope"];
        string[] selected = operation switch
        {
            "plan" => ["--output"],
            "run" => ["--plan", "--output", "--package", "--version", "--source-commit", "--dotnet", "--scratch-parent", "--repository", "--diff-base", "--diff-file", "--native-coverage-directory", "--native-coverage-contract-version", "--coverage-cli", "--coverage-collection", "--coverage-schedule-timings"],
            _ => ["--plan", "--manifest", "--artifact-root"],
        };
        if (options.Keys.Except(common.Concat(selected), StringComparer.Ordinal).Any())
            throw new ArgumentException("Option is not supported by this repository evidence operation.");
    }

    private static string Summary(EvidencePlan plan) =>
        $"## Repository evidence plan\n\nProfile: `{plan.Profile.Id}`\n\nRules: {string.Join(", ", plan.MatchedRuleIds.Select(id => $"`{id}`"))}\n\n"
        + "Required producers: " + (plan.Profile.Producers.Count == 0 ? "none (explicit policy selection)" : string.Join(", ", plan.Profile.Producers.Select(producer => $"`{producer.Id}`")))
        + "\n\nRequired obligations:\n\n" + string.Join("\n", plan.Profile.Obligations.Select(obligation => $"- `{obligation.Id}`: {obligation.Rationale}"))
        + $"\n\nPlan digest: `{plan.PlanDigest}`\n";
}
