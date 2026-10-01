using System.Net;
using System.Text;
using System.Text.Json;
using ForgeTrust.AppSurface.Evidence.Contracts;
using ForgeTrust.AppSurface.Evidence.Planner;
using ForgeTrust.AppSurface.EvidenceGate;

namespace ForgeTrust.AppSurface.EvidenceGate.Tests;

public sealed class GitHubActionsEvidenceAuthorityProviderTests
{
    [Fact]
    public async Task ProviderIgnoresWorkflowAndJobHeadShaWhenAttestedCheckoutIsAvailable()
    {
        const string baseRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string pullRequestHeadRevision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var expectedIdentity = CreateExpectedIdentity();
        var httpHandler = new GitHubApiFixtureHandler(baseRevision, pullRequestHeadRevision);
        using var httpClient = new HttpClient(httpHandler);
        var attestationProvider = new FixedCheckoutAttestationProvider(pullRequestHeadRevision);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(
            httpClient,
            new Uri("https://api.example.test/"),
            attestationProvider);

        var snapshot = await authorityProvider.ReadFreshAsync(expectedIdentity);

        Assert.NotNull(snapshot);
        Assert.Equal(baseRevision, snapshot.BaseRevision);
        Assert.Equal(pullRequestHeadRevision, snapshot.HeadRevision);
        Assert.Equal(pullRequestHeadRevision, snapshot.SubjectJobHeadRevision);
        Assert.Equal("success", snapshot.SubjectJobConclusion);
        Assert.Equal(3, httpHandler.Requests.Count);
        Assert.Equal(1, attestationProvider.CallCount);
    }

    [Fact]
    public async Task ProviderFailsClosedWithoutSeparateCheckoutAttestor()
    {
        var handler = new UnexpectedRequestHandler();
        using var httpClient = new HttpClient(handler);
        using var authorityProvider = new GitHubActionsEvidenceAuthorityProvider(httpClient, new Uri("https://api.example.test/"));

        var snapshot = await authorityProvider.ReadFreshAsync(CreateExpectedIdentity());

        Assert.Null(snapshot);
        Assert.Equal(0, handler.RequestCount);
    }

    private static EvidencePullRequestGateExpectedIdentity CreateExpectedIdentity() => new(
        "forge-trust/AppSurface",
        "pull_request_target",
        "1234",
        "5678",
        new EvidencePullRequestRunIdentity(321, 321, 777, "main", 654321, 1));

    private sealed class FixedCheckoutAttestationProvider(string checkoutRevision) : IEvidenceSubjectJobCheckoutAttestationProvider
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
            Assert.Equal("pull_request_target", expectedIdentity.EventName);
            Assert.Equal(authoritativeRunIdentity, expectedIdentity.RunIdentity);
            Assert.Equal("5678", subjectJobId);
            Assert.Equal(checkoutRevision, currentPullRequestHeadRevision);
            return Task.FromResult<EvidenceSubjectJobCheckoutAttestation?>(new EvidenceSubjectJobCheckoutAttestation(
                expectedIdentity.Repository,
                authoritativeRunIdentity,
                subjectJobId,
                checkoutRevision));
        }
    }

    private sealed class GitHubApiFixtureHandler(string baseRevision, string pullRequestHeadRevision) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            Requests.Add(pathAndQuery);
            var body = pathAndQuery switch
            {
                "/repos/forge-trust/AppSurface/pulls/777" => JsonSerializer.Serialize(new
                {
                    number = 777,
                    @base = new { repo = new { id = 321 }, @ref = "main", sha = baseRevision },
                    head = new { repo = new { id = 321 }, sha = pullRequestHeadRevision },
                }),
                "/repos/forge-trust/AppSurface/actions/runs/654321/attempts/1" => JsonSerializer.Serialize(new
                {
                    id = 654321,
                    run_attempt = 1,
                    workflow_id = 1234,
                    @event = "pull_request_target",
                    head_sha = baseRevision,
                    repository = new { full_name = "forge-trust/AppSurface" },
                }),
                "/repos/forge-trust/AppSurface/actions/runs/654321/attempts/1/jobs?per_page=100" => JsonSerializer.Serialize(new
                {
                    jobs = new[] { new { id = 5678, run_id = 654321, run_attempt = 1, head_sha = baseRevision, conclusion = "success" } },
                }),
                _ => throw new InvalidOperationException("Unexpected GitHub API request path."),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("A request must not be sent without a checkout attestor.");
        }
    }
}
