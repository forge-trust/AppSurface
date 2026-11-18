using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReuseSessionCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCapturedPatchCannotBecomeAnExplicitEmptySnapshotDuringPreparation(bool provideEmptySnapshot)
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "patch-bound producer input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("missing-patch-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion") with
        {
            Kind = "coverage",
            CoverageGate = new EvidenceCoverageGateRequirements(95, 85, 95, 85),
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true }
        };
        // A supplied digest is only a claim until preparation also receives its captured bytes.
        var plan = CreatePlan(declaration) with { PatchSha256 = EvidenceDigest.Sha256([]), PlanDigest = string.Empty };
        plan = plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
        var store = TestPathUtils.PathUnder(repository.Root, "patch-bound-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "patch-bound-artifacts");
        var options = ReuseOptions(repository.Root, store) with
        { CapturedPatch = provideEmptySnapshot ? ReadOnlyMemory<byte>.Empty : (ReadOnlyMemory<byte>?)null };

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), options, _ => artifacts, CancellationToken.None);

        var decision = Assert.Single(session.Decisions);
        Assert.False(session.IsUnavailable, decision.Diagnostic);
        if (provideEmptySnapshot)
        {
            Assert.Equal(EvidenceProducerReuseStatus.Miss, decision.Status);
            Assert.NotNull(decision.ProducerKey);
        }
        else
        {
            Assert.Equal(EvidenceProducerReuseStatus.Ineligible, decision.Status);
            Assert.Equal("reuse.identity.patch_unavailable", decision.Reason);
            Assert.Null(decision.ProducerKey);
            Assert.False(Directory.Exists(store));
        }
        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(artifacts));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-TimeSpan.TicksPerMillisecond)]
    public async Task PreparationRejectsNonPositiveOrInfiniteWaitBudgets(long ticks)
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "invalid wait budget input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("invalid-wait-budget-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "invalid-wait-budget-store");
        var options = ReuseOptions(repository.Root, store) with { WaitTimeout = TimeSpan.FromTicks(ticks) };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EvidenceProducerReuseSession.PrepareAsync(
            plan, Registrations(producer), new Dictionary<string, string>(), options,
            _ => TestPathUtils.PathUnder(repository.Root, "invalid-wait-budget-artifacts"), CancellationToken.None));

        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public async Task OversizedCapturedPatchIsRejectedBeforeStoreOrProducerWork()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "oversized patch input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("oversized-patch-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "oversized-patch-store");
        var options = ReuseOptions(repository.Root, store) with
        { CapturedPatch = new byte[20 * 1024 * 1024 + 1] };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EvidenceProducerReuseSession.PrepareAsync(
            plan, Registrations(producer), new Dictionary<string, string>(), options,
            _ => TestPathUtils.PathUnder(repository.Root, "oversized-patch-artifacts"), CancellationToken.None));

        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public async Task PreparationRejectsProtectedCiReuseUsingInvocationLocalEnvironment()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "protected context input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("protected-context-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "protected-context-store");
        var options = ReuseOptions(repository.Root, store) with
        { GetEnvironmentVariable = name => name == "GITHUB_ACTIONS" ? "TRUE" : null };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => EvidenceProducerReuseSession.PrepareAsync(
            plan, Registrations(producer), new Dictionary<string, string>(), options,
            _ => TestPathUtils.PathUnder(repository.Root, "protected-context-artifacts"), CancellationToken.None));

        Assert.Contains("reuse.local_only", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public async Task PreparationCancellationAndCoordinationExceptionsFailClosedWithoutLeakingMessages()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "preparation failure input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("prepare-failure-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "prepare-failure-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "prepare-failure-artifacts");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await using (var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, cancelled.Token))
        {
            Assert.True(session.IsCancelled);
            Assert.True(session.IsUnavailable);
            Assert.Equal("reuse.cancelled", Assert.Single(session.Decisions).Reason);
            Assert.Equal(0, producer.ExecutionCount);
            Assert.False(Directory.Exists(store));
            Assert.False(Directory.Exists(artifacts));
        }

        var holder = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        await using (holder)
        {
            Assert.True(await holder.BeginExecutionAsync(CancellationToken.None));
            var key = Assert.IsType<string>(Assert.Single(holder.Decisions).ProducerKey);
            var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
            var heldAuthority = EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key);
            Assert.Equal(holder.ExecutionId, heldAuthority?.InFlightExecutionId);
            const string secret = "unexpected-progress-exception-secret";
            var coordinationOptions = ReuseOptions(repository.Root, store) with
            {
                WaitTimeout = TimeSpan.FromSeconds(15),
                WaitProgress = (_, _, _) => throw new UnusualProbeException(secret),
            };

            await using var unavailable = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
                new Dictionary<string, string>(), coordinationOptions, _ => artifacts, CancellationToken.None);

            var decision = Assert.Single(unavailable.Decisions);
            Assert.Equal(EvidenceProducerReuseStatus.Unavailable, decision.Status);
            Assert.Equal("reuse.coordination_unavailable", decision.Reason);
            Assert.Contains(nameof(UnusualProbeException), decision.Diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, decision.Diagnostic, StringComparison.Ordinal);
            Assert.True(unavailable.IsUnavailable);
            Assert.False(await unavailable.BeginExecutionAsync(CancellationToken.None));
            Assert.Equal(0, producer.ExecutionCount);
            Assert.False(Directory.Exists(artifacts));
            Assert.Equal(holder.ExecutionId, EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
        }
    }

    [Fact]
    public async Task UnexpectedRegistrationLookupFailureFailsClosedWithoutStartingWork()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "registration lookup failure input");
        await repository.CommitAsync();
        const string secret = "registration-map-private-marker";
        var producer = new ProbeProducer("registration-lookup-failure-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "registration-lookup-failure-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "registration-lookup-failure-artifacts");

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan,
            new ThrowingRegistrationLookup(Registrations(producer), new UnusualProbeException(secret)), new Dictionary<string, string>(),
            ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);

        var decision = Assert.Single(session.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Unavailable, decision.Status);
        Assert.Equal("reuse.coordination_unavailable", decision.Reason);
        Assert.True(session.IsUnavailable);
        Assert.Contains(nameof(UnusualProbeException), decision.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("Framework stack", decision.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, decision.Diagnostic, StringComparison.Ordinal);
        Assert.False(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(store));
        Assert.False(Directory.Exists(artifacts));
    }

    [Theory]
    [InlineData("out-of-memory")]
    [InlineData("stack-overflow")]
    [InlineData("access-violation")]
    [InlineData("app-domain-unloaded")]
    public async Task FatalPreparationExceptionsAreRethrownAfterSessionCleanup(string kind)
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "fatal registration lookup input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("fatal-registration-lookup-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "fatal-registration-lookup-store-" + kind);
        var artifacts = TestPathUtils.PathUnder(repository.Root, "fatal-registration-lookup-artifacts-" + kind);
        Exception expected = kind switch
        {
            "out-of-memory" => new OutOfMemoryException("synthetic fatal preparation fault"),
            "stack-overflow" => new StackOverflowException("synthetic fatal preparation fault"),
            "access-violation" => new AccessViolationException("synthetic fatal preparation fault"),
            "app-domain-unloaded" => new AppDomainUnloadedException("synthetic fatal preparation fault"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => EvidenceProducerReuseSession.PrepareAsync(plan,
            new ThrowingRegistrationLookup(Registrations(producer), expected), new Dictionary<string, string>(),
            ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None));

        Assert.Same(expected, thrown);
        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(store));
        Assert.False(Directory.Exists(artifacts));
    }

    [Fact]
    public async Task DisabledPreparationIsFreshOnlyAndMissingRegistrationFailsStrictly()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "disabled input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("disabled-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "disabled-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "disabled-artifacts");
        var disabled = ReuseOptions(repository.Root, store) with { Enabled = false };

        await using (var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), disabled, _ => artifacts, CancellationToken.None))
        {
            Assert.Equal(EvidenceProducerReuseStatus.Ineligible, Assert.Single(session.Decisions).Status);
            Assert.Equal("reuse.disabled", Assert.Single(session.Decisions).Reason);
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
            Assert.False(Directory.Exists(store));
            Assert.Equal(0, producer.ExecutionCount);
        }

        var missingStore = TestPathUtils.PathUnder(repository.Root, "missing-registration-store");
        await using (var missing = await EvidenceProducerReuseSession.PrepareAsync(plan,
            new Dictionary<string, EvidenceReuseProducerRegistration>(), new Dictionary<string, string>(),
            ReuseOptions(repository.Root, missingStore), _ => artifacts, CancellationToken.None))
        {
            Assert.True(missing.IsUnavailable);
            Assert.Equal(EvidenceProducerReuseStatus.Unavailable, Assert.Single(missing.Decisions).Status);
            Assert.Equal("reuse.coordination_unavailable", Assert.Single(missing.Decisions).Reason);
            Assert.False(Directory.Exists(missingStore));
            Assert.Equal(0, producer.ExecutionCount);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), disabled with { VerifyReuse = true }, _ => artifacts, CancellationToken.None));
    }

    [Fact]
    public async Task AuthorityWriteFailureOnSecondProducerRollsBackWholeAdmission()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "rollback input");
        await repository.CommitAsync();
        var first = new ProbeProducer("rollback-first", "assertion-first");
        var second = new ProbeProducer("rollback-second", "assertion-second");
        var declarations = new[] { ProbeDeclaration(first.Id, "assertion-first"), ProbeDeclaration(second.Id, "assertion-second") };
        var plan = CreatePlan(declarations);
        var store = TestPathUtils.PathUnder(repository.Root, "rollback-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "rollback-artifacts");
        var registrations = Registrations(first, second);
        var writes = 0;
        var options = ReuseOptions(repository.Root, store) with
        { BeforeAuthorityWriteForTests = _ => { if (++writes == 2) throw new UnusualProbeException("authority-fault-marker"); } };

        await using (var failed = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => artifacts, CancellationToken.None))
        {
            var keys = failed.Decisions.Select(decision => Assert.IsType<string>(decision.ProducerKey)).ToArray();
            var identity = await BuildIdentityAsync(repository.Root, plan, declarations[0]);
            Assert.False(await failed.BeginExecutionAsync(CancellationToken.None));
            Assert.Equal(2, writes);
            Assert.True(failed.IsUnavailable);
            Assert.All(failed.Decisions, decision => Assert.Equal(EvidenceProducerReuseStatus.Unavailable, decision.Status));
            Assert.All(failed.Decisions, decision => Assert.DoesNotContain("authority-fault-marker", decision.Diagnostic, StringComparison.Ordinal));
            Assert.All(new[] { first, second }, producer => Assert.Equal(0, producer.ExecutionCount));
            foreach (var key in keys)
                Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
            Assert.False(Directory.Exists(artifacts));
        }

        var retryOptions = ReuseOptions(repository.Root, store);
        await using var retry = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), retryOptions, _ => artifacts, CancellationToken.None);
        Assert.True(await retry.BeginExecutionAsync(CancellationToken.None));
        Assert.All(retry.Decisions, decision => Assert.Equal(EvidenceProducerReuseStatus.Miss, decision.Status));
        await retry.CompleteUnsuccessfulSettlementAsync(actualCleanupCompleted: true);
        var finalIdentity = await BuildIdentityAsync(repository.Root, plan, declarations[0]);
        foreach (var decision in retry.Decisions)
            Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, finalIdentity.RepositoryNamespace,
                Assert.IsType<string>(decision.ProducerKey))?.InFlightExecutionId);
    }

    [Theory]
    [InlineData("{bad-json")]
    [InlineData("null")]
    public async Task MalformedPersistedAuditAssertionsFailClosedDuringPreparation(string malformedAssertions)
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "audit fallback input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("audit-fallback-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "audit-fallback-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "audit-fallback-artifacts");
        var registrations = Registrations(producer);
        string key;
        await using (var seed = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None))
        {
            key = Assert.IsType<string>(Assert.Single(seed.Decisions).ProducerKey);
            Assert.True(await seed.BeginExecutionAsync(CancellationToken.None));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration, TimeProvider.System,
                seed.GetWriter(declaration)), CancellationToken.None);
            Assert.True(await seed.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.Equal(EvidenceExecutionVerdict.Passed, (await seed.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None)).ExecutionVerdict);
        }

        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        var prior = Assert.IsType<EvidenceReceiptState>(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key));
        var malformed = prior with { AuditIntentExecutionId = "orphaned-audit-owner", AuditExpectedAssertions = malformedAssertions };
        EvidenceReceiptQuota.RecordState(store, identity.RepositoryNamespace, key, JsonSerializer.SerializeToUtf8Bytes(malformed).Length, malformed);
        var verify = ReuseOptions(repository.Root, store) with { VerifyReuse = true };

        await using var audit = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), verify, _ => artifacts, CancellationToken.None);
        var decision = Assert.Single(audit.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Unavailable, decision.Status);
        Assert.Equal("reuse.coordination_unavailable", decision.Reason);
        Assert.True(audit.IsUnavailable);
        Assert.False(await audit.BeginExecutionAsync(CancellationToken.None));
        var state = EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key);
        Assert.Equal(malformedAssertions, state?.AuditExpectedAssertions);
        Assert.Equal("orphaned-audit-owner", state?.AuditIntentExecutionId);
        Assert.Null(state?.InFlightExecutionId);
        Assert.Equal(1, producer.ExecutionCount);
        Assert.False(Directory.Exists(artifacts));
    }

    [Fact]
    public async Task InputMutationDuringAdmissionPreventsAnyProducerOrResourceExecution()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "input before admission");
        await repository.CommitAsync();
        var producer = new MutatingMaterialProducer(TestPathUtils.PathUnder(repository.Root, "src", "input.txt"));
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "changed-admission-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "changed-admission-artifacts");

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        Assert.Equal(EvidenceProducerReuseStatus.Miss, Assert.Single(session.Decisions).Status);
        Assert.False(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.Equal(EvidenceProducerReuseStatus.Unavailable, Assert.Single(session.Decisions).Status);
        Assert.Equal("reuse.prepared_inputs_changed", Assert.Single(session.Decisions).Reason);
        Assert.True(session.IsUnavailable);
        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(artifacts));
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace,
            Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey))?.InFlightExecutionId);
    }

    [Fact]
    public async Task ArtifactMutationDuringPublicationSuppressesReceiptAndSettlesAuthority()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "publication failure input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id);
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "publication-failure-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "publication-failure-artifacts");

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "reports", "report.txt"), "changed after cleanup");

        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        var decision = Assert.Single(session.Decisions);
        Assert.Equal("reuse.payload-publication-failed", decision.Reason);
        Assert.Equal(key, decision.ProducerKey);
        Assert.Equal(1, producer.ExecutionCount);
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        var state = EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key);
        Assert.Null(state?.InFlightExecutionId);
        var receipt = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = identity.RepositoryNamespace });
        Assert.False(receipt.Peek(key).IsCandidate);
    }

    [Fact]
    public async Task InputChangedAfterCleanupRevalidationSuppressesFinalPublication()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "input before final revalidation");
        await repository.CommitAsync();
        var producer = new ProbeProducer("final-revalidation-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "final-revalidation-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "final-revalidation-artifacts");
        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);

        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        await repository.WriteAsync("src/input.txt", "input changed after cleanup revalidation");

        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal("reuse.inputs_changed", Assert.Single(session.Decisions).Reason);
        Assert.Equal(key, Assert.Single(session.Decisions).ProducerKey);
        Assert.Equal(1, producer.ExecutionCount);
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
        var receipt = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = identity.RepositoryNamespace });
        Assert.False(receipt.Peek(key).IsCandidate);
    }

    [Fact]
    public async Task RepeatedKeyMaterialChangesExhaustSingleReprepareAndReleaseEveryLease()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "unstable material input");
        await repository.CommitAsync();
        var producer = new ChangingMaterialProducer();
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "unstable-material-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "unstable-material-artifacts");

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);

        var decision = Assert.Single(session.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Unavailable, decision.Status);
        Assert.Equal("reuse.inputs_unstable", decision.Reason);
        Assert.True(session.IsUnavailable);
        Assert.False(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.True(producer.MaterialReadCount >= 3);
        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(artifacts));
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace,
            Assert.IsType<string>(decision.ProducerKey))?.InFlightExecutionId);
    }

    [Fact]
    public async Task FalseCleanupSettlementRetainsAuthorityUntilLaterActualSettlement()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "deferred cleanup input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("deferred-cleanup-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "deferred-cleanup-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "deferred-cleanup-artifacts");
        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.Equal(session.ExecutionId, EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);

        Assert.False(await session.BeforeCleanupAsync(false, CancellationToken.None));

        Assert.Equal(session.ExecutionId, EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
        Assert.Equal(0, producer.ExecutionCount);
        var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal(1, producer.ExecutionCount);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
    }

    [Fact]
    public async Task MissingRequiredResourceIdentityIsIneligibleAndDoesNotAdmitExecution()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "missing resource identity input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("missing-resource-identity-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion") with { RequiredResources = ["database"] };
        var initialPlan = CreatePlan(declaration);
        var profile = initialPlan.Profile with
        { Resources = [new EvidenceResourceDeclaration("database", "completion", 10, [])] };
        var policy = initialPlan.PolicySnapshot! with { Profiles = [profile] };
        var planDraft = initialPlan with
        {
            Profile = profile,
            PolicySnapshot = policy,
            PolicyDigest = EvidenceDigest.CanonicalSha256(policy),
            PlanDigest = string.Empty,
        };
        var plan = planDraft with { PlanDigest = EvidenceDigest.CanonicalSha256(planDraft) };
        var store = TestPathUtils.PathUnder(repository.Root, "missing-resource-identity-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "missing-resource-identity-artifacts");

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);

        var decision = Assert.Single(session.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Ineligible, decision.Status);
        Assert.Equal("reuse.identity.resource_unavailable", decision.Reason);
        Assert.Null(decision.ProducerKey);
        Assert.False(session.IsUnavailable);
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(store));
        Assert.False(Directory.Exists(artifacts));
    }

    [Fact]
    public async Task CancellationDuringCompleteIdentityRecheckSuppressesPublicationAndSettlesAuthority()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "cancel complete identity input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("cancel-complete-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "cancel-complete-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "cancel-complete-artifacts");
        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), cancelled.Token);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal("reuse.publication_suppressed", Assert.Single(session.Decisions).Reason);
        Assert.Equal(1, producer.ExecutionCount);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
        var receipt = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = identity.RepositoryNamespace });
        Assert.False(receipt.Peek(key).IsCandidate);
    }

    [Fact]
    public async Task CancellationAfterPublicationRequestPreparationSuppressesOnlyReceiptPublication()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "publication-stage cancellation input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id);
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "publication-stage-cancel-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "publication-stage-cancel-artifacts");
        using var cancellation = new CancellationTokenSource();
        var cancelAtPublicationRequest = false;
        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ =>
            {
                if (cancelAtPublicationRequest) cancellation.Cancel();
                return artifacts;
            }, CancellationToken.None);
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        cancelAtPublicationRequest = true;

        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), cancellation.Token);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal("reuse.publication_suppressed", Assert.Single(session.Decisions).Reason);
        Assert.Equal(key, Assert.Single(session.Decisions).ProducerKey);
        Assert.Equal(1, producer.ExecutionCount);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
        var receipt = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = identity.RepositoryNamespace });
        Assert.False(receipt.Peek(key).IsCandidate);
    }

    [Fact]
    public async Task SessionJoinRethrowsChildAcknowledgementFaultAfterJoiningAndPreservesUnknownAuthority()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "child acknowledgement failure input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id) with
        {
            Kind = "coverage",
            CoverageGate = new EvidenceCoverageGateRequirements(95, 85),
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
        };
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "child-ack-fault-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "child-ack-fault-artifacts");
        var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? childTask = null;
        var childInvokedProducer = false;
        var expected = new ChildAcknowledgementProbeException();
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);

        try
        {
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
            var childSession = Assert.IsType<EvidenceChildLifetimeSession>(session.GetChildLifetime(producer.Id));
            childSession.BeforeChildAcknowledgementForTests = () =>
            {
                childStarted.TrySetResult();
                releaseChild.Task.GetAwaiter().GetResult();
                throw expected;
            };
            var actualChildTask = EvidenceOwnedChild.RunAsync(_ =>
            {
                childInvokedProducer = true;
                return Task.CompletedTask;
            }, childSession.Environment, CancellationToken.None);
            childTask = actualChildTask;
            await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            releaseChild.TrySetResult();

            var childFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => actualChildTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("enrollment was rejected", childFailure.Message, StringComparison.Ordinal);
            Assert.False(childInvokedProducer);
            var joinedFailure = await Assert.ThrowsAsync<ChildAcknowledgementProbeException>(() => session.JoinActualTasksAsync());
            Assert.Same(expected, joinedFailure);
            Assert.True(childSession.IsUnknown);
            Assert.Equal(session.ExecutionId, EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
            Assert.Equal(0, producer.ExecutionCount);
        }
        finally
        {
            releaseChild.TrySetResult();
            try
            {
                if (childTask is not null)
                {
                    try { await childTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (InvalidOperationException) { }
                }
            }
            finally
            {
                try { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (ChildAcknowledgementProbeException) { }
            }
        }
    }

    [Fact]
    public async Task CancelledUnsettledMaterialCallbackBlocksCleanupUntilActualCallbackJoins()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "late material callback input");
        await repository.CommitAsync();
        var producer = new BlockingMaterialProducer();
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "late-material-callback-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "late-material-callback-artifacts");
        using var cancellation = new CancellationTokenSource();
        EvidenceProducerReuseSession? session = null;
        Task<EvidenceProducerReuseSession>? preparation = null;

        try
        {
            preparation = EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
                new Dictionary<string, string>(), ReuseOptions(repository.Root, store) with
                { ProducerCleanupTimeout = TimeSpan.FromMilliseconds(50) }, _ => artifacts, cancellation.Token);
            await producer.CallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            session = await preparation.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(session.IsCancelled);
            Assert.True(session.IsUnavailable);
            Assert.Equal("reuse.material_cancelled_active", Assert.Single(session.Decisions).Reason);
            Assert.False(await session.BeginExecutionAsync(CancellationToken.None));
            Assert.False(await session.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.Equal(0, producer.ExecutionCount);
            Assert.False(Directory.Exists(store));
            Assert.False(Directory.Exists(artifacts));

            producer.ReleaseCallback.TrySetResult(new Dictionary<string, string>());
            Assert.True(await session.JoinActualTasksAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.Equal(0, producer.ExecutionCount);
        }
        finally
        {
            cancellation.Cancel();
            producer.ReleaseCallback.TrySetResult(new Dictionary<string, string>());
            if (session is null && preparation is not null)
                session = await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            if (session is not null)
            {
                try { await session.JoinActualTasksAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }
    }

    [Fact]
    public async Task LateFatalMaterialFaultPropagatesOnlyAfterOwnedCallbackJoins()
    {
        var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "late fatal material callback input");
        await repository.CommitAsync();
        var producer = new BlockingFatalMaterialProducer();
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "late-fatal-material-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "late-fatal-material-artifacts");
        using var cancellation = new CancellationTokenSource();
        EvidenceProducerReuseSession? session = null;
        Task<EvidenceProducerReuseSession>? preparation = null;
        var expected = new OutOfMemoryException("synthetic late material failure");
        var preparationSettled = false;
        var callbackJoined = false;
        var sessionCleanupSettled = false;

        try
        {
            preparation = EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
                new Dictionary<string, string>(), ReuseOptions(repository.Root, store) with
                { ProducerCleanupTimeout = TimeSpan.FromMilliseconds(50) }, _ => artifacts, cancellation.Token);
            await producer.CallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            session = await preparation.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(session.IsCancelled);
            Assert.True(session.IsUnavailable);
            Assert.Equal("reuse.material_cancelled_active", Assert.Single(session.Decisions).Reason);
            Assert.False(await session.BeginExecutionAsync(CancellationToken.None));
            Assert.False(await session.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.False(producer.CallbackSettled);
            Assert.Equal(0, producer.ExecutionCount);
            Assert.False(Directory.Exists(store));
            Assert.False(Directory.Exists(artifacts));

            producer.ReleaseCallback.TrySetException(expected);
            var joined = await Assert.ThrowsAnyAsync<Exception>(() => session.JoinActualTasksAsync());
            Assert.Same(expected, joined);
            Assert.True(producer.CallbackSettled);
            Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.Equal(0, producer.ExecutionCount);
        }
        finally
        {
            cancellation.Cancel();
            producer.ReleaseCallback.TrySetException(expected);
            if (session is null && preparation is not null)
            {
                try
                {
                    session = await preparation.WaitAsync(TimeSpan.FromSeconds(5));
                    preparationSettled = true;
                }
                catch (TimeoutException)
                {
                    preparationSettled = preparation.IsCompleted;
                }
            }
            else
            {
                preparationSettled = preparation?.IsCompleted == true;
            }
            if (session is not null)
            {
                try
                {
                    await session.JoinActualTasksAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    callbackJoined = producer.CallbackSettled;
                }
                catch (OutOfMemoryException exception) when (ReferenceEquals(exception, expected))
                {
                    callbackJoined = producer.CallbackSettled;
                }
                catch (TimeoutException) { }
                finally
                {
                    try
                    {
                        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                        sessionCleanupSettled = true;
                    }
                    catch (OutOfMemoryException exception) when (ReferenceEquals(exception, expected))
                    {
                        sessionCleanupSettled = true;
                    }
                    catch (TimeoutException) { }
                }
            }
            if (preparationSettled && producer.CallbackSettled && callbackJoined && sessionCleanupSettled)
                repository.Dispose();
        }
    }

    [Fact]
    public async Task SettledKeyMaterialFailureFallsBackToFreshProducerExecutionWithoutReceiptWork()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "settled material failure input");
        await repository.CommitAsync();
        var producer = new FailedMaterialProducer();
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "settled-material-failure-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "settled-material-failure-artifacts");

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);

        var decision = Assert.Single(session.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Ineligible, decision.Status);
        Assert.Equal("reuse.material_failed", decision.Reason);
        Assert.DoesNotContain("private callback error", decision.Diagnostic ?? string.Empty, StringComparison.Ordinal);
        Assert.False(session.IsUnavailable);
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);
        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal(1, producer.ExecutionCount);
        Assert.False(Directory.Exists(store));
    }

    [Fact]
    public async Task AuditPublicationFailureKeepsIntentAndFailsFinalizationWithoutLeakingArtifactData()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "audit publication input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id);
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "audit-publication-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "audit-publication-artifacts");
        var registrations = Registrations(producer);
        var options = ReuseOptions(repository.Root, store);
        string key;
        await using (var seed = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => artifacts, CancellationToken.None))
        {
            key = Assert.IsType<string>(Assert.Single(seed.Decisions).ProducerKey);
            Assert.True(await seed.BeginExecutionAsync(CancellationToken.None));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
                TimeProvider.System, seed.GetWriter(declaration)), CancellationToken.None);
            Assert.True(await seed.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.Equal(EvidenceExecutionVerdict.Passed, (await seed.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None)).ExecutionVerdict);
        }

        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        var auditOptions = options with { VerifyReuse = true };
        await using var audit = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), auditOptions, _ => artifacts, CancellationToken.None);
        Assert.Equal("reuse.audit_requested", Assert.Single(audit.Decisions).Reason);
        Assert.True(await audit.BeginExecutionAsync(CancellationToken.None));
        var auditedResult = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, audit.GetWriter(declaration)), CancellationToken.None);
        Assert.True(await audit.BeforeCleanupAsync(true, CancellationToken.None));
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(artifacts, "reports", "report.txt"), "tampered audit output");

        var completed = await audit.CompleteAsync(EvidenceManifestBuilder.Build(plan, [auditedResult],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.NotEqual(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Contains("reuse.audit_finalization_failed", Assert.Single(completed.ProducerResults).Diagnostic ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("tampered audit output", Assert.Single(completed.ProducerResults).Diagnostic ?? string.Empty, StringComparison.Ordinal);
        var state = EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key);
        Assert.Equal(audit.ExecutionId, state?.AuditIntentExecutionId);
        Assert.Equal("[\"assertion\"]", state?.AuditExpectedAssertions);
        Assert.Null(state?.InFlightExecutionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad id")]
    [InlineData("bad/id")]
    public void ChildLifetimeRejectsInvalidExecutionIdentity(string executionId)
        => Assert.Throws<ArgumentException>(() => EvidenceChildLifetimeSession.Start(executionId));

    [Fact]
    public async Task ClosingAnEmptyChildSessionIsIdempotentAndSettled()
    {
        await using var session = EvidenceChildLifetimeSession.Start("reuse-close-" + Guid.NewGuid().ToString("N"));

        session.CloseEnrollment();
        session.CloseEnrollment();

        Assert.False(session.CanAcceptChildren);
        Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.True(session.IsSettled);
        Assert.False(session.IsUnknown);
    }

    [Fact]
    public async Task CancellingSettlementWaitDoesNotCancelAnEnrolledChild()
    {
        await using var session = EvidenceChildLifetimeSession.Start("wait-cancel-" + Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = EvidenceOwnedChild.RunAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
        }, session.Environment, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            session.CloseEnrollment();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), cancellation.Token));

            Assert.False(child.IsCompleted);
            Assert.False(session.IsSettled);
            release.SetResult();
            await child.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(await session.WaitForSettlementAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.True(session.IsSettled);
        }
        finally
        {
            release.TrySetResult();
            await child.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task SessionChildSettlementTimeoutPreservesExecutionAuthorityUntilActualJoin()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "child settlement timeout input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id) with
        {
            Kind = "coverage",
            CoverageGate = new EvidenceCoverageGateRequirements(95, 85),
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
        };
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "child-settlement-timeout-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "child-settlement-timeout-artifacts");
        var options = ReuseOptions(repository.Root, store) with { ProducerCleanupTimeout = TimeSpan.FromMilliseconds(100) };
        var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), options, _ => artifacts, CancellationToken.None);
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? childTask = null;

        try
        {
            var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);
            Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
            var childSession = Assert.IsType<EvidenceChildLifetimeSession>(session.GetChildLifetime(producer.Id));
            childTask = EvidenceOwnedChild.RunAsync(async cancellationToken =>
            {
                childStarted.TrySetResult();
                await releaseChild.Task.WaitAsync(cancellationToken);
            }, childSession.Environment, CancellationToken.None);
            await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
                TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);

            Assert.False(await session.BeforeCleanupAsync(true, CancellationToken.None));
            var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

            Assert.NotEqual(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
            Assert.False(completed.Metrics.CleanupCompleted);
            Assert.Contains("reuse.child_settlement_unknown", completed.Metrics.CleanupDiagnostic ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(session.ExecutionId, EvidenceReceiptQuota.ReadAuthority(store,
                (await BuildIdentityAsync(repository.Root, plan, declaration)).RepositoryNamespace, key)?.InFlightExecutionId);
            Assert.Equal(1, producer.ExecutionCount);
        }
        finally
        {
            releaseChild.TrySetResult();
            try
            {
                if (childTask is not null) await childTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task UndeclaredInputsRemainFreshOnlyAndCannotAdmitReceiptWork()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "stable input");
        await repository.CommitAsync();
        var producer = new PreparationProbeProducer();
        var declaration = new EvidenceProducerDeclaration(producer.Id, "check", "1", [], ["assertion"], [], 10);
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "receipt-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "artifacts");
        Directory.CreateDirectory(artifacts);
        var options = new EvidenceProducerReuseOptions(true, repository.Root, store, TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100), false, false, ReadOnlyMemory<byte>.Empty, TimeProvider.System)
        { GetEnvironmentVariable = static _ => null };
        var registrations = new Dictionary<string, EvidenceReuseProducerRegistration>(StringComparer.Ordinal)
        {
            [producer.Id] = new(producer, "implementation-v1", new Dictionary<string, string>()),
        };

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => artifacts, CancellationToken.None);

        var decision = Assert.Single(session.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Ineligible, decision.Status);
        Assert.Equal("reuse.inputs_required", decision.Reason);
        Assert.Null(decision.ProducerKey);
        Assert.False(session.IsUnavailable);
        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.False(Directory.Exists(store));
        Assert.Equal(0, producer.ExecutionCount);
    }

    [Fact]
    public async Task ValidIdentityWithoutReceiptProducesFreshMissWithStableKey()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "valid declared input");
        await repository.CommitAsync();
        var producer = new PreparationProbeProducer();
        var declaration = new EvidenceProducerDeclaration(producer.Id, "check", "1", [], ["assertion"], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "receipt-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "artifacts");
        Directory.CreateDirectory(artifacts);
        var options = new EvidenceProducerReuseOptions(true, repository.Root, store, TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100), false, false, ReadOnlyMemory<byte>.Empty, TimeProvider.System)
        { GetEnvironmentVariable = static _ => null };
        var registrations = new Dictionary<string, EvidenceReuseProducerRegistration>(StringComparer.Ordinal)
        {
            [producer.Id] = new(producer, "implementation-v1", new Dictionary<string, string> { ["tool"] = "tool-v1" }),
        };

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => artifacts, CancellationToken.None);

        var decision = Assert.Single(session.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Miss, decision.Status);
        Assert.StartsWith("reuse.", decision.Reason, StringComparison.Ordinal);
        var key = Assert.IsType<string>(decision.ProducerKey);
        Assert.Equal(64, key.Length);
        Assert.All(key, character => Assert.True(char.IsAsciiHexDigit(character)));
        Assert.Empty(session.ReusedResults);
        Assert.False(session.IsUnavailable);
        Assert.Equal(0, producer.ExecutionCount);
    }

    [Theory]
    [InlineData("origin.plan.json")]
    [InlineData("artifacts/reports/report.txt")]
    public async Task NonprivateReceiptPayloadBlocksSessionAdmissionWithoutExecutingOrInvalidatingOrigin(string relativePath)
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "private receipt input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id);
        var plan = CreatePlan(declaration);
        var registrations = Registrations(producer);
        var store = TestPathUtils.PathUnder(repository.Root, "private-payload-store");
        var originalArtifacts = TestPathUtils.PathUnder(repository.Root, "original-artifacts");
        var restoredArtifacts = TestPathUtils.PathUnder(repository.Root, "refused-artifacts");
        var options = ReuseOptions(repository.Root, store);
        string key;
        await using (var seed = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => originalArtifacts, CancellationToken.None))
        {
            key = Assert.IsType<string>(Assert.Single(seed.Decisions).ProducerKey);
            Assert.True(await seed.BeginExecutionAsync(CancellationToken.None));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
                TimeProvider.System, seed.GetWriter(declaration)), CancellationToken.None);
            Assert.True(await seed.BeforeCleanupAsync(true, CancellationToken.None));
            var completed = await seed.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);
            Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        }
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        var authority = Assert.IsType<EvidenceReceiptState>(EvidenceReceiptQuota.ReadAuthority(
            store, identity.RepositoryNamespace, key));
        var payload = TestPathUtils.PathUnder(store, identity.RepositoryNamespace, key,
            Assert.IsType<string>(authority.PayloadDirectoryName));
        var file = TestPathUtils.PathUnder(payload, relativePath);
        var bytes = await File.ReadAllBytesAsync(file);
        var mode = File.GetUnixFileMode(file);
        try
        {
            File.SetUnixFileMode(file, mode | UnixFileMode.GroupRead);
            await using var refused = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
                new Dictionary<string, string>(), options, _ => restoredArtifacts, CancellationToken.None);

            var decision = Assert.Single(refused.Decisions);
            Assert.True(refused.IsUnavailable);
            Assert.Equal(EvidenceProducerReuseStatus.Unavailable, decision.Status);
            Assert.Equal("reuse.receipt-payload-io-unavailable", decision.Reason);
            Assert.Empty(refused.ReusedResults);
            Assert.False(await refused.BeginExecutionAsync(CancellationToken.None));
            Assert.Equal(1, producer.ExecutionCount);
            Assert.False(Directory.Exists(restoredArtifacts));
            Assert.Equal(authority, EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key));
            Assert.True(Directory.Exists(payload));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        }
        finally { File.SetUnixFileMode(file, mode); }
    }

    [Fact]
    public async Task FreshPublicationRestoresAValidatedArtifactOnTheNextPreparation()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "receipt input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = new EvidenceProducerDeclaration(producer.Id, "check", "1", [], ["assertion"],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 1024)], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "receipt-store");
        var firstArtifacts = TestPathUtils.PathUnder(repository.Root, "first-artifacts");
        var restoredArtifacts = TestPathUtils.PathUnder(repository.Root, "restored-artifacts");
        Directory.CreateDirectory(firstArtifacts);
        Directory.CreateDirectory(restoredArtifacts);
        var registration = new EvidenceReuseProducerRegistration(producer, "implementation-v1", new Dictionary<string, string>());
        var registrations = new Dictionary<string, EvidenceReuseProducerRegistration>(StringComparer.Ordinal) { [producer.Id] = registration };
        var options = new EvidenceProducerReuseOptions(true, repository.Root, store, TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100), false, false, ReadOnlyMemory<byte>.Empty, TimeProvider.System)
        { GetEnvironmentVariable = static _ => null };

        await using (var fresh = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => firstArtifacts, CancellationToken.None))
        {
            Assert.Equal(EvidenceProducerReuseStatus.Miss, Assert.Single(fresh.Decisions).Status);
            Assert.True(await fresh.BeginExecutionAsync(CancellationToken.None));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
                TimeProvider.System, fresh.GetWriter(declaration)), CancellationToken.None);
            Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
            Assert.True(await fresh.BeforeCleanupAsync(true, CancellationToken.None));
            var completed = await fresh.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);
            Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        }

        await using (var restored = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => restoredArtifacts, CancellationToken.None))
        {
            Assert.Equal(EvidenceProducerReuseStatus.Hit, Assert.Single(restored.Decisions).Status);
            var reused = Assert.Single(restored.ReusedResults);
            Assert.Equal(EvidenceProducerOutcome.Reused, reused.Value.Outcome);
            Assert.Equal("receipt content", await File.ReadAllTextAsync(TestPathUtils.PathUnder(restoredArtifacts, "reports", "report.txt")));
        }
        Assert.Equal(1, producer.ExecutionCount);

        var auditArtifacts = TestPathUtils.PathUnder(repository.Root, "audit-artifacts");
        Directory.CreateDirectory(auditArtifacts);
        await using (var audit = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options with { VerifyReuse = true }, _ => auditArtifacts, CancellationToken.None))
        {
            Assert.Empty(audit.ReusedResults);
            Assert.True(await audit.BeginExecutionAsync(CancellationToken.None));
            var freshResult = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
                TimeProvider.System, audit.GetWriter(declaration)), CancellationToken.None);
            Assert.True(await audit.BeforeCleanupAsync(true, CancellationToken.None));
            var altered = freshResult with { SatisfiedAssertionIds = [] };
            var mismatch = await audit.CompleteAsync(EvidenceManifestBuilder.Build(plan, [altered],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);
            Assert.NotEqual(EvidenceExecutionVerdict.Passed, mismatch.ExecutionVerdict);
            Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single(mismatch.ProducerResults).Outcome);
        }

        await using var quarantined = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => firstArtifacts, CancellationToken.None);
        Assert.Empty(quarantined.ReusedResults);
        Assert.Contains("reuse.audit_required", Assert.Single(quarantined.Decisions).Reason, StringComparison.Ordinal);
        Assert.Equal(2, producer.ExecutionCount);
    }

    [Fact]
    public async Task CorruptPublishedArtifactIsRejectedBeforeRestore()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "receipt input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = new EvidenceProducerDeclaration(producer.Id, "check", "1", [], ["assertion"],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 1024)], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "receipt-store");
        var firstArtifacts = TestPathUtils.PathUnder(repository.Root, "first-artifacts");
        var rejectedRestore = TestPathUtils.PathUnder(repository.Root, "rejected-restore");
        Directory.CreateDirectory(firstArtifacts);
        Directory.CreateDirectory(rejectedRestore);
        var registration = new EvidenceReuseProducerRegistration(producer, "implementation-v1", new Dictionary<string, string>());
        var registrations = new Dictionary<string, EvidenceReuseProducerRegistration>(StringComparer.Ordinal) { [producer.Id] = registration };
        var options = new EvidenceProducerReuseOptions(true, repository.Root, store, TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100), false, false, ReadOnlyMemory<byte>.Empty, TimeProvider.System)
        { GetEnvironmentVariable = static _ => null };
        string key;

        await using (var fresh = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => firstArtifacts, CancellationToken.None))
        {
            key = Assert.Single(fresh.Decisions).ProducerKey!;
            Assert.True(await fresh.BeginExecutionAsync(CancellationToken.None));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
                TimeProvider.System, fresh.GetWriter(declaration)), CancellationToken.None);
            Assert.True(await fresh.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.Equal(EvidenceExecutionVerdict.Passed, (await fresh.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None)).ExecutionVerdict);
        }

        var identity = await EvidenceProducerIdentityBuilder.BuildAsync(new EvidenceProducerIdentityRequest(repository.Root,
            plan, declaration, "implementation-v1", new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string>(), null), CancellationToken.None);
        Assert.True(identity.IsEligible, identity.Diagnostic);
        Assert.Equal(key, identity.Identity!.Key);
        var state = EvidenceReceiptQuota.ReadAuthority(store, identity.Identity.RepositoryNamespace, key);
        var payloadName = Assert.IsType<string>(state?.PayloadDirectoryName);
        var payload = TestPathUtils.PathUnder(store, identity.Identity.RepositoryNamespace.ToLowerInvariant(), key.ToLowerInvariant(),
            payloadName, "artifacts", "reports", "report.txt");
        await File.WriteAllTextAsync(payload, "tampered receipt bytes");

        await using var rejected = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => rejectedRestore, CancellationToken.None);

        Assert.Empty(rejected.ReusedResults);
        Assert.Equal(EvidenceProducerReuseStatus.Miss, Assert.Single(rejected.Decisions).Status);
        Assert.False(File.Exists(TestPathUtils.PathUnder(rejectedRestore, "reports", "report.txt")));
        Assert.Equal(1, producer.ExecutionCount);
    }

    [Fact]
    public async Task KeyMaterialFailureAndNullResultsSettleAsSafeIneligibility()
    {
        var producer = new EvidenceProducerDeclaration("key-probe", "check", "1", [], [], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var plan = new EvidencePlan("1.1", "policy", new string('a', 64), new string('b', 64),
            new EvidenceProfile("profile", EvidenceProfileScope.Targeted, [], [producer], []), [], [], new string('c', 64));
        var context = new EvidenceProducerKeyMaterialContext(plan, producer, TimeProvider.System);
        const string privateFailure = "private-key-callback-failure";

        await using var failedExecution = EvidenceProducerKeyMaterialExecution.Start(
            new KeyMaterialProbe(_ => ValueTask.FromException<IReadOnlyDictionary<string, string>>(new InvalidOperationException(privateFailure))),
            context, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var failed = await failedExecution.WaitAsync();
        Assert.True(failed.Settled);
        Assert.Equal("reuse.material_failed", failed.Reason);
        Assert.Null(failed.Material);
        Assert.DoesNotContain(privateFailure, failed.Diagnostic ?? string.Empty, StringComparison.Ordinal);

        await using var nullExecution = EvidenceProducerKeyMaterialExecution.Start(
            new KeyMaterialProbe(_ => ValueTask.FromResult<IReadOnlyDictionary<string, string>>(null!)),
            context, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var missing = await nullExecution.WaitAsync();
        Assert.True(missing.Settled);
        Assert.Equal("reuse.material_invalid", missing.Reason);
        Assert.Null(missing.Material);
        Assert.Contains("non-null map", missing.Diagnostic ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyPreparationMissDoesNotCreateReceiptStoreOrArtifacts()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "readonly input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id);
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "readonly-missing-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "readonly-missing-artifacts");
        var options = ReuseOptions(repository.Root, store, readOnly: true);
        var registrations = Registrations(producer);

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => artifacts, CancellationToken.None);

        var decision = Assert.Single(session.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Miss, decision.Status);
        Assert.Empty(session.ReusedResults);
        Assert.False(session.IsUnavailable);
        Assert.False(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.False(Directory.Exists(store));
        Assert.False(Directory.Exists(artifacts));
        Assert.Equal(0, producer.ExecutionCount);
    }

    [Fact]
    public async Task ReadOnlyValidReceiptIsOnlyACandidateAndLeavesStoreBytesUnchanged()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "readonly hit input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id);
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "readonly-hit-store");
        var published = TestPathUtils.PathUnder(repository.Root, "readonly-published");
        var restore = TestPathUtils.PathUnder(repository.Root, "readonly-hit-restore");
        var registrations = Registrations(producer);
        var writable = ReuseOptions(repository.Root, store);
        await using (var seed = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), writable, _ => published, CancellationToken.None))
        {
            Assert.Equal(EvidenceProducerReuseStatus.Miss, Assert.Single(seed.Decisions).Status);
            Assert.True(await seed.BeginExecutionAsync(CancellationToken.None));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
                TimeProvider.System, seed.GetWriter(declaration)), CancellationToken.None);
            Assert.True(await seed.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.Equal(EvidenceExecutionVerdict.Passed, (await seed.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None)).ExecutionVerdict);
        }

        var before = SnapshotStore(store);
        await using var readOnly = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store, readOnly: true), _ => restore, CancellationToken.None);

        Assert.Equal(EvidenceProducerReuseStatus.Hit, Assert.Single(readOnly.Decisions).Status);
        Assert.Empty(readOnly.ReusedResults);
        Assert.False(readOnly.IsUnavailable);
        Assert.Throws<InvalidOperationException>(() => readOnly.GetWriter(declaration));
        Assert.False(await readOnly.BeginExecutionAsync(CancellationToken.None));
        Assert.False(Directory.Exists(restore));
        Assert.Equal(1, producer.ExecutionCount);
        Assert.Equal(before, SnapshotStore(store));
    }

    [Fact]
    public async Task ReadOnlyPreparationPreservesInFlightAuthorityAsUnavailable()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "in-flight input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id);
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "readonly-inflight-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "readonly-inflight-artifacts");
        var registrations = Registrations(producer);
        var writer = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        await using (writer)
        {
            Assert.True(await writer.BeginExecutionAsync(CancellationToken.None));
            var key = Assert.IsType<string>(Assert.Single(writer.Decisions).ProducerKey);
            var executionId = writer.ExecutionId;
            var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
            var before = EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key);
            Assert.Equal(executionId, before?.InFlightExecutionId);

            await using var readOnly = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
                new Dictionary<string, string>(), ReuseOptions(repository.Root, store, readOnly: true), _ => artifacts, CancellationToken.None);

            var decision = Assert.Single(readOnly.Decisions);
            Assert.Equal(EvidenceProducerReuseStatus.Unavailable, decision.Status);
            Assert.Empty(readOnly.ReusedResults);
            Assert.False(readOnly.IsUnavailable);
            Assert.False(await readOnly.BeginExecutionAsync(CancellationToken.None));
            Assert.Equal(0, producer.ExecutionCount);
            Assert.Equal(executionId, EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
            Assert.False(Directory.Exists(artifacts));
        }
    }

    [Fact]
    public async Task FailedSecondArtifactRestoreRemovesOnlyFilesOwnedByThisAttempt()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "partial restore input");
        await repository.CommitAsync();
        var producer = new MultiArtifactProbeProducer();
        var declaration = new EvidenceProducerDeclaration(producer.Id, "check", "1", [], ["assertion"],
            [new EvidenceArtifactSlot("a-first", "reports/a", "text/plain", true, 1024),
             new EvidenceArtifactSlot("z-second", "reports/z", "text/plain", true, 1024)], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "partial-restore-store");
        var published = TestPathUtils.PathUnder(repository.Root, "partial-restore-published");
        var restore = TestPathUtils.PathUnder(repository.Root, "partial-restore-output");
        var foreign = TestPathUtils.PathUnder(restore, "reports", "z", "second.txt");
        var outside = TestPathUtils.PathUnder(repository.Root, "outside-output-root");
        var sentinel = TestPathUtils.PathUnder(outside, "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(foreign, "foreign owner bytes");
        await File.WriteAllTextAsync(sentinel, "outside sentinel");
        var registrations = Registrations(producer);
        var options = ReuseOptions(repository.Root, store);
        string key;
        await using (var seed = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => published, CancellationToken.None))
        {
            key = Assert.IsType<string>(Assert.Single(seed.Decisions).ProducerKey);
            Assert.True(await seed.BeginExecutionAsync(CancellationToken.None));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
                TimeProvider.System, seed.GetWriter(declaration)), CancellationToken.None);
            Assert.True(await seed.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.Equal(EvidenceExecutionVerdict.Passed, (await seed.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None)).ExecutionVerdict);
        }

        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        var authorityBefore = EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key);
        var firstReceipt = Assert.IsType<string>(authorityBefore?.PayloadDirectoryName);
        var receiptStore = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = identity.RepositoryNamespace });
        Assert.True(receiptStore.Peek(key).IsCandidate);
        await using var failedRestore = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), options, _ => restore, CancellationToken.None);

        var decision = Assert.Single(failedRestore.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Miss, decision.Status);
        Assert.Equal(key, decision.ProducerKey);
        Assert.Equal("reuse.no-comparable-receipt", decision.Reason);
        Assert.Empty(failedRestore.ReusedResults);
        Assert.False(File.Exists(TestPathUtils.PathUnder(restore, "reports", "a", "first.txt")));
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(restore, "reports", "a")));
        Assert.Equal("foreign owner bytes", await File.ReadAllTextAsync(foreign));
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(restore, "reports")));
        Assert.True(Directory.Exists(TestPathUtils.PathUnder(restore, "reports", "z")));
        Assert.Equal("outside sentinel", await File.ReadAllTextAsync(sentinel));
        Assert.Empty(failedRestore.GetWriter(declaration).WrittenArtifacts);
        var authorityAfter = EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key);
        Assert.Equal(firstReceipt, authorityAfter?.PayloadDirectoryName);
        Assert.Equal(authorityBefore?.OriginManifestDigest, authorityAfter?.OriginManifestDigest);
        Assert.Null(authorityAfter?.InFlightExecutionId);
        Assert.True(receiptStore.Peek(key).IsCandidate);
        Assert.Equal(1, producer.ExecutionCount);
    }

    [Fact]
    public async Task ChildChannelStartFailureIsUnavailableAndDoesNotExposeExceptionMessage()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "channel failure input");
        await repository.CommitAsync();
        const string secretMarker = "private-channel-start-marker";
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id) with
        {
            Kind = "coverage",
            CoverageGate = new EvidenceCoverageGateRequirements(95, 85),
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
        };
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "channel-failure-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "channel-failure-artifacts");
        var options = ReuseOptions(repository.Root, store) with
        { BeforeChildChannelStartForTests = _ => throw new UnusualProbeException(secretMarker) };

        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), options, _ => artifacts, CancellationToken.None);
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);

        Assert.False(await session.BeginExecutionAsync(CancellationToken.None));
        Assert.True(session.IsUnavailable);
        Assert.Equal(EvidenceProducerReuseStatus.Unavailable, Assert.Single(session.Decisions).Status);
        Assert.Contains("reuse.authority_unavailable", Assert.Single(session.Decisions).Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(secretMarker, Assert.Single(session.Decisions).Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, producer.ExecutionCount);
        Assert.False(Directory.Exists(artifacts));
        Assert.Null(session.GetChildLifetime(producer.Id));
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
    }

    private static EvidenceProducerDeclaration ArtifactDeclaration(string producerId)
        => new(producerId, "check", "1", [], ["assertion"],
            [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 1024)], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };

    private static Dictionary<string, EvidenceReuseProducerRegistration> Registrations(params IEvidenceProducer[] producers)
        => producers.ToDictionary(producer => producer.Id,
            producer => new EvidenceReuseProducerRegistration(producer, "implementation-v1",
                new Dictionary<string, string> { ["tool"] = "tool-v1" }), StringComparer.Ordinal);

    private static EvidenceProducerDeclaration ProbeDeclaration(string producerId, string assertionId)
        => new(producerId, "check", "1", [], [assertionId], [], 10)
        { Inputs = new EvidenceProducerInputs(["src/**"], []) };

    private static EvidenceProducerReuseOptions ReuseOptions(string repositoryRoot, string store, bool readOnly = false)
        => new(true, repositoryRoot, store, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100), false, readOnly,
            ReadOnlyMemory<byte>.Empty, TimeProvider.System)
        { GetEnvironmentVariable = static _ => null };

    private static SortedDictionary<string, string> SnapshotStore(string store)
        => new(Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(store, path), path => Convert.ToBase64String(File.ReadAllBytes(path)), StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static async ValueTask<EvidenceProducerIdentity> BuildIdentityAsync(string repositoryRoot, EvidencePlan plan,
        EvidenceProducerDeclaration declaration, string? capturedPatchSha256 = null)
    {
        var identity = await EvidenceProducerIdentityBuilder.BuildAsync(new EvidenceProducerIdentityRequest(repositoryRoot,
            plan, declaration, "implementation-v1", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["tool"] = "tool-v1" }, new Dictionary<string, string>(), capturedPatchSha256), CancellationToken.None);
        Assert.True(identity.IsEligible, identity.Diagnostic);
        return identity.Identity!;
    }

    private sealed class UnusualProbeException(string message) : Exception(message);

    private sealed class ThrowingRegistrationLookup(
        IReadOnlyDictionary<string, EvidenceReuseProducerRegistration> registrations, Exception exception)
        : IReadOnlyDictionary<string, EvidenceReuseProducerRegistration>
    {
        public EvidenceReuseProducerRegistration this[string key] => registrations[key];
        public IEnumerable<string> Keys => registrations.Keys;
        public IEnumerable<EvidenceReuseProducerRegistration> Values => registrations.Values;
        public int Count => registrations.Count;
        public bool ContainsKey(string key) => throw exception;
        public bool TryGetValue(string key, out EvidenceReuseProducerRegistration value)
            => registrations.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, EvidenceReuseProducerRegistration>> GetEnumerator()
            => registrations.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ChildAcknowledgementProbeException : Exception { }

    private sealed class ChangingMaterialProducer : IEvidenceProducer, IEvidenceProducerKeyMaterial
    {
        private int _materialReadCount;
        internal int MaterialReadCount => Volatile.Read(ref _materialReadCount);
        internal int ExecutionCount { get; private set; }
        public string Id => "changing-material-probe";

        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = Interlocked.Increment(ref _materialReadCount);
            IReadOnlyDictionary<string, string> material = new Dictionary<string, string> { ["revision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            return ValueTask.FromResult(material);
        }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["assertion"]));
        }
    }

    private sealed class BlockingMaterialProducer : IEvidenceProducer, IEvidenceProducerKeyMaterial
    {
        internal TaskCompletionSource CallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyDictionary<string, string>> ReleaseCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ExecutionCount { get; private set; }
        public string Id => "blocking-material-probe";

        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            CallbackStarted.TrySetResult();
            return new ValueTask<IReadOnlyDictionary<string, string>>(ReleaseCallback.Task);
        }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["assertion"]));
        }
    }

    private sealed class BlockingFatalMaterialProducer : IEvidenceProducer, IEvidenceProducerKeyMaterial
    {
        internal TaskCompletionSource CallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyDictionary<string, string>> ReleaseCallback { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CallbackSettled { get; private set; }
        internal int ExecutionCount { get; private set; }
        public string Id => "blocking-fatal-material-probe";

        public async ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            CallbackStarted.TrySetResult();
            try { return await ReleaseCallback.Task.ConfigureAwait(false); }
            finally { CallbackSettled = true; }
        }

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["assertion"]));
        }
    }

    private sealed class FailedMaterialProducer : IEvidenceProducer, IEvidenceProducerKeyMaterial
    {
        internal int ExecutionCount { get; private set; }
        public string Id => "failed-material-fresh-fallback";

        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
            => ValueTask.FromException<IReadOnlyDictionary<string, string>>(
                new InvalidOperationException("private callback error"));

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["assertion"]));
        }
    }

    private sealed class MultiArtifactProbeProducer : IEvidenceProducer
    {
        internal int ExecutionCount { get; private set; }
        public string Id => "multi-artifact-restore-probe";
        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            var first = await context.Artifacts!.WriteAsync("a-first", "reports/a/first.txt", Encoding.UTF8.GetBytes("first"), cancellationToken);
            var second = await context.Artifacts.WriteAsync("z-second", "reports/z/second.txt", Encoding.UTF8.GetBytes("second"), cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: [first, second]);
        }
    }

    private sealed class PreparationProbeProducer : IEvidenceProducer
    {
        internal int ExecutionCount { get; private set; }
        public string Id => "fresh-only-probe";
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["assertion"]));
        }
    }

    [Fact]
    public async Task CancellationBeforeCleanupRevalidationSuppressesReceiptButPreservesFreshPass()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "cancel before cleanup input");
        await repository.CommitAsync();
        var producer = new ProbeProducer("cancel-before-cleanup-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "cancel-before-cleanup-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "cancel-before-cleanup-artifacts");
        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);

        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.True(await session.BeforeCleanupAsync(true, cancellation.Token));
        var decision = Assert.Single(session.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Ineligible, decision.Status);
        Assert.Equal("reuse.inputs_changed", decision.Reason);
        Assert.Contains("publication is suppressed", decision.Diagnostic, StringComparison.Ordinal);
        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal(1, producer.ExecutionCount);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
        var receipt = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = identity.RepositoryNamespace });
        Assert.False(receipt.Peek(key).IsCandidate);
    }

    [Fact]
    public async Task InputMutationBeforeCleanupRevalidationNamesInputAndSuppressesReceipt()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "input before cleanup mutation");
        await repository.CommitAsync();
        var producer = new ProbeProducer("before-cleanup-mutation-probe", "assertion");
        var declaration = ProbeDeclaration(producer.Id, "assertion");
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "before-cleanup-mutation-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "before-cleanup-mutation-artifacts");
        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);

        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);
        await repository.WriteAsync("src/input.txt", "input changed before cleanup revalidation");

        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        var decision = Assert.Single(session.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Ineligible, decision.Status);
        Assert.Equal("reuse.inputs_changed", decision.Reason);
        Assert.Contains("src/input.txt", decision.Diagnostic, StringComparison.Ordinal);
        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal(1, producer.ExecutionCount);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
        var receipt = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = identity.RepositoryNamespace });
        Assert.False(receipt.Peek(key).IsCandidate);
    }

    [Fact]
    public async Task BeginExecutionCanAdmitOnlyOnceAndFirstRunStillPublishes()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "single admission input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id);
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "single-admission-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "single-admission-artifacts");
        await using var session = await EvidenceProducerReuseSession.PrepareAsync(plan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None);
        var key = Assert.IsType<string>(Assert.Single(session.Decisions).ProducerKey);
        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);

        Assert.True(await session.BeginExecutionAsync(CancellationToken.None));
        var admittedState = EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key);
        Assert.Equal(session.ExecutionId, admittedState?.InFlightExecutionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.BeginExecutionAsync(CancellationToken.None));
        Assert.Equal(session.ExecutionId, EvidenceReceiptQuota.ReadAuthority(store,
            identity.RepositoryNamespace, key)?.InFlightExecutionId);

        var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, session.GetWriter(declaration)), CancellationToken.None);
        Assert.True(await session.BeforeCleanupAsync(true, CancellationToken.None));
        var completed = await session.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal(1, producer.ExecutionCount);
        Assert.Null(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key)?.InFlightExecutionId);
        Assert.True(EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = identity.RepositoryNamespace }).Peek(key).IsCandidate);
    }

    [Fact]
    public async Task ChangedPlannedPatchProducesFreshDiffChangedMissAndPreservesOldReceipt()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "stable patch identity input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id) with
        {
            CoverageGate = new EvidenceCoverageGateRequirements(95, 85, 95, 85),
        };
        var initialPlan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "patch-comparison-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "patch-comparison-artifacts");
        var firstPatch = Encoding.UTF8.GetBytes("diff --git a/src/input.txt b/src/input.txt\n--- a/src/input.txt\n+++ b/src/input.txt\n@@ -1 +1 @@\n-old\n+first\n");
        var changedPatch = Encoding.UTF8.GetBytes("diff --git a/src/input.txt b/src/input.txt\n--- a/src/input.txt\n+++ b/src/input.txt\n@@ -1 +1 @@\n-old\n+second\n");
        var firstPlan = WithCapturedPatch(initialPlan, firstPatch);
        string firstKey;
        var firstIdentity = await BuildIdentityAsync(repository.Root, firstPlan, declaration, firstPlan.PatchSha256);

        await using (var fresh = await EvidenceProducerReuseSession.PrepareAsync(firstPlan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store) with
            { CapturedPatch = firstPatch }, _ => artifacts, CancellationToken.None))
        {
            firstKey = Assert.IsType<string>(Assert.Single(fresh.Decisions).ProducerKey);
            Assert.True(await fresh.BeginExecutionAsync(CancellationToken.None));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(firstPlan, declaration,
                TimeProvider.System, fresh.GetWriter(declaration)), CancellationToken.None);
            Assert.True(await fresh.BeforeCleanupAsync(true, CancellationToken.None));
            var completed = await fresh.CompleteAsync(EvidenceManifestBuilder.Build(firstPlan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);
            Assert.Equal(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        }

        var changedPlan = WithCapturedPatch(initialPlan, changedPatch);
        await using var changed = await EvidenceProducerReuseSession.PrepareAsync(changedPlan, Registrations(producer),
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store) with
            { CapturedPatch = changedPatch }, _ => TestPathUtils.PathUnder(repository.Root, "changed-patch-artifacts"), CancellationToken.None);

        var decision = Assert.Single(changed.Decisions);
        Assert.Equal(EvidenceProducerReuseStatus.Miss, decision.Status);
        Assert.Equal("reuse.diff_changed", decision.Reason);
        Assert.NotEqual(firstKey, decision.ProducerKey);
        Assert.Empty(changed.ReusedResults);
        Assert.Equal(1, producer.ExecutionCount);
        var receiptStore = EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = firstIdentity.RepositoryNamespace });
        Assert.True(receiptStore.Peek(firstKey).IsCandidate);
        Assert.False(receiptStore.Peek(Assert.IsType<string>(decision.ProducerKey)).IsCandidate);
    }

    [Fact]
    public async Task AuditMismatchPreservesForeignDurableIntentWhenQuarantineWriteIsRejected()
    {
        using var repository = await EvidenceIdentityCoverageTests.TestRepository.CreateAsync();
        await repository.WriteAsync("src/input.txt", "audit intent custody input");
        await repository.CommitAsync();
        var producer = new PublishingProbeProducer();
        var declaration = ArtifactDeclaration(producer.Id);
        var plan = CreatePlan(declaration);
        var store = TestPathUtils.PathUnder(repository.Root, "audit-intent-custody-store");
        var artifacts = TestPathUtils.PathUnder(repository.Root, "audit-intent-custody-artifacts");
        var registrations = Registrations(producer);
        string key;
        await using (var seed = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store), _ => artifacts, CancellationToken.None))
        {
            key = Assert.IsType<string>(Assert.Single(seed.Decisions).ProducerKey);
            Assert.True(await seed.BeginExecutionAsync(CancellationToken.None));
            var result = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
                TimeProvider.System, seed.GetWriter(declaration)), CancellationToken.None);
            Assert.True(await seed.BeforeCleanupAsync(true, CancellationToken.None));
            Assert.Equal(EvidenceExecutionVerdict.Passed, (await seed.CompleteAsync(EvidenceManifestBuilder.Build(plan, [result],
                metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None)).ExecutionVerdict);
        }

        var identity = await BuildIdentityAsync(repository.Root, plan, declaration);
        await using var audit = await EvidenceProducerReuseSession.PrepareAsync(plan, registrations,
            new Dictionary<string, string>(), ReuseOptions(repository.Root, store) with { VerifyReuse = true },
            _ => artifacts, CancellationToken.None);
        Assert.Equal("reuse.audit_requested", Assert.Single(audit.Decisions).Reason);
        Assert.True(await audit.BeginExecutionAsync(CancellationToken.None));
        var auditResult = await producer.ProduceAsync(new EvidenceProducerContext(plan, declaration,
            TimeProvider.System, audit.GetWriter(declaration)), CancellationToken.None);
        Assert.True(await audit.BeforeCleanupAsync(true, CancellationToken.None));

        var admittedState = Assert.IsType<EvidenceReceiptState>(EvidenceReceiptQuota.ReadAuthority(store,
            identity.RepositoryNamespace, key));
        var foreignIntent = admittedState with { AuditIntentExecutionId = "foreign-audit-owner" };
        var foreignBytes = JsonSerializer.SerializeToUtf8Bytes(foreignIntent);
        EvidenceReceiptQuota.RecordState(store, identity.RepositoryNamespace, key, foreignBytes.Length, foreignIntent);

        var completed = await audit.CompleteAsync(EvidenceManifestBuilder.Build(plan,
            [auditResult with { SatisfiedAssertionIds = ["different-assertion"] }],
            metrics: new EvidenceExecutionMetrics { CleanupCompleted = true }), CancellationToken.None);

        Assert.NotEqual(EvidenceExecutionVerdict.Passed, completed.ExecutionVerdict);
        Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single(completed.ProducerResults).Outcome);
        Assert.Contains("reuse.audit_mismatch", Assert.Single(completed.ProducerResults).Diagnostic ?? string.Empty, StringComparison.Ordinal);
        var after = Assert.IsType<EvidenceReceiptState>(EvidenceReceiptQuota.ReadAuthority(store, identity.RepositoryNamespace, key));
        Assert.Equal("foreign-audit-owner", after.AuditIntentExecutionId);
        Assert.Null(after.InFlightExecutionId);
        Assert.False(after.AuditMismatch);
        Assert.False(EvidenceReceiptStore.OpenReadOnly(new EvidenceReceiptStoreOptions
        { RootDirectory = store, RepositoryNamespace = identity.RepositoryNamespace }).Peek(key).IsCandidate);
    }

    private static EvidencePlan WithCapturedPatch(EvidencePlan plan, byte[] patch)
    {
        var draft = plan with { PatchSha256 = EvidenceDigest.Sha256(patch), PlanDigest = string.Empty };
        return draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
    }

    private static EvidencePlan CreatePlan(params EvidenceProducerDeclaration[] declarations)
    {
        var paths = Array.Empty<NormalizedDiffPath>();
        var obligations = declarations.SelectMany(declaration => declaration.AssertionIds.Select(assertion =>
            new EvidenceObligation("obligation-" + declaration.Id + "-" + assertion, "risk", "test evidence is required",
                [declaration.Id], assertion))).ToArray();
        var profile = new EvidenceProfile("profile", EvidenceProfileScope.Targeted, [], declarations, obligations);
        var policy = new EvidencePolicy("policy", "1", profile.Id, [profile], []);
        var draft = new EvidencePlan("1.1", policy.Id, EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(paths), profile, paths, [], string.Empty, policy);
        return draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
    }

    private sealed class PublishingProbeProducer : IEvidenceProducer
    {
        internal int ExecutionCount { get; private set; }
        public string Id => "artifact-reuse-probe";
        public async ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            var artifact = await context.Artifacts!.WriteAsync("report", "reports/report.txt",
                Encoding.UTF8.GetBytes("receipt content"), cancellationToken);
            return new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["assertion"], Artifacts: [artifact]);
        }
    }

    private sealed class ProbeProducer(string id, string assertionId) : IEvidenceProducer
    {
        internal int ExecutionCount { get; private set; }
        public string Id => id;
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, [assertionId]));
        }
    }

    private sealed class MutatingMaterialProducer(string inputPath) : IEvidenceProducer, IEvidenceProducerKeyMaterial
    {
        private int _materialCalls;
        internal int ExecutionCount { get; private set; }
        public string Id => "admission-input-mutation-probe";
        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _materialCalls) >= 3)
                File.WriteAllText(inputPath, "input changed during admission");
            IReadOnlyDictionary<string, string> material = new Dictionary<string, string> { ["mode"] = "stable" };
            return ValueTask.FromResult(material);
        }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return ValueTask.FromResult(new EvidenceProducerResult(Id, EvidenceProducerOutcome.Passed, ["assertion"]));
        }
    }

    private sealed class KeyMaterialProbe(
        Func<EvidenceProducerKeyMaterialContext, ValueTask<IReadOnlyDictionary<string, string>>> callback)
        : IEvidenceProducerKeyMaterial
    {
        public ValueTask<IReadOnlyDictionary<string, string>> GetKeyMaterialAsync(
            EvidenceProducerKeyMaterialContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return callback(context);
        }
    }
}
