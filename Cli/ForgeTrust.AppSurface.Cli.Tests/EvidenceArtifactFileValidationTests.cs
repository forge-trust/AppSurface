using System.Security.Cryptography;
using System.Text;
using ForgeTrust.AppSurface.Evidence.Cli;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.Cli.Tests;

public sealed class EvidenceArtifactFileValidationTests
{
    private const string ProducerId = "tooling-tests";
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");

    [Fact]
    public async Task CheckAsync_ShouldAcceptAnIntactArtifact()
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path);

        Assert.Equal(EvidenceArtifactFileStatus.Valid, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact));
        Assert.Equal(
            EvidenceArtifactFileStatus.Valid,
            await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact with { Sha256 = artifact.Sha256.ToUpperInvariant() }));
    }

    [Fact]
    public async Task CheckAsync_ShouldAcceptAnEmptyArtifact()
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path, []);

        Assert.Equal(EvidenceArtifactFileStatus.Valid, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/absolute/receipt.json")]
    [InlineData("test/../../receipt.json")]
    [InlineData("")]
    public async Task CheckAsync_ShouldRejectTraversingOrAbsoluteArtifactPaths(string relativePath)
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path) with { RelativePath = relativePath };

        Assert.Equal(EvidenceArtifactFileStatus.InvalidPath, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    public async Task CheckAsync_ShouldRejectProducerIdsThatAreNotOneSegment(string producerId)
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path);

        Assert.Equal(EvidenceArtifactFileStatus.InvalidPath, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, producerId, artifact));
    }

    [Fact]
    public async Task CheckAsync_ShouldRejectANegativeRecordedLength()
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path) with { LengthBytes = -1 };

        Assert.Equal(EvidenceArtifactFileStatus.InvalidPath, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact));
    }

    [Fact]
    public async Task CheckAsync_ShouldReportMissingFileDirectoryAndRoot()
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path);

        Assert.Equal(
            EvidenceArtifactFileStatus.Missing,
            await EvidenceArtifactFileValidation.CheckAsync(Path.Join(directory.Path, "no-such-root"), ProducerId, artifact));
        Assert.Equal(EvidenceArtifactFileStatus.Missing, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, "other-producer", artifact));
        File.Delete(Path.Join(directory.Path, ProducerId, "test", "test-receipt.json"));
        Assert.Equal(EvidenceArtifactFileStatus.Missing, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact));
    }

    [Fact]
    public async Task CheckAsync_ShouldReportADirectoryInPlaceOfTheFileAsMissing()
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path);
        var path = Path.Join(directory.Path, ProducerId, "test", "test-receipt.json");
        File.Delete(path);
        Directory.CreateDirectory(path);

        Assert.Equal(EvidenceArtifactFileStatus.Missing, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact));
    }

    [Fact]
    public async Task CheckAsync_ShouldCompareLengthBeforeDigest()
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path);
        var path = Path.Join(directory.Path, ProducerId, "test", "test-receipt.json");

        await File.WriteAllBytesAsync(path, [.. Content, 0x20]);
        Assert.Equal(EvidenceArtifactFileStatus.LengthMismatch, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact));
        await File.WriteAllBytesAsync(path, Content[..^1]);
        Assert.Equal(EvidenceArtifactFileStatus.LengthMismatch, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact));
    }

    [Fact]
    public async Task CheckAsync_ShouldReportReplacedBytesOfTheSameLength()
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path);
        var replaced = (byte[])Content.Clone();
        replaced[^2] ^= 1;
        await File.WriteAllBytesAsync(Path.Join(directory.Path, ProducerId, "test", "test-receipt.json"), replaced);

        Assert.Equal(EvidenceArtifactFileStatus.DigestMismatch, await EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("producer")]
    [InlineData("root")]
    public async Task CheckAsync_ShouldRejectALinkAtAnyComponent(string linked)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var root = Path.Join(directory.Path, "artifacts");
        var artifact = WriteArtifact(root);
        var target = linked switch
        {
            "file" => Path.Join(root, ProducerId, "test", "test-receipt.json"),
            "directory" => Path.Join(root, ProducerId, "test"),
            "producer" => Path.Join(root, ProducerId),
            _ => root,
        };
        var moved = Path.Join(directory.Path, "moved");
        if (linked == "file")
        {
            File.Move(target, moved);
            File.CreateSymbolicLink(target, moved);
        }
        else
        {
            Directory.Move(target, moved);
            Directory.CreateSymbolicLink(target, moved);
        }

        Assert.Equal(EvidenceArtifactFileStatus.Linked, await EvidenceArtifactFileValidation.CheckAsync(root, ProducerId, artifact));
    }

    [Fact]
    public async Task CheckAsync_ShouldValidateArgumentsAndObserveCancellation()
    {
        using var directory = TestDirectory.Create();
        var artifact = WriteArtifact(directory.Path);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => EvidenceArtifactFileValidation.CheckAsync(" ", ProducerId, artifact));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceArtifactFileValidation.CheckAsync(directory.Path, null!, artifact));
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, null!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvidenceArtifactFileValidation.CheckAsync(directory.Path, ProducerId, artifact, cancelled.Token));
    }

    [Fact]
    public async Task Verify_ShouldCheckArtifactsBesideTheManifestByDefault()
    {
        using var directory = TestDirectory.Create();
        var evidence = await WriteEvidenceAsync(directory.Path, EvidenceProducerOutcome.Passed);

        var verified = await evidence.Workflow.VerifyAsync(evidence.PlanPath, evidence.ManifestPath, CancellationToken.None);

        Assert.Equal(EvidenceClaimKind.TargetedComplete, verified.Manifest.ClaimKind);
    }

    [Fact]
    public async Task Verify_ShouldFailAfterTheReceiptIsDeleted()
    {
        using var directory = TestDirectory.Create();
        var evidence = await WriteEvidenceAsync(directory.Path, EvidenceProducerOutcome.Passed);
        File.Delete(evidence.ReceiptPath);

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(
            () => evidence.Workflow.VerifyAsync(evidence.PlanPath, evidence.ManifestPath, CancellationToken.None));

        Assert.Equal("ASEVD213", exception.Code);
        Assert.Contains("test-receipt", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_ShouldFailAfterTheReceiptIsReplaced()
    {
        using var directory = TestDirectory.Create();
        var evidence = await WriteEvidenceAsync(directory.Path, EvidenceProducerOutcome.Passed);
        var replaced = (byte[])Content.Clone();
        replaced[^2] ^= 1;
        await File.WriteAllBytesAsync(evidence.ReceiptPath, replaced);

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(
            () => evidence.Workflow.VerifyAsync(evidence.PlanPath, evidence.ManifestPath, CancellationToken.None));

        Assert.Equal("ASEVD213", exception.Code);
        Assert.Contains("DigestMismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_ShouldUseAnExplicitArtifactRoot()
    {
        using var directory = TestDirectory.Create();
        var evidence = await WriteEvidenceAsync(directory.Path, EvidenceProducerOutcome.Passed);
        var moved = Path.Join(directory.Path, "moved-artifacts");
        Directory.Move(Path.Join(evidence.Output, "artifacts"), moved);

        await Assert.ThrowsAsync<EvidenceCliException>(
            () => evidence.Workflow.VerifyAsync(evidence.PlanPath, evidence.ManifestPath, artifactRoot: " ", CancellationToken.None));
        var verified = await evidence.Workflow.VerifyAsync(evidence.PlanPath, evidence.ManifestPath, moved, CancellationToken.None);

        Assert.Equal(EvidenceClaimKind.TargetedComplete, verified.Manifest.ClaimKind);
    }

    /// <summary>Pins the documented behavior that verify proves integrity, not that a gate passed.</summary>
    [Fact]
    public async Task Verify_ShouldSucceedOnAnIntactManifestWhoseClaimIsNone()
    {
        using var directory = TestDirectory.Create();
        var evidence = await WriteEvidenceAsync(directory.Path, EvidenceProducerOutcome.Failed);

        var verified = await evidence.Workflow.VerifyAsync(evidence.PlanPath, evidence.ManifestPath, CancellationToken.None);

        Assert.Equal(EvidenceClaimKind.None, verified.Manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, verified.Manifest.Eligibility);
    }

    [Fact]
    public async Task Verify_ShouldNotTouchTheArtifactRootWhenTheManifestListsNoArtifacts()
    {
        using var directory = TestDirectory.Create();
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        var policy = new EvidencePolicy(
            "sample",
            "1",
            "coverage",
            [
                new EvidenceProfile("none", EvidenceProfileScope.Targeted, [], [], []),
                new EvidenceProfile(
                    "coverage",
                    EvidenceProfileScope.Targeted,
                    [],
                    [new EvidenceProducerDeclaration("coverage", "coverage", "1", [], ["a@1"], [], 60)],
                    [new EvidenceObligation("o", "r", "why", ["coverage"], "a@1")]),
            ],
            [new EvidencePolicyRule("docs", "docs/**", "none")]);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("docs/readme.md")]);
        var output = Path.Join(directory.Path, "evidence");
        await workflow.WritePlanAsync(plan, output, CancellationToken.None);
        await workflow.WriteManifestAsync(EvidenceManifestBuilder.Build(plan, []), output, CancellationToken.None);

        var verified = await workflow.VerifyAsync(
            Path.Join(output, "evidence-plan.json"),
            Path.Join(output, "evidence-manifest.json"),
            Path.Join(directory.Path, "does-not-exist"),
            CancellationToken.None);

        Assert.Equal(EvidenceClaimKind.NoEvidenceRequired, verified.Manifest.ClaimKind);
    }

    /// <summary>Old-reader mechanism: a manifest whose plan digest does not match its plan fails verify.</summary>
    [Fact]
    public async Task Verify_ShouldFailWhenThePlanDigestDoesNotMatch()
    {
        using var directory = TestDirectory.Create();
        var evidence = await WriteEvidenceAsync(directory.Path, EvidenceProducerOutcome.Passed);
        var plan = EvidenceCanonicalJson.Deserialize<EvidencePlan>(await File.ReadAllBytesAsync(evidence.PlanPath));
        var producer = plan.PolicySnapshot!.Profiles[0].Producers[0];
        var withoutTestBlock = plan.PolicySnapshot with
        {
            Profiles = [plan.PolicySnapshot.Profiles[0] with { Producers = [producer with { Kind = "future-kind", Test = null }] }],
        };
        await File.WriteAllBytesAsync(evidence.PlanPath, EvidenceCanonicalJson.Serialize(plan with { PolicySnapshot = withoutTestBlock }));

        var exception = await Assert.ThrowsAsync<EvidenceCliException>(
            () => evidence.Workflow.VerifyAsync(evidence.PlanPath, evidence.ManifestPath, CancellationToken.None));

        Assert.Equal("ASEVD203", exception.Code);
    }

    private static EvidenceArtifactResult WriteArtifact(string artifactRoot, byte[]? content = null)
    {
        content ??= Content;
        var path = Path.Join(artifactRoot, ProducerId, "test", "test-receipt.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return new EvidenceArtifactResult(
            EvidenceTestProducerContract.ReceiptSlotLogicalName,
            "test/test-receipt.json",
            EvidenceTestProducerContract.ReceiptMediaType,
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
    }

    private static async Task<WrittenEvidence> WriteEvidenceAsync(string root, EvidenceProducerOutcome outcome)
    {
        var producer = new EvidenceProducerDeclaration(
            ProducerId,
            EvidenceTestProducerContract.Kind,
            "1.0.0",
            [],
            [EvidenceTestProducerContract.AssertionId],
            [new EvidenceArtifactSlot(EvidenceTestProducerContract.ReceiptSlotLogicalName, "test", EvidenceTestProducerContract.ReceiptMediaType, true, 65536)],
            900)
        {
            Test = new EvidenceTestDeclaration([new EvidenceTestProject("tests/Tooling.Tests/Tooling.Tests.csproj")]),
        };
        var policy = new EvidencePolicy(
            "sample",
            "1",
            "tests",
            [
                new EvidenceProfile(
                    "tests",
                    EvidenceProfileScope.Targeted,
                    [],
                    [producer],
                    [new EvidenceObligation("tests-complete", "tooling", "Selected tests ran and passed.", [ProducerId], EvidenceTestProducerContract.AssertionId)]),
            ],
            []);
        var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("scripts/build.sh")]);
        var output = Path.Join(root, "evidence");
        var artifact = WriteArtifact(Path.Join(output, "artifacts"));
        var result = new EvidenceProducerResult(
            ProducerId,
            outcome,
            outcome == EvidenceProducerOutcome.Passed ? [EvidenceTestProducerContract.AssertionId] : [],
            Artifacts: [artifact]);
        var workflow = new EvidenceCliWorkflow(new EvidencePlanner());
        await workflow.WritePlanAsync(plan, output, CancellationToken.None);
        await workflow.WriteManifestAsync(EvidenceManifestBuilder.Build(plan, [result]), output, CancellationToken.None);
        return new WrittenEvidence(
            workflow,
            output,
            Path.Join(output, "evidence-plan.json"),
            Path.Join(output, "evidence-manifest.json"),
            Path.Join(output, "artifacts", ProducerId, "test", "test-receipt.json"));
    }

    private sealed record WrittenEvidence(EvidenceCliWorkflow Workflow, string Output, string PlanPath, string ManifestPath, string ReceiptPath);
}
