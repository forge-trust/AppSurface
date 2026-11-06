using System.Net;
using System.Text.Json;

namespace ForgeTrust.AppSurface.Durable.CloudTasks;

/// <summary>Reports only whether metadata token acquisition failed because data was invalid or unavailable.</summary>
/// <remarks>No provider response, exception, token, URI, or configuration value is retained.</remarks>
internal sealed class TokenAcquisitionException : Exception
{
    /// <summary>Initializes a safe acquisition failure with its closed invalid/unavailable classification.</summary>
    internal TokenAcquisitionException(bool invalidToken)
        : base(invalidToken ? "The metadata token response was invalid." : "The metadata token source was unavailable.")
    {
        InvalidToken = invalidToken;
    }

    /// <summary>Gets whether the response contained invalid token fields rather than an unavailable response.</summary>
    internal bool InvalidToken { get; }
}

/// <summary>Obtains an OAuth access token from the documented Cloud Run metadata endpoint only.</summary>
/// <remarks>
/// The metadata identity authorizes task creation; it is distinct from the OIDC service account that Cloud Tasks uses
/// when dispatching to the worker. This source does not cache tokens, use credential files, or select a fallback.
/// </remarks>
internal sealed class CloudRunMetadataAccessTokenSource : ICloudTasksAccessTokenSource, IDisposable
{
    private const string MetadataTokenUri = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token";
    private const int MaximumJsonBytes = 16 * 1024;
    private static readonly TimeSpan LifetimeSafetyMargin = TimeSpan.FromSeconds(5);

    private readonly CloudRunMetadataAccessTokenOptions _options;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    /// <summary>Creates the private fixed-endpoint HTTP transport without performing network or credential access.</summary>
    /// <param name="options">Validated metadata timeout options.</param>
    internal CloudRunMetadataAccessTokenSource(CloudRunMetadataAccessTokenOptions options)
        : this(options, CreateHttpClient(), TimeProvider.System)
    {
    }

    /// <summary>Creates a source with an owned HTTP client and a deterministic time provider for local tests.</summary>
    /// <param name="options">Validated metadata timeout options.</param>
    /// <param name="httpClient">An owned test client with a fixture handler.</param>
    /// <param name="timeProvider">The monotonic provider used to account for request and response-read time.</param>
    internal CloudRunMetadataAccessTokenSource(
        CloudRunMetadataAccessTokenOptions options,
        HttpClient httpClient,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>Obtains a token using the configured standalone metadata request timeout.</summary>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>A validated bare OAuth access token.</returns>
    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var budget = new SubmissionBudget(_options.RequestTimeout, _timeProvider);
        return GetAccessTokenAsync(budget, cancellationToken);
    }

    /// <summary>Obtains a token using the lesser of the source timeout and the invocation's remaining budget.</summary>
    /// <param name="budget">The invocation-local monotonic submission budget.</param>
    /// <param name="cancellationToken">Caller or overall submission cancellation.</param>
    /// <returns>A validated bare OAuth access token.</returns>
    internal async ValueTask<string> GetAccessTokenAsync(SubmissionBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = budget.Remaining;
        if (remaining <= TimeSpan.Zero)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var timeout = remaining < _options.RequestTimeout ? remaining : _options.RequestTimeout;
        var started = budget.TimeProvider.GetTimestamp();
        using var timeoutSource = new CancellationTokenSource(timeout, budget.TimeProvider);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MetadataTokenUri);
            request.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedSource.Token).ConfigureAwait(false);

            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                throw new TokenAcquisitionException(invalidToken: false);
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is < 0 or > MaximumJsonBytes)
            {
                throw new TokenAcquisitionException(invalidToken: false);
            }

            var body = await ReadBoundedBodyAsync(response, linkedSource.Token).ConfigureAwait(false);
            return ValidateResponse(body, budget, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Metadata token acquisition was canceled by the caller.", cancellationToken);
        }
        catch (OperationCanceledException) when (budget.IsExpired)
        {
            throw new OperationCanceledException("Metadata token acquisition exceeded the submission budget.", cancellationToken);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            throw new TokenAcquisitionException(invalidToken: false);
        }
        catch (TokenAcquisitionException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new TokenAcquisitionException(invalidToken: false);
        }
        catch (IOException)
        {
            throw new TokenAcquisitionException(invalidToken: false);
        }
        catch (JsonException)
        {
            throw new TokenAcquisitionException(invalidToken: false);
        }
    }

    /// <summary>Disposes the owned metadata HTTP client and its private handler.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream(capacity: Math.Min(MaximumJsonBytes + 1, (int)(response.Content.Headers.ContentLength ?? 0)));
        var buffer = new byte[2_048];
        while (true)
        {
            var requested = (int)Math.Min(buffer.Length, MaximumJsonBytes + 1L - body.Length);
            var count = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return body.ToArray();
            }

            body.Write(buffer, 0, count);
            if (body.Length > MaximumJsonBytes)
            {
                throw new TokenAcquisitionException(invalidToken: false);
            }
        }
    }

    private static string ValidateResponse(byte[] body, SubmissionBudget budget, long started)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new TokenAcquisitionException(invalidToken: false);
        }

        JsonElement accessToken = default;
        JsonElement tokenType = default;
        JsonElement expiresIn = default;
        var sawAccessToken = false;
        var sawTokenType = false;
        var sawExpiresIn = false;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "access_token":
                    if (sawAccessToken) throw new TokenAcquisitionException(invalidToken: true);
                    accessToken = property.Value;
                    sawAccessToken = true;
                    break;
                case "token_type":
                    if (sawTokenType) throw new TokenAcquisitionException(invalidToken: true);
                    tokenType = property.Value;
                    sawTokenType = true;
                    break;
                case "expires_in":
                    if (sawExpiresIn) throw new TokenAcquisitionException(invalidToken: true);
                    expiresIn = property.Value;
                    sawExpiresIn = true;
                    break;
            }
        }

        if (!sawAccessToken || !sawTokenType || !sawExpiresIn
            || accessToken.ValueKind != JsonValueKind.String
            || tokenType.ValueKind != JsonValueKind.String
            || !string.Equals(tokenType.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
            || expiresIn.ValueKind != JsonValueKind.Number
            || !expiresIn.TryGetInt32(out var expiresSeconds)
            || expiresSeconds is < 1 or > int.MaxValue)
        {
            throw new TokenAcquisitionException(invalidToken: true);
        }

        var token = accessToken.GetString();
        if (!CloudTasksValidation.IsValidToken(token))
        {
            throw new TokenAcquisitionException(invalidToken: true);
        }

        var elapsed = budget.TimeProvider.GetElapsedTime(started);
        var lifetime = TimeSpan.FromSeconds(expiresSeconds) - elapsed;
        var requiredLifetime = budget.Remaining + LifetimeSafetyMargin;
        if (lifetime <= requiredLifetime)
        {
            throw new TokenAcquisitionException(invalidToken: true);
        }

        return token!;
    }
}
