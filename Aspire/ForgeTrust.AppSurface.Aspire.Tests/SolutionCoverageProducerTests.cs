using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

[Collection("Native evidence program")]
public sealed class SolutionCoverageProducerTests
{
    [Fact]
    public async Task ProduceAsync_RejectsInvalidProducerContextBeforeRunningCommands()
    {
        using var fixture = new Fixture();
        var producer = fixture.Create((_, _) => throw new Xunit.Sdk.XunitException("Invalid context must not run a command."));
        Assert.Throws<ArgumentNullException>(() => new SolutionCoverageProducer(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => producer.ProduceAsync(null!, CancellationToken.None).AsTask());

        var mismatch = await producer.ProduceAsync(fixture.Context with
        {
            Producer = fixture.Context.Producer with { Id = "different-producer" },
        }, CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.Failed, mismatch.Outcome);
        Assert.Empty(mismatch.SatisfiedAssertionIds);

        var noWriter = await producer.ProduceAsync(fixture.Context with { Artifacts = null }, CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.Invalid, noWriter.Outcome);
        Assert.Empty(noWriter.SatisfiedAssertionIds);
    }

    [Fact]
    public async Task ProduceAsync_RunsEverySolutionTestProjectThenGatesAndWritesProof()
    {
        using var fixture = new Fixture();
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.Create(async (request, _) =>
        {
            commands.Add(request);
            await fixture.CompleteCommandAsync(request);
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal(["coverage/solution-complete@1", "coverage/thresholds@1"], result.SatisfiedAssertionIds);
        var run = Assert.Single(commands, command => command.Arguments.Contains("--solution"));
        Assert.Contains("--solution", run.Arguments);
        Assert.DoesNotContain("--test-project", run.Arguments);
        var gateCommands = commands.Where(command => command.Arguments.Contains("gate", StringComparer.Ordinal)).ToArray();
        Assert.NotEmpty(gateCommands);
        Assert.All(gateCommands, command =>
        {
            Assert.Contains("--no-build", command.Arguments);
            Assert.Contains("--no-restore", command.Arguments);
        });
        Assert.Equal(8, run.Arguments.Count(argument => argument == "--exclusive-test-project"));
        Assert.Contains("--parallelism", run.Arguments);
        Assert.Contains("--require-non-sandbox", run.Arguments);
        Assert.Equal("proof/aggregate-gate.json", Assert.Single(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "aggregate-gate").RelativePath);
        Assert.Contains(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "result");
        Assert.Contains(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "transcript");
        Assert.DoesNotContain(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "patch-gate");
    }

    [Fact]
    public async Task ProduceAsync_RejectsIncompleteFullSolutionInventory()
    {
        using var fixture = new Fixture();
        var producer = fixture.Create(async (request, _) =>
        {
            if (request.Arguments.Contains("run") && request.Arguments.Contains("coverage"))
                await fixture.WriteCoverageAsync(TestPathUtils.PathUnder(ValueAfter(request.Arguments, "--output"), "timings.json"), omitProject: true);
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.DoesNotContain(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "aggregate-gate");
    }

    [Theory]
    [InlineData("result")]
    [InlineData("transcript")]
    [InlineData("both")]
    [InlineData("io")]
    public async Task ProduceAsync_ReportsUnavailableFailureArtifactsAndStillAttemptsEachSlot(string blocked)
    {
        using var fixture = new Fixture();
        var proof = Path.Join(fixture.Root, "artifacts", "proof");
        if (blocked == "io")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(proof)!);
            await File.WriteAllTextAsync(proof, "caller-owned");
        }
        else
            foreach (var name in new[] { "result", "transcript" }.Where(name => blocked == "both" || blocked == name))
                await fixture.Writer.WriteAsync(name, $"proof/{name}.json", System.Text.Encoding.UTF8.GetBytes("existing " + name));
        var producer = fixture.Create((_, _) => Task.FromResult(new EvidenceCommandResult(1, "", "private failure detail", 1)));

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Contains("failed during collection", result.Diagnostic);
        Assert.Contains("Failure artifacts unavailable:", result.Diagnostic);
        Assert.DoesNotContain(fixture.Root, result.Diagnostic);
        Assert.DoesNotContain("private failure detail", result.Diagnostic);
        if (blocked == "io")
        {
            Assert.Contains("result (IOException)", result.Diagnostic);
            Assert.Contains("transcript (IOException)", result.Diagnostic);
            Assert.Empty(fixture.Writer.WrittenArtifacts);
            Assert.Equal("caller-owned", await File.ReadAllTextAsync(proof));
        }
        else
        {
            Assert.Equal(2, fixture.Writer.WrittenArtifacts.Count);
            foreach (var name in new[] { "result", "transcript" })
            {
                var contents = await File.ReadAllTextAsync(Path.Join(proof, name + ".json"));
                if (blocked == "both" || blocked == name)
                {
                    Assert.Contains(name + " (InvalidOperationException)", result.Diagnostic);
                    Assert.Equal("existing " + name, contents);
                }
                else
                {
                    Assert.DoesNotContain(name + " (", result.Diagnostic);
                    using var document = JsonDocument.Parse(contents);
                    if (name == "result") Assert.False(document.RootElement.GetProperty("completed").GetBoolean());
                    else Assert.NotEmpty(document.RootElement.EnumerateArray());
                }
            }
        }
    }

    [Theory]
    [InlineData("missing-solution")]
    [InlineData("empty-inventory")]
    public async Task ProduceAsync_RejectsMissingOrEmptySolutionInventory(string defect)
    {
        using var fixture = new Fixture();
        var solution = TestPathUtils.PathUnder(fixture.Root, "ForgeTrust.AppSurface.slnx");
        if (defect == "missing-solution")
            File.Delete(solution);
        else
            new XDocument(new XElement("Solution")).Save(solution);

        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.Create((request, _) =>
        {
            commands.Add(request);
            return Task.FromResult(Success());
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        await AssertFailedWithoutClaimsAsync(fixture, result, "settings");
        Assert.Empty(commands);
    }

    [Fact]
    public async Task ProduceAsync_RejectsNonFreshCoverageOutput()
    {
        using var fixture = new Fixture();
        var coverage = TestPathUtils.PathUnder(fixture.Output, "coverage");
        Directory.CreateDirectory(coverage);
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(coverage, "stale.txt"), "stale output");
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.Create((request, _) =>
        {
            commands.Add(request);
            return Task.FromResult(Success());
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        await AssertFailedWithoutClaimsAsync(fixture, result, "settings");
        Assert.Empty(commands);
    }

    [Theory]
    [InlineData("debug-configuration")]
    [InlineData("wrong-coverage-driver")]
    [InlineData("failed-merge")]
    [InlineData("wrong-coverage-count")]
    [InlineData("wrong-project-inventory")]
    [InlineData("failed-project")]
    [InlineData("nonzero-project-exit")]
    [InlineData("missing-coverage-artifact")]
    [InlineData("malformed-project-exit-code")]
    [InlineData("malformed-json")]
    public async Task ProduceAsync_RejectsMalformedOrUnsuccessfulTimings(string defect)
    {
        using var fixture = new Fixture();
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.Create(async (request, _) =>
        {
            commands.Add(request);
            await fixture.CompleteCommandAsync(request);
            if (request.Arguments.Contains("--solution"))
                await fixture.WriteCoverageAsync(TestPathUtils.PathUnder(fixture.Output, "coverage", "timings.json"), defect: defect);
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        await AssertFailedWithoutClaimsAsync(fixture, result, "collection");
        Assert.Contains(commands, command => command.Arguments.Contains("--solution"));
        Assert.DoesNotContain(commands, command => command.Arguments.Contains("gate", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ProduceAsync_RejectsChangedAggregateThresholds()
    {
        using var fixture = new Fixture();
        var producer = fixture.Create(async (request, _) =>
        {
            await fixture.CompleteCommandAsync(request, alterGate: request.Arguments.Contains("gate"));
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Contains("aggregate-gate", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "aggregate-gate");
    }

    [Theory]
    [InlineData("line-threshold")]
    [InlineData("branch-threshold")]
    [InlineData("line-percent")]
    [InlineData("branch-percent")]
    [InlineData("malformed-line-threshold")]
    [InlineData("malformed-line-percent")]
    [InlineData("failed-gate")]
    [InlineData("changed-tolerance")]
    public async Task ProduceAsync_RejectsMalformedAggregateGateThresholdsAndPercentages(string defect)
    {
        using var fixture = new Fixture();
        var producer = fixture.Create(async (request, _) =>
        {
            await fixture.CompleteCommandAsync(request);
            if (request.Arguments.Contains("gate", StringComparer.Ordinal))
                await fixture.WriteAggregateGateAsync(request, defect);
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        await AssertFailedWithoutClaimsAsync(fixture, result, "aggregate-gate");
        Assert.Contains("aggregate-gate", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("zero-patch")]
    [InlineData("thresholds")]
    [InlineData("patch-line")]
    [InlineData("patch-branch")]
    public async Task ProduceAsync_ValidatesPatchGateFlagsAndThresholds(string defect)
    {
        using var fixture = new Fixture();
        var sourceChecks = 0;
        var settings = new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, fixture.Output, DiffFile: TestPathUtils.PathUnder(fixture.Root, "local.patch"))
        { VerifyLocalSourceAsync = _ => { sourceChecks++; return Task.CompletedTask; } };
        await File.WriteAllTextAsync(settings.DiffFile!, "diff --git a/file.cs b/file.cs\n");
        EvidenceCommandRequest? patchRequest = null;
        var producer = fixture.CreateWithSettings(settings, async (request, _) =>
        {
            await fixture.CompleteCommandAsync(request);
            if (request.Arguments.Contains("gate") && request.Arguments.Contains("--min-patch-line"))
            {
                patchRequest = request;
                await fixture.WriteGateAsync(request, defect);
            }
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        if (defect is "success" or "zero-patch")
        {
            Assert.Equal(3, sourceChecks);
            Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
            Assert.Equal(["coverage/solution-complete@1", "coverage/thresholds@1"], result.SatisfiedAssertionIds);
            Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "patch-gate");
        }
        else
        {
            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Empty(result.SatisfiedAssertionIds);
        }
        Assert.NotNull(patchRequest);
        Assert.Contains("--diff-file", patchRequest!.Arguments);
        Assert.Equal("codecov", ValueAfter(patchRequest.Arguments, "--patch-line-mode"));
        Assert.Equal("95", ValueAfter(patchRequest.Arguments, "--min-patch-line"));
        Assert.Equal("85", ValueAfter(patchRequest.Arguments, "--min-patch-branch"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ProduceAsync_FailsCommandFailureOrTruncation(bool truncate, bool nonzero)
    {
        using var fixture = new Fixture();
        var producer = fixture.Create(async (request, _) =>
        {
            await fixture.CompleteCommandAsync(request);
            return new EvidenceCommandResult(nonzero ? 1 : 0, "", "", 1) { OutputTruncated = truncate };
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
    }

    [Fact]
    public async Task ProduceAsync_PropagatesCallerCancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var producer = fixture.Create((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<EvidenceCommandResult>(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer.ProduceAsync(fixture.Context, cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProduceAsync_RejectsDirtySourceBeforeCollection(bool native)
    {
        using var fixture = new Fixture();
        fixture.GitStatusResponses.Enqueue(" M src/Changed.cs\n");
        var commands = new List<EvidenceCommandRequest>();
        var settings = native
            ? fixture.NativeSettings(diffBase: "base-commit")
            : new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, fixture.Output, DiffBase: "base-commit");
        var producer = fixture.CreateWithSettings(settings, async (request, _) =>
        {
            commands.Add(request);
            await fixture.CompleteCommandAsync(request);
            if (request.Arguments.Contains("--min-patch-line")) await fixture.WriteGateAsync(request, "success");
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.DoesNotContain(commands, command => command.Arguments.Contains("coverage"));
        Assert.Single(fixture.GitCommands, command => command.Arguments.Contains("status"));
        Assert.DoesNotContain(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName is "aggregate-gate" or "patch-gate");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProduceAsync_RejectsSourceDirtiedByCollectionBeforeGatesOrClaim(bool native)
    {
        using var fixture = new Fixture();
        fixture.GitStatusResponses.Enqueue(string.Empty);
        fixture.GitStatusResponses.Enqueue("?? src/GeneratedSource.cs\n");
        var commands = new List<EvidenceCommandRequest>();
        var settings = native
            ? fixture.NativeSettings(diffBase: "base-commit")
            : new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, fixture.Output, DiffBase: "base-commit");
        var producer = fixture.CreateWithSettings(settings, async (request, _) =>
        {
            commands.Add(request);
            await fixture.CompleteCommandAsync(request);
            if (request.Arguments.Contains("--min-patch-line")) await fixture.WriteGateAsync(request, "success");
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Contains(commands, command => command.Arguments.Contains("coverage"));
        Assert.Equal(2, fixture.GitCommands.Count(command => command.Arguments.Contains("status")));
        Assert.DoesNotContain(commands, command => command.Arguments.Contains("gate", StringComparer.Ordinal));
        Assert.DoesNotContain(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName is "aggregate-gate" or "patch-gate");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("base-commit", "local.patch")]
    public async Task ProduceAsync_RejectsInvalidNativeFanInConfiguration(string? diffBase, string? diffFile)
    {
        using var fixture = new Fixture();
        var settings = fixture.NativeSettings(diffBase, diffFile is null ? null : TestPathUtils.PathUnder(fixture.Root, diffFile));
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.CreateWithSettings(settings, (request, _) =>
        {
            commands.Add(request);
            return Task.FromResult(Success());
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task ProduceAsync_RejectsPatchOverrideWithoutLocalSourceVerification()
    {
        using var fixture = new Fixture();
        var settings = new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, fixture.Output,
            DiffFile: TestPathUtils.PathUnder(fixture.Root, "local.patch"));
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.CreateWithSettings(settings, async (request, _) =>
        {
            commands.Add(request);
            await fixture.CompleteCommandAsync(request);
            if (request.Arguments.Contains("--min-patch-line")) await fixture.WriteGateAsync(request, "success");
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(commands);
        Assert.Empty(result.SatisfiedAssertionIds);
    }

    [Fact]
    public async Task ProduceAsync_RejectsSourceDirtiedByGateBeforePublishingClaim()
    {
        using var fixture = new Fixture();
        fixture.GitStatusResponses.Enqueue(string.Empty);
        fixture.GitStatusResponses.Enqueue(string.Empty);
        fixture.GitStatusResponses.Enqueue(" M src/Changed.cs\n");
        var producer = fixture.Create(async (request, _) =>
        {
            await fixture.CompleteCommandAsync(request);
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Equal(3, fixture.GitCommands.Count(command => command.Arguments.Contains("status")));
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.DoesNotContain(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "aggregate-gate");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ProduceAsync_RejectsLocalSourceDriftAtEachCheckpoint(int failingCheckpoint)
    {
        using var fixture = new Fixture();
        var sourceChecks = 0;
        var settings = new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, fixture.Output,
            DiffFile: TestPathUtils.PathUnder(fixture.Root, "local.patch"))
        {
            VerifyLocalSourceAsync = _ => ++sourceChecks == failingCheckpoint
                ? Task.FromException(new InvalidOperationException("Local source changed."))
                : Task.CompletedTask,
        };
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.CreateWithSettings(settings, async (request, _) =>
        {
            commands.Add(request);
            await fixture.CompleteCommandAsync(request);
            if (request.Arguments.Contains("--min-patch-line")) await fixture.WriteGateAsync(request, "success");
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Equal(failingCheckpoint, sourceChecks);
        Assert.Equal(failingCheckpoint > 1, commands.Any(command => command.Arguments.Contains("--solution")));
        Assert.Equal(failingCheckpoint > 2, commands.Any(command => command.Arguments.Contains("gate")));
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.DoesNotContain(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName is "aggregate-gate" or "patch-gate");
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("tree")]
    [InlineData("sdk")]
    public async Task ProduceAsync_RejectsInvalidRepositoryOrSdkIdentity(string identity)
    {
        using var fixture = new Fixture();
        switch (identity)
        {
            case "commit":
                fixture.HeadResponses.Enqueue("short-commit");
                break;
            case "tree":
                fixture.TreeResponses.Enqueue("short-tree");
                break;
            case "sdk":
                fixture.SdkResponses.Enqueue(string.Empty);
                break;
            default:
                throw new InvalidOperationException($"Unknown identity defect: {identity}");
        }

        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.Create((request, _) =>
        {
            commands.Add(request);
            return Task.FromResult(Success());
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        await AssertFailedWithoutClaimsAsync(fixture, result, "identity");
        Assert.DoesNotContain(commands, command => command.Arguments.Contains("--solution"));
    }

    [Fact]
    public async Task ProduceAsync_RejectsHeadChangeBeforeNativeCollection()
    {
        using var fixture = new Fixture();
        fixture.HeadResponses.Enqueue(Fixture.Commit);
        fixture.HeadResponses.Enqueue(new string('c', 40));
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.CreateWithSettings(fixture.NativeSettings("base-commit"), (request, _) =>
        {
            commands.Add(request);
            return Task.FromResult(Success());
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.DoesNotContain(commands, command => command.Arguments.Contains("coverage"));
    }

    [Fact]
    public async Task ProduceAsync_PropagatesSelectedDotnetHostEnvironmentToCommands()
    {
        using var fixture = new Fixture();
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.Create(async (request, _) =>
        {
            commands.Add(request);
            await fixture.CompleteCommandAsync(request);
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        var coverage = Assert.Single(commands, command => command.Arguments.Contains("--solution"));
        Assert.Equal(fixture.Dotnet, coverage.Environment!["DOTNET_HOST_PATH"]);
        Assert.StartsWith(Path.GetDirectoryName(fixture.Dotnet) + Path.PathSeparator, coverage.Environment["PATH"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProduceAsync_RejectsIncompleteNativeWorkflowProvenanceBeforeCapture()
    {
        using var fixture = new Fixture();
        var nativeRoot = TestPathUtils.PathUnder(fixture.Root, "native-captures");
        Directory.CreateDirectory(TestPathUtils.PathUnder(nativeRoot, "linux"));
        Directory.CreateDirectory(TestPathUtils.PathUnder(nativeRoot, "windows"));
        Directory.CreateDirectory(TestPathUtils.PathUnder(nativeRoot, "darwin"));
        var oldRepository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var oldRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        var oldAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
        Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", "forge-trust/appsurface");
        Environment.SetEnvironmentVariable("GITHUB_RUN_ID", "12345");
        Environment.SetEnvironmentVariable("GITHUB_RUN_ATTEMPT", null);
        try
        {
            var commands = new List<EvidenceCommandRequest>();
            var producer = fixture.CreateWithSettings(fixture.NativeSettings("base-commit"), async (request, _) =>
            {
                commands.Add(request);
                await fixture.CompleteCommandAsync(request);
                return Success();
            });

            var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

            await AssertFailedWithoutClaimsAsync(fixture, result, "native-fan-in");
            Assert.Contains(commands, command => command.Arguments.Contains("--solution"));
            Assert.Contains(commands, command => command.Arguments.Contains("gate", StringComparer.Ordinal));
            Assert.False(Directory.Exists(TestPathUtils.PathUnder(fixture.Output, "coverage-fanin")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", oldRepository);
            Environment.SetEnvironmentVariable("GITHUB_RUN_ID", oldRunId);
            Environment.SetEnvironmentVariable("GITHUB_RUN_ATTEMPT", oldAttempt);
        }
    }

    [Theory]
    [InlineData("Linux", "Linux")]
    [InlineData("Windows", "Windows")]
    [InlineData("FreeBSD", "Darwin")]
    public async Task ProduceAsync_ComposesAndVerifiesNativeCapturesBeforePublishingCoverageClaims(string runnerOs, string expectedPlatform)
    {
        using var producerFixture = new Fixture();
        using var fanInFixture = new EvidenceCoverageFanInTests.FanInFixture();
        await fanInFixture.WriteMinimalBundleAsync();

        const string commit = Fixture.Commit;
        const string tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string sdk = "10.0.100";
        var run = new Dictionary<string, string?>
        {
            ["GITHUB_REPOSITORY"] = "forge-trust/appsurface",
            ["GITHUB_RUN_ID"] = "12345",
            ["GITHUB_RUN_ATTEMPT"] = "2",
        };
        var nativeRoot = TestPathUtils.PathUnder(producerFixture.Root, "native-captures");
        Directory.CreateDirectory(nativeRoot);
        var platforms = new[] { "Linux", "Windows", "Darwin" };
        for (var index = 0; index < platforms.Length; index++)
        {
            var nativeSource = fanInFixture.Native[index];
            var nativeOutput = TestPathUtils.PathUnder(nativeRoot, platforms[index].ToLowerInvariant());
            await EvidenceCoverageFanIn.CaptureAsync(new EvidenceCoverageCaptureRequest(
                producerFixture.Root, nativeOutput,
                TestPathUtils.PathUnder(nativeSource, "coverage.cobertura.xml"),
                TestPathUtils.PathUnder(nativeSource, "collector.cobertura.xml"), null,
                "native", commit, tree, sdk, "Release", run, platforms[index],
                producerFixture.Projects.ToHashSet(StringComparer.Ordinal)));
        }

        var oldRepository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var oldRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        var oldAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
        var oldRunnerOs = Environment.GetEnvironmentVariable("RUNNER_OS");
        Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", run["GITHUB_REPOSITORY"]);
        Environment.SetEnvironmentVariable("GITHUB_RUN_ID", run["GITHUB_RUN_ID"]);
        Environment.SetEnvironmentVariable("GITHUB_RUN_ATTEMPT", run["GITHUB_RUN_ATTEMPT"]);
        Environment.SetEnvironmentVariable("RUNNER_OS", runnerOs);
        try
        {
            var commands = new List<EvidenceCommandRequest>();
            var settings = new RepositoryCheckSettings(producerFixture.Root, producerFixture.Dotnet,
                producerFixture.Output, "diff", nativeRoot);
            var producer = producerFixture.CreateWithSettings(settings, async (request, _) =>
            {
                commands.Add(request);
                if (request.Arguments.Contains("--solution"))
                {
                    await producerFixture.CompleteCommandAsync(request);
                    var output = ValueAfter(request.Arguments, "--output");
                    File.Copy(TestPathUtils.PathUnder(fanInFixture.Primary, "coverage.cobertura.xml"),
                        TestPathUtils.PathUnder(output, "coverage.cobertura.xml"), overwrite: true);
                    return Success();
                }

                if (request.Arguments.Contains("gate", StringComparer.Ordinal))
                {
                    await WriteFanInGateAsync(request);
                    return Success();
                }

                return Success();
            });

            var result = await producer.ProduceAsync(producerFixture.Context, CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
            Assert.Equal(["coverage/solution-complete@1", "coverage/thresholds@1"], result.SatisfiedAssertionIds);
            using (var primaryManifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                TestPathUtils.PathUnder(producerFixture.Output, "coverage-fanin", "manifest.json"))))
                Assert.Equal(expectedPlatform, primaryManifest.RootElement.GetProperty("platform").GetString());
            var gateCommands = commands.Where(command => command.Arguments.Contains("gate", StringComparer.Ordinal)).ToArray();
            Assert.Equal(["aggregate-gate", "primary-gate", "patch-gate"], gateCommands.Select(command =>
                Path.GetFileName(ValueAfter(command.Arguments, "--output"))));
            var primaryGate = gateCommands[1];
            Assert.Equal("diff", ValueAfter(primaryGate.Arguments, "--diff-base"));
            Assert.Equal("codecov", ValueAfter(primaryGate.Arguments, "--patch-line-mode"));
            Assert.DoesNotContain("--min-patch-line", primaryGate.Arguments);
            var patchGate = gateCommands[2];
            Assert.Equal("95", ValueAfter(patchGate.Arguments, "--min-patch-line"));
            Assert.Equal("85", ValueAfter(patchGate.Arguments, "--min-patch-branch"));

            var composed = TestPathUtils.PathUnder(producerFixture.Output, "coverage-fanin-composed");
            var primaryGatePath = TestPathUtils.PathUnder(producerFixture.Output, "coverage", "primary-gate", "coverage-gate.json");
            var patchGatePath = TestPathUtils.PathUnder(producerFixture.Output, "coverage", "patch-gate", "coverage-gate.json");
            var nativeDirectories = Directory.GetDirectories(nativeRoot).Order(StringComparer.Ordinal).ToArray();
            Assert.True(File.Exists(TestPathUtils.PathUnder(composed, "coverage.cobertura.xml")));
            using (var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(composed, "verification.json"))))
            {
                Assert.Equal(4, receipt.RootElement.GetProperty("inputs").GetArrayLength());
                Assert.Equal(1, receipt.RootElement.GetProperty("inventory").GetProperty("retainedClassLines").GetInt32());
            }
            Assert.True(File.Exists(primaryGatePath));
            Assert.True(File.Exists(patchGatePath));

            foreach (var capture in nativeDirectories)
            {
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(TestPathUtils.PathUnder(capture, "manifest.json")));
                var root = manifest.RootElement;
                Assert.Equal(commit, root.GetProperty("commit").GetString());
                Assert.Equal(tree, root.GetProperty("tree").GetString());
                Assert.Equal(sdk, root.GetProperty("sdk").GetString());
                Assert.Equal("Release", root.GetProperty("configuration").GetString());
                Assert.Equal(run["GITHUB_RUN_ID"], root.GetProperty("run").GetProperty("GITHUB_RUN_ID").GetString());
                Assert.Equal(run["GITHUB_RUN_ATTEMPT"], root.GetProperty("run").GetProperty("GITHUB_RUN_ATTEMPT").GetString());
            }

            Assert.Contains(producerFixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "aggregate-gate");
            Assert.Contains(producerFixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "patch-gate");
            Assert.Contains(producerFixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "result");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", oldRepository);
            Environment.SetEnvironmentVariable("GITHUB_RUN_ID", oldRunId);
            Environment.SetEnvironmentVariable("GITHUB_RUN_ATTEMPT", oldAttempt);
            Environment.SetEnvironmentVariable("RUNNER_OS", oldRunnerOs);
        }

        async Task WriteFanInGateAsync(EvidenceCommandRequest request)
        {
            var output = ValueAfter(request.Arguments, "--output");
            var report = Path.GetFileName(ValueAfter(request.Arguments, "--coverage"));
            var patch = request.Arguments.Contains("--min-patch-line", StringComparer.Ordinal);
            Directory.CreateDirectory(output);
            var gate = new
            {
                passed = true,
                coverage = report,
                tolerancePercent = .5m,
                patchLineMode = "codecov",
                patchDiffSource = new { diffBase = "diff" },
                thresholds = new
                {
                    line = patch ? 0m : 95m,
                    branch = patch ? 0m : 85m,
                    patchLine = patch ? 95m : (decimal?)null,
                    patchBranch = patch ? 85m : (decimal?)null,
                },
                effectiveThresholds = new
                {
                    line = patch ? 0m : 94.5m,
                    branch = patch ? 0m : 84.5m,
                    patchLine = patch ? 94.5m : (decimal?)null,
                    patchBranch = patch ? 84.5m : (decimal?)null,
                },
                line = new { covered = 1, valid = 1, percent = 100d },
                branch = new { covered = 1, valid = 1, percent = 100d },
                patchLine = new { covered = 1, measurable = 1, percent = 100d },
                patchBranch = new { covered = 1, measurable = 1, percent = 100d },
            };
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(output, "coverage-gate.json"), JsonSerializer.Serialize(gate));
        }
    }

    private static async Task AssertFailedWithoutClaimsAsync(Fixture fixture, EvidenceProducerResult result, string expectedStage)
    {
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Contains(expectedStage, result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Writer.WrittenArtifacts,
            artifact => artifact.LogicalName is "aggregate-gate" or "patch-gate");
        Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "transcript");
        Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "result");

        using var resultDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
            TestPathUtils.PathUnder(fixture.Root, "artifacts", "proof", "result.json")));
        Assert.False(resultDocument.RootElement.GetProperty("completed").GetBoolean());
        Assert.Equal(expectedStage, resultDocument.RootElement.GetProperty("stage").GetString());
        Assert.False(resultDocument.RootElement.TryGetProperty("assertions", out _));
    }

    private static EvidenceCommandResult Success() => new(0, "", "", 1);
    private static string ValueAfter(IReadOnlyList<string> args, string value) => args[Array.IndexOf(args.ToArray(), value) + 1];

    private sealed class Fixture : IDisposable
    {
        public const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly string _root = Directory.CreateTempSubdirectory("solution-coverage-producer-").FullName;
        private readonly string _dotnet;
        private readonly string _output;
        public string Root => _root;
        public string Dotnet => _dotnet;
        public string Output => _output;
        private readonly string[] _projects = ["tests/Alpha.Tests/Alpha.Tests.csproj", "tests/Beta.IntegrationTests/Beta.IntegrationTests.csproj"];
        public string[] Projects => _projects;
        private readonly EvidenceProducerDeclaration _declaration;
        public EvidenceProducerContext Context { get; }
        public EvidenceArtifactWriter Writer { get; }
        public List<EvidenceCommandRequest> GitCommands { get; } = [];
        public Queue<string> GitStatusResponses { get; } = new();
        public Queue<string> HeadResponses { get; } = new();
        public Queue<string> TreeResponses { get; } = new();
        public Queue<string> SdkResponses { get; } = new();

        public Fixture()
        {
            _dotnet = TestPathUtils.PathUnder(_root, "dotnet");
            File.WriteAllText(_dotnet, "test host placeholder");
            _output = TestPathUtils.PathUnder(_root, "evidence");
            Directory.CreateDirectory(_output);
            var solution = new XDocument(new XElement("Solution", _projects.Select(path => new XElement("Project", new XAttribute("Path", path)))));
            solution.Save(TestPathUtils.PathUnder(_root, "ForgeTrust.AppSurface.slnx"));
            _declaration = RepositoryEvidenceWorkflow.Declaration("solution-coverage");
            Writer = new EvidenceArtifactWriter(_declaration, TestPathUtils.PathUnder(_root, "artifacts"));
            var plan = new EvidencePlan("1", "policy", "digest", "diff", new("profile", EvidenceProfileScope.Targeted, [], [_declaration], []), [], [], "input");
            Context = new EvidenceProducerContext(plan, _declaration, TimeProvider.System, Writer);
        }

        public SolutionCoverageProducer Create(Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> command) =>
            CreateWithSettings(new RepositoryCheckSettings(_root, _dotnet, _output), command);

        public SolutionCoverageProducer CreateWithSettings(RepositoryCheckSettings settings, Func<EvidenceCommandRequest, CancellationToken, Task<EvidenceCommandResult>> command) =>
            new(settings, async (request, token) =>
            {
                if (request.FileName == "git")
                {
                    GitCommands.Add(request);
                    if (request.Arguments.Contains("status"))
                        return new EvidenceCommandResult(0, GitStatusResponses.Count > 0 ? GitStatusResponses.Dequeue() : string.Empty, "", 1);
                    if (request.Arguments.Last() == "HEAD^{tree}")
                        return new EvidenceCommandResult(0, TreeResponses.Count > 0 ? TreeResponses.Dequeue() : new string('b', 40), "", 1);
                    if (request.Arguments.Last() == "HEAD^{commit}")
                        return new EvidenceCommandResult(0, HeadResponses.Count > 0 ? HeadResponses.Dequeue() : Commit, "", 1);
                }
                if (request.Arguments.Contains("--version"))
                    return new EvidenceCommandResult(0, SdkResponses.Count > 0 ? SdkResponses.Dequeue() : "10.0.100", "", 1);
                return await command(request, token);
            });

        public RepositoryCheckSettings NativeSettings(string? diffBase, string? diffFile = null) =>
            new(_root, _dotnet, _output, diffBase, TestPathUtils.PathUnder(_root, "native-captures"), diffFile);

        public async Task CompleteCommandAsync(EvidenceCommandRequest request, bool alterGate = false)
        {
            if (request.Arguments.Contains("--version")) return;
            if (request.FileName == "git") return;
            if (request.Arguments.Contains("gate"))
            {
                var outDir = ValueAfter(request.Arguments, "--output");
                Directory.CreateDirectory(outDir);
                var passed = new
                {
                    passed = true,
                    tolerancePercent = .5m,
                    thresholds = new { line = alterGate ? 94m : 95m, branch = 85m, patchLine = (decimal?)null, patchBranch = (decimal?)null },
                    patchLineMode = (string?)null,
                    line = new { percent = 99m },
                    branch = new { percent = 90m },
                    patchLine = (object?)null,
                    patchBranch = (object?)null,
                };
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(outDir, "coverage-gate.json"), JsonSerializer.Serialize(passed));
                return;
            }
            if (request.Arguments.Contains("coverage"))
            {
                var coverage = ValueAfter(request.Arguments, "--output");
                Directory.CreateDirectory(coverage);
                await File.WriteAllTextAsync(TestPathUtils.PathUnder(coverage, "coverage.cobertura.xml"), "<coverage />");
                await WriteCoverageAsync(TestPathUtils.PathUnder(coverage, "timings.json"));
            }
        }

        public async Task WriteGateAsync(EvidenceCommandRequest request, string defect)
        {
            var outDir = ValueAfter(request.Arguments, "--output");
            Directory.CreateDirectory(outDir);
            var invalidThresholds = defect == "thresholds";
            var passed = new
            {
                passed = true,
                tolerancePercent = .5m,
                thresholds = new { line = 95m, branch = 85m, patchLine = invalidThresholds || defect == "patch-line" ? 94m : 95m, patchBranch = defect == "patch-branch" ? 84m : 85m },
                patchLineMode = "codecov",
                line = new { percent = 99m },
                branch = new { percent = 90m },
                patchLine = defect == "zero-patch" ? (object)new { percent = 100m, covered = 0, total = 0 } : new { percent = 99m },
                patchBranch = defect == "zero-patch" ? (object)new { percent = 100m, covered = 0, total = 0 } : new { percent = 90m },
            };
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(outDir, "coverage-gate.json"), JsonSerializer.Serialize(passed));
        }

        public async Task WriteAggregateGateAsync(EvidenceCommandRequest request, string defect)
        {
            var outDir = ValueAfter(request.Arguments, "--output");
            Directory.CreateDirectory(outDir);
            object lineThreshold = defect == "malformed-line-threshold" ? "95" : defect == "line-threshold" ? 94m : 95m;
            object branchThreshold = defect == "branch-threshold" ? 84m : 85m;
            object linePercent = defect == "malformed-line-percent" ? "99" : defect == "line-percent" ? 94.4d : 99d;
            object branchPercent = defect == "branch-percent" ? 84.4d : 90d;
            var gate = new
            {
                passed = defect != "failed-gate",
                tolerancePercent = defect == "changed-tolerance" ? 1m : .5m,
                thresholds = new { line = lineThreshold, branch = branchThreshold },
                line = new { percent = linePercent },
                branch = new { percent = branchPercent },
            };
            await File.WriteAllTextAsync(TestPathUtils.PathUnder(outDir, "coverage-gate.json"), JsonSerializer.Serialize(gate));
        }

        public async Task WriteCoverageAsync(string path, bool omitProject = false, string? defect = null)
        {
            if (defect == "malformed-json")
            {
                await File.WriteAllTextAsync(path, "{");
                return;
            }

            object mergeExitCode = defect == "failed-merge" ? 1 : 0;
            object projectExitCode = defect == "malformed-project-exit-code" ? "0" : defect == "nonzero-project-exit" ? 1 : 0;
            var projects = (omitProject ? _projects[..1] : _projects).Select((project, index) => new
            {
                project = defect == "wrong-project-inventory" && index == 0 ? "tests/Unexpected.Tests/Unexpected.Tests.csproj" : project,
                exitCode = projectExitCode,
                executionStatus = defect == "failed-project" ? "failed" : "completed",
                coverageArtifactStatus = defect == "missing-coverage-artifact" ? "missing" : "produced",
            });
            var timings = new
            {
                configuration = defect == "debug-configuration" ? "Debug" : "Release",
                coverageDriver = defect == "wrong-coverage-driver" ? "test-host" : "collector",
                merge = new { exitCode = mergeExitCode },
                artifacts = new { coverageFiles = defect == "wrong-coverage-count" ? _projects.Length - 1 : omitProject ? 1 : _projects.Length },
                projects,
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(timings));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
