using System.Text.Json;
using ForgeTrust.AppSurface.Release;
using ForgeTrust.AppSurface.ReleaseContracts;

namespace ForgeTrust.AppSurface.Release.Tests;

public sealed class ReleaseTaggedProjectionResolverTests
{
    private const string RepositoryRoot = "/fixture/repository";
    private const string VersionText = "0.1.0-preview.1";
    private const string Tag = "v0.1.0-preview.1";
    private const string TagObjectId = "1111111111111111111111111111111111111111";
    private const string TagCommit = "2222222222222222222222222222222222222222";
    private const string PreparationBaseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DifferentTagObjectId = "3333333333333333333333333333333333333333";
    private const string DifferentCommit = "4444444444444444444444444444444444444444";
    private const string PackageIndexPath = "packages/package-index.yml";

    [Fact]
    public async Task ResolveMachineInspectReturnsProjectionBoundToCapturedV2TagIdentity()
    {
        var fixture = CreateFixture();

        var projection = await ResolveMachineInspectAsync(fixture);

        Assert.Equal(Tag, projection.Tag);
        Assert.Equal(TagObjectId, projection.TagObjectId);
        Assert.Equal(TagCommit, projection.TagCommit);
        Assert.Equal(ReleaseEvidenceV2.Schema, projection.EvidenceSchema);
        Assert.Equal(PreparationBaseCommit, projection.PreparationBaseCommit);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1770000000), projection.TaggerTimestamp);
        Assert.Contains("state: tagged", projection.SidecarYaml, StringComparison.Ordinal);
        Assert.NotEmpty(projection.ReleaseArtifactDigests);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("tag")]
    public async Task ResolveMachineInspectRejectsCapturedTagObjectHeaderMismatch(string mismatch)
    {
        var fixture = CreateFixture();
        var invalidTagObject = mismatch switch
        {
            "commit" => fixture.TagObject.Replace($"object {TagCommit}", $"object {DifferentCommit}", StringComparison.Ordinal),
            "tag" => fixture.TagObject.Replace($"tag {Tag}", "tag v0.1.0-preview.2", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        fixture.Runner.Add(
            $"git cat-file -p {TagObjectId}",
            new CommandResult(0, invalidTagObject, string.Empty));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(fixture));

        Assert.Equal("release-tag-object-binding-invalid", failure.Diagnostic.Code);
        Assert.DoesNotContain($"git merge-base --is-ancestor {TagCommit} origin/main", fixture.Runner.Calls);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("short-header")]
    public async Task ResolveMachineInspectRejectsMalformedCapturedTagObjectHeaders(string malformedHeader)
    {
        var fixture = CreateFixture();
        var invalidTagObject = malformedHeader switch
        {
            "type" => fixture.TagObject.Replace("type commit", "type blob", StringComparison.Ordinal),
            "short-header" => $"object {TagCommit}\ntype commit",
            _ => throw new ArgumentOutOfRangeException(nameof(malformedHeader))
        };
        fixture.Runner.Add(
            $"git cat-file -p {TagObjectId}",
            new CommandResult(0, invalidTagObject, string.Empty));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(fixture));

        Assert.Equal("release-tag-object-binding-invalid", failure.Diagnostic.Code);
        Assert.DoesNotContain($"git merge-base --is-ancestor {TagCommit} origin/main", fixture.Runner.Calls);
    }

    [Theory]
    [InlineData("short-object-id")]
    [InlineData("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public async Task ResolveMachineInspectRejectsInvalidCapturedTagObjectId(string objectId)
    {
        var runner = new FakeCommandRunner();
        runner.Add(
            $"git rev-parse --verify refs/tags/{Tag}",
            new CommandResult(0, $"{objectId}\n", string.Empty));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(runner));

        Assert.Equal("release-tag-object-id-invalid", failure.Diagnostic.Code);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task ResolveMachineInspectAcceptsFullSha256TagAndCommitIds()
    {
        var tagObjectId = new string('1', 64);
        var tagCommit = new string('2', 64);
        var fixture = CreateFixture(tagObjectId, tagCommit);

        var projection = await ResolveMachineInspectAsync(fixture);

        Assert.Equal(tagObjectId, projection.TagObjectId);
        Assert.Equal(tagCommit, projection.TagCommit);
    }

    [Theory]
    [InlineData("", "", "Git did not resolve the requested tag.")]
    [InlineData("", "missing ref", "missing ref")]
    [InlineData("tag lookup failed", "", "tag lookup failed")]
    public async Task ResolveMachineInspectUsesAvailableGitDiagnosticWhenTagRefIsMissing(
        string standardError,
        string standardOutput,
        string expectedCause)
    {
        var runner = new FakeCommandRunner();
        runner.Add(
            $"git rev-parse --verify refs/tags/{Tag}",
            new CommandResult(1, standardOutput, standardError));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(runner));

        Assert.Equal("release-tag-missing", failure.Diagnostic.Code);
        Assert.Equal(expectedCause, failure.Diagnostic.Cause);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData("", "Git could not inspect the captured tag object.")]
    [InlineData("object storage unavailable", "object storage unavailable")]
    public async Task ResolveMachineInspectReportsFailureToReadCapturedTagObject(
        string standardError,
        string expectedCause)
    {
        var runner = new FakeCommandRunner();
        runner.Add(
            $"git rev-parse --verify refs/tags/{Tag}",
            new CommandResult(0, $"{TagObjectId}\n", string.Empty));
        runner.Add(
            $"git cat-file -t {TagObjectId}",
            new CommandResult(1, string.Empty, standardError));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(runner));

        Assert.Equal("release-tag-object-missing", failure.Diagnostic.Code);
        Assert.Equal(expectedCause, failure.Diagnostic.Cause);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task ResolveMachineInspectRejectsPeeledCommitWithDifferentObjectFormat()
    {
        var runner = new FakeCommandRunner();
        runner.Add(
            $"git rev-parse --verify refs/tags/{Tag}",
            new CommandResult(0, $"{TagObjectId}\n", string.Empty));
        runner.Add($"git cat-file -t {TagObjectId}", new CommandResult(0, "tag\n", string.Empty));
        runner.Add(
            $"git cat-file -p {TagObjectId}",
            new CommandResult(0, CreateTagObject(TagCommit), string.Empty));
        runner.Add(
            $"git rev-parse {TagObjectId}^{{commit}}",
            new CommandResult(0, $"{new string('b', 64)}\n", string.Empty));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(runner));

        Assert.Equal("release-tag-commit-id-invalid", failure.Diagnostic.Code);
        Assert.Equal(4, runner.Calls.Count);
    }

    [Fact]
    public async Task ResolveMachineInspectRejectsTagThatMovesDuringArtifactValidation()
    {
        var fixture = CreateFixture();
        fixture.Runner.AddSequence(
            $"git rev-parse --verify refs/tags/{Tag}",
            new CommandResult(0, $"{TagObjectId}\n", string.Empty),
            new CommandResult(0, $"{DifferentTagObjectId}\n", string.Empty));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(fixture));

        Assert.Equal("release-tag-moved-during-inspect", failure.Diagnostic.Code);
        Assert.Contains(DifferentTagObjectId, failure.Diagnostic.Cause, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "", "The tag ref could not be re-read after artifact validation.")]
    [InlineData("unexpected ref output", "tag lookup failed", "unexpected ref output")]
    public async Task ResolveMachineInspectReportsWhyCapturedTagRefCouldNotBeRevalidated(
        string standardError,
        string standardOutput,
        string expectedCause)
    {
        var fixture = CreateFixture();
        fixture.Runner.AddSequence(
            $"git rev-parse --verify refs/tags/{Tag}",
            new CommandResult(0, $"{TagObjectId}\n", string.Empty),
            new CommandResult(1, standardOutput, standardError));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(fixture));

        Assert.Equal("release-tag-moved-during-inspect", failure.Diagnostic.Code);
        Assert.Equal(expectedCause, failure.Diagnostic.Cause);
    }

    [Theory]
    [InlineData("releases/current.md", "release-current-pointer-missing-from-tag")]
    [InlineData("releases/current.md.yml", "release-current-pointer-sidecar-missing-from-tag")]
    [InlineData(PackageIndexPath, "release-package-index-missing-from-tag")]
    public async Task ResolveMachineInspectRejectsMissingV2TreeArtifact(string path, string expectedCode)
    {
        var fixture = CreateFixture();
        fixture.Runner.Add(
            $"git show {TagCommit}:{path}",
            new CommandResult(1, string.Empty, "missing tagged blob"));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(fixture));

        Assert.Equal(expectedCode, failure.Diagnostic.Code);
    }

    [Fact]
    public async Task ResolveMachineInspectRejectsRebuiltReleaseNoteAgainstV2EvidenceDigest()
    {
        var fixture = CreateFixture();
        fixture.Runner.Add(
            $"git show {TagCommit}:releases/v{VersionText}.md",
            new CommandResult(0, "# Release note rebuilt after preparation\n", string.Empty));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(fixture));

        Assert.Equal("release-evidence-artifact-digest-mismatch", failure.Diagnostic.Code);
    }

    [Fact]
    public async Task ResolveMachineInspectRejectsV2ManifestThatOmitsTaggedPublicPackage()
    {
        var fixture = CreateFixture();
        fixture.Runner.Add(
            $"git show {TagCommit}:{PackageIndexPath}",
            new CommandResult(
                0,
                $"packages:\n  - project: Core/ForgeTrust.AppSurface.Core.csproj\n    classification: public\n    publish_decision: publish\n    release_track: explicit\n    release_notes_path: releases/v{VersionText}.md\n",
                string.Empty));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(fixture));

        Assert.Equal("release-evidence-package-set-mismatch", failure.Diagnostic.Code);
    }

    [Fact]
    public async Task ResolveMachineInspectFailsClosedWhenTagCommitIsOutsideBaseRef()
    {
        var fixture = CreateFixture();
        fixture.Runner.Add(
            $"git merge-base --is-ancestor {TagCommit} origin/main",
            new CommandResult(1, string.Empty, "not reachable from origin/main"));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(fixture));

        Assert.Equal("release-tag-unreachable-from-base-ref", failure.Diagnostic.Code);
        Assert.DoesNotContain($"git show {TagCommit}:releases/v{VersionText}.md", fixture.Runner.Calls);
    }

    [Fact]
    public async Task ResolveMachineInspectRejectsV2PreparationBaseOutsideTaggedCommit()
    {
        var fixture = CreateFixture();
        fixture.Runner.Add(
            $"git merge-base --is-ancestor {PreparationBaseCommit} {TagCommit}",
            new CommandResult(1, string.Empty, "preparation base is not an ancestor"));

        var failure = await Assert.ThrowsAsync<ReleaseToolException>(() => ResolveMachineInspectAsync(fixture));

        Assert.Equal("release-preparation-base-commit-not-contained-by-tag", failure.Diagnostic.Code);
    }

    private static async Task<ReleaseTaggedProjection> ResolveMachineInspectAsync(ResolverFixture fixture) =>
        await ResolveMachineInspectAsync(fixture.Runner);

    private static Task<ReleaseTaggedProjection> ResolveMachineInspectAsync(FakeCommandRunner runner)
    {
        var resolver = new ReleaseTaggedProjectionResolver(new ReleaseWorkspace(RepositoryRoot), runner);
        return resolver.ResolveMachineInspectAsync(CreateOptions(), CancellationToken.None);
    }

    private static ReleaseOptions CreateOptions() => new(
        "inspect",
        RepositoryRoot,
        SemVer.Parse(VersionText),
        Tag,
        Date: null,
        DryRun: true,
        ReportPath: null,
        GitHubOutputPath: null,
        FailOnWarnings: false,
        AllowExistingTargets: false);

    private static ResolverFixture CreateFixture(string tagObjectId = TagObjectId, string tagCommit = TagCommit)
    {
        var version = SemVer.Parse(VersionText);
        var workspace = new ReleaseWorkspace(RepositoryRoot);
        var releaseNote = $"# Release {version}\n";
        var releaseSidecar = ReleaseSidecar.Parse("title: Prepared release\n", "fixture")
            .ToPreparedRelease(version, new DateOnly(2026, 5, 25));
        var currentRelease = ReleaseCurrentPointer.Build(version);
        const string currentReleaseSidecar = "title: Current coordinated release\n";
        const string packageIndex = "packages: []\n";
        var manifest = JsonSerializer.Serialize(
            new ReleaseManifestV2(
                ReleaseManifestV2Validator.Schema,
                version.ToString(),
                version.TagName,
                "2026-05-25",
                PreparationBaseCommit,
                "prerelease",
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<CoordinatedPackageReleaseNoteResolution>(),
                Array.Empty<ReleaseDiagnosticRecord>(),
                Array.Empty<string>()),
            ReleaseJson.Options);
        var evidenceBundle = ReleaseEvidenceV2.RefreshSubject(
            ReleaseEvidence.BuildDraftV2(
                workspace,
                version,
                "prerelease",
                new DateOnly(2026, 5, 25),
                PreparationBaseCommit,
                releaseNote,
                releaseSidecar,
                manifest,
                currentRelease,
                currentReleaseSidecar,
                Array.Empty<CoordinatedPackageReleaseNoteResolution>()) with
            {
                GeneratedAtUtc = "2026-05-25T00:00:00.0000000Z"
            });
        var evidence = ReleaseEvidenceV2.Serialize(evidenceBundle);
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"releases/v{version}.md"] = releaseNote,
            [$"releases/v{version}.md.yml"] = releaseSidecar,
            [$"releases/v{version}.release.json"] = manifest,
            [$"releases/v{version}.evidence.json"] = evidence,
            ["releases/current.md"] = currentRelease,
            ["releases/current.md.yml"] = currentReleaseSidecar,
            [PackageIndexPath] = packageIndex
        };
        var tagObject = CreateTagObject(tagCommit, new ReleaseTagBinding(
            Tag,
            ReleaseEvidence.ComputeSha256Hex(releaseSidecar),
            ReleaseEvidence.ComputeSha256Hex(manifest),
            evidenceBundle.Subject.Sha256).Render());
        var runner = new FakeCommandRunner();
        runner.Add(
            $"git rev-parse --verify refs/tags/{Tag}",
            new CommandResult(0, $"{tagObjectId}\n", string.Empty));
        runner.Add($"git cat-file -t {tagObjectId}", new CommandResult(0, "tag\n", string.Empty));
        runner.Add($"git cat-file -p {tagObjectId}", new CommandResult(0, tagObject, string.Empty));
        runner.Add(
            $"git rev-parse {tagObjectId}^{{commit}}",
            new CommandResult(0, $"{tagCommit}\n", string.Empty));
        runner.Add(
            $"git merge-base --is-ancestor {tagCommit} origin/main",
            new CommandResult(0, string.Empty, string.Empty));
        runner.Add(
            $"git merge-base --is-ancestor {PreparationBaseCommit} {tagCommit}",
            new CommandResult(0, string.Empty, string.Empty));
        foreach (var (path, content) in artifacts)
        {
            runner.Add(
                $"git show {tagCommit}:{path}",
                new CommandResult(0, content, string.Empty));
        }

        return new ResolverFixture(runner, tagObject, artifacts);
    }

    private static string CreateTagObject(string commit, string? message = null) =>
        $"object {commit}\ntype commit\ntag {Tag}\ntagger Release Tests <release-tests@example.test> 1770000000 +0000\n\n{message ?? string.Empty}";

    private sealed record ResolverFixture(
        FakeCommandRunner Runner,
        string TagObject,
        IReadOnlyDictionary<string, string> Artifacts);
}
