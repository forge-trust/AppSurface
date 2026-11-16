using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageEvidenceReceiptInventoryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(129, false)]
    [InlineData(128, true)]
    public void ReceiptCaptureRejectsIneligibleCountsBeforeInspectingPublications(int count, bool sidecar)
    {
        using var directory = TestDirectory.Create();
        var sidecarPath = Path.Join(directory.Path, "coverage", CoverageArtifactSession.SidecarName);
        if (sidecar)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllText(sidecarPath, "not JSON; an oversized receipt must never read this metadata");
        }
        var result = new CoverageEvidenceExecutionResult(new(true, directory.Path, "coverage.xml"),
            new("coverage.xml", new(1, 1, 100), new(1, 1, 100), 95, 85, true, "gate.json", "gate.md"));
        var publications = new CountOnlyPublications(count);

        var exception = Assert.Throws<CoverageEvidenceInventoryException>(() =>
            CoverageEvidenceInventory.Capture(directory.Path, result, publications, CancellationToken.None));

        Assert.Equal("inventory-file-limit", exception.Reason);
        if (sidecar) Assert.Equal("not JSON; an oversized receipt must never read this metadata", File.ReadAllText(sidecarPath));
        else Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("/coverage/file")]
    [InlineData("coverage/../escape")]
    [InlineData("coverage/./file")]
    [InlineData("coverage//file")]
    [InlineData("coverage/file ")]
    [InlineData("coverage/ file")]
    [InlineData("coverage/file.")]
    [InlineData("coverage/file\n")]
    [InlineData("coverage/CON.txt")]
    [InlineData("coverage/lpt1")]
    [InlineData("coverage/COM¹.txt")]
    [InlineData("coverage/cafe\u0301.txt")]
    [InlineData("coverage/file:stream")]
    [InlineData("coverage\\file")]
    [InlineData("Coverage/file")]
    public void UnsafePathsAreRejectedWithoutNormalization(string path) =>
        Assert.Throws<CoverageEvidenceInventoryException>(() => CoverageEvidenceInventory.ValidatePath(path));

    [Theory]
    [InlineData("duplicate", "inventory-path-collision")]
    [InlineData("case", "inventory-path-collision")]
    [InlineData("parent-case", "inventory-path-collision")]
    [InlineData("order", "inventory-order-invalid")]
    [InlineData("version", "inventory-version-unsupported")]
    [InlineData("digest", "inventory-digest-invalid")]
    [InlineData("size", "inventory-byte-limit")]
    [InlineData("corrupt", "inventory-file-changed")]
    [InlineData("missing", null)]
    [InlineData("linked-file", null)]
    [InlineData("linked-parent", null)]
    public async Task InvalidClosedSetsWriteNothing(string failure, string? expectedReason)
    {
        using var directory = TestDirectory.Create();
        var source = Path.Join(directory.Path, "source");
        Directory.CreateDirectory(Path.Join(source, "coverage"));
        var first = Put(source, "coverage/a.txt", "first"u8.ToArray());
        var second = Put(source, "coverage/b.txt", "second"u8.ToArray());
        CoverageEvidenceInventoryFile[] files = [first, second];
        var version = CoverageEvidenceInventory.CurrentVersion;
        switch (failure)
        {
            case "duplicate": files = [first, first]; break;
            case "case": files = [first, first with { RelativePath = "coverage/A.txt" }]; break;
            case "parent-case":
                files = [first with { RelativePath = "coverage/Reports/a" }, second with { RelativePath = "coverage/reports/b" }];
                break;
            case "order": files = [second, first]; break;
            case "version": version++; break;
            case "digest": files = [first with { Sha256 = "bad" }]; break;
            case "size": files = [first with { LengthBytes = -1 }]; break;
            case "missing": File.Delete(TestPathUtils.PathUnder(source, second.RelativePath)); break;
            case "corrupt": File.WriteAllText(TestPathUtils.PathUnder(source, second.RelativePath), "SECOND"); break;
            case "linked-file":
                File.Delete(TestPathUtils.PathUnder(source, second.RelativePath));
                File.CreateSymbolicLink(TestPathUtils.PathUnder(source, second.RelativePath), TestPathUtils.PathUnder(source, first.RelativePath));
                break;
            case "linked-parent":
                var moved = Path.Join(directory.Path, "moved");
                Directory.Move(Path.Join(source, "coverage"), moved);
                Directory.CreateSymbolicLink(Path.Join(source, "coverage"), moved);
                break;
        }
        var target = Path.Join(directory.Path, "custody");
        var slot = CoverageEvidenceInventory.CreateSlot();
        var writer = new EvidenceArtifactWriter(Declaration(slot), target);
        var write = () => new CoverageEvidenceInventory(version, files).WriteAsync(writer, slot, source, CancellationToken.None);
        if (expectedReason is null)
        {
            await Assert.ThrowsAnyAsync<IOException>(write);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<CoverageEvidenceInventoryException>(write);
            Assert.Equal(expectedReason, exception.Reason);
        }
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData("no-cooperative-contract")]
    [InlineData("missing-inputs")]
    [InlineData("oversized-inventory")]
    [InlineData("oversized-standard-report")]
    [InlineData("more-than-128-files")]
    [InlineData("ordinary-storage-cap")]
    [InlineData("gate-failed")]
    [InlineData("promotion-conflict")]
    [InlineData("failed-gate-promotion-conflict")]
    public async Task ReuseFreshFallbackPreservesCompleteReportsAtFinalRunRoot(string scenario)
    {
        using var directory = TestDirectory.Create();
        var repositoryRoot = FindRepositoryRoot();
        var solution = Path.Join(repositoryRoot, "ForgeTrust.AppSurface.slnx");
        Assert.True(File.Exists(solution));
        const string solutionProjectPath = "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj";

        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        var starter = await workflow.InitializeAsync(Path.Join(directory.Path, "starter"), false, CancellationToken.None);
        var plan = await workflow.ExplainAsync(new EvidencePlanningRequest(
            Path.Join(starter.RootPath, "evidence.policy.json"),
            ["Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceReuseCommandTests.cs"], null), CancellationToken.None);
        var policy = Assert.IsType<EvidencePolicy>(plan.PolicySnapshot);
        var profile = policy.Profiles.Single(item => item.Id == plan.Profile.Id);
        var original = Assert.Single(profile.Producers);
        var slot = CoverageEvidenceInventory.CreateSlot();
        if (scenario == "oversized-inventory") slot = slot with { MaximumBytes = 1 };
        var hasInputs = scenario != "missing-inputs";
        var cooperative = scenario != "no-cooperative-contract";
        var failedGate = scenario is "gate-failed" or "failed-gate-promotion-conflict";
        var promotionConflict = scenario is "promotion-conflict" or "failed-gate-promotion-conflict";
        var expectedAttachments = scenario switch
        { "more-than-128-files" => 129, "ordinary-storage-cap" => 10_001, _ => 1 };
        var declaration = original with
        {
            Inputs = hasInputs
                ? new EvidenceProducerInputs(["Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceReuseCommandTests.cs"], [])
                : null,
            ArtifactSlots = [slot],
            CoverageGate = (original.CoverageGate ?? throw new InvalidOperationException("The starter coverage gate is missing.")) with
            {
                MinPatchLinePercent = null,
                MinPatchBranchPercent = null,
            },
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit)
            {
                CooperativeChildLifetime = cooperative,
            },
            CoverageArtifactRetention = "managed",
        };
        var changedProfile = profile with { Producers = [declaration] };
        var changedPolicy = policy with
        {
            Profiles = policy.Profiles.Select(item => item.Id == changedProfile.Id ? changedProfile : item).ToArray(),
        };
        plan = new EvidencePlanner().Resolve(changedPolicy, plan.ChangedPaths);

        var runRoot = Path.Join(directory.Path, "run");
        var storeRoot = Path.Join(directory.Path, "receipt-store");
        if (promotionConflict)
        {
            Directory.CreateDirectory(Path.Join(runRoot, "coverage"));
            await File.WriteAllTextAsync(Path.Join(runRoot, "coverage", "coverage-gate.md"), "occupied by other bytes");
        }
        var runner = new Runner(solutionProjectPath: solutionProjectPath,
            attachmentCount: expectedAttachments);
        var producer = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(
            new CoverageRunWorkflow(runner, new Generator(scenario == "oversized-standard-report"
                ? CoverageEvidenceInventory.MaximumBytes + 1 : 0, failedGate), TimeProvider.System)));
        var result = await new EvidenceReuseExecutionResolver(producer).RunAsync(plan, null, solution, runRoot,
            verifyReuse: false, observationOnly: false, storeRoot: storeRoot, waitTimeout: TimeSpan.FromSeconds(1),
            output: TextWriter.Null, error: TextWriter.Null, cancellationToken: CancellationToken.None);

        var producerResult = Assert.Single(result.Manifest.ProducerResults);
        Assert.Equal(failedGate || promotionConflict ? EvidenceProducerOutcome.Failed : EvidenceProducerOutcome.Passed,
            producerResult.Outcome);
        Assert.Equal(failedGate || promotionConflict ? EvidenceExecutionVerdict.Incomplete : EvidenceExecutionVerdict.Passed,
            result.Manifest.ExecutionVerdict);
        Assert.Null(producerResult.Artifacts);
        Assert.Equal("suppressed", Assert.Single(result.Decisions).Publication);
        var roots = EvidenceReuseExecutionResolver.GetProducerRoots(runRoot, declaration.Id, useInvocationArtifactRoot: true);
        var executionRoot = roots.Execution;
        var finalCoverageRoot = Path.Join(roots.Artifacts, "coverage");
        var stagedCoverageRoot = Path.Join(executionRoot, "coverage");
        Assert.DoesNotContain("IOException", producerResult.Diagnostic ?? string.Empty, StringComparison.Ordinal);
        if (failedGate)
        {
            Assert.Empty(producerResult.SatisfiedAssertionIds);
            Assert.Contains("Coverage gate did not meet", producerResult.Diagnostic ?? string.Empty, StringComparison.Ordinal);
        }
        if (promotionConflict)
        {
            Assert.Equal("occupied by other bytes", await File.ReadAllTextAsync(Path.Join(finalCoverageRoot, "coverage-gate.md")));
            Assert.True(File.Exists(Path.Join(stagedCoverageRoot, "coverage-gate.md")));
            Assert.Contains("ordinary-report-promotion-unavailable", producerResult.Diagnostic ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains(stagedCoverageRoot, producerResult.Diagnostic ?? string.Empty, StringComparison.Ordinal);
            return;
        }
        Assert.DoesNotContain(executionRoot, producerResult.Diagnostic ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(finalCoverageRoot, producerResult.Diagnostic ?? string.Empty, StringComparison.Ordinal);
        foreach (var report in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt", "reportgenerator-summary.txt" })
        {
            var finalPath = Path.Join(finalCoverageRoot, report);
            var stagedPath = Path.Join(stagedCoverageRoot, report);
            Assert.True(File.Exists(finalPath), $"final report missing for {scenario}: {finalPath}");
            Assert.True(File.Exists(stagedPath), $"staged report missing for {scenario}: {stagedPath}");
            AssertFilesEqual(stagedPath, finalPath);
        }
        Assert.Single(Directory.GetFiles(finalCoverageRoot, CoverageProjectManifest.FileName, SearchOption.AllDirectories));
        var attachments = Directory.GetFiles(finalCoverageRoot, "attachment*.txt", SearchOption.AllDirectories);
        Assert.Equal(expectedAttachments, attachments.Length);
        Assert.Single(Directory.GetFiles(finalCoverageRoot, "junit-coverage-*.xml", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(finalCoverageRoot, "unrelated.txt", SearchOption.AllDirectories));
        AssertTimingReportPathsResolve(roots.Artifacts);

        var finalCobertura = Path.Join(finalCoverageRoot, "coverage.cobertura.xml");
        using (var stream = CoverageRunArtifactReader.OpenRegularFile(roots.Artifacts, finalCoverageRoot, finalCobertura))
        {
            var report = XDocument.Load(stream);
            Assert.Equal("coverage", report.Root?.Name.LocalName);
            Assert.Equal(failedGate ? "9" : "10", (string?)report.Root?.Attribute("lines-covered"));
        }
        using (var gateStream = CoverageRunArtifactReader.OpenRegularFile(roots.Artifacts, finalCoverageRoot,
            Path.Join(finalCoverageRoot, "coverage-gate.json")))
        using (var gate = JsonDocument.Parse(gateStream))
            Assert.Equal(!failedGate, gate.RootElement.GetProperty("passed").GetBoolean());

        if (scenario == "oversized-inventory")
            Assert.Equal("reuse.inventory-slot-ineligible", Assert.Single(result.Decisions).Reason);
    }

    [Fact]
    public async Task MultipleCoverageProducerFallbacksKeepReportsInDisjointProducerRoots()
    {
        using var directory = TestDirectory.Create();
        var repositoryRoot = FindRepositoryRoot();
        var solution = Path.Join(repositoryRoot, "ForgeTrust.AppSurface.slnx");
        const string solutionProjectPath = "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj";
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        var starter = await workflow.InitializeAsync(Path.Join(directory.Path, "starter"), false, CancellationToken.None);
        var plan = await workflow.ExplainAsync(new EvidencePlanningRequest(
            Path.Join(starter.RootPath, "evidence.policy.json"),
            ["Cli/ForgeTrust.AppSurface.Cli.Tests/EvidenceReuseCommandTests.cs"], null), CancellationToken.None);
        var policy = Assert.IsType<EvidencePolicy>(plan.PolicySnapshot);
        var profile = policy.Profiles.Single(item => item.Id == plan.Profile.Id);
        var original = Assert.Single(profile.Producers);
        var slot = CoverageEvidenceInventory.CreateSlot();
        EvidenceProducerDeclaration CreateProducer(string id, string assertion) => original with
        {
            Id = id,
            AssertionIds = [assertion],
            Inputs = null,
            ArtifactSlots = [slot],
            CoverageGate = (original.CoverageGate ?? throw new InvalidOperationException("The starter coverage gate is missing.")) with
            {
                MinPatchLinePercent = null,
                MinPatchBranchPercent = null,
            },
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit),
            CoverageArtifactRetention = "managed",
        };
        var declarations = new[]
        {
            CreateProducer(original.Id, original.AssertionIds.Single()),
            CreateProducer("coverage-second", "coverage/second@1"),
        };
        var changedProfile = profile with { Producers = declarations };
        var changedPolicy = policy with
        {
            Profiles = policy.Profiles.Select(item => item.Id == changedProfile.Id ? changedProfile : item).ToArray(),
        };
        plan = new EvidencePlanner().Resolve(changedPolicy, plan.ChangedPaths);

        var runRoot = Path.Join(directory.Path, "run");
        var producer = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(
            new CoverageRunWorkflow(new Runner(solutionProjectPath: solutionProjectPath), new Generator(), TimeProvider.System)));
        var result = await new EvidenceReuseExecutionResolver(producer).RunAsync(plan, null, solution, runRoot,
            verifyReuse: false, observationOnly: false, storeRoot: Path.Join(directory.Path, "receipt-store"),
            waitTimeout: TimeSpan.FromSeconds(1), output: TextWriter.Null, error: TextWriter.Null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.Manifest.ProducerResults.Count);
        Assert.All(result.Manifest.ProducerResults, item => Assert.Equal(EvidenceProducerOutcome.Passed, item.Outcome));
        Assert.All(result.Decisions, item => Assert.Equal("suppressed", item.Publication));
        Assert.False(Directory.Exists(Path.Join(runRoot, "coverage")));
        foreach (var declaration in declarations)
        {
            var roots = EvidenceReuseExecutionResolver.GetProducerRoots(runRoot, declaration.Id);
            var finalCoverageRoot = Path.Join(roots.Artifacts, "coverage");
            var stagedCoverageRoot = Path.Join(roots.Execution, "coverage");
            foreach (var report in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt" })
            {
                var finalPath = Path.Join(finalCoverageRoot, report);
                Assert.True(File.Exists(finalPath), $"final report missing for {declaration.Id}: {finalPath}");
                AssertFilesEqual(Path.Join(stagedCoverageRoot, report), finalPath);
            }
            AssertTimingReportPathsResolve(roots.Artifacts);
            using (var cobertura = CoverageRunArtifactReader.OpenRegularFile(roots.Artifacts, finalCoverageRoot,
                Path.Join(finalCoverageRoot, "coverage.cobertura.xml")))
            {
                var document = XDocument.Load(cobertura);
                Assert.Equal("coverage", document.Root?.Name.LocalName);
            }
            using var gateStream = CoverageRunArtifactReader.OpenRegularFile(roots.Artifacts, finalCoverageRoot,
                Path.Join(finalCoverageRoot, "coverage-gate.json"));
            using var gate = JsonDocument.Parse(gateStream);
            Assert.True(gate.RootElement.GetProperty("passed").GetBoolean());
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(64, 32768)]
    [InlineData(128, 65536)]
    public async Task RepresentativeCompleteSetsPreserveExactPathsBytesAndDigests(int count, int size)
    {
        using var directory = TestDirectory.Create();
        var source = Path.Join(directory.Path, "source");
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        var files = Enumerable.Range(0, count).Select(index => Put(source, $"coverage/reports/{index:D3}.bin", bytes)).ToArray();
        var inventory = new CoverageEvidenceInventory(1, files);
        var slot = CoverageEvidenceInventory.CreateSlot();
        var target = Path.Join(directory.Path, "restored");
        var writer = new EvidenceArtifactWriter(Declaration(slot), target);
        var results = await inventory.WriteAsync(writer, slot, source, CancellationToken.None);
        Assert.Equal(count, results.Count);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
        Assert.Equal(files.Select(file => file.RelativePath), results.Select(file => file.RelativePath));
        foreach (var file in files)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(TestPathUtils.PathUnder(target, file.RelativePath)));
            Assert.Equal(file.Sha256, results.Single(result => result.RelativePath == file.RelativePath).Sha256);
        }
    }

    [Theory]
    [InlineData("count")]
    [InlineData("bytes")]
    [InlineData("slot-count")]
    [InlineData("slot-bytes")]
    [InlineData("legacy-slot")]
    public async Task OversizedSetsAreRejectedBeforeAnyCopy(string limit)
    {
        using var directory = TestDirectory.Create();
        var source = Path.Join(directory.Path, "source");
        var first = Put(source, "coverage/a", [1]);
        var second = Put(source, "coverage/b", [2]);
        CoverageEvidenceInventoryFile[] files = [first, second];
        var slot = CoverageEvidenceInventory.CreateSlot();
        if (limit == "count") files = Enumerable.Range(0, 129).Select(index => first with { RelativePath = $"coverage/{index:D3}" }).ToArray();
        if (limit == "bytes") files = [first with { LengthBytes = CoverageEvidenceInventory.MaximumBytes }, second];
        if (limit == "slot-count") slot = slot with { MaximumFiles = 1 };
        if (limit == "slot-bytes") slot = slot with { MaximumBytes = 1 };
        if (limit == "legacy-slot") slot = slot with { MaximumFiles = null };
        var target = Path.Join(directory.Path, "custody");
        var writer = new EvidenceArtifactWriter(Declaration(slot), target);
        await Assert.ThrowsAsync<CoverageEvidenceInventoryException>(() => new CoverageEvidenceInventory(1, files)
            .WriteAsync(writer, slot, source, CancellationToken.None));
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(target));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(TestPathUtils.PathUnder(source, first.RelativePath)));
    }

    [Theory]
    [InlineData(false, "managed", false)]
    [InlineData(true, "managed", false)]
    [InlineData(true, "full", false)]
    [InlineData(true, "managed", true)]
    public async Task ActualWorkflowKeepsFreshOutcomeAndCompleteRetainedReports(bool custody, string retention, bool oversized)
    {
        using var directory = TestDirectory.Create();
        var solution = Path.Join(directory.Path, "sample.slnx");
        var project = Path.Join(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(solution, "{}");
        var slot = CoverageEvidenceInventory.CreateSlot();
        if (oversized) slot = slot with { MaximumBytes = 1 };
        var declaration = Declaration(slot) with
        {
            CoverageArtifactRetention = retention,
            CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit) { CooperativeChildLifetime = true }
        };
        var runner = new Runner();
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(new CoverageRunWorkflow(runner, new Generator(), TimeProvider.System)));
        var output = Path.Join(directory.Path, "run");
        var staged = Path.Join(directory.Path, "custody");
        var writer = custody ? new EvidenceArtifactWriter(declaration, staged) : null;
        await using var childLifetime = EvidenceChildLifetimeSession.Start(Guid.NewGuid().ToString("N"));
        var execution = await adapter.RunWithInventoryAsync(declaration, solution, output, null,
            CoverageTextWriters.Create(TextWriter.Null, TextWriter.Null), CancellationToken.None, writer, childLifetime);
        Assert.Equal(EvidenceProducerOutcome.Passed, execution.Result.Outcome);
        foreach (var file in new[] { "coverage.cobertura.xml", "coverage-gate.json", "coverage-gate.md", "timings.json", "summary.txt" })
            Assert.True(File.Exists(TestPathUtils.PathUnder(output, "coverage", file)), file);
        if (!custody || oversized)
        {
            Assert.Null(execution.Inventory);
            Assert.Null(execution.Result.Artifacts);
            Assert.NotNull(execution.InventoryIneligibilityReason);
            Assert.False(Directory.Exists(staged));
            if (oversized) Assert.Equal("inventory-slot-ineligible", execution.InventoryIneligibilityReason);
            return;
        }
        Assert.Null(execution.InventoryIneligibilityReason);
        Assert.NotNull(execution.Inventory);
        Assert.DoesNotContain(execution.Inventory.Files, file => file.RelativePath.EndsWith("/unrelated.txt", StringComparison.Ordinal));
        Assert.Contains(execution.Inventory.Files, file => file.RelativePath.EndsWith("/coverage-project.json", StringComparison.Ordinal));
        Assert.Contains(execution.Inventory.Files, file => file.RelativePath.EndsWith("/attachment.txt", StringComparison.Ordinal));
        Assert.Contains(execution.Inventory.Files, file => file.RelativePath.Contains("junit-coverage-", StringComparison.Ordinal));
        if (File.Exists(Path.Join(output, "coverage", CoverageArtifactSession.SidecarName)))
            Assert.Contains(execution.Inventory.Files, file => file.RelativePath.EndsWith(CoverageArtifactSession.SidecarName, StringComparison.Ordinal));
        Assert.Equal(execution.Inventory.Files.Count, execution.Result.Artifacts!.Count);
        Assert.True(await writer!.VerifyWrittenArtifactsAsync());
        foreach (var file in execution.Inventory.Files)
            Assert.Equal(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(output, file.RelativePath)), await File.ReadAllBytesAsync(TestPathUtils.PathUnder(staged, file.RelativePath)));
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("missing-project-manifest")]
    [InlineData("corrupt-project-manifest")]
    [InlineData("unsafe-attachment")]
    [InlineData("file-count")]
    [InlineData("corrupt-run-manifest")]
    [InlineData("run-manifest-outcome")]
    [InlineData("run-manifest-inventory")]
    [InlineData("run-manifest-oversized")]
    [InlineData("timings-oversized")]
    [InlineData("project-manifest-oversized")]
    [InlineData("oversized-attachment")]
    [InlineData("missing-timings-projects")]
    [InlineData("project-schema-mismatch")]
    [InlineData("project-schema-overflow")]
    [InlineData("project-schema-fractional")]
    [InlineData("project-slug-mismatch")]
    public async Task ActualWorkflowRejectsIncompleteOrUnsafeInventoryWithoutLosingFreshPass(string failure)
    {
        using var directory = TestDirectory.Create();
        // Isolate optional history publication so shared-store capacity or earlier corrupt fixtures cannot
        // prevent the deterministic post-publication fault from running.
        using var artifactHome = new EnvironmentVariableScope("APPSURFACE_ARTIFACT_HOME",
            TestPathUtils.PathUnder(directory.Path, "history"));
        var solution = Path.Join(directory.Path, "sample.slnx");
        var project = Path.Join(directory.Path, "tests", "Sample.Tests", "Sample.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(solution, "{}");
        var output = Path.Join(directory.Path, "run");
        var runner = new Runner(failure);
        var runManifestPath = TestPathUtils.PathUnder(output, "coverage", CoverageArtifactSession.SidecarName);
        // Managed storage republishes this optional sidecar after collection and again after gate reports.
        // Corrupt every completed publication, so the fixture does not depend on storage being unavailable.
        var changesSidecar = failure == "corrupt-run-manifest" || failure.StartsWith("run-manifest-", StringComparison.Ordinal);
        using var corruptingWriter = changesSidecar ? new SidecarCorruptingWriter(runManifestPath, failure) : null;
        TextWriter outputWriter = corruptingWriter ?? (TextWriter)TextWriter.Null;
        void MutateManifest()
        {
            // The test owns one declared project's fixed destination; production never discovers outputs this way.
            var manifest = Directory.GetFiles(Path.Join(output, "coverage", "projects"), "coverage-project.json", SearchOption.AllDirectories).Single();
            if (failure == "missing-project-manifest") File.Delete(manifest);
            if (failure == "corrupt-project-manifest") File.WriteAllText(manifest, "broken json");
            if (failure == "project-manifest-oversized")
                File.AppendAllText(manifest, new string(' ', CoverageEvidenceInventory.MaximumMetadataBytes));
            if (failure.StartsWith("project-schema-", StringComparison.Ordinal) || failure == "project-slug-mismatch")
            {
                var metadata = JsonNode.Parse(File.ReadAllText(manifest))!;
                if (failure == "project-schema-overflow") metadata["schemaVersion"] = long.MaxValue;
                else if (failure == "project-schema-fractional") metadata["schemaVersion"] = 1.5;
                else if (failure == "project-schema-mismatch") metadata["schemaVersion"] = 0;
                else metadata["slug"] = "a-different-project";
                File.WriteAllText(manifest, metadata.ToJsonString());
            }
            if (failure == "missing-timings-projects")
            {
                // The existing timing seam runs before this one fixture-owned staging file is committed.
                var timings = Directory.GetFiles(TestPathUtils.PathUnder(output, "coverage"), ".timings.*.tmp").Single();
                var metadata = JsonNode.Parse(File.ReadAllText(timings))!.AsObject();
                Assert.True(metadata.Remove("projects"));
                File.WriteAllText(timings, metadata.ToJsonString());
            }
            if (failure == "timings-oversized")
            {
                var timings = Directory.GetFiles(TestPathUtils.PathUnder(output, "coverage"), ".timings.*.tmp").Single();
                File.AppendAllText(timings, new string(' ', CoverageEvidenceInventory.MaximumMetadataBytes));
            }
            if (failure == "corrupt-run-manifest") File.WriteAllText(runManifestPath, "broken json");
            if (failure == "unsafe-attachment") File.WriteAllText(Path.Join(runner.RawDirectory!, "CON.txt"), "unsafe retained name");
            if (failure == "file-count")
                for (var index = 0; index < 129; index++) File.WriteAllText(Path.Join(runner.RawDirectory!, $"report-{index}.txt"), "retained");
        }
        var declaration = Declaration(CoverageEvidenceInventory.CreateSlot()) with
        { CoverageExecution = new EvidenceCoverageExecutionOptions(TestResults: EvidenceCoverageTestResultFormat.Junit) { CooperativeChildLifetime = true } };
        var adapter = new CoverageEvidenceProducer(new CoverageEvidenceExecutionWorkflow(
            new CoverageRunWorkflow(runner, new Generator(), TimeProvider.System, timingsStaged: MutateManifest)));
        var target = Path.Join(directory.Path, "custody");
        var writer = new EvidenceArtifactWriter(declaration, target);
        await using var childLifetime = EvidenceChildLifetimeSession.Start(Guid.NewGuid().ToString("N"));
        var execution = await adapter.RunWithInventoryAsync(declaration, solution, output, null,
            CoverageTextWriters.Create(outputWriter, TextWriter.Null), CancellationToken.None, writer, childLifetime);
        Assert.Equal(EvidenceProducerOutcome.Passed, execution.Result.Outcome);
        if (changesSidecar)
        {
            Assert.True(corruptingWriter!.CorruptionCount > 0, "The controlled managed sidecar publication must complete before injecting corruption.");
            if (failure == "corrupt-run-manifest") Assert.Equal("broken json", await File.ReadAllTextAsync(runManifestPath));
            else if (failure == "run-manifest-oversized") Assert.True(new FileInfo(runManifestPath).Length > CoverageEvidenceInventory.MaximumMetadataBytes);
            else
            {
                var sidecar = JsonNode.Parse(await File.ReadAllTextAsync(runManifestPath))!;
                if (failure == "run-manifest-outcome") Assert.Equal("failed", sidecar["manifestBase"]!["outcome"]!.GetValue<string>());
                else Assert.Empty(sidecar["inventory"]!.AsArray());
            }
        }
        Assert.Null(execution.Inventory);
        Assert.NotNull(execution.InventoryIneligibilityReason);
        if (failure.StartsWith("project-schema-", StringComparison.Ordinal) || failure == "project-slug-mismatch")
            Assert.Equal("inventory-project-manifest-mismatch", execution.InventoryIneligibilityReason);
        Assert.Null(execution.Result.Artifacts);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.False(Directory.Exists(target));
        Assert.True(File.Exists(Path.Join(output, "coverage", "coverage-gate.json")));
        Assert.True(File.Exists(Path.Join(output, "coverage", "coverage-gate.md")));
        Assert.True(File.Exists(Path.Join(output, "coverage", "coverage.cobertura.xml")));
    }

    [Fact]
    public async Task ExactGlobalByteLimitStreamsWithoutRaisingTheLimit()
    {
        using var directory = TestDirectory.Create();
        var source = Path.Join(directory.Path, "source");
        Directory.CreateDirectory(Path.Join(source, "coverage"));
        var path = Path.Join(source, "coverage", "large-report.bin");
        using (var stream = File.Create(path)) stream.SetLength(CoverageEvidenceInventory.MaximumBytes);
        string digest;
        using (var stream = File.OpenRead(path)) digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var inventory = new CoverageEvidenceInventory(1, [new("coverage/large-report.bin", CoverageEvidenceInventory.MaximumBytes, digest)]);
        var slot = CoverageEvidenceInventory.CreateSlot();
        var writer = new EvidenceArtifactWriter(Declaration(slot), Path.Join(directory.Path, "custody"));
        var results = await inventory.WriteAsync(writer, slot, source, CancellationToken.None);
        Assert.Equal(256L * 1024 * 1024, Assert.Single(results).LengthBytes);
        Assert.True(await writer.VerifyWrittenArtifactsAsync());
    }

    private static CoverageEvidenceInventoryFile Put(string root, string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TestPathUtils.PathUnder(root, path))!);
        File.WriteAllBytes(TestPathUtils.PathUnder(root, path), bytes);
        return new(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void AssertFilesEqual(string expectedPath, string actualPath)
    {
        using var expected = File.OpenRead(expectedPath);
        using var actual = File.OpenRead(actualPath);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
    }

    private static void AssertTimingReportPathsResolve(string artifactRoot)
    {
        var coverageRoot = Path.Join(artifactRoot, "coverage");
        using var timings = CoverageRunArtifactReader.OpenRegularFile(artifactRoot, coverageRoot,
            Path.Join(coverageRoot, "timings.json"));
        using var document = JsonDocument.Parse(timings);
        foreach (var project in document.RootElement.GetProperty("projects").EnumerateArray())
        {
            var slug = project.GetProperty("slug").GetString()!;
            var required = new[]
            {
                TestPathUtils.PathUnder(coverageRoot, "projects", slug, CoverageProjectManifest.FileName),
                Path.Join(coverageRoot, "projects", slug, "coverage.cobertura.xml"),
            }.Concat(project.GetProperty("testResults").EnumerateArray().Select(report =>
                Path.Join(coverageRoot, report.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar))));
            foreach (var path in required)
            {
                using var report = CoverageRunArtifactReader.OpenRegularFile(artifactRoot, coverageRoot, path);
                Assert.True(report.Length >= 0, path);
            }
        }
    }

    private static EvidenceProducerDeclaration Declaration(EvidenceArtifactSlot slot) => new("coverage", "coverage", "1.0.0", [],
        ["coverage/assertion@1"], [slot], 60, new EvidenceCoverageGateRequirements(95, 85, TolerancePercent: 0));

    private const string Cobertura = "<coverage lines-covered=\"10\" lines-valid=\"10\" branches-covered=\"10\" branches-valid=\"10\" line-rate=\"1\" branch-rate=\"1\"><packages /></coverage>";

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
        while (current is not null)
        {
            if (Directory.Exists(Path.Join(current.FullName, ".git")) || File.Exists(Path.Join(current.FullName, ".git")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("The CLI test process is not running inside a Git worktree.");
    }

    private sealed class CountOnlyPublications(int count) : IReadOnlyList<CoverageArtifactInventoryEntry>
    {
        public int Count => count;
        public CoverageArtifactInventoryEntry this[int index] => throw new InvalidOperationException("An ineligible set must not be inspected.");
        public IEnumerator<CoverageArtifactInventoryEntry> GetEnumerator() => throw new InvalidOperationException("An ineligible set must not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SidecarCorruptingWriter(string path, string failure) : StringWriter
    {
        internal int CorruptionCount { get; private set; }
        public override Task WriteLineAsync(string? value)
        {
            if (value?.StartsWith("Coverage storage: current=", StringComparison.Ordinal) == true)
            {
                // Open never creates a missing sidecar: every corruption must target the actual publication.
                using var published = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                string replacement;
                if (failure == "corrupt-run-manifest") replacement = "broken json";
                else if (failure == "run-manifest-oversized") replacement = new string(' ', CoverageEvidenceInventory.MaximumMetadataBytes + 1);
                else
                {
                    var sidecar = JsonNode.Parse(published)!;
                    if (failure == "run-manifest-outcome") sidecar["manifestBase"]!["outcome"] = "failed";
                    else sidecar["inventory"] = new JsonArray();
                    replacement = sidecar.ToJsonString();
                }
                published.Position = 0;
                published.SetLength(0);
                published.Write(System.Text.Encoding.UTF8.GetBytes(replacement));
                published.Flush(flushToDisk: true);
                CorruptionCount++;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class Runner(string? failure = null, string? solutionProjectPath = null, int attachmentCount = 1) : ICoverageRunProcessRunner
    {
        internal string? RawDirectory { get; private set; }
        public async Task<CoverageRunProcessResult> RunAsync(CoverageRunProcessRequest request, CancellationToken cancellationToken)
        {
            request.Lease.Complete();
            if (request.Arguments[0] == "sln")
                return new(0, $"Project(s)\n----------\n{solutionProjectPath ?? "tests/Sample.Tests/Sample.Tests.csproj"}\n");
            if (request.Arguments[0] == "msbuild")
            {
                const string capabilities = """{"Properties":{"TestingPlatformDotnetTestSupport":"false","TargetFramework":"net10.0"},"Items":{"PackageReference":[{"Identity":"coverlet.collector"}]}}""";
                return new(0, capabilities, StandardOutput: capabilities);
            }
            if (request.OutputFile is not null) await File.WriteAllTextAsync(request.OutputFile, "output", cancellationToken);
            if (request.Arguments[0] == "test")
            {
                var index = request.Arguments.ToList().IndexOf("--results-directory");
                var raw = Path.Join(request.Arguments[index + 1], "collector");
                Directory.CreateDirectory(raw);
                RawDirectory = raw;
                await File.WriteAllTextAsync(Path.Join(raw, "coverage.cobertura.xml"), Cobertura, cancellationToken);
                for (var attachment = 0; attachment < attachmentCount; attachment++)
                {
                    var attachmentName = attachmentCount == 1 ? "attachment.txt" : $"attachment-{attachment:D3}.txt";
                    await File.WriteAllTextAsync(Path.Join(raw, attachmentName), "retained attachment", cancellationToken);
                }
                if (failure == "oversized-attachment")
                {
                    using var oversized = File.Create(Path.Join(raw, "oversized.bin"));
                    oversized.SetLength(CoverageEvidenceInventory.MaximumBytes + 1);
                }
                await File.WriteAllTextAsync(Path.Join(Path.GetDirectoryName(request.OutputFile!)!, "unrelated.txt"), "not declared", cancellationToken);
                var logger = request.Arguments.Single(argument => argument.StartsWith("--logger:junit;LogFilePath=", StringComparison.Ordinal));
                var junit = logger["--logger:junit;LogFilePath=".Length..];
                if (failure != "missing-result") await File.WriteAllTextAsync(junit, "<testsuites><testsuite tests=\"1\" /></testsuites>", cancellationToken);
            }
            return new(0, "output");
        }
    }

    private sealed class Generator(long summaryLength = 0, bool failedGate = false) : ICoverageRunReportGenerator
    {
        public async Task<CoverageRunMergeResult> MergeAsync(IReadOnlyList<string> coverageFiles, string outputDirectory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Join(outputDirectory, "Cobertura.xml");
            var summary = Path.Join(outputDirectory, "Summary.txt");
            var cobertura = failedGate
                ? Cobertura.Replace("lines-covered=\"10\"", "lines-covered=\"9\"", StringComparison.Ordinal)
                    .Replace("line-rate=\"1\"", "line-rate=\"0.9\"", StringComparison.Ordinal)
                : Cobertura;
            await File.WriteAllTextAsync(path, cobertura, cancellationToken);
            if (summaryLength == 0) await File.WriteAllTextAsync(summary, "summary", cancellationToken);
            else
            {
                await using var stream = File.Create(summary);
                stream.SetLength(summaryLength);
            }
            return new(0, path, summary);
        }
    }
}
