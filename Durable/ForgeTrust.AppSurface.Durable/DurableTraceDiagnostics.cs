using System.Diagnostics;

namespace ForgeTrust.AppSurface.Durable;

/// <summary>Emits only the value-free diagnostics defined by the durable trace contract.</summary>
internal static class DurableTraceDiagnostics
{
    internal static void Report(string? diagnosticCode)
    {
        if (diagnosticCode is DurableProblemCodes.TraceContextInvalid or DurableProblemCodes.TraceStateRejected)
        {
            Trace.TraceWarning(diagnosticCode);
        }
    }
}
