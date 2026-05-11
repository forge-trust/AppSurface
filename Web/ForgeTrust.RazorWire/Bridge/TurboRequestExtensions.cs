using Microsoft.AspNetCore.Http;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Provides extension methods for <see cref="HttpRequest"/> to detect Turbo requests.
/// </summary>
public static class TurboRequestExtensions
{
    /// <summary>
    /// Determines whether the request's Accept header signals a Turbo Stream response.
    /// </summary>
    /// <param name="request">The <see cref="HttpRequest"/> to check.</param>
    /// <returns><c>true</c> if the Accept header contains "text/vnd.turbo-stream.html", otherwise <c>false</c>.</returns>
    public static bool IsTurboRequest(this HttpRequest request)
    {
        return request.Headers["Accept"]
            .ToString()
            .Contains("text/vnd.turbo-stream.html", StringComparison.OrdinalIgnoreCase);
    }
}
