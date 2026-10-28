#if EVIDENCE_PRIVATE_N04
namespace ForgeTrust.AppSurface.Evidence.Supervision;

/// <summary>Best-effort closed stage diagnostic for failures opening the private N04 owner.</summary>
/// <remarks>This emits no sampled values and is absent from ordinary and non-N04 qualification images.</remarks>
internal static class LinuxN04OwnerActivationDiagnostic
{
    /// <summary>Original owner activation stages; these labels do not alter or split the guarded operations.</summary>
    internal enum Stage
    {
        /// <summary>The original launch-input recheck was about to run.</summary>
        InputRecheck,
        /// <summary>The original selected systemd owner-property read was about to run.</summary>
        BackendRead,
        /// <summary>The original owner-unit name parse was about to run.</summary>
        UnitParse,
        /// <summary>The original complete owner-facts and deadline check was about to run.</summary>
        FactsRequire,
        /// <summary>The original root process identity capture was about to run.</summary>
        IdentityCapture,
        /// <summary>The original single-use owner-guard consumption was about to run.</summary>
        GuardConsume,
        /// <summary>The constructed owner's original active identity and guard recheck was about to run.</summary>
        ActiveRecheck,
    }

    private const string Schema = "issue779-n04-owner-activation-diagnostic-v1";

    /// <summary>Formats exactly one bounded JSON line from the closed stage vocabulary.</summary>
    internal static string FormatLine(Stage stage)
    {
        var name = stage switch
        {
            Stage.InputRecheck => "input-recheck",
            Stage.BackendRead => "backend-read",
            Stage.UnitParse => "unit-parse",
            Stage.FactsRequire => "facts-require",
            Stage.IdentityCapture => "identity-capture",
            Stage.GuardConsume => "guard-consume",
            Stage.ActiveRecheck => "active-recheck",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        return $"{{\"schema\":\"{Schema}\",\"stage\":\"{name}\",\"native_acceptance\":false}}\n";
    }

    /// <summary>Attempts the fixed diagnostic write while swallowing any diagnostic-only failure.</summary>
    internal static void WriteBestEffort(TextWriter stderr, Stage stage)
    {
        try
        {
            stderr.Write(FormatLine(stage));
            stderr.Flush();
        }
        catch { }
    }
}
#endif
