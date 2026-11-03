namespace EvidenceHost.CompletedIsolation;

/// <summary>Fixed private runtime-directory setup before the unchanged phase identity transition.</summary>
/// <remarks>Every phase hides systemd runtime plumbing beneath a read-only empty mount before setpriv.
/// Synthetic controls additionally print only fixed kernel credential/filter fields to stderr.
/// The shell execs the same setpriv command and never changes its privileges or arguments.</remarks>
internal static class BootstrapCommand
{
    /// <summary>The existing five bootstrap capabilities; no capability is added to this bounding set.</summary>
    internal const string BoundingCapabilities = "CAP_SYS_ADMIN CAP_SETUID CAP_SETGID CAP_SETPCAP CAP_SYS_CHROOT";

    /// <summary>Preserves only the observed missing UID-transition capability across the fixed bootstrap execs.</summary>
    internal static void ConfigureCapabilities(System.Diagnostics.ProcessStartInfo start)
    {
        start.ArgumentList.Add("--property=CapabilityBoundingSet=" + BoundingCapabilities);
        start.ArgumentList.Add("--property=AmbientCapabilities=CAP_SETUID");
    }

    /// <summary>Starts only the private managed launcher without diagnostic sockets or debugger FIFOs.</summary>
    /// <remarks>The Python child constructs its own original four-variable environment.</remarks>
    internal static void AppendLauncherEnvironment(System.Diagnostics.ProcessStartInfo start)
    {
        foreach (var argument in new[] { "/usr/bin/env", "-i", "PATH=/usr/bin:/bin", "HOME=/nonexistent",
            "LANG=C.UTF-8", "LC_ALL=C", "DOTNET_EnableDiagnostics=0" })
            start.ArgumentList.Add(argument);
    }

    /// <summary>Overmounts only the phase namespace's runtime directory; failure never reaches the target.</summary>
    /// <remarks>The existing unshare mount namespace isolates this fixed mount from the host. The empty
    /// read-only filesystem hides manager paths created by systemd after the owner prepared its root.</remarks>
    internal const string RuntimeDirectoryScript = "/usr/bin/mount --no-mtab --types tmpfs "
        + "--options ro,nosuid,nodev,noexec,size=4096,nr_inodes=16,mode=0755 tmpfs /run || exit 125; exec \"$@\"";

    internal const string DiagnosticScript = "while IFS= read -r line; do case \"$line\" in "
        + "Uid:*|Gid:*|Groups:*|CapInh:*|CapPrm:*|CapEff:*|CapBnd:*|CapAmb:*|NoNewPrivs:*|Seccomp:*|Seccomp_filters:*) "
        + "printf '%s\\n' \"ISOLATION_BOOTSTRAP:$line\" >&2;; esac; done </proc/self/status; "
        + "if IFS= read -r label </proc/self/attr/current; then "
        + "if [ \"$label\" = unconfined ]; then printf 'ISOLATION_BOOTSTRAP_LSM:unconfined\\n' >&2; "
        + "else printf 'ISOLATION_BOOTSTRAP_LSM:confined\\n' >&2; fi; "
        + "else printf 'ISOLATION_BOOTSTRAP_LSM:unavailable\\n' >&2; fi; " + RuntimeDirectoryScript;

    /// <summary>Returns the same private runtime setup for every phase, with diagnostics only for a control.</summary>
    internal static IReadOnlyList<string> Prefix(OwnerPhase phase) =>
        ["/usr/bin/bash", "-c", phase == OwnerPhase.Control ? DiagnosticScript : RuntimeDirectoryScript, "isolation-bootstrap"];
}
