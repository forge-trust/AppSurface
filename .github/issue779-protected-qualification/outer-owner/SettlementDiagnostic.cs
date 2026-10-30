using System.Runtime.Versioning;

namespace EvidenceHost.CompletedIsolation;

/// <summary>Finite, non-authorizing observations from cgroup settlement reads already performed.</summary>
[SupportedOSPlatform("linux")]
internal sealed class SettlementDiagnostic
{
    private string _launcher = "unknown";
    private string _held = "unknown";
    private string _links = "unknown";
    private string _path = "unknown";
    private string _events = "unknown";
    private string _processes = "unknown";
    private string _descriptorPath = "unknown";
    private string _procfs = "unknown";
    private string _cgroupRoot = "unknown";
    private string _error = "none";
    private bool _active;

    internal void Begin()
    {
        _launcher = "unknown";
        _held = "unknown";
        _links = "unknown";
        _path = "unknown";
        _events = "unknown";
        _processes = "unknown";
        _descriptorPath = "unknown";
        _procfs = "unknown";
        _cgroupRoot = "unknown";
        _error = "none";
        _active = true;
    }

    internal void Launcher(bool exited)
    {
        if (_active) _launcher = exited ? "exited" : "live";
    }

    internal void Held(NativeFacts.CgroupIdentity identity, NativeFacts.CgroupIdentity expected)
    {
        if (!_active) return;
        _held = identity.SameObject(expected) ? "matching" : "mismatch";
        _links = identity.Links == 0 ? "zero" : "nonzero";
    }

    internal void Path(NativeFacts.CgroupPathIdentity identity, NativeFacts.CgroupIdentity expected)
    {
        if (!_active) return;
        _path = !identity.ErrorFree ? "error"
            : identity.Missing ? "missing"
            : identity.SameNode(expected) ? "same"
            : "replaced";
    }

    internal void Events(bool empty) { if (_active) _events = empty ? "empty" : "populated"; }
    internal void Processes(bool empty) { if (_active) _processes = empty ? "empty" : "present"; }
    internal void RemovalEvidence(bool trustedProcfs, bool capturedPathExact, bool deletedPathExact)
    {
        if (!_active) return;
        _procfs = trustedProcfs ? "trusted" : "untrusted";
        _descriptorPath = capturedPathExact ? deletedPathExact ? "deleted-exact" : "deleted-mismatch"
            : "capture-mismatch";
    }
    internal void CgroupRoot(bool matches) { if (_active) _cgroupRoot = matches ? "matching" : "mismatch"; }
    internal void Error(Exception error) { if (_active) _error = Program.SafeFailureCode(error); }

    internal string Format() =>
        $"launcher={_launcher};held={_held};links={_links};path={_path};events={_events};processes={_processes};"
        + $"descriptor={_descriptorPath};procfs={_procfs};cgroup-root={_cgroupRoot};error={_error}";
}
