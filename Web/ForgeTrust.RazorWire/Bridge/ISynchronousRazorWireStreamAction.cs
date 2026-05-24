using Microsoft.AspNetCore.Mvc.Rendering;

namespace ForgeTrust.RazorWire.Bridge;

/// <summary>
/// Represents a RazorWire stream action that can render without view or component services.
/// </summary>
/// <remarks>
/// <see cref="RazorWireStreamBuilder.Build"/> uses this internal contract for stream actions whose markup is already
/// fully known. Actions that need Razor view rendering should implement only <see cref="IRazorWireStreamAction"/> so
/// callers are directed to <see cref="RazorWireStreamBuilder.RenderAsync(ViewContext, CancellationToken)"/> or
/// <see cref="RazorWireStreamBuilder.BuildResult(int?)"/>.
/// </remarks>
internal interface ISynchronousRazorWireStreamAction : IRazorWireStreamAction
{
    /// <summary>
    /// Renders this action into a Turbo Stream fragment without using MVC rendering services.
    /// </summary>
    /// <returns>The complete Turbo Stream fragment for the action.</returns>
    string Render();
}
