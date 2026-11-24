using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.PackageIndex;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class TailwindArtifactTransportTests : IDisposable
{
    private const string Repository = "forge-trust/AppSurface";
    private const string RepositoryId = "1035215851";
    private const string RunId = "36547070332";
    private const string Source = "10d375c6942fc650cc64f64e0748add77540d47c";
    private const string ArtifactId = "11023576342";
    private const string Attempt = "1";
    private const string PackageId = "ForgeTrust.AppSurface.Web.Tailwind";
    private const string Version = "1.2.3-ci.798";
    private const string Token = "token-sentinel-910";
    private readonly string _root = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot,
        "tailwind-artifact-transport-tests", Guid.NewGuid().ToString("N"));
    private readonly ArtifactExpectedContext _expected = new(Repository, RepositoryId, RunId, Attempt, Source, ArtifactId);

    public TailwindArtifactTransportTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("repository-root-blank")]
    [InlineData("receipt-path-blank")]
    [InlineData("subject-hash-null")]
    [InlineData("work-directory-blank")]
    [InlineData("artifacts-output-blank")]
    [InlineData("report-directory-blank")]
    [InlineData("token-blank")]
    [InlineData("token-whitespace")]
    [InlineData("token-control")]
    [InlineData("expected-null")]
    public async Task RequestValidation_RefusesNullAndBlankRequiredFieldsWithoutTouchingInputs(string scenario)
    {
        // protects: required adapter request values fail before source checks, HTTP, or owned output creation.
        // fails_when: a null/blank request value reaches transport or changes preserved caller-owned files.
        // no_claim: nested malformed expected-identity fields are validated by the verifier's separate contract.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var receiptBefore = Convert.ToHexString(await File.ReadAllBytesAsync(receipt));
        var preserved = Path.Combine(_root, "preserved-request-input.txt");
        await File.WriteAllTextAsync(preserved, "keep-existing-input");
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var sourceCalls = 0;
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) => { sourceCalls++; return Task.CompletedTask; });
        var work = Path.Combine(_root, "work-request-validation-" + scenario);
        var output = Path.Combine(_root, "output-request-validation-" + scenario);
        var report = Path.Combine(_root, "report-request-validation-" + scenario);
        var request = Request(_expected, receipt, fixture.SubjectHash, work, output, report);
        request = scenario switch
        {
            "repository-root-blank" => request with { RepositoryRoot = " " },
            "receipt-path-blank" => request with { ReceiptPath = "" },
            "subject-hash-null" => request with { ExpectedSubjectSha256 = null! },
            "work-directory-blank" => request with { WorkDirectory = " " },
            "artifacts-output-blank" => request with { ArtifactsOutput = "" },
            "report-directory-blank" => request with { ReportDirectory = " " },
            "token-blank" => request with { Token = " \t" },
            "token-whitespace" => request with { Token = "token value" },
            "token-control" => request with { Token = "token\u0001value" },
            "expected-null" => request with { Expected = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var result = await workflow.RunAsync(request!);

        Assert.False(result.Succeeded);
        Assert.Equal("failed", result.Status);
        Assert.Equal("artifact-input-invalid", result.Code);
        Assert.False(result.ReportWritten);
        Assert.Equal(0, sourceCalls);
        Assert.Empty(handler.Requests);
        Assert.Equal(receiptBefore, Convert.ToHexString(await File.ReadAllBytesAsync(receipt)));
        Assert.Equal("keep-existing-input", await File.ReadAllTextAsync(preserved));
        Assert.False(Directory.Exists(work));
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(report));
        Assert.False(File.Exists(Path.Combine(report, "verified-artifact.json")));
    }

    [Fact]
    public async Task NullRequestIsRefusedWithoutCreatingAnOwnedReport()
    {
        // protects: a null public request is a typed refusal before any path or transport work.
        // fails_when: null request throws to the caller or creates report/output state.
        // no_claim: a null request has no caller-owned paths to preserve.
        var handler = new FakeHandler([], "");
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier);

        var result = await workflow.RunAsync(null!);

        Assert.Equal("artifact-input-invalid", result.Code);
        Assert.Equal("failed", result.Status);
        Assert.False(result.ReportWritten);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("receipt-directory", "artifact-input-invalid", "artifact-verification")]
    [InlineData("repository-root", "artifact-output-failed", "source-validation")]
    public async Task MissingReceiptDirectoryAndRepositoryRootFailAtTheirOwnBoundaries(
        string scenario, string expectedCode, string expectedStage)
    {
        // protects: receipt I/O and initial checkout validation retain distinct safe failure boundaries.
        // fails_when: either missing input reaches HTTP, installs output, or is reported at the other boundary.
        // no_claim: a missing checkout exercises local process startup, not Git availability or repository state.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var repositoryRoot = _root;
        var sourceCalls = 0;
        TailwindArtifactTransportWorkflow workflow;
        if (scenario == "receipt-directory")
        {
            receipt = Path.Combine(_root, "missing-receipt-directory", "receipt.json");
            workflow = new TailwindArtifactTransportWorkflow(verifier,
                sourceIdentity: (_, _, _) => { sourceCalls++; return Task.CompletedTask; });
        }
        else
        {
            repositoryRoot = Path.Combine(_root, "missing-repository-root");
            workflow = new TailwindArtifactTransportWorkflow(verifier);
        }
        using (workflow)
        {
            var work = Path.Combine(_root, "work-missing-" + scenario);
            var output = Path.Combine(_root, "output-missing-" + scenario);
            var report = Path.Combine(_root, "report-missing-" + scenario);
            var request = Request(_expected, receipt, fixture.SubjectHash, work, output, report)
                with
            { RepositoryRoot = repositoryRoot };

            var result = await workflow.RunAsync(request);

            Assert.Equal(expectedCode, result.Code);
            Assert.Empty(handler.Requests);
            Assert.False(Directory.Exists(output));
            AssertFailureReport(result, report, expectedStage);
            if (scenario == "receipt-directory")
            {
                Assert.Equal(1, sourceCalls);
                using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
                Assert.Contains("cause=io",
                    diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(0, sourceCalls);
                await AssertReportsRedactedAsync(report, [repositoryRoot]);
            }
        }
    }

    [Fact]
    public async Task InitialSourceValidationCancellationIsReportedBeforeReceiptOrHttpAccess()
    {
        // protects: caller cancellation during the first source check is reported at that boundary.
        // fails_when: cancellation is mislabeled, HTTP begins, or a bundle/success marker becomes eligible.
        // no_claim: the injected callback verifies cancellation handling, not Git discovery or timing.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var caller = new CancellationTokenSource();
        var sourceCalls = 0;
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) =>
            {
                sourceCalls++;
                caller.Cancel();
                throw new OperationCanceledException(caller.Token);
            });
        var work = Path.Combine(_root, "work-initial-source-cancel");
        var output = Path.Combine(_root, "output-initial-source-cancel");
        var report = Path.Combine(_root, "report-initial-source-cancel");

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, work, output, report), caller.Token);

        Assert.Equal("artifact-cancelled", result.Code);
        Assert.Equal("cancelled", result.Status);
        Assert.Equal(1, sourceCalls);
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(work));
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "source-validation");
    }

    [Fact]
    public void Constructor_RejectsEachNonpositiveInternalResourceLimit()
    {
        // protects: every injected resource bound must remain strictly positive before a workflow can run.
        // fails_when: a zero entry, byte, or time limit creates a usable workflow.
        // no_claim: test-only limits do not alter or benchmark the fixed production defaults.
        var invalidLimits = new[]
        {
            new TailwindArtifactTransportLimits(0, 1, TimeSpan.FromSeconds(1)),
            new TailwindArtifactTransportLimits(1, 0, TimeSpan.FromSeconds(1)),
            new TailwindArtifactTransportLimits(1, 1, TimeSpan.Zero)
        };
        using var verifier = new GitHubActionsArtifactVerifier(new FakeHandler([], ""));

        foreach (var limits in invalidLimits)
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask, limits: limits));
            Assert.Equal("limits", exception.ParamName);
        }
    }

    [Fact]
    public async Task DisposedDefaultWorkflowRejectsRunsAndDisposesIdempotently()
    {
        // protects: disposing the default verifier-owning workflow is idempotent and closes its run boundary.
        // fails_when: a disposed adapter accepts work or creates reports/output before rejecting it.
        // no_claim: no HTTP or external GitHub access is attempted by this disposed-workflow check.
        var workflow = new TailwindArtifactTransportWorkflow();
        workflow.Dispose();
        workflow.Dispose();
        var work = Path.Combine(_root, "work-disposed-default");
        var output = Path.Combine(_root, "output-disposed-default");
        var report = Path.Combine(_root, "report-disposed-default");
        var request = Request(_expected, Path.Combine(_root, "unused-receipt.json"), new string('0', 64), work, output, report);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => workflow.RunAsync(request));

        Assert.False(Directory.Exists(work));
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(report));
        Assert.False(File.Exists(Path.Combine(report, "verified-artifact.json")));
    }

    [Theory]
    [InlineData("repository", "forge-trust/Other", "artifact-receipt-binding-mismatch", "repository")]
    [InlineData("repositoryId", "999", "artifact-receipt-binding-mismatch", "repositoryId")]
    [InlineData("runId", "999", "artifact-receipt-binding-mismatch", "runId")]
    [InlineData("sourceCommit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "artifact-receipt-binding-mismatch", "sourceCommit")]
    [InlineData("artifactId", "999", "artifact-receipt-binding-mismatch", "artifactId")]
    [InlineData("runAttempt", "2", "artifact-attempt-mismatch", "runAttempt")]
    [InlineData("producerJob", "producer-job-sentinel-910", "artifact-receipt-binding-mismatch", "workflowPolicy")]
    public async Task ReceiptIdentityConflictsReportOnlySafeBoundValues(
        string field, string observed, string expectedCode, string diagnosticField)
    {
        // protects: receipt identity conflicts retain their exact refusal while diagnostics expose only safe values.
        // fails_when: a conflict reaches HTTP or an untrusted policy value is copied into the report.
        // no_claim: matching local receipt data does not prove hosted producer authority.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var receiptNode = JsonNode.Parse(await File.ReadAllTextAsync(receipt))!.AsObject();
        receiptNode[field] = JsonValue.Create(observed);
        await File.WriteAllTextAsync(receipt, receiptNode.ToJsonString());
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var report = Path.Combine(_root, "report-receipt-conflict-" + field);
        var output = Path.Combine(_root, "output-receipt-conflict-" + field);
        var expected = field switch
        {
            "repository" => Repository,
            "repositoryId" => RepositoryId,
            "runId" => RunId,
            "sourceCommit" => Source,
            "artifactId" => ArtifactId,
            "runAttempt" => Attempt,
            _ => null
        };

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-receipt-conflict-" + field), output, report));

        Assert.Equal(expectedCode, result.Code);
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "artifact-verification");
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
        var error = diagnostics.RootElement.GetProperty("errors")[0];
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains($"field={diagnosticField}", message, StringComparison.Ordinal);
        if (field == "producerJob")
        {
            Assert.Equal(JsonValueKind.Null, error.GetProperty("expected").ValueKind);
            Assert.Equal(JsonValueKind.Null, error.GetProperty("observed").ValueKind);
        }
        else
        {
            Assert.Equal(expected, error.GetProperty("expected").GetString());
            Assert.Equal(observed, error.GetProperty("observed").GetString());
            Assert.Contains($"expected={expected}", message, StringComparison.Ordinal);
            Assert.Contains($"observed={observed}", message, StringComparison.Ordinal);
        }
        var sentinels = new List<string> { Token };
        if (field == "producerJob") sentinels.Add(observed);
        await AssertReportsRedactedAsync(report, sentinels);
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("old-attempt")]
    [InlineData("changed-byte")]
    public async Task AcceptanceCard(string outcome)
    {
        // protects: the maintainer's credential-free three-outcome adapter card with actual subject/package checks.
        // fails_when: a matching bundle lacks a final marker, an old receipt sends HTTP, or changed bytes reach extraction.
        // no_claim: controlled HTTP/source seams do not prove producer authority, hosted token capabilities, or a hosted run.
        var fixture = await CreateFixtureAsync(Attempt);
        var receiptAttempt = outcome == "old-attempt" ? "1" : Attempt;
        var expected = outcome == "old-attempt" ? _expected with { RunAttempt = "2" } : _expected;
        var bytes = (byte[])fixture.ZipBytes.Clone();
        if (outcome == "changed-byte") bytes[bytes.Length / 2] ^= 0x01;
        var http = new FakeHandler(bytes, fixture.ApiDigest);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, receiptAttempt);
        var report = Path.Combine(_root, "report-" + outcome);
        var work = Path.Combine(_root, "work-" + outcome);
        var output = Path.Combine(_root, "output-" + outcome);
        var sourceCalls = 0;
        using var verifier = new GitHubActionsArtifactVerifier(http);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => { sourceCalls++; return Task.CompletedTask; });
        var request = Request(expected, receipt, fixture.SubjectHash, work, output, report);

        var result = await workflow.RunAsync(request);

        if (outcome == "matching")
        {
            Assert.True(result.Succeeded);
            var markerPath = Path.Combine(report, "verified-artifact.json");
            Assert.True(File.Exists(markerPath));
            using var marker = JsonDocument.Parse(await File.ReadAllBytesAsync(markerPath));
            Assert.Equal("appsurface-github-actions-verified-artifact-v1", marker.RootElement.GetProperty("schema").GetString());
            Assert.Equal("succeeded", marker.RootElement.GetProperty("status").GetString());
            Assert.False(marker.RootElement.GetProperty("releaseEligible").GetBoolean());
            Assert.Equal(fixture.ApiDigest, marker.RootElement.GetProperty("archiveSha256").GetString());
            Assert.Equal(fixture.ZipBytes.Length, marker.RootElement.GetProperty("archiveBytes").GetInt64());
            using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
            Assert.Equal("succeeded", diagnostics.RootElement.GetProperty("status").GetString());
            Assert.Equal("artifact-transport", diagnostics.RootElement.GetProperty("stage").GetString());
            Assert.Contains("Phase: `complete`", await File.ReadAllTextAsync(Path.Combine(report, "summary.md")), StringComparison.Ordinal);
            Assert.True(File.Exists(TestPathUtils.PathUnder(output, TailwindProofSubjectService.FileName)));
            Assert.False(Directory.Exists(work));
            Assert.Equal(2, sourceCalls); // early checkout and the validator's source boundary
            Assert.Equal(3, http.Requests.Count);
            Assert.DoesNotContain(Token, request.ToString(), StringComparison.Ordinal);
        }
        else if (outcome == "old-attempt")
        {
            Assert.False(result.Succeeded);
            Assert.Equal("artifact-attempt-mismatch", result.Code);
            Assert.Empty(http.Requests);
            Assert.False(File.Exists(Path.Combine(report, "verified-artifact.json")));
            Assert.False(Directory.Exists(output));
            AssertFailureReport(result, report, "artifact-verification");
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Equal("artifact-bytes-digest-mismatch", result.Code);
            Assert.Equal(3, http.Requests.Count);
            Assert.False(Directory.Exists(Path.Combine(work, "bundle-stage")));
            Assert.False(Directory.Exists(output));
            Assert.False(File.Exists(Path.Combine(report, "verified-artifact.json")));
            AssertFailureReport(result, report, "artifact-verification");
        }
    }

    [Theory]
    [InlineData("../escape", "artifact-archive-unsafe")]
    [InlineData("a//b", "artifact-archive-unsafe")]
    [InlineData("a/../b", "artifact-archive-unsafe")]
    [InlineData("/rooted.txt", "artifact-archive-unsafe")]
    [InlineData("a\\backslash.txt", "artifact-archive-unsafe")]
    [InlineData("a:b.txt", "artifact-archive-unsafe")]
    [InlineData("a\0b.txt", "artifact-archive-unsafe")]
    [InlineData("a/./b.txt", "artifact-archive-unsafe")]
    [InlineData("trailing. ", "artifact-archive-unsafe")]
    [InlineData("NUL", "artifact-archive-unsafe")]
    [InlineData("CON.txt", "artifact-archive-unsafe")]
    [InlineData("AUX", "artifact-archive-unsafe")]
    [InlineData("PRN.log", "artifact-archive-unsafe")]
    [InlineData("COM1", "artifact-archive-unsafe")]
    [InlineData("LPT9.txt", "artifact-archive-unsafe")]
    [InlineData("trailing.", "artifact-archive-unsafe")]
    [InlineData("trailing ", "artifact-archive-unsafe")]
    [InlineData("a/line\nfeed", "artifact-archive-unsafe")]
    [InlineData("a/wild*card", "artifact-archive-unsafe")]
    [InlineData("NFC-e\u0301.txt", "artifact-archive-unsafe")]
    [InlineData("case-alias", "artifact-archive-unsafe")]
    public async Task Extractor_RejectsPortableAliasesAndTraversal(string entryName, string expectedCode)
    {
        // protects: canonical portable paths reject traversal, reserved names, Unicode aliases and case collisions.
        // fails_when: an unsafe archive member is normalized or written outside owned staging.
        // no_claim: this is the defined extraction policy, not a complete ZIP parser or hostile concurrent filesystem proof.
        var entries = entryName == "case-alias"
            ? new[] { ("Case.txt", "first"), ("case.txt", "second") }
            : new[] { (entryName, "x") };
        var fixture = await CreateFixtureAsync(Attempt, extraEntries: entries);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, Path.Combine(_root, "work-alias"),
            Path.Combine(_root, "output-alias"), Path.Combine(_root, "report-alias")));
        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Code);
        AssertFailureReport(result, Path.Combine(_root, "report-alias"), "extraction");
        Assert.Empty(Directory.Exists(Path.Combine(_root, "outside")) ? Directory.EnumerateFileSystemEntries(Path.Combine(_root, "outside")) : []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Extractor_AllowsInferredParentFollowedByExplicitDirectory(bool unixDirectoryMode)
    {
        // protects: ZIPs may list files before a corresponding explicit parent directory.
        // fails_when: a valid explicit directory marker is mistaken for a duplicate inferred node.
        // no_claim: only the documented normalized directory-entry case is asserted.
        var fixture = await CreateFixtureAsync(Attempt, extraEntries: [("z-parent/child.txt", "ok"), ("z-parent/", "")]);
        if (unixDirectoryMode)
        {
            using var bytes = new MemoryStream();
            bytes.Write(fixture.ZipBytes);
            bytes.Position = 0;
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Update, leaveOpen: true))
                archive.GetEntry("z-parent/")!.ExternalAttributes = 0x40000010;
            fixture = WithArchive(fixture, bytes.ToArray());
        }
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, Path.Combine(_root, "work-dir"),
            Path.Combine(_root, "output-dir"), Path.Combine(_root, "report-dir")));
        Assert.True(result.Succeeded);
        Assert.Equal("ok", await File.ReadAllTextAsync(Path.Combine(_root, "output-dir", "z-parent", "child.txt")));
    }

    [Fact]
    public async Task ArchivePreflight_AcceptsValidZip64Envelope()
    {
        // protects: a valid ZIP64 envelope reaches the same real content validation and final-marker path as standard ZIP.
        // fails_when: ZIP64 metadata is rejected or the verified package is installed without its success marker.
        // no_claim: this fixture covers the supported single-volume ZIP64 envelope, not every ZIP64 extension.
        var baseline = await CreateFixtureAsync(Attempt);
        var fixture = WithArchive(baseline, AddZip64Envelope(baseline.ZipBytes));
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var report = Path.Combine(_root, "report-zip64");
        var output = Path.Combine(_root, "output-zip64");
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-zip64"), output, report));

        Assert.True(result.Succeeded);
        Assert.Equal(3, handler.Requests.Count);
        Assert.True(File.Exists(TestPathUtils.PathUnder(output, TailwindProofSubjectService.FileName)));
        Assert.True(File.Exists(Path.Combine(report, "verified-artifact.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "work-zip64")));
    }

    [Theory]
    [InlineData("split")]
    [InlineData("truncated")]
    [InlineData("corrupt-offset")]
    [InlineData("false-count")]
    [InlineData("central-signature")]
    [InlineData("truncated-directory")]
    [InlineData("short-central-header")]
    [InlineData("comment-length-mismatch")]
    public async Task ArchivePreflight_RejectsSplitTruncatedAndCorruptEnvelopes(string scenario)
    {
        // protects: split, truncated and out-of-archive central-directory envelopes fail before ZIP entry extraction.
        // fails_when: malformed envelope metadata creates an installed bundle or success marker.
        // no_claim: these are representative envelope failures, not exhaustive ZIP parser conformance tests.
        var baseline = await CreateFixtureAsync(Attempt);
        var fixture = WithArchive(baseline, MutateZipEnvelope(baseline.ZipBytes, scenario));
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var report = Path.Combine(_root, "report-envelope-" + scenario);
        var output = Path.Combine(_root, "output-envelope-" + scenario);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-envelope-" + scenario), output, report));

        Assert.Equal("artifact-archive-unsafe", result.Code);
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "archive-preflight");
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
        var error = diagnostics.RootElement.GetProperty("errors")[0];
        Assert.Contains("operation=zip-envelope", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("#artifact-archive-unsafe", error.GetProperty("docsUrl").GetString(), StringComparison.Ordinal);
        Assert.NotNull(result.NextAction);
    }

    [Theory]
    [InlineData("locator")]
    [InlineData("record")]
    [InlineData("count")]
    [InlineData("offset")]
    [InlineData("locator-disk")]
    [InlineData("locator-count")]
    [InlineData("offset-too-large")]
    [InlineData("offset-distance")]
    [InlineData("record-disk")]
    [InlineData("record-disk-number")]
    [InlineData("record-size-minimum")]
    [InlineData("record-size-too-large")]
    public async Task ArchivePreflight_RejectsMalformedZip64AuthorityFields(string scenario)
    {
        // protects: ZIP64 locator, end-record, count, and directory-offset fields are checked before extraction.
        // fails_when: an internally inconsistent ZIP64 envelope reaches content validation or creates an output bundle.
        // no_claim: representative ZIP64 envelope failures do not establish conformance for every ZIP64 extension.
        var baseline = await CreateFixtureAsync(Attempt);
        var zip64 = AddZip64Envelope(baseline.ZipBytes);
        var fixture = WithArchive(baseline, MutateZip64Envelope(zip64, scenario));
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var report = Path.Combine(_root, "report-zip64-" + scenario);
        var output = Path.Combine(_root, "output-zip64-" + scenario);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-zip64-" + scenario), output, report));

        Assert.Equal("artifact-archive-unsafe", result.Code);
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "archive-preflight");
    }

    [Fact]
    public async Task ArchivePreflightWalksActualCentralHeadersBeforeBclEvenWhenDeclaredCountIsSmall()
    {
        // protects: false EOCD counts cannot hide actual central-directory entries from the injected entry bound.
        // fails_when: declared count one reaches ZipArchive despite multiple actual headers and a one-entry limit.
        // no_claim: controlled archive bytes verify preflight allocation bounds, not general ZIP interoperability.
        var baseline = await CreateFixtureAsync(Attempt);
        var fixture = WithArchive(baseline, MutateZipEnvelope(baseline.ZipBytes, "false-small-count"));
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) => Task.CompletedTask,
            limits: new TailwindArtifactTransportLimits(1, long.MaxValue, TimeSpan.FromMinutes(1)));
        var report = TestPathUtils.PathUnder(_root, "report-actual-central-count");
        var output = TestPathUtils.PathUnder(_root, "output-actual-central-count");

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            TestPathUtils.PathUnder(_root, "work-actual-central-count"), output, report));

        Assert.Equal("artifact-budget-exceeded", result.Code);
        AssertFailureReport(result, report, "archive-preflight");
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
        Assert.Contains("field=entry-count", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("file-first")]
    [InlineData("directory-first")]
    public async Task Extractor_RejectsFileDirectoryCollisionsInEitherOrder(string scenario)
    {
        // protects: a file cannot replace an inferred directory or become an ancestor of a later entry.
        // fails_when: entry ordering permits one path to be treated as both a file and a directory.
        // no_claim: the test covers portable file/directory collisions, not concurrent filesystem replacement.
        var extraEntries = scenario == "file-first"
            ? new[] { ("collision-node", "file"), ("collision-node/child.txt", "child") }
            : new[] { ("collision-node/child.txt", "child"), ("collision-node", "file") };
        var fixture = await CreateFixtureAsync(Attempt, extraEntries);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var report = Path.Combine(_root, "report-file-directory-" + scenario);
        var output = Path.Combine(_root, "output-file-directory-" + scenario);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-file-directory-" + scenario), output, report));

        Assert.Equal("artifact-archive-unsafe", result.Code);
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "extraction");
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public async Task Extractor_EnforcesPortableEntryNameLengthBound(int nameLength, bool isAtLimit)
    {
        // protects: portable ZIP names allow exactly 4096 characters and reject the first over-limit value.
        // fails_when: over-limit names reach prefix creation, or the exact name is rejected as an archive-policy violation.
        // no_claim: an OS path-length refusal after accepting the exact portable name is not treated as ZIP-policy failure.
        var name = PortableEntryName(nameLength, 128);
        var fixture = await CreateFixtureAsync(Attempt, [(name, "x")]);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var report = Path.Combine(_root, "report-name-bound-" + nameLength);
        var output = Path.Combine(_root, "output-name-bound-" + nameLength);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-name-bound-" + nameLength), output, report));

        if (!isAtLimit)
        {
            Assert.Equal("artifact-archive-unsafe", result.Code);
            AssertFailureReport(result, report, "extraction");
            using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
            Assert.Contains("field=entry-name", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        else if (!result.Succeeded)
        {
            Assert.Equal("artifact-output-failed", result.Code);
            AssertFailureReport(result, report, "extraction");
        }
        else
        {
            Assert.True(File.Exists(Path.Combine(report, "verified-artifact.json")));
            Assert.True(Directory.Exists(output));
        }
    }

    [Theory]
    [InlineData("wrong-subject-hash")]
    [InlineData("corrupt-subject")]
    [InlineData("corrupt-manifest")]
    [InlineData("corrupt-package")]
    public async Task Adapter_RejectsRealProducerBindingFailuresSafely(string scenario)
    {
        // protects: valid outer ZIP/API/receipt digests do not bypass the real subject, manifest and package validator.
        // fails_when: incorrect content binding installs a bundle, writes the final marker, or leaks untrusted content.
        // no_claim: controlled source identity and API fixtures do not prove hosted producer authority.
        var fixture = await CreateFixtureAsync(Attempt);
        if (scenario == "corrupt-subject")
            fixture = WithArchive(fixture, ReplaceZipEntry(fixture.ZipBytes, TailwindProofSubjectService.FileName,
                Encoding.UTF8.GetBytes("binding-corruption-sentinel-910 subject")));
        else if (scenario == "corrupt-manifest")
            fixture = WithArchive(fixture, ReplaceZipEntry(fixture.ZipBytes, "package-artifact-manifest.json",
                Encoding.UTF8.GetBytes("binding-corruption-sentinel-910 manifest")));
        else if (scenario == "corrupt-package")
            fixture = WithArchive(fixture, ReplaceZipEntry(fixture.ZipBytes, $"{PackageId}.{Version}.nupkg",
                Encoding.UTF8.GetBytes("binding-corruption-sentinel-910 package")));

        var expectedSubjectHash = scenario == "wrong-subject-hash" ? new string('0', 64) : fixture.SubjectHash;
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var report = Path.Combine(_root, "report-binding-" + scenario);
        var output = Path.Combine(_root, "output-binding-" + scenario);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);

        var result = await workflow.RunAsync(Request(_expected, receipt, expectedSubjectHash,
            Path.Combine(_root, "work-binding-" + scenario), output, report));

        Assert.Equal("artifact-producer-binding-invalid", result.Code);
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "producer-binding");
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
        Assert.Contains("operation=producer-binding", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("#artifact-producer-binding-invalid", diagnostics.RootElement.GetProperty("errors")[0].GetProperty("docsUrl").GetString(), StringComparison.Ordinal);
        Assert.NotNull(result.NextAction);
        await AssertReportsRedactedAsync(report, [Token, "binding-corruption-sentinel-910"]);
    }

    [Fact]
    public async Task SubjectAttemptIsComparedAfterRealBindingWhenReceiptAndApiAreCurrent()
    {
        // protects: the subject carries a separate current-attempt check after real manifest/package validation.
        // fails_when: a matching receipt/API lets a prior-attempt subject reach installation.
        // no_claim: this does not prove who produced the signed-in workflow receipt.
        var fixture = await CreateFixtureAsync(Attempt);
        var expected = _expected with { RunAttempt = "2" };
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, "2");
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest, "2");
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var report = Path.Combine(_root, "report-subject-attempt");
        var output = Path.Combine(_root, "output-subject-attempt");
        var result = await workflow.RunAsync(Request(expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-subject-attempt"), output, report));
        Assert.Equal("artifact-attempt-mismatch", result.Code);
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "subject-validation");
    }

    [Fact]
    public async Task KnownSourceFailureIsMappedAtItsBoundaryAndRedacted()
    {
        // protects: known source-stamp refusals retain their source code without forwarding the exception message.
        // fails_when: a source refusal is reported as a remote archive fault or leaks local paths/secrets.
        // no_claim: injected source failure does not test Git itself or the production assembly stamp.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) => throw new PackageIndexException("source-token-sentinel /private/source-path"));
        var report = Path.Combine(_root, "report-source-failure");
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-source-failure"), Path.Combine(_root, "output-source-failure"), report));
        Assert.Equal("artifact-source-mismatch", result.Code);
        Assert.Empty(handler.Requests);
        AssertFailureReport(result, report, "source-validation");
        await AssertReportsRedactedAsync(report, ["source-token-sentinel", "/private/source-path", Token]);
    }

    [Theory]
    [InlineData("preexisting")]
    [InlineData("existing-file-target")]
    [InlineData("overlap")]
    [InlineData("duplicate-target")]
    [InlineData("watched-source")]
    [InlineData("exact-watched-source")]
    public async Task PathOwnershipRefusesUnsafeTargetsBeforeHttp(string scenario)
    {
        // protects: fresh disjoint outputs avoid overwriting evidence or making the stamped source dirty.
        // fails_when: transport starts or creates output parents for preexisting, overlapping or watched-source paths.
        // no_claim: a hostile concurrent path replacement after preflight is outside this check.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var work = Path.Combine(_root, "work-path-" + scenario);
        var report = Path.Combine(_root, "report-path-" + scenario);
        var output = Path.Combine(_root, "output-path-" + scenario);
        if (scenario == "preexisting")
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "keep.txt"), "preserve");
        }
        else if (scenario == "existing-file-target")
        {
            await File.WriteAllTextAsync(work, "preserve-file-target");
        }
        else if (scenario == "overlap") output = Path.Combine(work, "nested-output");
        else if (scenario == "duplicate-target") output = work;
        else if (scenario == "exact-watched-source") work = TestPathUtils.PathUnder(_root, "tools/ForgeTrust.AppSurface.PackageIndex");
        else work = Path.Combine(_root, "tools", "ForgeTrust.AppSurface.PackageIndex", "work");
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, work, output, report));
        Assert.Equal("artifact-input-invalid", result.Code);
        Assert.Empty(handler.Requests);
        if (scenario == "preexisting") Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(output, "keep.txt")));
        if (scenario == "existing-file-target") Assert.Equal("preserve-file-target", await File.ReadAllTextAsync(work));
        if (scenario == "watched-source") Assert.False(Directory.Exists(Path.Combine(_root, "tools")));
        if (scenario == "exact-watched-source") Assert.False(Directory.Exists(work));
        if (scenario == "duplicate-target") Assert.False(Directory.Exists(work));
    }

    [Theory]
    [InlineData("access", "access-denied")]
    [InlineData("directory-missing", "directory-missing")]
    [InlineData("file-missing", "file-missing")]
    [InlineData("io", "io-failure")]
    [InlineData("invalid-data", "invalid-data")]
    [InlineData("invalid-argument", "invalid-argument")]
    public async Task TypedSourceFailuresExposeOnlyAllowlistedCauseAndSafeReport(string scenario, string expectedCause)
    {
        // protects: typed local source failures map to stable safe cause names without serializing exception details.
        // fails_when: paths, messages, or exception types escape into diagnostics or transport reaches HTTP.
        // no_claim: injected exceptions verify adapter classification, not the operating system's source-check behavior.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var sourceCalls = 0;
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) =>
            {
                if (++sourceCalls == 2) throw SourceFailure(scenario);
                return Task.CompletedTask;
            });
        var report = TestPathUtils.PathUnder(_root, "report-typed-source-" + scenario);
        var work = TestPathUtils.PathUnder(_root, "work-typed-source-" + scenario);
        var output = TestPathUtils.PathUnder(_root, "output-typed-source-" + scenario);

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, work, output, report));

        Assert.Equal("artifact-source-mismatch", result.Code);
        Assert.Equal(2, sourceCalls);
        AssertFailureReport(result, report, "producer-binding");
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(Directory.Exists(work));
        Assert.False(Directory.Exists(output));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
        var message = diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString()!;
        Assert.Contains("cause=" + expectedCause, message, StringComparison.Ordinal);
        await AssertReportsRedactedAsync(report, ["typed-source-sentinel", "/private/source-path", Token]);
    }

    [Fact]
    public async Task TypedVerifierStatusIsPreservedWithoutLeakingResponseBody()
    {
        // protects: a typed verifier refusal retains its safe HTTP status while API response text stays private.
        // fails_when: the status is lost, response/token data enters reports, or refused work leaves eligible output.
        // no_claim: a controlled 403 validates adapter reporting, not GitHub permissions or token capability.
        var receipt = await WriteReceiptAsync("sha256:" + new string('a', 64), Attempt);
        var handler = new RefusalHandler(HttpStatusCode.Forbidden, "response-body-sentinel token-sentinel-910");
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) => Task.CompletedTask);
        var report = TestPathUtils.PathUnder(_root, "report-verifier-status");
        var work = TestPathUtils.PathUnder(_root, "work-verifier-status");
        var output = TestPathUtils.PathUnder(_root, "output-verifier-status");

        var result = await workflow.RunAsync(Request(_expected, receipt, new string('0', 64), work, output, report));

        Assert.Equal("artifact-permission-denied", result.Code);
        AssertFailureReport(result, report, "artifact-verification");
        Assert.Equal(1, handler.RequestCount);
        Assert.False(Directory.Exists(work));
        Assert.False(Directory.Exists(output));
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
        var message = diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString()!;
        Assert.Contains("httpStatus=403", message, StringComparison.Ordinal);
        await AssertReportsRedactedAsync(report, ["response-body-sentinel", Token]);
    }

    [Fact]
    public async Task UnexpectedWorkflowPathReportsFixedPolicyWithoutEchoingApiText()
    {
        // protects: a foreign workflow refuses with fixed policy context instead of raw API path text.
        // fails_when: a changed attempt workflow reaches archive download or contaminates the report.
        // no_claim: fixture responses do not establish hosted authority.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        using var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest, workflowPath: "api-workflow-secret-sentinel");
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var report = TestPathUtils.PathUnder(_root, "report-workflow-policy");
        var output = TestPathUtils.PathUnder(_root, "output-workflow-policy");

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            TestPathUtils.PathUnder(_root, "work-workflow-policy"), output, report));

        Assert.Equal("artifact-workflow-mismatch", result.Code);
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "artifact-verification");
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(TestPathUtils.PathUnder(report, "diagnostics.json")));
        var error = diagnostics.RootElement.GetProperty("errors")[0];
        Assert.Equal(".github/workflows/package-artifacts.yml", error.GetProperty("expected").GetString());
        Assert.Equal("unexpected", error.GetProperty("observed").GetString());
        await AssertReportsRedactedAsync(report, ["api-workflow-secret-sentinel", Token]);
    }

    [Theory]
    [InlineData("COM!.txt")]
    [InlineData("LPTX.txt")]
    public async Task DeviceLikeOrdinaryNamesRemainValid(string name)
    {
        // protects: ordinary prefix matches remain valid under the portable reserved-device rule.
        // fails_when: lexical device-prefix matching blocks a valid producer bundle.
        // no_claim: these legal names do not cover every platform filesystem convention.
        var fixture = await CreateFixtureAsync(Attempt, extraEntries: [(name, "ordinary")]);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        using var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var output = TestPathUtils.PathUnder(_root, "output-device-prefix");
        var report = TestPathUtils.PathUnder(_root, "report-device-prefix");

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            TestPathUtils.PathUnder(_root, "work-device-prefix"), output, report));

        Assert.True(result.Succeeded);
        Assert.Equal("ordinary", await File.ReadAllTextAsync(TestPathUtils.PathUnder(output, name)));
        Assert.True(File.Exists(TestPathUtils.PathUnder(report, "verified-artifact.json")));
    }

    [Fact]
    public async Task MalformedReceiptIdentityIsRejectedWithoutEchoingValue()
    {
        // protects: malformed receipt identity is refused before binding or HTTP without copying the value into reports.
        // fails_when: invalid authority reaches the API or report JSON echoes the noncanonical input.
        // no_claim: this local schema refusal does not establish protected receipt or remote artifact authority.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var receiptNode = JsonNode.Parse(await File.ReadAllTextAsync(receipt))!.AsObject();
        receiptNode["runId"] = JsonValue.Create("response-secret-sentinel");
        await File.WriteAllTextAsync(receipt, receiptNode.ToJsonString());
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) => Task.CompletedTask);
        var report = TestPathUtils.PathUnder(_root, "report-typed-identity-redaction");
        var output = TestPathUtils.PathUnder(_root, "output-typed-identity-redaction");

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            TestPathUtils.PathUnder(_root, "work-typed-identity-redaction"), output, report));

        Assert.Equal("artifact-receipt-invalid", result.Code);
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "artifact-verification");
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
        var error = diagnostics.RootElement.GetProperty("errors")[0];
        Assert.Equal(JsonValueKind.Null, error.GetProperty("expected").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("observed").ValueKind);
        await AssertReportsRedactedAsync(report, ["response-secret-sentinel", Token]);
    }

    [Fact]
    public async Task OuterDeadlineAfterInitialSourceCallbackIsReportedAtThatBoundary()
    {
        // protects: expiry immediately after successful initial source validation uses the shared deadline classification.
        // fails_when: expired work reaches verifier HTTP or is confused with caller cancellation.
        // no_claim: ManualTimeProvider deterministically exercises the adapter boundary, not scheduler timing.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var clock = new ManualTimeProvider();
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler, clock);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, clock,
            sourceIdentity: (_, _, _) =>
            {
                clock.Advance(TimeSpan.FromMinutes(2));
                return Task.CompletedTask;
            }, limits: new TailwindArtifactTransportLimits(100, long.MaxValue, TimeSpan.FromMinutes(1)));
        var report = TestPathUtils.PathUnder(_root, "report-source-deadline");

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            TestPathUtils.PathUnder(_root, "work-source-deadline"),
            TestPathUtils.PathUnder(_root, "output-source-deadline"), report));

        Assert.Equal("artifact-budget-exceeded", result.Code);
        Assert.Empty(handler.Requests);
        AssertFailureReport(result, report, "source-validation");
    }

    private static Exception SourceFailure(string scenario)
    {
        const string message = "typed-source-sentinel /private/source-path";
        return scenario switch
        {
            "access" => new UnauthorizedAccessException(message),
            "directory-missing" => new DirectoryNotFoundException(message),
            "file-missing" => new FileNotFoundException(message),
            "io" => new IOException(message),
            "invalid-data" => new InvalidDataException(message),
            "invalid-argument" => new ArgumentException(message),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    [Theory]
    [InlineData("invalid-subject-hash")]
    [InlineData("receipt-file-overlap")]
    [InlineData("receipt-directory-overlap")]
    [InlineData("receipt-directory-child-overlap")]
    [InlineData("preexisting-work")]
    [InlineData("preexisting-report")]
    public async Task RequestAndReceiptOwnershipRefusalsPreserveExistingState(string scenario)
    {
        // protects: malformed request identity and receipt/output overlap refuse before HTTP or owned-path creation.
        // fails_when: validation downloads an artifact, overwrites a receipt, or removes preexisting caller state.
        // no_claim: these cases do not exercise concurrent replacement after path validation.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var work = Path.Combine(_root, "work-request-" + scenario);
        var output = Path.Combine(_root, "output-request-" + scenario);
        var report = Path.Combine(_root, "report-request-" + scenario);
        var expectedHash = fixture.SubjectHash;
        if (scenario == "invalid-subject-hash") expectedHash = "not-a-sha256";
        else if (scenario == "receipt-file-overlap") report = receipt;
        else if (scenario == "receipt-directory-overlap") report = Path.GetDirectoryName(receipt)!;
        else if (scenario == "receipt-directory-child-overlap")
            report = TestPathUtils.PathUnder(Path.GetDirectoryName(receipt)!, "transport-report");
        else
        {
            var existing = scenario == "preexisting-work" ? work : report;
            Directory.CreateDirectory(existing);
            await File.WriteAllTextAsync(Path.Combine(existing, "keep.txt"), "preserve");
        }

        var result = await workflow.RunAsync(Request(_expected, receipt, expectedHash, work, output, report));

        Assert.Equal("artifact-input-invalid", result.Code);
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(Path.Combine(report, "verified-artifact.json")));
        if (scenario == "preexisting-work")
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(work, "keep.txt")));
        if (scenario == "preexisting-report")
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(report, "keep.txt")));
        if (scenario == "receipt-directory-child-overlap")
        {
            Assert.False(result.ReportWritten);
            Assert.False(Directory.Exists(report));
        }
    }

    [Fact]
    public async Task OutputParentThatIsAFileRefusesAtInstallationAndCleansOwnedWork()
    {
        // protects: a regular file at the output parent boundary blocks installation without disturbing it.
        // fails_when: install overwrites the parent file, leaves work behind, or publishes a success marker.
        // no_claim: the fixture tests a stable file-vs-directory conflict, not a concurrent replacement race.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) => Task.CompletedTask);
        var parentFile = TestPathUtils.PathUnder(_root, "output-parent-file");
        await File.WriteAllTextAsync(parentFile, "keep-parent-file");
        var output = TestPathUtils.PathUnder(parentFile, "bundle");
        var report = TestPathUtils.PathUnder(_root, "report-parent-file");
        var work = TestPathUtils.PathUnder(_root, "work-parent-file");

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, work, output, report));

        Assert.Equal("artifact-input-invalid", result.Code);
        Assert.Equal(3, handler.Requests.Count);
        AssertFailureReport(result, report, "bundle-installation");
        using var diagnostics = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(report, "diagnostics.json")));
        Assert.Contains("field=output-parent-is-file",
            diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("keep-parent-file", await File.ReadAllTextAsync(parentFile));
        Assert.False(Directory.Exists(work));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("second-call-failure", "artifact-source-mismatch")]
    [InlineData("second-call-cancellation", "artifact-cancelled")]
    public async Task SourceRecheckFailureOrCancellationPreventsBundleInstallation(string scenario, string expectedCode)
    {
        // protects: the source identity is rechecked at the real producer-binding boundary before installation.
        // fails_when: a failed or cancelled second source check still installs a bundle or success marker.
        // no_claim: injected source outcomes do not test Git state discovery or external cancellation timing.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var caller = new CancellationTokenSource();
        var sourceCalls = 0;
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) =>
            {
                sourceCalls++;
                if (sourceCalls == 2 && scenario == "second-call-failure")
                    throw new PackageIndexException("second-source-sentinel /private/source-path");
                if (sourceCalls == 2)
                {
                    caller.Cancel();
                    throw new OperationCanceledException(caller.Token);
                }
                return Task.CompletedTask;
            });
        var report = Path.Combine(_root, "report-source-recheck-" + scenario);
        var output = Path.Combine(_root, "output-source-recheck-" + scenario);

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-source-recheck-" + scenario), output, report), caller.Token);

        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(2, sourceCalls);
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(Directory.Exists(output));
        AssertFailureReport(result, report, "producer-binding");
        if (scenario == "second-call-failure")
            await AssertReportsRedactedAsync(report, ["second-source-sentinel", "/private/source-path"]);
    }

    [Theory]
    [InlineData("report-directory-create", false, 0)]
    [InlineData("output-parent-create", true, 3)]
    [InlineData("success-diagnostics-write", false, 3)]
    [InlineData("success-summary-write", false, 3)]
    public async Task OwnedFilesystemFailuresWithholdSuccessAndPreserveFirstFailure(string scenario, bool reportWritten, int requestCount)
    {
        // protects: directory and success-report I/O failures cannot leave an installed bundle eligible for consumption.
        // fails_when: an output/report write failure publishes the marker, retains partial bundle output, or changes its safe code.
        // no_claim: injected filesystem failures do not model power loss or process termination between writes.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var report = Path.Combine(_root, "report-fs-" + scenario);
        var outputParent = Path.Combine(_root, "output-parent-fs-" + scenario);
        var output = Path.Combine(outputParent, "bundle");
        var fs = TailwindArtifactTransportFileSystem.Real with
        {
            CreateDirectory = path =>
            {
                if ((scenario == "report-directory-create" && path == report)
                    || (scenario == "output-parent-create" && path == outputParent))
                    throw new IOException("create-sentinel");
                Directory.CreateDirectory(path);
            },
            WriteJson = (path, value, token) => scenario == "success-diagnostics-write"
                && path.EndsWith("diagnostics.json", StringComparison.Ordinal)
                    ? throw new IOException("diagnostics-sentinel")
                    : TailwindArtifactTransportFileSystem.Real.WriteJson(path, value, token),
            WriteText = (path, value, token) => scenario == "success-summary-write"
                ? throw new IOException("summary-sentinel")
                : TailwindArtifactTransportFileSystem.Real.WriteText(path, value, token)
        };
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) => Task.CompletedTask, fileSystem: fs);

        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-fs-" + scenario), output, report));

        Assert.Equal("artifact-output-failed", result.Code);
        Assert.Equal(reportWritten, result.ReportWritten);
        Assert.Equal(requestCount, handler.Requests.Count);
        Assert.False(Directory.Exists(output));
        Assert.False(File.Exists(Path.Combine(report, "verified-artifact.json")));
        if (reportWritten) AssertFailureReport(result, report, scenario == "output-parent-create" ? "bundle-installation" : "arguments");
    }

    [Fact]
    public async Task CheckoutRelativeArtifactsOutputIsPermittedAndExistingParentIsPreserved()
    {
        // protects: a dedicated checkout artifacts directory can contain the installed output.
        // fails_when: an existing safe parent is treated as invocation-owned or rejected as stale output.
        // no_claim: the controlled source check does not establish production source cleanliness.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var artifacts = Path.Combine(_root, "artifacts");
        Directory.CreateDirectory(artifacts);
        var output = Path.Combine(artifacts, "verified-bundle");
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, Path.Combine(_root, "work-checkout-artifacts"),
            output, Path.Combine(_root, "report-checkout-artifacts")));
        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(artifacts));
        Assert.True(Directory.Exists(output));
    }

    [Theory]
    [InlineData("exact-entry-count")]
    [InlineData("over-entry-count")]
    [InlineData("exact-expanded-bytes")]
    [InlineData("over-expanded-bytes")]
    public async Task TestLimitsExerciseExactAndOverBudgetBranches(string boundary)
    {
        // protects: actual entry and expanded-byte counters enforce exact and over limits using the production path.
        // fails_when: declared metadata or inferred parent count substitutes for actual entries/bytes.
        // no_claim: injected small limits do not change or benchmark production defaults.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        using var archive = new ZipArchive(new MemoryStream(fixture.ZipBytes), ZipArchiveMode.Read);
        var entryCount = archive.Entries.Count;
        var expandedBytes = archive.Entries.Sum(item => item.Length);
        var limits = boundary switch
        {
            "exact-entry-count" => new TailwindArtifactTransportLimits(entryCount, long.MaxValue, TimeSpan.FromMinutes(1)),
            "over-entry-count" => new TailwindArtifactTransportLimits(entryCount - 1, long.MaxValue, TimeSpan.FromMinutes(1)),
            "exact-expanded-bytes" => new TailwindArtifactTransportLimits(entryCount, expandedBytes, TimeSpan.FromMinutes(1)),
            _ => new TailwindArtifactTransportLimits(entryCount, expandedBytes - 1, TimeSpan.FromMinutes(1))
        };
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) => Task.CompletedTask, limits: limits);
        var report = Path.Combine(_root, "report-limit-" + boundary);
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-limit-" + boundary), Path.Combine(_root, "output-limit-" + boundary), report));
        if (boundary.StartsWith("exact", StringComparison.Ordinal)) Assert.True(result.Succeeded);
        else
        {
            Assert.Equal("artifact-budget-exceeded", result.Code);
            AssertFailureReport(result, report, boundary.Contains("entry", StringComparison.Ordinal) ? "archive-preflight" : "extraction");
        }
    }

    [Fact]
    public async Task OuterDeadlineBeforeArchivePreflightIsNotCallerCancellation()
    {
        // protects: adapter deadline spans post-download extraction and has its own refusal classification.
        // fails_when: work continues after the shared deadline or is mislabeled as caller cancellation.
        // no_claim: fake time validates the cancellation boundary, not scheduler precision.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var clock = new ManualTimeProvider();
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var fs = TailwindArtifactTransportFileSystem.Real with
        {
            CreateDirectory = path =>
            {
                Directory.CreateDirectory(path);
                if (path.EndsWith("bundle-stage", StringComparison.Ordinal)) clock.Advance(TimeSpan.FromMinutes(2));
            }
        };
        using var verifier = new GitHubActionsArtifactVerifier(handler, clock);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, clock,
            sourceIdentity: (_, _, _) => Task.CompletedTask, fileSystem: fs,
            limits: new TailwindArtifactTransportLimits(100, long.MaxValue, TimeSpan.FromMinutes(1)));
        var report = Path.Combine(_root, "report-deadline");
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-deadline"), Path.Combine(_root, "output-deadline"), report));
        Assert.Equal("artifact-budget-exceeded", result.Code);
        AssertFailureReport(result, report, "archive-preflight");
    }

    [Fact]
    public async Task OuterDeadlineDuringHttpOverridesCoreLinkedTokenCancellation()
    {
        // protects: the adapter's own overall deadline is not misreported as caller cancellation by the core.
        // fails_when: a linked deadline cancellation escapes as artifact-cancelled during an HTTP request.
        // no_claim: fake time validates the classification boundary, not network timeout precision.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var clock = new ManualTimeProvider();
        var handler = new DeadlineHandler(fixture.ZipBytes, fixture.ApiDigest, clock, raceCaller: false);
        using var verifier = new GitHubActionsArtifactVerifier(handler, clock);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, clock,
            sourceIdentity: (_, _, _) => Task.CompletedTask,
            limits: new TailwindArtifactTransportLimits(100, long.MaxValue, TimeSpan.FromMinutes(1)));
        var report = Path.Combine(_root, "report-http-deadline");
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-http-deadline"), Path.Combine(_root, "output-http-deadline"), report));
        Assert.Equal("artifact-budget-exceeded", result.Code);
        AssertFailureReport(result, report, "artifact-verification");
    }

    [Fact]
    public async Task CallerCancellationWinsWhenCallerAndOuterDeadlineRaceDuringHttp()
    {
        // protects: caller intent has precedence when caller and adapter deadline become cancelled together.
        // fails_when: the deadline masks an explicitly cancelled caller in the same HTTP boundary.
        // no_claim: controlled simultaneous cancellation does not establish scheduler ordering outside this seam.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var clock = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        var handler = new DeadlineHandler(fixture.ZipBytes, fixture.ApiDigest, clock, raceCaller: true, caller: caller);
        using var verifier = new GitHubActionsArtifactVerifier(handler, clock);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, clock,
            sourceIdentity: (_, _, _) => Task.CompletedTask,
            limits: new TailwindArtifactTransportLimits(100, long.MaxValue, TimeSpan.FromMinutes(1)));
        var report = Path.Combine(_root, "report-cancel-deadline-race");
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-cancel-deadline-race"), Path.Combine(_root, "output-cancel-deadline-race"), report), caller.Token);
        Assert.Equal("artifact-cancelled", result.Code);
        Assert.Equal("cancelled", result.Status);
        AssertFailureReport(result, report, "artifact-verification");
    }

    [Fact]
    public async Task Paths_RejectLinkedAncestorBeforeCreatingOutputParent()
    {
        // protects: no output parent is created through a linked ancestor.
        // fails_when: path validation follows a symlink and leaves outside state.
        // no_claim: filesystem race attacks after preflight are not separately authenticated.
        var outside = Path.Combine(_root, "link-target");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "linked");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (PlatformNotSupportedException) { return; }
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var output = Path.Combine(link, "new-parent", "bundle");
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, Path.Combine(_root, "work-link"), output,
            Path.Combine(_root, "report-link")));
        Assert.Equal("artifact-input-invalid", result.Code);
        Assert.False(Directory.Exists(Path.Combine(outside, "new-parent")));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnexpectedSourceFailureIsSafeAndContainsOnlyAllowlistedFrames()
    {
        // protects: unexpected local errors cannot leak exception text and carry only compact allowlisted frames.
        // fails_when: nested sentinels appear in diagnostics or local exception details become a source verdict.
        // no_claim: emitted-channel scanning is not an external secret scanner.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var report = Path.Combine(_root, "report-sentinel");
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier,
            sourceIdentity: (_, _, _) => throw new InvalidOperationException("raw-sentinel /private/user/path token-sentinel"));
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, Path.Combine(_root, "work-sentinel"),
            Path.Combine(_root, "output-sentinel"), report));
        Assert.Equal("artifact-output-failed", result.Code);
        Assert.True(result.ReportWritten);
        using var diagnostics = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(report, "diagnostics.json")));
        var message = diagnostics.RootElement.GetProperty("errors")[0].GetProperty("message").GetString()!;
        Assert.Contains("cause=unexpected-local", message, StringComparison.Ordinal);
        Assert.Contains("frames=TailwindArtifactTransportWorkflow.RunAsync -> TailwindArtifactTransportWorkflow", message, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(report, "*", SearchOption.AllDirectories))
        {
            var contents = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("raw-sentinel", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("/private/user/path", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("token-sentinel", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("signed?token=secret", contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReportFailureDoesNotReplaceFirstTypedRefusal()
    {
        // protects: failure while reporting keeps the first transport code and withholds ReportWritten.
        // fails_when: diagnostics failure replaces a digest refusal or suggests a report exists.
        // no_claim: process-fatal termination remains outside handled-output guarantees.
        var fixture = await CreateFixtureAsync(Attempt);
        var mutated = (byte[])fixture.ZipBytes.Clone();
        mutated[mutated.Length / 2] ^= 1;
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(mutated, fixture.ApiDigest);
        var fs = TailwindArtifactTransportFileSystem.Real with
        {
            WriteJson = (path, _, _) => path.EndsWith("diagnostics.json", StringComparison.Ordinal)
                ? throw new IOException("nested-sentinel token-sentinel")
                : throw new IOException("marker-sentinel")
        };
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask, fileSystem: fs);
        var report = Path.Combine(_root, "report-double-failure");
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, Path.Combine(_root, "work-double-failure"),
            Path.Combine(_root, "output-double-failure"), report));
        Assert.Equal("artifact-bytes-digest-mismatch", result.Code);
        Assert.False(result.ReportWritten);
        Assert.False(File.Exists(Path.Combine(report, "verified-artifact.json")));
    }

    [Theory]
    [InlineData("move", "artifact-output-failed")]
    [InlineData("scratch-delete", "artifact-output-failed")]
    [InlineData("marker", "artifact-output-failed")]
    public async Task OwnedOutputFailures_LeaveNoSuccessMarker(string injectedFailure, string expectedCode)
    {
        // protects: move, scratch removal and final marker failures never make the bundle eligible.
        // fails_when: a partial or failed invocation retains a usable success record.
        // no_claim: injected move failure does not establish operating-system cross-volume behavior.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        var report = Path.Combine(_root, "report-io-" + injectedFailure);
        var fs = TailwindArtifactTransportFileSystem.Real with
        {
            MoveDirectory = injectedFailure == "move" ? (_, _) => throw new IOException("move-sentinel") : TailwindArtifactTransportFileSystem.Real.MoveDirectory,
            DeleteDirectory = injectedFailure == "scratch-delete" ? (_, _) => throw new IOException("delete-sentinel") : TailwindArtifactTransportFileSystem.Real.DeleteDirectory,
            WriteJson = injectedFailure == "marker" ? (path, value, token) =>
                path.EndsWith("verified-artifact.json", StringComparison.Ordinal)
                    ? throw new IOException("marker-sentinel")
                    : TailwindArtifactTransportFileSystem.Real.WriteJson(path, value, token)
                : TailwindArtifactTransportFileSystem.Real.WriteJson
        };
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask, fileSystem: fs);
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, Path.Combine(_root, "work-io-" + injectedFailure),
            Path.Combine(_root, "output-io-" + injectedFailure), report));
        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Code);
        Assert.False(File.Exists(Path.Combine(report, "verified-artifact.json")));
    }

    [Fact]
    public async Task CallerCancellationIsDistinctFromAdapterDeadline()
    {
        // protects: caller intent is reported as cancellation while deadline exhaustion remains a budget refusal.
        // fails_when: either cancellation source is collapsed into an unrelated local or remote failure.
        // no_claim: this controlled cancellation does not benchmark deadline precision.
        var fixture = await CreateFixtureAsync(Attempt);
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        using var verifier = new GitHubActionsArtifactVerifier(new BlockingHandler());
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash, Path.Combine(_root, "work-cancel"),
            Path.Combine(_root, "output-cancel"), Path.Combine(_root, "report-cancel")), caller.Token);
        Assert.Equal("artifact-cancelled", result.Code);
        Assert.True(result.ReportWritten);
    }

    private TailwindArtifactTransportRequest Request(ArtifactExpectedContext expected, string receipt, string subjectHash,
        string work, string output, string report)
        => new(_root, expected, receipt, subjectHash, work, output, report, Token);

    [Theory]
    [InlineData("inferred-case")]
    [InlineData("explicit-case")]
    [InlineData("depth128")]
    [InlineData("depth129")]
    [InlineData("workflow-layout")]
    [InlineData("symlink")]
    [InlineData("dos-reparse")]
    [InlineData("compression")]
    public async Task ExtractionPolicyAndActualWorkflowLayoutHaveObservableEffects(string scenario)
    {
        // protects: path aliases, depth, entry types and the concrete workflow input/output layout.
        // fails_when: unsafe entries become eligible or the workflow's separated receipt directory refuses.
        // no_claim: local fixtures do not prove hosted token access or resource fit.
        var extra = scenario switch
        {
            "inferred-case" => new[] { ("Case/one", "a"), ("case/two", "b") },
            "explicit-case" => new[] { ("Case/one", "a"), ("case/", "") },
            "depth128" => new[] { (string.Join('/', Enumerable.Repeat("x", 128)), "a") },
            "depth129" => new[] { (string.Join('/', Enumerable.Repeat("x", 129)), "a") },
            _ => Array.Empty<(string, string)>()
        };
        var fixture = await CreateFixtureAsync(Attempt, extra);
        if (scenario is "symlink" or "dos-reparse" or "compression")
        {
            var bytes = (byte[])fixture.ZipBytes.Clone();
            var directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6, 4));
            if (scenario == "symlink") BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 38), 0xA1FF0000);
            else if (scenario == "dos-reparse") BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 38), (uint)FileAttributes.ReparsePoint);
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(directory + 10), 99);
                var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(directory + 42));
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(local + 8), 99);
            }
            fixture = WithArchive(fixture, bytes);
        }
        var receipt = await WriteReceiptAsync(fixture.ApiDigest, Attempt);
        if (scenario == "workflow-layout")
        {
            var directory = Path.Combine(_root, "producer-receipt");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "receipt.json");
            File.Copy(receipt, target);
            receipt = target;
        }
        using var handler = new FakeHandler(fixture.ZipBytes, fixture.ApiDigest);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var workflow = new TailwindArtifactTransportWorkflow(verifier, sourceIdentity: (_, _, _) => Task.CompletedTask);
        var output = Path.Combine(_root, "bundle-policy");
        var report = Path.Combine(_root, "report-policy");
        var result = await workflow.RunAsync(Request(_expected, receipt, fixture.SubjectHash,
            Path.Combine(_root, "work-policy"), output, report));
        if (scenario is "depth128" or "workflow-layout")
        {
            Assert.True(result.Succeeded);
            Assert.True(File.Exists(Path.Combine(report, "verified-artifact.json")));
            Assert.True(File.Exists(TestPathUtils.PathUnder(output, TailwindProofSubjectService.FileName)));
        }
        else
        {
            Assert.Equal("artifact-archive-unsafe", result.Code);
            Assert.False(Directory.Exists(output));
            AssertFailureReport(result, report, "extraction");
        }
    }

    private async Task<Fixture> CreateFixtureAsync(string attempt, (string Name, string Contents)[]? extraEntries = null)
    {
        var producer = Path.Combine(_root, "producer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(producer);
        var packageFile = $"{PackageId}.{Version}.nupkg";
        var packagePath = TestPathUtils.PathUnder(producer, packageFile);
        using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            Add(package, $"{PackageId}.nuspec", $"<package><metadata><id>{PackageId}</id><version>{Version}</version></metadata></package>");
            Add(package, "build/tailwind.release.json", ReleaseManifest());
            Add(package, "build/tailwind.version", "4.1.0");
            Add(package, "contentFiles/any/any/tailwind.css", ".fixture{color:red}");
        }
        var packageHash = PackageHash.ComputeSha512(packagePath);
        var manifestPath = Path.Combine(producer, "package-artifact-manifest.json");
        var manifest = new PackageArtifactManifest(1, Version, DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            [new PackageArtifactManifestEntry(PackageId, "Web/Tailwind.csproj", "publish", packageFile, packageHash, false)]);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, PackageArtifactJson.Options);
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);
        var subject = await TailwindProofSubjectService.CreateAsync(producer, manifestPath, RepositoryId, RunId, attempt,
            Source, [new TailwindSubjectPackage(PackageId, Version, packageFile, packageHash)], CancellationToken.None);
        var subjectHash = await TailwindProofSubjectService.WriteAsync(subject, producer, CancellationToken.None);
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(producer))
            {
                var entry = archive.CreateEntry(Path.GetFileName(file));
                await using var destination = entry.Open();
                await using var source = File.OpenRead(file);
                await source.CopyToAsync(destination);
            }
            foreach (var entry in extraEntries ?? []) Add(archive, entry.Name, entry.Contents);
        }
        var zipBytes = zip.ToArray();
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
        return new Fixture(zipBytes, digest, subjectHash);
    }

    private static Fixture WithArchive(Fixture fixture, byte[] bytes)
        => fixture with { ZipBytes = bytes, ApiDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };

    private static byte[] AddZip64Envelope(byte[] bytes)
    {
        var end = bytes.Length - 22;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10, 2));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 12, 4));
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16, 4));
        var result = new byte[bytes.Length + 76];
        bytes.AsSpan(0, end).CopyTo(result);
        var record = result.AsSpan(end, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record[4..], 44);
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(record[14..], 45);
        BinaryPrimitives.WriteUInt64LittleEndian(record[24..], count);
        BinaryPrimitives.WriteUInt64LittleEndian(record[32..], count);
        BinaryPrimitives.WriteUInt64LittleEndian(record[40..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(record[48..], offset);
        var locator = result.AsSpan(end + 56, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator[8..], (ulong)end);
        BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
        bytes.AsSpan(end, 22).CopyTo(result.AsSpan(end + 76));
        var eocd = result.AsSpan(end + 76, 22);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..], ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..], uint.MaxValue);
        return result;
    }

    private static byte[] MutateZipEnvelope(byte[] bytes, string scenario)
    {
        if (scenario == "truncated") return bytes[..^1];
        var result = (byte[])bytes.Clone();
        var end = result.Length - 22;
        if (scenario == "split") BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 4), 1);
        else if (scenario == "false-count")
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 10), 1);
        }
        else if (scenario == "central-signature")
        {
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(end + 16));
            result[offset] ^= 1;
        }
        else if (scenario == "truncated-directory")
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(end + 12));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 12), size - 1);
        }
        else if (scenario == "short-central-header")
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 12), 45);
        }
        else if (scenario == "comment-length-mismatch")
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 20), 1);
        }
        else if (scenario == "false-small-count")
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(end + 10), 1);
        }
        else BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 16), (uint)bytes.Length);
        return result;
    }

    private static byte[] MutateZip64Envelope(byte[] bytes, string scenario)
    {
        var result = (byte[])bytes.Clone();
        var end = result.Length - 22;
        var locator = end - 20;
        var recordOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(locator + 8, 8)));
        switch (scenario)
        {
            case "locator":
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(locator), 0);
                break;
            case "record":
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(recordOffset), 0);
                break;
            case "count":
                var count = BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(recordOffset + 32, 8));
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(recordOffset + 24, 8), count + 1);
                break;
            case "offset":
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(recordOffset + 48, 8), ulong.MaxValue);
                break;
            case "locator-disk":
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(locator + 4, 4), 1);
                break;
            case "locator-count":
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(locator + 16, 4), 2);
                break;
            case "offset-too-large":
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(locator + 8, 8), ulong.MaxValue);
                break;
            case "offset-distance":
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(locator + 8, 8), (ulong)(end + 1));
                break;
            case "record-disk":
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(recordOffset + 16, 4), 1);
                break;
            case "record-disk-number":
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(recordOffset + 20, 4), 1);
                break;
            case "record-size-minimum":
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(recordOffset + 4, 8), 43);
                break;
            case "record-size-too-large":
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(recordOffset + 4, 8), ulong.MaxValue);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return result;
    }

    private static string PortableEntryName(int length, int segments)
    {
        var names = Enumerable.Repeat("x", segments).ToArray();
        var extra = length - (segments * 2 - 1);
        for (var i = 0; extra > 0; i = (i + 1) % segments)
        {
            names[i] += "x";
            extra--;
        }
        return string.Join('/', names);
    }

    private static byte[] ReplaceZipEntry(byte[] bytes, string name, byte[] replacement)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var input = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            foreach (var entry in input.Entries)
            {
                using var destination = archive.CreateEntry(entry.FullName).Open();
                if (entry.FullName == name) destination.Write(replacement);
                else { using var source = entry.Open(); source.CopyTo(destination); }
            }
        }
        return output.ToArray();
    }

    private async Task<string> WriteReceiptAsync(string digest, string attempt)
    {
        var directory = Path.Combine(_root, "receipt-input");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "receipt-" + Guid.NewGuid().ToString("N") + ".json");
        var receipt = new
        {
            schema = "appsurface-github-actions-producer-receipt-v1",
            repository = Repository,
            repositoryId = RepositoryId,
            workflowPath = ".github/workflows/package-artifacts.yml",
            @event = "workflow_dispatch",
            producerJob = "package-artifacts",
            runId = RunId,
            runAttempt = attempt,
            sourceCommit = Source,
            artifactId = ArtifactId,
            archiveSha256 = digest
        };
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(receipt));
        return path;
    }

    private static string ReleaseManifest() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        version = "4.1.0",
        baseUrl = "https://example.test/tailwind",
        assets = new[]
        {
            new { rid = "linux-x64", binaryName = "tailwindcss-linux-x64", sha256 = new string('a', 64) },
            new { rid = "linux-arm64", binaryName = "tailwindcss-linux-arm64", sha256 = new string('b', 64) },
            new { rid = "osx-x64", binaryName = "tailwindcss-macos-x64", sha256 = new string('c', 64) },
            new { rid = "osx-arm64", binaryName = "tailwindcss-macos-arm64", sha256 = new string('d', 64) },
            new { rid = "win-x64", binaryName = "tailwindcss-windows-x64.exe", sha256 = new string('e', 64) }
        }
    });

    private static void Add(ZipArchive archive, string path, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record Fixture(byte[] ZipBytes, string ApiDigest, string SubjectHash);

    private static void AssertFailureReport(ArtifactTransportResult result, string report, string stage)
    {
        Assert.False(result.Succeeded);
        Assert.True(result.ReportWritten);
        using var diagnostics = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(report, "diagnostics.json")));
        Assert.Equal(result.Code == "artifact-cancelled" ? "cancelled" : "failed", diagnostics.RootElement.GetProperty("status").GetString());
        Assert.Equal("artifact-transport", diagnostics.RootElement.GetProperty("stage").GetString());
        Assert.Equal(result.Code, diagnostics.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(diagnostics.RootElement.GetProperty("releaseEligible").GetBoolean());
        Assert.True(File.Exists(Path.Combine(report, "summary.md")));
        Assert.Contains($"Phase: `{stage}`", File.ReadAllText(Path.Combine(report, "summary.md")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(report, "verified-artifact.json")));
    }

    private static async Task AssertReportsRedactedAsync(string report, IReadOnlyList<string> sentinels)
    {
        foreach (var file in Directory.EnumerateFiles(report, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file);
            foreach (var sentinel in sentinels) Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        }
    }

    private sealed class FakeHandler(byte[] archive, string digest, string attempt = Attempt,
        string workflowPath = ".github/workflows/package-artifacts.yml") : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = Requests.Count switch
            {
                1 => Json($"{{\"id\":{ArtifactId},\"expired\":false,\"expires_at\":\"2099-01-01T00:00:00Z\",\"digest\":\"{digest}\",\"workflow_run\":{{\"id\":{RunId},\"repository_id\":{RepositoryId},\"head_repository_id\":{RepositoryId},\"head_sha\":\"{Source}\"}}}}"),
                2 => Json($"{{\"id\":{RunId},\"run_attempt\":{attempt},\"repository\":{{\"id\":{RepositoryId}}},\"head_repository\":{{\"id\":{RepositoryId}}},\"head_sha\":\"{Source}\",\"path\":\"{workflowPath}\",\"event\":\"workflow_dispatch\"}}"),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class RefusalHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromCanceled<HttpResponseMessage>(cancellationToken);
    }

    private sealed class DeadlineHandler(byte[] archive, string digest, ManualTimeProvider clock, bool raceCaller,
        CancellationTokenSource? caller = null) : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests++;
            if (_requests == 3)
            {
                if (raceCaller) caller!.Cancel();
                clock.Advance(TimeSpan.FromMinutes(2));
                cancellationToken.ThrowIfCancellationRequested();
            }

            var content = _requests switch
            {
                1 => Json($"{{\"id\":{ArtifactId},\"expired\":false,\"expires_at\":\"2099-01-01T00:00:00Z\",\"digest\":\"{digest}\",\"workflow_run\":{{\"id\":{RunId},\"repository_id\":{RepositoryId},\"head_repository_id\":{RepositoryId},\"head_sha\":\"{Source}\"}}}}"),
                2 => Json($"{{\"id\":{RunId},\"run_attempt\":{Attempt},\"repository\":{{\"id\":{RepositoryId}}},\"head_repository\":{{\"id\":{RepositoryId}}},\"head_sha\":\"{Source}\",\"path\":\".github/workflows/package-artifacts.yml\",\"event\":\"workflow_dispatch\"}}"),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }
            };
            content.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(content);
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

}
