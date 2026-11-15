using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Evidence.Validation;

namespace ForgeTrust.AppSurface.Cli.Tests;

// Keep these lifecycle tests in native fan-in because raw MoveNext branches require
// real execution; see CoverageFanInComposedPatchTests.RetainedNativeMethodBranchesRequireRealFullObservationBeforePatchAcceptance.
[Collection("CoverageGate process state")]
public sealed class CoverageArtifactNativeValidationExecutionTests
{
    private const string ProducerId = "ordinary-recipe";
    private const string AssertionId = "ordinary/assertion@1";
    private static readonly string InputDigest = new('a', 64);

    [Fact]
    public async Task ExecuteAsync_RequiredAssertionAndArtifactPassWithoutEvidenceHostClaim()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var producer = new FixedRecipeProducer(ProducerId, async (context, token) =>
        {
            var artifact = await WriteArtifactAsync(context, token);
            return Passed([AssertionId], [artifact]);
        });

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("passed", receipt.RunOutcome);
        Assert.True(receipt.CleanupCompleted);
        Assert.Equal([AssertionId], receipt.Journeys.Single().SatisfiedAssertionIds);
        Assert.Single(receipt.Journeys.Single().Artifacts);
        Assert.Equal("none", receipt.EvidenceHostClaim);
        Assert.Equal("none", receipt.EvidenceHostEligibility);
        Assert.False(receipt.EvidenceHostProofProduced);
        Assert.False(NativeValidationReceiptCodec.Verify(plan, receipt with { EvidenceHostClaim = "targeted-complete" }));
    }

    [Theory]
    [InlineData("missing-assertion")]
    [InlineData("undeclared-assertion")]
    [InlineData("missing-result")]
    [InlineData("missing-artifact")]
    public async Task ExecuteAsync_InvalidOrIncompleteProducerEvidenceCannotPass(string defect)
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var producer = new FixedRecipeProducer(ProducerId, async (context, token) =>
        {
            if (defect != "missing-artifact")
            {
                await WriteArtifactAsync(context, token);
            }

            return defect switch
            {
                "missing-assertion" => Passed([], null),
                "undeclared-assertion" => Passed(["ordinary/other-assertion@1"], null),
                "missing-result" => null!,
                "missing-artifact" => Passed([AssertionId], null),
                _ => throw new InvalidOperationException("Unknown recipe-test defect."),
            };
        });

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.Equal("invalid", receipt.Journeys.Single().Outcome);
        Assert.True(receipt.CleanupCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_CleanupFailurePreventsPositiveRunOutcome()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var producer = new FixedRecipeProducer(ProducerId, async (context, token) =>
        {
            var artifact = await WriteArtifactAsync(context, token);
            return Passed([AssertionId], [artifact]);
        }, () => ValueTask.FromException(new IOException("recipe cleanup failed")));

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.False(receipt.CleanupCompleted);
        Assert.NotNull(receipt.CleanupDiagnostic);
        Assert.Equal("passed", receipt.Journeys.Single().Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SynchronousDisposerControlsCleanupOutcome(bool failDisposal)
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var disposed = false;
        var producer = new FixedSyncDisposableProducer(ProducerId, async (context, token) =>
        {
            var artifact = await WriteArtifactAsync(context, token);
            return Passed([AssertionId], [artifact]);
        }, () =>
        {
            disposed = true;
            if (failDisposal) throw new IOException("synchronous recipe cleanup failed");
        });

        var receipt = await NativeValidationExecution.ExecuteAsync(
            plan, [producer], directory.Root, InputDigest, CancellationToken.None);

        Assert.True(disposed);
        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal(failDisposal ? "failed" : "passed", receipt.RunOutcome);
        Assert.Equal(!failDisposal, receipt.CleanupCompleted);
        Assert.Equal("passed", receipt.Journeys.Single().Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_UnjoinedSynchronousDisposerThrowsBeforeReceiptAndJoinsAfterRelease()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var disposalStarted = NewSignal();
        var releaseDisposal = NewSignal();
        var disposalFinished = NewSignal();
        var producer = new FixedSyncDisposableProducer(ProducerId, (_, _) =>
            ValueTask.FromResult(new EvidenceProducerResult(ProducerId, EvidenceProducerOutcome.Failed, [], "expected failure.")), () =>
            {
                disposalStarted.TrySetResult();
                try
                {
                    releaseDisposal.Task.GetAwaiter().GetResult();
                }
                finally
                {
                    disposalFinished.TrySetResult();
                }
            });
        var execution = NativeValidationExecution.ExecuteAsync(
            plan, [producer], directory.Root, InputDigest, CancellationToken.None, TimeSpan.FromMilliseconds(50));

        try
        {
            await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<NativeValidationUnjoinedWorkException>(() =>
                execution.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.False(disposalFinished.Task.IsCompleted);
            Assert.False(File.Exists(Path.Combine(directory.Root, "native-validation-receipt.json")));
        }
        finally
        {
            releaseDisposal.TrySetResult();
            await disposalFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ObserveCompletionAsync(execution);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ArtifactChangedDuringCleanupIsRejectedByFinalVerification()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var artifactPath = Path.Combine(directory.Root, ProducerId, "proof", "result.json");
        var producer = new FixedRecipeProducer(ProducerId, async (context, token) =>
        {
            var artifact = await WriteArtifactAsync(context, token);
            return Passed([AssertionId], [artifact]);
        }, async () => await File.WriteAllTextAsync(artifactPath, "tampered after callback"));

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.Equal("invalid", receipt.Journeys.Single().Outcome);
        Assert.Equal("tampered after callback", await File.ReadAllTextAsync(artifactPath));
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("producer-directory")]
    public async Task ExecuteAsync_ArtifactRemovedDuringCleanupFailsFinalVerification(string removal)
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var artifactPath = Path.Combine(directory.Root, ProducerId, "proof", "result.json");
        var producer = new FixedRecipeProducer(ProducerId, async (context, token) =>
        {
            var artifact = await WriteArtifactAsync(context, token);
            return Passed([AssertionId], [artifact]);
        }, () =>
        {
            if (removal == "artifact") File.Delete(artifactPath);
            else Directory.Delete(Path.Combine(directory.Root, ProducerId), recursive: true);
            return ValueTask.CompletedTask;
        });

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.Equal("invalid", receipt.Journeys.Single().Outcome);
        Assert.Single(receipt.Journeys.Single().Artifacts);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidInputDigestFailsInternalReceiptVerificationAfterDisposal()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var disposed = false;
        var producer = new FixedRecipeProducer(ProducerId, async (context, token) =>
        {
            var artifact = await WriteArtifactAsync(context, token);
            return Passed([AssertionId], [artifact]);
        }, () =>
        {
            disposed = true;
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => NativeValidationExecution.ExecuteAsync(
            plan, [producer], directory.Root, "invalid-digest", CancellationToken.None));

        Assert.True(disposed);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationJoinsProducerBeforeDisposal()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var plan = ResolvePlan();
        var started = NewSignal();
        var cancellationObserved = NewSignal();
        var release = NewSignal();
        var producerFinished = NewSignal();
        var disposed = false;
        var producer = new FixedRecipeProducer(ProducerId, async (_, token) =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            started.TrySetResult();
            try
            {
                await release.Task.ConfigureAwait(false);
                return Passed([AssertionId], null);
            }
            finally
            {
                producerFinished.TrySetResult();
            }
        }, () =>
        {
            disposed = true;
            return ValueTask.CompletedTask;
        });

        var execution = ExecuteAsync(plan, producer, directory.Root, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(execution.IsCompleted);
            Assert.False(disposed);
            release.TrySetResult();
            var receipt = await execution.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(producerFinished.Task.IsCompleted);
            Assert.True(disposed);
            Assert.Equal("cancelled", receipt.RunOutcome);
            Assert.True(receipt.CleanupCompleted);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await ObserveCompletionAsync(execution);
            if (started.Task.IsCompleted)
            {
                await producerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_CompletionAfterDeclaredOneSecondDeadlineCannotPass()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan(timeoutSeconds: 1);
        var producer = new FixedRecipeProducer(ProducerId, async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            return Passed([AssertionId], null);
        });

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.Equal("timedout", receipt.Journeys.Single().Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_UnjoinedWorkThrowsWithoutDisposalUntilCallbackFinishes()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var plan = ResolvePlan();
        var started = NewSignal();
        var release = NewSignal();
        var producerFinished = NewSignal();
        var disposed = false;
        var tokenUsableAfterUnjoinedReturn = false;
        var producer = new FixedRecipeProducer(ProducerId, async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await release.Task.ConfigureAwait(false);
                // Unjoined work still owns its cancellation lifetime after ExecuteAsync faults.
                Assert.NotNull(token.WaitHandle);
                using var registration = token.Register(static () => { });
                tokenUsableAfterUnjoinedReturn = true;
                return Passed([AssertionId], null);
            }
            finally
            {
                producerFinished.TrySetResult();
            }
        }, () =>
        {
            disposed = true;
            return ValueTask.CompletedTask;
        });

        var execution = ExecuteAsync(plan, producer, directory.Root, cancellation.Token, TimeSpan.FromMilliseconds(50));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAsync<NativeValidationUnjoinedWorkException>(() => execution);
            Assert.False(producerFinished.Task.IsCompleted);
            Assert.False(disposed);
            release.TrySetResult();
            await producerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(disposed);
            Assert.True(tokenUsableAfterUnjoinedReturn);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await ObserveCompletionAsync(execution);
            if (started.Task.IsCompleted)
            {
                await producerFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_UnjoinedDisposerThrowsWithoutPublishingReceipt()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var disposalStarted = NewSignal();
        var releaseDisposal = NewSignal();
        var disposalFinished = NewSignal();
        var producer = new FixedRecipeProducer(ProducerId, (_, _) =>
            ValueTask.FromResult(new EvidenceProducerResult(ProducerId, EvidenceProducerOutcome.Failed, [], "expected failure.")),
            async () =>
            {
                disposalStarted.TrySetResult();
                try
                {
                    await releaseDisposal.Task.ConfigureAwait(false);
                }
                finally
                {
                    disposalFinished.TrySetResult();
                }
            });
        var execution = ExecuteAsync(plan, producer, directory.Root, loweredJoinAllowance: TimeSpan.FromMilliseconds(50));

        try
        {
            await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<NativeValidationUnjoinedWorkException>(() => execution);

            Assert.False(disposalFinished.Task.IsCompleted);
            Assert.False(File.Exists(Path.Combine(directory.Root, "native-validation-receipt.json")));
        }
        finally
        {
            releaseDisposal.TrySetResult();
            await ObserveCompletionAsync(execution);
            if (disposalStarted.Task.IsCompleted)
            {
                await disposalFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyCancelledCallerDoesNotDispatchAndStillDisposes()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var dispatched = false;
        var disposed = false;
        var plan = ResolvePlan();
        var producer = new FixedRecipeProducer(ProducerId, (_, _) =>
        {
            dispatched = true;
            return ValueTask.FromResult(Passed([AssertionId], null));
        }, () =>
        {
            disposed = true;
            return ValueTask.CompletedTask;
        });

        var receipt = await ExecuteAsync(plan, producer, directory.Root, cancellation.Token);

        Assert.False(dispatched);
        Assert.True(disposed);
        Assert.Equal("cancelled", receipt.RunOutcome);
        Assert.Equal("cancelled", receipt.Journeys.Single().Outcome);
        Assert.True(receipt.CleanupCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_EarlierSettledFailureDoesNotSuppressLaterSelectedRecipe()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolveTwoRecipePlan();
        var laterStarted = NewSignal();
        var first = new FixedRecipeProducer(ProducerId, (_, _) =>
            ValueTask.FromResult(new EvidenceProducerResult(ProducerId, EvidenceProducerOutcome.Failed, [], "expected failure.")));
        var later = new FixedRecipeProducer("later-recipe", async (context, token) =>
        {
            laterStarted.TrySetResult();
            var artifact = await WriteArtifactAsync(context, token);
            return new EvidenceProducerResult("later-recipe", EvidenceProducerOutcome.Passed, ["ordinary/later-assertion@1"], Artifacts: [artifact]);
        });

        var receipt = await NativeValidationExecution.ExecuteAsync(
            plan, [first, later], directory.Root, InputDigest, CancellationToken.None);

        Assert.True(laterStarted.Task.IsCompleted);
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.Equal(["failed", "passed"], receipt.Journeys.Select(static journey => journey.Outcome));
        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
    }

    [Fact]
    public async Task ExecuteAsync_FatalCallbackExceptionPropagatesWithoutDisposalOrReceipt()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var started = NewSignal();
        var disposed = false;
        var producer = new FixedRecipeProducer(ProducerId, (_, _) =>
        {
            started.TrySetResult();
            return ValueTask.FromException<EvidenceProducerResult>(new AccessViolationException("managed fatal callback failure"));
        }, () =>
        {
            disposed = true;
            return ValueTask.CompletedTask;
        });
        var execution = ExecuteAsync(plan, producer, directory.Root);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<AccessViolationException>(() => execution);

        Assert.False(disposed);
        Assert.False(File.Exists(Path.Combine(directory.Root, "native-validation-receipt.json")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task ExecuteAsync_InvalidJoinAllowanceRejectsBeforeDispatch(int joinAllowanceSeconds)
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var dispatched = false;
        var disposed = false;
        var producer = new FixedRecipeProducer(ProducerId, (_, _) =>
        {
            dispatched = true;
            return ValueTask.FromResult(Passed([AssertionId], null));
        }, () =>
        {
            disposed = true;
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NativeValidationExecution.ExecuteAsync(
            plan, [producer], directory.Root, InputDigest, CancellationToken.None, TimeSpan.FromSeconds(joinAllowanceSeconds)));

        Assert.False(dispatched);
        Assert.False(disposed);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsEmptyRecipe()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolveEmptyRecipePlan();

        await Assert.ThrowsAsync<ArgumentException>(() => NativeValidationExecution.ExecuteAsync(
            plan, [], directory.Root, InputDigest, CancellationToken.None));
    }

    [Theory]
    [InlineData("resources")]
    [InlineData("producer-count")]
    [InlineData("zero-timeout")]
    [InlineData("duplicate-declarations")]
    [InlineData("duplicate-producers")]
    [InlineData("missing-registration")]
    public async Task ExecuteAsync_InvalidRecipeRegistryRejectsBeforeDispatch(string defect)
    {
        using var directory = new TemporaryDirectory();
        var dispatched = 0;
        var plan = ResolvePlan();
        var declaration = plan.Profile.Producers.Single();
        var producers = new List<FixedRecipeProducer>();
        EvidenceProfile profile;

        switch (defect)
        {
            case "resources":
                profile = plan.Profile with
                {
                    Resources = [new EvidenceResourceDeclaration("recipe-resource", "aspire_health", 5, [])],
                };
                producers.Add(CreateProducer(ProducerId));
                break;
            case "producer-count":
                profile = plan.Profile;
                break;
            case "zero-timeout":
                profile = plan.Profile with { Producers = [declaration with { TimeoutSeconds = 0 }] };
                producers.Add(CreateProducer(ProducerId));
                break;
            case "duplicate-declarations":
                profile = plan.Profile with { Producers = [declaration, declaration] };
                producers.Add(CreateProducer(ProducerId));
                producers.Add(CreateProducer("different-producer"));
                break;
            case "duplicate-producers":
                plan = ResolveTwoRecipePlan();
                profile = plan.Profile;
                producers.Add(CreateProducer(ProducerId));
                producers.Add(CreateProducer(ProducerId));
                break;
            case "missing-registration":
                profile = plan.Profile with { Producers = [declaration with { Id = "unregistered-recipe" }] };
                producers.Add(CreateProducer(ProducerId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }

        plan = plan with { Profile = profile };

        await Assert.ThrowsAsync<ArgumentException>(() => NativeValidationExecution.ExecuteAsync(
            plan, producers, Path.Combine(directory.Root, "artifacts"), InputDigest, CancellationToken.None));

        Assert.Equal(0, dispatched);
        Assert.False(Directory.Exists(Path.Combine(directory.Root, "artifacts")));

        FixedRecipeProducer CreateProducer(string id) => new(id, (_, _) =>
        {
            dispatched++;
            return ValueTask.FromResult(new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, []));
        });
    }

    [Theory]
    [InlineData("io")]
    [InlineData("operation-cancelled")]
    public async Task ExecuteAsync_NonFatalCallbackExceptionProducesFailedReceipt(string failure)
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var producer = new FixedRecipeProducer(ProducerId, (_, _) => failure switch
        {
            "io" => ValueTask.FromException<EvidenceProducerResult>(new IOException("ordinary recipe failure")),
            "operation-cancelled" => ValueTask.FromException<EvidenceProducerResult>(new OperationCanceledException("recipe stopped itself")),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        });

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.False(receipt.CallerCancellationRequested);
        Assert.Equal("failed", receipt.Journeys.Single().Outcome);
        Assert.True(receipt.CleanupCompleted);
    }

    [Theory]
    [InlineData("wrong-producer")]
    [InlineData("duplicate-assertion")]
    [InlineData("unknown-outcome")]
    public async Task ExecuteAsync_MalformedResultProducesInvalidJourney(string defect)
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var producer = new FixedRecipeProducer(ProducerId, (_, _) => ValueTask.FromResult(defect switch
        {
            "wrong-producer" => new EvidenceProducerResult("unexpected-recipe", EvidenceProducerOutcome.Passed, [AssertionId]),
            "duplicate-assertion" => new EvidenceProducerResult(ProducerId, EvidenceProducerOutcome.Passed, [AssertionId, AssertionId]),
            "unknown-outcome" => new EvidenceProducerResult(ProducerId, (EvidenceProducerOutcome)int.MaxValue, []),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        }));

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.Equal("invalid", receipt.Journeys.Single().Outcome);
        Assert.Empty(receipt.Journeys.Single().SatisfiedAssertionIds);
    }

    [Theory]
    [InlineData("null-assertions")]
    [InlineData("unreported-artifact")]
    public async Task ExecuteAsync_ResultCannotOmitOrInventItsProducerEvidence(string defect)
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var producer = new FixedRecipeProducer(ProducerId, async (context, token) =>
        {
            if (defect == "unreported-artifact")
            {
                await WriteArtifactAsync(context, token);
            }

            return new EvidenceProducerResult(
                ProducerId,
                EvidenceProducerOutcome.Passed,
                defect == "null-assertions" ? null! : [AssertionId],
                Artifacts: defect == "unreported-artifact" ? [] : null);
        });

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.Equal("invalid", receipt.Journeys.Single().Outcome);
        Assert.Empty(receipt.Journeys.Single().SatisfiedAssertionIds);
    }

    [Fact]
    public async Task ExecuteAsync_BoundsLongProducerDiagnosticBeforePublishingReceipt()
    {
        using var directory = new TemporaryDirectory();
        var plan = ResolvePlan();
        var producer = new FixedRecipeProducer(ProducerId, (_, _) => ValueTask.FromResult(
            new EvidenceProducerResult(ProducerId, EvidenceProducerOutcome.Failed, [], new string('x', 3000))));

        var receipt = await ExecuteAsync(plan, producer, directory.Root);

        Assert.True(NativeValidationReceiptCodec.Verify(plan, receipt));
        Assert.Equal("failed", receipt.RunOutcome);
        Assert.Equal(NativeValidationReceiptCodec.MaximumDiagnosticCharacters, receipt.Journeys.Single().Diagnostic!.Length);
    }

    private static Task<NativeValidationReceipt> ExecuteAsync(
        EvidencePlan plan,
        FixedRecipeProducer producer,
        string artifactsDirectory,
        CancellationToken cancellationToken = default,
        TimeSpan? loweredJoinAllowance = null) =>
        NativeValidationExecution.ExecuteAsync(
            plan, [producer], artifactsDirectory, InputDigest, cancellationToken, loweredJoinAllowance);

    private static EvidencePlan ResolvePlan(int timeoutSeconds = 5)
    {
        var declaration = Declaration(timeoutSeconds);
        var profile = new EvidenceProfile(
            "ordinary-recipe",
            EvidenceProfileScope.Targeted,
            [],
            [declaration],
            [new EvidenceObligation("ordinary-obligation", "ordinary-recipe", "Ordinary recipe coverage.", [ProducerId], AssertionId)]);
        var policy = new EvidencePolicy(
            "ordinary-recipe-policy",
            "1",
            profile.Id,
            [profile],
            [new EvidencePolicyRule("ordinary-recipe-path", "Aspire/Recipe.cs", profile.Id)]);
        return new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("Aspire/Recipe.cs")]);
    }

    private static EvidencePlan ResolveEmptyRecipePlan()
    {
        var conservative = new EvidenceProfile(
            "ordinary-recipe",
            EvidenceProfileScope.Targeted,
            [],
            [Declaration()],
            [new EvidenceObligation("ordinary-obligation", "ordinary-recipe", "Ordinary recipe coverage.", [ProducerId], AssertionId)]);
        var empty = new EvidenceProfile("empty-target", EvidenceProfileScope.Targeted, [], [], []);
        var policy = new EvidencePolicy(
            "ordinary-recipe-policy",
            "1",
            conservative.Id,
            [conservative, empty],
            [new EvidencePolicyRule("empty-recipe-path", "Aspire/EmptyRecipe.cs", empty.Id)]);
        return new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("Aspire/EmptyRecipe.cs")]);
    }

    private static EvidencePlan ResolveTwoRecipePlan()
    {
        var laterAssertionId = "ordinary/later-assertion@1";
        var firstDeclaration = Declaration();
        var laterDeclaration = Declaration("later-recipe", laterAssertionId);
        var profile = new EvidenceProfile(
            "ordinary-recipe",
            EvidenceProfileScope.Targeted,
            [],
            [firstDeclaration, laterDeclaration],
            [
                new EvidenceObligation("ordinary-obligation", "ordinary-recipe", "Ordinary recipe coverage.", [ProducerId], AssertionId),
                new EvidenceObligation("later-obligation", "ordinary-recipe", "Later recipe coverage.", ["later-recipe"], laterAssertionId),
            ]);
        var policy = new EvidencePolicy(
            "ordinary-recipe-policy",
            "1",
            profile.Id,
            [profile],
            [new EvidencePolicyRule("ordinary-recipe-path", "Aspire/Recipe.cs", profile.Id)]);
        return new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("Aspire/Recipe.cs")]);
    }

    private static EvidenceProducerDeclaration Declaration(int timeoutSeconds = 5) =>
        Declaration(ProducerId, AssertionId, timeoutSeconds);

    private static EvidenceProducerDeclaration Declaration(string producerId, string assertionId, int timeoutSeconds = 5) => new(
        producerId,
        "ordinary-recipe",
        "1",
        [],
        [assertionId],
        [new EvidenceArtifactSlot("result", "proof", "application/json", true, 1024)],
        timeoutSeconds);

    private static async ValueTask<EvidenceArtifactResult> WriteArtifactAsync(
        EvidenceProducerContext context,
        CancellationToken cancellationToken) =>
        await context.Artifacts!.WriteAsync("result", "proof/result.json", "{\"passed\":true}"u8.ToArray(), cancellationToken);

    private static EvidenceProducerResult Passed(
        IReadOnlyList<string> assertions,
        IReadOnlyList<EvidenceArtifactResult>? artifacts) =>
        new(ProducerId, EvidenceProducerOutcome.Passed, assertions, Artifacts: artifacts);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
    }

    private sealed class FixedRecipeProducer(
        string id,
        Func<EvidenceProducerContext, CancellationToken, ValueTask<EvidenceProducerResult>> produce,
        Func<ValueTask>? dispose = null) : IEvidenceProducer, IAsyncDisposable
    {
        public string Id => id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            produce(context, cancellationToken);

        public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
    }

    private sealed class FixedSyncDisposableProducer(
        string id,
        Func<EvidenceProducerContext, CancellationToken, ValueTask<EvidenceProducerResult>> produce,
        Action dispose) : IEvidenceProducer, IDisposable
    {
        public string Id => id;

        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken) =>
            produce(context, cancellationToken);

        public void Dispose() => dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Directory.CreateTempSubdirectory("native-validation-execution-").FullName;
        }

        public string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
