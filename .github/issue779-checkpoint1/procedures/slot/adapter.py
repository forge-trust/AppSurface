"""Fixed current N01–N08 adapter; the C# executable remains the runtime owner."""

from pathlib import Path
import hashlib
import json
import types


HERE = Path(__file__).resolve().parent


def bridge(capture_raw):
    """Load the fixed captured bridge after the dispatcher's source verification."""
    capture = json.loads(capture_raw)
    path = HERE / "bridge.py"
    raw = path.read_bytes()
    if len(raw) > 131072 or hashlib.sha256(raw).hexdigest() != capture["files"][
        ".github/issue779-checkpoint1/procedures/slot/bridge.py"]["sha256"]:
        raise ValueError("slot-bridge-binding")
    module = types.ModuleType("c1_fixed_slot_bridge")
    module.__file__ = str(path)
    exec(compile(raw, str(path), "exec", dont_inherit=True), module.__dict__)
    return module


def execute(context, *, deadline):
    """Run the original fixed slot procedure and validate its retained bytes."""
    raw = Path(context["capture_path"]).read_bytes()
    if len(raw) > 8 * 1024**2 or hashlib.sha256(raw).hexdigest() != context["capture_sha256"]:
        raise ValueError("slot-capture-binding")
    return bridge(raw).execute_case(context, deadline=deadline)


def verify_retained_evidence(context, *, deadline):
    """Re-read actual files and original data predicates without starting work."""
    raw = Path(context["capture_path"]).read_bytes()
    if len(raw) > 8 * 1024**2 or hashlib.sha256(raw).hexdigest() != context["capture_sha256"]:
        raise ValueError("slot-capture-binding")
    return bridge(raw).verify_packet(context, deadline=deadline)


def validate_evidence(context, evidence_raw, *, capture_raw, build_raw=None, raw_members=None, archive_raw=None, deadline=None):
    """Validate bytes only with all actual retained inputs and an original deadline.

    Aggregate callers use verify_retained_evidence to open every fixed retained
    file and command log. A detached envelope cannot pass this API.
    """
    if build_raw is None or raw_members is None or archive_raw is None or deadline is None:
        raise ValueError("slot-actual-retained-inputs-required")
    selected = bridge(capture_raw)
    return selected.validate_packet(context, selected.decode(evidence_raw), capture_raw, build_raw,
                                    raw_members, archive_raw=archive_raw, deadline=deadline)
