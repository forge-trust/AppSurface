namespace ForgeTrust.AppSurface.Evidence.Readiness;

/// <summary>Waits for an explicit status-only HTTP condition under one cooperative monotonic deadline.</summary>
public static class HttpReadinessProbe
{
    /// <summary>Sends headers-only GET requests until the status condition succeeds or a bound is reached.</summary>
    /// <param name="client">Borrowed client; its configuration and lifetime remain caller-owned.</param>
    /// <param name="endpoint">Absolute HTTP(S) base endpoint.</param>
    /// <param name="probePath">Nonempty relative or root-relative path; it must resolve to the same origin.</param>
    /// <param name="timeout">Finite positive total budget, including retries and delays.</param>
    /// <param name="options">Optional snapshotted status, timer, and attempt policy.</param>
    /// <param name="cancellationToken">Caller cancellation; wins over simultaneous deadline expiry.</param>
    /// <returns>Completion only after the declared condition passes inside the original budget.</returns>
    /// <remarks>Never reads response bodies, mutates the client, or logs raw exceptions. Redirect behavior belongs
    /// to the client; initial-origin validation is not a redirect sandbox. Non-cooperative callbacks can outlast the budget.</remarks>
    public static Task WaitAsync(HttpClient client, Uri endpoint, string probePath, TimeSpan timeout,
        HttpReadinessOptions? options = null, CancellationToken cancellationToken = default) =>
        WaitAsync(client, endpoint, probePath, timeout, options, cancellationToken, TimeProvider.System);

    /// <summary>Clock seam for deterministic timer and cancellation verification.</summary>
    internal static async Task WaitAsync(HttpClient client, Uri endpoint, string probePath, TimeSpan timeout,
        HttpReadinessOptions? options, CancellationToken cancellationToken, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        _ = ResolveProbeUri(endpoint, probePath);
        var settings = HttpReadinessSettings.Snapshot(options ?? new());
        using var deadline = new ReadinessDeadline(timeout, timeProvider, cancellationToken);
        await WaitCoreAsync(client, endpoint, probePath, settings, deadline).ConfigureAwait(false);
    }

    /// <summary>Validates the path without requiring an allocated endpoint; adapters call before acquisition.</summary>
    internal static void ValidateProbePath(string probePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(probePath);
        if (probePath.Contains('\\') || probePath.Contains('#') || probePath.StartsWith("//", StringComparison.Ordinal)
            || !Uri.TryCreate(probePath, UriKind.Relative, out _))
        {
            throw new ArgumentException("An unambiguous relative or root-relative HTTP path is required.", nameof(probePath));
        }
    }

    /// <summary>Resolves and checks HTTP(S) scheme, IDN host, and effective port before dispatch.</summary>
    internal static Uri ResolveProbeUri(Uri endpoint, string probePath)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateProbePath(probePath);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("An absolute HTTP(S) endpoint is required.", nameof(endpoint));
        }

        var resolved = new Uri(endpoint, probePath);
        if (!string.Equals(endpoint.Scheme, resolved.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(endpoint.IdnHost, resolved.IdnHost, StringComparison.OrdinalIgnoreCase)
            || endpoint.Port != resolved.Port)
        {
            throw new ArgumentException("The probe path must preserve the endpoint origin.", nameof(probePath));
        }

        return resolved;
    }

    /// <summary>Shared core used by adapters with their original deadline and validated option snapshot.</summary>
    internal static async Task WaitCoreAsync(HttpClient client, Uri endpoint, string probePath,
        HttpReadinessSettings settings, ReadinessDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(client);
        var uri = ResolveProbeUri(endpoint, probePath);
        var last = new ReadinessAttempt();
        while (true)
        {
            deadline.Check(ReadinessPhase.Http, last);
            var remaining = deadline.Remaining;
            var accepted = false;
            using (var requestTimer = new CancellationTokenSource(settings.RequestTimeout < remaining ? settings.RequestTimeout : remaining, deadline.TimeProvider))
            using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, requestTimer.Token))
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                HttpResponseMessage? response = null;
                deadline.Check(ReadinessPhase.Http, last);
                last = last with { Attempts = last.Attempts + 1 };
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException exception)
                {
                    last = new(last.Attempts, ReadinessFailureCategory.TransportFailure, HttpError: exception.HttpRequestError);
                    deadline.Check(ReadinessPhase.Http, last);
                }
                catch (OperationCanceledException exception)
                {
                    if (requestTimer.IsCancellationRequested || exception.InnerException is TimeoutException)
                    {
                        last = new(last.Attempts, ReadinessFailureCategory.RequestTimeout);
                    }

                    deadline.Check(ReadinessPhase.Http, last);
                    if (!requestTimer.IsCancellationRequested && exception.InnerException is not TimeoutException)
                    {
                        throw;
                    }
                }

                using (response)
                {
                    if (requestTimer.IsCancellationRequested)
                    {
                        last = new(last.Attempts, ReadinessFailureCategory.RequestTimeout);
                    }

                    deadline.Check(ReadinessPhase.Http, last);
                    if (response is not null && !requestTimer.IsCancellationRequested)
                    {
                        accepted = settings.IsSuccess(response.StatusCode);
                        if (!accepted)
                        {
                            last = new(last.Attempts, ReadinessFailureCategory.StatusRejected, response.StatusCode);
                        }
                        deadline.Check(ReadinessPhase.Http, last);
                    }
                }
            }

            deadline.Check(ReadinessPhase.Http, last);
            if (accepted)
            {
                return;
            }

            if (settings.MaxAttempts is int maximum && last.Attempts >= maximum)
            {
                throw new ReadinessException(ReadinessPhase.Http, ReadinessFailureCategory.AttemptLimit, last);
            }

            try
            {
                await Task.Delay(settings.RetryDelay < deadline.Remaining ? settings.RetryDelay : deadline.Remaining, deadline.TimeProvider, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                deadline.Check(ReadinessPhase.Http, last);
                throw;
            }
        }
    }
}
