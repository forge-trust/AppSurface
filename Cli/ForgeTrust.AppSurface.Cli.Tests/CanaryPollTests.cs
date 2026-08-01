using System.Net;
using System.Text;
using System.Text.Json;
using CliFx.Infrastructure;
using ForgeTrust.AppSurface.Testing;
using ForgeTrust.RazorWire.Cli;

namespace ForgeTrust.AppSurface.Cli.Tests;

[Collection(ProgramEntryPointCollection.Name)]
public sealed class CanaryPollTests
{
    [Fact]
    public void RequestFactory_Should_Preserve_ApplicationBasePath_And_Resolve_EnvironmentOnlyMarker()
    {
        using var marker = new EnvironmentVariableScope("APPSURFACE_CANARY_MARKER", "deploy-marker-secret");

        var request = CanaryPollRequestFactory.Create(
            "https://app.example.test/product/",
            "forwarding.alpha-evidence",
            "APPSURFACE_CANARY_MARKER",
            "2026-07-30T00:00:00Z",
            null,
            null,
            [],
            "5m",
            "5s",
            3);

        Assert.Equal(
            "https://app.example.test/product/_appsurface/canaries/forwarding.alpha-evidence",
            request.Endpoint.AbsoluteUri);
        Assert.Equal("deploy-marker-secret", request.Marker);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero), request.FreshSince);
    }

    [Theory]
    [InlineData("http://app.example.test")]
    [InlineData("http://127.0.0.2")]
    [InlineData("https://app.example.test/?unsafe=true")]
    [InlineData("https://user@app.example.test")]
    public void RequestFactory_Should_Reject_UnsafeUrls(string url)
    {
        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            url,
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            null,
            [],
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN401", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]")]
    public void RequestFactory_Should_Allow_OnlyTheExplicitHttpLoopbackHosts(string url)
    {
        var request = CanaryPollRequestFactory.Create(
            url,
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            null,
            [],
            "5m",
            "5s",
            3);

        Assert.Equal("forwarding.alpha-evidence", request.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RequestFactory_Should_Reject_ProvidedBlankEnvironmentVariableNames(string environmentVariableName)
    {
        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            environmentVariableName,
            null,
            null,
            null,
            [],
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN402", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("not-a-valid-environment-name")]
    [InlineData("APPSURFACE_CANARY_MISSING_VALUE")]
    public void RequestFactory_Should_RejectInvalidOrMissingEnvironmentSources(string environmentVariableName)
    {
        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            environmentVariableName,
            null,
            null,
            null,
            [],
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN402", exception.DiagnosticCode);
        Assert.DoesNotContain(environmentVariableName, exception.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestFactory_Should_RejectControlContainingCredentialValues()
    {
        using var credential = new EnvironmentVariableScope("APPSURFACE_CANARY_INVALID_VALUE", "credential\nvalue");

        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            "APPSURFACE_CANARY_INVALID_VALUE",
            null,
            [],
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN402", exception.DiagnosticCode);
        Assert.DoesNotContain("credential", exception.SafeMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.00000001ms")]
    [InlineData("30000h")]
    public void RequestFactory_Should_RejectDurationsThatCannotScheduleAPositiveCancellation(string timeout)
    {
        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            null,
            [],
            timeout,
            "5s",
            3));

        Assert.Equal("ASCAN401", exception.DiagnosticCode);
    }

    [Fact]
    public void RequestFactory_Should_RejectTimeoutAndIntervalCombinationsThatExceedTheAttemptBudget()
    {
        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            null,
            [],
            "5m",
            "5ms",
            3));

        Assert.Equal("ASCAN401", exception.DiagnosticCode);
        Assert.Contains("300 attempts", exception.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_Should_EnforceTheAttemptBudgetForInternalCallers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanaryPollRequest(
            new Uri("https://app.example.test/_appsurface/canaries/forwarding.alpha-evidence"),
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            [],
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(5),
            3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanaryPollRequest(
            new Uri("https://app.example.test/_appsurface/canaries/forwarding.alpha-evidence"),
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            [],
            TimeSpan.FromMilliseconds(uint.MaxValue),
            TimeSpan.FromDays(1),
            3));
    }

    [Fact]
    public void Request_Should_RejectOversizedDirectAuthenticationAndCustomHeaderInputs()
    {
        Assert.Throws<ArgumentException>(() => new CanaryPollHeader("X-Deploy-Audit", new string('h', 4 * 1024 + 1)));
        Assert.Throws<ArgumentException>(() => new CanaryPollRequest(
            new Uri("https://app.example.test/_appsurface/canaries/forwarding.alpha-evidence"),
            "forwarding.alpha-evidence",
            null,
            null,
            new string('b', 16 * 1024 + 1),
            [],
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(5),
            3));
        Assert.Throws<ArgumentException>(() => new CanaryPollRequest(
            new Uri("https://app.example.test/_appsurface/canaries/forwarding.alpha-evidence"),
            "forwarding.alpha-evidence",
            new string('m', 257),
            null,
            null,
            [],
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(5),
            3));
    }

    [Fact]
    public void RequestFactory_Should_RejectOversizedAuthenticationAndCustomHeaderValues()
    {
        using var bearer = new EnvironmentVariableScope("APPSURFACE_CANARY_OVERSIZED_BEARER", new string('b', 16 * 1024 + 1));
        using var header = new EnvironmentVariableScope("APPSURFACE_CANARY_OVERSIZED_HEADER", new string('h', 4 * 1024 + 1));

        var bearerException = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            "APPSURFACE_CANARY_OVERSIZED_BEARER",
            null,
            [],
            "5m",
            "5s",
            3));
        var headerException = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            null,
            ["X-Deploy-Audit=APPSURFACE_CANARY_OVERSIZED_HEADER"],
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN402", bearerException.DiagnosticCode);
        Assert.Equal("ASCAN402", headerException.DiagnosticCode);
        Assert.DoesNotContain("bbbb", bearerException.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("hhhh", headerException.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestFactory_Should_RejectOversizedCustomHeaderNames()
    {
        using var header = new EnvironmentVariableScope("APPSURFACE_CANARY_HEADER", "header-sentinel");
        var oversizedHeaderName = new string('X', 129);

        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            null,
            [$"{oversizedHeaderName}=APPSURFACE_CANARY_HEADER"],
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN401", exception.DiagnosticCode);
    }

    [Fact]
    public void EnvelopeParser_Should_Reject_DuplicateCompatibilityCore()
    {
        var body = Encoding.UTF8.GetBytes("""
            {"name":"forwarding.alpha-evidence","name":"forwarding.alpha-evidence","ready":true,"status":"pass"}
            """);

        Assert.Throws<CanaryPollProtocolException>(() =>
            CanaryPollEnvelopeParser.Parse(body, "forwarding.alpha-evidence"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"forwarding.alpha-evidence\",\"ready\":\"true\",\"status\":\"pass\"}")]
    [InlineData("{\"name\":\"other.canary\",\"ready\":true,\"status\":\"pass\"}")]
    [InlineData("{\"name\":\"forwarding.alpha-evidence\",\"ready\":false,\"status\":\"pass\"}")]
    [InlineData("{\"name\":\"forwarding.alpha-evidence\",\"ready\":false,\"status\":\"unknown\"}")]
    [InlineData("not-json")]
    public void EnvelopeParser_Should_Reject_InvalidCompatibilityCores(string json)
    {
        Assert.Throws<CanaryPollProtocolException>(() =>
            CanaryPollEnvelopeParser.Parse(Encoding.UTF8.GetBytes(json), "forwarding.alpha-evidence"));
    }

    [Fact]
    public async Task Workflow_Should_WaitOnceForPending_ThenPass()
    {
        var client = new QueueCanaryPollHttpClient(
            JsonResponse(HttpStatusCode.ServiceUnavailable, "pending", ready: false),
            JsonResponse(HttpStatusCode.OK, "pass", ready: true));
        var delay = new RecordingDelay();
        var workflow = new CanaryPollWorkflow(client, TimeProvider.System, delay);

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("pass", result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(5)], delay.Delays);
    }

    [Fact]
    public async Task Workflow_Should_TreatTruncatedServerErrorAsRecoverableUntilAllowanceExhausts()
    {
        var response = new CanaryPollHttpResponse(
            HttpStatusCode.BadGateway,
            "application/json",
            [],
            true,
            null);
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(response, response),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(maxTransientFailures: 1), CancellationToken.None);

        Assert.Equal("transient-exhausted", result.Outcome);
        Assert.Equal(5, result.ExitCode);
        Assert.Equal(2, result.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)429)]
    public async Task Workflow_Should_RetryRecoverableClientResponsesWithInvalidEnvelopes(HttpStatusCode statusCode)
    {
        var invalidEnvelope = new CanaryPollHttpResponse(
            statusCode,
            "application/json",
            Encoding.UTF8.GetBytes("{}"),
            false,
            null);
        var delay = new RecordingDelay();
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(invalidEnvelope, JsonResponse(HttpStatusCode.OK, "pass", ready: true)),
            TimeProvider.System,
            delay);

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("pass", result.Outcome);
        Assert.Equal(2, result.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(5)], delay.Delays);
    }

    [Fact]
    public async Task Workflow_Should_StopBeforeDispatch_WhenTheTotalDeadlineHasElapsed()
    {
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(),
            new ExpiredTimeProvider(),
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(timeout: TimeSpan.FromMilliseconds(1)), CancellationToken.None);

        Assert.Equal("deadline-exhausted", result.Outcome);
        Assert.Equal(6, result.ExitCode);
        Assert.Equal(0, result.Attempts);
    }

    [Fact]
    public async Task Command_Should_KeepEnvironmentValuesOutOfTextAndJsonOutput()
    {
        const string markerValue = "marker-sentinel-should-not-leak";
        const string tokenValue = "token-sentinel-should-not-leak";
        using var marker = new EnvironmentVariableScope("APPSURFACE_CANARY_MARKER", markerValue);
        using var token = new EnvironmentVariableScope("APPSURFACE_CANARY_TOKEN", tokenValue);
        using var console = new FakeInMemoryConsole();
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.OK, "pass", ready: true)),
            TimeProvider.System,
            new RecordingDelay());
        var command = new CanaryPollCommand(workflow)
        {
            Url = "https://app.example.test/path/not-for-terminal-output",
            Name = "forwarding.alpha-evidence",
            MarkerEnvironmentVariable = "APPSURFACE_CANARY_MARKER",
            BearerTokenEnvironmentVariable = "APPSURFACE_CANARY_TOKEN",
            Json = true,
        };
        var originalExitCode = Environment.ExitCode;

        try
        {
            Environment.ExitCode = 0;
            await command.ExecuteAsync(console);

            Assert.Equal(0, Environment.ExitCode);
            var output = console.ReadOutputString();
            ValueSafeAssert.DoesNotExpose(markerValue, output);
            ValueSafeAssert.DoesNotExpose(tokenValue, output);
            ValueSafeAssert.DoesNotExpose("https://app.example.test/path/not-for-terminal-output", output);
            Assert.Contains("\"outcome\":\"pass\"", output, StringComparison.Ordinal);
            Assert.Contains("\"docsUrl\"", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("stale")]
    [InlineData("not-configured")]
    public async Task TextRenderer_Should_IncludeEverySemanticFailureOutcome(string outcome)
    {
        using var console = new FakeInMemoryConsole();
        var result = CanaryPollResult.SemanticFailure(
            outcome,
            "forwarding.alpha-evidence",
            attempts: 1,
            TimeSpan.FromMilliseconds(42),
            reasonCode: null,
            summary: null);

        await CanaryPollResultRenderer.WriteAsync(console, result, json: false);

        var output = console.ReadOutputString();
        Assert.StartsWith($"ASCAN403 outcome={outcome} canary=forwarding.alpha-evidence", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpClientAdapter_Should_BoundBodiesWithoutParsingTheRemainder()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', (64 * 1024) + 1), Encoding.UTF8, "application/json"),
            }));
        var adapter = new CanaryPollHttpClient(httpClient);

        var response = await adapter.SendAsync(CreateRequest(), CancellationToken.None);

        Assert.True(response.Truncated);
        Assert.Equal(64 * 1024, response.Body.Length);
    }

    [Fact]
    public async Task HttpClientAdapter_Should_SendOnlyTheConfiguredCanaryAndAuthenticationHeaders()
    {
        var handler = new CapturingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var adapter = new CanaryPollHttpClient(httpClient);
        var request = new CanaryPollRequest(
            new Uri("https://app.example.test/_appsurface/canaries/forwarding.alpha-evidence"),
            "forwarding.alpha-evidence",
            "marker-sentinel",
            new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero),
            "token-sentinel",
            [],
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(5),
            3);

        await adapter.SendAsync(request, CancellationToken.None);

        Assert.Equal("marker-sentinel", handler.Headers["X-AppSurface-Canary-Marker"]);
        Assert.Equal("2026-07-30T00:00:00.0000000+00:00", handler.Headers["X-AppSurface-Canary-Fresh-Since"]);
        Assert.Equal("Bearer token-sentinel", handler.Headers["Authorization"]);
        Assert.Equal("/_appsurface/canaries/forwarding.alpha-evidence", handler.RequestPath);
    }

    [Fact]
    public async Task GithubSummaryWriter_Should_EscapeSafeFieldsWithoutChangingTheResult()
    {
        var path = TestPathUtils.PathUnder(Path.GetTempPath(), $"appsurface-canary-{Guid.NewGuid():N}.md");
        var result = CanaryPollResult.SemanticFailure(
            "stale",
            "canary|name",
            attempts: 2,
            TimeSpan.FromMilliseconds(12),
            reasonCode: null,
            summary: null);

        try
        {
            Assert.True(await CanaryPollGithubSummaryWriter.TryWriteAsync(path, result));
            var markdown = await File.ReadAllTextAsync(path);
            Assert.Contains("canary\\|name", markdown, StringComparison.Ordinal);
            Assert.Contains("stale", markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task BoundedHttpBodyReader_Should_KeepAnExactLimitWithoutMarkingItTruncated()
    {
        var expected = Enumerable.Repeat((byte)'x', 1024).ToArray();
        using var content = new ByteArrayContent(expected);

        var body = await BoundedHttpBodyReader.ReadAsync(content, expected.Length, CancellationToken.None);

        Assert.False(body.Truncated);
        Assert.Equal(expected, body.Bytes);
    }

    [Fact]
    public async Task BoundedHttpBodyReader_Should_RejectNonpositiveLimits()
    {
        using var content = new ByteArrayContent([]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BoundedHttpBodyReader.ReadAsync(content, 0, CancellationToken.None));
    }

    [Fact]
    public async Task BoundedHttpBodyReader_Should_ReturnAnEmptyUntruncatedBody_WhenTheStreamEndsImmediately()
    {
        using var content = new ByteArrayContent([]);

        var body = await BoundedHttpBodyReader.ReadAsync(content, 128, CancellationToken.None);

        Assert.Empty(body.Bytes);
        Assert.False(body.Truncated);
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("double--hyphen")]
    [InlineData("leading..dot")]
    [InlineData("trailing.")]
    public void RequestFactory_Should_Reject_InvalidCanaryNames(string name)
    {
        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            name,
            null,
            null,
            null,
            null,
            [],
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN401", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("5ms", "1s")]
    [InlineData("1s", "1h")]
    [InlineData("1h", "1m")]
    public void RequestFactory_Should_ParseEverySupportedDurationUnit(string timeout, string interval)
    {
        var request = CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            null,
            [],
            timeout,
            interval,
            0);

        Assert.True(request.Timeout > TimeSpan.Zero);
        Assert.True(request.Interval > TimeSpan.Zero);
        Assert.Equal(0, request.MaxTransientFailures);
    }

    [Theory]
    [InlineData("2026-07-30T00:00:00+02:00")]
    [InlineData("2026-07-30T00:00:00.1234567Z")]
    public void RequestFactory_Should_NormalizeValidFreshnessBoundaries(string freshSince)
    {
        var request = CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            freshSince,
            null,
            null,
            [],
            "5m",
            "5s",
            3);

        Assert.NotNull(request.FreshSince);
        Assert.Equal(TimeSpan.Zero, request.FreshSince.Value.Offset);
    }

    [Theory]
    [InlineData("2026-07-30")]
    [InlineData("2026-07-30T00:00:00")]
    [InlineData("2026-07-30T00:00:00.12345678Z")]
    public void RequestFactory_Should_RejectInvalidFreshnessBoundaries(string freshSince)
    {
        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            freshSince,
            null,
            null,
            [],
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN401", exception.DiagnosticCode);
    }

    [Fact]
    public void RequestFactory_Should_ResolveOneCustomHeaderFromTheEnvironment()
    {
        using var header = new EnvironmentVariableScope("APPSURFACE_CANARY_AUDIT", "audit-sentinel");

        var request = CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            null,
            ["X-Deploy-Audit=APPSURFACE_CANARY_AUDIT"],
            "5m",
            "5s",
            3);

        var resolved = Assert.Single(request.CustomHeaders);
        Assert.Equal("X-Deploy-Audit", resolved.Name);
        Assert.Equal("audit-sentinel", resolved.Value);
    }

    [Theory]
    [InlineData("Authorization=APPSURFACE_CANARY_HEADER")]
    [InlineData("X-Deploy-Audit=APPSURFACE_CANARY_HEADER", "x-deploy-audit=APPSURFACE_CANARY_HEADER")]
    public void RequestFactory_Should_RejectReservedAndDuplicateCustomHeaders(params string[] headers)
    {
        using var value = new EnvironmentVariableScope("APPSURFACE_CANARY_HEADER", "header-sentinel");

        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            null,
            headers,
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN401", exception.DiagnosticCode);
    }

    [Fact]
    public void RequestFactory_Should_RejectConflictingAuthenticationSources()
    {
        using var bearer = new EnvironmentVariableScope("APPSURFACE_CANARY_BEARER", "bearer-sentinel");
        using var identity = new EnvironmentVariableScope("APPSURFACE_CANARY_IDENTITY", "identity-sentinel");

        var exception = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test",
            "forwarding.alpha-evidence",
            null,
            null,
            "APPSURFACE_CANARY_BEARER",
            "APPSURFACE_CANARY_IDENTITY",
            [],
            "5m",
            "5s",
            3));

        Assert.Equal("ASCAN401", exception.DiagnosticCode);
    }

    [Fact]
    public void EnvelopeParser_Should_KeepOnlySafeOptionalEvidence()
    {
        var envelope = CanaryPollEnvelopeParser.Parse(
            Encoding.UTF8.GetBytes("""
                {"name":"forwarding.alpha-evidence","ready":true,"status":"pass","reasonCode":"proof-ready","summary":"accepted proof"}
                """),
            "forwarding.alpha-evidence");

        Assert.Equal("proof-ready", envelope.ReasonCode);
        Assert.Equal("accepted proof", envelope.Summary);
    }

    [Theory]
    [InlineData("invalid reason", false, null)]
    [InlineData("proof-current", true, "proof-current")]
    public void EnvelopeParser_Should_DropUnsafeOptionalEvidence(string reasonCode, bool oversizedSummary, string? expectedReasonCode)
    {
        var summary = oversizedSummary ? new string('x', 257) : "ready\nfor deployment";
        var body = JsonSerializer.Serialize(new
        {
            name = "forwarding.alpha-evidence",
            ready = true,
            status = "pass",
            reasonCode,
            summary,
        });

        var envelope = CanaryPollEnvelopeParser.Parse(Encoding.UTF8.GetBytes(body), "forwarding.alpha-evidence");

        Assert.Equal(expectedReasonCode, envelope.ReasonCode);
        Assert.Null(envelope.Summary);
    }

    [Fact]
    public async Task Workflow_Should_RetryTransportExceptions_ThenPass()
    {
        var delay = new RecordingDelay();
        var workflow = new CanaryPollWorkflow(
            new ScriptedCanaryPollHttpClient(
                (_, _) => Task.FromException<CanaryPollHttpResponse>(new HttpRequestException("transient")),
                (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "pass", ready: true))),
            TimeProvider.System,
            delay);

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("pass", result.Outcome);
        Assert.Equal(2, result.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(5)], delay.Delays);
    }

    [Fact]
    public async Task Workflow_Should_IgnoreAPassEnvelopeFromARecoverableHttpStatus()
    {
        var delay = new RecordingDelay();
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(
                JsonResponse(HttpStatusCode.ServiceUnavailable, "pass", ready: true),
                JsonResponse(HttpStatusCode.OK, "pass", ready: true)),
            TimeProvider.System,
            delay);

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("pass", result.Outcome);
        Assert.Equal(2, result.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(5)], delay.Delays);
    }

    [Fact]
    public async Task Workflow_Should_ReturnASemanticFailureFromARecoverableHttpStatus()
    {
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.ServiceUnavailable, "stale", ready: false)),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("stale", result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Workflow_Should_UseRetryAfterDeltaAsThePendingDelay()
    {
        var delay = new RecordingDelay();
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(
                JsonResponse(HttpStatusCode.ServiceUnavailable, "pending", ready: false, retryAfter: new CanaryPollRetryAfter(TimeSpan.FromSeconds(9), null)),
                JsonResponse(HttpStatusCode.OK, "pass", ready: true)),
            TimeProvider.System,
            delay);

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("pass", result.Outcome);
        Assert.Equal([TimeSpan.FromSeconds(9)], delay.Delays);
    }

    [Fact]
    public async Task Workflow_Should_UseRetryAfterDateAsThePendingDelay()
    {
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var delay = new RecordingDelay();
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(
                JsonResponse(HttpStatusCode.ServiceUnavailable, "pending", ready: false, retryAfter: new CanaryPollRetryAfter(null, now.AddSeconds(8))),
                JsonResponse(HttpStatusCode.OK, "pass", ready: true)),
            new FixedTimeProvider(now),
            delay);

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("pass", result.Outcome);
        Assert.Equal([TimeSpan.FromSeconds(8)], delay.Delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, "application/json", false)]
    [InlineData(HttpStatusCode.OK, "text/plain", false)]
    [InlineData(HttpStatusCode.OK, "application/json", true)]
    public async Task Workflow_Should_ReturnProtocolFailure_ForUnsafeResponseShapes(
        HttpStatusCode statusCode,
        string contentType,
        bool truncated)
    {
        var response = new CanaryPollHttpResponse(statusCode, contentType, [], truncated, null);
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(response),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("remote-protocol", result.Outcome);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Workflow_Should_ReturnProtocolFailure_ForANonrecoverableInvalidEnvelope()
    {
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(new CanaryPollHttpResponse(HttpStatusCode.Unauthorized, "application/json", Encoding.UTF8.GetBytes("{}"), false, null)),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("remote-protocol", result.Outcome);
        Assert.Equal(4, result.ExitCode);
    }

    [Fact]
    public async Task Workflow_Should_ReturnCancelled_WhenTheDelayObservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.ServiceUnavailable, "pending", ready: false)),
            TimeProvider.System,
            new CancellingDelay(cancellation));

        var result = await workflow.RunAsync(CreateRequest(), cancellation.Token);

        Assert.Equal("cancelled", result.Outcome);
        Assert.Equal(130, result.ExitCode);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Workflow_Should_ReturnDeadline_WhenAnAttemptTimesOut()
    {
        var workflow = new CanaryPollWorkflow(
            new BlockingCanaryPollHttpClient(),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(timeout: TimeSpan.FromMilliseconds(50)), CancellationToken.None);

        Assert.Equal("deadline-exhausted", result.Outcome);
        Assert.Equal(6, result.ExitCode);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Command_Should_RejectConflictingSummaryOptions_WithoutDispatching()
    {
        using var console = new FakeInMemoryConsole();
        var client = new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.OK, "pass", ready: true));
        var command = new CanaryPollCommand(new CanaryPollWorkflow(client, TimeProvider.System, new RecordingDelay()))
        {
            GithubSummary = true,
            NoGithubSummary = true,
        };
        var originalExitCode = Environment.ExitCode;

        try
        {
            await command.ExecuteAsync(console);

            Assert.Equal(0, client.Calls);
            Assert.Equal(2, Environment.ExitCode);
            Assert.Contains("ASCAN401", console.ReadOutputString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task Command_Should_WriteAJsonSemanticFailure_AndWarnWhenAnExplicitSummaryCannotBeWritten()
    {
        using var githubSummary = new EnvironmentVariableScope("GITHUB_STEP_SUMMARY", null);
        using var console = new FakeInMemoryConsole();
        var command = new CanaryPollCommand(new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.OK, "stale", ready: false)),
            TimeProvider.System,
            new RecordingDelay()))
        {
            Url = "https://app.example.test",
            Name = "forwarding.alpha-evidence",
            GithubSummary = true,
            Json = true,
        };
        var originalExitCode = Environment.ExitCode;

        try
        {
            await command.ExecuteAsync(console);

            Assert.Equal(3, Environment.ExitCode);
            Assert.Contains("\"outcome\":\"stale\"", console.ReadOutputString(), StringComparison.Ordinal);
            Assert.Contains("\"retryable\":false", console.ReadOutputString(), StringComparison.Ordinal);
            Assert.Contains("ASCAN407", console.ReadErrorString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task GithubSummaryWriter_Should_ReturnFalse_WhenThePathIsMissingOrTheResultIsTooLarge()
    {
        var oversizedResult = CanaryPollResult.Pass(new string('x', 9000), 1, TimeSpan.Zero, null, null);

        Assert.False(await CanaryPollGithubSummaryWriter.TryWriteAsync(null, oversizedResult));
        Assert.False(await CanaryPollGithubSummaryWriter.TryWriteAsync(Path.GetTempPath(), oversizedResult));
        Assert.False(await CanaryPollGithubSummaryWriter.TryWriteAsync("\0", oversizedResult));
    }

    [Fact]
    public async Task GithubSummaryWriter_Should_ReturnFalse_WhenWritingANullNamedResultToADirectory()
    {
        var result = CanaryPollResult.ProtocolFailure(1, TimeSpan.Zero);

        Assert.False(await CanaryPollGithubSummaryWriter.TryWriteAsync(Path.GetTempPath(), result));
    }

    [Fact]
    public async Task Command_Should_RenderInputFailuresWithoutDispatching()
    {
        using var console = new FakeInMemoryConsole();
        var client = new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.OK, "pass", ready: true));
        var command = new CanaryPollCommand(new CanaryPollWorkflow(client, TimeProvider.System, new RecordingDelay()))
        {
            Url = "https://app.example.test/?unsafe=true",
            Name = "forwarding.alpha-evidence",
        };
        var originalExitCode = Environment.ExitCode;

        try
        {
            await command.ExecuteAsync(console);

            Assert.Equal(0, client.Calls);
            Assert.Equal(2, Environment.ExitCode);
            Assert.Contains("ASCAN401", console.ReadOutputString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Fact]
    public async Task Workflow_Should_StopBeforeDispatchingWhenTheCallerHasAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.OK, "pass", ready: true));
        var workflow = new CanaryPollWorkflow(client, TimeProvider.System, new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(), cancellation.Token);

        Assert.Equal("cancelled", result.Outcome);
        Assert.Equal(0, result.Attempts);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Workflow_Should_ExhaustRecoverableTransportFailuresWithoutAnotherDispatch()
    {
        var workflow = new CanaryPollWorkflow(
            new ScriptedCanaryPollHttpClient((_, _) => Task.FromException<CanaryPollHttpResponse>(new HttpRequestException("transient"))),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(maxTransientFailures: 0), CancellationToken.None);

        Assert.Equal("transient-exhausted", result.Outcome);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Workflow_Should_ExhaustRecoverableInvalidEnvelopesWithoutAnotherDispatch()
    {
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(new CanaryPollHttpResponse(HttpStatusCode.ServiceUnavailable, "application/json", Encoding.UTF8.GetBytes("{}"), false, null)),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(maxTransientFailures: 0), CancellationToken.None);

        Assert.Equal("transient-exhausted", result.Outcome);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Workflow_Should_ExhaustRecoverablePassPayloadsWithoutAnotherDispatch()
    {
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.ServiceUnavailable, "pass", ready: true)),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(maxTransientFailures: 0), CancellationToken.None);

        Assert.Equal("transient-exhausted", result.Outcome);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Workflow_Should_ReturnDeadlineWhenTheScheduledDelayExceedsTheRemainingTime()
    {
        var request = new CanaryPollRequest(
            new Uri("https://app.example.test/_appsurface/canaries/forwarding.alpha-evidence"),
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            [],
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            3);
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.OK, "pending", ready: false)),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(request, CancellationToken.None);

        Assert.Equal("deadline-exhausted", result.Outcome);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task CanaryPollHttpClient_Should_ForwardValidatedCustomHeaders()
    {
        var handler = new CapturingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var client = new CanaryPollHttpClient(httpClient);
        var request = new CanaryPollRequest(
            new Uri("https://app.example.test/_appsurface/canaries/forwarding.alpha-evidence"),
            "forwarding.alpha-evidence",
            null,
            null,
            null,
            [new CanaryPollHeader("X-Deploy-Audit", "audit-sentinel")],
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(5),
            3);

        await client.SendAsync(request, CancellationToken.None);

        Assert.Equal("audit-sentinel", handler.Headers["X-Deploy-Audit"]);
    }

    [Fact]
    public async Task TimeProviderCanaryPollDelay_Should_PropagateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var delay = new TimeProviderCanaryPollDelay(TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay.DelayAsync(TimeSpan.FromSeconds(1), cancellation.Token));
    }

    [Fact]
    public void EnvelopeParser_Should_RejectANonObjectPayload()
    {
        Assert.Throws<CanaryPollProtocolException>(() =>
            CanaryPollEnvelopeParser.Parse(Encoding.UTF8.GetBytes("[]"), "forwarding.alpha-evidence"));
    }

    [Fact]
    public void RequestFactory_Should_RejectNegativeTransientFailuresAndInvalidHeaderSources()
    {
        var negativeFailures = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test", "forwarding.alpha-evidence", null, null, null, null, [], "5m", "5s", -1));
        var invalidHeader = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test", "forwarding.alpha-evidence", null, null, null, null, ["X-Deploy-Audit"], "5m", "5s", 3));

        Assert.Equal("ASCAN401", negativeFailures.DiagnosticCode);
        Assert.Equal("ASCAN401", invalidHeader.DiagnosticCode);
    }

    [Fact]
    public void RequestFactory_Should_RejectOversizedMarkersAndTooManyCustomHeaders()
    {
        using var marker = new EnvironmentVariableScope("APPSURFACE_CANARY_OVERSIZED_MARKER", new string('m', 257));
        var oversizedMarker = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test", "forwarding.alpha-evidence", "APPSURFACE_CANARY_OVERSIZED_MARKER", null, null, null, [], "5m", "5s", 3));
        var tooManyHeaders = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test", "forwarding.alpha-evidence", null, null, null, null,
            Enumerable.Range(0, 9).Select(index => $"X-Deploy-{index}=APPSURFACE_CANARY_OVERSIZED_MARKER").ToArray(), "5m", "5s", 3));

        Assert.Equal("ASCAN401", oversizedMarker.DiagnosticCode);
        Assert.Equal("ASCAN401", tooManyHeaders.DiagnosticCode);
    }

    [Fact]
    public void RequestFactory_Should_RejectMalformedDurationsAndControlFreeChecksShouldRejectMalformedText()
    {
        var invalidDuration = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test", "forwarding.alpha-evidence", null, null, null, null, [], "n/a", "5s", 3));
        var overflowingDuration = Assert.Throws<CanaryPollInputException>(() => CanaryPollRequestFactory.Create(
            "https://app.example.test", "forwarding.alpha-evidence", null, null, null, null, [], "999999999999999999999999999999999999999999h", "5s", 3));

        Assert.Equal("ASCAN401", invalidDuration.DiagnosticCode);
        Assert.Equal("ASCAN401", overflowingDuration.DiagnosticCode);
        Assert.False(CanaryPollRequestFactory.IsWellFormedControlFree("\uD800"));
    }

    [Fact]
    public void Request_Should_RejectInvalidSchedulesAndCustomHeaderCollections()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDirectRequest(TimeSpan.Zero, TimeSpan.FromSeconds(5), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDirectRequest(TimeSpan.FromMinutes(5), TimeSpan.Zero, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDirectRequest(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), [], maxTransientFailures: -1));
        Assert.Throws<ArgumentException>(() => CreateDirectRequest(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), [], name: " "));
        Assert.Throws<ArgumentException>(() => CreateDirectRequest(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), [], name: new string('n', 129)));
        Assert.Throws<ArgumentException>(() => new CanaryPollHeader("Authorization", "not-allowed"));

        var duplicateHeaders = new[]
        {
            new CanaryPollHeader("X-Deploy-Audit", "one"),
            new CanaryPollHeader("x-deploy-audit", "two"),
        };
        Assert.Throws<ArgumentException>(() => CreateDirectRequest(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), duplicateHeaders));

        var oversizedHeaders = Enumerable.Range(0, 5)
            .Select(index => new CanaryPollHeader($"X-Deploy-{index}", new string('x', 4 * 1024)))
            .ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDirectRequest(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), oversizedHeaders));

        var tooManyHeaders = Enumerable.Range(0, 9)
            .Select(index => new CanaryPollHeader($"X-Deploy-{index}", "audit"))
            .ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDirectRequest(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), tooManyHeaders));
    }

    [Fact]
    public async Task Workflow_Should_ReturnCancelledWhenTheHttpAttemptObservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var workflow = new CanaryPollWorkflow(
            new ScriptedCanaryPollHttpClient((_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<CanaryPollHttpResponse>(token);
            }),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(), cancellation.Token);

        Assert.Equal("cancelled", result.Outcome);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Workflow_Should_ReturnProtocolFailureForInvalidNonrecoverableEnvelopes()
    {
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(new CanaryPollHttpResponse(HttpStatusCode.OK, "application/json", Encoding.UTF8.GetBytes("{}"), false, null)),
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("remote-protocol", result.Outcome);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task Workflow_Should_ReturnDeadlineWhenItsDelayIsCancelledByTheDeadline()
    {
        var workflow = new CanaryPollWorkflow(
            new QueueCanaryPollHttpClient(JsonResponse(HttpStatusCode.OK, "pending", ready: false)),
            TimeProvider.System,
            new ThrowingDelay());

        var result = await workflow.RunAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("deadline-exhausted", result.Outcome);
        Assert.Equal(1, result.Attempts);
    }

    private static CanaryPollRequest CreateRequest(int maxTransientFailures = 3, TimeSpan? timeout = null) => new(
        new Uri("https://app.example.test/path/_appsurface/canaries/forwarding.alpha-evidence"),
        "forwarding.alpha-evidence",
        null,
        null,
        null,
        [],
        timeout ?? TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(5),
        maxTransientFailures);

    private static CanaryPollRequest CreateDirectRequest(
        TimeSpan timeout,
        TimeSpan interval,
        IReadOnlyList<CanaryPollHeader> customHeaders,
        int maxTransientFailures = 3,
        string name = "forwarding.alpha-evidence") => new(
        new Uri("https://app.example.test/_appsurface/canaries/forwarding.alpha-evidence"),
        name,
        null,
        null,
        null,
        customHeaders,
        timeout,
        interval,
        maxTransientFailures);

    private static CanaryPollHttpResponse JsonResponse(
        HttpStatusCode statusCode,
        string status,
        bool ready,
        CanaryPollRetryAfter? retryAfter = null) => new(
        statusCode,
        "application/json",
        Encoding.UTF8.GetBytes($"{{\"name\":\"forwarding.alpha-evidence\",\"ready\":{ready.ToString().ToLowerInvariant()},\"status\":\"{status}\"}}"),
        false,
        retryAfter);

    private sealed class QueueCanaryPollHttpClient(params CanaryPollHttpResponse[] responses) : ICanaryPollHttpClient
    {
        private readonly Queue<CanaryPollHttpResponse> _responses = new(responses);

        public int Calls { get; private set; }

        public Task<CanaryPollHttpResponse> SendAsync(CanaryPollRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.NotEmpty(_responses);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ScriptedCanaryPollHttpClient(
        params Func<CanaryPollRequest, CancellationToken, Task<CanaryPollHttpResponse>>[] actions) : ICanaryPollHttpClient
    {
        private readonly Queue<Func<CanaryPollRequest, CancellationToken, Task<CanaryPollHttpResponse>>> _actions = new(actions);

        public Task<CanaryPollHttpResponse> SendAsync(CanaryPollRequest request, CancellationToken cancellationToken)
        {
            Assert.NotEmpty(_actions);
            return _actions.Dequeue()(request, cancellationToken);
        }
    }

    private sealed class BlockingCanaryPollHttpClient : ICanaryPollHttpClient
    {
        public async Task<CanaryPollHttpResponse> SendAsync(CanaryPollRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation token should have interrupted the attempt.");
        }
    }

    private sealed class RecordingDelay : ICanaryPollDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingDelay(CancellationTokenSource cancellation) : ICanaryPollDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        }
    }

    private sealed class ThrowingDelay : ICanaryPollDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.FromException(new OperationCanceledException());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly long _timestamp = TimeProvider.System.GetTimestamp();

        public override long GetTimestamp() => _timestamp;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ExpiredTimeProvider : TimeProvider
    {
        private int _timestampsRead;

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

        public override long GetTimestamp() => Interlocked.Increment(ref _timestampsRead) == 1 ? 0 : TimestampFrequency;
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? RequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = Assert.Single(header.Value);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
