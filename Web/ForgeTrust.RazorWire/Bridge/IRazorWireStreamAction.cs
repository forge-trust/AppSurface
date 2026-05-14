using Microsoft.AspNetCore.Mvc.Rendering;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Represents an action to be performed in a RazorWire stream (e.g., append, replace).
/// </summary>
public interface IRazorWireStreamAction
{
    /// <summary>
    /// Renders the stream action to an HTML string using the provided view rendering context.
    /// </summary>
    /// <param name="viewContext">The Razor view rendering context used to produce the HTML output.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The rendered HTML for this stream action.</returns>
    Task<string> RenderAsync(ViewContext viewContext, CancellationToken cancellationToken = default);
}
