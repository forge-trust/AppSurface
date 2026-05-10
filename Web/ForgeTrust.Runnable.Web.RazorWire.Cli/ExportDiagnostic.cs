namespace ForgeTrust.Runnable.Web.RazorWire.Cli;

/// <summary>
/// Describes one CDN export validation problem with a stable code and actionable context.
/// </summary>
/// <remarks>
/// Diagnostics are immutable value objects suitable for command-line output, logs, and structured inspection through
/// <see cref="ExportValidationException.Diagnostics"/>. The public contract exposes the stable code, plain-text message, and route
/// context. Internal exporter code may attach the discovered reference that produced the diagnostic for de-duplication and richer
/// validation decisions.
/// </remarks>
public sealed record ExportDiagnostic
{
    /// <summary>
    /// Initializes a new instance of <see cref="ExportDiagnostic"/>.
    /// </summary>
    /// <param name="code">
    /// Short machine-readable diagnostic identifier. Codes use uppercase ASCII letters and digits, such as <c>RWEXPORT003</c>, and
    /// should remain concise enough for CLI display.
    /// </param>
    /// <param name="message">
    /// Human-readable plain-text description of the validation problem. The message must not contain markup and should be concise
    /// enough to show directly in terminal output.
    /// </param>
    /// <param name="route">
    /// Root-relative route or export context where the problem was found, such as <c>/docs/start</c>. Use <c>/</c> for root context.
    /// </param>
    public ExportDiagnostic(string code, string message, string route)
        : this(code, message, route, reference: null)
    {
    }

    internal ExportDiagnostic(string code, string message, string route, ExportReference? reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);

        Code = code;
        Message = message;
        Route = route;
        Reference = reference;
    }

    /// <summary>
    /// Gets the short machine-readable diagnostic identifier.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the human-readable plain-text validation message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the root-relative route or export context where the diagnostic was produced.
    /// </summary>
    public string Route { get; }

    /// <summary>
    /// Gets the optional exporter-managed reference that produced the diagnostic.
    /// </summary>
    /// <remarks>
    /// This value is internal because it is tied to crawl provenance rather than the public validation exception contract. It is
    /// <see langword="null"/> for diagnostics that describe route-level failures instead of a specific markup or CSS reference.
    /// </remarks>
    internal ExportReference? Reference { get; }
}
