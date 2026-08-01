using System.Net;
using System.Text;
using CliFx.Infrastructure;
using ForgeTrust.RazorWire.Cli;

namespace ForgeTrust.AppSurface.Cli.Tests;

[CollectionDefinition("Canary poll process state", DisableParallelization = true)]
public sealed class CanaryPollProcessStateCollection
{
}

[Collection("Canary poll process state")]
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
            TimeProvider.System,
            new RecordingDelay());

        var result = await workflow.RunAsync(CreateRequest(timeout: TimeSpan.Zero), CancellationToken.None);

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
        var path = Path.Combine(Path.GetTempPath(), $"appsurface-canary-{Guid.NewGuid():N}.md");
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

    private static CanaryPollHttpResponse JsonResponse(HttpStatusCode statusCode, string status, bool ready) => new(
        statusCode,
        "application/json",
        Encoding.UTF8.GetBytes($"{{\"name\":\"forwarding.alpha-evidence\",\"ready\":{ready.ToString().ToLowerInvariant()},\"status\":\"{status}\"}}"),
        false,
        null);

    private sealed class QueueCanaryPollHttpClient(params CanaryPollHttpResponse[] responses) : ICanaryPollHttpClient
    {
        private readonly Queue<CanaryPollHttpResponse> _responses = new(responses);

        public Task<CanaryPollHttpResponse> SendAsync(CanaryPollRequest request, CancellationToken cancellationToken)
        {
            Assert.NotEmpty(_responses);
            return Task.FromResult(_responses.Dequeue());
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
