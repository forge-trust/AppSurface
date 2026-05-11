using Microsoft.JSInterop;

namespace ForgeTrust.RazorWire;

// This class provides an example of how JavaScript functionality can be wrapped
// in a .NET class for easy consumption. The associated JavaScript module is
// loaded on demand when first needed.
//
// This class can be registered as scoped DI service and then injected into Blazor
// components for use.

/// <summary>
/// Provides an example of how JavaScript functionality can be wrapped in a .NET class for easy consumption.
/// </summary>
/// <param name="jsRuntime">The JS runtime used to invoke JavaScript functions.</param>
public class ExampleJsInterop(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> moduleTask = new(() => jsRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            "./_content/ForgeTrust.RazorWire/exampleJsInterop.js")
        .AsTask());

    /// <summary>
    /// Shows a browser prompt dialog with the specified message and returns the user's input as a string, or <c>null</c> if the dialog was dismissed.
    /// </summary>
    /// <param name="message">The message to display in the prompt dialog.</param>
    /// <returns>The user's input as a string, or <c>null</c> if the dialog was dismissed.</returns>
    public async ValueTask<string?> Prompt(string message)
    {
        var module = await moduleTask.Value;

        return await module.InvokeAsync<string?>("showPrompt", message);
    }

    /// <summary>
    /// Disposes the loaded JavaScript module and releases associated JS resources.
    /// </summary>
    /// <remarks>
    /// If the module was never loaded, this method completes without action.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (moduleTask.IsValueCreated)
        {
            var module = await moduleTask.Value;
            await module.DisposeAsync();
        }
    }
}
