using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>Runs one explicit repository planning, execution, or gate operation; incomplete evidence returns nonzero.</summary>
    /// <param name="options">Validated command options and trusted plan inputs.</param>
    /// <param name="cancellationToken">Cancels execution and artifact verification.</param>
    /// <param name="verifyLocalSourceAsync">Optional in-process local source and policy digest check; required for patch overrides.</param>
    internal static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? verifyLocalSourceAsync = null)
    {
        var operation = Required(options, "--operation");
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
            return 0;
        }

        var output = Path.GetFullPath(Required(options, "--output"));
        if (Directory.Exists(output) || File.Exists(output)) throw new ArgumentException("Evidence output must be fresh.");
        ValidationSettings? settings = null;
        RepositoryCheckSettings? checks = null;
        if (operation == "run" && plan.Profile.Producers.Count > 0)
        {
            var dotnet = Path.GetFullPath(Required(options, "--dotnet"));
            if (!File.Exists(dotnet)) throw new ArgumentException("The selected dotnet host does not exist.");
            var repository = Path.GetFullPath(options.GetValueOrDefault("--repository", Directory.GetCurrentDirectory()));
            if (RequiresPackage(plan.Profile))
                settings = new ValidationSettings(repository, Path.GetFullPath(Required(options, "--package")), Required(options, "--version"),
                    Required(options, "--source-commit"), dotnet,
                    Path.GetFullPath(options.GetValueOrDefault("--scratch-parent", Path.GetTempPath())), output);
            checks = new RepositoryCheckSettings(repository, dotnet, output,
                options.GetValueOrDefault("--diff-base"), options.GetValueOrDefault("--native-coverage-directory"), options.GetValueOrDefault("--diff-file"))
            {
                VerifyLocalSourceAsync = verifyLocalSourceAsync,
                CoverageCollectionDirectory = options.GetValueOrDefault("--coverage-collection"),
                CoverageScheduleTimingsPath = options.TryGetValue("--coverage-schedule-timings", out var timings) ? Path.GetFullPath(timings) : null,
            };
        }

        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Join(output, "evidence-plan.json"), EvidenceCanonicalJson.Serialize(plan), cancellationToken);
        await File.WriteAllTextAsync(Path.Join(output, "selection.json"), JsonSerializer.Serialize(new
        {
            profileId = plan.Profile.Id,
            requiresPackage = RequiresPackage(plan.Profile),
            hasProducers = plan.Profile.Producers.Count > 0,
            requiresCoverage = plan.Profile.Producers.Any(producer => producer.Id == "solution-coverage"),
            requiresNativeCoverage = rootPlan.Profile.Producers.Any(producer => producer.Id == "solution-coverage")
                && rootPlan.Profile.Producers.Any(producer => producer.Id != "solution-coverage"),
            requiresPackageChecks = plan.Profile.Producers.Any(producer => producer.Id == "package-checks"),
            producerIds = plan.Profile.Producers.Select(producer => producer.Id).ToArray(),
        }, Program.JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(Path.Join(output, "evidence-summary.md"), Summary(plan), cancellationToken);
        if (operation == "plan") return 0;

        await using var host = EvidenceHostBootstrap.Create(plan, registration =>
        {
            foreach (var declaration in plan.Profile.Producers)
            {
                IEvidenceProducer producer = declaration.Id switch
                {
                    "packed-storage" => new PackedStorageProducer(settings!),
                    "installed-consumer" => new InstalledConsumerProducer(settings!),
                    "solution-coverage" => new SolutionCoverageProducer(checks!),
                    "package-checks" => new PackageChecksProducer(checks!),
                    _ => throw new InvalidOperationException("Unsupported repository evidence producer."),
                };
                registration.AddProducer(producer);
            }
        }, new EvidenceHostOptions(ArtifactDirectory: Path.Join(output, "artifacts")));
        var result = await host.RunAsync(cancellationToken: cancellationToken);
        await File.WriteAllBytesAsync(Path.Join(output, "evidence-manifest.json"), EvidenceCanonicalJson.Serialize(result), CancellationToken.None);
        await File.AppendAllTextAsync(Path.Join(output, "evidence-summary.md"), $"\nClaim: `{result.ClaimKind}`; cleanup: `{result.Metrics.CleanupCompleted}`.\n", CancellationToken.None);
        foreach (var producer in result.ProducerResults) Console.WriteLine($"{producer.ProducerId}: {producer.Outcome} {producer.Diagnostic}");
        await VerifyAsync(plan, result, Path.Join(output, "artifacts"), cancellationToken);
        return 0;
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
                var relative = EvidenceArtifactValidation.NormalizeRelativePath(artifact.RelativePath);
                var root = Path.GetFullPath(artifactRoot);
                var components = new[] { producer.ProducerId }.Concat(relative.Split('/'));
                var path = root;
                RejectLink(path);
                foreach (var component in components)
                {
                    path = Path.Join(path, component);
                    RejectLink(path);
                }
                await using var stream = File.OpenRead(path);
                ValidationProof.Require(stream.Length == artifact.LengthBytes, "retained artifact length");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long length = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    length += read;
                    ValidationProof.Require(length <= artifact.LengthBytes, "bounded retained artifact");
                    hash.AppendData(buffer, 0, read);
                }
                ValidationProof.Require(length == artifact.LengthBytes
                    && string.Equals(Convert.ToHexString(hash.GetHashAndReset()), artifact.Sha256, StringComparison.OrdinalIgnoreCase),
                    "retained artifact digest");
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

    private static void RejectLink(string path) => ValidationProof.Require(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint), "unlinked retained artifact path");

    /// <summary>Whether the selected profile needs the separately packed CLI used by native acceptance journeys.</summary>
    internal static bool RequiresPackage(EvidenceProfile profile) =>
        profile.Producers.Any(producer => producer.Id is "packed-storage" or "installed-consumer");

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
            "run" => ["--plan", "--output", "--package", "--version", "--source-commit", "--dotnet", "--scratch-parent", "--repository", "--diff-base", "--diff-file", "--native-coverage-directory", "--coverage-collection", "--coverage-schedule-timings"],
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
