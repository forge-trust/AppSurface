using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.PackageIndex;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class ArtifactTransportBoundaryTests : IDisposable
{
    private const string Sentinel = "boundary-secret-sentinel";
    private const string Source = "10d375c6942fc650cc64f64e0748add77540d47c";
    private const string OtherSource = "20d375c6942fc650cc64f64e0748add77540d47c";
    private const string Digest = "sha256:27151d76524a22cd8126f5dc658295c16cf6d869930ccaf8a3f97d97e14be6fd";
    private static readonly ArtifactExpectedContext Expected = new(
        "forge-trust/AppSurface", "1035215851", "36547070332", "1", Source, "11023576342");
    private readonly string _root = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot,
        "artifact-transport-boundaries", Guid.NewGuid().ToString("N"));

    public ArtifactTransportBoundaryTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("repository", "another/repository", "artifact-receipt-binding-mismatch", "repository")]
    [InlineData("repositoryId", "1035215852", "artifact-receipt-binding-mismatch", "repositoryId")]
    [InlineData("runId", "36547070333", "artifact-receipt-binding-mismatch", "runId")]
    [InlineData("runAttempt", "2", "artifact-attempt-mismatch", "runAttempt")]
    [InlineData("sourceCommit", "20d375c6942fc650cc64f64e0748add77540d47c", "artifact-receipt-binding-mismatch", "sourceCommit")]
    [InlineData("artifactId", "11023576343", "artifact-receipt-binding-mismatch", "artifactId")]
    [InlineData("workflowPath", ".github/workflows/other.yml", "artifact-receipt-binding-mismatch", "workflowPolicy")]
    [InlineData("event", "push", "artifact-receipt-binding-mismatch", "workflowPolicy")]
    [InlineData("producerJob", "other-job", "artifact-receipt-binding-mismatch", "workflowPolicy")]
    public async Task ReceiptIdentityMismatch_IsTypedAndStopsBeforeHttp(string property, string replacement,
        string expectedCode, string expectedField)
    {
        // protects: each receipt identity and fixed workflow-policy field binds to independently supplied context.
        // fails_when: an internally consistent but unexpected receipt starts API access or selects another candidate.
        // no_claim: matching fixture fields do not authenticate the protected job that produced a real receipt.
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("unexpected HTTP"));
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync(ChangeReceipt(property, replacement));

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedField, error.Field);
        Assert.Empty(handler.Requests);
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("schema", "unsupported", "artifact-receipt-invalid")]
    [InlineData("repositoryId", "zero", "artifact-receipt-invalid")]
    [InlineData("runId", "zero", "artifact-receipt-invalid")]
    [InlineData("runAttempt", "zero", "artifact-receipt-invalid")]
    [InlineData("artifactId", "zero", "artifact-receipt-invalid")]
    [InlineData("repositoryId", "leading-zero", "artifact-receipt-invalid")]
    [InlineData("runId", "leading-zero", "artifact-receipt-invalid")]
    [InlineData("runAttempt", "leading-zero", "artifact-receipt-invalid")]
    [InlineData("artifactId", "leading-zero", "artifact-receipt-invalid")]
    [InlineData("sourceCommit", "uppercase", "artifact-receipt-invalid")]
    [InlineData("archiveSha256", "uppercase", "artifact-receipt-invalid")]
    [InlineData("repository", "repo-parent", "artifact-receipt-invalid")]
    [InlineData("repository", "repo-extra", "artifact-receipt-invalid")]
    [InlineData("repository", "repo-space", "artifact-receipt-invalid")]
    [InlineData("runAttempt", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("sourceCommit", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("schema", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("repository", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("repositoryId", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("workflowPath", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("event", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("producerJob", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("runId", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("artifactId", "wrong-type", "artifact-receipt-invalid")]
    [InlineData("archiveSha256", "wrong-type", "artifact-receipt-invalid")]
    public async Task ReceiptSchemaAndCanonicalValueFailures_RefuseBeforeHttp(string property, string mutation,
        string expectedCode)
    {
        // protects: exact receipt schema, JSON types, positive canonical decimal identities, and lowercase digests.
        // fails_when: malformed or noncanonical authority is interpreted as an identity and reaches GitHub.
        // no_claim: schema validity says nothing about whether the protected producer output is genuine.
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("unexpected HTTP"));
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync(MutateReceipt(property, mutation));

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt));

        Assert.Equal(expectedCode, error.Code);
        Assert.Empty(handler.Requests);
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("RepositoryId", "0")]
    [InlineData("RepositoryId", "01035215851")]
    [InlineData("RunId", "036547070332")]
    [InlineData("RunAttempt", "0")]
    [InlineData("RunAttempt", "01")]
    [InlineData("ArtifactId", "0")]
    [InlineData("ArtifactId", "011023576342")]
    [InlineData("SourceCommit", "10D375C6942FC650CC64F64E0748AD77540D47C")]
    public async Task ExpectedContextRequiresCanonicalPositiveIdentities(string property, string value)
    {
        // protects: caller context cannot acquire authority from zero, padded IDs, or noncanonical source spelling.
        // fails_when: invalid independent context reaches receipt parsing or any HTTP request.
        // no_claim: syntactic validity does not prove the supplied context came from GitHub Actions.
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("unexpected HTTP"));
        using var verifier = CreateVerifier(handler);
        var expected = Expected with { };
        expected = property switch
        {
            "RepositoryId" => expected with { RepositoryId = value },
            "RunId" => expected with { RunId = value },
            "RunAttempt" => expected with { RunAttempt = value },
            "ArtifactId" => expected with { ArtifactId = value },
            _ => expected with { SourceCommit = value }
        };
        var receipt = await WriteReceiptAsync();

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt, expected));

        Assert.Equal("artifact-input-invalid", error.Code);
        Assert.Empty(handler.Requests);
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("id", "identity", "artifact-api-response-invalid")]
    [InlineData("workflow_run.id", "identity", "artifact-run-mismatch")]
    [InlineData("workflow_run.repository_id", "identity", "artifact-repository-mismatch")]
    [InlineData("workflow_run.head_repository_id", "identity", "artifact-repository-mismatch")]
    [InlineData("workflow_run.head_sha", "identity", "artifact-source-mismatch")]
    [InlineData("id", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("expired", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("workflow_run.id", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("workflow_run.repository_id", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("workflow_run.head_repository_id", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("workflow_run.head_sha", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("expires_at", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("digest", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("expires_at", "missing", "artifact-api-response-invalid")]
    [InlineData("digest", "missing", "artifact-api-response-invalid")]
    [InlineData("id", "missing", "artifact-api-response-invalid")]
    [InlineData("expired", "missing", "artifact-api-response-invalid")]
    [InlineData("workflow_run", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("workflow_run", "missing", "artifact-api-response-invalid")]
    [InlineData("workflow_run.id", "missing", "artifact-api-response-invalid")]
    [InlineData("workflow_run.repository_id", "missing", "artifact-api-response-invalid")]
    [InlineData("workflow_run.head_repository_id", "missing", "artifact-api-response-invalid")]
    [InlineData("workflow_run.head_sha", "missing", "artifact-api-response-invalid")]
    [InlineData("workflow_run.head_sha", "uppercase", "artifact-api-response-invalid")]
    [InlineData("digest", "uppercase", "artifact-api-response-invalid")]
    [InlineData("expired", "true", "artifact-expired")]
    [InlineData("expires_at", "expired", "artifact-expired")]
    [InlineData("digest", "other-valid", "artifact-metadata-digest-mismatch")]
    public async Task ArtifactMetadataFieldMatrix_RefusesAtMetadataBoundary(string path, string mutation,
        string expectedCode)
    {
        // protects: each retained artifact identity, expiry, digest, field type and presence check has a distinct refusal.
        // fails_when: malformed or conflicting artifact metadata permits the attempt call or ZIP request.
        // no_claim: API metadata agreement does not independently authenticate a producer or bind its attempt.
        var handler = new RecordingHandler((_, _) => JsonResponse(MutateArtifact(path, mutation)));
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync();

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt));

        Assert.Equal(expectedCode, error.Code);
        Assert.Single(handler.Requests);
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Fact]
    public async Task ArtifactMetadataAllowsAdditivePropertiesAndRejectsDuplicateProperties()
    {
        // protects: API responses remain forward-compatible for additive fields while duplicate names remain ambiguous.
        // fails_when: additive metadata is rejected or duplicate identity properties get last-wins interpretation.
        // no_claim: additive-property tolerance is limited to API responses, not the exact producer receipt.
        var additiveHandler = new RecordingHandler((_, index) => index == 0
            ? JsonResponse(AddProperty(ArtifactJson(Expected), "future", "ignored"))
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var additiveVerifier = CreateVerifier(additiveHandler);
        var receipt = await WriteReceiptAsync();
        var additiveError = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(additiveVerifier, receipt));
        Assert.Equal("artifact-api-unavailable", additiveError.Code);
        Assert.Equal(2, additiveHandler.Requests.Count);
        AssertSafe(additiveError);

        var duplicateHandler = new RecordingHandler((_, _) => JsonResponse(
            ArtifactJson(Expected).Replace("\"expired\":false", "\"expired\":false,\"expired\":false", StringComparison.Ordinal)));
        using var duplicateVerifier = CreateVerifier(duplicateHandler);
        var duplicateError = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(duplicateVerifier, receipt));
        Assert.Equal("artifact-api-response-invalid", duplicateError.Code);
        Assert.Single(duplicateHandler.Requests);
        AssertNoOwnedZip(duplicateError);
        AssertSafe(duplicateError);
    }

    [Theory]
    [InlineData("artifact", "null", 1)]
    [InlineData("artifact", "[]", 1)]
    [InlineData("attempt", "null", 2)]
    [InlineData("attempt", "[]", 2)]
    public async Task MetadataJsonRequiresObjectRoots(string stage, string body, int expectedRequests)
    {
        // protects: parsed JSON scalars and arrays cannot be treated as metadata objects.
        // fails_when: a non-object response is accepted or advances to a later transport stage.
        // no_claim: object shape alone does not establish response authenticity.
        using var handler = new RecordingHandler((_, index) => index == 0 || stage == "attempt" && index == 1
            ? index == 0 && stage == "attempt" ? JsonResponse(ArtifactJson(Expected)) : JsonResponse(body)
            : throw new InvalidOperationException("unexpected request"));
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync();

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt));

        Assert.Equal("artifact-api-response-invalid", error.Code);
        Assert.Equal(expectedRequests, handler.Requests.Count);
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("workflow_run.head_sha", "invalid-hex")]
    [InlineData("digest", "invalid-hex")]
    [InlineData("workflow_run.head_sha", "short")]
    [InlineData("digest", "short")]
    public async Task ArtifactMetadataRejectsMalformedCanonicalHex(string path, string mutation)
    {
        // protects: source commits and SHA-256 text accept only canonical lowercase hexadecimal characters.
        // fails_when: correctly typed but malformed digest text passes its syntax boundary.
        // no_claim: syntax checks do not authenticate the API response.
        var handler = new RecordingHandler((_, _) => JsonResponse(MutateArtifact(path, mutation)));
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync();

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt));

        Assert.Equal("artifact-api-response-invalid", error.Code);
        Assert.Single(handler.Requests);
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("invalid-hex")]
    [InlineData("short")]
    public async Task AttemptMetadataRejectsMalformedSourceHex(string mutation)
    {
        // protects: a forty-character source field still must be lowercase hexadecimal.
        // fails_when: malformed source text reaches archive download or is reported as an identity mismatch.
        // no_claim: local syntax validation does not authenticate the attempt endpoint.
        var handler = new RecordingHandler((_, index) => index == 0
            ? JsonResponse(ArtifactJson(Expected))
            : JsonResponse(MutateAttempt(AttemptJson(Expected), "head_sha", mutation)));
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync();

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt));

        Assert.Equal("artifact-api-response-invalid", error.Code);
        Assert.Equal(2, handler.Requests.Count);
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("id", "36547070333", "artifact-run-mismatch")]
    [InlineData("run_attempt", "2", "artifact-attempt-mismatch")]
    [InlineData("repository.id", "1035215852", "artifact-repository-mismatch")]
    [InlineData("head_repository.id", "1035215852", "artifact-repository-mismatch")]
    [InlineData("head_sha", "20d375c6942fc650cc64f64e0748add77540d47c", "artifact-source-mismatch")]
    [InlineData("path", ".github/workflows/other.yml", "artifact-workflow-mismatch")]
    [InlineData("event", "push", "artifact-workflow-mismatch")]
    [InlineData("id", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("run_attempt", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("repository.id", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("head_repository.id", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("head_sha", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("path", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("event", "wrong-type", "artifact-api-response-invalid")]
    [InlineData("id", "missing", "artifact-api-response-invalid")]
    [InlineData("run_attempt", "missing", "artifact-api-response-invalid")]
    [InlineData("repository.id", "missing", "artifact-api-response-invalid")]
    [InlineData("head_repository.id", "missing", "artifact-api-response-invalid")]
    [InlineData("head_sha", "missing", "artifact-api-response-invalid")]
    [InlineData("path", "missing", "artifact-api-response-invalid")]
    [InlineData("event", "missing", "artifact-api-response-invalid")]
    public async Task AttemptMetadataFieldMatrix_RefusesBeforeArchiveRequest(string path, string mutation,
        string expectedCode)
    {
        // protects: every exact-attempt run, repository, head, source, workflow and event field is independently checked.
        // fails_when: an altered, missing, or wrongly typed attempt field permits the archive request.
        // no_claim: the attempt endpoint corroborates run context but does not associate artifact metadata with an attempt.
        var handler = new RecordingHandler((_, index) => index == 0
            ? JsonResponse(ArtifactJson(Expected))
            : JsonResponse(MutateAttempt(AttemptJson(Expected), path, mutation)));
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync();

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(2, handler.Requests.Count);
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Fact]
    public async Task AttemptMetadataAllowsAdditiveProperties()
    {
        // protects: API attempt responses tolerate future additive fields without relaxing required identity checks.
        // fails_when: a benign additive field is treated as a receipt-style schema extension error.
        // no_claim: tolerance does not make the endpoint an independent artifact-attempt binding.
        var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(Expected)),
            1 => JsonResponse(AddProperty(AttemptJson(Expected), "future", "ignored")),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt));
        Assert.Equal("artifact-api-unavailable", error.Code);
        Assert.Equal(3, handler.Requests.Count);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task RedirectedStorageHttpFailure_IsDownloadFailedWithoutRetryOrOwnedZip(int status)
    {
        // protects: failures after credential-free storage redirection are partitioned as transfer failures and never retried.
        // fails_when: storage responses are mistaken for GitHub API authority, retried, or leave a partial ZIP behind.
        // no_claim: fake storage responses do not establish signed-URL service availability or hosted permissions.
        var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(Expected)),
            1 => JsonResponse(AttemptJson(Expected)),
            2 => Redirect("https://storage.example.invalid/blob?sig=" + Sentinel),
            _ => new HttpResponseMessage((HttpStatusCode)status)
        });
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier,
            receipt, token: Sentinel));

        Assert.Equal("artifact-download-failed", error.Code);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Null(handler.Requests[3].Authorization);
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Fact]
    public async Task RedirectChainHasNoTokenAtStorageAndStopsAtThreeHopBudget()
    {
        // protects: every storage hop is credential-free and the bounded redirect chain is not retried or followed indefinitely.
        // fails_when: authorization reaches a signed storage URL or a fourth redirect target is sent.
        // no_claim: redirect fixtures do not establish actual GitHub storage behavior.
        var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(Expected)),
            1 => JsonResponse(AttemptJson(Expected)),
            _ => Redirect($"https://storage.example.invalid/hop-{index}?sig={Sentinel}")
        });
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier,
            receipt, token: Sentinel));

        Assert.Equal("artifact-redirect-invalid", error.Code);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal("Bearer", handler.Requests[0].Authorization?.Scheme);
        Assert.Equal("Bearer", handler.Requests[1].Authorization?.Scheme);
        Assert.Equal("Bearer", handler.Requests[2].Authorization?.Scheme);
        Assert.All(handler.Requests.Skip(3), request => Assert.Null(request.Authorization));
        Assert.DoesNotContain(handler.Requests.Skip(3).SelectMany(request => request.Headers),
            header => header.Value.Contains(Sentinel, StringComparison.Ordinal));
        AssertNoOwnedZip(error);
        AssertSafe(error);
    }

    [Fact]
    public async Task RawApiNumbersAboveJavaScriptPrecisionRemainExactStrings()
    {
        // protects: raw JSON integer identities are compared without floating-point rounding or ulong narrowing.
        // fails_when: adjacent IDs above 2^53 collapse to the same value or change the direct endpoint identity.
        // no_claim: exact numeric parsing does not confer authority on arbitrary caller-supplied IDs.
        var expected = new ArtifactExpectedContext("forge-trust/AppSurface", "9007199254740993",
            "9007199254740997", "9007199254740995", Source, "9007199254741001");
        var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(expected)),
            1 => JsonResponse(AttemptJson(expected)),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync(ReceiptJson(expected));

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt, expected));

        Assert.Equal("artifact-api-unavailable", error.Code);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("/artifacts/9007199254741001", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/runs/9007199254740997/attempts/9007199254740995", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
        AssertSafe(error);
    }

    [Fact]
    public async Task MetadataAndAttemptFailureMessagesNeverExposeSentinelsOrOwnedZip()
    {
        // protects: attacker-controlled response strings and caller token do not enter exception safe properties.
        // fails_when: a mismatching source/event/path or nested API value appears in diagnostics metadata.
        // no_claim: checking core exception properties does not audit adapter reports, console output, or process memory.
        var cases = new[]
        {
            (First: true, Body: ArtifactJson(Expected).Replace(Source, Sentinel + new string('a', 40 - Sentinel.Length), StringComparison.Ordinal), Code: "artifact-api-response-invalid"),
            (First: false, Body: AttemptJson(Expected).Replace("workflow_dispatch", Sentinel, StringComparison.Ordinal), Code: "artifact-workflow-mismatch")
        };
        foreach (var testCase in cases)
        {
            var handler = new RecordingHandler((_, index) => index == 0
                ? testCase.First ? JsonResponse(testCase.Body) : JsonResponse(ArtifactJson(Expected))
                : JsonResponse(testCase.Body));
            using var verifier = CreateVerifier(handler);
            var receipt = await WriteReceiptAsync();
            var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier,
                receipt, token: Sentinel));
            Assert.Equal(testCase.Code, error.Code);
            AssertNoOwnedZip(error);
            AssertSafe(error);
        }
    }

    private GitHubActionsArtifactVerifier CreateVerifier(RecordingHandler handler)
        => new(handler, timeProvider: null, maxJsonBytes: 1024 * 1024, maxArchiveBytes: 1024 * 1024,
            overallLimit: TimeSpan.FromMinutes(5), ioLimit: TimeSpan.FromSeconds(5));

    private Task<VerifiedArtifactArchive> VerifyAsync(GitHubActionsArtifactVerifier verifier, string receipt,
        ArtifactExpectedContext? expected = null, string token = "boundary-token")
        => verifier.VerifyAsync(expected ?? Expected, receipt, token, TestPathUtils.PathUnder(_root, "scratch"));

    private async Task<string> WriteReceiptAsync(string? content = null)
    {
        var path = TestPathUtils.PathUnder(_root, "receipt-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, content ?? ReceiptJson(Expected));
        return path;
    }

    private static string ReceiptJson(ArtifactExpectedContext expected) => $$"""{"schema":"appsurface-github-actions-producer-receipt-v1","repository":"{{expected.Repository}}","repositoryId":"{{expected.RepositoryId}}","workflowPath":".github/workflows/package-artifacts.yml","event":"workflow_dispatch","producerJob":"package-artifacts","runId":"{{expected.RunId}}","runAttempt":"{{expected.RunAttempt}}","sourceCommit":"{{expected.SourceCommit}}","artifactId":"{{expected.ArtifactId}}","archiveSha256":"{{Digest}}"}""";

    private static string ChangeReceipt(string property, string value)
        => ReceiptJson(Expected).Replace($"\"{property}\":\"{ReceiptValue(property)}\"",
            $"\"{property}\":\"{value}\"", StringComparison.Ordinal);

    private static string ReceiptValue(string property) => property switch
    {
        "repository" => Expected.Repository,
        "repositoryId" => Expected.RepositoryId,
        "runId" => Expected.RunId,
        "runAttempt" => Expected.RunAttempt,
        "sourceCommit" => Expected.SourceCommit,
        "artifactId" => Expected.ArtifactId,
        "workflowPath" => ".github/workflows/package-artifacts.yml",
        "event" => "workflow_dispatch",
        "producerJob" => "package-artifacts",
        _ => throw new ArgumentOutOfRangeException(nameof(property))
    };

    private static string MutateReceipt(string property, string mutation)
    {
        var node = JsonNode.Parse(ReceiptJson(Expected))!.AsObject();
        switch (mutation)
        {
            case "unsupported": node[property] = "wrong-schema"; break;
            case "zero": node[property] = "0"; break;
            case "leading-zero": node[property] = property == "repositoryId" ? "01035215851" : property == "runId" ? "036547070332" : property == "runAttempt" ? "01" : "011023576342"; break;
            case "uppercase": node[property] = property == "sourceCommit" ? Source.ToUpperInvariant() : "SHA256:" + new string('A', 64); break;
            case "wrong-type": node[property] = JsonValue.Create(17); break;
            case "repo-parent": node[property] = "../AppSurface"; break;
            case "repo-extra": node[property] = "forge-trust/AppSurface/extra"; break;
            case "repo-space": node[property] = "forge trust/AppSurface"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        return node.ToJsonString();
    }

    private static string ArtifactJson(ArtifactExpectedContext expected)
        => new JsonObject
        {
            ["id"] = RawNumber(expected.ArtifactId),
            ["expired"] = false,
            ["expires_at"] = "2099-01-01T00:00:00Z",
            ["digest"] = Digest,
            ["workflow_run"] = new JsonObject
            {
                ["id"] = RawNumber(expected.RunId),
                ["repository_id"] = RawNumber(expected.RepositoryId),
                ["head_repository_id"] = RawNumber(expected.RepositoryId),
                ["head_sha"] = expected.SourceCommit
            }
        }.ToJsonString();

    private static string MutateArtifact(string path, string mutation)
    {
        var node = JsonNode.Parse(ArtifactJson(Expected))!.AsObject();
        if (path == "workflow_run" && mutation == "missing")
        {
            node.Remove(path);
            return node.ToJsonString();
        }
        JsonObject parent = node;
        var segment = path.Split('.');
        foreach (var part in segment[..^1]) parent = parent[part]!.AsObject();
        var key = segment[^1];
        if (mutation == "missing") { parent.Remove(key); return node.ToJsonString(); }
        switch (mutation)
        {
            case "identity":
                if (key == "head_sha") parent[key] = OtherSource;
                else parent[key] = JsonNode.Parse(key switch
                {
                    "head_repository_id" or "repository_id" => "1035215852",
                    "id" => "36547070333",
                    _ => throw new ArgumentOutOfRangeException(nameof(key))
                });
                break;
            case "wrong-type":
                if (key == "expired") parent[key] = "false";
                else if (path == "id") parent[key] = Expected.ArtifactId;
                else if (path == "workflow_run.id") parent[key] = Expected.RunId;
                else if (path is "workflow_run.repository_id" or "workflow_run.head_repository_id") parent[key] = Expected.RepositoryId;
                else parent[key] = JsonValue.Create(17);
                break;
            case "uppercase": parent[key] = path.EndsWith("head_sha", StringComparison.Ordinal) ? Source.ToUpperInvariant() : "SHA256:" + new string('A', 64); break;
            case "invalid-hex":
                parent[key] = path.EndsWith("head_sha", StringComparison.Ordinal)
                ? Source[..^1] + "g"
                : "sha256:" + new string('g', 64); break;
            case "short": parent[key] = key == "head_sha" ? Source[..^1] : "sha256:a"; break;
            case "true": parent[key] = true; break;
            case "expired": parent[key] = "2000-01-01T00:00:00Z"; break;
            case "other-valid": parent[key] = "sha256:" + new string('0', 64); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        return node.ToJsonString();
    }

    private static string AttemptJson(ArtifactExpectedContext expected)
        => new JsonObject
        {
            ["id"] = RawNumber(expected.RunId),
            ["run_attempt"] = RawNumber(expected.RunAttempt),
            ["repository"] = new JsonObject { ["id"] = RawNumber(expected.RepositoryId) },
            ["head_repository"] = new JsonObject { ["id"] = RawNumber(expected.RepositoryId) },
            ["head_sha"] = expected.SourceCommit,
            ["path"] = ".github/workflows/package-artifacts.yml",
            ["event"] = "workflow_dispatch"
        }.ToJsonString();

    private static JsonNode RawNumber(string canonicalDigits)
        => JsonNode.Parse(canonicalDigits) ?? throw new InvalidOperationException("Canonical fixture number was not JSON.");

    private static string MutateAttempt(string json, string path, string mutation)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        JsonObject parent = node;
        var segment = path.Split('.');
        foreach (var part in segment[..^1]) parent = parent[part]!.AsObject();
        var key = segment[^1];
        if (mutation == "missing") { parent.Remove(key); return node.ToJsonString(); }
        switch (mutation)
        {
            case "invalid-hex": parent[key] = Source[..^1] + "g"; break;
            case "short": parent[key] = Source[..^1]; break;
            case "wrong-type":
                if (key is "head_sha" or "path" or "event") parent[key] = 17;
                else parent[key] = key switch
                {
                    "id" => Expected.RunId,
                    "run_attempt" => Expected.RunAttempt,
                    _ when segment[0] == "repository" => Expected.RepositoryId,
                    _ => Expected.RepositoryId
                };
                break;
            case "identity":
                if (key == "head_sha") parent[key] = OtherSource;
                else if (key == "path") parent[key] = ".github/workflows/other.yml";
                else if (key == "event") parent[key] = "push";
                else parent[key] = JsonNode.Parse(key switch
                {
                    "run_attempt" => "2",
                    "id" when segment.Length == 1 => "36547070333",
                    _ => "1035215852"
                });
                break;
            default:
                parent[key] = key is "head_sha" or "path" or "event"
                    ? JsonValue.Create(mutation)
                    : RawNumber(mutation);
                break;
        }
        return node.ToJsonString();
    }

    private static string AddProperty(string json, string name, string value)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node[name] = value;
        return node.ToJsonString();
    }

    private static HttpResponseMessage Redirect(string location)
        => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private void AssertNoOwnedZip(ArtifactTransportException error)
    {
        var scratch = TestPathUtils.PathUnder(_root, "scratch");
        Assert.Empty(Directory.Exists(scratch) ? Directory.EnumerateFiles(scratch) : []);
        Assert.DoesNotContain(".zip", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSafe(ArtifactTransportException error)
    {
        var values = new[] { error.Message, error.Code, error.Operation, error.Field, error.Expected, error.Observed, error.CauseKind }
            .Where(value => value is not null);
        Assert.DoesNotContain(values, value => value!.Contains(Sentinel, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord(request.RequestUri!, request.Headers.Authorization,
                request.Headers.SelectMany(header => header.Value.Select(value => KeyValuePair.Create(header.Key, value))).ToArray()));
            return Task.FromResult(respond(request, Requests.Count - 1));
        }
    }

    private sealed record RequestRecord(Uri Uri, AuthenticationHeaderValue? Authorization,
        KeyValuePair<string, string>[] Headers);

    [Fact]
    public async Task ReceiptAcceptsCanonicalRepositoryComponentsWithDotsAndUnderscores()
    {
        var expected = Expected with { Repository = "forge-trust/App.Surface_tests" };
        var handler = new RecordingHandler((_, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(expected)),
            1 => JsonResponse(AttemptJson(expected)),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        });
        using var verifier = CreateVerifier(handler);
        var receipt = await WriteReceiptAsync(ReceiptJson(expected));

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => VerifyAsync(verifier, receipt, expected));
        Assert.Equal("artifact-api-unavailable", error.Code);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("/repos/forge-trust/App.Surface_tests/", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        AssertSafe(error);
    }
}
