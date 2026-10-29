namespace EvidenceHost.CompletedIsolation;

/// <summary>Fixed synthetic-control diagnostics immediately before the unchanged identity transition.</summary>
/// <remarks>No provider token exists in a control. Only kernel credential/filter fields are printed to stderr;
/// the shell execs the same fixed setpriv command and never changes its privileges or arguments.</remarks>
internal static class BootstrapCommand
{
    internal const string DiagnosticScript = "while IFS= read -r line; do case \"$line\" in "
        + "Uid:*|Gid:*|Groups:*|CapInh:*|CapPrm:*|CapEff:*|CapBnd:*|CapAmb:*|NoNewPrivs:*|Seccomp:*|Seccomp_filters:*) "
        + "printf '%s\\n' \"ISOLATION_BOOTSTRAP:$line\" >&2;; esac; done </proc/self/status; "
        + "if IFS= read -r label </proc/self/attr/current; then "
        + "if [ \"$label\" = unconfined ]; then printf 'ISOLATION_BOOTSTRAP_LSM:unconfined\\n' >&2; "
        + "else printf 'ISOLATION_BOOTSTRAP_LSM:confined\\n' >&2; fi; "
        + "else printf 'ISOLATION_BOOTSTRAP_LSM:unavailable\\n' >&2; fi; exec \"$@\"";

    /// <summary>Returns the fixed prefix for a synthetic probe, or none for credentialed/replay phases.</summary>
    internal static IReadOnlyList<string> Prefix(OwnerPhase phase) => phase == OwnerPhase.Control
        ? ["/usr/bin/bash", "-c", DiagnosticScript, "isolation-bootstrap"]
        : [];
}
