using ForgeTrust.AppSurface.Release;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseInspectMachineResultTests
{
    [Fact]
    public void FromProjectionRequiresTheCompleteV2TagAndArtifactIdentity()
    {
        var (options, valid) = CreateValidProjection();
        var accepted = ReleaseInspectMachineResult.FromProjection(options, valid);

        Assert.Equal(ReleaseInspectMachineResult.CurrentSchema, accepted.Schema);
        Assert.Equal(5, accepted.ReleaseArtifactDigests.Count);

        var invalid = new Dictionary<string, ReleaseTaggedProjection>
        {
            ["legacy evidence"] = valid with { EvidenceSchema = "appsurface-release-evidence-bundle-v1" },
            ["missing tag object"] = valid with { TagObjectId = null },
            ["noncanonical tag object"] = valid with { TagObjectId = new string('B', 40) },
            ["noncanonical peeled commit"] = valid with { TagCommit = new string('C', 40) },
            ["missing peeled commit"] = valid with { TagCommit = string.Empty },
            ["mismatched Git formats"] = valid with { TagCommit = new string('c', 64) },
            ["comparison base format differs from Git object format"] = valid with
            {
                TagObjectId = new string('b', 64),
                TagCommit = new string('c', 64),
            },
            ["noncanonical preparation base"] = valid with { PreparationBaseCommit = new string('G', 40) },
            ["uppercase preparation base"] = valid with { PreparationBaseCommit = new string('A', 40) },
            ["mismatched preparation base format"] = valid with { PreparationBaseCommit = new string('a', 64) },
            ["invalid bundle digest"] = valid with { EvidenceBundleSha256 = "bad" },
            ["noncanonical full-length bundle digest"] = valid with { EvidenceBundleSha256 = new string('F', 64) },
            ["invalid subject digest"] = valid with { EvidenceSubjectSha256 = "bad" },
            ["noncanonical full-length subject digest"] = valid with { EvidenceSubjectSha256 = new string('F', 64) },
            ["missing artifact"] = valid with { ReleaseArtifactDigests = valid.ReleaseArtifactDigests.Skip(1).ToArray() },
            ["duplicate artifact"] = valid with
            {
                ReleaseArtifactDigests = valid.ReleaseArtifactDigests.Skip(1).Append(valid.ReleaseArtifactDigests[1]).ToArray(),
            },
            ["unexpected artifact path"] = valid with
            {
                ReleaseArtifactDigests = ReplaceFirst(
                    valid.ReleaseArtifactDigests,
                    valid.ReleaseArtifactDigests[0] with { Path = "releases/unexpected.md" }),
            },
            ["too many artifacts"] = valid with
            {
                ReleaseArtifactDigests = valid.ReleaseArtifactDigests.Append(valid.ReleaseArtifactDigests[0]).ToArray(),
            },
            ["wrong artifact algorithm"] = valid with
            {
                ReleaseArtifactDigests = ReplaceFirst(valid.ReleaseArtifactDigests, valid.ReleaseArtifactDigests[0] with { Algorithm = "sha512" }),
            },
            ["invalid artifact hash"] = valid with
            {
                ReleaseArtifactDigests = ReplaceFirst(valid.ReleaseArtifactDigests, valid.ReleaseArtifactDigests[0] with { Value = "bad" }),
            },
            ["noncanonical full-length artifact hash"] = valid with
            {
                ReleaseArtifactDigests = ReplaceFirst(
                    valid.ReleaseArtifactDigests,
                    valid.ReleaseArtifactDigests[0] with { Value = new string('F', 64) }),
            },
        };

        foreach (var (reason, projection) in invalid)
        {
            var failure = Assert.Throws<ReleaseToolException>(() => ReleaseInspectMachineResult.FromProjection(options, projection));
            Assert.Equal("release-inspect-machine-json-v2-required", failure.Diagnostic.Code);
            Assert.NotEmpty(reason);
        }
    }

    [Fact]
    public void SerializeBoundedRejectsAnOversizedMachineResult()
    {
        var (options, projection) = CreateValidProjection();
        var result = ReleaseInspectMachineResult.FromProjection(options, projection);
        var oversized = result with { Tag = new string('x', ReleaseInspectMachineResult.MaximumSerializedUtf8Bytes) };

        var failure = Assert.Throws<ReleaseToolException>(() => oversized.SerializeBounded());

        Assert.Equal("release-inspect-machine-json-too-large", failure.Diagnostic.Code);
    }

    private static IReadOnlyList<ReleaseEvidenceArtifactDigest> ReplaceFirst(
        IReadOnlyList<ReleaseEvidenceArtifactDigest> artifacts,
        ReleaseEvidenceArtifactDigest replacement) =>
        [replacement, .. artifacts.Skip(1)];

    private static (ReleaseOptions Options, ReleaseTaggedProjection Projection) CreateValidProjection()
    {
        var version = SemVer.Parse("0.1.0-preview.1");
        var options = new ReleaseOptions(
            "inspect",
            "/trusted/repository",
            version,
            version.TagName,
            Date: null,
            DryRun: false,
            ReportPath: null,
            GitHubOutputPath: null,
            FailOnWarnings: false,
            AllowExistingTargets: false);
        var paths = new[]
        {
            $"releases/v{version}.md",
            $"releases/v{version}.md.yml",
            $"releases/v{version}.release.json",
            "releases/current.md",
            "releases/current.md.yml",
        };
        var projection = new ReleaseTaggedProjection(
            version.TagName,
            new string('1', 40),
            new string('2', 40),
            DateTimeOffset.UnixEpoch,
            string.Empty,
            string.Empty,
            new ReleaseEvidenceValidationResult(null, [], null),
            ReleaseEvidenceV2.Schema,
            new string('0', 40),
            new string('3', 64),
            new string('4', 64),
            paths.Select(path => new ReleaseEvidenceArtifactDigest(path, "sha256", new string('5', 64))).ToArray());
        return (options, projection);
    }
}
