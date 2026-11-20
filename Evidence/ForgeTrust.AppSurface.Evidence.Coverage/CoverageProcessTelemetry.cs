using System.ComponentModel;
using System.Diagnostics;

namespace ForgeTrust.AppSurface.Evidence.Coverage;

/// <summary>Best-effort process memory observation for repository qualification; never an acceptance oracle.</summary>
/// <remarks>Sample while a process is alive. Reaped, unassociated, unsupported or zero-valued observations
/// return null; they must not turn successful process execution into a coverage failure.</remarks>
internal static class CoverageProcessTelemetry
{
    internal static long? TryGetPeakWorkingSet(Process process)
    {
        try
        {
            process.Refresh();
            var value = process.PeakWorkingSet64;
            return value > 0 ? value : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
