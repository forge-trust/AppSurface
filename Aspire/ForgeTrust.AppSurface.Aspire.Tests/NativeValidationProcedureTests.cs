using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Validation;
using Microsoft.Extensions.Time.Testing;

namespace ForgeTrust.AppSurface.Aspire.Tests;

public sealed class NativeValidationProcedureTests
{
    [Fact]
    public async Task RunAsync_ReturnsVerifiedProcedureReportWithFixedNonClaimFields()
    {
        using var directory = TestDirectory.Create();
        var producer = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            await context.Artifacts!.WriteAsync("result", "proof/result.json", "procedure-result"u8.ToArray(), token);
            return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"]);
        });
        var plan = Plan(producer);

        var report = await NativeValidationProcedure.RunAsync(plan, [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.Equal("appsurface-native-validation-procedure-v1", report.Schema);
        Assert.Equal(plan.PlanDigest, report.PlanDigest);
        Assert.True(report.ProcedurePassed);
        Assert.True(report.CleanupCompleted);
        Assert.False(report.ProtectedAdmission);
        Assert.False(report.NativeAcceptance);
        Assert.False(report.GateEligible);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.False(json.RootElement.GetProperty("protectedAdmission").GetBoolean());
        Assert.False(json.RootElement.GetProperty("nativeAcceptance").GetBoolean());
        Assert.False(json.RootElement.GetProperty("gateEligible").GetBoolean());
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Passed, result.Outcome);
        Assert.Equal(new[] { "storage/result@1" }, result.SatisfiedAssertionIds);
        var artifact = Assert.Single(result.Artifacts!);
        Assert.Equal("result", artifact.LogicalName);
        Assert.Equal("application/json", artifact.MediaType);
        Assert.Equal("procedure-result"u8.Length, artifact.LengthBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("procedure-result"u8)).ToLowerInvariant(), artifact.Sha256);
        Assert.True(File.Exists(Path.Join(directory.Path, "output/artifacts/packed-storage/proof/result.json")));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("assertion")]
    [InlineData("duplicate-assertion")]
    [InlineData("missing-artifact")]
    public async Task RunAsync_RejectsProducerResultContractViolations(string mutation)
    {
        using var directory = TestDirectory.Create();
        var producer = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            if (mutation != "missing-artifact")
                await context.Artifacts!.WriteAsync("result", "proof/result.json", "ok"u8.ToArray(), token);
            return mutation switch
            {
                "id" => new EvidenceProducerResult("unknown-producer", EvidenceProducerOutcome.Passed, ["storage/result@1"]),
                "assertion" => new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["undeclared/assertion@1"]),
                "duplicate-assertion" => new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1", "storage/result@1"]),
                _ => new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"]),
            };
        });

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        Assert.False(report.GateEligible);
        Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single(report.ProducerResults).Outcome);
        Assert.Empty(Assert.Single(report.ProducerResults).SatisfiedAssertionIds);
    }

    [Fact]
    public async Task RunAsync_RejectsDuplicateArtifactWritesAndDisposesProducerOnce()
    {
        using var directory = TestDirectory.Create();
        var producer = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            await context.Artifacts!.WriteAsync("result", "proof/result.json", "one"u8.ToArray(), token);
            await context.Artifacts.WriteAsync("result", "proof/other.json", "two"u8.ToArray(), token);
            return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"]);
        });

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        Assert.True(report.CleanupCompleted);
        Assert.Equal(1, producer.DisposeCount);
        Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single(report.ProducerResults).Outcome);
    }

    [Fact]
    public async Task RunAsync_ContainsProducerExceptionAfterArtifactWriteAndDisposesAllProducers()
    {
        using var directory = TestDirectory.Create();
        var throwing = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            await context.Artifacts!.WriteAsync("result", "proof/result.json", "partial"u8.ToArray(), token);
            throw new InvalidOperationException("native validation private exception text");
        });
        var following = Producer("installed-consumer", "consumer/result@1", 1800, async (context, token) =>
        {
            await context.Artifacts!.WriteAsync("result", "proof/result.json", "still-runs"u8.ToArray(), token);
            return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["consumer/result@1"]);
        });

        var report = await NativeValidationProcedure.RunAsync(Plan(throwing, following), [throwing, following], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        Assert.Equal(1, throwing.DisposeCount);
        Assert.Equal(1, following.DisposeCount);
        Assert.Equal(1, following.RunCount);
        var results = report.ProducerResults.ToDictionary(static result => result.ProducerId, StringComparer.Ordinal);
        Assert.Equal(EvidenceProducerOutcome.Failed, results[throwing.Id].Outcome);
        Assert.StartsWith("Procedure failed (invalid-operation; HRESULT 0x", results[throwing.Id].Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("native validation private exception text", results[throwing.Id].Diagnostic, StringComparison.Ordinal);
        Assert.Empty(results[throwing.Id].SatisfiedAssertionIds);
        Assert.Equal(EvidenceProducerOutcome.Passed, results[following.Id].Outcome);
    }

    [Fact]
    public async Task RunAsync_ClassifiesDeadlineCancellationFromOriginalProducerTaskAsFailure()
    {
        using var directory = TestDirectory.Create();
        var clock = new FakeTimeProvider();
        var producer = Producer("packed-storage", "storage/result@1", 600, async (_, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(600));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new EvidenceProducerResult("packed-storage", EvidenceProducerOutcome.Passed, ["storage/result@1"]);
        });

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None, clock);

        Assert.False(report.ProcedurePassed);
        Assert.True(report.CleanupCompleted);
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Equal("Producer deadline expired.", result.Diagnostic);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_DisposableOnlyProducerCleanupFailurePreventsPass()
    {
        using var directory = TestDirectory.Create();
        var producer = new SyncDisposableProducer(Declaration("packed-storage", "storage/result@1", 600),
            async (context, token) =>
            {
                await context.Artifacts!.WriteAsync("result", "proof/result.json", "complete"u8.ToArray(), token);
                return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"]);
            }, throwOnDispose: true);

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        Assert.False(report.CleanupCompleted);
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.StartsWith("Producer cleanup failed (io; HRESULT 0x", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    public async Task RunAsync_RejectsExistingOutputWithoutChangingIt(string kind)
    {
        using var directory = TestDirectory.Create();
        var output = Path.Join(directory.Path, "existing");
        string? sentinel = null;
        if (kind == "directory")
        {
            Directory.CreateDirectory(output);
            sentinel = Path.Join(output, "caller.txt");
            await File.WriteAllTextAsync(sentinel, "preserve");
        }
        else
        {
            await File.WriteAllTextAsync(output, "preserve");
        }
        var producer = Producer("packed-storage", "storage/result@1", 600,
            (_, _) => throw new InvalidOperationException("must not start"));

        await Assert.ThrowsAsync<ArgumentException>(() => NativeValidationProcedure.RunAsync(Plan(producer), [producer], output, CancellationToken.None));

        Assert.Equal(0, producer.RunCount);
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel ?? output));
    }

    [Theory]
    [InlineData("unknown-slot")]
    [InlineData("duplicate-logical")]
    [InlineData("duplicate-path")]
    [InlineData("length-cap")]
    [InlineData("bad-digest")]
    [InlineData("invalid-path")]
    [InlineData("wrong-media")]
    [InlineData("null-artifact")]
    [InlineData("required-slot-omitted")]
    public async Task RunAsync_RejectsInvalidReturnedArtifactMetadata(string mutation)
    {
        using var directory = TestDirectory.Create();
        var slots = mutation is "duplicate-path"
            ? new[]
            {
                new EvidenceArtifactSlot("result", "proof", "application/json", true, 1024),
                new EvidenceArtifactSlot("second", "proof", "application/json", true, 1024),
            }
            : new[] { new EvidenceArtifactSlot("result", "proof", "application/json", true, 1024) };
        var producer = new FakeProducer(Declaration("packed-storage", "storage/result@1", 600, slots), async (context, token) =>
        {
            if (mutation == "required-slot-omitted")
                return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"], Artifacts: []);

            var written = await context.Artifacts!.WriteAsync("result", "proof/result.json", "artifact"u8.ToArray(), token);
            IReadOnlyList<EvidenceArtifactResult> reported = [written];
            switch (mutation)
            {
                case "unknown-slot": reported = [written with { LogicalName = "unknown" }]; break;
                case "duplicate-logical": reported = [written, written]; break;
                case "duplicate-path":
                    var second = await context.Artifacts.WriteAsync("second", "proof/second.json", "second"u8.ToArray(), token);
                    reported = [written, second with { RelativePath = written.RelativePath }];
                    break;
                case "length-cap": reported = [written with { LengthBytes = 1025 }]; break;
                case "bad-digest": reported = [written with { Sha256 = "not-a-sha256" }]; break;
                case "invalid-path": reported = [written with { RelativePath = "../escape.json" }]; break;
                case "wrong-media": reported = [written with { MediaType = "text/plain" }]; break;
                case "null-artifact": reported = [null!]; break;
            }
            return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed,
                ["storage/result@1"], Artifacts: reported);
        }, dispose: null);

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
    }

    [Fact]
    public async Task RunAsync_VerifiesRequiredArtifactBytesAfterProducerDisposal()
    {
        using var directory = TestDirectory.Create();
        var artifactPath = Path.Join(directory.Path, "output/artifacts/packed-storage/proof/result.json");
        var producer = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            await context.Artifacts!.WriteAsync("result", "proof/result.json", "before"u8.ToArray(), token);
            return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"]);
        }, dispose: () => File.WriteAllTextAsync(artifactPath, "changed"));

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        Assert.True(report.CleanupCompleted);
        Assert.Contains("Artifact verification", Assert.Single(report.ProducerResults).Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_RejectsLateSuccessWhenProducerReturnsAtItsDeadline()
    {
        using var directory = TestDirectory.Create();
        var clock = new FakeTimeProvider();
        var producer = Producer("packed-storage", "storage/result@1", 600, (context, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(600));
            return ValueTask.FromResult(new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"]));
        });

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None, clock);

        Assert.False(report.ProcedurePassed);
        Assert.Equal(EvidenceProducerOutcome.Failed, Assert.Single(report.ProducerResults).Outcome);
        Assert.Empty(Assert.Single(report.ProducerResults).SatisfiedAssertionIds);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_JoinsOriginalProducerAfterDeadlineBeforeReportingOrDisposal()
    {
        using var directory = TestDirectory.Create();
        var clock = new FakeTimeProvider();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadlineObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            await context.Artifacts!.WriteAsync("result", "proof/result.json", "completed after deadline"u8.ToArray(), token);
            using var registration = token.Register(() => deadlineObserved.TrySetResult(true));
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false); // Deliberately ignore the cooperative token until explicitly released.
            return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"]);
        });
        var run = NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None, clock);

        await entered.Task;
        clock.Advance(TimeSpan.FromSeconds(600));
        await deadlineObserved.Task;

        Assert.False(run.IsCompleted);
        Assert.Equal(0, producer.DisposeCount);
        release.TrySetResult(true);
        var report = await run;

        Assert.False(report.ProcedurePassed);
        Assert.True(report.CleanupCompleted);
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal("Producer deadline expired.", result.Diagnostic);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_DisposesEveryProducerInReverseOrderExactlyOnce()
    {
        using var directory = TestDirectory.Create();
        var disposed = new List<string>();
        FakeProducer Create(string id, string assertion) => Producer(id, assertion, id == "packed-storage" ? 600 : 1800,
            async (context, token) =>
            {
                await context.Artifacts!.WriteAsync("result", "proof/result.json", "ok"u8.ToArray(), token);
                return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, [assertion]);
            },
            dispose: () => { disposed.Add(id); return Task.CompletedTask; });
        var first = Create("packed-storage", "storage/result@1");
        var second = Create("installed-consumer", "consumer/result@1");
        var plan = Plan(first, second);

        var report = await NativeValidationProcedure.RunAsync(plan, [first, second], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.True(report.ProcedurePassed);
        Assert.Equal(new[] { "installed-consumer", "packed-storage" }, disposed);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_ExecutesInPlanDeclarationOrderWhenProducerInputOrderDiffers()
    {
        using var directory = TestDirectory.Create();
        var executionOrder = new List<string>();
        FakeProducer Create(string id, string assertion, int timeout) => Producer(id, assertion, timeout, async (context, token) =>
        {
            executionOrder.Add(id);
            await context.Artifacts!.WriteAsync("result", "proof/result.json", Encoding.UTF8.GetBytes(id), token);
            return new EvidenceProducerResult(id, EvidenceProducerOutcome.Passed, [assertion]);
        });
        var storage = Create("packed-storage", "storage/result@1", 600);
        var consumer = Create("installed-consumer", "consumer/result@1", 1800);
        var plan = Plan(consumer, storage);

        var report = await NativeValidationProcedure.RunAsync(plan, [storage, consumer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.True(report.ProcedurePassed);
        Assert.Equal(new[] { "installed-consumer", "packed-storage" }, executionOrder);
        Assert.Equal(new[] { "installed-consumer", "packed-storage" }, report.ProducerResults.Select(static item => item.ProducerId));
    }

    [Fact]
    public async Task RunAsync_RejectsMismatchedReportedArtifactMetadata()
    {
        using var directory = TestDirectory.Create();
        var producer = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            var artifact = await context.Artifacts!.WriteAsync("result", "proof/result.json", "real-bytes"u8.ToArray(), token);
            return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"], Artifacts: [artifact with { Sha256 = new string('0', 64) }]);
        });

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Contains("result or declared artifacts", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_BoundsFailedProducerDiagnosticsAndUsesFixedFallback(bool longDiagnostic)
    {
        using var directory = TestDirectory.Create();
        var diagnostic = longDiagnostic ? new string('x', 2000) : null;
        var producer = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            await context.Artifacts!.WriteAsync("result", "proof/result.json", "failure"u8.ToArray(), token);
            return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Failed, [], diagnostic);
        });

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal(longDiagnostic ? 1024 : "Producer procedure failed.".Length, result.Diagnostic!.Length);
        Assert.Equal(longDiagnostic ? new string('x', 1024) : "Producer procedure failed.", result.Diagnostic);
    }

    [Theory]
    [InlineData("empty-plan")]
    [InlineData("resources")]
    [InlineData("wrong-deadline")]
    public async Task RunAsync_RejectsInvalidPlanOrDeadlineBeforeCreatingOutput(string change)
    {
        using var directory = TestDirectory.Create();
        var producer = Producer("packed-storage", "storage/result@1", change == "wrong-deadline" ? 599 : 600,
            (_, _) => throw new InvalidOperationException("must not start"));
        var plan = change switch
        {
            "empty-plan" => Plan(),
            "resources" => Plan(producer) with
            {
                Profile = Plan(producer).Profile with { Resources = [new EvidenceResourceDeclaration("db", "completion", 30, [])] },
            },
            _ => Plan(producer),
        };
        var output = Path.Join(directory.Path, "output");

        await Assert.ThrowsAsync<ArgumentException>(() => NativeValidationProcedure.RunAsync(plan, [producer], output, CancellationToken.None));

        Assert.False(Directory.Exists(output));
        Assert.Equal(0, producer.RunCount);
    }

    [Fact]
    public async Task RunAsync_CleanupFailurePreventsProcedurePass()
    {
        using var directory = TestDirectory.Create();
        var producer = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            await context.Artifacts!.WriteAsync("result", "proof/result.json", "ok"u8.ToArray(), token);
            return new EvidenceProducerResult(context.Producer.Id, EvidenceProducerOutcome.Passed, ["storage/result@1"]);
        }, dispose: () => throw new IOException("private cleanup detail"));

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        Assert.False(report.CleanupCompleted);
        Assert.DoesNotContain("private cleanup detail", Assert.Single(report.ProducerResults).Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_PreservesPrimaryAndCleanupFailureCategoriesWithoutPrivateDetails()
    {
        using var directory = TestDirectory.Create();
        var producer = Producer("packed-storage", "storage/result@1", 600, async (context, token) =>
        {
            await context.Artifacts!.WriteAsync("result", "proof/result.json", "partial"u8.ToArray(), token);
            throw new InvalidOperationException("private primary failure; type name must not escape");
        }, dispose: () => throw new InjectedIOException("private cleanup detail", unchecked((int)0x80070020)));

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        Assert.False(report.CleanupCompleted);
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Contains("Producer cleanup failed (io; HRESULT 0x80070020).", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("Prior failure: Procedure failed (invalid-operation; HRESULT 0x", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private primary failure", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private cleanup detail", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InjectedIOException), result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_BoundsCombinedPrimaryAndCleanupDiagnosticKeepingCleanupPrefix()
    {
        using var directory = TestDirectory.Create();
        var producer = Producer("packed-storage", "storage/result@1", 600, (_, _) =>
            ValueTask.FromResult(new EvidenceProducerResult("packed-storage", EvidenceProducerOutcome.Failed, [], new string('x', 3000))),
            dispose: () => throw new InjectedIOException("private cleanup detail", unchecked((int)0x80070020)));

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        Assert.False(report.CleanupCompleted);
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal(1024, result.Diagnostic!.Length);
        Assert.StartsWith("Producer cleanup failed (io; HRESULT 0x80070020). Prior failure:", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InjectedIOException), result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(1, producer.DisposeCount);
    }

    [Theory]
    [InlineData("io", "io")]
    [InlineData("access", "access-denied")]
    [InlineData("argument", "argument")]
    [InlineData("other", "other")]
    public async Task RunAsync_ProjectsOnlySafeFailureCategoryAndHResult(string kind, string category)
    {
        using var directory = TestDirectory.Create();
        Exception failure = kind switch
        {
            "io" => new IOException("diagnostic-canary-message"),
            "access" => new UnauthorizedAccessException("diagnostic-canary-message"),
            "argument" => new ArgumentException("diagnostic-canary-message"),
            _ => new CanaryExceptionType("diagnostic-canary-message"),
        };
        var producer = Producer("packed-storage", "storage/result@1", 600,
            (_, _) => ValueTask.FromException<EvidenceProducerResult>(failure));

        var report = await NativeValidationProcedure.RunAsync(Plan(producer), [producer], Path.Join(directory.Path, "output"), CancellationToken.None);

        Assert.False(report.ProcedurePassed);
        Assert.True(report.CleanupCompleted);
        var result = Assert.Single(report.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Failed, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal($"Procedure failed ({category}; HRESULT 0x{failure.HResult:X8}).", result.Diagnostic);
        Assert.DoesNotContain("diagnostic-canary-message", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(CanaryExceptionType), result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PropagatesCallerCancellationAfterJoiningAndDisposing()
    {
        using var directory = TestDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var first = Producer("packed-storage", "storage/result@1", 600, async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new EvidenceProducerResult("packed-storage", EvidenceProducerOutcome.Passed, ["storage/result@1"]);
        });
        var unstarted = Producer("installed-consumer", "consumer/result@1", 1800, (_, _) =>
            throw new InvalidOperationException("unstarted producer was invoked"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeValidationProcedure.RunAsync(
            Plan(first, unstarted), [first, unstarted], Path.Join(directory.Path, "output"), cancellation.Token));

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, unstarted.RunCount);
        Assert.Equal(1, unstarted.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_PreCancelledCallerTokenDisposesUnstartedProducersWithoutCreatingOutput()
    {
        using var directory = TestDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var producer = Producer("packed-storage", "storage/result@1", 600, (_, _) =>
            throw new InvalidOperationException("cancelled producer must not start"));
        var output = Path.Join(directory.Path, "output");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeValidationProcedure.RunAsync(
            Plan(producer), [producer], output, cancellation.Token));

        Assert.False(Directory.Exists(output));
        Assert.Equal(0, producer.RunCount);
        Assert.Equal(1, producer.DisposeCount);
    }

    private static EvidencePlan Plan(params IDeclaredProducer[] producers)
    {
        var profile = new EvidenceProfile("native-procedure", EvidenceProfileScope.Targeted, [], producers.Select(static producer => producer.Declaration).ToArray(), []);
        return new EvidencePlan("1.0", "policy", "policy-digest", "diff-digest", profile, [], [], "plan-digest");
    }

    private static FakeProducer Producer(string id, string assertion, int timeout,
        Func<EvidenceProducerContext, CancellationToken, ValueTask<EvidenceProducerResult>> run, Func<Task>? dispose = null)
    {
        var declaration = Declaration(id, assertion, timeout);
        return new FakeProducer(declaration, run, dispose);
    }

    private static EvidenceProducerDeclaration Declaration(string id, string assertion, int timeout,
        IReadOnlyList<EvidenceArtifactSlot>? slots = null) =>
        new(id, "native-validation", "1.0.0", [], [assertion],
            slots ?? [new EvidenceArtifactSlot("result", "proof", "application/json", true, 1024)], timeout);

    private interface IDeclaredProducer : IEvidenceProducer
    {
        EvidenceProducerDeclaration Declaration { get; }
    }

    private sealed class FakeProducer(EvidenceProducerDeclaration declaration,
        Func<EvidenceProducerContext, CancellationToken, ValueTask<EvidenceProducerResult>> run,
        Func<Task>? dispose) : IDeclaredProducer, IAsyncDisposable
    {
        public EvidenceProducerDeclaration Declaration { get; } = declaration;
        public string Id => Declaration.Id;
        public int RunCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            return run(context, cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (dispose is not null) await dispose();
        }
    }

    private sealed class SyncDisposableProducer(EvidenceProducerDeclaration declaration,
        Func<EvidenceProducerContext, CancellationToken, ValueTask<EvidenceProducerResult>> run,
        bool throwOnDispose) : IDeclaredProducer, IDisposable
    {
        public EvidenceProducerDeclaration Declaration { get; } = declaration;
        public string Id => Declaration.Id;
        public int RunCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ValueTask<EvidenceProducerResult> ProduceAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            RunCount++;
            return run(context, cancellationToken);
        }
        public void Dispose()
        {
            DisposeCount++;
            if (throwOnDispose) throw new IOException("private synchronous cleanup detail");
        }
    }

    private sealed class InjectedIOException : IOException
    {
        public InjectedIOException(string message, int hresult) : base(message)
        {
            HResult = hresult;
        }
    }

    private sealed class CanaryExceptionType(string message) : Exception(message)
    {
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("native-validation-procedure-").FullName;
        internal string Path => _path;
        internal static TestDirectory Create() => new();
        public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true); }
    }
}
