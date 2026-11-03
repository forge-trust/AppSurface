using System.Text;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Creates and checks the private harness synchronization frame for N13/N14.</summary>
/// <remarks>The line is emitted only after the actual authenticated accepted-work reply commits. It grants no authority.</remarks>
internal static class LinuxOwnerDeathPhaseFrame
{
    /// <summary>Returns the one fixed case-bound ASCII line, or null in N16 and ordinary images.</summary>
    internal static byte[]? Create()
    {
        var line = EvidenceNativeQualification.OwnerDeathAcceptedWorkPhaseLine;
        return line is null ? null : Encoding.ASCII.GetBytes(line + "\n");
    }

    /// <summary>Checks exact bytes against the compile-owned case label; this never authenticates a peer.</summary>
    internal static bool IsValid(ReadOnlySpan<byte> frame) =>
        EvidenceNativeQualification.IsOwnerDeathAcceptedWorkPhaseFrame(frame);
}
