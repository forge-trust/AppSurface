using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.PackageIndex;

namespace ForgeTrust.AppSurface.PackageIndex.Tests;

public sealed class GitHubActionsArtifactVerifierTests : IDisposable
{
    private readonly string _root = TestPathUtils.PathUnder(TailwindTestPaths.TemporaryRoot,
        "github-artifact-verifier-tests", Guid.NewGuid().ToString("N"));
    private const string ZipText = "verified archive bytes";
    private const string Sha = "sha256:74e79ddd5e5b690dbfb0995c1af7c4a86feaeff0b5f5b8d667fc2cda3360a489";
    private static readonly ArtifactExpectedContext Expected = new("forge-trust/AppSurface", "1035215851", "36547070332", "1",
        "10d375c6942fc650cc64f64e0748add77540d47c", "11023576342");

    public GitHubActionsArtifactVerifierTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task VerifyAsync_UsesDirectEndpointsAndReturnsOwnedHashedArchive()
    {
        // protects: direct exact-ID/current-attempt transport and hash-before-handoff.
        // fails_when: discovery, receipt URLs, or unverified archive bytes are accepted.
        // no_claim: fixtures do not prove GitHub token authority or a hosted upload.
        var handler = HandlerForSuccess(Encoding.UTF8.GetBytes(ZipText));
        var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var result = await verifier.VerifyAsync(Expected, receipt, "sentinel-token", TestPathUtils.PathUnder(_root, "scratch"));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("/repos/forge-trust/AppSurface/actions/artifacts/11023576342", handler.Requests[0].PathAndQuery);
        Assert.Equal("/repos/forge-trust/AppSurface/actions/runs/36547070332/attempts/1", handler.Requests[1].PathAndQuery);
        Assert.Equal("/repos/forge-trust/AppSurface/actions/artifacts/11023576342/zip", handler.Requests[2].PathAndQuery);
        Assert.Equal(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(ZipText)), await File.ReadAllTextAsync(result.ArchivePath));
        Assert.Equal(Encoding.UTF8.GetByteCount(ZipText), result.ArchiveBytes);
        Assert.Equal("Bearer", handler.Requests[0].Authorization?.Scheme);
    }

    [Fact]
    public async Task VerifyAsync_RefusesOldReceiptAttemptBeforeAnyHttp()
    {
        // protects: old producer receipts cannot start network access.
        // fails_when: attempt mismatch is discovered only after metadata/download.
        // no_claim: no earlier attempt is claimed to have been observed in a hosted run.
        var handler = HandlerForSuccess([]);
        var verifier = new GitHubActionsArtifactVerifier(handler);
        var path = await WriteReceiptAsync(attempt: "2");
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, path, "token", TestPathUtils.PathUnder(_root, "scratch")));
        Assert.Equal("artifact-attempt-mismatch", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("token with space")]
    [InlineData("token\tvalue")]
    [InlineData("token\rvalue")]
    [InlineData("token\u0001value")]
    public async Task VerifyAsync_RejectsTokenWhitespaceOrControlBeforeHttp(string token)
    {
        // protects: bearer credentials contain no whitespace or control characters before transport.
        // fails_when: malformed token text reaches HTTP or escapes as a header-construction exception.
        // no_claim: this validates local input only, not GitHub token authority.
        var handler = HandlerForSuccess([]);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receiptPath = await WriteReceiptAsync();

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected,
            receiptPath, token, TestPathUtils.PathUnder(_root, "scratch")));

        Assert.Equal("artifact-input-invalid", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(401, "artifact-permission-denied")]
    [InlineData(403, "artifact-permission-denied")]
    [InlineData(404, "artifact-not-found-or-inaccessible")]
    [InlineData(410, "artifact-expired")]
    [InlineData(429, "artifact-rate-limited")]
    [InlineData(500, "artifact-api-unavailable")]
    [InlineData(302, "artifact-redirect-invalid")]
    [InlineData(201, "artifact-api-response-invalid")]
    [InlineData(422, "artifact-api-response-invalid")]
    public async Task VerifyAsync_ClassifiesMetadataHttpStatus(int status, string expectedCode)
    {
        // protects: stable HTTP status partition.
        // fails_when: known API failures collapse to a generic transport refusal.
        // no_claim: fixture status does not establish current service availability.
        var handler = new QueueHandler((_, _) => new HttpResponseMessage((HttpStatusCode)status));
        var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "scratch")));
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(status, error.StatusCode);
    }

    [Theory]
    [InlineData(true, "artifact-rate-limited")]
    [InlineData(false, "artifact-permission-denied")]
    public async Task VerifyAsync_AnyRetryAfterOn403IsRateLimit(bool retryAfter, string expectedCode)
    {
        // protects: approved 403 rate-limit precedence, including an empty Retry-After.
        // fails_when: malformed or empty presence is ignored.
        // no_claim: response fixtures do not reproduce GitHub's live limit state.
        var handler = new QueueHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (retryAfter) response.Headers.TryAddWithoutValidation("Retry-After", "");
            return response;
        });
        var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "scratch")));
        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public async Task VerifyAsync_ClassifiesRateLimitMessageWithoutRateHeaders()
    {
        // protects: bounded primary/secondary limit body inspection when headers omit a signal.
        // fails_when: an explicit secondary-limit response is reported as ordinary permission denial.
        // no_claim: the controlled response does not establish a live GitHub limit condition.
        var handler = new QueueHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"message\":\"You have exceeded a secondary rate limit.\"}", Encoding.UTF8, "application/json")
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receiptPath = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected,
            receiptPath, "token", TestPathUtils.PathUnder(_root, "scratch")));
        Assert.Equal("artifact-rate-limited", error.Code);
    }

    [Fact]
    public async Task VerifyAsync_UsesAllowlistedObservedValueForWorkflowMismatch()
    {
        // protects: malformed workflow strings cannot leak through the diagnostic metadata boundary.
        // fails_when: a raw endpoint path or embedded credential is exposed as Observed.
        // no_claim: this does not inspect adapter-owned report files.
        var handler = new QueueHandler((_, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(Expected, Sha)),
            _ => JsonResponse(AttemptJson(Expected).Replace("workflow_dispatch", "https://sentinel.invalid/?token=secret", StringComparison.Ordinal))
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receiptPath = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected,
            receiptPath, "token", TestPathUtils.PathUnder(_root, "scratch")));
        Assert.Equal("artifact-workflow-mismatch", error.Code);
        Assert.Equal("unexpected", error.Observed);
        AssertNoSentinel(error);
    }

    [Fact]
    public async Task VerifyAsync_Stalled403BodyUsesTheInactivityBudget()
    {
        // protects: rate-limit body classification cannot hang past the response inactivity limit.
        // fails_when: a stalled 403 body hangs or becomes permission-denied rather than a time-budget refusal.
        // no_claim: fake-clock timing does not measure real network latency.
        var time = new ManualTimeProvider();
        var body = new HangingReadStream();
        var handler = new QueueHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(body) });
        using var verifier = new GitHubActionsArtifactVerifier(handler, time, 1024, 1024, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4));
        var verify = verifier.VerifyAsync(Expected, await WriteReceiptAsync(), "token", TestPathUtils.PathUnder(_root, "scratch"));
        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verify);
        Assert.Equal("artifact-budget-exceeded", error.Code);
        Assert.Equal("inactivity", error.Field);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("numeric")]
    [InlineData("bad-digest")]
    public async Task VerifyAsync_RejectsMalformedReceiptBeforeHttp(string mutation)
    {
        // protects: exact duplicate-free schema-v1 receipt boundary.
        // fails_when: malformed authority reaches REST endpoints.
        // no_claim: receipt shape validity is not producer authentication.
        var handler = HandlerForSuccess([]);
        var verifier = new GitHubActionsArtifactVerifier(handler);
        var path = TestPathUtils.PathUnder(_root, "malformed.json");
        var json = mutation switch
        {
            "duplicate" => "{\"schema\":\"x\",\"schema\":\"x\"}",
            "extra" => WithExtraReceipt(),
            "numeric" => ReceiptJson().Replace("\"artifactId\":\"11023576342\"", "\"artifactId\":11023576342", StringComparison.Ordinal),
            _ => ReceiptJson().Replace(Sha, "bad", StringComparison.Ordinal)
        };
        await File.WriteAllTextAsync(path, json);
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, path, "token", TestPathUtils.PathUnder(_root, "scratch")));
        Assert.Equal("artifact-receipt-invalid", error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task VerifyAsync_RequiresMetadataDigestAgreementBeforeDownload()
    {
        // protects: API digest and protected upload digest remain distinct checks.
        // fails_when: mismatched API metadata permits ZIP download.
        // no_claim: consistent digests do not authenticate a compromised producer.
        var handler = new QueueHandler((_, index) => index == 0
            ? JsonResponse(ArtifactJson(Expected, "sha256:" + new string('0', 64)))
            : throw new InvalidOperationException("unexpected request"));
        var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "scratch")));
        Assert.Equal("artifact-metadata-digest-mismatch", error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task VerifyAsync_RejectsChangedArchiveBytesAndDeletesPartialFile()
    {
        // protects: actual streamed archive bytes are checked before handoff.
        // fails_when: a digest-mismatched ZIP is returned or retained as owned output.
        // no_claim: this tests transport hashing, not ZIP extraction.
        var handler = HandlerForSuccess(Encoding.UTF8.GetBytes("mutated archive"));
        var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var scratch = TestPathUtils.PathUnder(_root, "scratch");
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", scratch));
        Assert.Equal("artifact-bytes-digest-mismatch", error.Code);
        Assert.Empty(Directory.Exists(scratch) ? Directory.EnumerateFiles(scratch) : []);
    }

    [Fact]
    public async Task VerifyAsync_InsecureStorageRedirectIsRejectedBeforeFollowup()
    {
        // protects: credentials stop at the GitHub API boundary and redirects remain HTTPS.
        // fails_when: a signed storage URL receives the bearer token or an unsafe scheme is followed.
        // no_claim: storage availability is not established by the fake handler.
        var handler = new QueueHandler((request, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(Expected, Sha)),
            1 => JsonResponse(AttemptJson(Expected)),
            _ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://storage.invalid/signed?secret=sentinel") } }
        });
        var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "secret-token", TestPathUtils.PathUnder(_root, "scratch")));
        Assert.Equal("artifact-redirect-invalid", error.Code);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("secret-token", handler.Requests.Last().Authorization?.Parameter);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host == "storage.invalid");
    }

    [Fact]
    public async Task VerifyAsync_ValidHttpsStorageRedirectOmitsAuthorization()
    {
        // protects: allowed HTTPS storage followups receive no API bearer credential.
        // fails_when: authorization is forwarded across the signed-storage boundary.
        // no_claim: fixture storage success does not establish hosted storage availability.
        var handler = new QueueHandler((_, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(Expected, Sha)),
            1 => JsonResponse(AttemptJson(Expected)),
            2 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://storage.invalid/signed?signature=secret-sentinel") } },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ZipText)) }
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receiptPath = await WriteReceiptAsync();
        var result = await verifier.VerifyAsync(Expected, receiptPath, "secret-token", TestPathUtils.PathUnder(_root, "scratch"));
        Assert.Equal(4, handler.Requests.Count);
        Assert.Null(handler.Requests[3].Authorization);
        Assert.DoesNotContain(handler.Requests[3].Headers, header => header.Value.Contains("secret-token", StringComparison.Ordinal));
        Assert.Equal(Encoding.UTF8.GetByteCount(ZipText), result.ArchiveBytes);
    }

    [Fact]
    public async Task VerifyAsync_HeaderStallUsesTheHeaderBudget()
    {
        // protects: response-header waits have a deterministic inactivity bound.
        // fails_when: a handler that never returns headers can hold the verifier indefinitely.
        // no_claim: fake-clock timeout coverage does not measure production latency.
        var time = new ManualTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler, time, 1024, 1024, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4));
        var receiptPath = await WriteReceiptAsync();
        var verify = verifier.VerifyAsync(Expected, receiptPath, "token", TestPathUtils.PathUnder(_root, "scratch"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verify);
        Assert.Equal("artifact-budget-exceeded", error.Code);
        Assert.Equal("headers", error.Field);
    }

    [Fact]
    public async Task VerifyAsync_CallerCancellationDuringStalled403RemainsDistinct()
    {
        // protects: caller cancellation is preserved during bounded 403 body inspection.
        // fails_when: intentional cancellation becomes permission denial or a deadline refusal.
        // no_claim: caller cancellation does not assert a report or workflow marker.
        var body = new HangingReadStream();
        var handler = new QueueHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(body) });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        using var caller = new CancellationTokenSource();
        var receiptPath = await WriteReceiptAsync();
        var verify = verifier.VerifyAsync(Expected, receiptPath, "token", TestPathUtils.PathUnder(_root, "scratch"), caller.Token);
        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verify);
        Assert.Equal("artifact-cancelled", error.Code);
        Assert.Equal("caller-cancelled", error.CauseKind);
    }

    [Theory]
    [InlineData(22, false)]
    [InlineData(21, true)]
    public async Task VerifyAsync_EnforcesActualArchiveByteLimitAndCleansIncompleteFile(long maxBytes, bool shouldFail)
    {
        // protects: actual streamed bytes enforce the archive bound at and over its boundary.
        // fails_when: missing or false Content-Length bypasses the raw-byte budget or leaves partial scratch behind.
        // no_claim: fixture byte limits are reduced internal test budgets, not production configuration.
        var handler = HandlerForSuccess(Encoding.UTF8.GetBytes(ZipText));
        using var verifier = new GitHubActionsArtifactVerifier(handler, null, 1024, maxBytes, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));
        var receiptPath = await WriteReceiptAsync();
        var scratch = TestPathUtils.PathUnder(_root, "scratch-" + Guid.NewGuid().ToString("N"));
        if (!shouldFail)
        {
            var archive = await verifier.VerifyAsync(Expected, receiptPath, "token", scratch);
            Assert.Equal(maxBytes, archive.ArchiveBytes);
            return;
        }
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receiptPath, "token", scratch));
        Assert.Equal("artifact-budget-exceeded", error.Code);
        Assert.Equal("archive-bytes", error.Field);
        Assert.Empty(Directory.EnumerateFiles(scratch));
    }

    [Fact]
    public async Task VerifyAsync_TruncatedStorageBodyIsDownloadFailureAndDeletesScratch()
    {
        // protects: transfer truncation is distinct from a complete archive with a wrong digest.
        // fails_when: fewer bytes than the declared Content-Length are handed off or called a digest mismatch.
        // no_claim: controlled fixture behavior does not reproduce every HTTP stack's truncation handling.
        var handler = new QueueHandler((_, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(Expected, Sha)),
            1 => JsonResponse(AttemptJson(Expected)),
            _ => TruncatedResponse()
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receiptPath = await WriteReceiptAsync();
        var scratch = TestPathUtils.PathUnder(_root, "truncated-scratch");
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receiptPath, "token", scratch));
        Assert.Equal("artifact-download-failed", error.Code);
        Assert.Equal("content-length", error.Field);
        Assert.Empty(Directory.EnumerateFiles(scratch));
    }

    [Fact]
    public void VerifyAsync_DisposingVerifierLeavesInjectedHandlerCallerOwned()
    {
        // protects: callers retain ownership of an injected handler.
        // fails_when: disposing the verifier disposes caller-owned transport infrastructure.
        // no_claim: production handler disposal is checked through its ownership configuration, not live network.
        var handler = new TrackingHandler();
        using (var verifier = new GitHubActionsArtifactVerifier(handler)) { }
        Assert.False(handler.Disposed);
        handler.Dispose();
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task ManualTimeProvider_FiresItsCancellationTimerWhenAdvanced()
    {
        // protects: deterministic timeout tests use a functioning TimeProvider timer.
        // fails_when: fake time advances without cancelling a linked operation.
        // no_claim: timer semantics do not measure production throughput.
        var time = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();
        using var timer = time.CreateTimer(_ => cts.Cancel(), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(cts.IsCancellationRequested);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("artifact-metadata")]
    [InlineData("403-body")]
    [InlineData("403-io")]
    [InlineData("archive-body")]
    [InlineData("storage-body")]
    public async Task NetworkFailureIsClassifiedAtItsRemoteBoundary(string stage)
    {
        // protects: network faults retain API versus storage/transfer diagnostic authority and cleanup.
        // fails_when: a remote body failure becomes a local output error or leaves a partial archive.
        // no_claim: injected faults do not establish hosted service availability.
        var handler = new QueueHandler((_, index) =>
        {
            if (stage == "artifact-metadata") throw new HttpRequestException("network-sentinel token=query");
            if (stage == "403-body") return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(new FaultReadStream()) };
            if (stage == "403-io") return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(new IoFailureStream()) };
            if (index == 0) return JsonResponse(ArtifactJson(Expected, Sha));
            if (index == 1) return JsonResponse(AttemptJson(Expected));
            if (stage == "storage-body" && index == 2) return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://storage.test/download?signed=sentinel") } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FaultReadStream()) };
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var scratch = TestPathUtils.PathUnder(_root, "network-scratch");
        var receiptPath = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receiptPath, "token", scratch));
        Assert.Equal(stage is "archive-body" or "storage-body" ? "artifact-download-failed" : "artifact-api-unavailable", error.Code);
        AssertNoSentinel(error);
        Assert.Empty(Directory.EnumerateFiles(scratch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualArchiveCounterRejectsOverrunWithoutTrustedLength(bool underreported)
    {
        // protects: actual bytes, not absent or dishonest Content-Length, enforce the archive bound.
        // fails_when: an oversized stream bypasses the incremental counter or leaves partial bytes.
        // no_claim: reduced internal budgets do not change the production 1 GiB policy.
        var handler = new QueueHandler((_, index) => index switch
        {
            0 => JsonResponse(ArtifactJson(Expected, Sha)),
            1 => JsonResponse(AttemptJson(Expected)),
            _ => StreamResponse(Encoding.UTF8.GetBytes(ZipText), underreported ? 1 : null)
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler, null, 4096, 10, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));
        var scratch = TestPathUtils.PathUnder(_root, "counter-scratch");
        var receiptPath = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receiptPath, "token", scratch));
        Assert.Equal("artifact-budget-exceeded", error.Code);
        Assert.Equal("archive-bytes", error.Field);
        Assert.Empty(Directory.EnumerateFiles(scratch));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("metadata", false)]
    [InlineData("archive", false)]
    [InlineData("archive", true)]
    public async Task StalledSuccessBodyHonorsInactivityAndOverallDeadline(string stage, bool overall)
    {
        // protects: success-response bodies share deterministic inactivity and overall deadlines.
        // fails_when: a ZIP or JSON body stalls forever or the timeout becomes caller cancellation.
        // no_claim: manual-clock fixtures are not latency measurements.
        var clock = new ManualTimeProvider();
        var body = new HangingReadStream();
        using var handler = new QueueHandler((_, index) =>
        {
            if (stage == "metadata" || index == 2) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            return JsonResponse(index == 0 ? ArtifactJson(Expected, Sha) : AttemptJson(Expected));
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler, clock, 4096, 4096,
            overall ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4));
        var scratch = TestPathUtils.PathUnder(_root, "stall-scratch");
        var verify = verifier.VerifyAsync(Expected, await WriteReceiptAsync(), "token", scratch);
        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verify);
        Assert.Equal("artifact-budget-exceeded", error.Code);
        Assert.Equal(overall ? "time" : "inactivity", error.Field);
        Assert.Empty(Directory.EnumerateFiles(scratch));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptDocumentBytesAreBoundedAtProductionLimit(bool over)
    {
        // protects: the exact 16 MiB receipt boundary is accepted while one extra byte refuses before HTTP.
        // fails_when: a document limit is off by one or oversized authority initiates a request.
        // no_claim: schema-valid receipt bytes do not authenticate the producer.
        const int limit = 16 * 1024 * 1024;
        var receipt = await WriteReceiptAsync();
        var text = ReceiptJson();
        await File.WriteAllTextAsync(receipt, text + new string(' ', limit - Encoding.UTF8.GetByteCount(text) + (over ? 1 : 0)), new UTF8Encoding(false));
        using var handler = HandlerForSuccess(Encoding.UTF8.GetBytes(ZipText));
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        if (over)
        {
            var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "document-scratch")));
            Assert.Equal("artifact-budget-exceeded", error.Code);
            Assert.Empty(handler.Requests);
        }
        else
        {
            var archive = await verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "document-scratch"));
            Assert.True(archive.ArchiveBytes > 0);
            Assert.Equal(3, handler.Requests.Count);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApiJsonCountsActualBytesAtAndOverBoundary(bool over, bool declared)
    {
        // protects: bounded JSON accounts actual streamed bytes independently of declared length.
        // fails_when: missing Content-Length bypasses the limit or a refused document advances requests.
        // no_claim: reduced internal JSON budget has the same production counting behavior.
        const int limit = 2048;
        var json = ArtifactJson(Expected, Sha);
        var bytes = Encoding.UTF8.GetBytes(json + new string(' ', limit - json.Length + (over ? 1 : 0)));
        using var handler = new QueueHandler((_, index) => index switch
        {
            0 => StreamResponse(bytes, declared ? bytes.Length : null),
            1 => JsonResponse(AttemptJson(Expected)),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ZipText)) }
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler, null, limit, 4096, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));
        var receipt = await WriteReceiptAsync();
        if (over)
        {
            var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "json-scratch")));
            Assert.Equal("artifact-budget-exceeded", error.Code);
            Assert.Equal("json-bytes", error.Field);
            Assert.Single(handler.Requests);
        }
        else { await verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "json-scratch")); Assert.Equal(3, handler.Requests.Count); }
    }

    [Theory]
    [InlineData(63, false)]
    [InlineData(64, true)]
    public async Task ApiJsonDepthAllows64AndRefuses65(int arrayDepth, bool refuses)
    {
        // protects: additive API JSON remains depth-bounded without losing valid contract fields.
        // fails_when: excessive nesting reaches the next endpoint or valid depth is rejected.
        // no_claim: additive fields are not producer authority.
        var baseline = ArtifactJson(Expected, Sha);
        var json = baseline[..^1] + ",\"extra\":" + new string('[', arrayDepth) + "0" + new string(']', arrayDepth) + "}";
        using var handler = new QueueHandler((_, index) => index switch { 0 => JsonResponse(json), 1 => JsonResponse(AttemptJson(Expected)), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ZipText)) } });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        if (refuses) { var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "depth-scratch"))); Assert.Equal("artifact-api-response-invalid", error.Code); Assert.Single(handler.Requests); }
        else { await verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "depth-scratch")); Assert.Equal(3, handler.Requests.Count); }
    }

    [Fact]
    public void ProductionVerifierCanBeCreatedAndDisposedWithoutNetwork()
    {
        // protects: the production ownership constructor is usable independently of injected handlers.
        // fails_when: production defaults cannot create or dispose their owned handler.
        // no_claim: construction does not establish redirect policy or hosted authentication.
        using var verifier = GitHubActionsArtifactVerifier.CreateProduction();
    }

    private static HttpResponseMessage StreamResponse(byte[] bytes, long? declared)
    {
        var content = new StreamContent(new NonSeekableReadStream(bytes));
        if (declared is not null) content.Headers.ContentLength = declared;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    private sealed class FaultReadStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new HttpRequestException("network-sentinel token=query"));
    }

    [Theory]
    [InlineData("metadata-open", "artifact-api-unavailable")]
    [InlineData("metadata-read", "artifact-api-unavailable")]
    [InlineData("archive-open", "artifact-download-failed")]
    [InlineData("archive-send", "artifact-api-unavailable")]
    [InlineData("storage-send", "artifact-download-failed")]
    public async Task RemoteIoFailuresKeepTheirBoundaryAndDeleteScratch(string stage, string code)
    {
        // protects: response-open, body-read and ZIP-send errors keep the remote diagnostic boundary.
        // fails_when: a transfer failure becomes local output failure or leaves eligible scratch.
        // no_claim: controlled transport faults do not prove service availability.
        using var handler = new QueueHandler((_, index) =>
        {
            if (stage == "metadata-open") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new OpenFailureContent() };
            if (stage == "metadata-read") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new IoFailureStream()) };
            if (index == 0) return JsonResponse(ArtifactJson(Expected, Sha));
            if (index == 1) return JsonResponse(AttemptJson(Expected));
            if (stage == "storage-send" && index == 2) return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://storage.test/secret?query=sentinel") } };
            if (stage is "archive-send" or "storage-send") throw new HttpRequestException("sentinel secret query");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new OpenFailureContent() };
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var scratch = TestPathUtils.PathUnder(_root, "remoteio");
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", scratch));
        Assert.Equal(code, error.Code);
        Assert.Empty(Directory.GetFiles(scratch));
        AssertNoSentinel(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZipResponseHeadersHaveTheirOwnBound(bool storage)
    {
        // protects: direct ZIP and redirected storage header waits cannot stall indefinitely.
        // fails_when: a header timeout leaks as cancellation or a generic network/output failure.
        // no_claim: manual time validates deadline behavior, not network latency.
        var clock = new ManualTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;
        using var handler = new AsyncHandler(async (_, ct) =>
        {
            var index = sent++;
            if (index == 0) return JsonResponse(ArtifactJson(Expected, Sha));
            if (index == 1) return JsonResponse(AttemptJson(Expected));
            if (storage && index == 2) return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://storage.test/bytes") } };
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler, clock, 4096, 4096, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4));
        var scratch = TestPathUtils.PathUnder(_root, "headers");
        var task = verifier.VerifyAsync(Expected, await WriteReceiptAsync(), "token", scratch);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => task);
        Assert.Equal("artifact-budget-exceeded", error.Code);
        Assert.Equal("headers", error.Field);
        Assert.Equal("archive-download", error.Operation);
        Assert.Empty(Directory.GetFiles(scratch));
    }

    [Theory]
    [InlineData("missing", "artifact-input-invalid")]
    [InlineData("cancelled", "artifact-cancelled")]
    [InlineData("malformed", "artifact-receipt-invalid")]
    [InlineData("deep", "artifact-receipt-invalid")]
    public async Task ReceiptReadFailuresRefuseBeforeCreatingScratch(string scenario, string code)
    {
        // protects: invalid/unreadable/cancelled receipt authority cannot reach HTTP or scratch creation.
        // fails_when: input failures escape typed refusal or produce partial output.
        // no_claim: a readable receipt does not establish its producer provenance.
        using var handler = HandlerForSuccess([]);
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        if (scenario == "missing") File.Delete(receipt);
        if (scenario == "malformed") await File.WriteAllTextAsync(receipt, "{");
        if (scenario == "deep") await File.WriteAllTextAsync(receipt, new string('[', 65) + "0" + new string(']', 65));
        using var caller = new CancellationTokenSource();
        if (scenario == "cancelled") caller.Cancel();
        var scratch = TestPathUtils.PathUnder(_root, "receipt-failed");
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", scratch, caller.Token));
        Assert.Equal(code, error.Code);
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(scratch));
    }

    private sealed class IoFailureStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("sentinel"));
    }

    private sealed class OpenFailureContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => Task.FromException(new IOException("sentinel"));
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromException<Stream>(new IOException("sentinel"));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromException<Stream>(new IOException("sentinel"));
    }

    [Theory]
    [InlineData("{\"message\":\"Not a rate limit response\"}", "artifact-permission-denied")]
    [InlineData("{\"message\":7}", "artifact-permission-denied")]
    [InlineData("[]", "artifact-permission-denied")]
    [InlineData("malformed", "artifact-permission-denied")]
    [InlineData("{\"message\":\"API rate limit exceeded for caller\"}", "artifact-rate-limited")]
    public async Task RateLimitBodyRequiresRecognizedMessage(string body, string code)
    {
        // protects: only explicit primary/secondary messages classify rate limits without a header signal.
        // fails_when: arbitrary text mentioning rate limits hides an ordinary permission refusal.
        // no_claim: fixtures do not model every GitHub response wording.
        using var handler = new QueueHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(body) });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "rate-body")));
        Assert.Equal(code, error.Code);
        Assert.Equal(403, error.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ArchiveOutputCreationFailureRefusesWithoutDeletingConflictingFile()
    {
        // protects: a local archive-output failure is distinct from a network or digest refusal.
        // fails_when: an unavailable scratch directory is misclassified or its conflicting file is deleted.
        // why_new=existing remote-I/O cases fail before archive output is created.
        // seam=HttpMessageHandler (production caller: HttpClient); no_claim=fixture replacement does not model hostile filesystem races.
        var scratch = TestPathUtils.PathUnder(_root, "blocked-archive-output");
        using var handler = new QueueHandler((_, index) =>
        {
            if (index == 0) return JsonResponse(ArtifactJson(Expected, Sha));
            if (index == 1) return JsonResponse(AttemptJson(Expected));
            Directory.Delete(scratch);
            File.WriteAllText(scratch, "existing-state-sentinel");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ZipText)) };
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected,
            receipt, "token", scratch));

        Assert.Equal("artifact-output-failed", error.Code);
        Assert.Equal("archive-download", error.Operation);
        Assert.Equal("existing-state-sentinel", await File.ReadAllTextAsync(scratch));
        Assert.Equal(3, handler.Requests.Count);
        AssertNoSentinel(error);
    }

    [Theory]
    [InlineData("0", "artifact-rate-limited")]
    [InlineData("1", "artifact-permission-denied")]
    public async Task RateLimitRemainingHeaderDistinguishesExhaustionFromPermission(string remaining, string code)
    {
        // protects: explicit zero quota on 403 takes precedence over generic permission refusal.
        // fails_when: zero quota is ignored, or a nonzero quota alone becomes rate-limit evidence.
        // why_new=Retry-After and response-message tests do not exercise this independent header signal.
        // seam=HttpMessageHandler (production caller: HttpClient); no_claim=fixtures do not establish live GitHub quota.
        using var handler = new QueueHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"Resource not accessible\"}")
            };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining);
            return response;
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        var scratch = TestPathUtils.PathUnder(_root, "quota-scratch");

        var error = await Assert.ThrowsAsync<ArtifactTransportException>(() => verifier.VerifyAsync(Expected, receipt, "token", scratch));

        Assert.Equal(code, error.Code);
        Assert.Equal(403, error.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Empty(Directory.GetFiles(scratch));
    }

    [Fact]
    public async Task ContentEncodingDoesNotChangeTheArchiveBytesHashedOrReturned()
    {
        // protects: downloaded byte identity remains raw even when storage supplies Content-Encoding.
        // fails_when: content decoding changes bytes before receipt/API digest comparison or archive handoff.
        // why_new=existing redirect/hash tests carry no content-encoding header.
        // seam=HttpMessageHandler (production caller: HttpClient); no_claim=injected responses do not exercise production handler wire decoding.
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(ZipText));
        var raw = buffer.ToArray();
        var digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant();
        using var handler = new QueueHandler((_, index) =>
        {
            if (index == 0) return JsonResponse(ArtifactJson(Expected, digest));
            if (index == 1) return JsonResponse(AttemptJson(Expected));
            var content = new ByteArrayContent(raw);
            content.Headers.ContentEncoding.Add("gzip");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var verifier = new GitHubActionsArtifactVerifier(handler);
        var receipt = await WriteReceiptAsync();
        await File.WriteAllTextAsync(receipt, (await File.ReadAllTextAsync(receipt)).Replace(Sha, digest, StringComparison.Ordinal));

        var archive = await verifier.VerifyAsync(Expected, receipt, "token", TestPathUtils.PathUnder(_root, "encoded-scratch"));

        Assert.Equal(raw, await File.ReadAllBytesAsync(archive.ArchivePath));
        Assert.Equal(raw.Length, archive.ArchiveBytes);
        Assert.Equal(digest, archive.Receipt.ArchiveSha256);
        Assert.Equal(3, handler.Requests.Count);
    }

    private QueueHandler HandlerForSuccess(byte[] archive)
    {
        var index = 0;
        return new QueueHandler((_, _) => index++ switch
        {
            0 => JsonResponse(ArtifactJson(Expected, Sha)),
            1 => JsonResponse(AttemptJson(Expected)),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }
        });
    }
    private async Task<string> WriteReceiptAsync(string? attempt = null)
    {
        var path = TestPathUtils.PathUnder(_root, "receipt-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, ReceiptJson(attempt)); return path;
    }
    private static string ReceiptJson(string? attempt = null) => JsonSerializer.Serialize(new
    {
        schema = "appsurface-github-actions-producer-receipt-v1",
        repository = Expected.Repository,
        repositoryId = Expected.RepositoryId,
        workflowPath = ".github/workflows/package-artifacts.yml",
        eventName = "workflow_dispatch",
        producerJob = "package-artifacts",
        runId = Expected.RunId,
        runAttempt = attempt ?? Expected.RunAttempt,
        sourceCommit = Expected.SourceCommit,
        artifactId = Expected.ArtifactId,
        archiveSha256 = Sha
    }).Replace("eventName", "event", StringComparison.Ordinal);
    private static string WithExtraReceipt() => ReceiptJson().Replace("\"schema\"", "\"extra\":\"value\",\"schema\"", StringComparison.Ordinal);
    private static string ArtifactJson(ArtifactExpectedContext e, string digest) => JsonSerializer.Serialize(new
    {
        id = ulong.Parse(e.ArtifactId, System.Globalization.CultureInfo.InvariantCulture),
        expired = false,
        expires_at = "2099-01-01T00:00:00Z",
        digest,
        workflow_run = new
        {
            id = ulong.Parse(e.RunId, System.Globalization.CultureInfo.InvariantCulture),
            repository_id = ulong.Parse(e.RepositoryId, System.Globalization.CultureInfo.InvariantCulture),
            head_repository_id = ulong.Parse(e.RepositoryId, System.Globalization.CultureInfo.InvariantCulture),
            head_sha = e.SourceCommit
        }
    });
    private static string AttemptJson(ArtifactExpectedContext e) => JsonSerializer.Serialize(new
    {
        id = ulong.Parse(e.RunId, System.Globalization.CultureInfo.InvariantCulture),
        run_attempt = int.Parse(e.RunAttempt, System.Globalization.CultureInfo.InvariantCulture),
        repository = new { id = ulong.Parse(e.RepositoryId, System.Globalization.CultureInfo.InvariantCulture) },
        head_repository = new { id = ulong.Parse(e.RepositoryId, System.Globalization.CultureInfo.InvariantCulture) },
        head_sha = e.SourceCommit,
        path = ".github/workflows/package-artifacts.yml",
        @event = "workflow_dispatch"
    });
    private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage TruncatedResponse()
    {
        var content = new StreamContent(new MemoryStream([1, 2, 3]));
        content.Headers.ContentLength = 4;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
    private static void AssertNoSentinel(ArtifactTransportException error)
    {
        var emitted = string.Join('|', error.Message, error.Code, error.Operation, error.Field, error.Expected, error.Observed, error.CauseKind);
        Assert.DoesNotContain("sentinel", emitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", emitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", emitted, StringComparison.OrdinalIgnoreCase);
    }
    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class QueueHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(request.RequestUri!, request.Headers.Authorization,
                request.Headers.SelectMany(h => h.Value.Select(v => KeyValuePair.Create(h.Key, v))).ToArray()));
            return Task.FromResult(respond(request, Requests.Count - 1));
        }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed record RequestSnapshot(Uri Uri, AuthenticationHeaderValue? Authorization, KeyValuePair<string, string>[] Headers)
    { public string PathAndQuery => Uri.PathAndQuery; }

    private sealed class HangingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            return ReadAfterCancellationAsync(cancellationToken);
        }
        private static async ValueTask<int> ReadAfterCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period); _timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue(_now);
        }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            private DateTimeOffset? _due = due == Timeout.InfiniteTimeSpan ? null : owner._now + due;
            private TimeSpan _period = period;
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) { _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime; _period = newPeriod; return true; }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public bool FireIfDue(DateTimeOffset now)
            {
                if (_due is not { } dueAt || dueAt > now) return false;
                callback(state); _due = _period == Timeout.InfiniteTimeSpan ? null : now + _period; return true;
            }
        }
    }
}
