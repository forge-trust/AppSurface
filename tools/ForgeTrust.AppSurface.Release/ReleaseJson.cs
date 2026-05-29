namespace ForgeTrust.AppSurface.Release;

/// <summary>
/// JSON serializer configuration for release artifacts.
/// </summary>
internal static class ReleaseJson
{
    /// <summary>
    /// Gets indented camel-case JSON options.
    /// </summary>
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
