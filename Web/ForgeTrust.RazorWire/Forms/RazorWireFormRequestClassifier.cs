using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace ForgeTrust.RazorWire.Forms;

/// <summary>
/// Detects whether an incoming request originated from a RazorWire-enhanced form.
/// </summary>
internal sealed class RazorWireFormRequestClassifier
{
    /// <summary>
    /// Maximum URL-encoded form body size the classifier will read when the durable request header is absent.
    /// </summary>
    private const long MaxFallbackFormBytes = 64 * 1024;

    private readonly ILogger<RazorWireFormRequestClassifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RazorWireFormRequestClassifier"/> class.
    /// </summary>
    /// <param name="logger">Logger used when fallback form parsing fails.</param>
    public RazorWireFormRequestClassifier(ILogger<RazorWireFormRequestClassifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Determines whether a request carries RazorWire form markers.
    /// </summary>
    /// <param name="request">The HTTP request to inspect.</param>
    /// <param name="cancellationToken">Token used when fallback URL-encoded form parsing is required.</param>
    /// <returns>
    /// <c>true</c> when the request has the RazorWire form header, an already-parsed marker field, or a safely readable
    /// URL-encoded marker field; otherwise <c>false</c>.
    /// </returns>
    public async ValueTask<bool> IsRazorWireFormRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (IsTruthy(request.Headers[RazorWireFormHeaders.FormRequest]))
        {
            return true;
        }

        if (!request.HasFormContentType)
        {
            return false;
        }

        var contentType = request.ContentType ?? string.Empty;
        if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var existingForm = request.HttpContext.Features.Get<IFormFeature>()?.Form;
        if (existingForm is not null)
        {
            return IsTruthy(existingForm[RazorWireFormFields.FormMarker]);
        }

        if (!contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.ContentLength is null or > MaxFallbackFormBytes)
        {
            return false;
        }

        try
        {
            var form = await request.ReadFormAsync(cancellationToken);

            return IsTruthy(form[RazorWireFormFields.FormMarker]);
        }
        catch (Exception ex) when (ex is InvalidDataException
                                      or IOException
                                      or InvalidOperationException
                                      or BadHttpRequestException)
        {
            _logger.LogDebug(ex, "Failed to read RazorWire form marker while classifying a failed form request.");

            return false;
        }
    }

    private static bool IsTruthy(StringValues values)
    {
        foreach (var value in values)
        {
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
