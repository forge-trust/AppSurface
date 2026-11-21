using System.Text.Json;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

// Keep the orchestration contracts in the native coverage collector's test project.
// Retained collector method records must observe these branches, not just the normalized primary report.
[Collection("CoverageGate process state")]
public sealed class CoverageFanInSolutionCoverageProducerTests
{
    private const string NativeV3DiffBase = "cccccccccccccccccccccccccccccccccccccccc";

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
        var exclusiveProjects = run.Arguments.Zip(run.Arguments.Skip(1))
            .Where(pair => pair.First == "--exclusive-test-project").Select(pair => pair.Second).ToArray();
        Assert.Equal(9, exclusiveProjects.Length);
        Assert.Contains("ForgeTrust.AppSurface.Durable.CloudTasks.Tests.csproj", exclusiveProjects);
        Assert.Equal("2", ValueAfter(run.Arguments, "--parallelism"));
        Assert.Equal("longest-first", ValueAfter(run.Arguments, "--schedule"));
        Assert.DoesNotContain("--schedule-timings", run.Arguments);
        Assert.Contains("--require-non-sandbox", run.Arguments);
        Assert.Equal("proof/aggregate-gate.json", Assert.Single(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "aggregate-gate").RelativePath);
        Assert.Contains(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "result");
        Assert.Contains(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "transcript");
        Assert.DoesNotContain(fixture.Writer.WrittenArtifacts, x => x.LogicalName == "patch-gate");
    }

    [Fact]
    public async Task ProduceAsync_UsesPriorTimingsOnlyForScheduling()
    {
        using var fixture = new Fixture();
        var history = Path.Join(fixture.Root, "prior-timings.json");
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.CreateWithSettings(new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, fixture.Output)
        { CoverageScheduleTimingsPath = history }, async (request, _) =>
        {
            commands.Add(request);
            await fixture.CompleteCommandAsync(request);
            return Success();
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        var run = Assert.Single(commands, command => command.Arguments.Contains("--solution"));
        Assert.Equal("longest-first", ValueAfter(run.Arguments, "--schedule"));
        Assert.Equal(history, ValueAfter(run.Arguments, "--schedule-timings"));
        Assert.All(commands.Where(command => command.Arguments.Contains("gate")),
            command => Assert.DoesNotContain(history, command.Arguments));
    }

    [Fact]
    public async Task CollectAsync_HandsOffCompleteCollectionWithoutClaimAndProducerStillRunsGates()
    {
        using var fixture = new Fixture();
        await WithWorkflowAsync(async () =>
        {
            var collection = Path.Join(fixture.Root, "collection");
            var settings = new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, collection);
            var commands = new List<EvidenceCommandRequest>();
            var collector = fixture.CreateWithSettings(settings, async (request, _) =>
            {
                commands.Add(request);
                await fixture.CompleteCommandAsync(request);
                return Success();
            });

            await collector.CollectAsync(CancellationToken.None);

            Assert.Single(commands, command => command.Arguments.Contains("--solution"));
            Assert.DoesNotContain(commands, command => command.Arguments.Contains("gate"));
            Assert.Empty(fixture.Writer.WrittenArtifacts);
            Assert.False(File.Exists(Path.Join(collection, "evidence-manifest.json")));
            Assert.True(File.Exists(Path.Join(collection, "coverage", "collection-receipt.json")));
            commands.Clear();
            var producer = fixture.CreateWithSettings(new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, fixture.Output)
            { CoverageCollectionDirectory = collection }, async (request, _) =>
            {
                commands.Add(request);
                await fixture.CompleteCommandAsync(request);
                return Success();
            });

            var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
            Assert.DoesNotContain(commands, command => command.Arguments.Contains("--solution"));
            Assert.Single(commands, command => command.Arguments.Contains("gate"));
            Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "aggregate-gate");
        });
    }

    [Fact]
    public async Task CollectAsync_UsesInstalledCliWithoutNativeInputsWhenProvided()
    {
        using var fixture = new Fixture();
        await WithWorkflowAsync(async () =>
        {
            var collection = Path.Join(fixture.Root, "collection");
            var installedCli = Path.Join(fixture.Root, "appsurface");
            await File.WriteAllTextAsync(installedCli, "installed coverage CLI");
            var settings = new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, collection)
            { CoverageCliPath = installedCli };
            var commands = new List<EvidenceCommandRequest>();
            var collector = fixture.CreateWithSettings(settings, async (request, _) =>
            {
                commands.Add(request);
                await fixture.CompleteCommandAsync(request);
                return Success();
            });

            await collector.CollectAsync(CancellationToken.None);

            var run = Assert.Single(commands, command => command.Arguments.Contains("--solution"));
            Assert.Equal(installedCli, run.FileName);
            Assert.Equal("coverage", run.Arguments[0]);
            Assert.Equal("run", run.Arguments[1]);
            Assert.DoesNotContain("--project", run.Arguments);
            Assert.DoesNotContain("--native-inputs", run.Arguments);
            Assert.True(File.Exists(Path.Join(collection, "coverage", "collection-receipt.json")));
        });
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("missing")]
    public async Task CollectAsync_RejectsInvalidInstalledCliBeforeExecution(string defect)
    {
        using var fixture = new Fixture();
        var installedCli = defect == "relative" ? "appsurface" : Path.Join(fixture.Root, "missing-appsurface");
        var settings = new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, Path.Join(fixture.Root, "collection"))
        { CoverageCliPath = installedCli };
        var collector = fixture.CreateWithSettings(settings, (_, _) =>
            throw new Xunit.Sdk.XunitException("An invalid installed CLI must not execute."));

        await Assert.ThrowsAsync<ArgumentException>(() => collector.CollectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProduceAsync_RejectsInvalidImportedInventoryEvenWithMatchingReceipt()
    {
        using var fixture = new Fixture();
        await WithWorkflowAsync(async () =>
        {
            var collection = Path.Join(fixture.Root, "collection");
            var coverage = Path.Join(collection, "coverage");
            Directory.CreateDirectory(coverage);
            await File.WriteAllTextAsync(Path.Join(coverage, "coverage.cobertura.xml"), "<coverage />");
            await fixture.WriteCoverageAsync(Path.Join(coverage, "timings.json"), omitProject: true);
            var run = new[] { "GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT" }
                .ToDictionary(key => key, Environment.GetEnvironmentVariable);
            await SolutionCoverageHandoff.WriteAsync(coverage, new(Fixture.Commit, new string('b', 40), "10.0.100"), run, CancellationToken.None);
            var commands = new List<EvidenceCommandRequest>();
            var producer = fixture.CreateWithSettings(new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, fixture.Output)
            { CoverageCollectionDirectory = collection }, (request, _) =>
            {
                commands.Add(request);
                return Task.FromResult(Success());
            });

            var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
            Assert.Empty(result.SatisfiedAssertionIds);
            Assert.DoesNotContain(commands, command => command.Arguments.Contains("gate"));
        });
    }

    [Theory]
    [InlineData("existing-output")]
    [InlineData("local-patch")]
    [InlineData("native")]
    [InlineData("collection")]
    [InlineData("dirty")]
    [InlineData("incomplete")]
    [InlineData("failed-command")]
    public async Task CollectAsync_RejectsInvalidOrUnsuccessfulCollectionWithoutReceipt(string defect)
    {
        using var fixture = new Fixture();
        await WithWorkflowAsync(async () =>
        {
            var output = defect == "existing-output" ? fixture.Output : Path.Join(fixture.Root, "collection");
            var settings = new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, output,
                NativeCoverageDirectory: defect == "native" ? "native" : null, DiffFile: defect == "local-patch" ? "patch" : null)
            { CoverageCollectionDirectory = defect == "collection" ? "previous" : null };
            if (defect == "dirty") fixture.GitStatusResponses.Enqueue(" M source.cs");
            var collectionCommands = 0;
            var collector = fixture.CreateWithSettings(settings, async (request, _) =>
            {
                Assert.Contains("--solution", request.Arguments);
                collectionCommands++;
                if (defect == "failed-command") return new EvidenceCommandResult(1, "", "failed", 1);
                await fixture.CompleteCommandAsync(request);
                if (defect == "incomplete")
                    await fixture.WriteCoverageAsync(Path.Join(output, "coverage", "timings.json"), omitProject: true);
                return Success();
            });

            if (defect == "failed-command")
                await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CollectAsync(CancellationToken.None));
            else if (defect is "dirty" or "incomplete")
                await Assert.ThrowsAsync<InvalidDataException>(() => collector.CollectAsync(CancellationToken.None));
            else
                await Assert.ThrowsAsync<ArgumentException>(() => collector.CollectAsync(CancellationToken.None));

            Assert.Equal(defect is "failed-command" or "incomplete" ? 1 : 0, collectionCommands);
            Assert.False(File.Exists(Path.Join(output, "coverage", "collection-receipt.json")));
            Assert.Empty(fixture.Writer.WrittenArtifacts);
        });
    }

    private static async Task WithWorkflowAsync(Func<Task> action)
    {
        var values = new Dictionary<string, string>
        { ["GITHUB_REPOSITORY"] = "forge-trust/AppSurface", ["GITHUB_RUN_ID"] = "123", ["GITHUB_RUN_ATTEMPT"] = "1" };
        var original = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
            await action();
        }
        finally
        {
            foreach (var (key, value) in original) Environment.SetEnvironmentVariable(key, value);
        }
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
    public async Task ProduceAsync_RejectsCollectedCiCoverageForLocalPatch()
    {
        using var fixture = new Fixture();
        await WithWorkflowAsync(async () =>
        {
            var collection = TestPathUtils.PathUnder(fixture.Root, "collection");
            var collector = fixture.CreateWithSettings(new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, collection),
                async (request, _) =>
                {
                    await fixture.CompleteCommandAsync(request);
                    return Success();
                });
            await collector.CollectAsync(CancellationToken.None);
            var settings = new RepositoryCheckSettings(fixture.Root, fixture.Dotnet, fixture.Output,
                DiffFile: TestPathUtils.PathUnder(fixture.Root, "local.patch"))
            {
                CoverageCollectionDirectory = collection,
                VerifyLocalSourceAsync = _ => Task.CompletedTask,
            };
            var commands = new List<EvidenceCommandRequest>();
            var producer = fixture.CreateWithSettings(settings, async (request, _) =>
            {
                commands.Add(request);
                await fixture.CompleteCommandAsync(request);
                return Success();
            });

            var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

            await AssertFailedWithoutClaimsAsync(fixture, result, "collection");
            Assert.DoesNotContain(commands, command => command.Arguments.Contains("--solution") || command.Arguments.Contains("gate"));
        });
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
    [InlineData("success")]
    [InlineData("missing-verify")]
    [InlineData("not-verified")]
    [InlineData("not-accepted")]
    [InlineData("delivery-failed")]
    [InlineData("historical")]
    [InlineData("wrong-policy")]
    [InlineData("wrong-source")]
    [InlineData("wrong-run")]
    [InlineData("altered-gate")]
    [InlineData("failed-aggregate")]
    [InlineData("failed-patch")]
    public async Task ProduceAsync_RequiresDeliveredNativeV2AcceptanceAndIndependentVerification(string defect)
    {
        using var fixture = new Fixture();
        var values = new Dictionary<string, string?>
        {
            ["GITHUB_REPOSITORY"] = "forge-trust/appsurface",
            ["GITHUB_RUN_ID"] = "12345",
            ["GITHUB_RUN_ATTEMPT"] = "2",
        };
        var previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
        try
        {
            var settings = fixture.NativeSettings("base-commit");
            foreach (var platform in new[] { "linux", "windows", "darwin" })
                Directory.CreateDirectory(Path.Join(settings.NativeCoverageDirectory!, platform));
            var commands = new List<EvidenceCommandRequest>();
            var producer = fixture.CreateWithSettings(settings, async (request, _) =>
            {
                commands.Add(request);
                if (request.Arguments.Contains("fanin"))
                {
                    Assert.Equal(fixture.Dotnet, request.FileName);
                    Assert.Equal(settings.CoverageCliPath, ValueAfter(request.Arguments, "--cli"));
                    Assert.Equal(settings.DiffBase, ValueAfter(request.Arguments, "--diff-base"));
                    Assert.Equal(settings.NativeCoverageDirectory, ValueAfter(request.Arguments, "--native-inputs"));
                    await fixture.WriteNativeV2ProofAsync(ValueAfter(request.Arguments, "--output"), defect);
                }
                else
                {
                    Assert.Equal(settings.CoverageCliPath, request.FileName);
                    await fixture.CompleteCommandAsync(request);
                }
                return Success();
            });
            var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);
            Assert.Single(commands, command => command.Arguments.Contains("--solution"));
            Assert.Single(commands, command => command.Arguments.Contains("fanin"));
            if (defect == "success")
            {
                Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
                Assert.Equal(["coverage/solution-complete@1", "coverage/thresholds@1"], result.SatisfiedAssertionIds);
                Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "aggregate-gate");
                Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "patch-gate");
                using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(fixture.Root, "artifacts/proof/result.json")));
                Assert.Equal(64, proof.RootElement.GetProperty("nativeFanInProofSha256").GetString()!.Length);
            }
            else await AssertFailedWithoutClaimsAsync(fixture, result, "native-fan-in");
        }
        finally { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); }
    }

    [Theory]
    [InlineData("measured")]
    [InlineData("both-not-applicable")]
    [InlineData("mixed")]
    [InlineData("exact-thresholds")]
    [InlineData("repeating-ratio")]
    [InlineData("primary-below-threshold")]
    public async Task ProduceAsync_AcceptsVerifiedNativeV3ApplicabilityAndComposedThresholds(string scenario)
    {
        using var fixture = new Fixture();
        await WithWorkflowAsync(async () =>
        {
            var settings = fixture.NativeSettings(NativeV3DiffBase) with { NativeCoverageContractVersion = 3 };
            fixture.CreateNativeCaptureDirectories(settings);
            var commands = new List<EvidenceCommandRequest>();
            var producer = fixture.CreateWithSettings(settings, async (request, _) =>
            {
                commands.Add(request);
                if (request.Arguments.Contains("fanin"))
                {
                    Assert.Equal(fixture.Dotnet, request.FileName);
                    Assert.Equal(settings.CoverageCliPath, ValueAfter(request.Arguments, "--cli"));
                    Assert.Equal(NativeV3DiffBase, ValueAfter(request.Arguments, "--diff-base"));
                    Assert.Equal(settings.NativeCoverageDirectory, ValueAfter(request.Arguments, "--native-inputs"));
                    Assert.Equal("3", ValueAfter(request.Arguments, "--contract-version"));
                    Assert.Equal("appsurface-visual-baseline-png-v1", ValueAfter(request.Arguments, "--diff-policy-id"));
                    await fixture.WriteNativeV3ProofAsync(ValueAfter(request.Arguments, "--output"), scenario);
                }
                else await fixture.CompleteCommandAsync(request);
                return Success();
            });

            var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
            Assert.Equal(["coverage/solution-complete@1", "coverage/thresholds@1"], result.SatisfiedAssertionIds);
            Assert.Equal(3, Directory.GetDirectories(settings.NativeCoverageDirectory!).Length);
            Assert.Single(commands, command => command.Arguments.Contains("--solution"));
            var fanIn = Assert.Single(commands, command => command.Arguments.Contains("fanin"));
            Assert.Equal("3", ValueAfter(fanIn.Arguments, "--contract-version"));
            Assert.Equal(NativeV3DiffBase, ValueAfter(fanIn.Arguments, "--diff-base"));
            Assert.Equal("appsurface-visual-baseline-png-v1", ValueAfter(fanIn.Arguments, "--diff-policy-id"));

            var fanInOutput = Path.Join(fixture.Output, "coverage-fanin");
            using var applicability = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Join(fanInOutput, "accepted", "patch-applicability.json")));
            var claim = applicability.RootElement;
            Assert.Equal(3, claim.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("coverlet-collector-cobertura-v1", claim.GetProperty("reportProfileId").GetString());
            using (var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Join(fanInOutput, "accepted", "diff-metadata.json"))))
                Assert.Equal("appsurface-visual-baseline-png-v1", metadata.RootElement.GetProperty("diffPolicyId").GetString());
            if (scenario == "both-not-applicable")
                foreach (var set in new[] { "primary", "composed" })
                    foreach (var dimension in new[] { "line", "branch" })
                    {
                        var metric = claim.GetProperty(set).GetProperty(dimension);
                        Assert.Equal("notApplicable", metric.GetProperty("outcome").GetString());
                        Assert.Equal("no-reported-patch-points", metric.GetProperty("reason").GetString());
                        Assert.Equal(0, metric.GetProperty("measurable").GetInt32());
                        Assert.Equal(0, metric.GetProperty("covered").GetInt32());
                        Assert.Equal(JsonValueKind.Null, metric.GetProperty("percent").ValueKind);
                    }
            if (scenario == "mixed")
            {
                Assert.Equal("notApplicable", claim.GetProperty("primary").GetProperty("line").GetProperty("outcome").GetString());
                Assert.Equal("measured", claim.GetProperty("primary").GetProperty("branch").GetProperty("outcome").GetString());
                Assert.Equal("measured", claim.GetProperty("composed").GetProperty("line").GetProperty("outcome").GetString());
            }
            if (scenario == "exact-thresholds")
            {
                var composed = claim.GetProperty("composed");
                Assert.Equal(94.5m, composed.GetProperty("line").GetProperty("percent").GetDecimal());
                Assert.Equal(84.5m, composed.GetProperty("branch").GetProperty("percent").GetDecimal());
            }
            if (scenario == "repeating-ratio")
                Assert.Equal(95.2381m, claim.GetProperty("primary").GetProperty("line").GetProperty("percent").GetDecimal());
            if (scenario == "primary-below-threshold")
            {
                Assert.True(claim.GetProperty("primary").GetProperty("line").GetProperty("percent").GetDecimal() < 94.5m);
                Assert.True(claim.GetProperty("composed").GetProperty("line").GetProperty("percent").GetDecimal() >= 94.5m);
            }

            foreach (var operation in new[] { "accept", "verify" })
            {
                using var terminal = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Join(fanInOutput, operation + "-result.json")));
                Assert.Equal(3, terminal.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("succeeded", terminal.RootElement.GetProperty("result").GetProperty("outcome").GetString());
            }
            using var producerResult = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Join(fixture.Root, "artifacts", "proof", "result.json")));
            Assert.Equal(3, producerResult.RootElement.GetProperty("nativeFanInContractVersion").GetInt32());
            Assert.Equal(64, producerResult.RootElement.GetProperty("nativeFanInProofSha256").GetString()!.Length);
            Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "patch-gate");
        });
    }

    [Theory]
    [InlineData("wrong-terminal-version")]
    [InlineData("wrong-acceptance-version")]
    [InlineData("wrong-acceptance-intent")]
    [InlineData("missing-verify")]
    [InlineData("not-verified")]
    [InlineData("unverified")]
    [InlineData("wrong-result-outcome")]
    [InlineData("false-not-applicable-measured")]
    [InlineData("invalid-primary-not-applicable")]
    [InlineData("invalid-primary-metric")]
    [InlineData("invalid-composed-not-applicable")]
    [InlineData("invalid-composed-metric")]
    [InlineData("below-threshold")]
    [InlineData("result-primary-mismatch")]
    [InlineData("result-composed-mismatch")]
    [InlineData("primary-report-mismatch")]
    [InlineData("composed-report-mismatch")]
    [InlineData("tampered-applicability")]
    [InlineData("tampered-diff")]
    [InlineData("tampered-diff-metadata")]
    [InlineData("tampered-primary-gate")]
    [InlineData("tampered-composed-gate")]
    [InlineData("tampered-composition-receipt")]
    [InlineData("metadata-schema-version")]
    [InlineData("metadata-requested-base")]
    [InlineData("metadata-head-commit")]
    [InlineData("wrong-diff-policy")]
    [InlineData("metadata-merge-base-short")]
    [InlineData("metadata-merge-base-invalid-hex")]
    [InlineData("applicability-schema-version")]
    [InlineData("applicability-report-profile")]
    [InlineData("applicability-policy-digest")]
    [InlineData("applicability-source")]
    [InlineData("applicability-run")]
    [InlineData("applicability-diff-sha256")]
    [InlineData("applicability-diff-metadata-sha256")]
    [InlineData("negative-changed-lines")]
    [InlineData("negative-covered-lines")]
    public async Task ProduceAsync_RejectsInvalidOrTamperedNativeV3ApplicabilityEvidence(string defect)
    {
        using var fixture = new Fixture();
        await WithWorkflowAsync(async () =>
        {
            var settings = fixture.NativeSettings(NativeV3DiffBase) with { NativeCoverageContractVersion = 3 };
            fixture.CreateNativeCaptureDirectories(settings);
            var commands = new List<EvidenceCommandRequest>();
            var producer = fixture.CreateWithSettings(settings, async (request, _) =>
            {
                commands.Add(request);
                if (request.Arguments.Contains("fanin"))
                {
                    Assert.Equal("appsurface-visual-baseline-png-v1", ValueAfter(request.Arguments, "--diff-policy-id"));
                    await fixture.WriteNativeV3ProofAsync(ValueAfter(request.Arguments, "--output"), defect);
                }
                else await fixture.CompleteCommandAsync(request);
                return Success();
            });

            var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

            await AssertFailedWithoutClaimsAsync(fixture, result, "native-fan-in");
            Assert.Single(commands, command => command.Arguments.Contains("--solution"));
            var fanIn = Assert.Single(commands, command => command.Arguments.Contains("fanin"));
            Assert.Equal("3", ValueAfter(fanIn.Arguments, "--contract-version"));
            Assert.Equal("appsurface-visual-baseline-png-v1", ValueAfter(fanIn.Arguments, "--diff-policy-id"));
            Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "result");
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("main")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task ProduceAsync_RejectsMissingOrInvalidNativeV3PinnedDiffBase(string? diffBase)
    {
        using var fixture = new Fixture();
        var settings = fixture.NativeSettings(diffBase) with { NativeCoverageContractVersion = 3 };
        var commands = new List<EvidenceCommandRequest>();
        var producer = fixture.CreateWithSettings(settings, (request, _) =>
        {
            commands.Add(request);
            return Task.FromResult(Success());
        });

        var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

        await AssertFailedWithoutClaimsAsync(fixture, result, "settings");
        Assert.Empty(commands);
    }

    [Theory]
    [InlineData("Linux", "linux", false)]
    [InlineData("Windows", "windows", false)]
    [InlineData("FreeBSD", "darwin", false)]
    [InlineData("Linux", "linux", true)]
    public async Task ProduceAsync_PassesNativeInputsThroughInstalledCliBeforePublishingCoverageClaims(
        string runnerOs, string expectedPlatform, bool importedCollection)
    {
        using var fixture = new Fixture();
        var run = new Dictionary<string, string?>
        {
            ["GITHUB_REPOSITORY"] = "forge-trust/appsurface",
            ["GITHUB_RUN_ID"] = "12345",
            ["GITHUB_RUN_ATTEMPT"] = "2",
            ["RUNNER_OS"] = runnerOs,
        };
        var previous = run.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        foreach (var (key, value) in run) Environment.SetEnvironmentVariable(key, value);
        try
        {
            var nativeRoot = TestPathUtils.PathUnder(fixture.Root, "native-captures");
            foreach (var platform in new[] { "linux", "windows", "darwin" })
                Directory.CreateDirectory(Path.Join(nativeRoot, platform));
            Assert.True(Directory.Exists(Path.Join(nativeRoot, expectedPlatform)));

            var collection = Path.Join(fixture.Root, "collection");
            if (importedCollection)
            {
                var coverage = Path.Join(collection, "coverage");
                Directory.CreateDirectory(coverage);
                await File.WriteAllTextAsync(Path.Join(coverage, "coverage.cobertura.xml"), "<coverage />");
                await fixture.WriteCoverageAsync(Path.Join(coverage, "timings.json"));
                // RUNNER_OS selects the fixture host; handoff identity contains only the workflow provenance fields.
                var workflowRun = run.Where(pair => pair.Key != "RUNNER_OS")
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                await SolutionCoverageHandoff.WriteAsync(coverage,
                    new(Fixture.Commit, new string('b', 40), "10.0.100"), workflowRun, CancellationToken.None);
            }

            var settings = fixture.NativeSettings("base-commit") with
            { CoverageCollectionDirectory = importedCollection ? collection : null };
            var commands = new List<EvidenceCommandRequest>();
            var producer = fixture.CreateWithSettings(settings, async (request, _) =>
            {
                commands.Add(request);
                if (request.Arguments.Contains("fanin"))
                {
                    Assert.Equal(fixture.Dotnet, request.FileName);
                    Assert.Equal(settings.CoverageCliPath, ValueAfter(request.Arguments, "--cli"));
                    Assert.Equal(settings.DiffBase, ValueAfter(request.Arguments, "--diff-base"));
                    Assert.Equal(nativeRoot, ValueAfter(request.Arguments, "--native-inputs"));
                    Assert.Equal(Path.Join(fixture.Output, "coverage-fanin"), ValueAfter(request.Arguments, "--output"));
                    await fixture.WriteNativeV2ProofAsync(ValueAfter(request.Arguments, "--output"), "success");
                }
                else
                {
                    Assert.Equal(settings.CoverageCliPath, request.FileName);
                    await fixture.CompleteCommandAsync(request);
                }
                return Success();
            });

            var result = await producer.ProduceAsync(fixture.Context, CancellationToken.None);

            Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
            Assert.Equal(["coverage/solution-complete@1", "coverage/thresholds@1"], result.SatisfiedAssertionIds);
            Assert.Equal(importedCollection, !commands.Any(command => command.Arguments.Contains("--solution")));
            var fanIn = Assert.Single(commands, command => command.Arguments.Contains("fanin"));
            Assert.Equal(settings.CoverageCliPath, ValueAfter(fanIn.Arguments, "--cli"));
            Assert.Equal(Path.Join(fixture.Output, "coverage", "coverage.cobertura.xml"), ValueAfter(fanIn.Arguments, "--primary"));
            Assert.Equal(Path.Join(fixture.Output, "coverage", "timings.json"), ValueAfter(fanIn.Arguments, "--timings"));
            Assert.Equal(nativeRoot, ValueAfter(fanIn.Arguments, "--native-inputs"));
            Assert.DoesNotContain("--diff-policy-id", fanIn.Arguments);
            Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "aggregate-gate");
            Assert.Contains(fixture.Writer.WrittenArtifacts, artifact => artifact.LogicalName == "patch-gate");
            using var proof = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Join(fixture.Root, "artifacts", "proof", "result.json")));
            Assert.Equal(64, proof.RootElement.GetProperty("nativeFanInProofSha256").GetString()!.Length);
        }
        finally { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative-cli")]
    [InlineData("missing-cli")]
    public async Task ProduceAsync_RejectsMissingOrInvalidInstalledNativeCliBeforeExecution(string? cli)
    {
        using var fixture = new Fixture();
        var settings = fixture.NativeSettings("base-commit") with
        {
            CoverageCliPath = cli == "missing-cli" ? Path.Join(fixture.Root, "missing-appsurface") : cli,
        };
        var producer = fixture.CreateWithSettings(settings, (_, _) => throw new Xunit.Sdk.XunitException("Invalid native CLI must not execute."));
        await AssertFailedWithoutClaimsAsync(fixture, await producer.ProduceAsync(fixture.Context, CancellationToken.None), "settings");
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
        private readonly string _root = CreateRoot();

        private static string CreateRoot()
        {
            var root = Directory.CreateTempSubdirectory("solution-coverage-producer-").FullName;
            return OperatingSystem.IsMacOS() && (root.StartsWith("/var/", StringComparison.Ordinal) || root.StartsWith("/tmp/", StringComparison.Ordinal))
                ? "/private" + root : root;
        }
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
            new(_root, _dotnet, _output, diffBase, TestPathUtils.PathUnder(_root, "native-captures"), diffFile, _dotnet);

        public void CreateNativeCaptureDirectories(RepositoryCheckSettings settings)
        {
            foreach (var platform in new[] { "linux", "windows", "darwin" })
                Directory.CreateDirectory(Path.Join(settings.NativeCoverageDirectory!, platform));
        }

        public async Task WriteNativeV3ProofAsync(string output, string scenario)
        {
            // This command-orchestration fixture supplies the installed adapter's retained v3 receipt shape.
            var accepted = Path.Join(output, "accepted");
            var composed = Path.Join(output, "composed");
            Directory.CreateDirectory(accepted);
            Directory.CreateDirectory(composed);

            static object Metric(string outcome, string reason, int changedLines, int measurable, int covered, decimal? percent) =>
                new { outcome, reason, changedLines, measurable, covered, percent };

            var notApplicable = Metric("notApplicable", "no-reported-patch-points", 1, 0, 0, null);
            var measuredLine = Metric("measured", "reported-points", 1, 100, 99, 99m);
            var measuredBranch = Metric("measured", "reported-points", 1, 100, 90, 90m);
            var lineBelow = Metric("measured", "reported-points", 1, 100, 94, 94m);
            var branchBelow = Metric("measured", "reported-points", 1, 100, 80, 80m);
            var repeatingRatioLine = Metric("measured", "reported-points", 1, 21, 20, 95.2381m);
            var primaryLine = scenario switch
            {
                "both-not-applicable" or "mixed" => notApplicable,
                "invalid-primary-not-applicable" => Metric("notApplicable", "no-reported-patch-points", 1, 0, 1, null),
                "primary-below-threshold" => Metric("measured", "reported-points", 1, 100, 90, 90m),
                "invalid-primary-metric" => Metric("measured", "reported-points", 1, 100, 98, 99m),
                "repeating-ratio" => repeatingRatioLine,
                "negative-changed-lines" => Metric("measured", "reported-points", -1, 100, 99, 99m),
                "negative-covered-lines" => Metric("measured", "reported-points", 1, 100, -1, 99m),
                _ => measuredLine,
            };
            var primaryBranch = scenario switch
            {
                "both-not-applicable" => notApplicable,
                "primary-below-threshold" => branchBelow,
                _ => measuredBranch,
            };
            var composedLine = scenario switch
            {
                "both-not-applicable" => notApplicable,
                "invalid-composed-not-applicable" => Metric("notApplicable", "no-reported-patch-points", 1, 0, 1, null),
                "below-threshold" => lineBelow,
                "false-not-applicable-measured" => Metric("notApplicable", "no-reported-patch-points", 1, 1, 1, 100m),
                "unverified" => Metric("unverified", "verification-failed", 1, 0, 0, null),
                "exact-thresholds" => Metric("measured", "reported-points", 1, 200, 189, 94.5m),
                "repeating-ratio" => repeatingRatioLine,
                "invalid-composed-metric" => Metric("measured", "reported-points", 1, 100, 98, 99m),
                _ => measuredLine,
            };
            var composedBranch = scenario switch
            {
                "both-not-applicable" => notApplicable,
                "exact-thresholds" => Metric("measured", "reported-points", 1, 200, 169, 84.5m),
                _ => measuredBranch,
            };
            var primary = new { line = primaryLine, branch = primaryBranch };
            var composedMetrics = new { line = composedLine, branch = composedBranch };

            var patchBytes = System.Text.Encoding.UTF8.GetBytes(
                $"diff --git a/src/Example.cs b/src/Example.cs\nindex {new string('d', 40)}..{new string('e', 40)} 100644\n--- a/src/Example.cs\n+++ b/src/Example.cs\n@@ -1 +1 @@\n-before\n+after\n");
            var diffSha256 = Hash(patchBytes);
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = scenario == "metadata-schema-version" ? 2 : 3,
                requestedBaseCommit = scenario == "metadata-requested-base" ? new string('a', 40) : NativeV3DiffBase,
                mergeBaseCommit = scenario switch
                {
                    "metadata-merge-base-short" => new string('a', 39),
                    "metadata-merge-base-invalid-hex" => new string('A', 40),
                    _ => NativeV3DiffBase,
                },
                headCommit = scenario == "metadata-head-commit" ? new string('f', 40) : Commit,
                diffPolicyId = scenario == "wrong-diff-policy"
                    ? "appsurface-git-patch-v1" : "appsurface-visual-baseline-png-v1",
                diffSha256,
                empty = false,
                changes = new[]
                {
                    new
                    {
                        kind = "modified",
                        oldPath = "src/Example.cs",
                        newPath = "src/Example.cs",
                        addedRanges = new[] { new { start = 1, count = 1 } },
                    },
                },
            });
            var diffMetadataSha256 = Hash(metadataBytes);
            var source = new { commit = Commit, tree = new string('b', 40), sdk = "10.0.100", configuration = "Release" };
            var run = new { runScope = "forge-trust/AppSurface", runId = "123", attempt = 1 };
            var claimSource = new
            {
                commit = scenario == "applicability-source" ? new string('f', 40) : Commit,
                tree = new string('b', 40),
                sdk = "10.0.100",
                configuration = "Release",
            };
            var claimRun = new
            {
                runScope = "forge-trust/AppSurface",
                runId = scenario == "applicability-run" ? "124" : "123",
                attempt = 1,
            };

            object primaryReportLine = scenario == "primary-report-mismatch"
                ? Metric("measured", "reported-points", 1, 100, 98, 98m) : primaryLine;
            object composedReportLine = scenario == "composed-report-mismatch"
                ? Metric("measured", "reported-points", 1, 100, 98, 98m) : composedLine;
            var primaryGateBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                passed = true,
                tolerancePercent = .5m,
                thresholds = new { line = 95m, branch = 85m, patchLine = 95m, patchBranch = 85m },
                patchLineMode = "codecov",
                line = new { percent = 99m },
                branch = new { percent = 90m },
                patchLine = primaryReportLine,
                patchBranch = primaryBranch,
            });
            var composedGateBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                passed = true,
                tolerancePercent = .5m,
                thresholds = new { line = 95m, branch = 85m, patchLine = 95m, patchBranch = 85m },
                patchLineMode = "codecov",
                line = new { percent = 99m },
                branch = new { percent = 90m },
                patchLine = composedReportLine,
                patchBranch = composedBranch,
            });
            var compositionReceiptBytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 3, outcome = "succeeded" });
            var applicabilityBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = scenario == "applicability-schema-version" ? 2 : 3,
                reportProfileId = scenario == "applicability-report-profile" ? "another-profile" : "coverlet-collector-cobertura-v1",
                policyDigest = scenario == "applicability-policy-digest" ? "another-policy" : "policy",
                source = claimSource,
                run = claimRun,
                diffSha256 = scenario == "applicability-diff-sha256" ? new string('a', 64) : diffSha256,
                diffMetadataSha256 = scenario == "applicability-diff-metadata-sha256" ? new string('a', 64) : diffMetadataSha256,
                primary,
                composed = composedMetrics,
            });

            await File.WriteAllBytesAsync(Path.Join(accepted, "diff.patch"), patchBytes);
            await File.WriteAllBytesAsync(Path.Join(accepted, "diff-metadata.json"), metadataBytes);
            await File.WriteAllBytesAsync(Path.Join(accepted, "patch-applicability.json"), applicabilityBytes);
            await File.WriteAllBytesAsync(Path.Join(accepted, "primary-gate.json"), primaryGateBytes);
            await File.WriteAllBytesAsync(Path.Join(accepted, "composed-gate.json"), composedGateBytes);
            await File.WriteAllBytesAsync(Path.Join(composed, "verification.json"), compositionReceiptBytes);

            object resultPrimary = scenario == "result-primary-mismatch"
                ? new { line = Metric("measured", "reported-points", 1, 100, 98, 98m), branch = primaryBranch }
                : primary;
            object resultComposed = scenario == "result-composed-mismatch"
                ? new { line = Metric("measured", "reported-points", 1, 100, 98, 98m), branch = composedBranch }
                : composedMetrics;
            var resultApplicability = new { primary = resultPrimary, composed = resultComposed };
            foreach (var operation in new[] { "accept", "verify" })
            {
                if (operation == "verify" && scenario == "missing-verify") continue;
                var terminal = new
                {
                    schemaVersion = scenario == "wrong-terminal-version" && operation == "verify" ? 2 : 3,
                    operation,
                    status = "succeeded",
                    operationOutcome = "succeeded",
                    delivery = new { resultFileCommitted = true },
                    result = new
                    {
                        operation,
                        outcome = scenario == "wrong-result-outcome" && operation == "verify" ? "failed" : "succeeded",
                        verified = scenario != "not-verified",
                        accepted = true,
                        effects = new { cleanupComplete = true },
                        patchOutcome = "PatchAccepted",
                        evaluationIntent = "currentTrustedRun",
                        policyDigest = "policy",
                        patchApplicability = resultApplicability,
                    },
                };
                await File.WriteAllTextAsync(Path.Join(output, operation + "-result.json"), JsonSerializer.Serialize(terminal));
            }

            var acceptance = new
            {
                schemaVersion = scenario == "wrong-acceptance-version" ? 2 : 3,
                evaluationIntent = scenario == "wrong-acceptance-intent" ? "historicalReplay" : "currentTrustedRun",
                patchOutcome = "PatchAccepted",
                policyDigest = "policy",
                source,
                run,
                primaryGateSha256 = Hash(primaryGateBytes),
                composedGateSha256 = Hash(composedGateBytes),
                compositionReceiptSha256 = Hash(compositionReceiptBytes),
                applicabilitySha256 = Hash(applicabilityBytes),
                diffSha256,
                diffMetadataSha256,
            };
            await File.WriteAllTextAsync(Path.Join(accepted, "acceptance.json"), JsonSerializer.Serialize(acceptance));

            var tamperedPath = scenario switch
            {
                "tampered-applicability" => Path.Join(accepted, "patch-applicability.json"),
                "tampered-diff" => Path.Join(accepted, "diff.patch"),
                "tampered-diff-metadata" => Path.Join(accepted, "diff-metadata.json"),
                "tampered-primary-gate" => Path.Join(accepted, "primary-gate.json"),
                "tampered-composed-gate" => Path.Join(accepted, "composed-gate.json"),
                "tampered-composition-receipt" => Path.Join(composed, "verification.json"),
                _ => null,
            };
            if (tamperedPath is not null)
                await File.AppendAllTextAsync(tamperedPath, " ");
        }

        private static string Hash(byte[] bytes) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        public async Task WriteNativeV2ProofAsync(string output, string defect)
        {
            // This is a command-orchestration fixture using the CLI's delivered v2 shape.
            // Native API/CLI semantic verification and real OS execution are tested separately.
            Directory.CreateDirectory(Path.Join(output, "accepted"));
            Directory.CreateDirectory(Path.Join(output, "composed"));
            foreach (var operation in new[] { "accept", "verify" })
            {
                if (operation == "verify" && defect == "missing-verify") continue;
                var terminal = new
                {
                    schemaVersion = 2,
                    operation,
                    status = "succeeded",
                    operationOutcome = "succeeded",
                    delivery = new { resultFileCommitted = defect != "delivery-failed" },
                    result = new
                    {
                        operation,
                        verified = defect != "not-verified",
                        accepted = defect != "not-accepted",
                        effects = new { cleanupComplete = true },
                        patchOutcome = "PatchAccepted",
                        evaluationIntent = defect == "historical" ? "historicalReplay" : "currentTrustedRun",
                        policyDigest = operation == "verify" && defect == "wrong-policy" ? "another-policy" : "policy",
                    },
                };
                await File.WriteAllTextAsync(Path.Join(output, operation + "-result.json"), JsonSerializer.Serialize(terminal));
            }
            foreach (var patch in new[] { false, true })
            {
                var gate = new
                {
                    passed = defect != (patch ? "failed-patch" : "failed-aggregate"),
                    tolerancePercent = .5m,
                    thresholds = new { line = 95m, branch = 85m, patchLine = 95m, patchBranch = 85m },
                    patchLineMode = "codecov",
                    line = new { percent = 99m },
                    branch = new { percent = 90m },
                    patchLine = new { percent = 99m },
                    patchBranch = new { percent = 90m },
                };
                await File.WriteAllTextAsync(Path.Join(output, "accepted", patch ? "composed-gate.json" : "primary-gate.json"), JsonSerializer.Serialize(gate));
            }
            await File.WriteAllTextAsync(Path.Join(output, "composed", "verification.json"), "{\"schemaVersion\":2}");
            async Task<string> Digest(string relative) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, relative))));
            var acceptance = new
            {
                policyDigest = "policy",
                source = new { commit = defect == "wrong-source" ? new string('c', 40) : Commit, tree = new string('b', 40), sdk = "10.0.100", configuration = "Release" },
                run = new { runScope = "forge-trust/appsurface", runId = "12345", attempt = defect == "wrong-run" ? 1 : 2 },
                primaryGateSha256 = await Digest("accepted/primary-gate.json"),
                composedGateSha256 = await Digest("accepted/composed-gate.json"),
                compositionReceiptSha256 = await Digest("composed/verification.json"),
            };
            await File.WriteAllTextAsync(Path.Join(output, "accepted/acceptance.json"), JsonSerializer.Serialize(acceptance));
            if (defect == "altered-gate") await File.AppendAllTextAsync(Path.Join(output, "accepted/primary-gate.json"), " ");
        }

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
