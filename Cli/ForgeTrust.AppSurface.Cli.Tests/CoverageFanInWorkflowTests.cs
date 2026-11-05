using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Coverage;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection("CoverageGate process state")]
public sealed class CoverageFanInWorkflowTests
{
    [Fact]
    public async Task PublicWorkflowKeepsCaptureCompositionBaselineAcceptanceAndReadOnlyValiditySeparate()
    {
        using var fixture = await Fixture.Create();
        var capture = await CoverageFanIn.CaptureAsync(fixture.Capture());
        Assert.Equal(CoverageOperationOutcome.Succeeded, capture.Outcome);
        Assert.False(capture.Accepted); Assert.False(capture.Verified);
        Assert.Equal(CoverageObservation.TrustedProducer, capture.Observation);
        var composition = await CoverageFanIn.ComposeAsync(fixture.Compose());
        Assert.Equal(CoverageOperationOutcome.Succeeded, composition.Outcome);
        Assert.False(composition.Accepted);
        var accepted = await CoverageFanIn.AcceptAsync(fixture.Accept());
        Assert.Equal(CoverageOperationOutcome.Succeeded, accepted.Outcome);
        Assert.True(accepted.Accepted); Assert.True(accepted.Verified);
        Assert.True(accepted.Effects.GenerationCommitted); Assert.True(accepted.Effects.CleanupComplete);
        Assert.Equal("PatchNotRequested", accepted.PatchOutcome);
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "composed-gate.json")));
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "primary-gate.md")));
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "composed-gate.md")));
        var before = Directory.GetFiles(fixture.Temp.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(x => x, x => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(x))));
        var verified = await CoverageFanIn.VerifyAsync(fixture.Verify());
        Assert.Equal(CoverageOperationOutcome.Succeeded, verified.Outcome);
        Assert.True(verified.Verified); Assert.False(verified.Accepted);
        Assert.False(verified.Effects.OutputCreated); Assert.False(verified.Effects.GenerationCommitted);
        Assert.Equal(before.Count, Directory.GetFiles(fixture.Temp.Path, "*", SearchOption.AllDirectories).Length);
        foreach (var (path, hash) in before)
            Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public async Task PublicCaptureRejectsFailedInventoryBeforeOutputMutationAndPreservesProducerBytes()
    {
        using var fixture = await Fixture.Create();
        File.WriteAllText(fixture.Timings, """{"configuration":"Release","coverageDriver":"collector","merge":{"exitCode":0},"artifacts":{"coverageFiles":1},"projects":[{"project":"checks/Verification.csproj","exitCode":1,"executionStatus":"completed","coverageArtifactStatus":"produced"}]}""");
        var before = File.ReadAllBytes(fixture.Report);
        var result = await CoverageFanIn.CaptureAsync(fixture.Capture());
        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted); Assert.False(result.Effects.OutputCreated);
        Assert.False(Directory.Exists(fixture.Captured));
        Assert.Equal(before, File.ReadAllBytes(fixture.Report));
    }

    [Fact]
    public async Task DirtyOrMismatchedSourceCannotBeCapturedAndDiagnosticsDoNotEchoSecretInput()
    {
        using var fixture = await Fixture.Create();
        File.WriteAllText(Path.Join(fixture.Repository, "untracked-secret-token.txt"), "do-not-print-secret");
        var result = await CoverageFanIn.CaptureAsync(fixture.Capture());
        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(Directory.Exists(fixture.Captured));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        File.Delete(Path.Join(fixture.Repository, "untracked-secret-token.txt"));
        var tracked = Path.Join(fixture.Repository, "checks", "Verification.csproj");
        var original = File.ReadAllBytes(tracked);
        File.AppendAllText(tracked, "<!-- tracked edit -->");
        Assert.Equal(CoverageOperationOutcome.Rejected, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        File.WriteAllBytes(tracked, original);
        var changed = fixture.Capture() with { Policy = fixture.Policy with { Source = fixture.Policy.Source with { Tree = new string('0', 40) } } };
        Assert.Equal(CoverageOperationOutcome.Rejected, (await CoverageFanIn.CaptureAsync(changed)).Outcome);
    }

    [Fact]
    public async Task PreCancellationAndMissingCaptureSetCannotCreateSuccessfulGeneration()
    {
        using var fixture = await Fixture.Create();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = await CoverageFanIn.CaptureAsync(fixture.Capture(), cancellation.Token);
        Assert.Equal(CoverageOperationOutcome.Cancelled, cancelled.Outcome);
        Assert.False(Directory.Exists(fixture.Captured));
        Assert.Equal(CoverageOperationOutcome.Rejected, (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        Assert.False(Directory.Exists(fixture.Composed));
    }

    [Fact]
    public async Task ChangedCaptureOrCompositionCannotUseAnOlderAcceptance()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.AcceptAsync(fixture.Accept())).Outcome);
        var path = Path.Join(fixture.Composed, "coverage.cobertura.xml");
        var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("hits=\"1\"", "hits=\"0\"", StringComparison.Ordinal));
        var verify = await CoverageFanIn.VerifyAsync(fixture.Verify());
        Assert.NotEqual(CoverageOperationOutcome.Succeeded, verify.Outcome);
        Assert.False(verify.Verified); Assert.False(verify.Accepted);
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
    }

    [Fact]
    public async Task OwnedReplacementReportsEffectsWithoutClaimingCreationOfAnExistingOutput()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        var next = await CoverageFanIn.CaptureAsync(fixture.Capture() with { OutputMode = CoverageOutputMode.ReplaceOwned });
        Assert.Equal(CoverageOperationOutcome.Succeeded, next.Outcome);
        Assert.False(next.Effects.OutputCreated);
        Assert.True(next.Effects.GenerationCommitted);
        Assert.True(next.Effects.CleanupComplete);
    }

    [Fact]
    public async Task ExplicitImportPreservesProducerObservationAndHasFreshReceivingCustody()
    {
        using var fixture = await Fixture.Create();
        var first = await CoverageFanIn.CaptureAsync(fixture.Capture());
        Assert.Equal(CoverageOperationOutcome.Succeeded, first.Outcome);
        var sourceMarkerPath = Path.Join(fixture.Captured, CoverageFanInIO.CompletionName);
        var sourceMarker = JsonNode.Parse(File.ReadAllBytes(sourceMarkerPath))!.AsObject();
        sourceMarker["generation"] = "transport-metadata-is-not-local-custody";
        File.WriteAllText(sourceMarkerPath, sourceMarker.ToJsonString());
        var receiving = Path.Join(fixture.Temp.Path, "receiving");
        var imported = await CoverageFanIn.CaptureAsync(new(fixture.Policy, fixture.Repository, receiving, "primary",
            ImportDirectory: fixture.Captured,
            OriginalSourceRoot: ForgeTrust.AppSurface.Evidence.Coverage.CoverageRunOutputLease.NormalizePlatformPath(fixture.Repository)));
        Assert.Equal(CoverageOperationOutcome.Succeeded, imported.Outcome);
        Assert.True(imported.Effects.Imported);
        Assert.Equal(CoverageObservation.TrustedProducer, imported.Observation);
        Assert.NotEqual(first.Effects.Generation, imported.Effects.Generation);
        Assert.Equal(File.ReadAllBytes(Path.Join(fixture.Captured, "manifest.json")), File.ReadAllBytes(Path.Join(receiving, "manifest.json")));
        var receivingMarker = JsonNode.Parse(File.ReadAllBytes(Path.Join(receiving, CoverageFanInIO.CompletionName)))!.AsObject();
        Assert.Equal(imported.Effects.Generation, receivingMarker["generation"]!.GetValue<string>());
        Assert.Equal("imported", receivingMarker["materialization"]!.GetValue<string>());
        Assert.NotEqual(sourceMarker["physicalIdentity"]!.GetValue<string>(),
            receivingMarker["physicalIdentity"]!.GetValue<string>());
        using (var store = CoverageArtifactStore.OpenReadOnly())
            Assert.Equal(imported.Effects.Generation, store.GetCurrentRun(receiving)?.ManifestBase.RunId);
        var composition = fixture.Compose() with { PrimaryCaptureDirectory = receiving };
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.ComposeAsync(composition)).Outcome);
        var accepted = await CoverageFanIn.AcceptAsync(fixture.Accept() with { PrimaryCaptureDirectory = receiving });
        Assert.Equal(CoverageOperationOutcome.Succeeded, accepted.Outcome);
        Assert.True(accepted.Effects.Imported);
        Assert.Equal(CoverageObservation.TrustedProducer, accepted.Observation);
        Assert.True(accepted.Accepted);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("stage")]
    [InlineData("policyDigest")]
    [InlineData("physicalIdentity")]
    [InlineData("materialization")]
    [InlineData("generation")]
    [InlineData("operationId")]
    public async Task PublicComposeRejectsCaptureWithInvalidLocalGenerationBinding(string field)
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        var markerPath = Path.Join(fixture.Captured, CoverageFanInIO.CompletionName);
        MutateGenerationMarker(markerPath, field);
        var captureAfterMutation = Snapshot(fixture.Captured);

        var result = await CoverageFanIn.ComposeAsync(fixture.Compose());

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Effects.OutputCreated);
        Assert.False(result.Effects.GenerationCommitted);
        Assert.False(Directory.Exists(fixture.Composed));
        Assert.Equal(captureAfterMutation, Snapshot(fixture.Captured));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("stage")]
    [InlineData("policyDigest")]
    [InlineData("physicalIdentity")]
    [InlineData("materialization")]
    [InlineData("generation")]
    [InlineData("operationId")]
    public async Task PublicAcceptRejectsCompositionWithInvalidGenerationBinding(string field)
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        var markerPath = Path.Join(fixture.Composed, CoverageFanInIO.CompletionName);
        MutateGenerationMarker(markerPath, field);
        var compositionAfterMutation = Snapshot(fixture.Composed);

        var result = await CoverageFanIn.AcceptAsync(fixture.Accept());

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.False(result.Effects.OutputCreated);
        Assert.False(result.Effects.GenerationCommitted);
        Assert.False(Directory.Exists(fixture.Accepted));
        Assert.Equal(compositionAfterMutation, Snapshot(fixture.Composed));
    }

    [Fact]
    public async Task HistoricalAcceptanceCannotBeReadAsCurrentRunOrRelabeledByImport()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        var replay = await CoverageFanIn.AcceptAsync(fixture.Accept() with { EvaluationIntent = CoverageEvaluationIntent.HistoricalReplay });
        Assert.Equal(CoverageOperationOutcome.Succeeded, replay.Outcome);
        Assert.Equal(CoverageEvaluationIntent.HistoricalReplay, replay.EvaluationIntent);
        Assert.NotEqual(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.VerifyAsync(fixture.Verify())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.VerifyAsync(fixture.Verify() with
        { EvaluationIntent = CoverageEvaluationIntent.HistoricalReplay })).Outcome);
        var changedRun = fixture.Policy with { Run = fixture.Policy.Run with { RunId = "relabelled" } };
        var receiving = Path.Join(fixture.Temp.Path, "receiving");
        var recertify = await CoverageFanIn.CaptureAsync(new(changedRun, fixture.Repository, receiving, "primary",
            ImportDirectory: fixture.Captured,
            OriginalSourceRoot: ForgeTrust.AppSurface.Evidence.Coverage.CoverageRunOutputLease.NormalizePlatformPath(fixture.Repository)));
        Assert.Equal(CoverageOperationOutcome.Rejected, recertify.Outcome);
        Assert.False(Directory.Exists(receiving));
    }

    [Fact]
    public async Task PatchAcceptanceUsesOneExactDiffAndRejectsAChangedExpectedBase()
    {
        using var fixture = await Fixture.Create();
        await fixture.SelectPatch();
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        var accepted = await CoverageFanIn.AcceptAsync(fixture.Accept());
        Assert.Equal(CoverageOperationOutcome.Succeeded, accepted.Outcome);
        Assert.Equal("PatchAccepted", accepted.PatchOutcome);
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "diff.patch")));
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "primary-gate.md")));
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "composed-gate.md")));
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.VerifyAsync(fixture.Verify())).Outcome);
        var missingDiff = fixture.Policy with { Gate = fixture.Policy.Gate with { DiffBase = "missing-ref" } };
        Assert.NotEqual(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.VerifyAsync(fixture.Verify() with { Policy = missingDiff })).Outcome);
    }

    [Fact]
    public async Task NumericFailureRetainsRejectedGateReportsWithoutAnAcceptanceReceipt()
    {
        using var fixture = await Fixture.Create();
        await fixture.SelectPatch();
        File.WriteAllText(fixture.Report, File.ReadAllText(fixture.Report).Replace("hits=\"1\"", "hits=\"0\"", StringComparison.Ordinal)
            .Replace("lines-covered=\"1\"", "lines-covered=\"0\"", StringComparison.Ordinal)
            .Replace("line-rate=\"1\"", "line-rate=\"0\"", StringComparison.Ordinal));
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        var rejected = await CoverageFanIn.AcceptAsync(fixture.Accept());
        Assert.Equal(CoverageOperationOutcome.Rejected, rejected.Outcome);
        Assert.False(rejected.Accepted); Assert.False(rejected.Verified);
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "primary-gate.json")));
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "primary-gate.md")));
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "composed-gate.md")));
        Assert.True(File.Exists(Path.Join(fixture.Accepted, "rejection.json")));
        Assert.False(File.Exists(Path.Join(fixture.Accepted, "acceptance.json")));
        Assert.Equal("NotEvaluated", rejected.PatchOutcome);
    }

    [Fact]
    public async Task PublicComposeRejectsMissingExtraAndDuplicateSupplementSets()
    {
        using var fixture = await Fixture.Create();
        var supplements = new[]
        {
            new CoverageSupplement("native-a", Fixture.CurrentPlatform, "Category=fast"),
            new CoverageSupplement("native-b", Fixture.CurrentPlatform, "Category=slow")
        };
        fixture.SetSupplements(supplements);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        var first = await fixture.CaptureSupplement(supplements[0], "supplement-a");
        var firstDuplicate = await fixture.CaptureSupplement(supplements[0], "supplement-a-copy");
        var second = await fixture.CaptureSupplement(supplements[1], "supplement-b");

        var invalidSets = new (string Name, string[] Paths)[]
        {
            ("missing", [first]),
            ("extra", [first, second, firstDuplicate]),
            ("duplicate", [first, firstDuplicate])
        };
        foreach (var (name, paths) in invalidSets)
        {
            var output = Path.Join(fixture.Temp.Path, "composition-" + name);
            var result = await CoverageFanIn.ComposeAsync(fixture.Compose() with
            {
                SupplementaryCaptureDirectories = paths,
                OutputDirectory = output
            });

            Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
            Assert.False(result.Accepted);
            Assert.False(result.Verified);
            Assert.False(result.Effects.OutputCreated);
            Assert.False(Directory.Exists(output));
        }
    }

    [Fact]
    public async Task PublicImportRejectsManifestIdentityHashAndRehashedInvalidTimingMutations()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);

        var mutations = new (string Name, Action<JsonObject> Mutate)[]
        {
            ("scope", manifest => manifest["scope"] = "native"),
            ("policy-encoding", manifest => manifest["policyEncoding"] = "unknown"),
            ("run", manifest => manifest["run"]!["attempt"] = 99),
            ("observation", manifest => manifest["observation"] = "ImportedExecution"),
            ("blank-root", manifest => manifest["sourceRoot"] = " "),
            ("relative-root", manifest => manifest["sourceRoot"] = "relative"),
            ("short-root", manifest => manifest["sourceRoot"] = "C:"),
            ("invalid-drive", manifest => manifest["sourceRoot"] = "0:/repo"),
            ("invalid-drive-separator", manifest => manifest["sourceRoot"] = "C:repo"),
            ("null-root", manifest => manifest["sourceRoot"] = null),
            ("source", manifest => manifest["source"]!["commit"] = new string('a', 40)),
            ("sdk", manifest => manifest["source"]!["sdk"] = "different-sdk"),
            ("configuration", manifest => manifest["source"]!["configuration"] = "Debug"),
            ("policy", manifest => manifest["policy"]!["gate"]!["minLine"] = 94),
            ("policy-digest", manifest => manifest["policyDigest"] = new string('0', 64)),
            ("producer", manifest => manifest["producerId"] = "unexpected"),
            ("hash", manifest => manifest["files"]!["coverage.cobertura.xml"] = new string('0', 64))
        };
        foreach (var (name, mutate) in mutations)
            await AssertImportRejected(fixture, fixture.Captured, "primary", name, mutate);

        await AssertImportRejected(fixture, fixture.Captured, "primary", "timings-rehashed", null,
            invalidateAndRehashTimings: true);
    }

    [Fact]
    public async Task PublicImportRejectsSupplementPlatformAndFilterMismatches()
    {
        using var fixture = await Fixture.Create();
        var supplement = new CoverageSupplement("native", Fixture.CurrentPlatform, "Category=fast");
        fixture.SetSupplements([supplement]);
        var capture = await fixture.CaptureSupplement(supplement, "native-capture");

        await AssertImportRejected(fixture, capture, supplement.Id, "platform",
            manifest => manifest["platform"] = "other-platform");
        await AssertImportRejected(fixture, capture, supplement.Id, "filter",
            manifest => manifest["filter"] = "Category=other");
    }

    [Fact]
    public async Task PublicImportRejectsOriginalSourceSchemaAndUnknownFileMismatches()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);

        await AssertImportRejected(fixture, fixture.Captured, "primary", "schema",
            manifest => manifest["schemaVersion"] = 1);
        await AssertImportRejected(fixture, fixture.Captured, "primary", "unknown-file", null,
            addUnknownFile: true);
        await AssertImportRejected(fixture, fixture.Captured, "primary", "original-source",
            null, originalSourceRoot: Path.Join(fixture.Temp.Path, "different-source-root"));
    }

    [Fact]
    public async Task InvalidNewCaptureReplacementLeavesPriorGenerationUntouched()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        var before = Snapshot(fixture.Captured);
        var validTimings = File.ReadAllBytes(fixture.Timings);
        try
        {
            File.WriteAllText(fixture.Timings, """{"configuration":"Debug","coverageDriver":"collector","merge":{"exitCode":0},"artifacts":{"coverageFiles":1},"projects":[{"project":"checks/Verification.csproj","exitCode":0,"executionStatus":"completed","coverageArtifactStatus":"produced"}]}""");
            var result = await CoverageFanIn.CaptureAsync(fixture.Capture() with
            {
                OutputMode = CoverageOutputMode.ReplaceOwned
            });

            Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
            Assert.False(result.Accepted);
            Assert.False(result.Effects.GenerationCommitted);
            Assert.False(result.Effects.OutputCreated);
            Assert.Equal(before, Snapshot(fixture.Captured));
        }
        finally
        {
            File.WriteAllBytes(fixture.Timings, validTimings);
        }
    }

    [Fact]
    public async Task PublicVerifyRejectsTamperedAcceptedGateAndGenerationBinding()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.AcceptAsync(fixture.Accept())).Outcome);

        var gatePath = Path.Join(fixture.Accepted, "primary-gate.json");
        var originalGate = File.ReadAllBytes(gatePath);
        var gate = JsonNode.Parse(originalGate)!.AsObject();
        gate["passed"] = false;
        File.WriteAllText(gatePath, gate.ToJsonString());
        AssertVerifyRejected(await CoverageFanIn.VerifyAsync(fixture.Verify()));
        File.WriteAllBytes(gatePath, originalGate);

        var generationPath = Path.Join(fixture.Accepted, "generation.json");
        var marker = JsonNode.Parse(File.ReadAllBytes(generationPath))!.AsObject();
        marker["generation"] = Guid.NewGuid().ToString("N");
        File.WriteAllText(generationPath, marker.ToJsonString());
        AssertVerifyRejected(await CoverageFanIn.VerifyAsync(fixture.Verify()));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("stage")]
    [InlineData("policyDigest")]
    [InlineData("physicalIdentity")]
    [InlineData("materialization")]
    [InlineData("generation")]
    [InlineData("operationId")]
    public async Task PublicVerifyRejectsEachInvalidCompletionBindingWithoutChangingCustody(string field)
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.AcceptAsync(fixture.Accept())).Outcome);

        var markerPath = Path.Join(fixture.Accepted, "generation.json");
        MutateGenerationMarker(markerPath, field);
        var afterMutation = Snapshot(fixture.Accepted);

        var result = await CoverageFanIn.VerifyAsync(fixture.Verify());

        AssertVerifyRejected(result);
        Assert.Equal(afterMutation, Snapshot(fixture.Accepted));
    }

    [Fact]
    public async Task PublicVerifyRejectsCopiedGenerationWithoutAdoptingItsCustody()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.AcceptAsync(fixture.Accept())).Outcome);

        var copied = fixture.CopyDirectory(fixture.Accepted, "accepted-copy");
        var before = Snapshot(copied);
        var copiedRequest = fixture.Verify() with { AcceptedOutputDirectory = copied };

        AssertVerifyRejected(await CoverageFanIn.VerifyAsync(copiedRequest));

        Assert.Equal(before, Snapshot(copied));
        using var store = CoverageArtifactStore.OpenReadOnly();
        Assert.Null(store.GetCurrentRun(copied));
    }

    [Fact]
    public async Task PublicVerifyWithMissingCoordinationStoreCreatesNothing()
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded,
            (await CoverageFanIn.AcceptAsync(fixture.Accept())).Outcome);
        Directory.Delete(fixture.StoreHome, recursive: true);
        var before = Snapshot(fixture.Temp.Path);

        var result = await CoverageFanIn.VerifyAsync(fixture.Verify());

        AssertVerifyRejected(result);
        Assert.False(Directory.Exists(fixture.StoreHome));
        Assert.Equal(before, Snapshot(fixture.Temp.Path));
    }

    private static void MutateGenerationMarker(string markerPath, string field)
    {
        var marker = JsonNode.Parse(File.ReadAllBytes(markerPath))!.AsObject();
        marker[field] = field switch
        {
            "schemaVersion" => 1,
            "stage" => "invalid-stage",
            "policyDigest" => new string('0', 64),
            "physicalIdentity" => "different-directory-identity",
            "materialization" => "unrecognized",
            "generation" or "operationId" => "not-a-guid",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        File.WriteAllText(markerPath, marker.ToJsonString());
    }

    [Theory]
    [InlineData("schemaVersion", "1")]
    [InlineData("policyDigest", "\"other\"")]
    [InlineData("source.commit", "\"other\"")]
    [InlineData("run.attempt", "2")]
    [InlineData("coverageSha256", "\"other\"")]
    [InlineData("primarySha256", "\"other\"")]
    [InlineData("inputs", "[]")]
    [InlineData("inputs.0.manifestSha256", "\"other\"")]
    [InlineData("inputs.0.producerId", "\"other\"")]
    public async Task AcceptanceRejectsSelfConsistentlyPublishedCompositionFaults(string field, string value)
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        // A producer can publish internally consistent hashes and custody for false semantic facts.
        // Acceptance must independently bind those facts to the original captures.
        await RepublishFault(fixture, fixture.Composed, "composition", "verification.json", field, value);
        var before = Snapshot(fixture.Composed);
        var result = await CoverageFanIn.AcceptAsync(fixture.Accept());
        AssertVerifyRejected(result);
        Assert.False(Directory.Exists(fixture.Accepted));
        Assert.Equal(before, Snapshot(fixture.Composed));
    }

    [Theory]
    [InlineData("schemaVersion", "1")]
    [InlineData("policyDigest", "\"other\"")]
    [InlineData("source.commit", "\"other\"")]
    [InlineData("run.attempt", "2")]
    [InlineData("evaluationIntent", "\"historicalReplay\"")]
    [InlineData("compositionReceiptSha256", "\"other\"")]
    [InlineData("primaryGateSha256", "\"other\"")]
    [InlineData("patchOutcome", "\"PatchNotRequested\"")]
    [InlineData("inputs", "[]")]
    [InlineData("composedGateSha256", "\"other\"")]
    [InlineData("diffSha256", "\"other\"")]
    public async Task VerificationRejectsSelfConsistentlyPublishedAcceptanceFaults(string field, string value)
    {
        using var fixture = await Fixture.Create();
        await fixture.SelectPatch();
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.ComposeAsync(fixture.Compose())).Outcome);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.AcceptAsync(fixture.Accept())).Outcome);
        await RepublishFault(fixture, fixture.Accepted, "acceptance", "acceptance.json", field, value);
        var before = Snapshot(fixture.Accepted);
        AssertVerifyRejected(await CoverageFanIn.VerifyAsync(fixture.Verify()));
        Assert.Equal(before, Snapshot(fixture.Accepted));
    }

    private static async Task RepublishFault(Fixture fixture, string directory, string stage, string artifact,
        string field, string value)
    {
        var files = Directory.EnumerateFiles(directory).Where(path => !Path.GetFileName(path).StartsWith('.')
            && Path.GetFileName(path) != CoverageFanInIO.CompletionName)
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);
        var node = JsonNode.Parse(files[artifact])!;
        var path = field.Split('.');
        var parent = node;
        foreach (var part in path[..^1]) parent = parent is JsonArray ? parent[int.Parse(part)]! : parent[part]!;
        if (parent is JsonArray) parent[int.Parse(path[^1])] = JsonNode.Parse(value);
        else parent[path[^1]] = JsonNode.Parse(value);
        files[artifact] = JsonSerializer.SerializeToUtf8Bytes(node);
        var limits = CoveragePolicyEncoding.Freeze(fixture.Policy).Limits!;
        var bounds = new CoverageFanInBounds(limits.XmlBytes, limits.JsonBytes, limits.TotalBytes, limits.Depth,
            limits.LineRecordsPerReport, limits.XmlElementsPerReport, limits.AttributesPerElement,
            limits.TokenCharacters, limits.BranchRecordsPerReport, limits.TotalRecords);
        await CoverageFanInPublication.PublishAsync(directory, fixture.Repository, stage,
            Guid.NewGuid().ToString("N"), CoveragePolicyEncoding.Digest(fixture.Policy), files, true, bounds, CancellationToken.None);
    }

    [Theory]
    [InlineData("C:/agent/work")]
    [InlineData(@"C:\agent\work")]
    [InlineData(@"\\server\share\repo")]
    public async Task PortableImportAcceptsDeclaredForeignAbsoluteRootsWithoutClaimingLocalExecution(string sourceRoot)
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        var transported = fixture.CopyDirectory(fixture.Captured, "transported");
        var manifestPath = Path.Join(transported, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!;
        manifest["sourceRoot"] = sourceRoot;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var output = Path.Join(fixture.Temp.Path, "imported");
        var result = await CoverageFanIn.CaptureAsync(new(fixture.Policy, fixture.Repository, output, "primary",
            ImportDirectory: transported, OriginalSourceRoot: sourceRoot));
        Assert.Equal(CoverageOperationOutcome.Succeeded, result.Outcome);
        Assert.True(result.Effects.Imported);
        Assert.Equal(CoverageObservation.TrustedProducer, result.Observation);
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.ComposeAsync(fixture.Compose() with
        { PrimaryCaptureDirectory = output })).Outcome);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("hits")]
    [InlineData("line-number")]
    [InlineData("filename")]
    public async Task ImportedPrimaryMustContainValidMeasurablePointsDespiteMatchingPayloadHashes(string defect)
    {
        using var fixture = await Fixture.Create();
        Assert.Equal(CoverageOperationOutcome.Succeeded, (await CoverageFanIn.CaptureAsync(fixture.Capture())).Outcome);
        var transported = fixture.CopyDirectory(fixture.Captured, "transported");
        var reportPath = Path.Join(transported, "coverage.cobertura.xml");
        var report = System.Xml.Linq.XDocument.Load(reportPath);
        switch (defect)
        {
            case "root": report.Root!.Name = "other"; break;
            case "hits": report.Descendants("line").Single().SetAttributeValue("hits", "-1"); break;
            case "line-number": report.Descendants("line").Single().SetAttributeValue("number", "0"); break;
            default: report.Descendants("class").Single().SetAttributeValue("filename", "../outside.cs"); break;
        }
        report.Save(reportPath);
        var manifestPath = Path.Join(transported, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!;
        manifest["files"]!["coverage.cobertura.xml"] = Hash(File.ReadAllBytes(reportPath));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var output = Path.Join(fixture.Temp.Path, "imported");
        var result = await CoverageFanIn.CaptureAsync(new(fixture.Policy, fixture.Repository, output, "primary",
            ImportDirectory: transported, OriginalSourceRoot: manifest["sourceRoot"]!.GetValue<string>()));
        AssertVerifyRejected(result);
        Assert.False(Directory.Exists(output));
    }

    private static async Task AssertImportRejected(Fixture fixture, string sourceDirectory, string producerId,
        string name, Action<JsonObject>? mutate, bool invalidateAndRehashTimings = false,
        bool addUnknownFile = false, string? originalSourceRoot = null)
    {
        var input = fixture.CopyDirectory(sourceDirectory, "transport-" + name);
        var manifestPath = Path.Join(input, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        mutate?.Invoke(manifest);
        if (invalidateAndRehashTimings)
        {
            var timingsPath = Path.Join(input, "timings.json");
            var timings = JsonNode.Parse(File.ReadAllBytes(timingsPath))!.AsObject();
            timings["configuration"] = "Debug";
            File.WriteAllText(timingsPath, timings.ToJsonString());
            manifest["files"]!["timings.json"] = Hash(File.ReadAllBytes(timingsPath));
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        if (addUnknownFile) File.WriteAllText(Path.Join(input, "unlisted.txt"), "unexpected");

        var output = Path.Join(fixture.Temp.Path, "imported-" + name);
        var request = new CoverageCaptureRequest(fixture.Policy, fixture.Repository, output, producerId,
            ImportDirectory: input,
            OriginalSourceRoot: originalSourceRoot ?? CoverageRunOutputLease.NormalizePlatformPath(fixture.Repository));
        var result = await CoverageFanIn.CaptureAsync(request);

        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.False(result.Effects.OutputCreated);
        Assert.False(Directory.Exists(output));
    }

    private static void AssertVerifyRejected(CoverageFanInResult result)
    {
        Assert.Equal(CoverageOperationOutcome.Rejected, result.Outcome);
        Assert.False(result.Accepted);
        Assert.False(result.Verified);
        Assert.False(result.Effects.OutputCreated);
        Assert.False(result.Effects.GenerationCommitted);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string[] Snapshot(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Select(path => (Path: Path.GetRelativePath(directory, path), Hash: Hash(File.ReadAllBytes(path))))
        .OrderBy(item => item.Path, StringComparer.Ordinal)
        .Select(item => item.Path + ":" + item.Hash).ToArray();

    private sealed class Fixture : IDisposable
    {
        internal TestDirectory Temp { get; } = TestDirectory.Create();
        private readonly string? _priorHome = Environment.GetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME");
        internal string Repository => Path.Join(Temp.Path, "repo");
        internal string Report => Path.Join(Temp.Path, "producer", "coverage.xml");
        internal string Timings => Path.Join(Temp.Path, "producer", "timings.json");
        internal string Captured => Path.Join(Temp.Path, "captured");
        internal string Composed => Path.Join(Temp.Path, "composed");
        internal string Accepted => Path.Join(Temp.Path, "accepted");
        internal string StoreHome => Path.Join(Temp.Path, "store");
        internal static string CurrentPlatform => OperatingSystem.IsMacOS() ? "Darwin"
            : OperatingSystem.IsWindows() ? "Windows" : "Linux";
        internal CoverageFanInPolicy Policy { get; private set; } = null!;
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture();
            Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", Path.Join(f.Temp.Path, "store"));
            f.Temp.WriteFile("repo/checks/Verification.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await Command(f.Repository, "git", "init");
            await Command(f.Repository, "git", "config", "user.email", "test@example.invalid");
            await Command(f.Repository, "git", "config", "user.name", "AppSurface tests");
            await Command(f.Repository, "git", "add", ".");
            await Command(f.Repository, "git", "commit", "-m", "fixture");
            f.Policy = new(new(await Command(f.Repository, "git", "rev-parse", "HEAD"),
                    await Command(f.Repository, "git", "rev-parse", "HEAD^{tree}"), await Command(f.Repository, "dotnet", "--version"), "Release"),
                new("local", "workflow-tests", Guid.NewGuid().ToString("N"), 1), ["checks/Verification.csproj"], [], new(CoverageGateMode.AggregateOnly));
            f.Temp.WriteFile("producer/coverage.xml", """<coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2" line-rate="1" branch-rate="1"><packages><package name="Example"><classes><class name="Example" filename="src/Example.cs"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)"/></lines></class></classes></package></packages></coverage>""");
            f.Temp.WriteFile("producer/timings.json", """{"configuration":"Release","coverageDriver":"collector","merge":{"exitCode":0},"artifacts":{"coverageFiles":1},"projects":[{"project":"checks/Verification.csproj","exitCode":0,"executionStatus":"completed","coverageArtifactStatus":"produced"}]}""");
            return f;
        }
        internal CoverageCaptureRequest Capture() => new(Policy, Repository, Captured, "primary", Report, Timings);
        internal void SetSupplements(IReadOnlyList<CoverageSupplement> supplements) =>
            Policy = Policy with { Supplements = supplements };
        internal async Task<string> CaptureSupplement(CoverageSupplement supplement, string outputName)
        {
            var output = TestPathUtils.PathUnder(Temp.Path, outputName);
            var request = new CoverageCaptureRequest(Policy, Repository, output, supplement.Id,
                CoveragePath: Report, RawCoveragePath: Report);
            var result = await CoverageFanIn.CaptureAsync(request);
            Assert.Equal(CoverageOperationOutcome.Succeeded, result.Outcome);
            return output;
        }
        internal string CopyDirectory(string source, string name)
        {
            var destination = TestPathUtils.PathUnder(Temp.Path, name);
            Directory.CreateDirectory(destination);
            foreach (var path in Directory.EnumerateFiles(source))
                File.Copy(path, TestPathUtils.PathUnder(destination, Path.GetFileName(path)));
            return destination;
        }
        internal CoverageComposeRequest Compose() => new(Policy, Repository, Captured, [], Composed);
        internal CoverageAcceptRequest Accept() => new(Policy, Repository, Captured, [], Composed, Accepted);
        internal CoverageVerifyRequest Verify() => new(Policy, Repository, Captured, [], Composed, Accepted);
        internal async Task SelectPatch()
        {
            Temp.WriteFile("repo/src/Example.cs", "public class Example { }\n");
            await Command(Repository, "git", "add", ".");
            await Command(Repository, "git", "commit", "-m", "measurable source");
            Policy = Policy with
            {
                Source = Policy.Source with
                { Commit = await Command(Repository, "git", "rev-parse", "HEAD"), Tree = await Command(Repository, "git", "rev-parse", "HEAD^{tree}") },
                Gate = new(CoverageGateMode.AggregateAndPatch, DiffBase: "HEAD^")
            };
        }
        private static async Task<string> Command(string root, string executable, params string[] args)
        {
            using var process = new Process
            {
                StartInfo = new(executable)
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            return (await output).Trim();
        }
        public void Dispose() { Environment.SetEnvironmentVariable("APPSURFACE_ARTIFACT_HOME", _priorHome); Temp.Dispose(); }
    }
}
