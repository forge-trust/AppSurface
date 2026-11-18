using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class EvidencePreparedRegistrationCoverageTests
{
    [Fact]
    public async Task DuplicateProducerRegistration_ShouldFailAndDisposeOnlyTheAcceptedOwner()
    {
        using var fixture = new Fixture();
        var first = new Producer("check");
        var duplicate = new Producer("check");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(first, "fixture-v1");
            registration.AddProducer(duplicate, "fixture-v1");
        }));

        Assert.Contains("already registered", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, first.Runs);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, duplicate.Runs);
        Assert.Equal(0, duplicate.Disposals);
    }

    [Fact]
    public async Task DuplicateResourceIdentity_ShouldRejectPreparationWithoutStartingProducer()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "first-image@sha256:fixture");
            registration.AddResource("database", "second-image@sha256:fixture");
        }));

        Assert.Contains("already registered", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RegistrationMismatch_ShouldFailWithTypedCodeAndDisposeAcceptedProducer(bool registerProducer, bool wrongProducerId)
    {
        using var fixture = new Fixture();
        var producer = new Producer(wrongProducerId ? "other" : "check");

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => fixture.PrepareAsync(registration =>
        {
            if (registerProducer) registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        }));

        Assert.Equal("ASEVD303", failure.Code);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(registerProducer ? 1 : 0, producer.Disposals);
    }

    [Fact]
    public async Task ExactProducerAndResourceRegistration_ShouldPrepareAndDisposeSuccessfully()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");

        await using (var prepared = await fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        }))
        {
            Assert.Equal("check", Assert.Single(prepared.Plan.Profile.Producers).Id);
            Assert.Equal(["database"], prepared.RequiredResourceIds);
            Assert.Equal(0, producer.Runs);
            Assert.Equal(0, producer.Disposals);
        }

        Assert.Equal(1, producer.Disposals);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unrequested")]
    public async Task InvalidTransferredResourceSet_ShouldRejectExecutionAndDisposeEverySuppliedOwner(string shape)
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var database = new Readiness("database");
        var extra = new Readiness("extra");
        var owner = new Owner();
        await using var prepared = await fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        });

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => prepared.RunAsync((_, _) =>
        {
            IReadOnlyList<IEvidenceResourceReadiness> resources = shape == "missing" ? [] : [database, extra];
            return ValueTask.FromResult(new EvidenceHostResourceScope(resources, owner));
        }));
        await prepared.DisposeAsync();

        Assert.Equal("reuse.resource_scope_invalid", failure.Code);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(shape == "missing" ? 0 : 1, database.Disposals);
        Assert.Equal(shape == "unrequested" ? 1 : 0, extra.Disposals);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task ResourceDisposalFailure_ShouldInvalidateCompletionAfterAttemptingApplicationOwnerCleanup()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var readiness = new Readiness("database", disposalFailure: new IOException("controlled readiness disposal failure"));
        var owner = new Owner();
        await using var prepared = await fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        });

        var result = await prepared.RunAsync((_, _) =>
            ValueTask.FromResult(new EvidenceHostResourceScope([readiness], owner)));

        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.False(result.Metrics.CleanupCompleted);
        Assert.Contains("reuse.cleanup_failed", result.Metrics.CleanupDiagnostic, StringComparison.Ordinal);
        Assert.Equal(1, producer.Runs);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(1, readiness.Disposals);
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task ApplicationOwnerDisposalFailure_ShouldStillDisposeReadinessAndInvalidateClaim()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var readiness = new Readiness("database");
        var owner = new Owner(new IOException("controlled application owner disposal failure"));
        await using var prepared = await fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        });

        var result = await prepared.RunAsync((_, _) =>
            ValueTask.FromResult(new EvidenceHostResourceScope([readiness], owner)));

        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, result.ClaimKind);
        Assert.False(result.Metrics.CleanupCompleted);
        Assert.Contains("reuse.cleanup_failed", result.Metrics.CleanupDiagnostic, StringComparison.Ordinal);
        Assert.Equal(1, readiness.Disposals);
        Assert.Equal(1, owner.Disposals);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task CyclicDeclaredResourceGraph_ShouldRejectPreparationBeforeAnyExecution()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var plan = fixture.PlanWithCyclicResources();

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => EvidenceHostBootstrap.PrepareAsync(
            plan, registration =>
            {
                registration.AddProducer(producer, "fixture-v1");
                registration.AddResource("database", "database@sha256:fixture");
                registration.AddResource("secondary", "secondary@sha256:fixture");
            }, new EvidenceHostOptions
            {
                RepositoryRoot = fixture.Repository,
                ArtifactDirectory = TestPathUtils.PathUnder(fixture.Root, "artifacts"),
                EnvironmentReader = _ => null,
            }));

        Assert.Equal("ASEVD304", failure.Code);
        Assert.Contains("cycle", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
        Assert.False(Directory.Exists(TestPathUtils.PathUnder(fixture.Root, "artifacts")));
    }

    [Fact]
    public async Task NonfatalProducerDisposalFailure_ShouldStillDisposeTransferredResourcesAndDenyClaim()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check", new IOException("controlled producer disposal failure"));
        var readiness = new Readiness("database");
        var owner = new Owner();
        await using var prepared = await fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        });

        var result = await prepared.RunAsync((_, _) =>
            ValueTask.FromResult(new EvidenceHostResourceScope([readiness], owner)));

        Assert.Equal(1, producer.Runs);
        Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(result.ProducerResults).Outcome);
        Assert.Equal(EvidenceExecutionVerdict.Incomplete, result.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, result.ClaimKind);
        Assert.False(result.Metrics.CleanupCompleted);
        Assert.Contains("reuse.cleanup_failed", result.Metrics.CleanupDiagnostic, StringComparison.Ordinal);
        Assert.Equal(1, producer.Disposals);
        Assert.Equal(1, readiness.Disposals);
        Assert.Equal(1, owner.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedHostDoesNotSynthesizeAbsentCapturedPatch(bool provideEmptySnapshot)
    {
        using var fixture = new Fixture();
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        const string selectedInput = "Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidencePreparedRegistrationCoverageTests.cs";
        var producer = new Producer("patch-bound-check");
        var declaration = new EvidenceProducerDeclaration(producer.Id, "coverage", "1", ["database"], ["valid"], [], 30)
        {
            Inputs = new EvidenceProducerInputs([selectedInput], []),
            CoverageGate = new EvidenceCoverageGateRequirements(95, 85, 95, 85),
            CoverageExecution = new EvidenceCoverageExecutionOptions { CooperativeChildLifetime = true },
        };
        var profile = new EvidenceProfile("patch-bound", EvidenceProfileScope.Targeted,
            [new EvidenceResourceDeclaration("database", "completion", 30, [])], [declaration],
            [new EvidenceObligation("coverage-obligation", "test", "Fixture", [producer.Id], "valid")]);
        var plan = new EvidencePlanner().Resolve(
            new("patch-policy", "1", profile.Id, [profile], [new("all", "**", profile.Id)]),
            [new NormalizedDiffPath(selectedInput)]);
        // FreezePlan accepts and digest-binds a supplied patch digest even though ResolvePatch rejects empty bytes.
        plan = plan with { PatchSha256 = EvidenceDigest.Sha256([]), PlanDigest = string.Empty };
        plan = plan with { PlanDigest = EvidenceDigest.CanonicalSha256(plan) };
        var store = TestPathUtils.PathUnder(fixture.Root, "patch-receipts", provideEmptySnapshot ? "empty" : "absent");
        var artifactRoot = TestPathUtils.PathUnder(fixture.Root, "patch-artifacts", provideEmptySnapshot ? "empty" : "absent");
        var options = new EvidenceHostOptions
        {
            Reuse = true,
            RepositoryRoot = repositoryRoot,
            ReceiptStore = store,
            ArtifactDirectory = artifactRoot,
            CapturedPatch = provideEmptySnapshot ? ReadOnlyMemory<byte>.Empty : (ReadOnlyMemory<byte>?)null,
            EnvironmentReader = _ => null,
        };

        var prepared = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        }, options);
        try
        {
            var decision = Assert.Single(prepared.Decisions);
            if (provideEmptySnapshot)
            {
                Assert.Equal(EvidenceReuseStatus.Miss, decision.Status);
                Assert.NotNull(decision.ProducerKey);
                Assert.True(Directory.Exists(store));
            }
            else
            {
                Assert.Equal(EvidenceReuseStatus.Ineligible, decision.Status);
                Assert.Equal("reuse.identity.patch_unavailable", decision.Reason);
                Assert.Null(decision.ProducerKey);
                Assert.False(Directory.Exists(store));
            }
            Assert.Equal(0, producer.Runs);
            Assert.True(Directory.Exists(artifactRoot));
            Assert.Empty(Directory.EnumerateFileSystemEntries(artifactRoot));
        }
        finally
        {
            await prepared.DisposeAsync();
        }

        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task OptedInNoArtifactProducer_ShouldReuseSuccessfulResultWithoutStartingResources()
    {
        using var fixture = new Fixture();
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        const string selectedInput = "Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidencePreparedRegistrationCoverageTests.cs";
        var declaration = new EvidenceProducerDeclaration("check", "custom", "1", [], ["valid"], [], 30)
        {
            Inputs = new EvidenceProducerInputs([selectedInput], []),
        };
        var profile = new EvidenceProfile("no-artifact-reuse", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("check-obligation", "test", "Fixture", ["check"], "valid")]);
        var policy = new EvidencePolicy("fixture", "1", profile.Id, [profile], [new("all", "**", profile.Id)]);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath(selectedInput)]);
        var store = TestPathUtils.PathUnder(fixture.Root, "no-artifact-receipts");
        var firstOutput = TestPathUtils.PathUnder(fixture.Root, "no-artifact-first-output");
        var secondOutput = TestPathUtils.PathUnder(fixture.Root, "no-artifact-second-output");
        var firstProducer = new Producer("check");
        string? firstProducerKey = null;
        var firstOptions = new EvidenceHostOptions
        {
            Reuse = true,
            RepositoryRoot = repositoryRoot,
            ReceiptStore = store,
            ArtifactDirectory = firstOutput,
            EnvironmentReader = _ => null,
        };

        await using (var first = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            registration.AddProducer(firstProducer, "fixture-v1");
        }, firstOptions))
        {
            Assert.Empty(first.RequiredResourceIds);
            Assert.Equal(EvidenceReuseStatus.Miss, Assert.Single(first.Decisions).Status);
            firstProducerKey = Assert.Single(first.Decisions).ProducerKey;
            Assert.NotNull(firstProducerKey);
            var factoryCalls = 0;
            var fresh = await first.RunAsync((_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("A run with no required resources must not invoke the factory.");
            });

            Assert.Equal(0, factoryCalls);
            Assert.Equal(EvidenceExecutionVerdict.Passed, fresh.ExecutionVerdict);
            Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(fresh.ProducerResults).Outcome);
            Assert.Equal(1, firstProducer.Runs);
        }

        Assert.Equal(1, firstProducer.Disposals);
        var secondProducer = new Producer("check");
        var secondOptions = new EvidenceHostOptions
        {
            Reuse = true,
            RepositoryRoot = repositoryRoot,
            ReceiptStore = store,
            ArtifactDirectory = secondOutput,
            EnvironmentReader = _ => null,
        };
        await using (var second = await EvidenceHostBootstrap.PrepareAsync(plan, registration =>
        {
            registration.AddProducer(secondProducer, "fixture-v1");
        }, secondOptions))
        {
            Assert.Empty(second.RequiredResourceIds);
            var decision = Assert.Single(second.Decisions);
            Assert.Equal(EvidenceReuseStatus.Hit, decision.Status);
            Assert.Equal(firstProducerKey, decision.ProducerKey);
            var factoryCalls = 0;
            var reused = await second.RunAsync((_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("An all-hit run with no resources must not invoke the factory.");
            });

            Assert.Equal(0, factoryCalls);
            Assert.Equal(0, secondProducer.Runs);
            Assert.Equal(EvidenceExecutionVerdict.Passed, reused.ExecutionVerdict);
            Assert.Equal(EvidenceClaimKind.LocalReusedComplete, reused.ClaimKind);
            Assert.Equal(EvidenceProducerOutcome.Reused, Assert.Single(reused.ProducerResults).Outcome);
        }

        Assert.Equal(0, secondProducer.Runs);
        Assert.Equal(1, secondProducer.Disposals);
    }

    [Fact]
    public async Task ContendedReceiptPreparation_ShouldReportBoundedWaitProgressAndResumeAfterOwnerDisposal()
    {
        using var fixture = new Fixture();
        var repositoryRoot = GitRepositoryRootResolver.FindRepositoryRoot(AppContext.BaseDirectory);
        const string selectedInput = "Aspire/ForgeTrust.AppSurface.Aspire.Tests/EvidencePreparedRegistrationCoverageTests.cs";
        var declaration = new EvidenceProducerDeclaration("check", "custom", "1", [], ["valid"], [], 30)
        {
            Inputs = new EvidenceProducerInputs([selectedInput], []),
        };
        var profile = new EvidenceProfile("wait-progress", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("check-obligation", "test", "Fixture", ["check"], "valid")]);
        var plan = new EvidencePlanner().Resolve(
            new EvidencePolicy("fixture", "1", profile.Id, [profile], [new("all", "**", profile.Id)]),
            [new NormalizedDiffPath(selectedInput)]);
        var store = TestPathUtils.PathUnder(fixture.Root, "wait-progress-receipts");
        var progress = new WaitProgress();
        var firstProducer = new Producer("check");
        var first = await EvidenceHostBootstrap.PrepareAsync(plan,
            registration => registration.AddProducer(firstProducer, "fixture-v1"),
            new EvidenceHostOptions
            {
                Reuse = true,
                ReceiptWaitTimeout = TimeSpan.FromSeconds(10),
                RepositoryRoot = repositoryRoot,
                ReceiptStore = store,
                ArtifactDirectory = TestPathUtils.PathUnder(fixture.Root, "wait-progress-first"),
                EnvironmentReader = _ => null,
            });
        Task<EvidencePreparedRun>? preparingSecond = null;
        EvidencePreparedRun? second = null;

        try
        {
            Assert.Equal(EvidenceReuseStatus.Miss, Assert.Single(first.Decisions).Status);
            var secondProducer = new Producer("check");
            preparingSecond = EvidenceHostBootstrap.PrepareAsync(plan,
                registration => registration.AddProducer(secondProducer, "fixture-v1"),
                new EvidenceHostOptions
                {
                    Reuse = true,
                    ReceiptWaitTimeout = TimeSpan.FromSeconds(10),
                    ReceiptStore = store,
                    RepositoryRoot = repositoryRoot,
                    ArtifactDirectory = TestPathUtils.PathUnder(fixture.Root, "wait-progress-second"),
                    ReceiptWaitProgress = progress,
                    EnvironmentReader = _ => null,
                });

            var update = await progress.Reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEmpty(update.ProducerKeyPrefix);
            Assert.True(update.Elapsed >= TimeSpan.Zero);
            Assert.True(update.Remaining > TimeSpan.Zero);

            await first.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            second = await preparingSecond.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EvidenceReuseStatus.Miss, Assert.Single(second.Decisions).Status);
            Assert.Equal(0, secondProducer.Runs);
        }
        finally
        {
            await first.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (second is null && preparingSecond is not null)
            {
                try { second = await preparingSecond.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (preparingSecond.IsCompleted) { }
            }
            if (second is not null) await second.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1, firstProducer.Disposals);
    }

    [Fact]
    public async Task ToolIdentityRegistration_ShouldAllowFreshExecutionWithoutStartingWorkDuringPreparation()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var toolIdentity = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["compiler"] = "compiler-v4",
            ["arguments"] = "deterministic",
        };
        var readiness = new Readiness("database");
        var owner = new Owner();

        await using (var prepared = await fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v2", toolIdentity);
            registration.AddResource("database", "database@sha256:fixture");
        }))
        {
            toolIdentity["compiler"] = "changed-after-registration";
            Assert.Equal(0, producer.Runs);
            Assert.Equal(0, producer.Disposals);
            var decision = Assert.Single(prepared.Decisions);
            Assert.Equal("check", decision.ProducerId);
            Assert.Equal(EvidenceReuseStatus.Ineligible, decision.Status);
            Assert.Equal("reuse.disabled", decision.Reason);

            var result = await prepared.RunAsync((ids, _) =>
            {
                Assert.Equal(["database"], ids);
                return ValueTask.FromResult(new EvidenceHostResourceScope([readiness], owner));
            });

            Assert.Equal(EvidenceExecutionVerdict.Passed, result.ExecutionVerdict);
            Assert.Equal(EvidenceProducerOutcome.Passed, Assert.Single(result.ProducerResults).Outcome);
            Assert.Equal(1, producer.Runs);
            Assert.Equal(1, readiness.ReadinessCalls);
            Assert.Equal(1, readiness.Disposals);
            Assert.Equal(1, owner.Disposals);
        }

        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task SuccessfulPreparationFreezesEveryRegistrationMutation()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var lateProducer = new Producer("late");
        EvidenceHostPreparationRegistration? captured = null;

        await using (var prepared = await fixture.PrepareAsync(registration =>
        {
            captured = registration;
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        }))
        {
            Assert.Throws<InvalidOperationException>(() => captured!.AddProducer(lateProducer, "fixture-v1"));
            Assert.Throws<InvalidOperationException>(() => captured!.AddResource("late-resource", "fixture"));
            Assert.Throws<InvalidOperationException>(() => captured!.SetEnvelopeVerifier(new EnvelopeVerifier()));
            Assert.Equal(["database"], prepared.RequiredResourceIds);
            Assert.Equal(0, producer.Runs);
        }

        Assert.Equal(1, producer.Disposals);
        Assert.Equal(0, lateProducer.Runs);
        Assert.Equal(0, lateProducer.Disposals);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("src//file.txt")]
    [InlineData("src/../secret.txt")]
    [InlineData("src/**file.txt")]
    [InlineData("/absolute/path")]
    [InlineData("src\\file.txt")]
    public void PlannerRejectsUnsafeProducerInputPatterns(string? pattern)
    {
        var declaration = new EvidenceProducerDeclaration("check", "custom", "1", [], ["valid"], [], 30)
        { Inputs = new EvidenceProducerInputs([pattern!], []) };
        var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted, [], [declaration],
            [new EvidenceObligation("check-obligation", "test", "Fixture", ["check"], "valid")]);
        var policy = new EvidencePolicy("fixture", "1", "local", [profile], [new("all", "**", "local")]);

        var failure = Assert.Throws<EvidencePlanningException>(() => new EvidencePlanner().Resolve(policy,
            [new NormalizedDiffPath("input.txt")]));

        Assert.Equal("ASEVD137", failure.Code);
        Assert.Contains("invalid input pattern", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingResourceIdentity_ShouldFailPreparationAfterCleaningRegisteredProducer()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => fixture.PrepareAsync(registration =>
            registration.AddProducer(producer, "fixture-v1")));

        Assert.Equal("ASEVD302", failure.Code);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task ExtraUndeclaredResourceIdentity_ShouldFailExactSetCheckAndDisposeProducer()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
            registration.AddResource("undeclared", "extra@sha256:fixture");
        }));

        Assert.Equal("ASEVD302", failure.Code);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task RejectedExecutionEnvelope_ShouldStopPreparationBeforeRunAndDisposeProducer()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var verifier = new EnvelopeVerifier(accepted: false);

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
            registration.SetEnvelopeVerifier(verifier);
        }, requireEnvelope: true));

        Assert.Equal("reuse.envelope_invalid", failure.Code);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task RequiredEnvelopeWithoutVerifier_ShouldRejectBeforeRegistrationTransfer()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");

        var failure = await Assert.ThrowsAsync<EvidenceHostException>(() => fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
        }, requireEnvelope: true));

        Assert.Equal("ASEVD302", failure.Code);
        Assert.Contains("no execution-envelope verifier", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task AcceptedExecutionEnvelope_ShouldRecordValidationAndTransferOwnership()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check");
        var verifier = new EnvelopeVerifier();

        await using (var prepared = await fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            registration.AddResource("database", "database@sha256:fixture");
            registration.SetEnvelopeVerifier(verifier);
        }, requireEnvelope: true))
        {
            Assert.Equal(EvidenceEnvelopeStatus.ValidatedNotAttested,
                (await prepared.RunAsync((_, _) => ValueTask.FromException<EvidenceHostResourceScope>(
                    new IOException("controlled resource startup failure")))).EnvelopeStatus);
            await prepared.DisposeAsync();
            Assert.Equal(1, verifier.Calls);
            Assert.Equal(0, producer.Runs);
            Assert.Equal(1, producer.Disposals);
        }

        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public async Task RegistrationFailure_ShouldPreserveOriginalFailureWhenOwnedDisposalAlsoFails()
    {
        using var fixture = new Fixture();
        var producer = new Producer("check", new IOException("controlled disposal failure"));
        var configureFailure = new InvalidDataException("controlled registration failure");

        var observed = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PrepareAsync(registration =>
        {
            registration.AddProducer(producer, "fixture-v1");
            throw configureFailure;
        }));

        Assert.Same(configureFailure, observed);
        Assert.Equal(0, producer.Runs);
        Assert.Equal(1, producer.Disposals);
    }

    [Fact]
    public void Registration_ShouldRejectUnsafeIdentityAndRepeatedEnvelopeVerifier()
    {
        var registration = new EvidenceHostPreparationRegistration();
        Assert.Throws<ArgumentException>(() => registration.AddResource("database", "bad\nidentity"));
        registration.SetEnvelopeVerifier(new EnvelopeVerifier());
        Assert.Throws<InvalidOperationException>(() => registration.SetEnvelopeVerifier(new EnvelopeVerifier()));
        Assert.Throws<ArgumentException>(() => registration.AddResource("database", ""));
    }

    [Fact]
    public void ProducerRegistration_ShouldEnforceIdentityAndToolBoundsBeforeAddingRegistration()
    {
        var registration = new EvidenceHostPreparationRegistration();
        var producer = new Producer("check");
        var tooManyTools = Enumerable.Range(0, 65).ToDictionary(static index => $"tool-{index}", static _ => "v1");

        var oversizedImplementation = Assert.Throws<ArgumentException>(() =>
            registration.AddProducer(producer, new string('i', 257)));
        var excessiveTools = Assert.Throws<ArgumentException>(() =>
            registration.AddProducer(producer, "fixture-v1", tooManyTools));
        var invalidToolKey = Assert.Throws<ArgumentException>(() =>
            registration.AddProducer(producer, "fixture-v1", new Dictionary<string, string> { ["bad\nkey"] = "v1" }));
        Assert.Throws<ArgumentNullException>(() => registration.SetEnvelopeVerifier(null!));

        Assert.Equal("implementationIdentity", oversizedImplementation.ParamName);
        Assert.Equal("toolIdentity", excessiveTools.ParamName);
        Assert.Equal("toolIdentity", invalidToolKey.ParamName);
        var tools = new Dictionary<string, string> { ["sdk"] = "10.0" };
        registration.AddProducer(producer, "fixture-v1", tools);
        tools["sdk"] = "mutated after registration";
        Assert.Equal("10.0", registration.Freeze()["check"].ToolIdentity["sdk"]);
    }

    [Fact]
    public async Task ArtifactStreamOverflow_ShouldRemovePartialFileAndReleaseSlotForValidRetry()
    {
        var parent = Directory.CreateTempSubdirectory("evidence-artifact-retry-").FullName;
        try
        {
            var declaration = new EvidenceProducerDeclaration("writer", "custom", "1", [], [],
                [new EvidenceArtifactSlot("report", "reports", "text/plain", true, 3)], 30);
            var root = TestPathUtils.PathUnder(parent, "artifacts");
            var path = TestPathUtils.PathUnder(root, "reports", "result.txt");
            var writer = new EvidenceArtifactWriter(declaration, root);

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.WriteAsync(
                "report", "reports/result.txt", new MemoryStream(new byte[] { 1, 2, 3, 4 })));

            Assert.Empty(writer.WrittenArtifacts);
            Assert.False(File.Exists(path));
            var written = await writer.WriteAsync("report", "reports/result.txt", new MemoryStream(new byte[] { 1, 2, 3 }));
            Assert.Equal(3, written.LengthBytes);
            Assert.Equal("text/plain", written.MediaType);
            Assert.True(await writer.VerifyWrittenArtifactsAsync());

            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            Assert.False(await writer.VerifyWrittenArtifactsAsync());
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void MultiFileArtifactValidation_ShouldRequireCompletePortableUniquePaths()
    {
        var slot = new EvidenceArtifactSlot("report", "reports", "text/plain", true, 20) { MaximumFiles = 2 };
        var producer = new EvidenceProducerDeclaration("writer", "custom", "1", [], [], [slot], 30);
        var first = new EvidenceArtifactResult("report", "reports/one.txt", "text/plain", 1, new string('a', 64));
        var second = new EvidenceArtifactResult("report", "reports/two.txt", "text/plain", 1, new string('b', 64));

        Assert.True(EvidenceArtifactValidation.AreValid(producer, [first, second]));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [first, second with { RelativePath = "reports/ONE.txt" }]));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, [first with { RelativePath = "reports/CON.txt" }]));
        Assert.False(EvidenceArtifactValidation.AreValid(producer, []));
        Assert.False(EvidenceArtifactValidation.AreValid(producer with
        {
            ArtifactSlots = [slot with { MaximumFiles = 0 }],
        }, [first]));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("prepared-registration-evidence-").FullName;
        internal string Repository => TestPathUtils.PathUnder(Root, "repo");

        internal Fixture()
        {
            Directory.CreateDirectory(Repository);
        }

        internal Task<EvidencePreparedRun> PrepareAsync(Action<EvidenceHostPreparationRegistration> configure, bool requireEnvelope = false)
        {
            var plan = PlanWithDatabase();
            return EvidenceHostBootstrap.PrepareAsync(plan, configure, new EvidenceHostOptions
            {
                Reuse = false,
                RequireTrustedEnvelope = requireEnvelope,
                RepositoryRoot = Repository,
                ArtifactDirectory = TestPathUtils.PathUnder(Root, "artifacts"),
                EnvironmentReader = _ => null,
            });
        }

        internal EvidencePlan PlanWithCyclicResources()
        {
            var declaration = new EvidenceProducerDeclaration("check", "custom", "1", ["database"], ["valid"], [], 30);
            var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted,
                [new EvidenceResourceDeclaration("database", "completion", 30, ["secondary"]),
                 new EvidenceResourceDeclaration("secondary", "completion", 30, ["database"])],
                [declaration], [new EvidenceObligation("check-obligation", "test", "Fixture", ["check"], "valid")]);
            return new EvidencePlanner().Resolve(
                new("fixture", "1", "local", [profile], [new("all", "**", "local")]),
                [new NormalizedDiffPath("input.txt")]);
        }

        private static EvidencePlan PlanWithDatabase()
        {
            var declaration = new EvidenceProducerDeclaration("check", "custom", "1", ["database"], ["valid"], [], 30);
            var profile = new EvidenceProfile("local", EvidenceProfileScope.Targeted,
                [new EvidenceResourceDeclaration("database", "completion", 30, [])], [declaration],
                [new EvidenceObligation("check-obligation", "test", "Fixture", ["check"], "valid")]);
            return new EvidencePlanner().Resolve(
                new("fixture", "1", "local", [profile], [new("all", "**", "local")]),
                [new NormalizedDiffPath("input.txt")]);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class Producer(string id, Exception? disposalFailure = null) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id => id;
        internal int Runs { get; private set; }
        internal int Disposals { get; private set; }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return ValueTask.FromResult(new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, ["valid"]));
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return disposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposalFailure);
        }
    }

    private sealed class Readiness(string id, Exception? disposalFailure = null) : IEvidenceResourceReadiness, IAsyncDisposable
    {
        public string Id => id;
        internal int ReadinessCalls { get; private set; }
        internal int Disposals { get; private set; }
        public Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadinessCalls++;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (disposalFailure is not null) throw disposalFailure;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Owner(Exception? failure = null) : IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }

    private sealed class EnvelopeVerifier(bool accepted = true) : IEvidenceExecutionEnvelopeVerifier
    {
        internal int Calls { get; private set; }
        public ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new EvidenceEnvelopeResult(accepted, false));
        }
    }

    private sealed class WaitProgress : IProgress<EvidenceReuseWaitProgress>
    {
        internal TaskCompletionSource<EvidenceReuseWaitProgress> Reported { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Report(EvidenceReuseWaitProgress value) => Reported.TrySetResult(value);
    }
}
