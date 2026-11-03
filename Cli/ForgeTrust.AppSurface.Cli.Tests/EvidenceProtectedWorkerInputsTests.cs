using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceProtectedWorkerInputsTests
{
    [Theory]
    [InlineData("evidence-worker-linux-v2")]
    [InlineData("unknown-protocol-canary")]
    public void CreateContextRejectsUnsupportedOrIncompleteApplicationProtocolBeforeProducingFacts(string schema)
    {
        var policy = CreatePolicy();
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/Feature.cs")]);
        var descriptor = CreateDescriptor("/protected-tools", "/protected-tools/policy.json", new string('a', 64)) with
        {
            Schema = schema,
        };

        var rejected = Assert.Throws<EvidenceAdmissionException>(() =>
            EvidenceProtectedWorkerInputs.CreateContext(descriptor, policy, plan));

        Assert.Equal("ASEVD404", rejected.Code);
        Assert.Null(rejected.InnerException);
        Assert.DoesNotContain("canary", rejected.Message, StringComparison.Ordinal);
        Assert.False(EvidenceProtectedWorkerInputs.AcceptedConsumerProof(descriptor));
    }

    [Fact]
    public async Task ResolveAsync_BindsExactBoundedPolicyBytesAndReresolvesCanonicalPlan()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var policy = CreatePolicy();
        var canonicalBytes = EvidenceCanonicalJson.Serialize(policy);
        var exactBytes = Encoding.UTF8.GetBytes($" \n{Encoding.UTF8.GetString(canonicalBytes)}\n");
        var policyFile = directory.WriteBytes("tools/policy.json", exactBytes);
        var descriptor = CreateDescriptor(toolRoot, policyFile, EvidenceDigest.Sha256(exactBytes));

        Assert.NotEqual(EvidenceDigest.Sha256(canonicalBytes), descriptor.PolicySha256);

        var resolved = await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, CancellationToken.None);
        var expectedPlan = new EvidencePlanner().Resolve(policy,
            descriptor.Paths.Select(static path => new NormalizedDiffPath(path)).ToArray());

        Assert.Equal(EvidenceDigest.CanonicalSha256(policy), EvidenceDigest.CanonicalSha256(resolved.Policy));
        Assert.Equal(expectedPlan.PlanDigest, resolved.Plan.PlanDigest);
        Assert.Equal(EvidenceCanonicalJson.Serialize(expectedPlan), EvidenceCanonicalJson.Serialize(resolved.Plan));
        Assert.Null(resolved.DiffBytes);
    }

    [Fact]
    public async Task ResolveAsync_RejectsPolicyAboveBoundedInputLimit()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var policyFile = directory.FilePath("tools/oversized-policy.json");
        await using (var output = new FileStream(policyFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.SetLength(EvidenceCanonicalJson.MaximumInputBytes + 1L);
        }

        var descriptor = CreateDescriptor(toolRoot, policyFile, new string('a', 64));

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, CancellationToken.None));

        Assert.Contains("maximum input size", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_RejectsChangedPolicyWithoutEchoingPolicyCanary()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var originalBytes = EvidenceCanonicalJson.Serialize(CreatePolicy());
        const string canary = "protected-policy-canary-779";
        var changedBytes = EvidenceCanonicalJson.Serialize(CreatePolicy(canary));
        var policyFile = directory.WriteBytes("tools/policy.json", changedBytes);
        var descriptor = CreateDescriptor(toolRoot, policyFile, EvidenceDigest.Sha256(originalBytes));

        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, CancellationToken.None));

        Assert.Equal("ASEVD403", error.Code);
        Assert.DoesNotContain(canary, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_MissingPolicyFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var missingPolicy = directory.FilePath("tools/missing-policy.json");
        var descriptor = CreateDescriptor(toolRoot, missingPolicy, new string('a', 64));

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_ReturnsExactProtectedDiffBytesAndPlansTheirChangedPath()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var policyFile = WritePolicy(directory);
        var diffBytes = Encoding.UTF8.GetBytes(
            "diff --git a/src/DiffFeature.cs b/src/DiffFeature.cs\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/src/DiffFeature.cs\n" +
            "+++ b/src/DiffFeature.cs\n" +
            "@@ -1 +1 @@\n-old\n+new\n");
        var diffFile = directory.WriteBytes("tools/snapshots/change.patch", diffBytes);
        var descriptor = CreateDescriptor(toolRoot, policyFile, EvidenceDigest.Sha256(File.ReadAllBytes(policyFile))) with
        {
            DiffFile = diffFile,
            DiffSha256 = EvidenceDigest.Sha256(diffBytes),
        };

        var resolved = await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, CancellationToken.None);
        var expectedPaths = descriptor.Paths.Select(static path => new NormalizedDiffPath(path))
            .Concat(EvidenceUnifiedDiffReader.Read(Encoding.UTF8.GetString(diffBytes))).ToArray();
        var expectedPlan = new EvidencePlanner().Resolve(CreatePolicy(), expectedPaths);

        Assert.Equal(diffBytes, resolved.DiffBytes);
        Assert.Equal(expectedPlan.PlanDigest, resolved.Plan.PlanDigest);
        Assert.Contains(resolved.Plan.ChangedPaths, static path => path.Path == "src/DiffFeature.cs");
    }

    [Fact]
    public async Task ResolveAsync_RejectsChangedProtectedDiffWithoutEchoingDiffCanary()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var policyFile = WritePolicy(directory);
        var policyDigest = EvidenceDigest.Sha256(File.ReadAllBytes(policyFile));
        const string canary = "src/diff-canary-779.cs";
        var changedDiff = Encoding.UTF8.GetBytes($"diff --git a/{canary} b/{canary}\n--- a/{canary}\n+++ b/{canary}\n");
        var intendedDiff = Encoding.UTF8.GetBytes("diff --git a/src/Intended.cs b/src/Intended.cs\n");
        var diffFile = directory.WriteBytes("tools/snapshots/change.patch", changedDiff);
        var descriptor = CreateDescriptor(toolRoot, policyFile, policyDigest) with
        {
            DiffFile = diffFile,
            DiffSha256 = EvidenceDigest.Sha256(intendedDiff),
        };

        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, CancellationToken.None));

        Assert.Equal("ASEVD403", error.Code);
        Assert.DoesNotContain(canary, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_RejectsDiffOutsideProtectedToolingBeforeOpeningIt()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var policyFile = WritePolicy(directory);
        const string outsideCanary = "outside-tooling-canary-779.patch";
        var outsideDiff = directory.FilePath(outsideCanary);
        var descriptor = CreateDescriptor(toolRoot, policyFile, EvidenceDigest.Sha256(File.ReadAllBytes(policyFile))) with
        {
            DiffFile = outsideDiff,
            DiffSha256 = new string('b', 64),
        };

        Assert.False(File.Exists(outsideDiff));
        var error = await Assert.ThrowsAsync<EvidenceAdmissionException>(async () =>
            await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, CancellationToken.None));

        Assert.Equal("ASEVD403", error.Code);
        Assert.DoesNotContain(outsideCanary, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_WithoutDiffUsesOnlyDescriptorPaths()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var policyFile = WritePolicy(directory);
        var descriptor = CreateDescriptor(toolRoot, policyFile, EvidenceDigest.Sha256(File.ReadAllBytes(policyFile)),
            ["src/ExplicitPath.cs"]);

        var resolved = await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, CancellationToken.None);
        var expected = new EvidencePlanner().Resolve(CreatePolicy(), [new NormalizedDiffPath("src/ExplicitPath.cs")]);

        Assert.Null(resolved.DiffBytes);
        Assert.Equal(expected.PlanDigest, resolved.Plan.PlanDigest);
        Assert.Equal("src/ExplicitPath.cs", Assert.Single(resolved.Plan.ChangedPaths).Path);
    }

    [Fact]
    public async Task CreateContext_UsesSelectedProtectedCatalogueAndCoverageVersionOnly()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var policy = CreatePolicy();
        var policyFile = directory.WriteBytes("tools/policy.json", EvidenceCanonicalJson.Serialize(policy));
        var descriptor = CreateDescriptor(toolRoot, policyFile, EvidenceDigest.Sha256(File.ReadAllBytes(policyFile)));
        var resolved = await EvidenceProtectedWorkerInputs.ResolveAsync(descriptor, CancellationToken.None);

        var context = EvidenceProtectedWorkerInputs.CreateContext(descriptor, resolved.Policy, resolved.Plan);
        var expectedCatalogueDigest = EvidenceDigest.CanonicalSha256(new
        {
            Producers = resolved.Plan.Profile.Producers,
            Resources = resolved.Plan.Profile.Resources,
        });

        Assert.Equal("coverage", resolved.Plan.Profile.Id);
        Assert.Equal(2, resolved.Policy.Profiles.Count);
        Assert.Equal(resolved.Plan.Profile.Producers, context.Producers);
        Assert.Equal(resolved.Plan.Profile.Resources, context.Resources);
        Assert.Equal(expectedCatalogueDigest, context.ExpectedAssertion?.CatalogueDigest);
        Assert.Equal(new[] { new EvidenceObservationProducerClass("coverage", "1.0.0") },
            context.ObservationProducerClasses);
        Assert.False(context.ConsumerAcceptanceMatches);
    }

    [Fact]
    public void AcceptedConsumerProof_RemainsFalseForValidlyShapedDescriptor()
    {
        using var directory = new TemporaryDirectory();
        var toolRoot = directory.CreateDirectory("tools");
        var policyFile = directory.WriteBytes("tools/policy.json", EvidenceCanonicalJson.Serialize(CreatePolicy()));
        var descriptor = CreateDescriptor(toolRoot, policyFile, EvidenceDigest.Sha256(File.ReadAllBytes(policyFile)));

        Assert.Equal("evidence-worker-linux-v1", descriptor.Schema);
        Assert.Equal("github-actions", descriptor.Provider);
        Assert.Equal("linux-x64", descriptor.Platform);
        Assert.NotEqual(0u, descriptor.WorkerUid);
        Assert.NotEqual(descriptor.WorkerUid, descriptor.SubjectUid);
        Assert.True(descriptor.JobDeadlineUtc > DateTimeOffset.UtcNow);
        Assert.Equal(64, descriptor.ProofDigest.Length);
        Assert.False(EvidenceProtectedWorkerInputs.AcceptedConsumerProof(descriptor));
    }

    private static EvidencePolicy CreatePolicy(string id = "protected-policy")
    {
        var coverage = new EvidenceProfile("coverage", EvidenceProfileScope.Targeted, [],
            [new EvidenceProducerDeclaration("coverage", "coverage", "1.0.0", [],
                ["appsurface/coverage/behavioral-patch@1"], [], 60)],
            [new EvidenceObligation("behavior", "behavior", "Changed behavior needs evidence.",
                ["coverage"], "appsurface/coverage/behavioral-patch@1")]);
        var noEvidence = new EvidenceProfile("no-evidence", EvidenceProfileScope.Targeted, [], [], []);
        return new EvidencePolicy(id, "1", "coverage", [coverage, noEvidence], []);
    }

    private static string WritePolicy(TemporaryDirectory directory)
    {
        return directory.WriteBytes("tools/policy.json", EvidenceCanonicalJson.Serialize(CreatePolicy()));
    }

    private static EvidenceLinuxWorkerDescriptor CreateDescriptor(
        string toolRoot,
        string policyFile,
        string policySha256,
        string[]? paths = null)
    {
        var workspace = Directory.GetParent(toolRoot)!.FullName;
        var subjectRoot = Path.Combine(workspace, "subject");
        return new EvidenceLinuxWorkerDescriptor(
            Schema: "evidence-worker-linux-v1",
            RunId: "run/attempt-1",
            WorkerPid: Environment.ProcessId,
            WorkerUid: 1001,
            WorkerGid: 1001,
            SubjectUid: 1002,
            SubjectGid: 1002,
            Unit: "evidence-worker.service",
            Cgroup: "/system.slice/evidence-worker.service",
            JobDeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5),
            ToolRoot: toolRoot,
            SubjectRoot: subjectRoot,
            OutputParent: Path.Combine(workspace, "output"),
            OutputSlot: "run-slot",
            DotnetPath: "/usr/bin/dotnet",
            TestOutputRoot: Path.Combine(subjectRoot, "test-results"),
            PolicyFile: policyFile,
            Mode: "observation",
            SocketPath: Path.Combine(workspace, "worker.sock"),
            EntrySha256: new string('a', 64),
            BaseRevision: new string('b', 40),
            SubjectRevision: new string('c', 40),
            WorkflowIdentity: "workflow:protected-evidence",
            Provider: "github-actions",
            Platform: "linux-x64",
            ProofDigest: new string('d', 64),
            PolicySha256: policySha256,
            OutputParentIdentity: new EvidenceLinuxArtifactIdentity(8, 1, 42, 1001, 1001),
            ObservationProfileIds: ["coverage"],
            ObservationProducerIds: ["coverage"],
            Paths: paths ?? ["src/Feature.cs"],
            AdmissionSeconds: 10,
            StartSeconds: 10,
            CollectionSeconds: 10,
            CleanupSeconds: 20,
            StoppingSeconds: 5);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "evidence-protected-inputs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateDirectory(string relativePath)
        {
            var path = FilePath(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string FilePath(string relativePath) => TestPathUtils.PathUnder(Root, relativePath);

        public string WriteBytes(string relativePath, byte[] contents)
        {
            var path = FilePath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
