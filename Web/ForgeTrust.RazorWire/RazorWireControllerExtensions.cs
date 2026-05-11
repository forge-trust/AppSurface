using ForgeTrust.RazorWire.Bridge;
using Microsoft.AspNetCore.Mvc;

namespace ForgeTrust.RazorWire;

/// <summary>
/// Provides extension methods for MVC controllers to interact with RazorWire.
/// </summary>
public static class RazorWireControllerExtensions
{
    /// <summary>
    /// Creates a <see cref="RazorWireStreamBuilder"/> bound to the specified controller.
    /// </summary>
    /// <param name="controller">The controller instance used to initialize the builder.</param>
    /// <returns>A <see cref="RazorWireStreamBuilder"/> configured to operate with the given controller.</returns>
    public static RazorWireStreamBuilder RazorWireStream(this Controller controller)
    {
        return new RazorWireStreamBuilder(controller);
    }
}
