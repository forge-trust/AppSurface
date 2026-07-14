namespace ForgeTrust.AppSurface.Web;

/// <summary>
/// Configures opt-in Web Push event handling in the shared AppSurface service worker.
/// </summary>
/// <remarks>
/// Enabling push maps worker plumbing and the explicit registration helper. It does not request notification
/// permission, create a push subscription, store subscriber identity, or send notifications.
/// </remarks>
public sealed class PwaPushOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the generated worker should support push events.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets an optional app-root-relative classic worker script that owns push and notification-click events.
    /// </summary>
    /// <remarks>
    /// Leave this value <see langword="null"/> to use AppSurface's strict version-1 notification adapter. When set,
    /// AppSurface imports the script with <c>importScripts()</c> and does not emit its default push or click listeners.
    /// AppSurface contains a load or top-level evaluation failure with <c>ASPWAJS030</c> so shared lifecycle and
    /// offline behavior remain available. The application should still deploy this asset atomically. Browser script
    /// evaluation is not transactional, so a handler must finish validation and initialization before registering
    /// listeners that would otherwise survive a later top-level exception.
    /// </remarks>
    public string? HandlerScriptPath { get; set; }
}
