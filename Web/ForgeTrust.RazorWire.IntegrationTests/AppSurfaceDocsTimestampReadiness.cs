using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>Observes the contributor timestamp's existing client-formatting readiness contract.</summary>
/// <remarks>
/// Missing timestamps and failed formatting both fail the readiness predicate. On failure, bounded diagnostic
/// booleans distinguish missing server data from missing runtime initialization without recording page content,
/// URLs, datetime values or arbitrary browser errors. Diagnostics never turn a failed wait into success.
/// </remarks>
internal static class AppSurfaceDocsTimestampReadiness
{
    /// <summary>Waits for nonempty relative timestamp text without the server's UTC fallback.</summary>
    /// <param name="page">The page containing the contributor provenance strip.</param>
    /// <param name="timeoutMilliseconds">
    /// The positive readiness allowance; zero is rejected because Playwright interprets it as unbounded.
    /// The real-page regression retains its original 60-second default. Synthetic
    /// negative probes supply a short allowance to verify failure reporting without waiting a full minute.
    /// </param>
    /// <returns>A task that completes only when the original readiness predicate succeeds.</returns>
    /// <exception cref="TimeoutException">The original wait failed; a bounded readiness snapshot is included.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The readiness allowance is not positive.</exception>
    internal static async Task WaitAsync(IPage page, int timeoutMilliseconds = 60_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        try
        {
            await page.WaitForFunctionAsync(
                "() => { const time = document.querySelector('.docs-provenance-strip time'); return Boolean(time?.textContent) && !time.textContent.includes('UTC'); }",
                null,
                new PageWaitForFunctionOptions { Timeout = timeoutMilliseconds });
        }
        catch (TimeoutException exception)
        {
            var snapshot = "unavailable";
            try
            {
                snapshot = await page.EvaluateAsync<string>(
                    """
                    () => {
                        const times = document.querySelectorAll('.docs-provenance-strip time');
                        const time = times[0];
                        const datetime = time?.getAttribute('datetime');
                        const formatter = window.RazorWire?.localTimeFormatter;
                        return JSON.stringify({
                            TimeCount: Math.min(times.length, 100),
                            HasText: Boolean(time?.textContent),
                            HasUtcFallback: time?.textContent?.includes('UTC') === true,
                            HasTimeMarker: time?.hasAttribute('data-rw-time') === true,
                            IsRelative: time?.getAttribute('data-rw-time-display') === 'relative',
                            HasValidDatetime: Boolean(datetime) && !Number.isNaN(new Date(datetime).getTime()),
                            RuntimeInitialized: window.RazorWireInitialized === true,
                            HasFormatter: Boolean(formatter),
                            FormatterStarted: formatter?.isStarted === true
                        });
                    }
                    """).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Failure evidence must not replace or suppress the original readiness failure.
            }

            throw new TimeoutException($"Contributor timestamp readiness failed. State: {snapshot}", exception);
        }
    }
}
