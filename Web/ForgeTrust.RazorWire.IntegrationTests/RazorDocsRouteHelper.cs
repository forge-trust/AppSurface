using Microsoft.Playwright;

namespace ForgeTrust.RazorWire.IntegrationTests;

internal static class RazorDocsRouteHelper
{
    public static async Task GotoFirstAvailableAsync(IPage page, string docsUrl, params string[] relativePaths)
    {
        var attempts = new List<string>(relativePaths.Length);

        foreach (var relativePath in relativePaths)
        {
            var response = await page.GotoAsync($"{docsUrl}{relativePath}");
            attempts.Add($"{relativePath} => {response?.Status.ToString() ?? "no response"}");

            if (response is not null && response.Status < 400)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"None of the RazorDocs test routes resolved successfully. Attempts: {string.Join(", ", attempts)}.");
    }
}
