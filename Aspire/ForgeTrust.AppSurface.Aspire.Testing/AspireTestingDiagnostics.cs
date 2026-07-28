using System.Diagnostics;

namespace ForgeTrust.AppSurface.Aspire.Testing;

/// <summary>
/// Emits best-effort compatibility and cleanup warnings without changing primary build behavior.
/// </summary>
internal static class AspireTestingDiagnostics
{
    /// <summary>
    /// Writes a warning through the supplied sink and suppresses non-process-fatal diagnostic failures.
    /// </summary>
    /// <param name="warningSink">The optional warning destination.</param>
    /// <param name="message">The warning message.</param>
    internal static void TryWrite(Action<string>? warningSink, string message)
    {
        TryWrite(warningSink, () => message);
    }

    /// <summary>
    /// Creates and writes a warning inside the diagnostic boundary, suppressing non-process-fatal formatting and sink
    /// failures.
    /// </summary>
    /// <param name="warningSink">The optional warning destination.</param>
    /// <param name="messageFactory">Creates the warning message only when a destination is configured.</param>
    internal static void TryWrite(Action<string>? warningSink, Func<string> messageFactory)
    {
        ArgumentNullException.ThrowIfNull(messageFactory);
        if (warningSink is null)
        {
            return;
        }

        try
        {
            warningSink(messageFactory());
        }
        catch (Exception exception) when (!AspireExceptionUtilities.IsProcessFatal(exception))
        {
            // Non-fatal diagnostics are secondary evidence and must never replace build or cleanup behavior.
        }
    }

    /// <summary>
    /// Writes a warning to the process trace listeners.
    /// </summary>
    /// <param name="message">The warning message.</param>
    internal static void TraceWarning(string message) => Trace.TraceWarning(message);
}
