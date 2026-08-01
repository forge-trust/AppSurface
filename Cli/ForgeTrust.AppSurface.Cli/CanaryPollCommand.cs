using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CliFx;
using CliFx.Binding;
using CliFx.Infrastructure;

namespace ForgeTrust.AppSurface.Cli;

/// <summary>
/// Polls one protected AppSurface named-canary endpoint until it produces a terminal deployment decision.
/// </summary>
/// <remarks>
/// This command is read-only. It evaluates application-owned evidence already exposed by the protected Web endpoint;
/// it does not trigger a workflow, change traffic, perform a rollback, or replace health and readiness probes.
/// </remarks>
[Command("canary poll", Description = "Poll a protected named canary until it passes or reaches a terminal result.")]
internal sealed partial class CanaryPollCommand : ICommand
{
    private readonly CanaryPollWorkflow _workflow;

    public CanaryPollCommand(CanaryPollWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    /// <summary>Gets the absolute application base URL.</summary>
    [CommandOption("url", Description = "Required application base URL, for example https://app.example.com.")]
    public string? Url { get; set; }

    /// <summary>Gets the registered named-canary name.</summary>
    [CommandOption("name", Description = "Required registered lowercase named-canary identifier.")]
    public string? Name { get; set; }

    /// <summary>Gets the environment variable containing the optional canary marker.</summary>
    [CommandOption("marker-env", Description = "Optional environment variable containing the canary marker. Never pass a marker directly on the command line.")]
    public string? MarkerEnvironmentVariable { get; set; }

    /// <summary>Gets the optional proof freshness boundary.</summary>
    [CommandOption("fresh-since", Description = "Optional RFC 3339 proof freshness boundary, for example 2026-07-30T00:00:00Z.")]
    public string? FreshSince { get; set; }

    /// <summary>Gets the environment variable containing a bearer token.</summary>
    [CommandOption("bearer-token-env", Description = "Optional environment variable containing a bearer token.")]
    public string? BearerTokenEnvironmentVariable { get; set; }

    /// <summary>Gets the environment variable containing an already acquired identity token.</summary>
    [CommandOption("identity-token-env", Description = "Optional environment variable containing an already acquired identity token.")]
    public string? IdentityTokenEnvironmentVariable { get; set; }

    /// <summary>Gets repeatable custom header environment sources in HEADER=VARIABLE form.</summary>
    [CommandOption("header-env", Description = "Optional repeatable custom header source in HEADER=VARIABLE form. Values are read only from environment variables.")]
    public string[] HeaderEnvironmentVariables { get; set; } = [];

    /// <summary>Gets the total polling deadline.</summary>
    [CommandOption("timeout", Description = "Total polling deadline. Supports a positive number followed by ms, s, m, or h. Default: 5m.")]
    public string Timeout { get; set; } = "5m";

    /// <summary>Gets the interval between scheduled polls.</summary>
    [CommandOption("interval", Description = "Polling interval. Supports a positive number followed by ms, s, m, or h. Default: 5s.")]
    public string Interval { get; set; } = "5s";

    /// <summary>Gets the maximum consecutive recoverable transport failures.</summary>
    [CommandOption("max-transient-failures", Description = "Maximum consecutive recoverable transport failures. Default: 3.")]
    public int MaxTransientFailures { get; set; } = 3;

    /// <summary>Gets whether to write exactly one machine-readable JSON terminal result.</summary>
    [CommandOption("json", Description = "Write one machine-readable JSON terminal result to stdout.")]
    public bool Json { get; set; }

    /// <summary>Gets whether to write a GitHub Actions step summary when available.</summary>
    [CommandOption("github-summary", Description = "Write a GitHub Actions step summary when GITHUB_STEP_SUMMARY is available.")]
    public bool GithubSummary { get; set; }

    /// <summary>Gets whether to suppress GitHub Actions step summary output.</summary>
    [CommandOption("no-github-summary", Description = "Suppress GitHub Actions step summary output.")]
    public bool NoGithubSummary { get; set; }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);

        CanaryPollResult result;
        if (GithubSummary && NoGithubSummary)
        {
            result = CanaryPollResult.Invalid("ASCAN401", "Use either --github-summary or --no-github-summary, not both.");
        }
        else
        {
            try
            {
                var request = CanaryPollRequestFactory.Create(
                    Url,
                    Name,
                    MarkerEnvironmentVariable,
                    FreshSince,
                    BearerTokenEnvironmentVariable,
                    IdentityTokenEnvironmentVariable,
                    HeaderEnvironmentVariables,
                    Timeout,
                    Interval,
                    MaxTransientFailures);
                result = await _workflow.RunAsync(request, console.RegisterCancellationHandler());
            }
            catch (CanaryPollInputException exception)
            {
                result = CanaryPollResult.Invalid(exception.DiagnosticCode, exception.SafeMessage);
            }
            catch (OperationCanceledException)
            {
                result = CanaryPollResult.Cancelled();
            }
        }

        await CanaryPollResultRenderer.WriteAsync(console, result, Json);
        var summaryEnabled = !NoGithubSummary
            && (GithubSummary || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY")));
        if (summaryEnabled)
        {
            var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (!await CanaryPollGithubSummaryWriter.TryWriteAsync(summaryPath, result))
            {
                await console.Error.WriteLineAsync("ASCAN407 summary-write-warning: The GitHub Actions summary could not be written. The command result is unchanged.");
            }
        }

        Environment.ExitCode = result.ExitCode;
    }
}

/// <summary>
/// Coordinates deterministic named-canary polling without hidden HTTP retries.
/// </summary>
internal sealed class CanaryPollWorkflow
{
    private const int MaximumAttemptSeconds = 30;
    private readonly ICanaryPollHttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ICanaryPollDelay _delay;

    public CanaryPollWorkflow(
        ICanaryPollHttpClient httpClient,
        TimeProvider timeProvider,
        ICanaryPollDelay delay)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    /// <summary>Runs the caller-owned named-canary polling state machine.</summary>
    public async Task<CanaryPollResult> RunAsync(CanaryPollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = _timeProvider.GetTimestamp();
        var attempts = 0;
        var consecutiveTransientFailures = 0;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return CanaryPollResult.Cancelled(attempts, _timeProvider.GetElapsedTime(startedAt));
            }

            var remaining = Remaining(request.Timeout, startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return CanaryPollResult.Deadline(attempts, _timeProvider.GetElapsedTime(startedAt));
            }

            var attemptTimeout = remaining < TimeSpan.FromSeconds(MaximumAttemptSeconds)
                ? remaining
                : TimeSpan.FromSeconds(MaximumAttemptSeconds);
            using var attemptDeadline = new CancellationTokenSource(attemptTimeout);
            using var linkedAttempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptDeadline.Token);
            attempts++;

            CanaryPollHttpResponse response;
            try
            {
                response = await _httpClient.SendAsync(request, linkedAttempt.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CanaryPollResult.Cancelled(attempts, _timeProvider.GetElapsedTime(startedAt));
            }
            catch (OperationCanceledException) when (attemptDeadline.IsCancellationRequested)
            {
                return CanaryPollResult.Deadline(attempts, _timeProvider.GetElapsedTime(startedAt));
            }
            catch (Exception exception) when (IsRecoverableTransportException(exception))
            {
                consecutiveTransientFailures++;
                var recovered = await RetryAfterRecoverableFailureAsync(
                    request,
                    null,
                    startedAt,
                    attempts,
                    consecutiveTransientFailures,
                    cancellationToken);
                if (recovered is not null)
                {
                    return recovered;
                }

                continue;
            }

            if (IsAlwaysProtocolFailure(response.StatusCode)
                || response.Truncated
                || !string.Equals(response.ContentType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                if (IsRecoverableStatusCode(response.StatusCode) && !IsAlwaysProtocolFailure(response.StatusCode))
                {
                    consecutiveTransientFailures++;
                    var recovered = await RetryAfterRecoverableFailureAsync(
                        request,
                        response.RetryAfter,
                        startedAt,
                        attempts,
                        consecutiveTransientFailures,
                        cancellationToken);
                    if (recovered is not null)
                    {
                        return recovered;
                    }

                    continue;
                }

                return CanaryPollResult.ProtocolFailure(attempts, _timeProvider.GetElapsedTime(startedAt));
            }

            CanaryPollEnvelope envelope;
            try
            {
                envelope = CanaryPollEnvelopeParser.Parse(response.Body, request.Name);
            }
            catch (CanaryPollProtocolException)
            {
                if (IsRecoverableStatusCode(response.StatusCode))
                {
                    consecutiveTransientFailures++;
                    var recovered = await RetryAfterRecoverableFailureAsync(
                        request,
                        response.RetryAfter,
                        startedAt,
                        attempts,
                        consecutiveTransientFailures,
                        cancellationToken);
                    if (recovered is not null)
                    {
                        return recovered;
                    }

                    continue;
                }

                return CanaryPollResult.ProtocolFailure(attempts, _timeProvider.GetElapsedTime(startedAt));
            }

            consecutiveTransientFailures = 0;
            var elapsed = _timeProvider.GetElapsedTime(startedAt);
            if (envelope.Status == "pass")
            {
                return CanaryPollResult.Pass(request.Name, attempts, elapsed, envelope.ReasonCode, envelope.Summary);
            }

            if (envelope.Status is "fail" or "stale" or "not-configured")
            {
                return CanaryPollResult.SemanticFailure(
                    envelope.Status,
                    request.Name,
                    attempts,
                    elapsed,
                    envelope.ReasonCode,
                    envelope.Summary);
            }

            var pendingDelay = await DelayBeforeNextAttemptAsync(
                request,
                response.RetryAfter,
                startedAt,
                attempts,
                cancellationToken);
            if (pendingDelay is not null)
            {
                return pendingDelay;
            }
        }
    }

    private async Task<CanaryPollResult?> DelayBeforeNextAttemptAsync(
        CanaryPollRequest request,
        CanaryPollRetryAfter? retryAfter,
        long startedAt,
        int attempts,
        CancellationToken cancellationToken)
    {
        var delay = request.Interval;
        if (retryAfter?.Delta is { } delta && delta > delay)
        {
            delay = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            var dateDelay = date - _timeProvider.GetUtcNow();
            if (dateDelay > delay)
            {
                delay = dateDelay;
            }
        }

        var remaining = Remaining(request.Timeout, startedAt);
        if (remaining <= TimeSpan.Zero || delay >= remaining)
        {
            return CanaryPollResult.Deadline(attempts, _timeProvider.GetElapsedTime(startedAt));
        }

        using var deadline = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await _delay.DelayAsync(delay, linked.Token);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CanaryPollResult.Cancelled(attempts, _timeProvider.GetElapsedTime(startedAt));
        }
        catch (OperationCanceledException)
        {
            return CanaryPollResult.Deadline(attempts, _timeProvider.GetElapsedTime(startedAt));
        }
    }

    private async Task<CanaryPollResult?> RetryAfterRecoverableFailureAsync(
        CanaryPollRequest request,
        CanaryPollRetryAfter? retryAfter,
        long startedAt,
        int attempts,
        int consecutiveTransientFailures,
        CancellationToken cancellationToken)
    {
        if (consecutiveTransientFailures > request.MaxTransientFailures)
        {
            return CanaryPollResult.TransientExhausted(attempts, _timeProvider.GetElapsedTime(startedAt));
        }

        return await DelayBeforeNextAttemptAsync(request, retryAfter, startedAt, attempts, cancellationToken);
    }

    private TimeSpan Remaining(TimeSpan timeout, long startedAt) => timeout - _timeProvider.GetElapsedTime(startedAt);

    private static bool IsAlwaysProtocolFailure(HttpStatusCode statusCode) => (int)statusCode < 200
        || statusCode is HttpStatusCode.NoContent
        || (int)statusCode is >= 300 and < 400
        || ((int)statusCode is >= 400 and < 500 && !IsRecoverableStatusCode(statusCode));

    private static bool IsRecoverableStatusCode(HttpStatusCode statusCode) => statusCode is HttpStatusCode.RequestTimeout
        or (HttpStatusCode)429
        || (int)statusCode >= 500;

    private static bool IsRecoverableTransportException(Exception exception) => exception is HttpRequestException or IOException;
}

/// <summary>Performs one caller-owned delay in the polling state machine.</summary>
internal interface ICanaryPollDelay
{
    /// <summary>Waits for the requested duration or cancellation.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Uses the configured time provider for production polling delays.</summary>
internal sealed class TimeProviderCanaryPollDelay(TimeProvider timeProvider) : ICanaryPollDelay
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _timeProvider, cancellationToken);
}

/// <summary>Issues one named-canary HTTP request without following redirects.</summary>
internal interface ICanaryPollHttpClient
{
    /// <summary>Sends exactly one request and returns bounded response evidence.</summary>
    Task<CanaryPollHttpResponse> SendAsync(CanaryPollRequest request, CancellationToken cancellationToken);
}

/// <summary>Adapts the typed HTTP client to the polling workflow's bounded response contract.</summary>
internal sealed class CanaryPollHttpClient(HttpClient httpClient) : ICanaryPollHttpClient
{
    private const int MaximumBodyBytes = 64 * 1024;
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <inheritdoc />
    public async Task<CanaryPollHttpResponse> SendAsync(CanaryPollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Endpoint);
        if (request.Marker is not null)
        {
            message.Headers.TryAddWithoutValidation("X-AppSurface-Canary-Marker", request.Marker);
        }

        if (request.FreshSince is { } freshSince)
        {
            message.Headers.TryAddWithoutValidation(
                "X-AppSurface-Canary-Fresh-Since",
                freshSince.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        if (request.BearerToken is not null)
        {
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {request.BearerToken}");
        }

        foreach (var customHeader in request.CustomHeaders)
        {
            message.Headers.TryAddWithoutValidation(customHeader.Name, customHeader.Value);
        }

        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await BoundedHttpBodyReader.ReadAsync(response.Content, MaximumBodyBytes, cancellationToken);
        var retryAfter = response.Headers.RetryAfter is { } header
            ? new CanaryPollRetryAfter(header.Delta, header.Date)
            : null;
        return new CanaryPollHttpResponse(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            body.Bytes,
            body.Truncated,
            retryAfter);
    }
}

/// <summary>Captures one bounded named-canary HTTP response.</summary>
internal sealed record CanaryPollHttpResponse(
    HttpStatusCode StatusCode,
    string ContentType,
    byte[] Body,
    bool Truncated,
    CanaryPollRetryAfter? RetryAfter);

/// <summary>Captures a parsed Retry-After header without retaining raw header text.</summary>
internal sealed record CanaryPollRetryAfter(TimeSpan? Delta, DateTimeOffset? Date);

/// <summary>Parses the required named-canary compatibility core.</summary>
internal static class CanaryPollEnvelopeParser
{
    private static readonly Regex ReasonCodePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    /// <summary>Parses a bounded response and validates it against the requested canary name.</summary>
    public static CanaryPollEnvelope Parse(byte[] body, string expectedName)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CanaryPollProtocolException();
            }

            string? name = null;
            string? status = null;
            bool? ready = null;
            var nameCount = 0;
            var statusCount = 0;
            var readyCount = 0;
            string? reasonCode = null;
            string? summary = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "name":
                        nameCount++;
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            name = property.Value.GetString();
                        }

                        break;
                    case "status":
                        statusCount++;
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            status = property.Value.GetString();
                        }

                        break;
                    case "ready":
                        readyCount++;
                        if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            ready = property.Value.GetBoolean();
                        }

                        break;
                    case "reasonCode" when property.Value.ValueKind == JsonValueKind.String:
                        var candidateReason = property.Value.GetString();
                        if (candidateReason is not null && candidateReason.Length is >= 1 and <= 64 && ReasonCodePattern.IsMatch(candidateReason))
                        {
                            reasonCode = candidateReason;
                        }

                        break;
                    case "summary" when property.Value.ValueKind == JsonValueKind.String:
                        var candidateSummary = property.Value.GetString();
                        if (CanaryPollRequestFactory.IsSafeSummary(candidateSummary))
                        {
                            summary = candidateSummary;
                        }

                        break;
                }
            }

            if (nameCount != 1
                || statusCount != 1
                || readyCount != 1
                || string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(status)
                || ready is null
                || !string.Equals(name, expectedName, StringComparison.Ordinal)
                || status is not ("pass" or "pending" or "fail" or "stale" or "not-configured")
                || ready.Value != string.Equals(status, "pass", StringComparison.Ordinal))
            {
                throw new CanaryPollProtocolException();
            }

            return new CanaryPollEnvelope(status, reasonCode, summary);
        }
        catch (JsonException)
        {
            throw new CanaryPollProtocolException();
        }
    }
}

/// <summary>Represents the parsed safe fields required by the polling state machine.</summary>
internal sealed record CanaryPollEnvelope(string Status, string? ReasonCode, string? Summary);

/// <summary>Represents a protocol incompatibility without exposing response content.</summary>
internal sealed class CanaryPollProtocolException : Exception;

/// <summary>Normalizes safe command options and resolves environment-sourced values.</summary>
internal static class CanaryPollRequestFactory
{
    private const double MaximumCancellableDurationMilliseconds = uint.MaxValue - 1d;
    private static readonly Regex NamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvironmentNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex HeaderNamePattern = new("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex FreshSincePattern = new("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,7})?(?:Z|[+-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Host",
        "Content-Length",
        "Connection",
        "Transfer-Encoding",
        "X-AppSurface-Canary-Marker",
        "X-AppSurface-Canary-Fresh-Since",
    };

    /// <summary>Creates a fully normalized request before any HTTP connection is opened.</summary>
    public static CanaryPollRequest Create(
        string? urlText,
        string? name,
        string? markerEnvironmentVariable,
        string? freshSinceText,
        string? bearerTokenEnvironmentVariable,
        string? identityTokenEnvironmentVariable,
        IReadOnlyList<string>? headerEnvironmentVariables,
        string? timeoutText,
        string? intervalText,
        int maxTransientFailures)
    {
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || (baseUri.Scheme == Uri.UriSchemeHttp && !IsLoopback(baseUri)))
        {
            throw Invalid("--url must be an absolute HTTPS URL (or HTTP loopback URL) with no user-info, query, or fragment.");
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !NamePattern.IsMatch(name))
        {
            throw Invalid("--name must be a 1-128 character lowercase dot-separated canary name with internal hyphens only.");
        }

        var timeout = ParseDuration(timeoutText, "--timeout");
        var interval = ParseDuration(intervalText, "--interval");
        if (maxTransientFailures < 0)
        {
            throw Invalid("--max-transient-failures must be zero or greater.");
        }

        var marker = ResolveEnvironmentValue(markerEnvironmentVariable, "--marker-env", required: false);
        if (marker is not null && (!IsWellFormedControlFree(marker) || GetUtf8ByteCount(marker) > 256))
        {
            throw Invalid("The marker environment value must be control-free, well-formed Unicode, and at most 256 UTF-8 bytes.");
        }

        DateTimeOffset? freshSince = null;
        if (!string.IsNullOrWhiteSpace(freshSinceText))
        {
            if (!FreshSincePattern.IsMatch(freshSinceText)
                || !DateTimeOffset.TryParse(freshSinceText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedFreshSince))
            {
                throw Invalid("--fresh-since must be an RFC 3339 timestamp with Z or a numeric offset and at most seven fractional digits.");
            }

            freshSince = parsedFreshSince.ToUniversalTime();
        }

        var bearer = ResolveEnvironmentValue(bearerTokenEnvironmentVariable, "--bearer-token-env", required: false);
        var identity = ResolveEnvironmentValue(identityTokenEnvironmentVariable, "--identity-token-env", required: false);
        var headers = ResolveHeaders(headerEnvironmentVariables ?? []);
        if (new[] { bearer, identity, headers.Count == 0 ? null : "custom" }.Count(value => value is not null) > 1)
        {
            throw Invalid("Use only one authentication mode: --bearer-token-env, --identity-token-env, or --header-env.");
        }

        var path = baseUri.AbsolutePath.TrimEnd('/');
        var endpointBuilder = new UriBuilder(baseUri)
        {
            Path = $"{path}/_appsurface/canaries/{Uri.EscapeDataString(name)}",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return new CanaryPollRequest(
            endpointBuilder.Uri,
            name,
            marker,
            freshSince,
            bearer ?? identity,
            headers,
            timeout,
            interval,
            maxTransientFailures);
    }

    /// <summary>Returns whether a server summary is safe to expose in terminal evidence.</summary>
    public static bool IsSafeSummary(string? summary) => summary is not null
        && IsWellFormedControlFree(summary)
        && GetUtf8ByteCount(summary) <= 256;

    private static List<CanaryPollHeader> ResolveHeaders(IReadOnlyList<string> sources)
    {
        if (sources.Count > 8)
        {
            throw Invalid("--header-env supports at most eight headers.");
        }

        var result = new List<CanaryPollHeader>(sources.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var separator = source?.IndexOf('=') ?? -1;
            if (separator <= 0 || separator == source!.Length - 1)
            {
                throw Invalid("--header-env values must use HEADER=VARIABLE form.");
            }

            var headerName = source[..separator];
            var environmentName = source[(separator + 1)..];
            if (!HeaderNamePattern.IsMatch(headerName)
                || ReservedHeaders.Contains(headerName)
                || !names.Add(headerName))
            {
                throw Invalid("--header-env includes a duplicate, reserved, or invalid header name.");
            }

            var value = ResolveEnvironmentValue(environmentName, "--header-env", required: true)!;
            result.Add(new CanaryPollHeader(headerName, value));
        }

        return result;
    }

    private static string? ResolveEnvironmentValue(string? environmentName, string option, bool required)
    {
        if (environmentName is null)
        {
            return required ? throw Credential($"{option} requires a nonblank environment-variable name.") : null;
        }

        if (string.IsNullOrWhiteSpace(environmentName))
        {
            throw Credential($"{option} requires a nonblank environment-variable name.");
        }

        if (!EnvironmentNamePattern.IsMatch(environmentName))
        {
            throw Credential($"{option} must name an environment variable using [A-Za-z_][A-Za-z0-9_]*.");
        }

        var value = Environment.GetEnvironmentVariable(environmentName);
        if (string.IsNullOrWhiteSpace(value) || !IsWellFormedControlFree(value))
        {
            throw Credential($"{option} resolved to a missing, blank, malformed, or control-containing environment value.");
        }

        return value;
    }

    private static TimeSpan ParseDuration(string? value, string option)
    {
        var match = Regex.Match(value ?? string.Empty, "^(?<value>[0-9]+(?:\\.[0-9]+)?)(?<unit>ms|s|m|h)$", RegexOptions.CultureInvariant);
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, CultureInfo.InvariantCulture, out var numeric) || numeric <= 0)
        {
            throw Invalid($"{option} must be a positive duration ending in ms, s, m, or h.");
        }

        try
        {
            var duration = match.Groups["unit"].Value switch
            {
                "ms" => TimeSpan.FromMilliseconds(numeric),
                "s" => TimeSpan.FromSeconds(numeric),
                "m" => TimeSpan.FromMinutes(numeric),
                "h" => TimeSpan.FromHours(numeric),
                _ => throw Invalid($"{option} must be a positive duration ending in ms, s, m, or h."),
            };

            if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > MaximumCancellableDurationMilliseconds)
            {
                throw Invalid($"{option} is outside the supported duration range.");
            }

            return duration;
        }
        catch (OverflowException)
        {
            throw Invalid($"{option} is outside the supported duration range.");
        }
    }

    private static bool IsLoopback(Uri uri) => string.Equals(uri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.DnsSafeHost, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(uri.DnsSafeHost, "::1", StringComparison.Ordinal);

    private static bool IsWellFormedControlFree(string value)
    {
        if (value.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static int GetUtf8ByteCount(string value) => StrictUtf8.GetByteCount(value);

    private static CanaryPollInputException Invalid(string message) => new("ASCAN401", message);

    private static CanaryPollInputException Credential(string message) => new("ASCAN402", message);
}

/// <summary>Contains normalized request metadata and non-renderable environment-sourced values.</summary>
internal sealed class CanaryPollRequest
{
    public CanaryPollRequest(
        Uri endpoint,
        string name,
        string? marker,
        DateTimeOffset? freshSince,
        string? bearerToken,
        IReadOnlyList<CanaryPollHeader> customHeaders,
        TimeSpan timeout,
        TimeSpan interval,
        int maxTransientFailures)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Marker = marker;
        FreshSince = freshSince;
        BearerToken = bearerToken;
        CustomHeaders = customHeaders ?? throw new ArgumentNullException(nameof(customHeaders));
        Timeout = timeout;
        Interval = interval;
        MaxTransientFailures = maxTransientFailures;
    }

    public Uri Endpoint { get; }

    public string Name { get; }

    public string? Marker { get; }

    public DateTimeOffset? FreshSince { get; }

    public string? BearerToken { get; }

    public IReadOnlyList<CanaryPollHeader> CustomHeaders { get; }

    public TimeSpan Timeout { get; }

    public TimeSpan Interval { get; }

    public int MaxTransientFailures { get; }
}

/// <summary>Contains one non-renderable custom HTTP header.</summary>
internal sealed class CanaryPollHeader
{
    public CanaryPollHeader(string name, string value)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Name { get; }

    public string Value { get; }
}

/// <summary>Represents a safe local command-input failure.</summary>
internal sealed class CanaryPollInputException : Exception
{
    public CanaryPollInputException(string diagnosticCode, string safeMessage)
        : base(safeMessage)
    {
        DiagnosticCode = diagnosticCode;
        SafeMessage = safeMessage;
    }

    public string DiagnosticCode { get; }

    public string SafeMessage { get; }
}

/// <summary>Represents one safe terminal polling result.</summary>
internal sealed record CanaryPollResult(
    string Outcome,
    int ExitCode,
    string DiagnosticCode,
    string? CanaryName,
    int Attempts,
    long ElapsedMilliseconds,
    string NextAction,
    string? ReasonCode = null,
    string? Summary = null)
{
    public const string DocsUrl = "https://github.com/forge-trust/AppSurface/blob/main/Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-canary-poll";

    public static CanaryPollResult Pass(string name, int attempts, TimeSpan elapsed, string? reasonCode, string? summary) =>
        new("pass", 0, "", name, attempts, ToMilliseconds(elapsed), "Continue the deployment decision.", reasonCode, summary);

    public static CanaryPollResult Invalid(string diagnosticCode, string message) =>
        new(diagnosticCode == "ASCAN402" ? "credential-source" : "invalid-input", 2, diagnosticCode, null, 0, 0, message);

    public static CanaryPollResult SemanticFailure(string status, string name, int attempts, TimeSpan elapsed, string? reasonCode, string? summary) =>
        new(status, 3, "ASCAN403", name, attempts, ToMilliseconds(elapsed), NextActionForStatus(status), reasonCode, summary);

    public static CanaryPollResult ProtocolFailure(int attempts, TimeSpan elapsed) =>
        new("remote-protocol", 4, "ASCAN404", null, attempts, ToMilliseconds(elapsed), "Verify the protected canary route, host authorization, and response compatibility core.");

    public static CanaryPollResult TransientExhausted(int attempts, TimeSpan elapsed) =>
        new("transient-exhausted", 5, "ASCAN405", null, attempts, ToMilliseconds(elapsed), "Inspect the target's availability and retry the deployment step after the transient condition clears.");

    public static CanaryPollResult Deadline(int attempts, TimeSpan elapsed) =>
        new("deadline-exhausted", 6, "ASCAN406", null, attempts, ToMilliseconds(elapsed), "Increase --timeout only when the proof workflow is expected to take longer, then rerun.");

    public static CanaryPollResult Cancelled(int attempts = 0, TimeSpan? elapsed = null) =>
        new("cancelled", 130, "ASCAN408", null, attempts, ToMilliseconds(elapsed ?? TimeSpan.Zero), "The caller cancelled polling; rerun when the deployment operation should continue.");

    /// <summary>Gets whether a retry can produce a different deployment decision without changing local input.</summary>
    public bool IsRetryable => Outcome is "transient-exhausted" or "deadline-exhausted";

    private static long ToMilliseconds(TimeSpan elapsed) => Math.Max(0, (long)Math.Ceiling(elapsed.TotalMilliseconds));

    private static string NextActionForStatus(string status) => status switch
    {
        "stale" => "Refresh the proof or correct the marker and freshness boundary before retrying.",
        "not-configured" => "Configure the host-owned proof dependency or deliberately skip this workflow outside AppSurface.",
        _ => "Inspect the bounded canary reason and correct the application-owned proof before retrying.",
    };
}

/// <summary>Renders one safe terminal result for a person or an automation client.</summary>
internal static class CanaryPollResultRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes exactly one terminal result to stdout.</summary>
    public static Task WriteAsync(IConsole console, CanaryPollResult result, bool json)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(result);
        if (json)
        {
            return console.Output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                result.Outcome,
                result.CanaryName,
                result.Attempts,
                result.ElapsedMilliseconds,
                result.DiagnosticCode,
                Retryable = result.IsRetryable,
                result.ReasonCode,
                result.Summary,
                result.NextAction,
                DocsUrl = CanaryPollResult.DocsUrl,
            }, JsonOptions));
        }

        var status = result.Outcome == "pass" ? "PASS" : result.DiagnosticCode;
        var outcome = result.Outcome == "pass" ? string.Empty : $" outcome={result.Outcome}";
        var canary = result.CanaryName is null ? string.Empty : $" canary={result.CanaryName}";
        var reason = result.ReasonCode is null ? string.Empty : $" reason={result.ReasonCode}";
        var summary = result.Summary is null ? string.Empty : $" summary={result.Summary}";
        var firstLine = $"{status}{outcome}{canary} attempts={result.Attempts} elapsed={result.ElapsedMilliseconds}ms{reason}{summary}";
        return WriteTextAsync(console, firstLine, result);
    }

    private static async Task WriteTextAsync(IConsole console, string firstLine, CanaryPollResult result)
    {
        await console.Output.WriteLineAsync(firstLine);
        if (result.Outcome != "pass")
        {
            await console.Output.WriteLineAsync($"Next: {result.NextAction}");
            await console.Output.WriteLineAsync($"Docs: {CanaryPollResult.DocsUrl}");
        }
    }
}

/// <summary>Appends a bounded, safe result table to a GitHub Actions step summary.</summary>
internal static class CanaryPollGithubSummaryWriter
{
    private const int MaximumSummaryBytes = 8192;

    /// <summary>Attempts summary output without changing the polling result.</summary>
    public static async Task<bool> TryWriteAsync(string? path, CanaryPollResult result)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var markdown = $"| Canary | Outcome | Attempts | Elapsed | Diagnostic |{Environment.NewLine}"
                + $"| --- | --- | ---: | ---: | --- |{Environment.NewLine}"
                + $"| {Escape(result.CanaryName ?? "-")} | {Escape(result.Outcome)} | {result.Attempts} | {result.ElapsedMilliseconds} ms | {Escape(result.DiagnosticCode)} |{Environment.NewLine}";
            if (Encoding.UTF8.GetByteCount(markdown) > MaximumSummaryBytes)
            {
                return false;
            }

            await File.AppendAllTextAsync(path, markdown, new UTF8Encoding(false));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
