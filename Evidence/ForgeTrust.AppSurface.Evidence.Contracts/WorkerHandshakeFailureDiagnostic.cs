#if EVIDENCE_PRIVATE_C3_SDK || EVIDENCE_QUALIFICATION_DATA_TESTS
using System.Text.Json;

namespace ForgeTrust.AppSurface.Evidence.Contracts;

/// <summary>Closed failure families for the private C3 SDK worker handshake observer.</summary>
internal enum WorkerHandshakeFailureOutcome
{
    /// <summary>The initial authenticated admission handshake exceeded its original deadline.</summary>
    InitialAdmissionDeadline,
    /// <summary>The preparation or subsequent admission handshake exceeded its original allowance.</summary>
    PreparationOrAdmissionDeadline,
    /// <summary>An existing authentication or descriptor check rejected the handshake.</summary>
    AdmissionRejected,
    /// <summary>The worker response did not conform to the fixed handshake data shape.</summary>
    MalformedResponse,
    /// <summary>The existing socket transport failed before handshake completion.</summary>
    TransportFailure,
}

/// <summary>Best-effort private C3 SDK handshake diagnostics; no authentication or failure outcome is changed.</summary>
internal static class WorkerHandshakeFailureDiagnostic
{
    /// <summary>The maximum elapsed interval representable by the closed diagnostic row.</summary>
    private const int MaximumElapsedMilliseconds = 60 * 60 * 1000;

    /// <summary>Writes one closed row, ignoring diagnostic failures so the original handshake result is preserved.</summary>
    /// <param name="writer">Optional caller-owned diagnostic sink.</param>
    /// <param name="outcome">One closed handshake failure family.</param>
    /// <param name="started">The monotonic timestamp captured by the existing handshake.</param>
    internal static void TryWrite(TextWriter? writer, WorkerHandshakeFailureOutcome outcome, long started)
    {
        try
        {
            if (writer is null || !Enum.IsDefined(outcome)) return;
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromHours(1)) return;
            var elapsedMilliseconds = (long)elapsed.TotalMilliseconds;
            if (elapsedMilliseconds < 0 || elapsedMilliseconds > MaximumElapsedMilliseconds) return;
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "issue779-c3sdk-worker-handshake-failure-v1",
                outcome = outcome.ToString(),
                elapsed_ms = elapsedMilliseconds,
                native_acceptance = false,
            }));
        }
        catch (Exception)
        {
            // Optional diagnostics cannot replace the original handshake exception or normalization.
        }
    }
}
#endif
