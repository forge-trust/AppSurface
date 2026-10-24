using System.ComponentModel;
using ForgeTrust.AppSurface.Evidence.Contracts;

namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Defines the existing exception families handled by the native supervision procedures.</summary>
/// <remarks>This class only classifies an exception object. Each native caller retains its original catch
/// body, failure latch, quarantine, cleanup, cancellation and fatal propagation. Classification grants no
/// owner, process, handle, admission or authority and adds no retry or replacement exception.</remarks>
internal static class SupervisionExceptionPolicy
{
    /// <summary>Preserves the broad original filter that excludes exactly the three fatal runtime classes.</summary>
    internal static bool IsRecoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    /// <summary>Preserves the producer workspace's narrower original recoverable exception family.</summary>
    /// <remarks>In particular, cancellation and Win32Exception do not enter this translation path.</remarks>
    internal static bool IsProducerWorkspaceRecoverable(Exception error) =>
        error is IOException or UnauthorizedAccessException or PlatformNotSupportedException
            or ArgumentException or EvidenceAdmissionException or ObjectDisposedException
            or InvalidOperationException or OverflowException;

    /// <summary>Preserves the application workspace's original family, including its native Win32 failures.</summary>
    /// <remarks>This separately named policy prevents broadening the producer workspace's translation.</remarks>
    internal static bool IsApplicationWorkspaceRecoverable(Exception error) =>
        error is IOException or UnauthorizedAccessException or PlatformNotSupportedException
            or ArgumentException or EvidenceAdmissionException or ObjectDisposedException
            or InvalidOperationException or OverflowException or Win32Exception;
}
