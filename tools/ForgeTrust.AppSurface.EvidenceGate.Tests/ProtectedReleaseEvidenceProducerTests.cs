using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.EvidenceGate;
using ForgeTrust.AppSurface.Release;
using ForgeTrust.AppSurface.Testing;

namespace ForgeTrust.AppSurface.EvidenceGate.Tests;

[CollectionDefinition("Protected release process environment", DisableParallelization = true)]
public sealed class ProtectedReleaseProcessEnvironmentCollection
{
}

[Collection("Protected release process environment")]
public sealed class ProtectedReleaseEvidenceProducerTests
{
    private const string Version = "1.2.3-preview.1";
    private const string Tag = "v1.2.3-preview.1";
    private const string TagObjectId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PeeledCommit = "cccccccccccccccccccccccccccccccccccccccc";
    private const string ComparisonBaseCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ProducerRequiresTrustedInvocationProvider()
    {
        using var fixture = new ProducerFixture();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            new ProtectedReleaseEvidenceProducer(fixture.Root, null!));

        Assert.Equal("invocationProvider", exception.ParamName);
    }

    [Fact]
    public async Task RegisteredReleaseProducerFailsClosedWhenTrustedInvocationIsUnavailable()
    {
        using var fixture = new ProducerFixture();
        var registrations = EvidenceHostRunner.CreateFirstPartyRegistrations(fixture.Root);
        var producer = Assert.IsType<ProtectedReleaseEvidenceProducer>(
            registrations.Producers[ProtectedReleaseEvidenceProducer.ProducerId]);
        var declaration = CreateDeclaration();
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Contains("trusted protected-release invocation identity is unavailable", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultInspectAuthorityFailsClosedWhenTheTrustedTagIsUnavailable()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var invocationProvider = new FixedInvocationProvider(CreateInvocation());
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var producer = CreateProducer(fixture.Root, invocationProvider);

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Equal(1, invocationProvider.Calls);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Contains("Release inspect authority rejected", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidatedInspectIdentityAndCommittedReleaseDigestsAreWrittenAsTypedEvidenceButRemainUnavailable()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var invocation = CreateInvocation();
        var inspection = CreateInspection();
        var inspectAuthority = new FakeInspectAuthority(inspection);
        var producer = CreateProducer(fixture.Root, new FixedInvocationProvider(invocation), inspectAuthority);
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Equal("main", inspectAuthority.BaseRef);
        Assert.Equal(Version, inspectAuthority.Version);
        Assert.Equal(Tag, inspectAuthority.Tag);
        Assert.Equal(fixture.Root, inspectAuthority.RepositoryRoot);
        Assert.Equal(2, result.Artifacts!.Count);

        var projection = result.Artifacts.Single(artifact => artifact.LogicalName == ProtectedReleaseEvidenceProducer.ReleaseProjectionArtifactName);
        var projectionBytes = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(GetWriterRoot(fixture.Root), projection.RelativePath));
        using var projectionDocument = JsonDocument.Parse(projectionBytes);
        Assert.Equal("appsurface-release-inspect-v1", projectionDocument.RootElement.GetProperty("schema").GetString());
        Assert.Equal(TagObjectId, projectionDocument.RootElement.GetProperty("tagObjectId").GetString());
        Assert.Equal(PeeledCommit, projectionDocument.RootElement.GetProperty("peeledCommit").GetString());
        Assert.Equal(ComparisonBaseCommit, projectionDocument.RootElement.GetProperty("comparisonBaseCommit").GetString());
        Assert.Equal(ComputeSha256(projectionBytes), projection.Sha256);

        var digestIndex = result.Artifacts.Single(artifact => artifact.LogicalName == ProtectedReleaseEvidenceProducer.ReleaseDigestIndexArtifactName);
        var digestIndexBytes = await File.ReadAllBytesAsync(TestPathUtils.PathUnder(GetWriterRoot(fixture.Root), digestIndex.RelativePath));
        using var digestDocument = JsonDocument.Parse(digestIndexBytes);
        Assert.Equal("appsurface-protected-release-digest-index-v1", digestDocument.RootElement.GetProperty("schema").GetString());
        var releaseDigests = digestDocument.RootElement.GetProperty("releaseArtifactDigests").EnumerateArray().ToArray();
        Assert.Equal(5, releaseDigests.Length);
        Assert.Equal(
            inspection.ReleaseArtifactDigests.Select(static artifact => artifact.Path).OrderBy(static path => path, StringComparer.Ordinal),
            releaseDigests.Select(static artifact => artifact.GetProperty("path").GetString()).OrderBy(static path => path, StringComparer.Ordinal));
        Assert.Equal(ComputeSha256(digestIndexBytes), digestIndex.Sha256);
        Assert.Contains("package/archive", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tag-object")]
    [InlineData("peeled-commit")]
    [InlineData("lightweight")]
    public async Task FreshRemoteTagMismatchIsInvalidAndWritesNoEvidence(string mismatch)
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var remoteTag = mismatch switch
        {
            "tag-object" => new ProtectedReleaseRemoteTagObservation(new string('d', 40), PeeledCommit),
            "peeled-commit" => new ProtectedReleaseRemoteTagObservation(TagObjectId, new string('e', 40)),
            _ => new ProtectedReleaseRemoteTagObservation(TagObjectId, null),
        };
        var remoteAuthority = new FakeRemoteTagAuthority(remoteTag);
        var inspectAuthority = new FakeInspectAuthority(CreateInspection());
        var producer = CreateProducer(
            fixture.Root,
            new FixedInvocationProvider(CreateInvocation()),
            inspectAuthority,
            remoteAuthority);

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Equal(1, inspectAuthority.Calls);
        Assert.Equal(1, remoteAuthority.Calls);
        Assert.Equal(Tag, remoteAuthority.Tag);
    }

    [Fact]
    public async Task UnavailableRemoteTagReadCannotWriteEvidenceOrCloseTheReleaseObligation()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var remoteAuthority = new FakeRemoteTagAuthority(null)
        {
            Failure = new ProtectedReleaseRemoteTagUnavailableException(),
        };
        var producer = CreateProducer(
            fixture.Root,
            new FixedInvocationProvider(CreateInvocation()),
            new FakeInspectAuthority(CreateInspection()),
            remoteAuthority);

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Contains("could not be reread", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitRemoteAuthorityObservesAnAnnotatedTagMoveOnAReadOnlyFreshLookup()
    {
        using var fixture = new ProducerFixture();
        var remotePath = TestPathUtils.PathUnder(fixture.Root, "protected-release.git");
        var workPath = TestPathUtils.PathUnder(fixture.Root, "release-work");
        Directory.CreateDirectory(workPath);
        _ = await RunGitAsync(fixture.Root, "init", "--bare", "--quiet", remotePath);
        _ = await RunGitAsync(workPath, "init", "--quiet");
        _ = await RunGitAsync(workPath, "config", "user.name", "Evidence Gate Tests");
        _ = await RunGitAsync(workPath, "config", "user.email", "evidence-gate-tests@example.invalid");
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(workPath, "release.txt"), "first release commit");
        _ = await RunGitAsync(workPath, "add", "release.txt");
        _ = await RunGitAsync(workPath, "commit", "--quiet", "-m", "first release commit");
        _ = await RunGitAsync(workPath, "tag", "-a", Tag, "-m", "first annotated tag");
        _ = await RunGitAsync(workPath, "tag", "-a", $"{Tag}-extra", "-m", "same-prefix sibling tag");
        _ = await RunGitAsync(workPath, "remote", "add", "origin", remotePath);
        _ = await RunGitAsync(workPath, "push", "--quiet", "origin", $"refs/tags/{Tag}", $"refs/tags/{Tag}-extra");
        var firstTagObjectId = await RunGitAsync(workPath, "rev-parse", $"refs/tags/{Tag}");
        var firstPeeledCommit = await RunGitAsync(workPath, "rev-parse", $"refs/tags/{Tag}^{{}}");
        var remoteUrl = new Uri(remotePath).AbsoluteUri;
        var authority = new GitProtectedReleaseRemoteTagAuthority();

        var firstRead = await authority.ReadLocalFixtureForTestingAsync(remoteUrl, Tag, CancellationToken.None);

        Assert.Equal(new ProtectedReleaseRemoteTagObservation(firstTagObjectId, firstPeeledCommit), firstRead);

        await File.WriteAllTextAsync(TestPathUtils.PathUnder(workPath, "release.txt"), "second release commit");
        _ = await RunGitAsync(workPath, "add", "release.txt");
        _ = await RunGitAsync(workPath, "commit", "--quiet", "-m", "second release commit");
        _ = await RunGitAsync(workPath, "tag", "--force", "-a", Tag, "-m", "moved annotated tag");
        _ = await RunGitAsync(workPath, "push", "--quiet", "--force", "origin", $"refs/tags/{Tag}");
        var movedTagObjectId = await RunGitAsync(workPath, "rev-parse", $"refs/tags/{Tag}");
        var movedPeeledCommit = await RunGitAsync(workPath, "rev-parse", $"refs/tags/{Tag}^{{}}");

        var secondRead = await authority.ReadLocalFixtureForTestingAsync(remoteUrl, Tag, CancellationToken.None);

        Assert.Equal(new ProtectedReleaseRemoteTagObservation(movedTagObjectId, movedPeeledCommit), secondRead);
        Assert.NotEqual(firstRead, secondRead);
    }

    [Fact]
    public void RemoteTagParserRequiresTheExactAnnotatedRefAndCanonicalObjectIds()
    {
        var output = $"{TagObjectId}\trefs/tags/{Tag}\n{PeeledCommit}\trefs/tags/{Tag}^{{}}\n{new string('d', 40)}\trefs/tags/{Tag}-extra\n";

        var observation = GitProtectedReleaseRemoteTagAuthority.ParseRemoteTagAdvertisement(output, Tag);

        Assert.Equal(new ProtectedReleaseRemoteTagObservation(TagObjectId, PeeledCommit), observation);
        Assert.Null(GitProtectedReleaseRemoteTagAuthority.ParseRemoteTagAdvertisement($"{TagObjectId}\trefs/tags/{Tag}-extra\n", Tag));
        Assert.Throws<InvalidDataException>(() =>
            GitProtectedReleaseRemoteTagAuthority.ParseRemoteTagAdvertisement($"not-an-object-id\trefs/tags/{Tag}\n", Tag));
    }

    [Fact]
    public void RemoteTagParserRejectsMalformedAndDuplicateExactRefs()
    {
        var malformed = Assert.Throws<InvalidDataException>(() =>
            GitProtectedReleaseRemoteTagAuthority.ParseRemoteTagAdvertisement("missing-tab-separator", Tag));
        Assert.Contains("malformed tag advertisement", malformed.Message, StringComparison.Ordinal);

        var duplicateTag = Assert.Throws<InvalidDataException>(() =>
            GitProtectedReleaseRemoteTagAuthority.ParseRemoteTagAdvertisement(
                $"{TagObjectId}\trefs/tags/{Tag}\n{new string('d', 40)}\trefs/tags/{Tag}\n",
                Tag));
        Assert.Contains("exact tag ref more than once", duplicateTag.Message, StringComparison.Ordinal);

        var duplicatePeeledCommit = Assert.Throws<InvalidDataException>(() =>
            GitProtectedReleaseRemoteTagAuthority.ParseRemoteTagAdvertisement(
                $"{TagObjectId}\trefs/tags/{Tag}\n{PeeledCommit}\trefs/tags/{Tag}^{{}}\n{new string('d', 40)}\trefs/tags/{Tag}^{{}}\n",
                Tag));
        Assert.Contains("peeled tag ref more than once", duplicatePeeledCommit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteTagParserAcceptsCanonicalSha256ObjectIds()
    {
        var tagObjectId = new string('a', 64);
        var peeledCommit = new string('b', 64);

        var observation = GitProtectedReleaseRemoteTagAuthority.ParseRemoteTagAdvertisement(
            $"{tagObjectId}\trefs/tags/{Tag}\n{peeledCommit}\trefs/tags/{Tag}^{{}}\n",
            Tag);

        Assert.Equal(new ProtectedReleaseRemoteTagObservation(tagObjectId, peeledCommit), observation);
    }

    [Theory]
    [InlineData("file:///tmp/protected-release.git?token=ignored")]
    [InlineData("file:///tmp/protected-release.git#fragment")]
    public async Task LocalFixtureRejectsFileRemotesWithCredentialsOrSelectors(string remoteUrl)
    {
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitProtectedReleaseRemoteTagAuthority().ReadLocalFixtureForTestingAsync(remoteUrl, Tag, CancellationToken.None));

        Assert.Contains("outside the allowed scheme", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLocalRemoteIsReportedAsUnavailableRatherThanAsAnAbsentTag()
    {
        using var fixture = new ProducerFixture();
        var missingRemotePath = TestPathUtils.PathUnder(fixture.Root, "missing-protected-release.git");

        await Assert.ThrowsAsync<ProtectedReleaseRemoteTagUnavailableException>(() =>
            new GitProtectedReleaseRemoteTagAuthority().ReadLocalFixtureForTestingAsync(
                new Uri(missingRemotePath).AbsoluteUri,
                Tag,
                CancellationToken.None));
    }

    [Fact]
    public async Task MissingGitExecutableIsReportedAsUnavailableWithTheStartupFailure()
    {
        using var fixture = new ProducerFixture();
        var remotePath = TestPathUtils.PathUnder(fixture.Root, "protected-release.git");
        _ = await RunGitAsync(fixture.Root, "init", "--bare", "--quiet", remotePath);
        var emptyPath = TestPathUtils.PathUnder(fixture.Root, "empty-path");
        Directory.CreateDirectory(emptyPath);
        var originalPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);

        try
        {
            Environment.SetEnvironmentVariable("PATH", emptyPath, EnvironmentVariableTarget.Process);

            var exception = await Assert.ThrowsAsync<ProtectedReleaseRemoteTagUnavailableException>(() =>
                new GitProtectedReleaseRemoteTagAuthority().ReadLocalFixtureForTestingAsync(
                    new Uri(remotePath).AbsoluteUri,
                    Tag,
                    CancellationToken.None));

            Assert.IsType<Win32Exception>(exception.InnerException);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public async Task RemoteReadTimeoutReportsUnavailableAndTerminatesTheGitProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("The fake git shell shim requires a Unix host.");
        }

        const string sleepPath = "/bin/sleep";
        if (!File.Exists(sleepPath))
        {
            throw Xunit.Sdk.SkipException.ForSkip("The timeout fixture requires /bin/sleep.");
        }

        using var fixture = new ProducerFixture();
        var remotePath = TestPathUtils.PathUnder(fixture.Root, "protected-release.git");
        _ = await RunGitAsync(fixture.Root, "init", "--bare", "--quiet", remotePath);
        var shimDirectory = TestPathUtils.PathUnder(fixture.Root, "git-shim");
        Directory.CreateDirectory(shimDirectory);
        var pidFile = TestPathUtils.PathUnder(fixture.Root, "git-shim.pid");
        var shimPath = TestPathUtils.PathUnder(shimDirectory, "git");
        await File.WriteAllTextAsync(
            shimPath,
            $"#!/bin/sh\nprintf '%s\\n' \"$$\" > {QuoteForShell(pidFile)}\nexec {QuoteForShell(sleepPath)} 30\n");
        File.SetUnixFileMode(
            shimPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var originalPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
        int? shimProcessId = null;

        try
        {
            Environment.SetEnvironmentVariable("PATH", shimDirectory, EnvironmentVariableTarget.Process);

            var exception = await Assert.ThrowsAsync<ProtectedReleaseRemoteTagUnavailableException>(() =>
                new GitProtectedReleaseRemoteTagAuthority().ReadLocalFixtureForTestingAsync(
                    new Uri(remotePath).AbsoluteUri,
                    Tag,
                    CancellationToken.None));

            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
            shimProcessId = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(
                await HasExitedAsync(shimProcessId.Value, TimeSpan.FromSeconds(3)),
                "The timed-out git process should be terminated before the remote read returns.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath, EnvironmentVariableTarget.Process);
            if (shimProcessId is null
                && File.Exists(pidFile)
                && int.TryParse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture, out var startedProcessId))
            {
                shimProcessId = startedProcessId;
            }

            if (shimProcessId is int processId)
            {
                KillIfRunning(processId);
            }
        }
    }

    [Fact]
    public async Task ProductionRemoteReadPropagatesPreCanceledTokenBeforeNetworkAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new GitProtectedReleaseRemoteTagAuthority().ReadAsync(Tag, cancellation.Token));
    }

    [Fact]
    public async Task ProductionRemoteReadUsesOnlyThePinnedRepositoryAndExactTagRefs()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("The fake git shell shim requires a Unix host.");
        }

        using var fixture = new ProducerFixture();
        var shimDirectory = TestPathUtils.PathUnder(fixture.Root, "git-shim");
        Directory.CreateDirectory(shimDirectory);
        var argumentsPath = TestPathUtils.PathUnder(fixture.Root, "git-arguments.txt");
        var shimPath = TestPathUtils.PathUnder(shimDirectory, "git");
        await File.WriteAllTextAsync(
            shimPath,
            $"#!/bin/sh\nprintf '%s\\n' \"$@\" > {QuoteForShell(argumentsPath)}\n"
            + $"printf '%s\\t%s\\n' {QuoteForShell(TagObjectId)} {QuoteForShell($"refs/tags/{Tag}")}\n"
            + $"printf '%s\\t%s\\n' {QuoteForShell(PeeledCommit)} {QuoteForShell($"refs/tags/{Tag}^{{}}")}\n");
        File.SetUnixFileMode(
            shimPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var originalPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);

        try
        {
            Environment.SetEnvironmentVariable("PATH", shimDirectory, EnvironmentVariableTarget.Process);

            var observation = await new GitProtectedReleaseRemoteTagAuthority().ReadAsync(Tag, CancellationToken.None);

            Assert.Equal(new ProtectedReleaseRemoteTagObservation(TagObjectId, PeeledCommit), observation);
            var arguments = await File.ReadAllLinesAsync(argumentsPath);
            Assert.Equal(
                [
                    "-c", "credential.helper=", "-c", "credential.interactive=false", "-c", "core.askPass=",
                    "-c", "http.extraHeader=", "-c", "http.proxy=", "-c", "protocol.allow=never",
                    "-c", "protocol.https.allow=always", "ls-remote", "--tags", "--",
                    GitProtectedReleaseRemoteTagAuthority.ProtectedRepositoryRemoteUrl,
                    $"refs/tags/{Tag}", $"refs/tags/{Tag}^{{}}",
                ],
                arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public async Task OversizedRemoteAdvertisementStopsReadingAndRejectsTheResponse()
    {
        using var fixture = new ProducerFixture();
        var remotePath = TestPathUtils.PathUnder(fixture.Root, "large-protected-release.git");
        var workPath = TestPathUtils.PathUnder(fixture.Root, "large-release-work");
        Directory.CreateDirectory(workPath);
        _ = await RunGitAsync(fixture.Root, "init", "--bare", "--quiet", remotePath);
        _ = await RunGitAsync(workPath, "init", "--quiet");
        _ = await RunGitAsync(workPath, "config", "user.name", "Evidence Gate Tests");
        _ = await RunGitAsync(workPath, "config", "user.email", "evidence-gate-tests@example.invalid");
        await File.WriteAllTextAsync(TestPathUtils.PathUnder(workPath, "release.txt"), "large tag advertisement fixture");
        _ = await RunGitAsync(workPath, "add", "release.txt");
        _ = await RunGitAsync(workPath, "commit", "--quiet", "-m", "large tag advertisement fixture");
        var commit = await RunGitAsync(workPath, "rev-parse", "HEAD");
        _ = await RunGitAsync(workPath, "remote", "add", "origin", remotePath);
        _ = await RunGitAsync(workPath, "push", "--quiet", "origin", "HEAD:refs/heads/main");

        var tagReferences = string.Join(
            '\n',
            Enumerable.Range(0, 3000).Select(index => $"create refs/tags/bulk-{index:D4} {commit}")) + "\n";
        await RunGitWithInputAsync(remotePath, tagReferences, "update-ref", "--stdin");

        var authority = new GitProtectedReleaseRemoteTagAuthority();
        var remoteUrl = new Uri(remotePath).AbsoluteUri;
        using var cancellation = new CancellationTokenSource();
        var canceledRead = authority.ReadLocalFixtureForTestingAsync(remoteUrl, "bulk-*", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledRead);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            authority.ReadLocalFixtureForTestingAsync(remoteUrl, "bulk-*", CancellationToken.None));
    }

    [Fact]
    public async Task ProductionRemoteAuthorityUsesOnlyTheFixedPublicRepositoryAndRejectsRemoteOverrideInputs()
    {
        Assert.Equal(
            "https://github.com/forge-trust/AppSurface.git",
            GitProtectedReleaseRemoteTagAuthority.ProtectedRepositoryRemoteUrl);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new GitProtectedReleaseRemoteTagAuthority().ReadLocalFixtureForTestingAsync("https://example.invalid/repo.git", Tag, CancellationToken.None));
    }

    [Fact]
    public async Task FreshRemoteSuccessDiagnosticNamesVerifiedTagAndRemainingEvidenceGaps()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var producer = CreateProducer(
            fixture.Root,
            new FixedInvocationProvider(CreateInvocation()),
            new FakeInspectAuthority(CreateInspection()));

        var result = await producer.ProduceAsync(
            CreateContext(declaration, new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root))),
            CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Unavailable, result.Outcome);
        Assert.Contains("matching fresh remote annotated-tag identity", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("release-event validation", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("produced package/archive verification", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProducerRejectsMissingArtifactWriterAfterInspectionInsteadOfReturningUnwrittenEvidence()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var inspectAuthority = new FakeInspectAuthority(CreateInspection());
        var producer = CreateProducer(fixture.Root, new FixedInvocationProvider(CreateInvocation()), inspectAuthority);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await producer.ProduceAsync(CreateContext(declaration, writer: null), CancellationToken.None));

        Assert.Equal(1, inspectAuthority.Calls);
    }

    [Fact]
    public async Task EvidenceHostCannotIssueReleaseCompleteFromLocalInspectEvenWithAcceptedEnvelope()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var context = CreateContext(declaration, new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root)));
        var producer = CreateProducer(fixture.Root, new FixedInvocationProvider(CreateInvocation()), new FakeInspectAuthority(CreateInspection()));
        await using var host = EvidenceHostBootstrap.Create(
            context.Plan,
            registration =>
            {
                registration.AddProducer(producer);
                registration.SetEnvelopeVerifier(new AcceptedEnvelopeVerifier());
            },
            new EvidenceHostOptions(ArtifactDirectory: TestPathUtils.PathUnder(fixture.Root, "host-artifacts")));

        var manifest = await host.RunAsync();

        Assert.Equal(EvidenceExecutionVerdict.Incomplete, manifest.ExecutionVerdict);
        Assert.Equal(EvidenceClaimKind.None, manifest.ClaimKind);
        Assert.Equal(EvidenceClaimEligibility.None, manifest.Eligibility);
        Assert.Equal(EvidenceEnvelopeStatus.ValidatedNotAttested, manifest.EnvelopeStatus);
        Assert.Empty(manifest.ClosedObligationIds);
        Assert.Equal(["protected-release-subject"], manifest.UnmediatedObligationIds);
        var producerResult = Assert.Single(manifest.ProducerResults);
        Assert.Equal(EvidenceProducerOutcome.Unavailable, producerResult.Outcome);
        Assert.Equal(2, producerResult.Artifacts!.Count);
        Assert.True(EvidenceManifestBuilder.Verify(context.Plan, manifest));
    }

    [Theory]
    [InlineData("tag-object")]
    [InlineData("peeled-commit")]
    public async Task InspectIdentityMismatchIsInvalidAndWritesNoTypedArtifacts(string mismatch)
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var inspection = CreateInspection() with
        {
            TagObjectId = mismatch == "tag-object" ? new string('d', 40) : TagObjectId,
            PeeledCommit = mismatch == "peeled-commit" ? new string('e', 40) : PeeledCommit,
        };
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var producer = CreateProducer(fixture.Root, new FixedInvocationProvider(CreateInvocation()), new FakeInspectAuthority(inspection));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Null(result.Artifacts);
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Fact]
    public async Task UnsupportedDeclarationIsRejectedBeforeInvocationOrInspection()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration() with { Kind = "other" };
        var invocationProvider = new FixedInvocationProvider(CreateInvocation());
        var inspectAuthority = new FakeInspectAuthority(CreateInspection());
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var producer = CreateProducer(fixture.Root, invocationProvider, inspectAuthority);

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Equal(0, invocationProvider.Calls);
        Assert.Equal(0, inspectAuthority.Calls);
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Fact]
    public async Task OversizedInspectionProjectionIsRejectedBeforeAnyArtifactIsWritten()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var inspection = CreateInspection() with
        {
            ReleaseArtifactDigests = [new ReleaseInspectArtifactDigest(new string('p', 20 * 1024), new string('a', 64))],
        };
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        var producer = CreateProducer(fixture.Root, new FixedInvocationProvider(CreateInvocation()), new FakeInspectAuthority(inspection));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Contains("artifact bound", result.Diagnostic, StringComparison.Ordinal);
        Assert.Empty(writer.WrittenArtifacts);
    }

    [Theory]
    [InlineData("release-rejected")]
    [InlineData("unreadable-inspection")]
    public async Task InspectFailureIsInvalidAndCannotWriteReleaseArtifacts(string failure)
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        Exception failureException = failure == "release-rejected"
            ? new ReleaseToolException(ReleaseDiagnostic.Error("release-invalid", "Tag inspection failed.", "Invalid tag.", "Correct the tag.", "releases/README.md"))
            : new IOException("The inspection stream is unavailable.");
        var producer = CreateProducer(fixture.Root, new FixedInvocationProvider(CreateInvocation()), new ThrowingInspectAuthority(failureException));

        var result = await producer.ProduceAsync(CreateContext(declaration, writer), CancellationToken.None);

        Assert.Equal(EvidenceProducerOutcome.Invalid, result.Outcome);
        Assert.Empty(result.SatisfiedAssertionIds);
        Assert.Empty(writer.WrittenArtifacts);
        Assert.Contains(
            failure == "release-rejected" ? "Release inspect authority rejected" : "could not be captured",
            result.Diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutAReleaseClaim()
    {
        using var fixture = new ProducerFixture();
        var declaration = CreateDeclaration();
        var writer = new EvidenceArtifactWriter(declaration, GetWriterRoot(fixture.Root));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var producer = CreateProducer(fixture.Root, new FixedInvocationProvider(CreateInvocation()), new FakeInspectAuthority(CreateInspection()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await producer.ProduceAsync(CreateContext(declaration, writer), cancellation.Token));

        Assert.Empty(writer.WrittenArtifacts);
    }

    private static EvidenceProducerDeclaration CreateDeclaration() => new(
        ProtectedReleaseEvidenceProducer.ProducerId,
        "release-inspection",
        "1.0.0",
        [],
        [ProtectedReleaseEvidenceProducer.ProtectedReleaseAssertionId],
        [
            new EvidenceArtifactSlot("release-projection", "release/projection", "application/json", true, 2 * 1024 * 1024),
            new EvidenceArtifactSlot("release-digest-index", "release/digests", "application/json", true, 2 * 1024 * 1024),
        ],
        600);

    private static ProtectedReleaseInvocation CreateInvocation() => new(
        Version,
        Tag,
        "main",
        TagObjectId,
        PeeledCommit);

    private static ReleaseInspectMachineResult CreateInspection() => new(
        ReleaseInspectMachineResult.CurrentSchema,
        Version,
        Tag,
        "main",
        TagObjectId,
        PeeledCommit,
        ComparisonBaseCommit,
        new string('f', 64),
        new string('1', 64),
        [
            new ReleaseInspectArtifactDigest($"releases/v{Version}.md", new string('2', 64)),
            new ReleaseInspectArtifactDigest($"releases/v{Version}.md.yml", new string('3', 64)),
            new ReleaseInspectArtifactDigest($"releases/v{Version}.release.json", new string('4', 64)),
            new ReleaseInspectArtifactDigest("releases/current.md", new string('5', 64)),
            new ReleaseInspectArtifactDigest("releases/current.md.yml", new string('6', 64)),
        ]);

    private static EvidenceProducerContext CreateContext(EvidenceProducerDeclaration declaration, EvidenceArtifactWriter? writer)
    {
        var obligation = new EvidenceObligation(
            "protected-release-subject",
            "protected-release",
            "Bind protected release identity and artifacts.",
            [declaration.Id],
            ProtectedReleaseEvidenceProducer.ProtectedReleaseAssertionId);
        var profile = new EvidenceProfile("protected-release", EvidenceProfileScope.Release, [], [declaration], [obligation]);
        var policy = new EvidencePolicy("release-producer-tests", "1", profile.Id, [profile], []);
        var changedPaths = Array.Empty<NormalizedDiffPath>();
        var draft = new EvidencePlan(
            "1.0",
            policy.Id,
            EvidenceDigest.CanonicalSha256(policy),
            EvidenceDigest.CanonicalSha256(changedPaths),
            profile,
            changedPaths,
            [],
            string.Empty,
            policy);
        var plan = draft with { PlanDigest = EvidenceDigest.CanonicalSha256(draft) };
        return new EvidenceProducerContext(plan, declaration, TimeProvider.System, writer);
    }

    private static string GetWriterRoot(string root) =>
        TestPathUtils.PathUnder(root, "artifacts", ProtectedReleaseEvidenceProducer.ProducerId);

    private static ProtectedReleaseEvidenceProducer CreateProducer(
        string root,
        IProtectedReleaseInvocationProvider invocationProvider,
        IReleaseInspectMachineAuthority? inspectAuthority = null,
        FakeRemoteTagAuthority? remoteTagAuthority = null) =>
        new(
            root,
            invocationProvider,
            inspectAuthority,
            remoteTagAuthority ?? new FakeRemoteTagAuthority(new ProtectedReleaseRemoteTagObservation(TagObjectId, PeeledCommit)));

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string QuoteForShell(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static async Task<bool> HasExitedAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return false;
    }

    private static void KillIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git for the protected release test fixture.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private static async Task RunGitWithInputAsync(string workingDirectory, string input, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git to populate the protected release test fixture.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
    }

    private sealed class FixedInvocationProvider(ProtectedReleaseInvocation? invocation) : IProtectedReleaseInvocationProvider
    {
        public int Calls { get; private set; }

        public ValueTask<ProtectedReleaseInvocation?> ReadAsync(EvidenceProducerContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(invocation);
        }
    }

    private sealed class FakeInspectAuthority(ReleaseInspectMachineResult result) : IReleaseInspectMachineAuthority
    {
        public int Calls { get; private set; }

        public string? RepositoryRoot { get; private set; }

        public string? Version { get; private set; }

        public string? Tag { get; private set; }

        public string? BaseRef { get; private set; }

        public Task<ReleaseInspectMachineResult> InspectAsync(
            string repositoryRoot,
            string version,
            string tag,
            string baseRef,
            CancellationToken cancellationToken)
        {
            Calls++;
            RepositoryRoot = repositoryRoot;
            Version = version;
            Tag = tag;
            BaseRef = baseRef;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingInspectAuthority(Exception exception) : IReleaseInspectMachineAuthority
    {
        public Task<ReleaseInspectMachineResult> InspectAsync(
            string repositoryRoot,
            string version,
            string tag,
            string baseRef,
            CancellationToken cancellationToken) => Task.FromException<ReleaseInspectMachineResult>(exception);
    }

    private sealed class FakeRemoteTagAuthority(ProtectedReleaseRemoteTagObservation? observation) : IProtectedReleaseRemoteTagAuthority
    {
        public int Calls { get; private set; }

        public string? Tag { get; private set; }

        public Exception? Failure { get; init; }

        public Task<ProtectedReleaseRemoteTagObservation?> ReadAsync(
            string tag,
            CancellationToken cancellationToken)
        {
            Calls++;
            Tag = tag;
            return Failure is null
                ? Task.FromResult(observation)
                : Task.FromException<ProtectedReleaseRemoteTagObservation?>(Failure);
        }
    }

    private sealed class AcceptedEnvelopeVerifier : IEvidenceExecutionEnvelopeVerifier
    {
        public ValueTask<EvidenceEnvelopeResult> VerifyAsync(EvidencePlan plan, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EvidenceEnvelopeResult(Accepted: true, Attested: false));
    }

    private sealed class ProducerFixture : IDisposable
    {
        public ProducerFixture()
        {
            Root = TestPathUtils.PathUnder(Path.GetTempPath(), "appsurface-protected-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
