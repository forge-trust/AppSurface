using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgeTrust.AppSurface.PackageIndex;

/// <summary>Identity expected independently from the protected producer receipt.</summary>
/// <param name="Repository">Canonical owner/repository name.</param>
/// <param name="RepositoryId">Canonical decimal repository ID.</param>
/// <param name="RunId">Canonical decimal workflow run ID.</param>
/// <param name="RunAttempt">Canonical positive decimal attempt.</param>
/// <param name="SourceCommit">Expected 40-character source commit.</param>
/// <param name="ArtifactId">Canonical decimal artifact ID.</param>
internal sealed record ArtifactExpectedContext(string Repository, string RepositoryId, string RunId,
    string RunAttempt, string SourceCommit, string ArtifactId);

/// <summary>Exact schema-v1 producer identity and upload digest.</summary>
/// <param name="Schema">Versioned receipt schema.</param>
/// <param name="Repository">Repository name.</param>
/// <param name="RepositoryId">Repository ID.</param>
/// <param name="WorkflowPath">Protected workflow path.</param>
/// <param name="Event">Producer event.</param>
/// <param name="ProducerJob">Protected producer job name.</param>
/// <param name="RunId">Workflow run ID.</param>
/// <param name="RunAttempt">Workflow run attempt.</param>
/// <param name="SourceCommit">Producer source commit.</param>
/// <param name="ArtifactId">Uploaded artifact ID.</param>
/// <param name="ArchiveSha256">SHA-256 of the uploaded ZIP bytes.</param>
internal sealed record ProducerArtifactReceipt(string Schema, string Repository, string RepositoryId,
    string WorkflowPath, string Event, string ProducerJob, string RunId, string RunAttempt,
    string SourceCommit, string ArtifactId, string ArchiveSha256);

/// <summary>A streamed archive whose digest and exact identity have been verified.</summary>
/// <param name="Receipt">Bound producer receipt.</param>
/// <param name="ArchivePath">Owned temporary archive path; ownership transfers to the caller.</param>
/// <param name="ArchiveBytes">Actual streamed byte count.</param>
internal sealed record VerifiedArtifactArchive(ProducerArtifactReceipt Receipt, string ArchivePath, long ArchiveBytes);

/// <summary>Safe, stable transport refusal with allowlisted metadata only.</summary>
internal sealed class ArtifactTransportException : Exception
{
    /// <summary>Creates a refusal without incorporating untrusted exception text.</summary>
    public ArtifactTransportException(string code, string operation, string? field = null,
        string? expected = null, string? observed = null, int? statusCode = null, string? causeKind = null)
        : base("GitHub artifact verification was refused.")
    {
        Code = code; Operation = operation; Field = field; Expected = expected; Observed = observed;
        StatusCode = statusCode; CauseKind = causeKind;
    }
    /// <summary>Canonical refusal code.</summary>
    public string Code { get; }
    /// <summary>Fixed verifier stage.</summary>
    public string Operation { get; }
    /// <summary>Validated identity field, when applicable.</summary>
    public string? Field { get; }
    /// <summary>Safe expected value, when applicable.</summary>
    public string? Expected { get; }
    /// <summary>Safe observed value, when applicable.</summary>
    public string? Observed { get; }
    /// <summary>HTTP status, when applicable.</summary>
    public int? StatusCode { get; }
    /// <summary>Allowlisted failure kind, when applicable.</summary>
    public string? CauseKind { get; }
}

/// <summary>Verifies GitHub Actions artifact identity, metadata, and raw ZIP bytes.</summary>
internal sealed class GitHubActionsArtifactVerifier : IDisposable
{
    private const string ApiRoot = "https://api.github.com";
    private const string ApiVersion = "2026-03-10";
    private const string ReceiptSchema = "appsurface-github-actions-producer-receipt-v1";
    private const int MaxJsonBytes = 16 * 1024 * 1024;
    private const long MaxArchiveBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan OverallLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IoLimit = TimeSpan.FromSeconds(30);
    private readonly HttpClient _client;
    private readonly TimeProvider _time;
    private readonly long _maxArchiveBytes;
    private readonly int _maxJsonBytes;
    private readonly TimeSpan _overallLimit;
    private readonly TimeSpan _ioLimit;

    /// <summary>Creates a verifier over a caller-owned handler and controllable clock.</summary>
    /// <param name="handler">Handler used for requests; it remains owned and disposed by the caller.</param>
    /// <param name="timeProvider">Optional clock used for overall and inactivity deadlines.</param>
    public GitHubActionsArtifactVerifier(HttpMessageHandler handler, TimeProvider? timeProvider = null)
        : this(handler, timeProvider, MaxJsonBytes, MaxArchiveBytes, OverallLimit, IoLimit) { }

    internal GitHubActionsArtifactVerifier(HttpMessageHandler handler, TimeProvider? timeProvider,
        int maxJsonBytes, long maxArchiveBytes, TimeSpan overallLimit, TimeSpan ioLimit)
        : this(handler, timeProvider, maxJsonBytes, maxArchiveBytes, overallLimit, ioLimit, disposeHandler: false) { }

    private GitHubActionsArtifactVerifier(HttpMessageHandler handler, TimeProvider? timeProvider,
        int maxJsonBytes, long maxArchiveBytes, TimeSpan overallLimit, TimeSpan ioLimit, bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _time = timeProvider ?? TimeProvider.System;
        _maxJsonBytes = maxJsonBytes; _maxArchiveBytes = maxArchiveBytes;
        _overallLimit = overallLimit; _ioLimit = ioLimit;
        _client = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Creates a verifier that owns a production handler with redirects, cookies, ambient credentials, and decompression disabled.</summary>
    public static GitHubActionsArtifactVerifier CreateProduction(TimeProvider? timeProvider = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        return new GitHubActionsArtifactVerifier(handler, timeProvider, MaxJsonBytes, MaxArchiveBytes, OverallLimit, IoLimit, disposeHandler: true);
    }

    /// <summary>Checks required caller identity before receipt parsing or any HTTP operation.</summary>
    internal static void ValidateExpected(ArtifactExpectedContext expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!IsRepository(expected.Repository) || !IsPositiveDecimal(expected.RepositoryId) || !IsPositiveDecimal(expected.RunId) ||
            !IsPositiveDecimal(expected.RunAttempt) || !IsPositiveDecimal(expected.ArtifactId) ||
            expected.SourceCommit.Length != 40 || !IsLowerHex(expected.SourceCommit))
            throw Failure("artifact-input-invalid", "input");
    }

    /// <summary>Verifies the exact artifact and returns ownership of its streamed ZIP on success.</summary>
    public async Task<VerifiedArtifactArchive> VerifyAsync(ArtifactExpectedContext expected, string receiptPath,
        string token, string scratchDirectory, CancellationToken cancellationToken = default)
    {
        ValidateExpected(expected);
        if (string.IsNullOrWhiteSpace(receiptPath) || string.IsNullOrWhiteSpace(token) ||
            token.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            string.IsNullOrWhiteSpace(scratchDirectory))
            throw Failure("artifact-input-invalid", "input");
        var receipt = await ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        BindReceipt(receipt, expected);
        Directory.CreateDirectory(scratchDirectory);
        var fullScratch = Path.GetFullPath(scratchDirectory);
        var archivePath = Path.Combine(fullScratch, $"artifact-{Guid.NewGuid():N}.zip");
        using var deadline = NewTimeout(_overallLimit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var requests = new RequestCounter();
        try
        {
            var apiToken = linked.Token;
            var artifactUri = new Uri($"{ApiRoot}/repos/{expected.Repository}/actions/artifacts/{expected.ArtifactId}");
            using var artifact = await SendApiAsync(artifactUri, token, "artifact-metadata", requests, apiToken).ConfigureAwait(false);
            await RequireApiSuccess(artifact, "artifact-metadata", apiToken).ConfigureAwait(false);
            using var artifactJson = await ReadJsonAsync(artifact, "artifact-metadata", apiToken).ConfigureAwait(false);
            var digest = ValidateArtifactMetadata(artifactJson.RootElement, expected, receipt);

            var attemptUri = new Uri($"{ApiRoot}/repos/{expected.Repository}/actions/runs/{expected.RunId}/attempts/{expected.RunAttempt}");
            using var attempt = await SendApiAsync(attemptUri, token, "attempt-metadata", requests, apiToken).ConfigureAwait(false);
            await RequireApiSuccess(attempt, "attempt-metadata", apiToken).ConfigureAwait(false);
            using var attemptJson = await ReadJsonAsync(attempt, "attempt-metadata", apiToken).ConfigureAwait(false);
            ValidateAttempt(attemptJson.RootElement, expected);

            var zipUri = new Uri($"{ApiRoot}/repos/{expected.Repository}/actions/artifacts/{expected.ArtifactId}/zip");
            var bytes = await DownloadAsync(zipUri, token, receipt, digest, archivePath, requests, apiToken).ConfigureAwait(false);
            return new VerifiedArtifactArchive(receipt, archivePath, bytes);
        }
        catch (ArtifactTransportException) { TryDelete(archivePath); throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { TryDelete(archivePath); throw Failure("artifact-cancelled", "transport", cause: "caller-cancelled"); }
        catch (OperationCanceledException)
        { TryDelete(archivePath); throw Failure("artifact-budget-exceeded", "transport", field: "time", cause: "deadline"); }
        catch { TryDelete(archivePath); throw; }
    }

    private async Task<HttpResponseMessage> SendApiAsync(Uri uri, string token, string operation, RequestCounter count, CancellationToken ct)
    {
        if (++count.Value > 6) throw Failure("artifact-budget-exceeded", operation, field: "requests");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("AppSurface-ArtifactVerifier");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        using var header = NewTimeout(_ioLimit, ct);
        try { return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, header.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure("artifact-budget-exceeded", operation, field: "headers", cause: "header-timeout"); }
        catch (HttpRequestException ex) { throw Failure("artifact-api-unavailable", operation, cause: SafeCause(ex)); }
    }

    private async Task RequireApiSuccess(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.OK) return;
        throw await ClassifyApiFailureAsync(response, operation, api: true, ct).ConfigureAwait(false);
    }

    private async Task<ArtifactTransportException> ClassifyApiFailureAsync(HttpResponseMessage response, string operation, bool api, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (!api) return Failure("artifact-download-failed", operation, status: status);
        string code;
        if (status == 429) code = "artifact-rate-limited";
        else if (status == 403 && (response.Headers.Contains("Retry-After") ||
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remain) && remain.Any(v => v == "0") ||
            await HasRateLimitMessageAsync(response, operation, ct).ConfigureAwait(false)))
            code = "artifact-rate-limited";
        else if (status == 401 || status == 403) code = "artifact-permission-denied";
        else if (status == 404) code = "artifact-not-found-or-inaccessible";
        else if (status == 410) code = "artifact-expired";
        else if (status >= 500) code = "artifact-api-unavailable";
        else if (status is 301 or 302 or 303 or 307 or 308) code = "artifact-redirect-invalid";
        else code = "artifact-api-response-invalid";
        return Failure(code, operation, status: status);
    }

    private async Task<bool> HasRateLimitMessageAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bytes = new byte[8192]; var count = 0;
            while (count < bytes.Length)
            {
                using var idle = NewTimeout(_ioLimit, ct);
                int read;
                try { read = await stream.ReadAsync(bytes.AsMemory(count, bytes.Length - count), idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw Failure("artifact-budget-exceeded", operation, field: "inactivity", cause: "stream-timeout"); }
                if (read == 0) break;
                count += read;
            }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                var value = message.GetString()!;
                return value.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw Failure("artifact-api-unavailable", operation, cause: SafeCause(ex));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return false;
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > _maxJsonBytes)
            throw Failure("artifact-budget-exceeded", operation, field: "json-bytes");
        Stream source;
        try { source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { throw Failure("artifact-api-unavailable", operation, cause: SafeCause(ex)); }
        try
        {
            await using (source.ConfigureAwait(false))
            {
                using var memory = new MemoryStream();
                var buffer = new byte[81920];
                while (true)
                {
                    using var idle = NewTimeout(_ioLimit, ct);
                    int read;
                    try { read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure("artifact-budget-exceeded", operation, field: "inactivity", cause: "stream-timeout"); }
                    catch (IOException ex) { throw Failure("artifact-api-unavailable", operation, cause: SafeCause(ex)); }
                    if (read == 0) break;
                    if (memory.Length + read > _maxJsonBytes) throw Failure("artifact-budget-exceeded", operation, field: "json-bytes");
                    memory.Write(buffer, 0, read);
                }
                try
                {
                    var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
                    CheckDuplicates(document.RootElement, operation);
                    return document;
                }
                catch (ArtifactTransportException) { throw; }
                catch (JsonException) { throw Failure("artifact-api-response-invalid", operation); }
            }
        }
        catch (ArtifactTransportException) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { throw Failure("artifact-api-unavailable", operation, cause: SafeCause(ex)); }
    }

    private async Task<long> DownloadAsync(Uri uri, string token, ProducerArtifactReceipt receipt, string apiDigest,
        string archivePath, RequestCounter count, CancellationToken ct)
    {
        var current = uri;
        var redirects = 0;
        while (true)
        {
            if (++count.Value > 6) throw Failure("artifact-budget-exceeded", "archive-download", field: "requests");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var apiRequest = redirects == 0;
            if (apiRequest)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.UserAgent.ParseAdd("AppSurface-ArtifactVerifier");
                request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
            }
            using var headers = NewTimeout(_ioLimit, ct);
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure("artifact-budget-exceeded", "archive-download", field: "headers", cause: "header-timeout"); }
            catch (HttpRequestException ex)
            {
                throw Failure(apiRequest ? "artifact-api-unavailable" : "artifact-download-failed", "archive-download", cause: SafeCause(ex));
            }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects >= 3 || response.Headers.Location is null || !Uri.TryCreate(current, response.Headers.Location, out var next) ||
                        next.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(next.UserInfo))
                        throw Failure("artifact-redirect-invalid", "archive-download", status: (int)response.StatusCode);
                    current = next; redirects++; continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw await ClassifyApiFailureAsync(response, "archive-download", api: apiRequest, ct).ConfigureAwait(false);
                if (response.Content.Headers.ContentLength > _maxArchiveBytes)
                    throw Failure("artifact-budget-exceeded", "archive-download", field: "archive-bytes");
                Stream input;
                try { input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                { throw Failure("artifact-download-failed", "archive-download", status: (int)response.StatusCode, cause: SafeCause(ex)); }
                await using (input.ConfigureAwait(false))
                {
                    FileStream output;
                    try { output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { throw Failure("artifact-output-failed", "archive-download", cause: SafeCause(ex)); }
                    await using (output.ConfigureAwait(false))
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81920]; long total = 0;
                        while (true)
                        {
                            using var idle = NewTimeout(_ioLimit, ct);
                            int read;
                            try { read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Failure("artifact-budget-exceeded", "archive-download", field: "inactivity", cause: "stream-timeout"); }
                            catch (Exception ex) when (ex is IOException or HttpRequestException)
                            { throw Failure("artifact-download-failed", "archive-download", status: (int)response.StatusCode, cause: SafeCause(ex)); }
                            if (read == 0) break;
                            total += read;
                            if (total > _maxArchiveBytes) throw Failure("artifact-budget-exceeded", "archive-download", field: "archive-bytes");
                            hash.AppendData(buffer, 0, read);
                            try { await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            { throw Failure("artifact-output-failed", "archive-download", cause: SafeCause(ex)); }
                        }
                        try { await output.FlushAsync(ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { throw Failure("artifact-output-failed", "archive-download", cause: SafeCause(ex)); }
                        if (response.Content.Headers.ContentLength is { } declared && declared != total)
                            throw Failure("artifact-download-failed", "archive-download", status: (int)response.StatusCode, field: "content-length");
                        var actual = "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                        var expected = receipt.ArchiveSha256;
                        if (!actual.Equals(expected, StringComparison.Ordinal) || !actual.Equals(apiDigest, StringComparison.Ordinal))
                            throw Failure("artifact-bytes-digest-mismatch", "archive-download", field: "archiveSha256", expected: expected, observed: actual);
                        return total;
                    }
                }
            }
        }
    }

    private string ValidateArtifactMetadata(JsonElement root, ArtifactExpectedContext expected, ProducerArtifactReceipt receipt)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Failure("artifact-api-response-invalid", "artifact-metadata");
        var id = RequireDecimalProperty(root, "id", "artifact-metadata");
        if (id != expected.ArtifactId) throw Failure("artifact-api-response-invalid", "artifact-metadata", field: "id");
        if (!root.TryGetProperty("expired", out var expired) || expired.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Failure("artifact-api-response-invalid", "artifact-metadata", field: "expired");
        if (expired.ValueKind == JsonValueKind.True) throw Failure("artifact-expired", "artifact-metadata", field: "expired");
        var run = RequireObject(root, "workflow_run", "artifact-metadata");
        Compare(RequireDecimalProperty(run, "id", "artifact-metadata"), expected.RunId, "artifact-run-mismatch", "artifact-metadata", "runId");
        Compare(RequireDecimalProperty(run, "repository_id", "artifact-metadata"), expected.RepositoryId, "artifact-repository-mismatch", "artifact-metadata", "repositoryId");
        Compare(RequireDecimalProperty(run, "head_repository_id", "artifact-metadata"), expected.RepositoryId, "artifact-repository-mismatch", "artifact-metadata", "headRepositoryId");
        var source = RequireStringProperty(run, "head_sha", "artifact-metadata");
        if (source.Length != 40 || !IsLowerHex(source)) throw Failure("artifact-api-response-invalid", "artifact-metadata", field: "head_sha");
        Compare(source, expected.SourceCommit, "artifact-source-mismatch", "artifact-metadata", "sourceCommit");
        if (!root.TryGetProperty("expires_at", out var expires) || expires.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry))
            throw Failure("artifact-api-response-invalid", "artifact-metadata", field: "expires_at");
        if (expiry <= _time.GetUtcNow()) throw Failure("artifact-expired", "artifact-metadata", field: "expires_at");
        var digest = RequireStringProperty(root, "digest", "artifact-metadata");
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !IsLowerHex(digest.AsSpan(7)))
            throw Failure("artifact-api-response-invalid", "artifact-metadata", field: "digest");
        if (!digest.Equals(receipt.ArchiveSha256, StringComparison.Ordinal))
            throw Failure("artifact-metadata-digest-mismatch", "artifact-metadata", field: "archiveSha256", expected: receipt.ArchiveSha256, observed: digest);
        return digest;
    }

    private static void ValidateAttempt(JsonElement root, ArtifactExpectedContext expected)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Failure("artifact-api-response-invalid", "attempt-metadata");
        Compare(RequireDecimalProperty(root, "id", "attempt-metadata"), expected.RunId, "artifact-run-mismatch", "attempt-metadata", "runId");
        Compare(RequireDecimalProperty(root, "run_attempt", "attempt-metadata"), expected.RunAttempt, "artifact-attempt-mismatch", "attempt-metadata", "runAttempt");
        var repository = RequireObject(root, "repository", "attempt-metadata");
        Compare(RequireDecimalProperty(repository, "id", "attempt-metadata"), expected.RepositoryId, "artifact-repository-mismatch", "attempt-metadata", "repositoryId");
        var head = RequireObject(root, "head_repository", "attempt-metadata");
        Compare(RequireDecimalProperty(head, "id", "attempt-metadata"), expected.RepositoryId, "artifact-repository-mismatch", "attempt-metadata", "headRepositoryId");
        var source = RequireStringProperty(root, "head_sha", "attempt-metadata");
        if (source.Length != 40 || !IsLowerHex(source)) throw Failure("artifact-api-response-invalid", "attempt-metadata", field: "head_sha");
        Compare(source, expected.SourceCommit, "artifact-source-mismatch", "attempt-metadata", "sourceCommit");
        if (RequireStringProperty(root, "path", "attempt-metadata") != ".github/workflows/package-artifacts.yml")
            throw Failure("artifact-workflow-mismatch", "attempt-metadata", field: "workflowPath", expected: ".github/workflows/package-artifacts.yml", observed: "unexpected");
        if (RequireStringProperty(root, "event", "attempt-metadata") != "workflow_dispatch")
            throw Failure("artifact-workflow-mismatch", "attempt-metadata", field: "event", expected: "workflow_dispatch", observed: "unexpected");
    }

    private static async Task<ProducerArtifactReceipt> ReadReceiptAsync(string path, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (stream.Length > MaxJsonBytes) throw Failure("artifact-budget-exceeded", "receipt", field: "json-bytes");
            using var memory = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (memory.Length + read > MaxJsonBytes) throw Failure("artifact-budget-exceeded", "receipt", field: "json-bytes");
                memory.Write(buffer, 0, read);
            }
            bytes = memory.ToArray();
        }
        catch (ArtifactTransportException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw Failure("artifact-cancelled", "receipt", cause: "caller-cancelled"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Failure("artifact-input-invalid", "receipt", cause: SafeCause(ex)); }
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            CheckDuplicates(json.RootElement, "receipt");
            var root = json.RootElement;
            string[] fields = ["schema", "repository", "repositoryId", "workflowPath", "event", "producerJob", "runId", "runAttempt", "sourceCommit", "artifactId", "archiveSha256"];
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length || fields.Any(f => !root.TryGetProperty(f, out _)))
                throw Failure("artifact-receipt-invalid", "receipt");
            string Get(string key) => root.GetProperty(key).ValueKind == JsonValueKind.String ? root.GetProperty(key).GetString()! : throw Failure("artifact-receipt-invalid", "receipt", field: key);
            var receipt = new ProducerArtifactReceipt(Get("schema"), Get("repository"), Get("repositoryId"), Get("workflowPath"), Get("event"), Get("producerJob"), Get("runId"), Get("runAttempt"), Get("sourceCommit"), Get("artifactId"), Get("archiveSha256"));
            if (receipt.Schema != ReceiptSchema || !IsRepository(receipt.Repository) || !IsPositiveDecimal(receipt.RepositoryId) ||
                !IsPositiveDecimal(receipt.RunId) || !IsPositiveDecimal(receipt.RunAttempt) || !IsPositiveDecimal(receipt.ArtifactId) ||
                receipt.SourceCommit.Length != 40 || !IsLowerHex(receipt.SourceCommit) || receipt.ArchiveSha256.Length != 71 ||
                !receipt.ArchiveSha256.StartsWith("sha256:", StringComparison.Ordinal) || !IsLowerHex(receipt.ArchiveSha256.AsSpan(7)))
                throw Failure("artifact-receipt-invalid", "receipt");
            return receipt;
        }
        catch (ArtifactTransportException) { throw; }
        catch (JsonException) { throw Failure("artifact-receipt-invalid", "receipt"); }
    }

    private static void BindReceipt(ProducerArtifactReceipt receipt, ArtifactExpectedContext expected)
    {
        Check(receipt.Repository, expected.Repository, "repository"); Check(receipt.RepositoryId, expected.RepositoryId, "repositoryId");
        Check(receipt.RunId, expected.RunId, "runId"); Check(receipt.RunAttempt, expected.RunAttempt, "runAttempt", true);
        Check(receipt.SourceCommit, expected.SourceCommit, "sourceCommit"); Check(receipt.ArtifactId, expected.ArtifactId, "artifactId");
        if (receipt.WorkflowPath != ".github/workflows/package-artifacts.yml" || receipt.Event != "workflow_dispatch" || receipt.ProducerJob != "package-artifacts")
            throw Failure("artifact-receipt-binding-mismatch", "receipt", field: "workflowPolicy");
        void Check(string observed, string expectedValue, string field, bool attempt = false)
        {
            if (observed != expectedValue) throw Failure(attempt ? "artifact-attempt-mismatch" : "artifact-receipt-binding-mismatch", "receipt", field, expectedValue, observed);
        }
    }

    private static JsonElement RequireObject(JsonElement value, string name, string operation)
        => value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.Object ? result : throw Failure("artifact-api-response-invalid", operation, field: name);
    private static string RequireStringProperty(JsonElement value, string name, string operation)
        => value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.String && result.GetString() is { } text ? text : throw Failure("artifact-api-response-invalid", operation, field: name);
    private static string RequireDecimalProperty(JsonElement value, string name, string operation)
    {
        if (!value.TryGetProperty(name, out var item)) throw Failure("artifact-api-response-invalid", operation, field: name);
        var text = item.ValueKind == JsonValueKind.Number ? item.GetRawText() : null;
        if (!IsPositiveDecimal(text)) throw Failure("artifact-api-response-invalid", operation, field: name);
        return text!;
    }
    private static void Compare(string observed, string expected, string code, string operation, string field)
    { if (observed != expected) throw Failure(code, operation, field, expected, observed); }
    private static void CheckDuplicates(JsonElement value, string operation)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Failure(operation == "receipt" ? "artifact-receipt-invalid" : "artifact-api-response-invalid", operation);
                CheckDuplicates(property.Value, operation);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicates(item, operation);
    }
    private static bool IsRepository(string value) => value.Split('/') is [var owner, var name] && IsRepoPart(owner) && IsRepoPart(name);
    private static bool IsRepoPart(string value) => value.Length > 0 && value is not "." and not ".." &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static bool IsDecimal(string? value) => value is { Length: > 0 } && (value == "0" || value[0] is >= '1' and <= '9') && value.All(char.IsAsciiDigit);
    private static bool IsPositiveDecimal(string? value) => IsDecimal(value) && value != "0";
    private static bool IsLowerHex(string value) => IsLowerHex(value.AsSpan());
    private static bool IsLowerHex(ReadOnlySpan<char> value) => value.Length > 0 && value.ToString().All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    private static ArtifactTransportException Failure(string code, string operation, string? field = null, string? expected = null, string? observed = null, int? status = null, string? cause = null)
        => new(code, operation, field, expected, observed, status, cause);
    private static string SafeCause(Exception ex) => ex switch { HttpRequestException => "network", IOException => "io", UnauthorizedAccessException => "access", _ => "unexpected-local" };
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private TimeoutScope NewTimeout(TimeSpan timeout, CancellationToken parent = default)
    {
        var source = parent.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parent)
            : new CancellationTokenSource();
        var timer = _time.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), source, timeout, Timeout.InfiniteTimeSpan);
        return new TimeoutScope(source, timer);
    }

    private sealed class RequestCounter { public int Value; }
    private sealed class TimeoutScope(CancellationTokenSource source, ITimer timer) : IDisposable
    {
        public CancellationToken Token => source.Token;
        public void Dispose() { timer.Dispose(); source.Dispose(); }
    }
    public void Dispose() => _client.Dispose();
}
