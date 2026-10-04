using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;

namespace ForgeTrust.AppSurface.EvidenceGate;

/// <summary>Reads current PR revisions and workflow-job provenance from the GitHub REST API.</summary>
internal sealed class GitHubActionsEvidenceAuthorityProvider : IEvidencePullRequestGateAuthorityProvider, IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly Uri _apiBaseAddress;
    private readonly IEvidenceSubjectJobCheckoutAttestationProvider? _checkoutAttestationProvider;

    /// <summary>Creates a provider over a caller-owned HTTP client and optional trusted checkout attestor.</summary>
    internal GitHubActionsEvidenceAuthorityProvider(
        HttpClient httpClient,
        Uri apiBaseAddress,
        IEvidenceSubjectJobCheckoutAttestationProvider? checkoutAttestationProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        if (!apiBaseAddress.IsAbsoluteUri || apiBaseAddress.Scheme != Uri.UriSchemeHttps || apiBaseAddress.UserInfo.Length != 0)
        {
            throw new ArgumentException("The GitHub API base address must be an absolute HTTPS URI without user information.", nameof(apiBaseAddress));
        }

        _apiBaseAddress = new Uri(apiBaseAddress.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _checkoutAttestationProvider = checkoutAttestationProvider;
    }

    /// <summary>Creates the production provider from trusted GitHub Actions environment values.</summary>
    internal static GitHubActionsEvidenceAuthorityProvider? TryCreateFromEnvironment() =>
        TryCreateFromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>Creates a provider from a caller-supplied environment reader for deterministic validation.</summary>
    /// <remarks>
    /// The caller must supply only trusted process environment values. A missing or malformed
    /// <see href="https://www.rfc-editor.org/rfc/rfc6750#section-2.1">Bearer credential</see> or
    /// HTTPS API endpoint returns <see langword="null"/>; this factory does not attest a subject checkout.
    /// Its fresh API snapshot may authorize only an explicitly empty profile, because the checkout revision
    /// remains blank until an independent subject-job attestor is registered.
    /// The returned provider owns its HTTP client and must be disposed.
    /// </remarks>
    internal static GitHubActionsEvidenceAuthorityProvider? TryCreateFromEnvironment(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        var token = readEnvironment("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token) || token.Length > 4096 || !IsValidBearerToken(token))
        {
            return null;
        }

        var apiAddressText = readEnvironment("GITHUB_API_URL") ?? "https://api.github.com";
        if (!Uri.TryCreate(apiAddressText, UriKind.Absolute, out var apiAddress)
            || apiAddress.Scheme != Uri.UriSchemeHttps
            || apiAddress.UserInfo.Length != 0
            || apiAddress.Query.Length != 0
            || apiAddress.Fragment.Length != 0)
        {
            return null;
        }

        try
        {
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(9),
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeTrust-AppSurface-EvidenceGate/1.0");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
            return new GitHubActionsEvidenceAuthorityProvider(client, apiAddress);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsValidBearerToken(string token)
    {
        var paddingStarted = false;
        foreach (var character in token)
        {
            if (character == '=')
            {
                paddingStarted = true;
                continue;
            }

            if (paddingStarted || character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                    or '-' or '.' or '_' or '~' or '+' or '/'))
            {
                return false;
            }
        }

        return token[0] != '=';
    }

    /// <inheritdoc />
    public async Task<EvidencePullRequestGateAuthoritySnapshot?> ReadFreshAsync(
        EvidencePullRequestGateExpectedIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        try
        {
            var repository = expectedIdentity.Repository.Split('/', 2);
            if (repository.Length != 2
                || !long.TryParse(expectedIdentity.WorkflowId, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedWorkflowId)
                || !long.TryParse(expectedIdentity.SubjectJobId, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedJobId))
            {
                return null;
            }

            var repositoryPath = $"repos/{Uri.EscapeDataString(repository[0])}/{Uri.EscapeDataString(repository[1])}";
            var runIdentity = expectedIdentity.RunIdentity;
            using var pullRequest = await GetJsonAsync(
                $"{repositoryPath}/pulls/{runIdentity.PullRequestNumber.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(false);
            using var workflowRun = await GetJsonAsync(
                $"{repositoryPath}/actions/runs/{runIdentity.WorkflowRunId.ToString(CultureInfo.InvariantCulture)}/attempts/{runIdentity.WorkflowRunAttempt.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(false);
            using var jobs = await GetJsonAsync(
                $"{repositoryPath}/actions/runs/{runIdentity.WorkflowRunId.ToString(CultureInfo.InvariantCulture)}/attempts/{runIdentity.WorkflowRunAttempt.ToString(CultureInfo.InvariantCulture)}/jobs?per_page=100",
                cancellationToken).ConfigureAwait(false);

            if (pullRequest is null || workflowRun is null || jobs is null
                || !TryGetInt64(pullRequest.RootElement, "number", out var pullRequestNumber)
                || !TryGetObject(pullRequest.RootElement, "base", out var baseObject)
                || !TryGetObject(pullRequest.RootElement, "head", out var headObject)
                || !TryGetObject(baseObject, "repo", out var baseRepository)
                || !TryGetObject(headObject, "repo", out var headRepository)
                || !TryGetInt64(baseRepository, "id", out var baseRepositoryId)
                || !TryGetInt64(headRepository, "id", out var headRepositoryId)
                || !TryGetString(baseObject, "ref", out var targetBranch)
                || !TryGetString(baseObject, "sha", out var baseRevision)
                || !TryGetString(headObject, "sha", out var headRevision)
                || !TryGetInt64(workflowRun.RootElement, "id", out var workflowRunId)
                || !TryGetInt64(workflowRun.RootElement, "run_attempt", out var runAttempt)
                || !TryGetInt64(workflowRun.RootElement, "workflow_id", out var workflowId)
                || !TryGetString(workflowRun.RootElement, "event", out var eventName)
                || !TryGetObject(workflowRun.RootElement, "repository", out var runRepository)
                || !TryGetString(runRepository, "full_name", out var runRepositoryName)
                || !string.Equals(runRepositoryName, expectedIdentity.Repository, StringComparison.OrdinalIgnoreCase)
                || pullRequestNumber != runIdentity.PullRequestNumber
                || baseRepositoryId != runIdentity.RepositoryId
                || headRepositoryId != runIdentity.HeadRepositoryId
                || workflowRunId != runIdentity.WorkflowRunId
                || runAttempt != runIdentity.WorkflowRunAttempt
                || workflowId != expectedWorkflowId)
            {
                return null;
            }

            if (!TryGetArray(jobs.RootElement, "jobs", out var jobItems))
            {
                return null;
            }

            JsonElement selectedJob = default;
            var jobFound = false;
            foreach (var job in jobItems.EnumerateArray())
            {
                if (TryGetInt64(job, "id", out var jobId) && jobId == expectedJobId)
                {
                    selectedJob = job;
                    jobFound = true;
                    break;
                }
            }

            if (!jobFound
                || !TryGetInt64(selectedJob, "run_id", out var jobRunId)
                || !TryGetString(selectedJob, "conclusion", out var conclusion)
                || jobRunId != workflowRunId
                // The attempt-specific jobs endpoint selects the attempt. GitHub's documented
                // job shape does not include run_attempt; validate it if the API supplies it.
                || (selectedJob.TryGetProperty("run_attempt", out _)
                    && (!TryGetInt64(selectedJob, "run_attempt", out var jobAttempt) || jobAttempt != runAttempt)))
            {
                return null;
            }

            var authorityIdentity = new EvidencePullRequestRunIdentity(
                baseRepositoryId,
                headRepositoryId,
                checked((int)pullRequestNumber),
                targetBranch,
                workflowRunId,
                checked((int)runAttempt));

            // GitHub workflow/job head_sha is event metadata, not proof of the checkout
            // consumed by a pull_request_target subject job. Leave that field empty when
            // no independent attestor exists. Only an explicitly empty profile can use
            // a fresh PR/run/job observation without a subject checkout.
            var checkoutRevision = string.Empty;
            if (_checkoutAttestationProvider is not null)
            {
                var attestation = await _checkoutAttestationProvider.AttestAsync(
                    expectedIdentity,
                    authorityIdentity,
                    expectedJobId.ToString(CultureInfo.InvariantCulture),
                    headRevision,
                    cancellationToken).ConfigureAwait(false);
                if (attestation is null
                    || !string.Equals(attestation.Repository, expectedIdentity.Repository, StringComparison.OrdinalIgnoreCase)
                    || attestation.RunIdentity != authorityIdentity
                    || !string.Equals(attestation.SubjectJobId, expectedJobId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    || !string.Equals(attestation.SubjectCheckoutRevision, headRevision, StringComparison.Ordinal))
                {
                    return null;
                }

                checkoutRevision = attestation.SubjectCheckoutRevision;
            }

            return new EvidencePullRequestGateAuthoritySnapshot(
                expectedIdentity.Repository,
                eventName,
                baseRevision,
                headRevision,
                workflowId.ToString(CultureInfo.InvariantCulture),
                authorityIdentity,
                expectedJobId.ToString(CultureInfo.InvariantCulture),
                checkoutRevision,
                conclusion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException
            and not AppDomainUnloadedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    private async Task<JsonDocument?> GetJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBaseAddress, relativePath));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (bounded.Length + read > MaximumResponseBytes)
            {
                return null;
            }

            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return JsonDocument.Parse(bounded.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static bool TryGetObject(JsonElement parent, string propertyName, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArray(JsonElement parent, string propertyName, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Array;
    }

    private static bool TryGetString(JsonElement parent, string propertyName, out string value)
    {
        value = string.Empty;
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length <= 4096;
    }

    private static bool TryGetInt64(JsonElement parent, string propertyName, out long value)
    {
        value = 0;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }
}

/// <summary>
/// Provides trusted evidence of the actual subject checkout associated with an authoritative workflow job.
/// </summary>
/// <remarks>
/// Implementations must independently bind the checked-out commit to the exact repository, workflow run,
/// attempt, and subject job. GitHub workflow-run and job <c>head_sha</c> fields are not sufficient evidence,
/// especially for <c>pull_request_target</c>. The provider accepts only an attestation for the current PR head.
/// </remarks>
internal interface IEvidenceSubjectJobCheckoutAttestationProvider
{
    /// <summary>Attests the actual checkout revision for one authoritative subject job.</summary>
    Task<EvidenceSubjectJobCheckoutAttestation?> AttestAsync(
        EvidencePullRequestGateExpectedIdentity expectedIdentity,
        EvidencePullRequestRunIdentity authoritativeRunIdentity,
        string subjectJobId,
        string currentPullRequestHeadRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>Trusted binding between a subject job identity and its actual checked-out revision.</summary>
internal sealed record EvidenceSubjectJobCheckoutAttestation(
    string Repository,
    EvidencePullRequestRunIdentity RunIdentity,
    string SubjectJobId,
    string SubjectCheckoutRevision);

/// <summary>Fail-closed authority provider used when trusted GitHub API credentials are unavailable.</summary>
internal sealed class UnavailableEvidenceAuthorityProvider : IEvidencePullRequestGateAuthorityProvider
{
    /// <inheritdoc />
    public Task<EvidencePullRequestGateAuthoritySnapshot?> ReadFreshAsync(
        EvidencePullRequestGateExpectedIdentity expectedIdentity,
        CancellationToken cancellationToken = default) => Task.FromResult<EvidencePullRequestGateAuthoritySnapshot?>(null);
}
