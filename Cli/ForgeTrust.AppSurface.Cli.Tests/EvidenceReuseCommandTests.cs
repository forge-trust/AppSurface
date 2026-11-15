using System.Text.Json;
using CliFx;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Coverage;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceReuseCommandTests
{
    [Fact]
    public void FreshRootCreatesExclusivePrivateChildAndNeverReusesIt()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"evidence-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        try
        {
            var first = EvidenceReuseOutput.CreateFreshRoot(null, parent);
            var second = EvidenceReuseOutput.CreateFreshRoot(null, parent);

            Assert.NotEqual(first, second);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
            Assert.Equal(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
            Assert.Equal("runs", Path.GetFileName(Path.GetDirectoryName(first)));
            Assert.Equal(Path.GetFileName(parent), Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(first))));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void ReuseProducerRootsSeparateExecutionReportsFromCustodyForEveryProducer()
    {
        var invocationRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "evidence-run"));
        var first = EvidenceReuseExecutionResolver.GetProducerRoots(invocationRoot, "coverage/first");
        var second = EvidenceReuseExecutionResolver.GetProducerRoots(invocationRoot, "coverage/second");

        Assert.NotEqual(first.Execution, first.Artifacts);
        Assert.NotEqual(second.Execution, second.Artifacts);
        Assert.NotEqual(first.Execution, second.Execution);
        Assert.NotEqual(first.Artifacts, second.Artifacts);
        Assert.Equal(Path.Join(invocationRoot, "execution"), Path.GetDirectoryName(first.Execution));
        Assert.Equal(Path.Join(invocationRoot, "producers"), Path.GetDirectoryName(first.Artifacts));
        Assert.DoesNotContain("coverage/first", first.Execution, StringComparison.Ordinal);
        Assert.DoesNotContain("coverage/first", first.Artifacts, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleProducerKeepsEstablishedReportsAtTheInvocationRoot()
    {
        var invocationRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "evidence-single-run"));

        var roots = EvidenceReuseExecutionResolver.GetProducerRoots(invocationRoot, "coverage", useInvocationArtifactRoot: true);

        Assert.Equal(invocationRoot, roots.Artifacts);
        Assert.NotEqual(invocationRoot, roots.Execution);
        Assert.Equal(Path.Join(invocationRoot, "coverage", "coverage-gate.json"),
            Path.Join(roots.Artifacts, "coverage", "coverage-gate.json"));
    }

    [Fact]
    public void ExplicitFreshRootMustNotAlreadyExist()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"evidence-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        var existing = Path.Combine(parent, "existing");
        Directory.CreateDirectory(existing);
        try
        {
            Assert.Throws<IOException>(() => EvidenceReuseOutput.CreateFreshRoot(existing, parent));
            Assert.True(Directory.Exists(existing));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void ExplicitFreshRootCreatesRequestedAbsoluteDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"evidence-output-{Guid.NewGuid():N}");
        var requested = Path.Combine(parent, "fresh");
        try
        {
            var actual = EvidenceReuseOutput.CreateFreshRoot(requested, parent);
            Assert.Equal("fresh", Path.GetFileName(actual));
            Assert.Equal("fresh", Path.GetFileName(requested));
            Assert.True(Directory.Exists(actual));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void VerifyReuseWithoutReuseFailsFast()
    {
        var exception = Assert.Throws<CommandException>(() => EvidenceRunCommand.ValidateReuseOptions(false, true, "00:30:00"));

        Assert.Contains("reuse.verify_requires_reuse", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EvidenceProfileScope.Release, null)]
    [InlineData(EvidenceProfileScope.Targeted, "true")]
    [InlineData(EvidenceProfileScope.Targeted, "TRUE")]
    public void ProtectedReuseContextRejectsBeforeAnyExecution(EvidenceProfileScope scope, string? githubActions)
    {
        var plan = new EvidencePlan("1.0", "policy", "", "", new("profile", scope, [], [], []), [], [], "");

        var failure = Assert.Throws<CommandException>(() => EvidenceRunCommand.ValidateReuseContext(true, plan,
            name => name == "GITHUB_ACTIONS" ? githubActions : null));

        Assert.Contains("reuse.gate_context_forbidden", failure.Message, StringComparison.Ordinal);
        Assert.Contains("No producer or resource was started", failure.Message, StringComparison.Ordinal);
        EvidenceRunCommand.ValidateReuseContext(false, plan, _ => "true");
    }

    [Fact]
    public void LocalTargetedReuseRemainsAllowed()
    {
        var plan = new EvidencePlan("1.0", "policy", "", "", new("profile", EvidenceProfileScope.Targeted, [], [], []), [], [], "");

        EvidenceRunCommand.ValidateReuseContext(true, plan, _ => null);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-00:00:01")]
    [InlineData("Infinite")]
    [InlineData("not-a-duration")]
    public void InvalidReceiptWaitTimeoutFailsFast(string timeout)
    {
        var exception = Assert.Throws<CommandException>(() => EvidenceRunCommand.ValidateReuseOptions(true, false, timeout));

        Assert.Contains("reuse.invalid_wait_timeout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyCommandAcceptsReusedIntegrityButRejectsGateAdmission()
    {
        var root = Path.Combine(Path.GetTempPath(), $"evidence-verify-reuse-{Guid.NewGuid():N}");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(root);
        try
        {
            var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
            var starter = await workflow.InitializeAsync(Path.Combine(root, "starter"), false, CancellationToken.None);
            var plan = await workflow.ExplainAsync(
                new EvidencePlanningRequest(Path.Combine(starter.RootPath, "evidence.policy.json"), ["src/Feature.cs"], null),
                CancellationToken.None);
            var policy = Assert.IsType<EvidencePolicy>(plan.PolicySnapshot);
            var profile = policy.Profiles.Single(item => item.Id == plan.Profile.Id);
            var producer = Assert.Single(profile.Producers);
            var reusableProducer = producer with { Inputs = new EvidenceProducerInputs(["src/**"], []) };
            var reusableProfile = profile with { Producers = [reusableProducer] };
            var reusablePolicy = policy with
            {
                Profiles = policy.Profiles.Select(item => item.Id == reusableProfile.Id ? reusableProfile : item).ToArray(),
            };
            plan = new EvidencePlanner().Resolve(reusablePolicy, plan.ChangedPaths);
            producer = Assert.Single(plan.Profile.Producers);
            var reused = new EvidenceProducerResult(producer.Id, EvidenceProducerOutcome.Reused,
                producer.AssertionIds, Artifacts: [])
            {
                ProducerKey = new string('c', 64),
                OriginManifestDigest = new string('a', 64),
                OriginCompletedAt = DateTimeOffset.UtcNow,
            };
            var manifest = EvidenceManifestBuilder.Build(plan, [reused]);
            Assert.Equal("1.1", plan.ContractVersion);
            Assert.Equal(EvidenceClaimKind.LocalReusedComplete, manifest.ClaimKind);
            Assert.True(EvidenceManifestBuilder.Verify(plan, manifest));
            await workflow.WritePlanAsync(plan, output, CancellationToken.None);
            await workflow.WriteManifestAsync(manifest, output, CancellationToken.None);

            const string informationalNextAction = "Local reused evidence is complete for this invocation, but it is informational and cannot satisfy a gate. Run fresh evidence for gate admission.";
            Assert.Contains(informationalNextAction, EvidenceCliWorkflow.FormatSummary(manifest), StringComparison.Ordinal);
            using (var summary = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "evidence-summary.json"))))
                Assert.Equal(informationalNextAction, summary.RootElement.GetProperty("next").GetString());

            using var ordinaryConsole = new FakeInMemoryConsole();
            await new EvidenceVerifyCommand(workflow)
            {
                ManifestPath = Path.Combine(output, "evidence-manifest.json"),
            }.ExecuteAsync(ordinaryConsole);
            Assert.Contains("manifest verified", ordinaryConsole.ReadOutputString(), StringComparison.OrdinalIgnoreCase);

            using var gateConsole = new FakeInMemoryConsole();
            var exception = await Assert.ThrowsAsync<CommandException>(async () =>
                await new EvidenceVerifyCommand(workflow)
                {
                    ManifestPath = Path.Combine(output, "evidence-manifest.json"),
                    ForGate = true,
                }.ExecuteAsync(gateConsole));
            Assert.Contains("gate-admission requirements", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
