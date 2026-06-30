namespace ForgeTrust.AppSurface.Workers;

/// <summary>
/// Thrown when durable worker diagnostics or metadata contain values that are unsafe to persist or publish.
/// </summary>
/// <remarks>
/// AppSurface worker metadata is designed for logs, projections, and repair reports. It must use stable identifiers,
/// reason codes, and sanitized values rather than raw source payloads, credentials, OAuth tokens, provider URLs, model
/// output, prompts, message bodies, attachments, or unclassified sensitive text.
/// </remarks>
public sealed class DurableWorkerUnsafeMetadataException : ArgumentException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DurableWorkerUnsafeMetadataException"/> class.
    /// </summary>
    /// <param name="message">Human-readable validation failure message.</param>
    /// <param name="paramName">Name of the parameter that carried unsafe metadata.</param>
    public DurableWorkerUnsafeMetadataException(string message, string? paramName = null)
        : base(message, paramName)
    {
    }
}
