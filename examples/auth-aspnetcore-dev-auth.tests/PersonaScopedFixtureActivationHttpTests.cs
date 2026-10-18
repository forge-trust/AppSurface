using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AuthAspNetCoreDevAuthExample;
using ForgeTrust.AppSurface.Auth.AspNetCore.DevAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthAspNetCoreDevAuthExample.Tests;

public sealed class PersonaScopedFixtureActivationHttpTests
{
    private const string ScenarioId = "synthetic-candidate-001";
    private const string ReloadConfigEnvironmentVariable = "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE";

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:5058")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:5058")]
    [InlineData("[::1]")]
    [InlineData("[::1]:5058")]
    public async Task HostAdmission_AllowsExplicitLoopbackAuthorities(string host)
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = CreateClient(factory);
            client.DefaultRequestHeaders.Host = host;
            using var control = await client.GetAsync("/_appsurface/dev-auth/");
            Assert.Equal(HttpStatusCode.OK, control.StatusCode);
            using var selected = await SelectAsync(client, "labeler");
            Assert.Equal(HttpStatusCode.Found, selected.StatusCode);
            using var landing = await client.GetAsync("/candidate/label");
            Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
            Assert.True(Assert.IsType<LocalCandidateSnapshot>(
                factory.Services.GetRequiredService<LocalCandidateFixtureStore>().Read()).LabelerReady);
        });
    }

    [Theory]
    [InlineData("unapproved.example")]
    [InlineData("unapproved.example:5058")]
    [InlineData("")]
    public async Task HostAdmission_RejectsUnknownOrEmptyAuthorityBeforeIdentityAndFixtureChanges(string host)
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = CreateClient(factory);
            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            var routes = new[]
            {
                (HttpMethod.Get, "/_appsurface/dev-auth/"),
                (HttpMethod.Get, "/_appsurface/dev-auth/status"),
                (HttpMethod.Post, "/_appsurface/dev-auth/select/reviewer"),
                (HttpMethod.Post, "/_appsurface/dev-auth/clear"),
                (HttpMethod.Get, "/candidate/label"),
                (HttpMethod.Get, "/candidate/review"),
                (HttpMethod.Post, "/candidate/label/complete"),
                (HttpMethod.Post, "/candidate/review/complete"),
            };

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var before = store.Read();
                foreach (var (method, path) in routes)
                {
                    using var request = new HttpRequestMessage(method, path);
                    if (host.Length == 0)
                    {
                        // HttpClient supplies a Host; inject the parsed empty header at the test host boundary.
                        request.Headers.Add("X-Test-Blank-Header", "Host");
                    }
                    else
                    {
                        request.Headers.Host = host;
                        request.Headers.Add("Origin", $"http://{host}");
                    }

                    using var rejected = await client.SendAsync(request);
                    Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                    Assert.False(rejected.Headers.Contains("Set-Cookie"));
                    Assert.Null(rejected.Headers.Location);
                    Assert.Equal(before, store.Read());
                }

                if (attempt == 0)
                {
                    Assert.Null(store.Read());
                    using var selected = await SelectAsync(client, "labeler");
                    Assert.Equal(HttpStatusCode.Found, selected.StatusCode);
                }
            }

            using var statusResponse = await client.GetAsync("/_appsurface/dev-auth/status");
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            Assert.Equal("labeler", status.RootElement.GetProperty("personaId").GetString());
        });
    }

    [Fact]
    public void StoreTransitions_RejectMissingUnknownAndUnreadyWorkWithoutChangingSnapshots()
    {
        var store = new LocalCandidateFixtureStore();
        Assert.Null(store.Read());
        Assert.False(store.TryComplete("labeler"));
        Assert.Throws<InvalidOperationException>(() => store.MarkReady("labeler"));
        Assert.Null(store.Read());

        var initial = store.Ensure();
        Assert.Equal(ScenarioId, initial.Id);
        Assert.False(initial.LabelerReady);
        Assert.False(initial.ReviewerReady);
        Assert.False(store.TryComplete("labeler"));
        Assert.False(store.TryComplete("reviewer"));
        Assert.False(store.TryComplete("unknown"));
        Assert.Throws<InvalidOperationException>(() => store.MarkReady("unknown"));
        Assert.Equal(initial, store.Read());

        store.MarkReady("labeler");
        var ready = Assert.IsType<LocalCandidateSnapshot>(store.Read());
        Assert.NotSame(initial, ready);
        Assert.False(initial.LabelerReady);
        Assert.True(ready.LabelerReady);
        Assert.False(store.TryComplete("reviewer"));
        Assert.True(store.TryComplete("labeler"));
        Assert.True(store.TryComplete("labeler"));
        Assert.Equal(ready with { LabelingCompleted = true }, store.Read());
    }

    [Theory]
    [InlineData("labeler", "/candidate/label")]
    [InlineData("reviewer", "/candidate/review")]
    public async Task FirstOperatorSelection_CreatesOneSharedReadyCandidate(string personaId, string page)
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = CreateClient(factory);
            using var selected = await SelectAsync(client, personaId);

            Assert.Equal(HttpStatusCode.Found, selected.StatusCode);
            Assert.Equal(page, selected.Headers.Location?.OriginalString);

            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            var snapshot = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.Equal(ScenarioId, snapshot.Id);
            Assert.Equal(personaId == "labeler", snapshot.LabelerReady);
            Assert.Equal(personaId == "reviewer", snapshot.ReviewerReady);
            Assert.False(snapshot.LabelingCompleted);
            Assert.False(snapshot.ReviewCompleted);

            using var landing = await client.GetAsync(page);
            var body = await landing.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
            // Value: protects=ready landing cache policy; fails_when=200 omits NoStore; why_new=typed assertion; seam=none
            Assert.True(landing.Headers.CacheControl?.NoStore);
            Assert.Contains($"data-candidate-id=\"{ScenarioId}\"", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PersonaSwitchesAndClear_PreserveIndependentWorkAndAdminViewerAreNoOps()
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = CreateClient(factory);
            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();

            using var labelerSelection = await SelectAsync(client, "labeler");
            using var labelAction = await client.PostAsync("/candidate/label/complete", content: null);
            Assert.Equal(HttpStatusCode.SeeOther, labelAction.StatusCode);

            using var reviewerSelection = await SelectAsync(client, "reviewer");
            using var reviewAction = await client.PostAsync("/candidate/review/complete", content: null);
            Assert.Equal(HttpStatusCode.SeeOther, reviewAction.StatusCode);

            using var repeatedLabeler = await SelectAsync(client, "labeler");
            using var repeatedReviewer = await SelectAsync(client, "reviewer");
            var completed = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.Equal(ScenarioId, completed.Id);
            Assert.True(completed.LabelingCompleted);
            Assert.True(completed.ReviewCompleted);

            using var clear = await client.PostAsync("/_appsurface/dev-auth/clear", content: null);
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            Assert.Equal(completed, store.Read());

            using var reselectedLabeler = await SelectAsync(client, "labeler");
            using var reselectedReviewer = await SelectAsync(client, "reviewer");
            using var admin = await SelectAsync(client, "admin");
            Assert.Equal(HttpStatusCode.Found, admin.StatusCode);
            Assert.Equal(completed, store.Read());

            using var adminStatusResponse = await client.GetAsync("/_appsurface/dev-auth/status");
            using var adminStatus = JsonDocument.Parse(await adminStatusResponse.Content.ReadAsStringAsync());
            Assert.Equal("admin", adminStatus.RootElement.GetProperty("personaId").GetString());

            using var viewer = await SelectAsync(client, "viewer");
            Assert.Equal(HttpStatusCode.Found, viewer.StatusCode);
            using var viewerLanding = await client.GetAsync("/viewer");
            Assert.Equal(HttpStatusCode.OK, viewerLanding.StatusCode);
            Assert.Equal(completed, store.Read());
        });
    }

    [Theory]
    [InlineData("labeler", "/candidate/label/complete", "/candidate/label", "Labeling: completed", "Review: pending")]
    [InlineData("reviewer", "/candidate/review/complete", "/candidate/review", "Labeling: pending", "Review: completed")]
    public async Task CompletionActions_AreRoleSpecificIdempotentAndRedirectLocally(
        string personaId,
        string action,
        string page,
        string completedStage,
        string untouchedStage)
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = CreateClient(factory);
            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            using var selected = await SelectAsync(client, personaId);
            var before = Assert.IsType<LocalCandidateSnapshot>(store.Read());

            using var first = await client.PostAsync(action, content: null);
            Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
            Assert.Equal(page, first.Headers.Location?.OriginalString);
            var afterFirst = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.Equal(before.Id, afterFirst.Id);
            Assert.True(personaId == "labeler" ? afterFirst.LabelingCompleted : afterFirst.ReviewCompleted);
            Assert.Equal(personaId == "labeler" ? before.ReviewCompleted : before.LabelingCompleted,
                personaId == "labeler" ? afterFirst.ReviewCompleted : afterFirst.LabelingCompleted);

            using var repeated = await client.PostAsync(action, content: null);
            Assert.Equal(HttpStatusCode.SeeOther, repeated.StatusCode);
            Assert.Equal(page, repeated.Headers.Location?.OriginalString);
            Assert.Equal(afterFirst, store.Read());

            using var rendered = await client.GetAsync(page);
            var body = await rendered.Content.ReadAsStringAsync();
            Assert.Contains(completedStage, body, StringComparison.Ordinal);
            Assert.Contains(untouchedStage, body, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("labeler", false)]
    [InlineData("labeler", true)]
    [InlineData("reviewer", false)]
    [InlineData("reviewer", true)]
    public async Task CandidateReadsAndActions_ReturnReadOnlyConflictWhenMissingOrUnready(string personaId, bool seedUnready)
    {
        await WithFactoryAsync(async factory =>
        {
            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            if (seedUnready)
            {
                store.Ensure();
            }

            var before = store.Read();
            using var client = CreateClient(factory);
            using var selected = await SelectAsync(client, personaId);
            Assert.Equal(HttpStatusCode.Found, selected.StatusCode);

            var page = personaId == "labeler" ? "/candidate/label" : "/candidate/review";
            var action = page + "/complete";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var read = await client.GetAsync(page);
                var readBody = await read.Content.ReadAsStringAsync();
                // Value: protects=missing/unready read-only conflict cache policy; fails_when=409 omits NoStore; why_new=typed assertion; seam=none
                Assert.Equal(HttpStatusCode.Conflict, read.StatusCode);
                Assert.True(read.Headers.CacheControl?.NoStore);
                Assert.Contains("Fixtures not ready", readBody, StringComparison.Ordinal);
                Assert.Contains($"Reselect {personaId}", readBody, StringComparison.Ordinal);
                Assert.Equal(before, store.Read());

                using var mutation = await client.PostAsync(action, content: null);
                var mutationBody = await mutation.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.Conflict, mutation.StatusCode);
                Assert.True(mutation.Headers.CacheControl?.NoStore);
                Assert.Contains("Fixtures not ready", mutationBody, StringComparison.Ordinal);
                Assert.Equal(before, store.Read());
            }
        }, ConfigureActivationHarness(noOp: true));
    }

    [Theory]
    [InlineData("/candidate/label", "GET", "labeler", "reviewer")]
    [InlineData("/candidate/review", "GET", "reviewer", "labeler")]
    [InlineData("/candidate/label/complete", "POST", "labeler", "reviewer")]
    [InlineData("/candidate/review/complete", "POST", "reviewer", "labeler")]
    public async Task CandidateRoutes_EnforceAuthenticationRoleEnvironmentAndLoopbackWithoutMutation(
        string path,
        string method,
        string requiredPersona,
        string wrongPersona)
    {
        await WithFactoryAsync(async factory =>
        {
            using (var anonymous = CreateClient(factory))
            using (var response = await SendAsync(anonymous, path, method))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using var wrongClient = CreateClient(factory);
            using var wrongSelection = await SelectAsync(wrongClient, wrongPersona);
            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            var beforeWrongRole = store.Read();
            using (var forbidden = await SendAsync(wrongClient, path, method))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }

            Assert.Equal(beforeWrongRole, store.Read());

            using var allowedClient = CreateClient(factory);
            using var allowedSelection = await SelectAsync(allowedClient, requiredPersona);
            var beforeGuards = store.Read();
            var environment = factory.Services.GetRequiredService<IHostEnvironment>();
            var originalEnvironment = environment.EnvironmentName;
            try
            {
                environment.EnvironmentName = Environments.Production;
                using var nonDevelopment = await SendAsync(allowedClient, path, method);
                Assert.Equal(HttpStatusCode.Forbidden, nonDevelopment.StatusCode);
            }
            finally
            {
                environment.EnvironmentName = originalEnvironment;
            }

            using (var nonLoopbackRequest = new HttpRequestMessage(
                       method == "GET" ? HttpMethod.Get : HttpMethod.Post, path))
            {
                nonLoopbackRequest.Headers.Add("X-Test-Remote", "203.0.113.19");
                using var nonLoopback = await allowedClient.SendAsync(nonLoopbackRequest);
                Assert.Equal(HttpStatusCode.Forbidden, nonLoopback.StatusCode);
            }

            using (var nullRemoteRequest = new HttpRequestMessage(
                       method == "GET" ? HttpMethod.Get : HttpMethod.Post, path))
            {
                nullRemoteRequest.Headers.Add("X-Test-Remote", "null");
                using var nullRemote = await allowedClient.SendAsync(nullRemoteRequest);
                Assert.Equal(HttpStatusCode.Forbidden, nullRemote.StatusCode);
            }

            if (method == "POST")
            {
                foreach (var protectedMutation in new[] { "/candidate/label/complete", "/candidate/review/complete" })
                {
                    var rejectedHeaders = new[]
                    {
                        ("Origin", "https://attacker.example/path"),
                        ("Origin", string.Empty),
                        ("Origin", "not a valid absolute URI"),
                        ("Referer", "https://attacker.example/path"),
                        ("Referer", string.Empty),
                        ("Referer", "not a valid absolute URI"),
                        ("Sec-Fetch-Site", "cross-site"),
                    };
                    foreach (var (name, value) in rejectedHeaders)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, protectedMutation);
                        if (value.Length == 0)
                        {
                            // HttpClient omits empty header values; inject the parsed empty value at the test host boundary.
                            request.Headers.Add("X-Test-Blank-Header", name);
                        }
                        else
                        {
                            Assert.True(request.Headers.TryAddWithoutValidation(name, value));
                        }

                        using var rejected = await allowedClient.SendAsync(request);
                        Assert.True(rejected.StatusCode == HttpStatusCode.Forbidden,
                            $"Expected {name}={value} on {protectedMutation} to be rejected, received {(int)rejected.StatusCode}.");
                        Assert.Equal(beforeGuards, store.Read());
                    }
                }
            }

            Assert.Equal(beforeGuards, store.Read());
        });
    }

    [Theory]
    [InlineData("labeler", "/candidate/label/complete")]
    [InlineData("reviewer", "/candidate/review/complete")]
    public async Task CandidateActions_AdmitSameOriginAndSameSiteHeadersWithoutCrossRoleMutation(
        string personaId,
        string action)
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = CreateClient(factory);
            using var selected = await SelectAsync(client, personaId);
            Assert.Equal(HttpStatusCode.Found, selected.StatusCode);
            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();

            using var request = new HttpRequestMessage(HttpMethod.Post, action);
            request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
            request.Headers.Referrer = new Uri("http://localhost/operator/page");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
            Assert.Equal(personaId == "labeler" ? "/candidate/label" : "/candidate/review",
                response.Headers.Location?.OriginalString);
            var snapshot = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.Equal(personaId == "labeler", snapshot.LabelingCompleted);
            Assert.Equal(personaId == "reviewer", snapshot.ReviewCompleted);
        });
    }

    [Theory]
    [InlineData("labeler", "reviewer")]
    [InlineData("reviewer", "labeler")]
    public async Task OverlappingSelections_EnsureOneCandidateInEitherCompletionOrder(string firstPersona, string secondPersona)
    {
        await WithFactoryAsync(async factory =>
        {
            using var firstClient = CreateClient(factory);
            using var secondClient = CreateClient(factory);
            var control = factory.Services.GetRequiredService<ActivationTestControl>();
            var firstGate = control.Arm(firstPersona, ActivationGatePosition.AfterEnsure);
            var secondGate = control.Arm(secondPersona, ActivationGatePosition.AfterEnsure);

            var firstRequest = SelectAsync(firstClient, firstPersona);
            var secondRequest = SelectAsync(secondClient, secondPersona);
            await Task.WhenAll(firstGate.WaitForEntryAsync(), secondGate.WaitForEntryAsync());

            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            Assert.Equal(ScenarioId, Assert.IsType<LocalCandidateSnapshot>(store.Read()).Id);
            firstGate.Release();
            using var firstResponse = await firstRequest.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.Found, firstResponse.StatusCode);
            secondGate.Release();
            using var secondResponse = await secondRequest.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.Found, secondResponse.StatusCode);

            var snapshot = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.Equal(ScenarioId, snapshot.Id);
            Assert.True(snapshot.LabelerReady);
            Assert.True(snapshot.ReviewerReady);
            Assert.False(snapshot.LabelingCompleted);
            Assert.False(snapshot.ReviewCompleted);
            var firstPage = firstPersona == "labeler" ? "/candidate/label" : "/candidate/review";
            var secondPage = secondPersona == "labeler" ? "/candidate/label" : "/candidate/review";
            Assert.Equal(ScenarioId, await AssertCandidateId(await firstClient.GetAsync(firstPage)));
            Assert.Equal(ScenarioId, await AssertCandidateId(await secondClient.GetAsync(secondPage)));

            using var firstAction = await firstClient.PostAsync(
                firstPersona == "labeler" ? "/candidate/label/complete" : "/candidate/review/complete", content: null);
            using var secondAction = await secondClient.PostAsync(
                secondPersona == "labeler" ? "/candidate/label/complete" : "/candidate/review/complete", content: null);
            Assert.Equal(HttpStatusCode.SeeOther, firstAction.StatusCode);
            Assert.Equal(HttpStatusCode.SeeOther, secondAction.StatusCode);
            var completedSnapshot = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.True(completedSnapshot.LabelingCompleted);
            Assert.True(completedSnapshot.ReviewCompleted);

            var repeatedFirstGate = control.Arm(firstPersona, ActivationGatePosition.AfterEnsure);
            var repeatedSecondGate = control.Arm(secondPersona, ActivationGatePosition.AfterEnsure);
            var repeatedFirst = SelectAsync(firstClient, firstPersona);
            var repeatedSecond = SelectAsync(secondClient, secondPersona);
            await Task.WhenAll(repeatedFirstGate.WaitForEntryAsync(), repeatedSecondGate.WaitForEntryAsync());
            repeatedSecondGate.Release();
            using var repeatedSecondResponse = await repeatedSecond.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.Found, repeatedSecondResponse.StatusCode);
            repeatedFirstGate.Release();
            using var repeatedFirstResponse = await repeatedFirst.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.Found, repeatedFirstResponse.StatusCode);
            Assert.Equal(completedSnapshot, store.Read());
        }, ConfigureActivationHarness());
    }

    [Theory]
    [InlineData("BeforeEnsure")]
    [InlineData("AfterEnsure")]
    public async Task ProductActionOverlappingReselection_PreservesImmutableSnapshotsAndCompletedWork(string positionName)
    {
        var position = Enum.Parse<ActivationGatePosition>(positionName);
        await WithFactoryAsync(async factory =>
        {
            using var labelerClient = CreateClient(factory);
            using var reviewerClient = CreateClient(factory);
            using var labelerSelection = await SelectAsync(labelerClient, "labeler");
            using var reviewerSelection = await SelectAsync(reviewerClient, "reviewer");
            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            var priorSnapshot = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.False(priorSnapshot.LabelingCompleted);

            var gate = factory.Services.GetRequiredService<ActivationTestControl>().Arm("reviewer", position);
            var reselection = SelectAsync(reviewerClient, "reviewer");
            await gate.WaitForEntryAsync();

            using var action = await labelerClient.PostAsync("/candidate/label/complete", content: null);
            Assert.Equal(HttpStatusCode.SeeOther, action.StatusCode);
            Assert.False(priorSnapshot.LabelingCompleted);
            Assert.True(Assert.IsType<LocalCandidateSnapshot>(store.Read()).LabelingCompleted);

            gate.Release();
            using var selected = await reselection.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.Found, selected.StatusCode);
            var current = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.Equal(priorSnapshot.Id, current.Id);
            Assert.True(current.LabelerReady);
            Assert.True(current.ReviewerReady);
            Assert.True(current.LabelingCompleted);
            Assert.False(current.ReviewCompleted);
        }, ConfigureActivationHarness());
    }

    [Fact]
    public async Task ReviewerActivationFailure_PreservesCookieAndRequiresExplicitRetryOfSameScenario()
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = CreateClient(factory);
            using var labeler = await SelectAsync(client, "labeler");
            using var labelAction = await client.PostAsync("/candidate/label/complete", content: null);
            Assert.Equal(HttpStatusCode.SeeOther, labelAction.StatusCode);
            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            var original = Assert.IsType<LocalCandidateSnapshot>(store.Read());

            using var failedSelection = await SelectAsync(client, "reviewer");
            var body = await failedSelection.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.InternalServerError, failedSelection.StatusCode);
            Assert.Contains(LocalFixtureActivationFailureMiddleware.FailureMessage, body, StringComparison.Ordinal);
            Assert.Null(failedSelection.Headers.Location);
            Assert.Contains(failedSelection.Headers, header => string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);

            using var statusResponse = await client.GetAsync("/_appsurface/dev-auth/status");
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            Assert.Equal("reviewer", status.RootElement.GetProperty("personaId").GetString());

            using var notReady = await client.GetAsync("/candidate/review");
            Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
            Assert.Equal(original with { ReviewerReady = false }, store.Read());

            using var retry = await SelectAsync(client, "reviewer");
            Assert.Equal(HttpStatusCode.Found, retry.StatusCode);
            Assert.Equal("/candidate/review", retry.Headers.Location?.OriginalString);
            var recovered = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.Equal(original.Id, recovered.Id);
            Assert.True(recovered.LabelingCompleted);
            Assert.True(recovered.ReviewerReady);
            Assert.False(recovered.ReviewCompleted);
            Assert.Equal(1, factory.Services.GetRequiredService<ActivationTestControl>().ReviewerFailureCount);
        }, ConfigureActivationHarness(failFirstReviewerAfterEnsure: true));
    }

    [Fact]
    public async Task ActivationMiddleware_PropagatesUnknownCancellationAndPostStartFailures()
    {
        var unknown = new InvalidOperationException("sensitive exception details");
        var unknownContext = new DefaultHttpContext();
        var unknownMiddleware = new LocalFixtureActivationFailureMiddleware(_ => Task.FromException(unknown));
        var unknownObserved = await Record.ExceptionAsync(() => unknownMiddleware.InvokeAsync(unknownContext));
        Assert.Same(unknown, unknownObserved);
        Assert.Equal(StatusCodes.Status200OK, unknownContext.Response.StatusCode);

        var cancellation = new OperationCanceledException("sensitive cancellation details");
        var cancellationContext = new DefaultHttpContext();
        var cancellationMiddleware = new LocalFixtureActivationFailureMiddleware(_ => Task.FromException(cancellation));
        var cancellationObserved = await Record.ExceptionAsync(() => cancellationMiddleware.InvokeAsync(cancellationContext));
        Assert.Same(cancellation, cancellationObserved);

        var startedFailure = new LocalFixtureActivationException();
        var startedContext = new DefaultHttpContext();
        var startedFeature = new TestStartedResponseFeature
        {
            StatusCode = StatusCodes.Status202Accepted,
        };
        startedFeature.Headers["Content-Type"] = "application/original";
        startedFeature.Headers["Location"] = "/preserved";
        startedFeature.Headers["Set-Cookie"] = "proof=preserved";
        startedContext.Features.Set<IHttpResponseFeature>(startedFeature);
        var startedMiddleware = new LocalFixtureActivationFailureMiddleware(_ => Task.FromException(startedFailure));
        var startedObserved = await Record.ExceptionAsync(() => startedMiddleware.InvokeAsync(startedContext));
        Assert.Same(startedFailure, startedObserved);
        Assert.True(startedContext.Response.HasStarted);
        Assert.Equal(StatusCodes.Status202Accepted, startedContext.Response.StatusCode);
        Assert.Equal("application/original", startedContext.Response.ContentType);
        Assert.Equal("/preserved", startedContext.Response.Headers["Location"]);
        Assert.Equal("proof=preserved", startedContext.Response.Headers["Set-Cookie"]);
        Assert.Equal(0, startedFeature.Body.Length);
    }

    [Fact]
    public async Task CancelledActivation_DoesNotReportNavigationAndCanBeRetriedWithoutLosingWork()
    {
        await WithFactoryAsync(async factory =>
        {
            using var client = CreateClient(factory);
            using var labeler = await SelectAsync(client, "labeler");
            using var labelAction = await client.PostAsync("/candidate/label/complete", content: null);
            using var reviewer = await SelectAsync(client, "reviewer");
            using var reviewAction = await client.PostAsync("/candidate/review/complete", content: null);
            Assert.Equal(HttpStatusCode.SeeOther, labelAction.StatusCode);
            Assert.Equal(HttpStatusCode.SeeOther, reviewAction.StatusCode);

            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            var beforeCancel = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            var gate = factory.Services.GetRequiredService<ActivationTestControl>()
                .Arm("reviewer", ActivationGatePosition.AfterReady);
            using var cancellation = new CancellationTokenSource();
            var pendingSelection = client.PostAsync("/_appsurface/dev-auth/select/reviewer", content: null, cancellation.Token);
            await gate.WaitForEntryAsync();
            cancellation.Cancel();

            var observed = await Record.ExceptionAsync(async () => await pendingSelection);
            Assert.IsAssignableFrom<OperationCanceledException>(observed);
            Assert.Equal(beforeCancel, store.Read());

            using var page = await client.GetAsync("/candidate/review");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            using var retry = await SelectAsync(client, "reviewer");
            Assert.Equal(HttpStatusCode.Found, retry.StatusCode);
            Assert.Equal("/candidate/review", retry.Headers.Location?.OriginalString);
            var afterRetry = Assert.IsType<LocalCandidateSnapshot>(store.Read());
            Assert.Equal(beforeCancel.Id, afterRetry.Id);
            Assert.True(afterRetry.LabelingCompleted);
            Assert.True(afterRetry.ReviewCompleted);
        }, ConfigureActivationHarness());
    }

    [Theory]
    [InlineData("labeler", "labeler")]
    [InlineData("reviewer", "reviewer")]
    [InlineData("admin", "admin")]
    [InlineData("viewer", "viewer")]
    [InlineData("custom-operator", "other")]
    public async Task ActivationLogs_UseOnlyRecognizedPersonaIds(string personaId, string expectedLogId)
    {
        using var logProvider = new RecordingLoggerProvider();
        var options = new AppSurfaceDevAuthOptions();
        options.Users.Add(personaId, user => user.DisplayName("Local operator").Subject("operator-1"));
        var context = new DefaultHttpContext { TraceIdentifier = "trace-persona-allowlist" };
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logProvider));
        var activation = new LocalFixtureActivation(new LocalCandidateFixtureStore(),
            loggerFactory.CreateLogger<LocalFixtureActivation>());

        await activation.ActivateAsync(options.Users.Personas[personaId], context, CancellationToken.None);

        Assert.Equal([
            $"Local fixture activation TraceId=trace-persona-allowlist PersonaId={expectedLogId} Outcome=start",
            $"Local fixture activation TraceId=trace-persona-allowlist PersonaId={expectedLogId} Outcome=success",
        ], logProvider.Entries);
    }

    [Fact]
    public async Task ActivationLogs_CorrelateSafeFieldsAndExcludeCookiesClaimsAndExceptionData()
    {
        using var logProvider = new RecordingLoggerProvider();
        await WithFactoryAsync(async factory =>
        {
            var store = factory.Services.GetRequiredService<LocalCandidateFixtureStore>();
            var actualActivation = new LocalFixtureActivation(
                store,
                factory.Services.GetRequiredService<ILogger<LocalFixtureActivation>>());
            var actualContext = new DefaultHttpContext();
            actualContext.TraceIdentifier = "trace-activation-success";
            actualContext.Request.Headers.Cookie = "session=COOKIE_SECRET_SENTINEL";
            actualContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("private", "CLAIM_SECRET_SENTINEL")]));
            var configuredOptions = factory.Services.GetRequiredService<IOptions<AppSurfaceDevAuthOptions>>().Value;
            await actualActivation.ActivateAsync(
                configuredOptions.Users.Personas["labeler"], actualContext, CancellationToken.None);

            using var client = CreateClient(factory);
            client.DefaultRequestHeaders.Add("X-Test-TraceId", "trace-correlation-safe");
            var control = factory.Services.GetRequiredService<ActivationTestControl>();
            control.AddSensitiveSentinels = true;

            using var labeler = await SelectAsync(client, "labeler");
            Assert.Equal(HttpStatusCode.Found, labeler.StatusCode);
            using var failedReviewer = await SelectAsync(client, "reviewer");
            Assert.Equal(HttpStatusCode.InternalServerError, failedReviewer.StatusCode);

            var gate = control.Arm("reviewer", ActivationGatePosition.AfterReady);
            using var cancellation = new CancellationTokenSource();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/_appsurface/dev-auth/select/reviewer");
            request.Headers.Add("X-Test-TraceId", "trace-cancellation-safe");
            var pending = client.SendAsync(request, cancellation.Token);
            await gate.WaitForEntryAsync();
            cancellation.Cancel();
            var cancellationError = await Record.ExceptionAsync(async () => await pending);
            Assert.IsAssignableFrom<OperationCanceledException>(cancellationError);

            using var scope = factory.Services.CreateScope();
            var preCanceledContext = new DefaultHttpContext();
            preCanceledContext.TraceIdentifier = "trace-pre-canceled";
            using var alreadyCanceled = new CancellationTokenSource();
            alreadyCanceled.Cancel();
            var prepareCalled = false;
            var preCanceled = await Record.ExceptionAsync(async () => await LocalFixtureActivation.RunAsync(
                configuredOptions.Users.Personas["reviewer"],
                preCanceledContext,
                alreadyCanceled.Token,
                scope.ServiceProvider.GetRequiredService<ILogger<LocalFixtureActivation>>(),
                _ =>
                {
                    prepareCalled = true;
                    return ValueTask.CompletedTask;
                }));
            Assert.IsAssignableFrom<OperationCanceledException>(preCanceled);
            Assert.False(prepareCalled);

            using var preparationCancellation = new CancellationTokenSource();
            var preparationToken = preparationCancellation.Token;
            var preparationContext = new DefaultHttpContext();
            preparationContext.TraceIdentifier = "trace-preparation-canceled";
            var preparationReturnedNormally = false;
            var preparationCancellationError = await Record.ExceptionAsync(async () => await LocalFixtureActivation.RunAsync(
                configuredOptions.Users.Personas["reviewer"],
                preparationContext,
                preparationToken,
                scope.ServiceProvider.GetRequiredService<ILogger<LocalFixtureActivation>>(),
                _ =>
                {
                    preparationCancellation.Cancel();
                    preparationReturnedNormally = true;
                    return ValueTask.CompletedTask;
                }));
            // Value: protects=post-preparation cancellation contract; fails_when=swallowed or substituted token; why_new=missing branch evidence; seam=none
            Assert.True(preparationReturnedNormally);
            var preparationCanceledException = Assert.IsAssignableFrom<OperationCanceledException>(preparationCancellationError);
            Assert.Equal(preparationToken, preparationCanceledException.CancellationToken);

            var entries = string.Join("\n", logProvider.Entries);
            Assert.Contains("Outcome=start", entries, StringComparison.Ordinal);
            Assert.Contains("Outcome=success", entries, StringComparison.Ordinal);
            Assert.Contains("Outcome=failure", entries, StringComparison.Ordinal);
            Assert.Contains("Outcome=cancel", entries, StringComparison.Ordinal);
            Assert.Contains("TraceId=trace-activation-success", entries, StringComparison.Ordinal);
            Assert.Contains("TraceId=trace-correlation-safe", entries, StringComparison.Ordinal);
            Assert.Contains("TraceId=trace-cancellation-safe", entries, StringComparison.Ordinal);
            Assert.Contains("TraceId=trace-pre-canceled", entries, StringComparison.Ordinal);
            Assert.Contains("TraceId=trace-preparation-canceled PersonaId=reviewer Outcome=cancel", entries, StringComparison.Ordinal);
            Assert.DoesNotContain("TraceId=trace-preparation-canceled PersonaId=reviewer Outcome=success", entries, StringComparison.Ordinal);
            Assert.Contains("PersonaId=labeler", entries, StringComparison.Ordinal);
            Assert.Contains("PersonaId=reviewer", entries, StringComparison.Ordinal);
            Assert.DoesNotContain("COOKIE_SECRET_SENTINEL", entries, StringComparison.Ordinal);
            Assert.DoesNotContain("CLAIM_SECRET_SENTINEL", entries, StringComparison.Ordinal);
            Assert.DoesNotContain("EXCEPTION_SECRET_SENTINEL", entries, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", entries, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("OperationCanceledException", entries, StringComparison.Ordinal);
        }, ConfigureActivationHarness(failFirstReviewerAfterEnsure: true, addSensitiveSentinels: true),
            configureLogging: logging => logging.AddProvider(logProvider));
    }

    [Fact]
    public void Renderer_EncodesDynamicIdentityAndShowsReadyCompletedAndRecoveryStates()
    {
        const string trustedMarker = "<aside data-marker=\"trusted-package-html\">Local identity marker</aside>";
        var dangerousId = "<script>alert('candidate')</script>&";
        var pending = new LocalCandidateSnapshot(dangerousId, true, true, false, true);
        var labelPage = LocalCandidatePages.Render(trustedMarker, pending, "labeler");
        Assert.Contains(trustedMarker, labelPage, StringComparison.Ordinal);
        Assert.Contains("<h1>Label candidate</h1>", labelPage, StringComparison.Ordinal);
        Assert.Contains("Fixtures ready", labelPage, StringComparison.Ordinal);
        Assert.Contains("Labeling: pending", labelPage, StringComparison.Ordinal);
        Assert.Contains("Review: completed", labelPage, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(&#x27;candidate&#x27;)&lt;/script&gt;&amp;", labelPage, StringComparison.Ordinal);
        Assert.DoesNotContain(dangerousId, labelPage, StringComparison.Ordinal);
        Assert.Contains("action=\"/candidate/label/complete\"", labelPage, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(labelPage, "<h1>"));

        var completed = LocalCandidatePages.Render(trustedMarker, pending with { LabelingCompleted = true }, "labeler");
        Assert.Contains("Your work is completed.", completed, StringComparison.Ordinal);
        Assert.DoesNotContain("/candidate/label/complete", completed, StringComparison.Ordinal);

        var recovery = LocalCandidatePages.Render(trustedMarker, null, "labeler");
        Assert.Contains("Fixtures not ready", recovery, StringComparison.Ordinal);
        Assert.Contains("Reselect labeler", recovery, StringComparison.Ordinal);
        Assert.Contains("action=\"/_appsurface/dev-auth/select/labeler?returnUrl=%2Fcandidate%2Flabel\"", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("Candidate ID:", recovery, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(recovery, "<h1>"));

        var hostilePersona = LocalCandidatePages.Render(trustedMarker, null, "<script>bad</script>");
        Assert.DoesNotContain("<script>bad</script>", hostilePersona, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;bad&lt;/script&gt;", hostilePersona, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string match)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(match, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += match.Length;
        }

        return count;
    }

    private static async Task<string> AssertCandidateId(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            const string prefix = "data-candidate-id=\"";
            var start = body.IndexOf(prefix, StringComparison.Ordinal);
            Assert.True(start >= 0, "The ready page should expose its stable candidate identity.");
            start += prefix.Length;
            var end = body.IndexOf('"', start);
            Assert.True(end > start, "The candidate identity attribute should be complete.");
            return body[start..end];
        }
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static Task<HttpResponseMessage> SelectAsync(HttpClient client, string personaId) =>
        client.PostAsync($"/_appsurface/dev-auth/select/{personaId}", content: null);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string method) =>
        method == "GET" ? client.GetAsync(path) : client.PostAsync(path, content: null);

    private static Action<IServiceCollection> ConfigureActivationHarness(
        bool noOp = false,
        bool failFirstReviewerAfterEnsure = false,
        bool addSensitiveSentinels = false) => services =>
    {
        services.RemoveAll<IAppSurfaceDevAuthPersonaSelectionHandler>();
        services.AddSingleton(new ActivationTestControl(noOp, failFirstReviewerAfterEnsure, addSensitiveSentinels));
        services.AddScoped<IAppSurfaceDevAuthPersonaSelectionHandler, TestPersonaSelectionHandler>();
    };

    private static async Task WithFactoryAsync(
        Func<WebApplicationFactory<Program>, Task> action,
        Action<IServiceCollection>? configureTestServices = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        var priorReloadConfig = Environment.GetEnvironmentVariable(ReloadConfigEnvironmentVariable);
        Environment.SetEnvironmentVariable(ReloadConfigEnvironmentVariable, "false");
        try
        {
            await using var rootFactory = new WebApplicationFactory<Program>();
            await using var factory = rootFactory.WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment(Environments.Development);
                    builder.ConfigureLogging(logging => configureLogging?.Invoke(logging));
                    builder.ConfigureServices(services =>
                    {
                        services.AddDataProtection().UseEphemeralDataProtectionProvider();
                        services.AddSingleton<IStartupFilter, TestLoopbackStartupFilter>();
                    });
                    if (configureTestServices is not null)
                    {
                        builder.ConfigureTestServices(configureTestServices);
                    }
                });
            await action(factory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReloadConfigEnvironmentVariable, priorReloadConfig);
        }
    }

    private sealed class TestLoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var requestedRemote = context.Request.Headers["X-Test-Remote"].FirstOrDefault();
                context.Connection.RemoteIpAddress = string.Equals(requestedRemote, "null", StringComparison.Ordinal)
                    ? null
                    : IPAddress.TryParse(requestedRemote, out var address) ? address : IPAddress.Loopback;
                if (context.Request.Headers.TryGetValue("X-Test-TraceId", out var traceIds) && traceIds.Count == 1)
                {
                    context.TraceIdentifier = traceIds[0]!;
                }

                var blankHeader = context.Request.Headers["X-Test-Blank-Header"].FirstOrDefault();
                if (blankHeader is "Origin" or "Referer" or "Host")
                {
                    context.Request.Headers[blankHeader] = string.Empty;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class TestStartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private enum ActivationGatePosition
    {
        BeforeEnsure,
        AfterEnsure,
        AfterReady,
    }

    private sealed class ActivationGateSlot
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ActivationGatePosition Position { get; init; }

        internal async Task WaitForEntryAsync() => await Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        internal void SignalEntry() => Entered.TrySetResult();
        internal void Release() => Released.TrySetResult();
        internal async Task PauseAsync(CancellationToken cancellationToken)
        {
            SignalEntry();
            await Released.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ActivationTestControl(bool noOp, bool failFirstReviewerAfterEnsure, bool addSensitiveSentinels)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ActivationGateSlot> _slots = new(StringComparer.Ordinal);
        private int _reviewerFailureCount;

        internal bool NoOp { get; } = noOp;
        internal int ReviewerFailureCount => Volatile.Read(ref _reviewerFailureCount);
        internal bool AddSensitiveSentinels { get; set; } = addSensitiveSentinels;

        internal ActivationGateSlot Arm(string personaId, ActivationGatePosition position)
        {
            var slot = new ActivationGateSlot { Position = position };
            lock (_gate)
            {
                _slots[personaId] = slot;
            }

            return slot;
        }

        internal bool TryFailReviewerOnce(string personaId) =>
            failFirstReviewerAfterEnsure && personaId == "reviewer" && Interlocked.CompareExchange(ref _reviewerFailureCount, 1, 0) == 0;

        internal async ValueTask PauseIfArmedAsync(string personaId, ActivationGatePosition position, CancellationToken cancellationToken)
        {
            ActivationGateSlot? slot = null;
            lock (_gate)
            {
                if (_slots.TryGetValue(personaId, out var candidate) && candidate.Position == position)
                {
                    slot = candidate;
                    _slots.Remove(personaId);
                }
            }

            if (slot is not null)
            {
                await slot.PauseAsync(cancellationToken);
            }
        }
    }

    private sealed class TestPersonaSelectionHandler(
        LocalCandidateFixtureStore store,
        ActivationTestControl control,
        ILogger<LocalFixtureActivation> logger) : IAppSurfaceDevAuthPersonaSelectionHandler
    {
        public ValueTask ActivateAsync(AppSurfaceDevAuthPersona persona, HttpContext httpContext, CancellationToken cancellationToken)
        {
            if (control.AddSensitiveSentinels)
            {
                httpContext.Request.Headers.Cookie = string.Concat(httpContext.Request.Headers.Cookie, "; test=COOKIE_SECRET_SENTINEL");
                httpContext.User.AddIdentity(new ClaimsIdentity([new Claim("private", "CLAIM_SECRET_SENTINEL")]));
            }

            return LocalFixtureActivation.RunAsync(persona, httpContext, cancellationToken, logger, async token =>
            {
                if (control.NoOp || persona.Id is not ("labeler" or "reviewer"))
                {
                    return;
                }

                await control.PauseIfArmedAsync(persona.Id, ActivationGatePosition.BeforeEnsure, token);
                store.Ensure();
                await control.PauseIfArmedAsync(persona.Id, ActivationGatePosition.AfterEnsure, token);
                if (control.TryFailReviewerOnce(persona.Id))
                {
                    var failure = new LocalFixtureActivationException();
                    failure.Data["test-sentinel"] = "EXCEPTION_SECRET_SENTINEL";
                    throw failure;
                }

                store.MarkReady(persona.Id);
                await control.PauseIfArmedAsync(persona.Id, ActivationGatePosition.AfterReady, token);
            });
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();
        internal IEnumerable<string> Entries => _entries.ToArray();
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);
        public void Dispose() { }

        private sealed class RecordingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : exception.ToString()));
                }
            }
        }
    }
}
