using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;

namespace ForgeTrust.AppSurface.Cli.Tests;

/// <summary>Exercises receipt admission, execution ownership, publication and audit recovery through the private session API.</summary>
public sealed class EvidenceProducerReuseSessionTests
{
    private static readonly IReadOnlyDictionary<string, string> Material = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mode"] = "private-material-sentinel",
    };

    [Fact]
    public void CoverageToolIdentityUsesCoordinatedVersionsAndRepositoryRelativeSolutionOnly()
    {
        var producer = new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["assertion"], [], 30)
        {
            CoverageExecution = new EvidenceCoverageExecutionOptions(),
        };
        var first = EvidenceReuseExecutionResolver.CreateToolIdentity(producer, "/work/checkout-a", "/work/checkout-a/src/App.sln", "10.0.402");
        var moved = EvidenceReuseExecutionResolver.CreateToolIdentity(producer, "/different/root", "/different/root/src/App.sln", "10.0.402");

        Assert.Equal("src/App.sln", first["solution"]);
        Assert.Equal(first, moved);
        Assert.Equal("10.0.402", first["sdk"]);
        Assert.Equal("5.5.10", first["reportGeneratorVersion"]);
        Assert.StartsWith("sha256:", first["reportGeneratorManifest"], StringComparison.Ordinal);
        Assert.Equal(StrictReportGeneratorRuntimeLocator.GetPinnedManifestDigest(), first["reportGeneratorManifest"]);
        Assert.DoesNotContain("/work", string.Join("\n", first.Values), StringComparison.Ordinal);
        Assert.DoesNotContain("commit", string.Join("\n", first.Keys), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("endpoint", string.Join("\n", first.Keys), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FreshExecutionPublishesOnlyAfterCleanupAndNextSessionRestoresIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var first = await fixture.PrepareAsync())
        {
            fixture.PublishedArtifactRoot = fixture.LastArtifactRoot;
            Assert.Equal(EvidenceProducerReuseStatus.Miss, Assert.Single(first.Decisions).Status);
            Assert.True(await first.BeginExecutionAsync(CancellationToken.None), string.Join("; ", first.Decisions.Select(static item => item.Diagnostic)));
            var produced = await fixture.Producer.ProduceAsync(
                new EvidenceProducerContext(fixture.Plan, fixture.ProducerDeclaration, TimeProvider.System, first.GetWriter(fixture.ProducerDeclaration)),
                CancellationToken.None);
            Assert.Equal(EvidenceProducerOutcome.Passed, produced.Outcome);
            Assert.True(await first.BeforeCleanupAsync(actualProducerTasksSettled: true, CancellationToken.None));
            var manifest = EvidenceManifestBuilder.Build(fixture.Plan, [produced], metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
            var completed = await first.CompleteAsync(manifest, CancellationToken.None);
            Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
            Assert.Contains("reuse.published", Assert.Single(first.Decisions).Reason, StringComparison.Ordinal);
        }

        await using var second = await fixture.PrepareAsync();
        var receiptDecision = Assert.Single(second.Decisions);
        var restored = Assert.Single(second.ReusedResults);
        Assert.Equal(fixture.ProducerDeclaration.Id, restored.Key);
        Assert.Equal(EvidenceProducerOutcome.Reused, restored.Value.Outcome);
        Assert.Equal(1, fixture.Producer.ExecutionCount);
        Assert.True(File.Exists(Path.Combine(fixture.LastArtifactRoot, "reports", "report.txt")));
        Assert.True(await second.BeginExecutionAsync(CancellationToken.None));
        Assert.Equal(receiptDecision, Assert.Single(second.Decisions));
    }

    [Theory]
    [InlineData("inventory-slot-ineligible")]
    [InlineData("reuse.inventory-slot-ineligible")]
    public async Task FreshPassingProducerWithIncompleteReceiptInventoryNeverPublishes(string reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Producer.ReceiptIneligibilityReason = reason;

        await using (var session = await fixture.PrepareAsync())
        {
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None), string.Join("; ", session.Decisions.Select(static item => item.Diagnostic)));
            var produced = await fixture.Producer.ProduceAsync(new EvidenceProducerContext(fixture.Plan,
                fixture.ProducerDeclaration, TimeProvider.System, session.GetWriter(fixture.ProducerDeclaration)), CancellationToken.None);
            Assert.Equal(EvidenceProducerOutcome.Passed, produced.Outcome);
            Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
            var manifest = EvidenceManifestBuilder.Build(fixture.Plan, [produced], metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
            Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
            Assert.Equal(EvidenceExecutionVerdict.Passed,
                (await session.CompleteAsync(manifest, CancellationToken.None)).ExecutionVerdict);
            Assert.Equal("reuse.inventory-slot-ineligible", Assert.Single(session.Decisions).Reason);
        }

        await using var next = await fixture.PrepareAsync();
        Assert.Empty(next.ReusedResults);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalSettlementFailurePreservesFreshVerdictAndAuditFailure(bool verifyReuse)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (verifyReuse) await fixture.PublishFreshAsync();
        fixture.Producer.ReceiptIneligibilityReason = "inventory-slot-ineligible";

        await using var session = await fixture.PrepareAsync(verifyReuse: verifyReuse);
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var produced = await fixture.Producer.ProduceAsync(new EvidenceProducerContext(fixture.Plan,
            fixture.ProducerDeclaration, TimeProvider.System, session.GetWriter(fixture.ProducerDeclaration)), CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.Passed, produced.Outcome);
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        var manifest = EvidenceManifestBuilder.Build(fixture.Plan, [produced],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });

        var indexPath = Path.Combine(fixture.StoreRoot, "quota-index.json");
        var savedIndexPath = Path.Combine(fixture.Root, "quota-index.saved.json");
        Assert.True(File.Exists(indexPath));
        var originalIndex = await File.ReadAllBytesAsync(indexPath);
        File.Move(indexPath, savedIndexPath);
        Directory.CreateDirectory(indexPath);
        try
        {
            var completed = await session.CompleteAsync(manifest, CancellationToken.None);

            if (verifyReuse)
            {
                Assert.NotEqual(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
                Assert.Contains("reuse.audit_finalization_failed",
                    Assert.Single(completed.ProducerResults).Diagnostic ?? string.Empty, StringComparison.Ordinal);
            }
            else
                Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);

            Assert.Contains("Settlement of this key's execution authority could not be confirmed.",
                Assert.Single(session.Decisions).Diagnostic, StringComparison.Ordinal);
            Assert.Equal(Convert.ToHexString(originalIndex),
                Convert.ToHexString(await File.ReadAllBytesAsync(savedIndexPath)));
        }
        finally
        {
            if (Directory.Exists(indexPath)) Directory.Delete(indexPath);
            if (File.Exists(savedIndexPath)) File.Move(savedIndexPath, indexPath);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RefusedProducerSettlementFailurePreservesConfirmedPublication(int refusedIndex)
    {
        await using var fixture = await Fixture.CreateAsync(producerCount: 2);
        var refused = fixture.ProducerDeclarations[refusedIndex];
        var published = fixture.ProducerDeclarations[1 - refusedIndex];
        var settlementAttempts = new List<string>();
        await using var session = await fixture.PrepareAsync(configureOptions: options => options with
        {
            BeforeRefusedPublicationSettlementForTests = producerId =>
            {
                settlementAttempts.Add(producerId);
                throw new IOException("injected refusal settlement failure");
            }
        });
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var results = new List<EvidenceProducerResult>();
        foreach (var (declaration, producer) in fixture.ProducerDeclarations.Zip(fixture.Producers))
        {
            results.Add(await producer.ProduceAsync(new EvidenceProducerContext(fixture.Plan, declaration,
                TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None));
        }
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        await File.WriteAllTextAsync(Path.Join(fixture.LastArtifactRoot, refused.Id, "reports", "report.txt"),
            "changed after cleanup");
        var manifest = EvidenceManifestBuilder.Build(fixture.Plan, results,
            resourceResults: [new EvidenceResourceResult("database", EvidenceResourceOutcome.Ready, 1)],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });

        var completed = await session.CompleteAsync(manifest, CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal([refused.Id], settlementAttempts);
        var publishedDecision = Assert.Single(session.Decisions, decision => decision.ProducerId == published.Id);
        var refusedDecision = Assert.Single(session.Decisions, decision => decision.ProducerId == refused.Id);
        Assert.Equal(EvidenceReceiptPublication.Published, publishedDecision.Publication);
        Assert.Equal("reuse.published", publishedDecision.Reason);
        Assert.Equal(EvidenceReceiptPublication.Suppressed, refusedDecision.Publication);
        Assert.Equal("reuse.payload-publication-failed", refusedDecision.Reason);
        Assert.Contains("could not be confirmed", refusedDecision.Diagnostic, StringComparison.Ordinal);
        var publishedState = Assert.IsType<EvidenceReceiptState>(await fixture.ReadStateAsync(publishedDecision.ProducerKey!));
        Assert.NotNull(publishedState.PayloadDirectoryName);
        Assert.Null(publishedState.InFlightExecutionId);
        var refusedState = Assert.IsType<EvidenceReceiptState>(await fixture.ReadStateAsync(refusedDecision.ProducerKey!));
        Assert.Null(refusedState.PayloadDirectoryName);
        Assert.Equal(session.ExecutionId, refusedState.InFlightExecutionId);
    }

    [Fact]
    public async Task KeyMaterialResultIsAnImmutableSnapshotDistinctFromProducerOwnedDictionary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = new Dictionary<string, string>(StringComparer.Ordinal) { ["mode"] = "before" };
        fixture.Producer.KeyMaterial = (_, _) => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(source);
        await using var execution = EvidenceProducerKeyMaterialExecution.Start(fixture.Producer,
            new EvidenceProducerKeyMaterialContext(fixture.Plan, fixture.ProducerDeclaration, TimeProvider.System),
            TimeSpan.FromSeconds(1), CancellationToken.None);

        var prepared = await execution.WaitAsync();

        Assert.True(prepared.Settled);
        var captured = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(prepared.Material);
        Assert.NotSame(source, captured);
        source["mode"] = "after";
        Assert.Equal("before", captured["mode"]);
    }

    [Fact]
    public async Task InvalidSettledKeyMaterialFallsBackToFreshPassWithoutPublishing()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Producer.KeyMaterial = static (_, _) => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["large"] = new string('x', 4097) });

        await using (var session = await fixture.PrepareAsync())
        {
            Assert.False(session.IsUnavailable);
            Assert.Equal(EvidenceProducerReuseStatus.Ineligible, Assert.Single(session.Decisions).Status);
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
            var produced = await fixture.Producer.ProduceAsync(new EvidenceProducerContext(fixture.Plan,
                fixture.ProducerDeclaration, TimeProvider.System, session.GetWriter(fixture.ProducerDeclaration)), CancellationToken.None);
            Assert.Equal(EvidenceProducerOutcome.Passed, produced.Outcome);
            Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
            var manifest = EvidenceManifestBuilder.Build(fixture.Plan, [produced], metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
            Assert.Equal(EvidenceExecutionVerdict.Passed,
                (await session.CompleteAsync(manifest, CancellationToken.None)).ExecutionVerdict);
        }

        await using var next = await fixture.PrepareAsync();
        Assert.Empty(next.ReusedResults);
    }

    [Fact]
    public async Task HistoryOnlyAndLinkedWorktreeChangesPreserveTheContentKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CommitAsync("baseline");
        await fixture.PublishFreshAsync();
        await fixture.CommitAsync("history only");
        var historySession = await fixture.PrepareAsync();
        await using (historySession)
            Assert.True(historySession.ReusedResults.ContainsKey(fixture.ProducerDeclaration.Id),
                string.Join("; ", historySession.Decisions.Select(static item => item.Reason + ":" + item.Diagnostic)));

        var worktree = fixture.RepositoryRoot + "-linked";
        await fixture.GitAsync("worktree", "add", "--detach", worktree, "HEAD");
        try
        {
            var worktreeFixture = await Fixture.CreateForExistingRepositoryAsync(fixture, worktree);
            await using (worktreeFixture)
            await using (var linkedSession = await worktreeFixture.PrepareAsync())
            {
                Assert.Contains(worktreeFixture.ProducerDeclaration.Id, linkedSession.ReusedResults.Keys);
                Assert.True(await linkedSession.BeginExecutionAsync(CancellationToken.None));
            }
        }
        finally
        {
            await fixture.GitAsync("worktree", "remove", "--force", worktree);
        }
    }

    [Fact]
    public async Task IndependentRepositoryNamespaceChangeBeforeExecutionDoesNotAdmitProducer()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CommitAsync("baseline");
        var preparedIdentity = await fixture.BuildIdentityAsync(fixture.ProducerDeclaration);
        var replacementRepository = Path.Combine(fixture.Root, "replacement-repository");
        await RunGitAtAsync(fixture.Root, "clone", "--no-hardlinks", fixture.RepositoryRoot, replacementRepository);
        await using var session = await fixture.PrepareAsync();

        var gitDirectory = Path.Combine(fixture.RepositoryRoot, ".git");
        var originalGitDirectory = Path.Combine(fixture.Root, "original-git-directory");
        try
        {
            Directory.Move(gitDirectory, originalGitDirectory);
            await File.WriteAllTextAsync(gitDirectory,
                $"gitdir: {Path.Combine(replacementRepository, ".git")}{Environment.NewLine}");
            await RunGitAtAsync(replacementRepository, "config", "core.worktree", fixture.RepositoryRoot);

            var replacementIdentity = await fixture.BuildIdentityAsync(fixture.ProducerDeclaration);
            Assert.Equal(preparedIdentity.Key, replacementIdentity.Key);
            Assert.NotEqual(preparedIdentity.RepositoryNamespace, replacementIdentity.RepositoryNamespace);

            Assert.False(await session.BeginExecutionAsync(CancellationToken.None));
            Assert.True(session.IsUnavailable);
            Assert.Equal("reuse.prepared_inputs_changed", Assert.Single(session.Decisions).Reason);
            Assert.Equal(0, fixture.Producer.ExecutionCount);
            Assert.Null(EvidenceReceiptQuota.ReadAuthority(fixture.StoreRoot,
                preparedIdentity.RepositoryNamespace, preparedIdentity.Key)?.InFlightExecutionId);
            Assert.Null(EvidenceReceiptQuota.ReadAuthority(fixture.StoreRoot,
                replacementIdentity.RepositoryNamespace, replacementIdentity.Key)?.InFlightExecutionId);
        }
        finally
        {
            if (File.Exists(gitDirectory)) File.Delete(gitDirectory);
            if (Directory.Exists(originalGitDirectory)) Directory.Move(originalGitDirectory, gitDirectory);
        }
    }

    [Fact]
    public async Task ChangedDeclaredInputCreatesANewMissInsteadOfRestoringOldResult()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PublishFreshAsync();
        await fixture.WriteInputAsync("changed bytes");

        await using var changed = await fixture.PrepareAsync();

        Assert.Empty(changed.ReusedResults);
        Assert.Equal(EvidenceProducerReuseStatus.Miss, Assert.Single(changed.Decisions).Status);
        Assert.NotEqual(fixture.PublishedKey, Assert.Single(changed.Decisions).ProducerKey);
    }

    [Fact]
    public async Task MissingInputsAndNonCooperativeCoverageRemainFreshOnlyWithoutCreatingStore()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var producer in new[]
        {
            fixture.ProducerDeclaration with { Inputs = null },
            fixture.ProducerDeclaration with
            {
                Kind = "coverage",
                CoverageExecution = new EvidenceCoverageExecutionOptions(),
            },
        })
        {
            var session = await fixture.PrepareAsync(producer, Path.Combine(fixture.Root, "uncreated-store-" + Guid.NewGuid().ToString("N")));
            await using (session)
            {
                var decision = Assert.Single(session.Decisions);
                Assert.Equal(EvidenceProducerReuseStatus.Ineligible, decision.Status);
                Assert.Contains("reuse.", decision.Reason, StringComparison.Ordinal);
                Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
                Assert.Null(session.GetChildLifetime(producer.Id));
            }
        }

        Assert.False(Directory.EnumerateDirectories(fixture.Root, "uncreated-store-*").Any());
    }

    [Fact]
    public async Task CooperativeCoverageAdmissionClosesAndJoinsItsEmptyChildChannel()
    {
        await using var fixture = await Fixture.CreateAsync(coverage: true);
        await using var session = await fixture.PrepareAsync();

        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.NotNull(session.GetChildLifetime(fixture.ProducerDeclaration.Id));
        Assert.True(await session.BeforeCleanupAsync(actualProducerTasksSettled: true, CancellationToken.None));
        Assert.True(await session.JoinActualTasksAsync());
    }

    [Fact]
    public async Task InputChangeDuringExecutionSuppressesReceiptEvenWhenFreshManifestPasses()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var session = await fixture.PrepareAsync())
        {
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
            var passed = await fixture.Producer.ProduceAsync(
                new EvidenceProducerContext(fixture.Plan, fixture.ProducerDeclaration, TimeProvider.System, session.GetWriter(fixture.ProducerDeclaration)),
                CancellationToken.None);
            await fixture.WriteInputAsync("changed while producer ran");
            Assert.True(await session.BeforeCleanupAsync(actualProducerTasksSettled: true, CancellationToken.None));
            var manifest = EvidenceManifestBuilder.Build(fixture.Plan, [passed], metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
            Assert.Equal(EvidenceExecutionVerdict.Passed, manifest.ExecutionVerdict);
            Assert.Equal(EvidenceExecutionVerdict.Passed, (await session.CompleteAsync(manifest, CancellationToken.None)).ExecutionVerdict);
            Assert.Contains("reuse.inputs_changed", Assert.Single(session.Decisions).Reason, StringComparison.Ordinal);
        }

        await using var next = await fixture.PrepareAsync();
        Assert.Empty(next.ReusedResults);
    }

    [Fact]
    public async Task KeyMaterialChangeDuringExecutionSuppressesReceiptWithoutPersistingRawMaterial()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var session = await fixture.PrepareAsync())
        {
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
            var passed = await fixture.Producer.ProduceAsync(
                new EvidenceProducerContext(fixture.Plan, fixture.ProducerDeclaration, TimeProvider.System, session.GetWriter(fixture.ProducerDeclaration)),
                CancellationToken.None);
            fixture.Producer.Material = new Dictionary<string, string> { ["mode"] = "changed-private-material" };
            Assert.True(await session.BeforeCleanupAsync(actualProducerTasksSettled: true, CancellationToken.None));
            var manifest = EvidenceManifestBuilder.Build(fixture.Plan, [passed], metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
            await session.CompleteAsync(manifest, CancellationToken.None);
            Assert.Contains("reuse.inputs_changed", Assert.Single(session.Decisions).Reason, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("changed-private-material", fixture.ReadStoreText(), StringComparison.Ordinal);
        await using var next = await fixture.PrepareAsync();
        Assert.Empty(next.ReusedResults);
    }

    [Theory]
    [InlineData(EvidenceProducerOutcome.Failed, true, false)]
    [InlineData(EvidenceProducerOutcome.Flaky, true, false)]
    [InlineData(EvidenceProducerOutcome.Unavailable, true, false)]
    [InlineData(EvidenceProducerOutcome.Invalid, true, false)]
    [InlineData(EvidenceProducerOutcome.TimedOut, true, false)]
    [InlineData(EvidenceProducerOutcome.Cancelled, true, false)]
    [InlineData(EvidenceProducerOutcome.Passed, false, true)]
    [InlineData(EvidenceProducerOutcome.Passed, true, false)]
    public async Task FailedIncompleteOrUncleanInvocationsNeverPublish(EvidenceProducerOutcome outcome, bool cleanupCompleted,
        bool completeAssertions)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var session = await fixture.PrepareAsync())
        {
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
            var produced = await fixture.Producer.ProduceAsync(
                new EvidenceProducerContext(fixture.Plan, fixture.ProducerDeclaration, TimeProvider.System, session.GetWriter(fixture.ProducerDeclaration)),
                CancellationToken.None);
            produced = produced with
            {
                Outcome = outcome,
                SatisfiedAssertionIds = completeAssertions ? produced.SatisfiedAssertionIds : []
            };
            Assert.True(await session.BeforeCleanupAsync(actualProducerTasksSettled: true, CancellationToken.None));
            var manifest = EvidenceManifestBuilder.Build(fixture.Plan, [produced], metrics: new EvidenceExecutionMetrics { CleanupCompleted = cleanupCompleted });
            var completed = await session.CompleteAsync(manifest, CancellationToken.None);
            Assert.NotEqual(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        }

        await using var next = await fixture.PrepareAsync();
        Assert.Empty(next.ReusedResults);
        Assert.Equal(!cleanupCompleted, next.IsUnavailable);
    }

    [Fact]
    public async Task EverySelectedKeyIsInFlightBeforeCallerCanStartAnyProducer()
    {
        await using var fixture = await Fixture.CreateAsync(producerCount: 2);
        await using var session = await fixture.PrepareAsync();

        Assert.Equal(2, session.Decisions.Count);
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.All(fixture.Producers, producer => Assert.Equal(0, producer.ExecutionCount));
        foreach (var producer in fixture.ProducerDeclarations)
        {
            var identity = await fixture.BuildIdentityAsync(producer);
            var state = EvidenceReceiptQuota.ReadAuthority(fixture.StoreRoot, identity.RepositoryNamespace, identity.Key);
            Assert.Equal(session.ExecutionId, state?.InFlightExecutionId);
        }
    }

    [Fact]
    public async Task InvalidQuotaIndexDuringTwoKeyDisposalPreservesIndexAndReleasesBothNativeLocks()
    {
        await using var fixture = await Fixture.CreateAsync(producerCount: 2);
        await using var session = await fixture.PrepareAsync();
        var repository = (await fixture.BuildIdentityAsync(fixture.ProducerDeclaration)).RepositoryNamespace;
        var keys = session.Decisions.Select(decision => Assert.IsType<string>(decision.ProducerKey)).ToArray();
        Assert.Equal(2, keys.Length);
        foreach (var key in keys)
        {
            var directory = Path.Join(fixture.StoreRoot, repository, key);
            var busy = Assert.Throws<CoverageExecutionException>(() =>
            {
                using var contender = CoverageArtifactOperationLease.Acquire(directory, exclusive: true);
            });
            Assert.Equal("ASCOV202", busy.Code);
        }

        var indexPath = Path.Join(fixture.StoreRoot, "quota-index.json");
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(indexPath))!;
        index["SchemaVersion"] = int.MaxValue;
        var invalidBytes = Encoding.UTF8.GetBytes(index.ToJsonString());
        await File.WriteAllBytesAsync(indexPath, invalidBytes);

        await session.DisposeAsync();

        Assert.Equal(invalidBytes, await File.ReadAllBytesAsync(indexPath));
        using var first = CoverageArtifactOperationLease.Acquire(Path.Join(fixture.StoreRoot, repository, keys[0]), exclusive: true);
        using var second = CoverageArtifactOperationLease.Acquire(Path.Join(fixture.StoreRoot, repository, keys[1]), exclusive: true);
        first.VerifyBinding();
        second.VerifyBinding();
    }

    [Fact]
    public async Task ResourceReadinessFollowsFreshResultSetExposedByReusedResults()
    {
        await using var fixture = await Fixture.CreateAsync(producerCount: 2);
        await fixture.PublishFreshAllAsync();
        fixture.Producers[1].Material = new Dictionary<string, string> { ["mode"] = "new-resource-bound-key" };

        await using var mixed = await fixture.PrepareAsync();

        Assert.Contains(fixture.ProducerDeclarations[0].Id, mixed.ReusedResults.Keys);
        Assert.DoesNotContain(fixture.ProducerDeclarations[1].Id, mixed.ReusedResults.Keys);
        Assert.True(await mixed.BeginExecutionAsync(CancellationToken.None));
        var freshDeclaration = fixture.ProducerDeclarations[1];
        var fresh = await fixture.Producers[1].ProduceAsync(new EvidenceProducerContext(fixture.Plan, freshDeclaration,
            TimeProvider.System, mixed.GetWriter(freshDeclaration)), CancellationToken.None);
        Assert.True(await mixed.BeforeCleanupAsync(true, CancellationToken.None));

        var results = mixed.ReusedResults.Values.Append(fresh).ToArray();
        var skipped = EvidenceManifestBuilder.Build(fixture.Plan, results, resourceResults: [
            new EvidenceResourceResult("database", EvidenceResourceOutcome.NotRequiredForExecution, 0)],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
        var ready = EvidenceManifestBuilder.Build(fixture.Plan, results, resourceResults: [
            new EvidenceResourceResult("database", EvidenceResourceOutcome.Ready, 1)],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, skipped.ExecutionVerdict);
        Assert.Equal(EvidenceExecutionVerdict.Passed, ready.ExecutionVerdict);
        await mixed.CompleteAsync(ready, CancellationToken.None);
    }

    [Fact]
    public async Task KeyLeaseRemainsHeldUntilWholeInvocationPublicationCompletes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var identity = await fixture.BuildIdentityAsync(fixture.ProducerDeclaration);
        var keyDirectory = Path.Join(fixture.StoreRoot, identity.RepositoryNamespace, identity.Key);
        var session = await fixture.PrepareAsync();
        try
        {
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None),
                string.Join("; ", session.Decisions.Select(static decision => decision.Diagnostic)));
            var produced = await fixture.Producer.ProduceAsync(
                new EvidenceProducerContext(fixture.Plan, fixture.ProducerDeclaration, TimeProvider.System, session.GetWriter(fixture.ProducerDeclaration)),
                CancellationToken.None);
            Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
            var busy = Assert.Throws<CoverageExecutionException>(() =>
            {
                using var contender = CoverageArtifactOperationLease.Acquire(keyDirectory, exclusive: true);
            });
            Assert.Equal("ASCOV202", busy.Code);
            Assert.Equal(session.ExecutionId, (await fixture.ReadStateAsync(Assert.Single(session.Decisions).ProducerKey!))?.InFlightExecutionId);
            var manifest = EvidenceManifestBuilder.Build(fixture.Plan, [produced], metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
            await session.CompleteAsync(manifest, CancellationToken.None);
            var state = await fixture.ReadStateAsync(session.Decisions[0].ProducerKey!);
            Assert.Null(state?.InFlightExecutionId);
            Assert.NotNull(state?.PayloadDirectoryName);
            var stillOwned = Assert.Throws<CoverageExecutionException>(() =>
            {
                using var contender = CoverageArtifactOperationLease.Acquire(keyDirectory, exclusive: true);
            });
            Assert.Equal("ASCOV202", stillOwned.Code);
        }
        finally
        {
            await session.DisposeAsync();
        }
        using var released = CoverageArtifactOperationLease.Acquire(keyDirectory, exclusive: true);
        released.VerifyBinding();
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("channel")]
    public async Task AdmissionFailureRollsBackOnlyItsUnstartedAuthorityAndAllowsRetry(string failure)
    {
        await using var fixture = await Fixture.CreateAsync(coverage: failure == "channel", producerCount: 2);
        using var cancellation = new CancellationTokenSource();
        var writes = 0;
        await using var session = await fixture.PrepareAsync(cancellationToken: cancellation.Token,
            configureOptions: options => options with
            {
                BeforeAuthorityWriteForTests = _ =>
                {
                    if (++writes != 2 || failure == "channel") return;
                    if (failure == "cancel") cancellation.Cancel();
                    else throw new IOException("Controlled second authority failure.");
                },
                BeforeChildChannelStartForTests = failure == "channel"
                    ? _ => throw new IOException("Controlled channel startup failure.") : null,
            });

        Assert.False(await session.BeginExecutionAsync(cancellation.Token));
        Assert.True(session.IsUnavailable);
        Assert.Equal(failure == "cancel", session.IsCancelled);
        Assert.Equal(2, writes); // The first durable write completed before the failing admission step.
        Assert.All(fixture.Producers, producer => Assert.Equal(0, producer.ExecutionCount));
        foreach (var decision in session.Decisions)
            Assert.Null((await fixture.ReadStateAsync(decision.ProducerKey!))?.InFlightExecutionId);

        await session.DisposeAsync();
        await using var retry = await fixture.PrepareAsync();
        Assert.False(retry.IsUnavailable);
        Assert.All(retry.Decisions, decision => Assert.Equal(EvidenceProducerReuseStatus.Miss, decision.Status));
    }

    [Fact]
    public async Task PartialAuditAdmissionRollbackRetainsIntentAndOriginalPayload()
    {
        await using var fixture = await Fixture.CreateAsync(producerCount: 2);
        await fixture.PublishFreshAllAsync();
        var writes = 0;
        await using var audit = await fixture.PrepareAsync(verifyReuse: true,
            configureOptions: options => options with
            {
                BeforeAuthorityWriteForTests = _ =>
                {
                    if (++writes == 2) throw new IOException("Controlled second audit admission failure.");
                },
            });
        var firstKey = audit.Decisions[0].ProducerKey!;

        Assert.False(await audit.BeginExecutionAsync(CancellationToken.None));
        var firstState = await fixture.ReadStateAsync(firstKey);
        Assert.Null(firstState?.InFlightExecutionId);
        Assert.Equal(audit.ExecutionId, firstState?.AuditIntentExecutionId);
        Assert.NotNull(firstState?.PayloadDirectoryName);
        Assert.All(fixture.Producers, producer => Assert.Equal(1, producer.ExecutionCount));

        await audit.DisposeAsync();
        await using var ordinary = await fixture.PrepareAsync();
        Assert.False(ordinary.IsUnavailable);
        Assert.Contains(ordinary.Decisions, decision => decision.ProducerKey == firstKey && decision.Reason == "reuse.audit_required");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditComparisonSignalRequiresAnActualVerifiedReceiptAndCompletedComparison(bool seedReceipt)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (seedReceipt) await fixture.PublishFreshAsync();
        await using var session = await fixture.PrepareAsync(verifyReuse: true);
        Assert.False(session.HasMatchedVerifiedAudit(fixture.ProducerDeclaration.Id));
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var produced = await fixture.Producer.ProduceAsync(new EvidenceProducerContext(fixture.Plan,
            fixture.ProducerDeclaration, TimeProvider.System, session.GetWriter(fixture.ProducerDeclaration)), CancellationToken.None);
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        Assert.False(session.HasMatchedVerifiedAudit(fixture.ProducerDeclaration.Id));

        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(fixture.Plan, [produced],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal(seedReceipt, session.HasMatchedVerifiedAudit(fixture.ProducerDeclaration.Id));
    }

    [Fact]
    public async Task AuditFinalizationFailurePreservesObservationAndEnvelopeFields()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PublishFreshAsync();
        await using var audit = await fixture.PrepareAsync(verifyReuse: true);
        Assert.True(await audit.BeginExecutionAsync(CancellationToken.None));
        var produced = await fixture.Producer.ProduceAsync(
            new EvidenceProducerContext(fixture.Plan, fixture.ProducerDeclaration, TimeProvider.System, audit.GetWriter(fixture.ProducerDeclaration)),
            CancellationToken.None);
        fixture.Producer.ReceiptIneligibilityReason = "inventory-file-limit";
        Assert.True(await audit.BeforeCleanupAsync(true, CancellationToken.None));
        var observation = EvidenceManifestBuilder.Build(fixture.Plan, [produced], observationOnly: true,
            envelopeStatus: EvidenceEnvelopeStatus.ValidatedNotAttested, metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });

        var failed = await audit.CompleteAsync(observation, CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, failed.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.ObservationOnly, failed.ClaimKind);
        Assert.Equal(EvidenceEnvelopeStatus.ValidatedNotAttested, failed.EnvelopeStatus);
        Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single(failed.ProducerResults).Outcome);
        Assert.True(EvidenceManifestBuilder.Verify(fixture.Plan, failed));
        var state = await fixture.ReadStateAsync(Assert.Single(audit.Decisions).ProducerKey!);
        Assert.Null(state?.InFlightExecutionId);
        Assert.Equal(audit.ExecutionId, state?.AuditIntentExecutionId);
    }

    [Fact]
    public async Task UnknownPriorInFlightStatePreventsBeginAndFreshProducerWork()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.PrepareAsync();
        Assert.True(await first.BeginExecutionAsync(CancellationToken.None));
        var key = Assert.Single(first.Decisions).ProducerKey!;
        await first.DisposeAsync(); // Releasing a process lease never settles durable execution authority.

        await using var blocked = await fixture.PrepareAsync();
        Assert.True(blocked.IsUnavailable);
        Assert.Contains("reuse.unresolved_execution", Assert.Single(blocked.Decisions).Reason, StringComparison.Ordinal);
        Assert.False(await blocked.BeginExecutionAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Producer.ExecutionCount);
        Assert.NotNull(await fixture.ReadStateAsync(key));
    }

    [Fact]
    public async Task AuditMismatchPersistsQuarantineOrdinaryFreshCannotPublishAndExplicitAuditRecovers()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PublishFreshAsync();

        await using (var audit = await fixture.PrepareAsync(verifyReuse: true))
        {
            Assert.Empty(audit.ReusedResults);
            Assert.True(await audit.BeginExecutionAsync(CancellationToken.None));
            var mismatch = new EvidenceProducerResult(fixture.ProducerDeclaration.Id, EvidenceProducerOutcome.Passed, [],
                Artifacts: []);
            Assert.True(await audit.BeforeCleanupAsync(true, CancellationToken.None));
            var manifest = EvidenceManifestBuilder.Build(fixture.Plan, [mismatch], metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
            var compared = await audit.CompleteAsync(manifest, CancellationToken.None);
            Assert.NotEqual(EvidenceExecutionVerdict.Passed, compared.ExecutionVerdict);
        }

        await using (var ordinary = await fixture.PrepareAsync())
        {
            Assert.Empty(ordinary.ReusedResults);
            Assert.Contains("reuse.audit_required", Assert.Single(ordinary.Decisions).Reason, StringComparison.Ordinal);
            Assert.True(await ordinary.BeginExecutionAsync(CancellationToken.None));
            var produced = await fixture.Producer.ProduceAsync(
                new EvidenceProducerContext(fixture.Plan, fixture.ProducerDeclaration, TimeProvider.System, ordinary.GetWriter(fixture.ProducerDeclaration)),
                CancellationToken.None);
            Assert.True(await ordinary.BeforeCleanupAsync(true, CancellationToken.None));
            await ordinary.CompleteAsync(EvidenceManifestBuilder.Build(fixture.Plan, [produced], metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);
        }

        await using (var stillQuarantined = await fixture.PrepareAsync())
            Assert.Empty(stillQuarantined.ReusedResults);

        await using (var recovery = await fixture.PrepareAsync(verifyReuse: true))
        {
            Assert.Empty(recovery.ReusedResults);
            Assert.False(recovery.HasMatchedVerifiedAudit(fixture.ProducerDeclaration.Id));
            Assert.True(await recovery.BeginExecutionAsync(CancellationToken.None));
            var passed = await fixture.Producer.ProduceAsync(
                new EvidenceProducerContext(fixture.Plan, fixture.ProducerDeclaration, TimeProvider.System, recovery.GetWriter(fixture.ProducerDeclaration)),
                CancellationToken.None);
            Assert.True(await recovery.BeforeCleanupAsync(true, CancellationToken.None));
            var result = await recovery.CompleteAsync(EvidenceManifestBuilder.Build(fixture.Plan, [passed], metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);
            Assert.Equal(EvidenceExecutionVerdict.Passed, result.ExecutionVerdict);
            Assert.False(recovery.HasMatchedVerifiedAudit(fixture.ProducerDeclaration.Id));
        }

        await using var recovered = await fixture.PrepareAsync();
        Assert.Contains(fixture.ProducerDeclaration.Id, recovered.ReusedResults.Keys);
    }

    [Fact]
    public async Task CancellationWithActiveKeyCallbackRetainsTaskAndBlocksExecutionUntilJoined()
    {
        await using var fixture = await Fixture.CreateAsync(callbackCleanupTimeout: TimeSpan.FromMilliseconds(25));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Producer.KeyMaterial = (_, _) =>
        {
            callbackStarted.TrySetResult();
            return new ValueTask<IReadOnlyDictionary<string, string>>(releaseCallback.Task);
        };
        using var cancellation = new CancellationTokenSource();
        var preparing = fixture.PrepareAsync(cancellationToken: cancellation.Token);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var session = await preparing.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.True(session.IsUnavailable);
            Assert.True(session.IsCancelled);
            var join = session.JoinActualTasksAsync();
            Assert.False(join.IsCompleted);
            Assert.Equal(0, fixture.Producer.ExecutionCount);
            releaseCallback.SetResult(Material);
            Assert.True(await join.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            releaseCallback.TrySetResult(Material);
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task LateFatalKeyCallbackExceptionSurfacesOnlyAfterActualJoinAndNeverAdmitsWork()
    {
        await using var fixture = await Fixture.CreateAsync(callbackCleanupTimeout: TimeSpan.FromMilliseconds(25));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Producer.KeyMaterial = (_, _) =>
        {
            callbackStarted.TrySetResult();
            return new ValueTask<IReadOnlyDictionary<string, string>>(releaseCallback.Task);
        };
        using var cancellation = new CancellationTokenSource();
        var preparing = fixture.PrepareAsync(cancellationToken: cancellation.Token);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var session = await preparing.WaitAsync(TimeSpan.FromSeconds(3));
        var join = session.JoinKeyMaterialAsync();
        Assert.False(join.IsCompleted);
        releaseCallback.SetException(new OutOfMemoryException("controlled fatal sentinel"));
        await Assert.ThrowsAsync<OutOfMemoryException>(async () => await join.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, fixture.Producer.ExecutionCount);
        // JoinActualTasks observes the same already-completed fatal task; Dispose remains the final lease owner.
        await Assert.ThrowsAsync<OutOfMemoryException>(async () => await session.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task CurrentAndOriginArtifactRootsDoNotAliasAcrossInvocations()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.PublishFreshAsync();
        var origin = fixture.PublishedArtifactRoot!;
        var current = fixture.NextArtifactRoot();
        await using var session = await fixture.PrepareAsync(artifactRoot: current);
        Assert.Contains(fixture.ProducerDeclaration.Id, session.ReusedResults.Keys);
        Assert.NotEqual(Path.GetFullPath(origin), Path.GetFullPath(current));
        Assert.Equal("origin artifact", await File.ReadAllTextAsync(Path.Combine(origin, "reports", "report.txt")));
        Assert.Equal("origin artifact", await File.ReadAllTextAsync(Path.Combine(current, "reports", "report.txt")));
    }

    private static async Task RunGitAtAsync(string workingDirectory, params string[] arguments)
    {
        var isolationRoot = Path.Combine(Path.GetTempPath(), "evidence-reuse-git-" + Guid.NewGuid().ToString("N"));
        var hooksPath = Path.Combine(isolationRoot, "no-hooks");
        Directory.CreateDirectory(hooksPath);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        var environment = process.StartInfo.Environment;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var name in environment.Keys.Where(name => name.StartsWith("GIT_", comparison)).ToArray())
            environment.Remove(name);
        var nullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = Path.Combine(isolationRoot, "absent-global-config");
        environment["GIT_CONFIG_SYSTEM"] = nullDevice;
        environment["GIT_ATTR_NOSYSTEM"] = "1";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_OPTIONAL_LOCKS"] = "0";
        environment["GIT_NO_LAZY_FETCH"] = "1";
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("commit.gpgsign=false");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.hooksPath=" + hooksPath);
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("user.name=Evidence Reuse Test");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("user.email=evidence-reuse@example.invalid");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Git did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
            {
                await KillAndJoinGitAsync(process, stdout, stderr);
                throw new TimeoutException("Git fixture command exceeded its 20-second deadline.", exception);
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException(error + output);
        }
        finally
        {
            Directory.Delete(isolationRoot, recursive: true);
        }
    }

    private static async Task KillAndJoinGitAsync(Process process, Task stdout, Task stderr)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The process exited between the check and kill; still join it and drain both streams.
        }
        finally
        {
            using var reapDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(reapDeadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly bool _coverage;
        private readonly int _producerCount;
        private readonly TimeSpan _callbackCleanupTimeout;
        private int _artifactRun;

        private Fixture(string root, string repositoryRoot, string storeRoot, bool coverage, int producerCount,
            TimeSpan callbackCleanupTimeout)
        {
            Root = root;
            RepositoryRoot = repositoryRoot;
            StoreRoot = storeRoot;
            _coverage = coverage;
            _producerCount = producerCount;
            _callbackCleanupTimeout = callbackCleanupTimeout;
            ArtifactRoot = NextArtifactRoot();
            ProducerDeclarations = Enumerable.Range(0, producerCount).Select(index => CreateProducer(index, coverage)).ToArray();
            var resources = producerCount > 1
                ? new[] { new EvidenceResourceDeclaration("database", "completion", 10, []) }
                : [];
            var obligations = ProducerDeclarations.Select(producer => new EvidenceObligation("obligation-" + producer.Id,
                "risk", "test obligation", [producer.Id], "assertion-" + producer.Id)).ToArray();
            var profile = new EvidenceProfile("targeted", EvidenceProfileScope.Targeted, resources, ProducerDeclarations, obligations);
            var policy = new EvidencePolicy("policy", "1", profile.Id, [profile], []);
            var paths = Array.Empty<NormalizedDiffPath>();
            var draft = new EvidencePlan("1.1", policy.Id, EvidenceDigest.CanonicalSha256(policy), EvidenceDigest.CanonicalSha256(paths),
                profile, paths, [], string.Empty, policy);
            Plan = draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
            Producers = ProducerDeclarations.Select(producer => new FakeProducer(producer.Id, producer.AssertionIds.Single())).ToArray();
            Producer = Producers[0];
            foreach (var producer in Producers) producer.Material = new Dictionary<string, string>(Material, StringComparer.Ordinal);
        }

        internal string Root { get; }
        internal string RepositoryRoot { get; }
        internal string StoreRoot { get; }
        internal string ArtifactRoot { get; }
        internal string? PublishedArtifactRoot { get; set; }
        internal string LastArtifactRoot { get; private set; } = string.Empty;
        internal EvidencePlan Plan { get; }
        internal EvidenceProducerDeclaration ProducerDeclaration => ProducerDeclarations[0];
        internal EvidenceProducerDeclaration[] ProducerDeclarations { get; }
        internal FakeProducer Producer { get; }
        internal FakeProducer[] Producers { get; }
        internal string? PublishedKey { get; private set; }

        internal static async Task<Fixture> CreateAsync(bool coverage = false, int producerCount = 1,
            TimeSpan? callbackCleanupTimeout = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "evidence-reuse-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var repository = Path.Combine(root, "repository");
            var store = Path.Combine(root, "private-store");
            Directory.CreateDirectory(repository);
            var fixture = new Fixture(root, repository, store, coverage, producerCount,
                callbackCleanupTimeout ?? TimeSpan.FromMilliseconds(100));
            await fixture.GitAsync("init", "--quiet");
            await fixture.GitAsync("config", "user.email", "evidence-test@example.invalid");
            await fixture.GitAsync("config", "user.name", "Evidence Session Test");
            await fixture.WriteInputAsync("input bytes");
            return fixture;
        }

        internal static async Task<Fixture> CreateForExistingRepositoryAsync(Fixture original, string worktree)
        {
            var root = Path.Combine(Path.GetTempPath(), "evidence-reuse-worktree-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new Fixture(root, worktree, original.StoreRoot, original._coverage,
                original._producerCount, original._callbackCleanupTimeout);
            await fixture.WriteInputAsync("input bytes");
            return fixture;
        }

        internal async Task<EvidenceProducerReuseSession> PrepareAsync(EvidenceProducerDeclaration? producer = null,
            string? storeRoot = null, bool verifyReuse = false, TimeSpan? waitTimeout = null,
            string? artifactRoot = null, CancellationToken cancellationToken = default,
            Func<EvidenceProducerReuseOptions, EvidenceProducerReuseOptions>? configureOptions = null)
        {
            var declarations = producer is null ? ProducerDeclarations : [producer];
            var plan = producer is null ? Plan : RebuildPlan(producer);
            var registrations = declarations.ToDictionary(static item => item.Id, item =>
            {
                var registered = Producers.SingleOrDefault(candidate => candidate.Id == item.Id)
                    ?? new FakeProducer(item.Id, item.AssertionIds.Single());
                if (registered.Material is null) registered.Material = new Dictionary<string, string>(Material, StringComparer.Ordinal);
                return new EvidenceReuseProducerRegistration(registered, "implementation-v1",
                    new Dictionary<string, string> { ["tool"] = "tool-v1" });
            }, StringComparer.Ordinal);
            var resourceIdentities = plan.Profile.Resources.ToDictionary(static resource => resource.Id,
                static _ => "sha256:" + new string('d', 64), StringComparer.Ordinal);
            var outputRoot = artifactRoot ?? NextArtifactRoot();
            LastArtifactRoot = outputRoot;
            Directory.CreateDirectory(outputRoot);
            var options = new EvidenceProducerReuseOptions(true, RepositoryRoot, storeRoot ?? StoreRoot,
                waitTimeout ?? TimeSpan.FromSeconds(2), _callbackCleanupTimeout, verifyReuse, false,
                ReadOnlyMemory<byte>.Empty, TimeProvider.System)
            {
                ForbiddenRoots = [outputRoot],
                GetEnvironmentVariable = static _ => null,
            };
            options = configureOptions?.Invoke(options) ?? options;
            var session = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations, resourceIdentities,
                options, declaration => _producerCount > 1 ? Path.Join(outputRoot, declaration.Id) : outputRoot,
                cancellationToken);
            return session;
        }

        internal async Task PublishFreshAsync()
            => await PublishFreshAllAsync();

        internal async Task PublishFreshAllAsync()
        {
            await using var session = await PrepareAsync();
            PublishedArtifactRoot = LastArtifactRoot;
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
            var results = new List<EvidenceProducerResult>();
            foreach (var producer in ProducerDeclarations)
                results.Add(await Producers.Single(candidate => candidate.Id == producer.Id).ProduceAsync(
                    new EvidenceProducerContext(Plan, producer, TimeProvider.System, session.GetWriter(producer)),
                    CancellationToken.None));
            Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
            var resources = Plan.Profile.Resources.Select(resource => new EvidenceResourceResult(resource.Id,
                EvidenceResourceOutcome.Ready, 1)).ToArray();
            var manifest = EvidenceManifestBuilder.Build(Plan, results, resourceResults: resources,
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true });
            await session.CompleteAsync(manifest, CancellationToken.None);
            PublishedKey = session.Decisions.Single(decision => decision.ProducerId == ProducerDeclarations[0].Id).ProducerKey;
        }

        internal async Task<EvidenceProducerIdentity> BuildIdentityAsync(EvidenceProducerDeclaration producer)
        {
            var resources = Plan.Profile.Resources.ToDictionary(static resource => resource.Id,
                static _ => "sha256:" + new string('d', 64), StringComparer.Ordinal);
            var built = await EvidenceProducerIdentityBuilder.BuildAsync(new EvidenceProducerIdentityRequest(
                RepositoryRoot, producer == ProducerDeclaration ? Plan : RebuildPlan(producer), producer,
                "implementation-v1", resources, new Dictionary<string, string> { ["tool"] = "tool-v1" },
                Material, null), CancellationToken.None);
            return Assert.IsType<EvidenceProducerIdentity>(built.Identity);
        }

        internal async Task<EvidenceReceiptState?> ReadStateAsync(string key)
        {
            var producer = ProducerDeclarations.Single(declaration => declaration.Id == ProducerDeclarations.First().Id);
            var identity = await BuildIdentityAsync(producer);
            return EvidenceReceiptQuota.ReadAuthority(StoreRoot, identity.RepositoryNamespace, key);
        }

        internal string NextArtifactRoot()
        {
            var root = Path.Combine(Root, "output-" + Interlocked.Increment(ref _artifactRun).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);
            return root;
        }

        internal async Task WriteInputAsync(string contents)
        {
            var path = Path.Combine(RepositoryRoot, "src", "input.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, contents);
        }

        internal string ReadStoreText()
        {
            if (!Directory.Exists(StoreRoot)) return string.Empty;
            return string.Join("\n", Directory.EnumerateFiles(StoreRoot, "*", SearchOption.AllDirectories)
                .Select(path => File.ReadAllText(path)));
        }

        internal async Task CommitAsync(string message)
        {
            await GitAsync("add", "--all");
            await GitAsync("commit", "--quiet", "--no-gpg-sign", "--allow-empty", "-m", message);
        }

        internal async Task GitAsync(params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = RepositoryRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException(await stderr + await stdout);
        }

        private static EvidenceProducerDeclaration CreateProducer(int index, bool coverage)
        {
            var id = index == 0 ? "producer" : "producer-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var producer = new EvidenceProducerDeclaration(id, coverage ? "coverage" : "test", "1",
                index > 0 ? ["database"] : [], ["assertion-" + id],
                [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 1024)], 30)
            {
                Inputs = new EvidenceProducerInputs(["src/**"], []),
            };
            return coverage
                ? producer with { CoverageExecution = new EvidenceCoverageExecutionOptions() { CooperativeChildLifetime = true } }
                : producer;
        }

        private EvidencePlan RebuildPlan(EvidenceProducerDeclaration producer)
        {
            var producers = ProducerDeclarations.Select(item => item.Id == producer.Id ? producer : item).ToArray();
            var profile = Plan.Profile with { Producers = producers };
            var policy = Plan.PolicySnapshot! with { Profiles = [profile] };
            var draft = Plan with
            {
                PolicyDigest = EvidenceDigest.CanonicalSha256(policy),
                Profile = profile,
                PolicySnapshot = policy,
                PlanDigest = string.Empty,
            };
            return draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
        }

        public ValueTask DisposeAsync()
        {
            new TestDirectory(Root).Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeProducer(string id, string assertion) : IEvidenceProducer, IEvidenceProducerKeyMaterial, IEvidenceProducerReceiptEligibility
    {
        private int _executionCount;
        internal int ExecutionCount => Volatile.Read(ref _executionCount);
        internal IReadOnlyDictionary<string, string>? Material { get; set; }
        internal string? ReceiptIneligibilityReason { get; set; }
        string? IEvidenceProducerReceiptEligibility.ReceiptIneligibilityReason => ReceiptIneligibilityReason;
        internal Func<EvidenceProducerKeyMaterialContext, CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>>? KeyMaterial { get; set; }
        public string Id { get; } = id;

        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executionCount);
            var artifact = await context.Artifacts!.WriteAsync("report", "reports/report.txt", Encoding.UTF8.GetBytes("origin artifact"), cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [assertion], Artifacts: [artifact], ElapsedMilliseconds: 5);
        }

        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(EvidenceProducerKeyMaterialContext context,
            CancellationToken cancellationToken)
        {
            if (KeyMaterial is { } callback) return callback(context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Material ?? new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }
}
