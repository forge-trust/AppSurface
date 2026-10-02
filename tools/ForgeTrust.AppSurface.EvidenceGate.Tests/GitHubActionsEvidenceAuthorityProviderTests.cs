using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.EvidenceGate;

namespace ForgeTrust.AppSurface.EvidenceGate.Tests;

public sealed class GitHubActionsEvidenceAuthorityProviderTests
{
    private const string Repository = "forge-trust/AppSurface";
    private const string BaseRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PullRequestHeadRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string PullRequestPath = "/repos/forge-trust/AppSurface/pulls/777";
    private const string WorkflowRunPath = "/repos/forge-trust/AppSurface/actions/runs/654321/attempts/1";
    private const string JobsPath = WorkflowRunPath + "/jobs?per_page=100";

    [Theory]
    [InlineData("token with spaces")]
    [InlineData("token,with,commas")]
    [InlineData("abc=def")]
    [InlineData("=abc")]
    [InlineData("abc\n")]
    public void EnvironmentFactoryReturnsNoProviderForMalformedBearerToken(string token)
    {
        var values = new Dictionary<string, string?>
        {
            ["GITHUB_TOKEN"] = token,
            ["GITHUB_API_URL"] = "https://api.example.test",
        };

        var provider = GitHubActionsEvidenceAuthorityProvider.TryCreateFromEnvironment(
            name => values.GetValueOrDefault(name));

        Assert.Null(provider);
    }

    [Fact]
    public void EnvironmentFactoryAcceptsBearerCredentialCharactersAndTrailingPadding()
    {
        var values = new Dictionary<string, string?>
        {
            ["GITHUB_TOKEN"] = "Ab09-._~+/==",
            ["GITHUB_API_URL"] = "https://api.example.test",
        };

        using var provider = GitHubActionsEvidenceAuthorityProvider.TryCreateFromEnvironment(
            name => values.GetValueOrDefault(name));

        Assert.NotNull(provider);
    }

    [Fact]
    public void ProviderRejectsApiBaseAddressesThatAreNotSafeHttpsEndpoints()
    {
        using var httpClient = new HttpClient();
        var insecureTransport = new UriBuilder(Uri.UriSchemeHttp, "api.example.test").Uri;
        var credentialBearingAddress = new UriBuilder(Uri.UriSchemeHttps, "api.example.test")
        {
            UserName = "synthetic-user",
            Password = "synthetic-password",
        }.Uri;

        foreach (var address in new[] { insecureTransport, credentialBearingAddress })
        {
            var exception = Assert.Throws<ArgumentException>(() =>
                new GitHubActionsEvidenceAuthorityProvider(httpClient, address));

            Assert.Equal("apiBaseAddress", exception.ParamName);
        }
    }

    [Fact]
    public async Task UnavailableProviderReturnsNoAuthoritySnapshot()
    {
        var provider = new UnavailableEvidenceAuthorityProvider();

        var snapshot = await provider.ReadFreshAsync(CreateExpectedIdentity());

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task ProviderReturnsFreshSnapshotAfterCheckoutIsBoundToCurrentRunAndJob()
    {
        var expectedIdentity = CreateExpectedIdentity();
        var httpHandler = new GitHubApiFixtureHandler(BaseRevision, PullRequestHeadRevision);
        using var httpClient = new HttpClient(httpHandler);
        var attestationProvider = new FixedCheckoutAttestationProvider(PullRequestHeadRevision);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(
            httpClient,
            new Uri("https://api.example.test/"),
            attestationProvider);

        var snapshot = await authorityProvider.ReadFreshAsync(expectedIdentity);

        Assert.NotNull(snapshot);
        Assert.Equal(Repository, snapshot.Repository);
        Assert.Equal(expectedIdentity.EventName, snapshot.EventName);
        Assert.Equal(BaseRevision, snapshot.BaseRevision);
        Assert.Equal(PullRequestHeadRevision, snapshot.HeadRevision);
        Assert.Equal(expectedIdentity.WorkflowId, snapshot.WorkflowId);
        Assert.Equal(expectedIdentity.RunIdentity, snapshot.RunIdentity);
        Assert.Equal(expectedIdentity.SubjectJobId, snapshot.SubjectJobId);
        Assert.Equal(PullRequestHeadRevision, snapshot.SubjectJobHeadRevision);
        Assert.Equal("success", snapshot.SubjectJobConclusion);
        Assert.Equal(new[] { PullRequestPath, WorkflowRunPath, JobsPath }, httpHandler.Requests);
        Assert.Equal(1, attestationProvider.CallCount);
        Assert.Equal(expectedIdentity, attestationProvider.LastExpectedIdentity);
        Assert.Equal(expectedIdentity.RunIdentity, attestationProvider.LastRunIdentity);
        Assert.Equal(expectedIdentity.SubjectJobId, attestationProvider.LastSubjectJobId);
        Assert.Equal(PullRequestHeadRevision, attestationProvider.LastHeadRevision);
    }

    [Fact]
    public async Task ProviderReportsFreshPrAndJobButNoCheckoutWithoutSeparateAttestor()
    {
        var handler = new GitHubApiFixtureHandler(BaseRevision, PullRequestHeadRevision);
        using var httpClient = new HttpClient(handler);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(httpClient, new Uri("https://api.example.test/"));

        var snapshot = await authorityProvider.ReadFreshAsync(CreateExpectedIdentity());

        Assert.NotNull(snapshot);
        Assert.Equal(BaseRevision, snapshot.BaseRevision);
        Assert.Equal(PullRequestHeadRevision, snapshot.HeadRevision);
        Assert.Equal(string.Empty, snapshot.SubjectJobHeadRevision);
        Assert.False(snapshot.SubjectEnvelopeAttested);
        Assert.Equal(new[] { PullRequestPath, WorkflowRunPath, JobsPath }, handler.Requests);
    }

    [Fact]
    public async Task ProviderAcceptsDocumentedJobShapeWithoutRunAttemptField()
    {
        var handler = new GitHubApiFixtureHandler(
            BaseRevision,
            PullRequestHeadRevision,
            (path, body) => path == JobsPath
                ? new ApiResponse(HttpStatusCode.OK, RemoveJsonProperty(body, "jobs.0.run_attempt"))
                : null);
        using var httpClient = new HttpClient(handler);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(httpClient, new Uri("https://api.example.test/"));

        var snapshot = await authorityProvider.ReadFreshAsync(CreateExpectedIdentity());

        Assert.NotNull(snapshot);
        Assert.Equal("success", snapshot.SubjectJobConclusion);
        Assert.Equal(string.Empty, snapshot.SubjectJobHeadRevision);
        Assert.Equal(new[] { PullRequestPath, WorkflowRunPath, JobsPath }, handler.Requests);
    }

    [Theory]
    [MemberData(nameof(AuthorityMismatchCases))]
    public async Task ProviderRejectsRepositoryRunAndJobAssociationsThatDoNotMatchExpectedIdentity(
        string responsePath,
        string jsonPath,
        string replacementJsonValue)
    {
        var handler = new GitHubApiFixtureHandler(
            BaseRevision,
            PullRequestHeadRevision,
            (path, body) => path == responsePath
                ? new ApiResponse(HttpStatusCode.OK, SetJsonValue(body, jsonPath, replacementJsonValue))
                : null);
        using var httpClient = new HttpClient(handler);
        var attestationProvider = new FixedCheckoutAttestationProvider(PullRequestHeadRevision);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(
            httpClient,
            new Uri("https://api.example.test/"),
            attestationProvider);

        var snapshot = await authorityProvider.ReadFreshAsync(CreateExpectedIdentity());

        Assert.Null(snapshot);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(0, attestationProvider.CallCount);
    }

    public static TheoryData<string, string, string> AuthorityMismatchCases => new()
    {
        { PullRequestPath, "number", "778" },
        { PullRequestPath, "base.repo.id", "999" },
        { PullRequestPath, "head.repo.id", "999" },
        { WorkflowRunPath, "repository.full_name", "\"another/repository\"" },
        { WorkflowRunPath, "id", "654322" },
        { WorkflowRunPath, "run_attempt", "2" },
        { WorkflowRunPath, "workflow_id", "1235" },
        { JobsPath, "jobs.0.id", "999" },
        { JobsPath, "jobs.0.run_id", "654322" },
        { JobsPath, "jobs.0.run_attempt", "2" },
    };

    [Theory]
    [MemberData(nameof(MalformedApiContractCases))]
    public async Task ProviderFailsClosedWhenApiResponseOmitsRequiredContractData(
        string responsePath,
        string jsonPath,
        string replacementJsonValue)
    {
        var handler = new GitHubApiFixtureHandler(
            BaseRevision,
            PullRequestHeadRevision,
            (path, body) => path == responsePath
                ? new ApiResponse(HttpStatusCode.OK, SetJsonValue(body, jsonPath, replacementJsonValue))
                : null);
        using var httpClient = new HttpClient(handler);
        var attestationProvider = new FixedCheckoutAttestationProvider(PullRequestHeadRevision);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(
            httpClient,
            new Uri("https://api.example.test/"),
            attestationProvider);

        var snapshot = await authorityProvider.ReadFreshAsync(CreateExpectedIdentity());

        Assert.Null(snapshot);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(0, attestationProvider.CallCount);
    }

    public static TheoryData<string, string, string> MalformedApiContractCases => new()
    {
        { PullRequestPath, "number", "null" },
        { PullRequestPath, "base", "null" },
        { PullRequestPath, "head", "null" },
        { PullRequestPath, "base.repo", "null" },
        { PullRequestPath, "head.repo", "null" },
        { PullRequestPath, "base.repo.id", "\"321\"" },
        { PullRequestPath, "head.repo.id", "null" },
        { PullRequestPath, "base.ref", "null" },
        { PullRequestPath, "base.sha", "null" },
        { PullRequestPath, "head.sha", "null" },
        { WorkflowRunPath, "id", "null" },
        { WorkflowRunPath, "run_attempt", "null" },
        { WorkflowRunPath, "workflow_id", "null" },
        { WorkflowRunPath, "event", "null" },
        { WorkflowRunPath, "repository", "null" },
        { WorkflowRunPath, "repository.full_name", "null" },
        { JobsPath, "jobs", "null" },
        { JobsPath, "jobs", "[null]" },
        { JobsPath, "jobs.0.id", "\"5678\"" },
        { JobsPath, "jobs.0.run_id", "null" },
        { JobsPath, "jobs.0.run_attempt", "null" },
        { JobsPath, "jobs.0.conclusion", "null" },
    };

    [Theory]
    [InlineData("repository")]
    [InlineData("workflow")]
    [InlineData("subject-job")]
    public async Task ProviderDoesNotQueryGitHubWhenControllerIdentityIsMalformed(string invalidField)
    {
        var expectedIdentity = invalidField switch
        {
            "repository" => CreateExpectedIdentity() with { Repository = "forge-trust" },
            "workflow" => CreateExpectedIdentity() with { WorkflowId = "workflow-x" },
            "subject-job" => CreateExpectedIdentity() with { SubjectJobId = "job-x" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField)),
        };
        var handler = new UnexpectedRequestHandler();
        using var httpClient = new HttpClient(handler);
        var attestationProvider = new FixedCheckoutAttestationProvider(PullRequestHeadRevision);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(
            httpClient,
            new Uri("https://api.example.test/"),
            attestationProvider);

        var snapshot = await authorityProvider.ReadFreshAsync(expectedIdentity);

        Assert.Null(snapshot);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(0, attestationProvider.CallCount);
    }

    [Theory]
    [InlineData("unavailable", 3)]
    [InlineData("oversized", 3)]
    [InlineData("malformed", 1)]
    public async Task ProviderFailsClosedForUnavailableOversizedOrMalformedApiResponse(
        string responseKind,
        int expectedRequestCount)
    {
        var handler = new GitHubApiFixtureHandler(
            BaseRevision,
            PullRequestHeadRevision,
            (path, body) => path != PullRequestPath
                ? null
                : responseKind switch
                {
                    "unavailable" => new ApiResponse(HttpStatusCode.ServiceUnavailable, body),
                    "oversized" => new ApiResponse(HttpStatusCode.OK, body + new string(' ', 1024 * 1024)),
                    "malformed" => new ApiResponse(HttpStatusCode.OK, "{"),
                    _ => throw new ArgumentOutOfRangeException(nameof(responseKind)),
                });
        using var httpClient = new HttpClient(handler);
        var attestationProvider = new FixedCheckoutAttestationProvider(PullRequestHeadRevision);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(
            httpClient,
            new Uri("https://api.example.test/"),
            attestationProvider);

        var snapshot = await authorityProvider.ReadFreshAsync(CreateExpectedIdentity());

        Assert.Null(snapshot);
        Assert.Equal(expectedRequestCount, handler.Requests.Count);
        Assert.Equal(0, attestationProvider.CallCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("repository")]
    [InlineData("run")]
    [InlineData("subject-job")]
    [InlineData("checkout-revision")]
    public async Task ProviderRejectsCheckoutAttestationNotBoundToTheCurrentAuthorityView(string mismatch)
    {
        var handler = new GitHubApiFixtureHandler(BaseRevision, PullRequestHeadRevision);
        using var httpClient = new HttpClient(handler);
        var attestationProvider = new FixedCheckoutAttestationProvider(PullRequestHeadRevision, mismatch);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(
            httpClient,
            new Uri("https://api.example.test/"),
            attestationProvider);

        var snapshot = await authorityProvider.ReadFreshAsync(CreateExpectedIdentity());

        Assert.Null(snapshot);
        Assert.Equal(1, attestationProvider.CallCount);
    }

    [Fact]
    public async Task ProviderPropagatesCancellationWhileReadingGitHubApi()
    {
        var handler = new CancellationAwareRequestHandler();
        using var httpClient = new HttpClient(handler);
        var attestationProvider = new FixedCheckoutAttestationProvider(PullRequestHeadRevision);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(
            httpClient,
            new Uri("https://api.example.test/"),
            attestationProvider);
        using var cancellationSource = new CancellationTokenSource();

        var readTask = authorityProvider.ReadFreshAsync(CreateExpectedIdentity(), cancellationSource.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await readTask);
        Assert.Equal(0, attestationProvider.CallCount);
    }

    [Fact]
    public async Task ProviderPropagatesCancellationFromCheckoutAttestor()
    {
        var handler = new GitHubApiFixtureHandler(BaseRevision, PullRequestHeadRevision);
        using var httpClient = new HttpClient(handler);
        using var cancellationSource = new CancellationTokenSource();
        var attestationProvider = new CancellingCheckoutAttestationProvider(cancellationSource);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(
            httpClient,
            new Uri("https://api.example.test/"),
            attestationProvider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await authorityProvider.ReadFreshAsync(CreateExpectedIdentity(), cancellationSource.Token));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, attestationProvider.CallCount);
    }

    private static EvidencePullRequestGateExpectedIdentity CreateExpectedIdentity() => new(
        Repository,
        "pull_request_target",
        "1234",
        "5678",
        new EvidencePullRequestRunIdentity(321, 321, 777, "main", 654321, 1));

    private static string SetJsonValue(string body, string propertyPath, string replacementJsonValue)
    {
        var root = JsonNode.Parse(body) ?? throw new InvalidOperationException("Fixture JSON must have a root value.");
        var propertyNames = propertyPath.Split('.');
        var parent = root;
        for (var index = 0; index < propertyNames.Length - 1; index++)
        {
            parent = parent switch
            {
                JsonArray array when int.TryParse(propertyNames[index], out var arrayIndex) => array[arrayIndex],
                JsonObject jsonObject => jsonObject[propertyNames[index]],
                _ => null,
            } ?? throw new InvalidOperationException($"Fixture JSON path '{propertyPath}' does not exist.");
        }

        var finalProperty = propertyNames[^1];
        if (parent is JsonArray finalArray && int.TryParse(finalProperty, out var finalIndex))
        {
            finalArray[finalIndex] = JsonNode.Parse(replacementJsonValue);
        }
        else if (parent is JsonObject finalObject)
        {
            finalObject[finalProperty] = JsonNode.Parse(replacementJsonValue);
        }
        else
        {
            throw new InvalidOperationException($"Fixture JSON path '{propertyPath}' does not exist.");
        }

        return root.ToJsonString();
    }

    private static string RemoveJsonProperty(string body, string propertyPath)
    {
        var root = JsonNode.Parse(body) ?? throw new InvalidOperationException("Fixture JSON must have a root value.");
        var propertyNames = propertyPath.Split('.');
        JsonNode? parent = root;
        for (var index = 0; index < propertyNames.Length - 1; index++)
        {
            parent = parent switch
            {
                JsonArray array when int.TryParse(propertyNames[index], out var arrayIndex) => array[arrayIndex],
                JsonObject jsonObject => jsonObject[propertyNames[index]],
                _ => null,
            } ?? throw new InvalidOperationException($"Fixture JSON path '{propertyPath}' does not exist.");
        }

        if (parent is not JsonObject objectParent || !objectParent.Remove(propertyNames[^1]))
        {
            throw new InvalidOperationException($"Fixture JSON property '{propertyPath}' does not exist.");
        }

        return root.ToJsonString();
    }

    private static IReadOnlyDictionary<string, string> CreateApiBodies(string baseRevision, string pullRequestHeadRevision) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PullRequestPath] = JsonSerializer.Serialize(new
            {
                number = 777,
                @base = new { repo = new { id = 321 }, @ref = "main", sha = baseRevision },
                head = new { repo = new { id = 321 }, sha = pullRequestHeadRevision },
            }),
            [WorkflowRunPath] = JsonSerializer.Serialize(new
            {
                id = 654321,
                run_attempt = 1,
                workflow_id = 1234,
                @event = "pull_request_target",
                head_sha = baseRevision,
                repository = new { full_name = Repository },
            }),
            [JobsPath] = JsonSerializer.Serialize(new
            {
                jobs = new[] { new { id = 5678, run_id = 654321, run_attempt = 1, head_sha = baseRevision, conclusion = "success" } },
            }),
        };

    private sealed class FixedCheckoutAttestationProvider(string checkoutRevision, string mismatch = "")
        : IEvidenceSubjectJobCheckoutAttestationProvider
    {
        public int CallCount { get; private set; }

        public EvidencePullRequestGateExpectedIdentity? LastExpectedIdentity { get; private set; }

        public EvidencePullRequestRunIdentity? LastRunIdentity { get; private set; }

        public string? LastSubjectJobId { get; private set; }

        public string? LastHeadRevision { get; private set; }

        public Task<EvidenceSubjectJobCheckoutAttestation?> AttestAsync(
            EvidencePullRequestGateExpectedIdentity expectedIdentity,
            EvidencePullRequestRunIdentity authoritativeRunIdentity,
            string subjectJobId,
            string currentPullRequestHeadRevision,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastExpectedIdentity = expectedIdentity;
            LastRunIdentity = authoritativeRunIdentity;
            LastSubjectJobId = subjectJobId;
            LastHeadRevision = currentPullRequestHeadRevision;
            Assert.Equal("pull_request_target", expectedIdentity.EventName);
            Assert.Equal(expectedIdentity.RunIdentity, authoritativeRunIdentity);
            Assert.Equal("5678", subjectJobId);
            Assert.Equal(checkoutRevision, currentPullRequestHeadRevision);

            var attestation = new EvidenceSubjectJobCheckoutAttestation(
                expectedIdentity.Repository,
                authoritativeRunIdentity,
                subjectJobId,
                checkoutRevision);
            EvidenceSubjectJobCheckoutAttestation? result = mismatch switch
            {
                "missing" => null,
                "repository" => attestation with { Repository = "another/repository" },
                "run" => attestation with
                {
                    RunIdentity = authoritativeRunIdentity with { WorkflowRunId = authoritativeRunIdentity.WorkflowRunId + 1 },
                },
                "subject-job" => attestation with { SubjectJobId = "9999" },
                "checkout-revision" => attestation with { SubjectCheckoutRevision = BaseRevision },
                "" => attestation,
                _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
            };
            return Task.FromResult(result);
        }
    }

    private sealed record ApiResponse(HttpStatusCode StatusCode, string Body);

    private sealed class GitHubApiFixtureHandler(
        string baseRevision,
        string pullRequestHeadRevision,
        Func<string, string, ApiResponse?>? responseOverride = null) : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _apiBodies = CreateApiBodies(baseRevision, pullRequestHeadRevision);

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            Requests.Add(pathAndQuery);
            if (!_apiBodies.TryGetValue(pathAndQuery, out var defaultBody))
            {
                throw new InvalidOperationException("Unexpected GitHub API request path.");
            }

            var apiResponse = responseOverride?.Invoke(pathAndQuery, defaultBody)
                ?? new ApiResponse(HttpStatusCode.OK, defaultBody);
            return Task.FromResult(new HttpResponseMessage(apiResponse.StatusCode)
            {
                Content = new StringContent(apiResponse.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("A request must not be sent without a checkout attestor or with invalid identity.");
        }
    }

    private sealed class CancellationAwareRequestHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A cancelled API request cannot return a response.");
        }
    }

    private sealed class CancellingCheckoutAttestationProvider(CancellationTokenSource cancellationSource)
        : IEvidenceSubjectJobCheckoutAttestationProvider
    {
        public int CallCount { get; private set; }

        public Task<EvidenceSubjectJobCheckoutAttestation?> AttestAsync(
            EvidencePullRequestGateExpectedIdentity expectedIdentity,
            EvidencePullRequestRunIdentity authoritativeRunIdentity,
            string subjectJobId,
            string currentPullRequestHeadRevision,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationSource.Cancel();
            return Task.FromCanceled<EvidenceSubjectJobCheckoutAttestation?>(cancellationToken);
        }
    }
}
