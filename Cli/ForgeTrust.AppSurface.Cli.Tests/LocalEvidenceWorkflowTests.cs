using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Core;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ValidationProgram = ForgeTrust.AppSurface.Evidence.Validation.Program;

namespace ForgeTrust.AppSurface.Cli.Tests;

// The native coverage collector runs this project and must observe the same local workflow paths as the primary run.
[Collection("CoverageGate process state")]
public sealed partial class LocalEvidenceWorkflowTests : IDisposable
{
    private const string PolicyPath = ".github/evidence.policy.json";
    private const string StoragePath = "tools/ForgeTrust.AppSurface.Evidence.Validation/PackedStorageProducer.cs";
    private readonly string _repo = TestPathUtils.FindRepoRoot(AppContext.BaseDirectory);
    private readonly string _root = Directory.CreateTempSubdirectory("local-evidence-workflow-").FullName;
    private string? _lastOutput;

    [Fact]
    public async Task EmptyInventory_WritesSummaryWithoutPlanningOrPacking()
    {
        var fixture = await CreateFixtureAsync();
        var workflow = new LocalEvidenceWorkflow((_, _) => throw new Xunit.Sdk.XunitException("Empty inventory must not pack."));

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local"), CancellationToken.None));
        var output = LastOutput;
        Assert.True(File.Exists(TestPathUtils.PathUnder(output, "changed-paths.json")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output, "plan", "evidence-plan.json")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output, "proof", "evidence-manifest.json")));
        Assert.Contains("No changed paths", await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, "evidence-summary.md")));
    }

    [Fact]
    public async Task DocsOnly_LocalPlanAndLocalCompleteWithoutPackage_UseFreshDefaultOutput()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "local docs change");
        var workflow = new LocalEvidenceWorkflow((_, _) => throw new Xunit.Sdk.XunitException("Docs do not select a producer."));

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local-plan"), CancellationToken.None));
        var planOutput = LastOutput;
        Assert.Equal("no-evidence", ReadPlan(planOutput).Profile.Id);
        Assert.False(File.Exists(TestPathUtils.PathUnder(planOutput, "proof", "evidence-manifest.json")));

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local"), CancellationToken.None));
        var runOutput = LastOutput;
        Assert.NotEqual(planOutput, runOutput);
        var manifest = ReadManifest(runOutput);
        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, manifest.ClaimKind);
        Assert.Empty(manifest.ProducerResults);
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(runOutput, "source-state.json")));
        var digest = source.RootElement.GetProperty("sourceDigest").GetString();
        Assert.EndsWith("-local-" + digest, ReadPlan(runOutput).PolicyId);
        Assert.Equal(ReadPlan(runOutput).PlanDigest, manifest.PlanDigest);
    }

    [Fact]
    public async Task SourceChange_DefaultLocalPlanSelectsCoverageAndPackageChecksWithoutPacking()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var calls = 0;
        var workflow = new LocalEvidenceWorkflow((_, _) =>
        {
            calls++;
            throw new Xunit.Sdk.XunitException("Planning must not invoke package command.");
        });

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local-plan"), CancellationToken.None));
        var plan = ReadPlan(LastOutput);
        Assert.Equal(["package-checks", "solution-coverage"], plan.Profile.Producers.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CoveragePlan_UsesRunSeamAndStillPassesRealManifestAndArtifactVerification()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var runCalls = 0;
        var workflow = new LocalEvidenceWorkflow(workflow: async (options, cancellationToken) =>
        {
            runCalls++;
            Assert.Equal("run", options["--operation"]);
            return await BuildCompleteProofAsync(options, cancellationToken);
        });

        Assert.Equal(0, await workflow.RunAsync(Options(fixture, "local", "coverage"), CancellationToken.None));

        Assert.Equal(1, runCalls);
        var plan = ReadPlan(LastOutput);
        Assert.Equal("solution-coverage", Assert.Single(plan.Profile.Producers).Id);
        var manifest = ReadManifest(LastOutput);
        Assert.Equal(EvidenceClaimKind.TargetedComplete, manifest.ClaimKind);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.True(File.Exists(TestPathUtils.PathUnder(LastOutput, "proof", "artifacts", "solution-coverage", "proof", "result.json")));
    }

    [Fact]
    public async Task SourceDriftAfterCompleteRun_PersistsValidObservationOnlyManifest()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var workflow = new LocalEvidenceWorkflow(workflow: async (options, cancellationToken) =>
        {
            var candidate = await BuildCompleteProofAsync(options, cancellationToken);
            await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { int Changed; }");
            return candidate;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunAsync(Options(fixture, "local", "coverage"), CancellationToken.None));

        var plan = ReadPlan(LastOutput);
        var manifest = ReadManifest(LastOutput);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        Assert.Contains("Source verification failed during execution. Claim downgraded to ObservationOnly.",
            await File.ReadAllTextAsync(TestPathUtils.PathUnder(LastOutput, "proof", "evidence-summary.md")));
    }

    [Theory]
    [InlineData(false, false, typeof(InvalidOperationException))]
    [InlineData(true, false, typeof(FileNotFoundException))]
    [InlineData(true, true, typeof(FileNotFoundException))]
    public async Task PolicyVerificationAfterCompleteRun_RetainsObservationAndOriginalError(
        bool removePolicy, bool summaryUnavailable, Type expectedFailure)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        // Keep the override outside the checkout so only the policy check sees the change.
        var policy = TestPathUtils.PathUnder(_root, "override-policy.json");
        File.Copy(TestPathUtils.PathUnder(fixture.Root, ".github", "evidence.policy.json"), policy);
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, cancellationToken) =>
        {
            var candidate = await BuildCompleteProofAsync(runOptions, cancellationToken);
            if (summaryUnavailable)
                Directory.CreateDirectory(TestPathUtils.PathUnder(runOptions["--output"], RepositoryEvidenceLayout.SummaryFile));
            if (removePolicy)
                File.Delete(policy);
            else
                await File.AppendAllTextAsync(policy, "\n", cancellationToken);
            return candidate;
        });
        var options = Options(fixture, "local", "coverage");
        options["--policy"] = policy;
        options.Remove("--output");

        var failure = await Assert.ThrowsAsync(expectedFailure, () => workflow.RunAsync(options, CancellationToken.None));

        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        const string archive = "latest-failure.zip";
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(
            Encoding.UTF8.GetBytes(ReadArchive(retained, archive, "plan/evidence-plan.json")!));
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(
            Encoding.UTF8.GetBytes(ReadArchive(retained, archive, "proof/evidence-manifest.json")!));
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        var summary = ReadArchive(retained, archive, "proof/evidence-summary.md");
        if (summaryUnavailable)
            Assert.Null(summary);
        else
            Assert.Contains("Source verification failed during execution. Claim downgraded to ObservationOnly.", summary);
        Assert.Contains(failure.Message, ReadArchive(retained, archive, "run-status.txt"));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("missing")]
    public async Task InvalidSelectedDotnetHost_IsRejectedBeforeProducerExecution(string selection)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options["--dotnet"] = selection == "relative" ? "dotnet" : TestPathUtils.PathUnder(_root, "missing-dotnet");
        var workflow = new LocalEvidenceWorkflow(workflow: (_, _) => throw new Xunit.Sdk.XunitException("Invalid host must not execute producers."));

        await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(options, CancellationToken.None));

        Assert.False(File.Exists(TestPathUtils.PathUnder(LastOutput, "proof", "evidence-manifest.json")));
    }

    [Theory]
    [InlineData("selected")]
    [InlineData("environment")]
    [InlineData("path")]
    public async Task ValidDotnetHostSelection_IsPassedToTheProducer(string selection)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var host = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Join(directory, executable)).First(File.Exists);
        host = Path.GetFullPath(host);
        if (selection == "selected") options["--dotnet"] = host;
        var previous = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token) =>
        {
            Assert.Equal(host, runOptions["--dotnet"]);
            return await BuildCompleteProofAsync(runOptions, token);
        });

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", selection == "environment" ? host : "missing-relative-host");
            Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", previous);
        }
    }

    [Fact]
    public async Task FailedRunWorkflow_DoesNotAcceptOrReadAProofManifest()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/new-feature.cs", "class Feature { }");
        var workflow = new LocalEvidenceWorkflow(workflow: (_, _) =>
            throw new InvalidOperationException("Simulated producer failure."));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.RunAsync(Options(fixture, "local", "coverage"), CancellationToken.None));

        Assert.False(File.Exists(TestPathUtils.PathUnder(LastOutput, "proof", "evidence-manifest.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterCompleteCandidate_PersistsObservationAndNeverReportsSuccess(bool managedOutput)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        if (managedOutput)
            options.Remove("--output");

        using var cancellation = new CancellationTokenSource();
        CancellationToken producerToken = default;
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token) =>
        {
            producerToken = token;
            var candidate = await BuildCompleteProofAsync(runOptions, token);
            cancellation.Cancel();
            return candidate;
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => workflow.RunAsync(options, cancellation.Token));
        Assert.Equal(cancellation.Token, producerToken);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(File.Exists(PendingCleanupMarker(TestPathUtils.PathUnder(fixture.Root, ".git"))));

        var proofRoot = LastOutput;
        if (managedOutput)
        {
            var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
            var cancelledArchive = TestPathUtils.PathUnder(retained, "latest-cancelled.zip");
            Assert.True(File.Exists(cancelledArchive));
            Assert.False(File.Exists(TestPathUtils.PathUnder(retained, "latest.zip")));
            proofRoot = TestPathUtils.PathUnder(_root, "cancelled-output");
            ZipFile.ExtractToDirectory(cancelledArchive, proofRoot);
        }

        var plan = ReadPlan(proofRoot);
        var manifest = ReadManifest(proofRoot);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.Informational, manifest.Eligibility);
        Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryEvidenceWorkflow.VerifyAsync(
            plan, manifest, TestPathUtils.PathUnder(proofRoot, "proof", "artifacts"), CancellationToken.None));
    }

    [Fact]
    public async Task SameChangedPathsWithNewContent_ProduceADifferentBoundLocalPlan()
    {
        var fixture = await CreateFixtureAsync();
        var workflow = new LocalEvidenceWorkflow((_, _) => throw new Xunit.Sdk.XunitException("Planning must not pack."));
        await ChangeAsync(fixture, "docs/guide.md", "first version");
        await workflow.RunAsync(Options(fixture, "local-plan"), CancellationToken.None);
        var first = ReadPlan(LastOutput);
        await ChangeAsync(fixture, "docs/guide.md", "second version");
        await workflow.RunAsync(Options(fixture, "local-plan"), CancellationToken.None);
        var second = ReadPlan(LastOutput);
        Assert.Equal(first.DiffDigest, second.DiffDigest);
        Assert.NotEqual(first.PlanDigest, second.PlanDigest);
    }

    [Fact]
    public async Task CiCollectionEntryPoint_WritesInputsForTheSamePolicyPlanner()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "committed docs");
        await GitAsync(fixture.Root, "add", ".");
        await GitAsync(fixture.Root, "commit", "-qm", "docs change");
        var head = (await GitAsync(fixture.Root, "rev-parse", "HEAD")).Trim();
        var eventFile = TestPathUtils.PathUnder(_root, "event.json");
        await File.WriteAllTextAsync(eventFile, JsonSerializer.Serialize(new { before = fixture.Head, after = head }));
        var collection = TestPathUtils.PathUnder(_root, "collected");
        Assert.Equal(0, await ValidationProgram.RunAsync(["--operation", "collect", "--repository", fixture.Root,
            "--event-file", eventFile, "--event-name", "push", "--workflow-sha", head, "--output", collection]));
        var paths = EvidenceCanonicalJson.Deserialize<NormalizedDiffPath[]>(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(collection, "changed-paths.json")));
        Assert.Equal("docs/guide.md", Assert.Single(paths).Path);
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(collection, "source-state.json")));
        Assert.Equal(head, state.RootElement.GetProperty("headCommit").GetString());
        var planned = TestPathUtils.PathUnder(_root, "ci-plan");
        Assert.Equal(0, await ValidationProgram.RunAsync(["--operation", "plan", "--policy", TestPathUtils.PathUnder(fixture.Root, PolicyPath),
            "--changed-paths", TestPathUtils.PathUnder(collection, "changed-paths.json"), "--output", planned]));
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(planned, "evidence-plan.json")));
        Assert.Equal("no-evidence", plan.Profile.Id);
    }

    [Fact]
    public async Task StorageChange_PacksCandidateAndPersistsOnlySelectedProducerObservation()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, StoragePath, "// changed storage producer");
        EvidenceCommandRequest? request = null;
        var workflow = new LocalEvidenceWorkflow((command, _) =>
        {
            request = command;
            var outputIndex = command.Arguments.ToList().IndexOf("--output");
            Directory.CreateDirectory(command.Arguments[outputIndex + 1]);
            var version = command.Arguments.Single(argument => argument.StartsWith("/p:PackageVersion=", StringComparison.Ordinal))["/p:PackageVersion=".Length..];
            File.WriteAllText(TestPathUtils.PathUnder(command.Arguments[outputIndex + 1], $"ForgeTrust.AppSurface.Cli.{version}.nupkg"), "deliberately invalid package");
            return Task.FromResult(new EvidenceCommandResult(0, "pack output", "pack warning", 1));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(Options(fixture, "local", "native"), CancellationToken.None));

        Assert.NotNull(request);
        Assert.Contains("pack", request!.Arguments);
        Assert.Contains("--configuration", request.Arguments);
        var output = LastOutput;
        Assert.Contains("pack warning", await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, "package-build.log")));
        var manifest = ReadManifest(output);
        Assert.Equal(["packed-storage"], manifest.ProducerResults.Select(item => item.ProducerId));
        Assert.NotEqual(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(0, true, true)]
    [InlineData(0, false, false)]
    public async Task FailedTruncatedOrMissingPackage_RetainsPackageLogAndNoManifest(int exitCode, bool truncated, bool createPackage)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, StoragePath, "// changed storage producer");
        var workflow = new LocalEvidenceWorkflow((command, _) =>
        {
            var index = command.Arguments.ToList().IndexOf("--output");
            if (createPackage)
            {
                Directory.CreateDirectory(command.Arguments[index + 1]);
                var version = command.Arguments.Single(argument => argument.StartsWith("/p:PackageVersion=", StringComparison.Ordinal))["/p:PackageVersion=".Length..];
                File.WriteAllText(TestPathUtils.PathUnder(command.Arguments[index + 1], $"ForgeTrust.AppSurface.Cli.{version}.nupkg"), "invalid");
            }
            return Task.FromResult(new EvidenceCommandResult(exitCode, "stdout", "stderr", 1) { OutputTruncated = truncated });
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(Options(fixture, "local", "native"), CancellationToken.None));
        var output = LastOutput;
        Assert.Contains("stdout", await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, "package-build.log")));
        Assert.False(File.Exists(TestPathUtils.PathUnder(output, "proof", "evidence-manifest.json")));
    }

    [Fact]
    public async Task SourceDriftDuringPackageBuild_RejectsBeforeManifestCreation()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, StoragePath, "// initial change");
        var workflow = new LocalEvidenceWorkflow((command, _) =>
        {
            File.WriteAllText(TestPathUtils.PathUnder(fixture.Root, StoragePath), "// changed while collecting");
            var index = command.Arguments.ToList().IndexOf("--output");
            Directory.CreateDirectory(command.Arguments[index + 1]);
            var version = command.Arguments.Single(argument => argument.StartsWith("/p:PackageVersion=", StringComparison.Ordinal))["/p:PackageVersion=".Length..];
            File.WriteAllText(TestPathUtils.PathUnder(command.Arguments[index + 1], $"ForgeTrust.AppSurface.Cli.{version}.nupkg"), "invalid package");
            return Task.FromResult(new EvidenceCommandResult(0, "built", string.Empty, 1));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(Options(fixture, "local", "native"), CancellationToken.None));
        Assert.False(File.Exists(TestPathUtils.PathUnder(LastOutput, "proof", "evidence-manifest.json")));
    }

    [Fact]
    public async Task InvalidOptionsOutputCollisionAndCancellationAreRejected()
    {
        var fixture = await CreateFixtureAsync();
        var workflow = new LocalEvidenceWorkflow();
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(Options(fixture, "unknown"), CancellationToken.None));
        var unsupported = Options(fixture, "local");
        unsupported["--unexpected"] = "x";
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(unsupported, CancellationToken.None));
        var collision = TestPathUtils.PathUnder(_root, "existing");
        Directory.CreateDirectory(collision);
        var collisionOptions = Options(fixture, "local");
        collisionOptions["--output"] = collision;
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(collisionOptions, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.RunAsync(Options(fixture, "local"), cancelled.Token));
    }

    public void Dispose() => NativeEvidenceTestDirectory.Delete(_root);

    [Fact]
    public async Task ManagedOutput_ReplacesSuccessAndPreservesSeparateFailureAndPlan()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options.Remove("--output");
        var run = 0;
        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var scratch = TestPathUtils.PathUnder(retained, ".scratch");
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token) =>
        {
            run++;
            var output = runOptions["--output"];
            Assert.StartsWith("evidence-workspace-", Path.GetRelativePath(scratch, output), StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
            var candidate = await BuildCompleteProofAsync(runOptions, token);
            var coverage = TestPathUtils.PathUnder(output, "coverage");
            Directory.CreateDirectory(coverage);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(coverage, "coverage.cobertura.xml"), $"run {run}", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(coverage, "coverage-run.log"), $"log {run}", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(coverage, "generated.bin"), "scratch", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(coverage, "junit-tests.xml"), "test report", token);
            var packages = TestPathUtils.PathUnder(output, "package-checks", "artifacts");
            var nested = TestPathUtils.PathUnder(packages, "generated");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(output, "package-checks", "verify.log"), "verify log", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(output, "package-checks", "verify-packages.log"), "package verification log", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(output, "package-checks", "generated.md"), "generated scratch", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(packages, "package-validation-report.md"), "package report", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(packages, "package-artifact-manifest.json"), "package manifest", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(packages, "candidate.nupkg"), "generated package", token);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(nested, "generated.md"), "generated input", token);
            return run == 2
                ? EvidenceManifestBuilder.Build(
                    EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(runOptions["--plan"], token)),
                    candidate.ProducerResults.Select(result => result with
                    {
                        Outcome = EvidenceProducerOutcome.Failed,
                        SatisfiedAssertionIds = [],
                    }).ToArray())
                : candidate;
        });

        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        Assert.Equal("run 1", ReadArchive(retained, "latest.zip", "proof/coverage/coverage.cobertura.xml"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(options, CancellationToken.None));
        Assert.Equal("run 1", ReadArchive(retained, "latest.zip", "proof/coverage/coverage.cobertura.xml"));
        Assert.Equal("log 2", ReadArchive(retained, "latest-failure.zip", "proof/coverage/coverage-run.log"));

        options["--operation"] = "local-plan";
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        Assert.NotNull(ReadArchive(retained, "latest-plan.zip", "plan/evidence-plan.json"));
        Assert.NotNull(ReadArchive(retained, "latest-plan.zip", "plan/selection.json"));
        Assert.Equal(2, run);

        options["--operation"] = "local";
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));
        Assert.Equal("run 3", ReadArchive(retained, "latest.zip", "proof/coverage/coverage.cobertura.xml"));
        Assert.NotNull(ReadArchive(retained, "latest.zip", "plan/evidence-summary.md"));
        Assert.Equal("log 2", ReadArchive(retained, "latest-failure.zip", "proof/coverage/coverage-run.log"));
        var extracted = TestPathUtils.PathUnder(_root, "extracted");
        ZipFile.ExtractToDirectory(TestPathUtils.PathUnder(retained, "latest.zip"), extracted);
        Assert.Equal(0, await ValidationProgram.RunAsync([
            "--operation", "verify", "--scope", "coverage",
            "--policy", TestPathUtils.PathUnder(extracted, "local-policy.json"),
            "--changed-paths", TestPathUtils.PathUnder(extracted, "changed-paths.json"),
            "--plan", TestPathUtils.PathUnder(extracted, "plan", "evidence-plan.json"),
            "--manifest", TestPathUtils.PathUnder(extracted, "proof", "evidence-manifest.json"),
            "--artifact-root", TestPathUtils.PathUnder(extracted, "proof", "artifacts")
        ]));
        Assert.Null(ReadArchive(retained, "latest.zip", "proof/coverage/generated.bin"));
        Assert.Equal("test report", ReadArchive(retained, "latest.zip", "proof/coverage/junit-tests.xml"));
        Assert.Equal("package report", ReadArchive(retained, "latest.zip", "proof/package-checks/artifacts/package-validation-report.md"));
        Assert.Equal("package manifest", ReadArchive(retained, "latest.zip", "proof/package-checks/artifacts/package-artifact-manifest.json"));
        Assert.Equal("verify log", ReadArchive(retained, "latest.zip", "proof/package-checks/verify.log"));
        Assert.Equal("package verification log", ReadArchive(retained, "latest.zip", "proof/package-checks/verify-packages.log"));
        Assert.Null(ReadArchive(retained, "latest.zip", "proof/package-checks/generated.md"));
        Assert.Null(ReadArchive(retained, "latest.zip", "proof/package-checks/artifacts/candidate.nupkg"));
        Assert.Null(ReadArchive(retained, "latest.zip", "proof/package-checks/artifacts/generated/generated.md"));
        Assert.Empty(Directory.EnumerateDirectories(scratch, "evidence-workspace-*"));
    }

    [Fact]
    public async Task ManagedPlan_RetainsAdditionalReplayInputsFromRequiredInventory()
    {
        var fixture = await CreateFixtureAsync();
        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(fixture.Root, ".github", "evidence.policy.json")));
        var plan = RepositoryEvidenceWorkflow.ResolveScope(policy, [new("src/feature.cs")], "coverage");
        const string replayInputPath = "future-input/new-replay-input.json";
        var replayInput = Encoding.UTF8.GetBytes("future replay input");
        var result = new LocalEvidenceRunResult(plan, null,
            [LocalEvidenceReplayInput.Snapshot(replayInputPath, replayInput)]);

        Assert.Equal(0, await LocalEvidenceOutput.RunAsync(fixture.Root, "local-plan",
            TestPathUtils.PathUnder(fixture.Root, ".git"), async run =>
            {
                Directory.CreateDirectory(TestPathUtils.PathUnder(run.OutputDirectory, "plan"));
                Directory.CreateDirectory(TestPathUtils.PathUnder(run.OutputDirectory, "future-input"));
                await File.WriteAllBytesAsync(TestPathUtils.PathUnder(run.OutputDirectory, "plan", "evidence-plan.json"),
                    EvidenceCanonicalJson.Serialize(plan));
                await File.WriteAllBytesAsync(TestPathUtils.PathUnder(run.OutputDirectory, replayInputPath.Split('/')), replayInput);
                return result;
            }));

        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        Assert.Equal("future replay input", ReadArchive(retained, "latest-plan.zip", replayInputPath));
    }

    [Fact]
    public async Task ManagedOutput_EmptyInventoryPreservesLatestVerifiedProofAndPlan()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "docs/guide.md", "local docs change");
        var runOptions = Options(fixture, "local");
        runOptions.Remove("--output");
        var planOptions = new Dictionary<string, string>(runOptions) { ["--operation"] = "local-plan" };
        var workflow = new LocalEvidenceWorkflow();
        Assert.Equal(0, await workflow.RunAsync(runOptions, CancellationToken.None));
        Assert.Equal(0, await workflow.RunAsync(planOptions, CancellationToken.None));
        var root = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var latest = TestPathUtils.PathUnder(root, "latest.zip");
        var latestPlan = TestPathUtils.PathUnder(root, "latest-plan.zip");
        Assert.NotNull(ReadArchive(root, "latest.zip", "proof/evidence-manifest.json"));
        var previousProof = await File.ReadAllBytesAsync(latest);
        var previousPlan = await File.ReadAllBytesAsync(latestPlan);

        File.Delete(TestPathUtils.PathUnder(fixture.Root, "docs", "guide.md"));
        Assert.Equal(0, await workflow.RunAsync(runOptions, CancellationToken.None));
        Assert.Equal(0, await workflow.RunAsync(planOptions, CancellationToken.None));

        Assert.Equal(previousProof, await File.ReadAllBytesAsync(latest));
        Assert.Equal(previousPlan, await File.ReadAllBytesAsync(latestPlan));
        Assert.Empty(Directory.EnumerateDirectories(TestPathUtils.PathUnder(root, ".scratch"), "evidence-workspace-*"));
    }

    [Theory]
    [InlineData("preflight", false)]
    [InlineData("unsettled-command", true)]
    [InlineData("missing-manifest", true)]
    [InlineData("invalid-manifest", true)]
    [InlineData("archive-collision", true)]
    [InlineData("cancelled", false)]
    public async Task ManagedOutput_PreservesOriginalFailureAndUnsettledScratch(string phase, bool retainScratch)
    {
        var retained = TestPathUtils.PathUnder(_root, "TestResults", "repository-evidence", "local");
        var preflight = phase == "preflight";
        var archive = phase == "cancelled" ? "latest-cancelled.zip" : "latest-failure.zip";
        var failureArchive = TestPathUtils.PathUnder(retained, "latest-failure.zip");
        if (phase is "preflight" or "cancelled")
        {
            Directory.CreateDirectory(retained);
            await File.WriteAllTextAsync(failureArchive, "earlier diagnostics");
        }
        if (phase == "archive-collision")
            Directory.CreateDirectory(failureArchive);
        Exception failure = phase switch
        {
            "unsettled-command" => new EvidenceCommandException("cleanup_failed", "command still owns scratch"),
            "cancelled" => new OperationCanceledException(),
            _ => new InvalidOperationException("original failure"),
        };
        var scratch = TestPathUtils.PathUnder(retained, ".scratch");
        var thrown = await Record.ExceptionAsync(() => LocalEvidenceOutput.RunAsync(_root, preflight ? "local-plan" : "local", _root, run =>
        {
            var output = run.OutputDirectory;
            Assert.Equal(TestPathUtils.PathUnder(scratch, "package-restore"), run.PackageRestoreCache);
            if (!preflight)
                Directory.CreateDirectory(output);
            if (phase is "missing-manifest" or "invalid-manifest")
            {
                var proof = TestPathUtils.PathUnder(output, "proof");
                Directory.CreateDirectory(proof);
                if (phase == "invalid-manifest")
                    File.WriteAllText(TestPathUtils.PathUnder(proof, "evidence-manifest.json"), "not a manifest");
            }
            return Task.FromException<LocalEvidenceRunResult?>(failure);
        }));

        Assert.Same(failure, thrown);
        Assert.Equal(retainScratch, Directory.EnumerateDirectories(scratch, "evidence-workspace-*").Any());
        if (phase is "preflight" or "cancelled")
            Assert.Equal("earlier diagnostics", await File.ReadAllTextAsync(failureArchive));
        if (phase is not ("preflight" or "archive-collision"))
            Assert.Contains(failure.Message, ReadArchive(retained, archive, "run-status.txt")!);
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, "cleanup_failed.", false)]
    [InlineData(true, "Coverage gate failed.", false)]
    public async Task ManagedOutput_UsesManifestToDecideWhetherFailedScratchCanBeRemoved(
        bool cleanupCompleted, string? diagnostic, bool retainScratch)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options.Remove("--output");
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token) =>
        {
            var candidate = await BuildCompleteProofAsync(runOptions, token);
            var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(runOptions["--plan"], token));
            var results = candidate.ProducerResults.Select(result => result with
            {
                Outcome = EvidenceProducerOutcome.Failed,
                SatisfiedAssertionIds = [],
                Diagnostic = diagnostic,
            }).ToArray();
            return EvidenceManifestBuilder.Build(plan, results,
                metrics: candidate.Metrics with { CleanupCompleted = cleanupCompleted });
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(options, CancellationToken.None));

        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var scratch = TestPathUtils.PathUnder(retained, ".scratch");
        Assert.Equal(retainScratch, Directory.EnumerateDirectories(scratch, "evidence-workspace-*").Any());
        var archived = ReadArchive(retained, "latest-failure.zip", "proof/evidence-manifest.json");
        Assert.NotNull(archived);
        var failed = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(Encoding.UTF8.GetBytes(archived));
        Assert.Equal(cleanupCompleted, failed.Metrics.CleanupCompleted);
        Assert.All(failed.ProducerResults, result => Assert.Equal(diagnostic, result.Diagnostic));
    }

    [Fact]
    public async Task ManagedOutput_RetainsProducerLogsAndScratchWhenTypedCleanupFails()
    {
        var fixture = await CreateFixtureAsync();
        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(
            await File.ReadAllBytesAsync(TestPathUtils.PathUnder(fixture.Root, ".github", "evidence.policy.json")));
        var profile = policy.Profiles.Single(profile => profile.Producers is [{ Id: "package-checks" }]);
        var declaration = Assert.Single(profile.Producers);
        var plan = new EvidencePlan("1", "policy", "digest", "diff", profile, [], [], "input");
        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var scratch = TestPathUtils.PathUnder(retained, ".scratch");

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalEvidenceOutput.RunAsync(fixture.Root, "local", TestPathUtils.PathUnder(fixture.Root, ".git"), async run =>
        {
            var proof = TestPathUtils.PathUnder(run.OutputDirectory, "proof");
            Directory.CreateDirectory(proof);
            var settings = new RepositoryCheckSettings(fixture.Root, TestDotnetHost.Resolve(), proof)
            {
                PackageRestoreCache = run.PackageRestoreCache,
            };
            var writer = new EvidenceArtifactWriter(declaration, TestPathUtils.PathUnder(proof, "artifacts", declaration.Id));
            var producer = new PackageChecksProducer(settings, (request, token) =>
                run.ObserveCommandAsync((command, _) => command.Arguments[^1] == "gate"
                    ? Task.FromResult(new EvidenceCommandResult(0, "producer stdout", "", 1))
                    : Task.FromException<EvidenceCommandResult>(new EvidenceCommandException("cleanup_failed", "command still owns scratch")),
                    request, token));
            var result = await producer.ProduceAsync(new(plan, declaration, TimeProvider.System, writer), CancellationToken.None);
            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.NotNull(result.Diagnostic);
            Assert.DoesNotContain("cleanup_failed", result.Diagnostic, StringComparison.Ordinal);
            var manifest = EvidenceManifestBuilder.Build(plan, [result with { Artifacts = writer.WrittenArtifacts }]);
            Assert.True(manifest.Metrics.CleanupCompleted);
            await File.WriteAllBytesAsync(TestPathUtils.PathUnder(proof, "evidence-manifest.json"), EvidenceCanonicalJson.Serialize(manifest));
            throw new InvalidOperationException("local evidence execution failed");
        }));

        Assert.Single(Directory.EnumerateDirectories(scratch, "evidence-workspace-*"));
        Assert.Contains("producer stdout", ReadArchive(retained, "latest-failure.zip", "proof/package-checks/gate.log")!);
        Assert.Null(ReadArchive(retained, "latest-failure.zip", "proof/package-checks/verify.log"));
    }

    [Fact]
    public async Task ManagedOutput_RegisteredInstalledConsumerRetainsFeedWhenCommandCleanupFails()
    {
        NormalizedDiffPath[] changes = [new("tools/ForgeTrust.AppSurface.Evidence.Validation/InstalledConsumerProducer.cs")];
        var head = (await GitAsync(_repo, "rev-parse", "HEAD")).Trim();

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalEvidenceOutput.RunAsync(_root, "local", _root, async run =>
        {
            var (runOptions, plan) = await PrepareRegisteredRunAsync(run.OutputDirectory, "native", changes);
            Assert.Equal("installed-consumer", Assert.Single(plan.Profile.Producers).Id);
            var packagePath = TestPathUtils.PathUnder(run.OutputDirectory, "candidate.nupkg");
            await File.WriteAllTextAsync(packagePath, "candidate awaiting validation");
            runOptions["--package"] = packagePath;
            runOptions["--version"] = "0.0.0-local.1";
            runOptions["--source-commit"] = head;
            runOptions["--scratch-parent"] = _root;
            await RepositoryEvidenceWorkflow.RunLocalAsync(runOptions, CancellationToken.None,
                verifyLocalSourceAsync: _ => Task.CompletedTask,
                localRun: run,
                runProducerCommand: (request, _) =>
                {
                    Assert.Equal("git", request.FileName);
                    Assert.Equal("status", request.Arguments[0]);
                    return Task.FromException<EvidenceCommandResult>(
                        new EvidenceCommandException("cleanup_failed", "command still owns scratch"));
                });
            return null;
        }));

        var retained = TestPathUtils.PathUnder(_root, "TestResults", "repository-evidence", "local");
        var scratch = Assert.Single(Directory.EnumerateDirectories(TestPathUtils.PathUnder(retained, ".scratch"), "evidence-workspace-*"));
        Assert.True(File.Exists(TestPathUtils.PathUnder(scratch, "run", "candidate.nupkg")));
        Assert.Single(Directory.EnumerateDirectories(_root, "evidence-workspace-*"));
        var archived = ReadArchive(retained, "latest-failure.zip", "proof/evidence-manifest.json");
        Assert.NotNull(archived);
        var manifest = EvidenceCanonicalJson.Deserialize<EvidenceManifest>(Encoding.UTF8.GetBytes(archived));
        Assert.False(manifest.Metrics.CleanupCompleted);
        Assert.Contains(nameof(EvidenceCommandException), manifest.Metrics.CleanupDiagnostic);
        Assert.EndsWith("/cleanup_failed.", Assert.Single(manifest.ProducerResults).Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManagedOutput_RejectsCompletionWhenTheRunHandledAnUnsettledCommand()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => LocalEvidenceOutput.RunAsync(_root, "local", _root, async run =>
        {
            Directory.CreateDirectory(run.OutputDirectory);
            var request = new EvidenceCommandRequest("dotnet", ["--version"], run.OutputDirectory, TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<EvidenceCommandException>(() => run.ObserveCommandAsync(
                (_, _) => throw new EvidenceCommandException("cleanup_failed", "command still owns scratch"),
                request, CancellationToken.None));
            return null;
        }));

        Assert.Equal("Local evidence command cleanup could not be confirmed.", failure.Message);
        var retained = TestPathUtils.PathUnder(_root, "TestResults", "repository-evidence", "local");
        Assert.Single(Directory.EnumerateDirectories(TestPathUtils.PathUnder(retained, ".scratch"), "evidence-workspace-*"));
        Assert.Contains(failure.Message, ReadArchive(retained, "latest-failure.zip", "run-status.txt")!);
    }

    [Theory]
    [InlineData("proof/artifacts/solution-coverage/proof/result.json", null)]
    [InlineData("proof/artifacts/solution-coverage/proof/result.json", "[]")]
    [InlineData("proof/artifacts/solution-coverage/proof/result.json", "{ }")]
    [InlineData("local-policy.json", null)]
    [InlineData("local-policy.json", "{}")]
    [InlineData("changed-paths.json", null)]
    [InlineData("changed-paths.json", "[]")]
    public async Task ManagedOutput_RejectsUnverifiedZipBeforeReplacingPreviousProof(string entry, string? changedContent)
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options.Remove("--output");
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token) =>
        {
            return await BuildCompleteProofAsync(runOptions, token);
        });
        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));

        var retained = TestPathUtils.PathUnder(fixture.Root, "TestResults", "repository-evidence", "local");
        var latest = TestPathUtils.PathUnder(retained, "latest.zip");
        var previous = await File.ReadAllBytesAsync(latest);
        var extracted = TestPathUtils.PathUnder(_root, "verified-input");
        ZipFile.ExtractToDirectory(latest, extracted);
        var plan = ReadPlan(extracted);
        var manifest = ReadManifest(extracted);
        var replayInputs = new[] { "changed-paths.json", "local-policy.json" }.Select(name =>
            LocalEvidenceReplayInput.Snapshot(name, File.ReadAllBytes(TestPathUtils.PathUnder(extracted, name)))).ToArray();

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => LocalEvidenceOutput.RunAsync(fixture.Root, "local", TestPathUtils.PathUnder(fixture.Root, ".git"), run =>
        {
            var output = run.OutputDirectory;
            ZipFile.ExtractToDirectory(latest, output);
            var file = TestPathUtils.PathUnder(output, entry.Split('/'));
            if (changedContent is null)
                File.Delete(file);
            else
                File.WriteAllText(file, changedContent);
            return Task.FromResult<LocalEvidenceRunResult?>(new(plan, manifest, replayInputs));
        }));

        Assert.Equal(previous, await File.ReadAllBytesAsync(latest));
        Assert.Contains(failure.Message, ReadArchive(retained, "latest-failure.zip", "run-status.txt")!);
        Assert.Empty(Directory.EnumerateDirectories(TestPathUtils.PathUnder(retained, ".scratch"), "evidence-workspace-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedOutput_RejectsInvalidWorktreeWithoutCreatingOutput(bool staleGitPointer)
    {
        var fixture = await CreateFixtureAsync();
        var options = Options(fixture, "local");
        options.Remove("--output");
        var missing = TestPathUtils.PathUnder(_root, "non-worktree");
        options["--repository"] = missing;
        var workflow = new LocalEvidenceWorkflow();

        if (staleGitPointer)
        {
            Directory.CreateDirectory(missing);
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(missing, ".git"), "gitdir: missing-admin\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(options, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(options, CancellationToken.None));
            Assert.False(Directory.Exists(missing));
        }

        Assert.False(Directory.Exists(TestPathUtils.PathUnder(missing, "TestResults")));
    }

    [Fact]
    public async Task ManagedOutput_RejectsLinkedStorage()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        var target = TestPathUtils.PathUnder(_root, "external-results");
        Directory.CreateDirectory(target);
        var results = TestPathUtils.PathUnder(fixture.Root, "TestResults");
        Directory.CreateSymbolicLink(results, target);
        var options = Options(fixture, "local");
        options.Remove("--output");

        var error = await Assert.ThrowsAsync<IOException>(() => new LocalEvidenceWorkflow().RunAsync(options, CancellationToken.None));

        Assert.Contains(results, error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task ManagedOutput_AcceptsLinkedGitWorktree()
    {
        var fixture = await CreateFixtureAsync();
        var linked = new Fixture(TestPathUtils.PathUnder(_root, "linked"), fixture.Head);
        await GitAsync(fixture.Root, "worktree", "add", "--detach", "--quiet", linked.Root, fixture.Head);
        var primaryMarker = PendingCleanupMarker(TestPathUtils.PathUnder(fixture.Root, ".git"));
        Directory.CreateDirectory(Path.GetDirectoryName(primaryMarker)!);
        await File.WriteAllTextAsync(primaryMarker, "Primary worktree needs cleanup.");
        await ChangeAsync(linked, "docs/guide.md", "linked worktree change");
        var options = Options(linked, "local");
        options.Remove("--output");
        var workflow = new LocalEvidenceWorkflow();

        Assert.Equal(0, await workflow.RunAsync(options, CancellationToken.None));

        var retained = TestPathUtils.PathUnder(linked.Root, "TestResults", "repository-evidence", "local");
        Assert.NotNull(ReadArchive(retained, "latest.zip", "proof/evidence-manifest.json"));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(fixture.Root, "TestResults")));
        Assert.True(File.Exists(primaryMarker));
    }

    [Fact]
    public async Task ManagedOutput_RejectsOverlappingRunInTheSameWorktree()
    {
        var fixture = await CreateFixtureAsync();
        await ChangeAsync(fixture, "src/feature.cs", "class Feature { }");
        var options = Options(fixture, "local", "coverage");
        options.Remove("--output");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new LocalEvidenceWorkflow(workflow: async (runOptions, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            return await BuildCompleteProofAsync(runOptions, token);
        });

        var active = workflow.RunAsync(options, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Assert.ThrowsAsync<IOException>(() => workflow.RunAsync(options, CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            Assert.Equal(0, await active);
        }
    }

    private static string? ReadArchive(string root, string archiveName, string entryName)
    {
        using var archive = ZipFile.OpenRead(TestPathUtils.PathUnder(root, archiveName));
        var entry = archive.GetEntry(entryName);
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private string LastOutput => _lastOutput ?? throw new InvalidOperationException("Test output path was not initialized.");

    private async Task<Fixture> CreateFixtureAsync()
    {
        var root = TestPathUtils.PathUnder(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(root, ".gitignore"), "TestResults/\nbin/\nobj/\n");
        var policy = TestPathUtils.PathUnder(root, ".github", "evidence.policy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policy)!);
        File.Copy(TestPathUtils.PathUnder(_repo, ".github", "evidence.policy.json"), policy);
        await GitAsync(root, "init", "-q");
        await GitAsync(root, "config", "user.email", "evidence-tests@example.invalid");
        await GitAsync(root, "config", "user.name", "Evidence Tests");
        await GitAsync(root, "add", ".");
        await GitAsync(root, "commit", "-qm", "baseline");
        var head = await GitAsync(root, "rev-parse", "HEAD");
        return new Fixture(root, head.Trim());
    }

    private static async Task ChangeAsync(Fixture fixture, string relativePath, string content)
    {
        var path = TestPathUtils.PathUnder(fixture.Root, relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private Dictionary<string, string> Options(Fixture fixture, string operation, string? scope = null)
    {
        _lastOutput = TestPathUtils.PathUnder(_root, "output", Guid.NewGuid().ToString("N"));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--operation"] = operation,
            ["--repository"] = fixture.Root,
            ["--base"] = fixture.Head,
            ["--policy"] = TestPathUtils.PathUnder(fixture.Root, ".github", "evidence.policy.json"),
            ["--output"] = _lastOutput,
            ["--scope"] = scope ?? "all",
        };
    }

    private async Task<(Dictionary<string, string> Options, EvidencePlan Plan)> PrepareRegisteredRunAsync(
        string output, string scope, NormalizedDiffPath[] changes)
    {
        var policyPath = TestPathUtils.PathUnder(_repo, ".github", "evidence.policy.json");
        var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(await File.ReadAllBytesAsync(policyPath));
        var plan = RepositoryEvidenceWorkflow.ResolveScope(policy, changes, scope);
        Directory.CreateDirectory(output);
        var changedPaths = TestPathUtils.PathUnder(output, RepositoryEvidenceLayout.ChangedPathsFile);
        var planPath = TestPathUtils.PathUnder(output, RepositoryEvidenceLayout.PlanFile);
        await File.WriteAllBytesAsync(changedPaths, EvidenceCanonicalJson.Serialize(changes));
        await File.WriteAllBytesAsync(planPath, EvidenceCanonicalJson.Serialize(plan));
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--operation"] = "run",
            ["--scope"] = scope,
            ["--policy"] = policyPath,
            ["--changed-paths"] = changedPaths,
            ["--plan"] = planPath,
            ["--output"] = TestPathUtils.PathUnder(output, RepositoryEvidenceLayout.ProofDirectory),
            ["--repository"] = _repo,
            ["--dotnet"] = TestDotnetHost.Resolve(),
        };
        return (options, plan);
    }

    private static EvidencePlan ReadPlan(string output) => EvidenceCanonicalJson.Deserialize<EvidencePlan>(File.ReadAllBytes(TestPathUtils.PathUnder(output, "plan", "evidence-plan.json")));
    private static EvidenceManifest ReadManifest(string output) => EvidenceCanonicalJson.Deserialize<EvidenceManifest>(File.ReadAllBytes(TestPathUtils.PathUnder(output, "proof", "evidence-manifest.json")));

    private static async Task<EvidenceManifest> BuildCompleteProofAsync(
        IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(options["--plan"], cancellationToken));
        var output = options["--output"];
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(TestPathUtils.PathUnder(output, "evidence-plan.json"), EvidenceCanonicalJson.Serialize(plan), cancellationToken);
        var results = new List<EvidenceProducerResult>();
        foreach (var producer in plan.Profile.Producers)
        {
            var writer = new EvidenceArtifactWriter(producer, TestPathUtils.PathUnder(output, "artifacts", producer.Id));
            foreach (var slot in producer.ArtifactSlots.Where(slot => slot.Required))
            {
                await writer.WriteAsync(slot.LogicalName, $"proof/{slot.LogicalName}.json", "{}"u8.ToArray(), cancellationToken);
            }

            results.Add(new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Passed,
                producer.AssertionIds, Artifacts: writer.WrittenArtifacts));
        }

        return EvidenceManifestBuilder.Build(plan, results);
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessUtils.ExecuteProcessAsync(
            "git",
            arguments,
            workingDirectory,
            NullLogger.Instance,
            CancellationToken.None);
        Assert.True(result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{result.Stderr}");
        return result.Stdout;
    }

    private sealed record Fixture(string Root, string Head);
}
