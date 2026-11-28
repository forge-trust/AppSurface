using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Validation;

/// <summary>
/// Runs the repository policy against a local branch and working tree, packing a candidate only after
/// planning selects native producers. Uses the same host and manifest verifier as CI on the current OS.
/// </summary>
/// <remarks>
/// No results are reused. The merge-base diff, index, working tree and untracked files are collected by
/// <see cref="RepositoryGitChanges"/>. Source drift prevents gate success. Output belongs outside the
/// source inventory. The default local output retains a compact result under ignored TestResults.
/// </remarks>
internal sealed class LocalEvidenceWorkflow
{
    private readonly Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> _command;
    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, RepositoryEvidenceWorkflow.LocalExecutionContext, Task<EvidenceManifest>> _workflow;

    /// <summary>
    /// Creates local orchestration. Tests may replace the package command or the execution operation while planning
    /// and final manifest and artifact verification continue through the repository workflow.
    /// </summary>
    /// <param name="command">Optional bounded seam for creating the candidate package.</param>
    /// <param name="workflow">Optional replacement for repository execution. It receives the same local services as the default executor.</param>
    internal LocalEvidenceWorkflow(
        Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>>? command = null,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, RepositoryEvidenceWorkflow.LocalExecutionContext, Task<EvidenceManifest>>? workflow = null)
    {
        var runner = new EvidenceCommandRunner();
        _command = command ?? runner.RunAsync;
        _workflow = workflow ?? RepositoryEvidenceWorkflow.RunLocalAsync;
    }

    /// <summary>Plans or executes current local changes and returns zero only for successful verification or an empty inventory.</summary>
    internal async Task<int> RunAsync(IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        string[] allowed = ["--operation", "--repository", "--base", "--policy", "--output", "--dotnet", "--scope"];
        if (options.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new ArgumentException("Unsupported local evidence option.");
        var operation = options.GetValueOrDefault("--operation");
        if (operation is not ("local" or "local-plan")) throw new ArgumentException("Expected local or local-plan.");
        var repository = Path.GetFullPath(options.GetValueOrDefault("--repository", Directory.GetCurrentDirectory()));
        cancellationToken.ThrowIfCancellationRequested();
        var explicitOutput = options.TryGetValue("--output", out var output) ? Path.GetFullPath(output) : null;
        if (operation == "local-plan" && explicitOutput is not null)
            return await RunFreshAsync(options, repository, operation, explicitOutput, new(null), cancellationToken);

        if (!Path.Exists(PathUtils.PathUnder(repository, ".git")))
            throw new ArgumentException("Local evidence requires a Git worktree root.");
        var gitDirectory = await RepositoryGitChanges.ReadIdentityAsync(repository, cancellationToken, "rev-parse", "--absolute-git-dir");
        if (!Path.IsPathFullyQualified(gitDirectory))
            throw new ArgumentException("Local evidence requires an absolute worktree Git directory.");
        return await LocalEvidenceOutput.RunAsync(repository, operation, gitDirectory,
            (scratch, managedRun) => RunFreshAsync(options, repository, operation, scratch, managedRun, cancellationToken), explicitOutput);
    }

    private async Task<int> RunFreshAsync(IReadOnlyDictionary<string, string> options, string repository, string operation,
        string output, LocalEvidenceOutput.ManagedRun managedRun, CancellationToken cancellationToken)
    {
        var policy = Path.GetFullPath(options.GetValueOrDefault("--policy", PathUtils.PathUnder(repository, ".github", "evidence.policy.json")));
        var baseRevision = options.GetValueOrDefault("--base", "origin/main");
        if (Path.Exists(output)) throw new ArgumentException("Evidence output must be fresh.");
        var git = new RepositoryGitChanges();
        var changes = await git.CollectLocalAsync(repository, baseRevision, cancellationToken);
        var policySnapshot = ValidationProof.ReadBoundedFile(policy, EvidenceCanonicalJson.MaximumInputBytes, cancellationToken);
        var policyDigest = EvidenceDigest.Sha256(policySnapshot.Span);
        Directory.CreateDirectory(output);
        if (options.ContainsKey("--output"))
            Console.WriteLine("Local evidence output: " + output);
        var paths = PathUtils.PathUnder(output, "changed-paths.json");
        var pathsBytes = EvidenceCanonicalJson.Serialize(changes.Paths);
        await File.WriteAllBytesAsync(paths, pathsBytes, cancellationToken);
        await File.WriteAllTextAsync(PathUtils.PathUnder(output, "source-state.json"), JsonSerializer.Serialize(changes, Program.JsonOptions), cancellationToken);
        if (changes.Paths.Count == 0)
        {
            Console.WriteLine("No local changes relative to the selected base; no evidence collected.");
            await File.WriteAllTextAsync(PathUtils.PathUnder(output, RepositoryEvidenceOutputLayout.SummaryFileName),
                "No changed paths. No evidence claim was produced.\n", cancellationToken);
            managedRun.SkipArchive = true;
            return 0;
        }

        // Preserve checked-in rules/contracts while binding dirty local content into the canonical
        // plan and manifest. HEAD remains package metadata, not the complete local identity.
        var localPolicy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(policySnapshot.Span);
        localPolicy = localPolicy with { Id = localPolicy.Id + "-local-" + changes.SourceDigest };
        var resolvedPolicy = PathUtils.PathUnder(output, "local-policy.json");
        var policyBytes = EvidenceCanonicalJson.Serialize(localPolicy);
        await File.WriteAllBytesAsync(resolvedPolicy, policyBytes, cancellationToken);
        var replayInputs = new Dictionary<string, LocalEvidenceOutput.ArchiveEntry>(StringComparer.Ordinal)
        {
            ["changed-paths.json"] = LocalEvidenceOutput.ArchiveEntry.Snapshot(pathsBytes),
            ["local-policy.json"] = LocalEvidenceOutput.ArchiveEntry.Snapshot(policyBytes),
        };

        var planDirectory = PathUtils.PathUnder(output, RepositoryEvidenceOutputLayout.PlanDirectoryName);
        var common = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--policy"] = resolvedPolicy,
            ["--changed-paths"] = paths,
            ["--operation"] = "plan",
            ["--output"] = planDirectory,
            ["--scope"] = options.GetValueOrDefault("--scope", "all"),
        };
        await RepositoryEvidenceWorkflow.RunAsync(common, cancellationToken);
        var planPath = PathUtils.PathUnder(planDirectory, RepositoryEvidenceOutputLayout.PlanFileName);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(planPath, cancellationToken));
        if (operation == "local-plan")
        {
            managedRun.VerifyPublication(plan, replayInputs, null);
            return 0;
        }

        // Pin the resolved base, not a moving branch name, when checking for edits during collection.
        var verifySource = managedRun.Settlement.TrackSourceCheck(
            token => RequireUnchangedAsync(git, repository, changes, policy, policyDigest, token));
        var execution = new RepositoryEvidenceWorkflow.LocalExecutionContext(verifySource, managedRun);
        await execution.VerifySourceAsync(cancellationToken);
        var proof = PathUtils.PathUnder(output, RepositoryEvidenceOutputLayout.ProofDirectoryName);
        common["--operation"] = "run";
        common["--output"] = proof;
        common["--plan"] = planPath;
        if (plan.Profile.Producers.Count > 0)
        {
            var dotnet = ResolveDotnet(options.GetValueOrDefault("--dotnet"));
            common["--dotnet"] = dotnet;
            common["--repository"] = repository;
            common["--diff-base"] = changes.BaseCommit;
            if (plan.Profile.Producers.Any(producer => producer.Id == "solution-coverage"))
            {
                var patch = PathUtils.PathUnder(output, "changed.patch");
                await git.WriteLocalPatchAsync(repository, changes.BaseCommit, changes.Paths, patch, cancellationToken);
                common["--diff-file"] = patch;
            }
            if (RepositoryEvidenceWorkflow.RequiresPackage(plan.Profile))
            {
                var version = "0.0.0-local." + Guid.NewGuid().ToString("N");
                var feed = PathUtils.PathUnder(output, "feed");
                var package = PathUtils.PathUnder(feed, "ForgeTrust.AppSurface.Cli." + version + ".nupkg");
                var request = new EvidenceCommandRequest(dotnet,
                    ["pack", PathUtils.PathUnder(repository, "Cli", "ForgeTrust.AppSurface.Cli", "ForgeTrust.AppSurface.Cli.csproj"),
                    "--configuration", "Release", "--output", feed, "/p:RestoreLockedMode=true", "/p:PackageVersion=" + version,
                    "/p:RepositoryCommit=" + changes.HeadCommit, "/p:SourceRevisionId=" + changes.HeadCommit],
                    repository, TimeSpan.FromMinutes(10))
                {
                    MaximumOutputBytes = 4 * 1024 * 1024,
                    Environment = new RepositoryCheckSettings(repository, dotnet, output).CommandEnvironment,
                };
                var result = await managedRun.Settlement.Track(_command)(request, cancellationToken);
                await File.WriteAllTextAsync(PathUtils.PathUnder(output, "package-build.log"), result.StandardOutput + "\n" + result.StandardError, cancellationToken);
                ValidationProof.Require(result.ExitCode == 0 && !result.OutputTruncated && File.Exists(package), "local candidate package build");
                await execution.VerifySourceAsync(cancellationToken);
                common["--package"] = package;
                common["--version"] = version;
                common["--source-commit"] = changes.HeadCommit;
                common["--dotnet"] = dotnet;
                common["--repository"] = repository;
                common["--scratch-parent"] = Path.GetTempPath();
            }
        }
        var manifest = await _workflow(common, cancellationToken, execution);
        await RepositoryEvidenceWorkflow.FinalizeLocalManifestAsync(plan, manifest, proof, execution, cancellationToken);
        managedRun.VerifyPublication(plan, replayInputs, manifest);
        Console.WriteLine($"Local evidence gate passed: {manifest.ClaimKind}. This run covers the current OS; CI requires its native matrix.");
        return 0;
    }

    private static async Task RequireUnchangedAsync(RepositoryGitChanges git, string repository, RepositoryChangeSet original,
        string policy, string policyDigest, CancellationToken cancellationToken)
    {
        var current = await git.CollectLocalAsync(repository, original.BaseCommit, cancellationToken);
        ValidationProof.Require(current.SourceDigest == original.SourceDigest
            && await ValidationProof.HashAsync(policy, cancellationToken) == policyDigest,
            "local source or policy changed during evidence; rerun from the new source state");
    }

    private static string ResolveDotnet(string? selected)
    {
        if (selected is not null)
        {
            if (!Path.IsPathFullyQualified(selected) || !File.Exists(selected)) throw new ArgumentException("Select an absolute existing dotnet host.");
            return selected;
        }
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(host) && Path.IsPathFullyQualified(host) && File.Exists(host)) return host;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Join(directory, executable)).FirstOrDefault(File.Exists)
            ?? throw new ArgumentException("Could not locate dotnet; supply --dotnet with an absolute host path.");
    }
}
