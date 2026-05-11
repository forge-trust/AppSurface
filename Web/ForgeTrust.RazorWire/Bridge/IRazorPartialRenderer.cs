namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Provides a mechanism to render Razor partial views to strings, specifically useful for background services and non-HTTP request contexts.
/// </summary>
public interface IRazorPartialRenderer
{
    /// <summary>
    /// Renders a partial view as an HTML string.
    /// </summary>
    /// <param name="viewName">The name or path of the partial view to render.</param>
    /// <param name="model">Optional model to pass to the view.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous render operation. The task result contains the rendered HTML string.</returns>
    Task<string> RenderPartialToStringAsync(
        string viewName,
        object? model = null,
        CancellationToken cancellationToken = default);
}
