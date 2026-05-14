using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

/// <summary>
/// Provides route-navigation helpers for RazorDocs Playwright tests that need to run against multiple route shapes.
/// </summary>
internal static class RazorDocsRouteHelper
{
    /// <summary>
    /// Navigates to the first RazorDocs route candidate that returns a successful HTTP response.
    /// </summary>
    /// <param name="page">The Playwright page that will be navigated for each candidate.</param>
    /// <param name="docsUrl">The absolute RazorDocs host URL prefix to combine with each relative path.</param>
    /// <param name="relativePaths">
    /// Ordered root-relative path candidates. Candidates are tried in the supplied order, and callers should pass at
    /// least one value because an empty list always fails.
    /// </param>
    /// <returns>A task that completes after <paramref name="page"/> has navigated to the first candidate whose response status is less than 400.</returns>
    /// <remarks>
    /// This helper is intended for tests that need to tolerate canonical and legacy RazorDocs route shapes across host
    /// environments. Each failed response or navigation exception is recorded in the final diagnostic message. A failed
    /// candidate may still leave the page at its attempted URL before the next candidate is tried.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="relativePaths"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="relativePaths"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no candidate returns a non-null response with status less than 400.</exception>
    public static async Task GotoFirstAvailableAsync(IPage page, string docsUrl, params string[] relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        if (relativePaths.Length == 0)
        {
            throw new ArgumentException("At least one RazorDocs route candidate is required.", nameof(relativePaths));
        }

        var attempts = new List<string>(relativePaths.Length);

        foreach (var relativePath in relativePaths)
        {
            try
            {
                var response = await page.GotoAsync($"{docsUrl}{relativePath}");
                attempts.Add($"{relativePath} => {response?.Status.ToString() ?? "no response"}");

                if (response is not null && response.Status < 400)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                attempts.Add($"{relativePath} => navigation error ({ex.GetType().Name}: {ex.Message})");
            }
        }

        throw new InvalidOperationException(
            $"None of the RazorDocs test routes resolved successfully. Attempts: {string.Join(", ", attempts)}.");
    }
}
