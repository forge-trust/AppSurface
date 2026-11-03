"""Fixed current-source N12 native adapter; no detached model proves execution."""
from pathlib import Path
import hashlib
import json
import types

HERE = Path(__file__).resolve().parent


def _bridge(capture_raw):
    capture = json.loads(capture_raw)
    path = HERE.parent / "n11/bridge.py"
    rel = ".github/issue779-checkpoint1/procedures/n11/bridge.py"
    raw = path.read_bytes()
    if len(raw) > 131072 or hashlib.sha256(raw).hexdigest() != capture["files"][rel]["sha256"]:
        raise ValueError("native-family-bridge-binding")
    module = types.ModuleType("c1_fixed_native_family_bridge")
    module.__file__ = str(path)
    exec(compile(raw, str(path), "exec", dont_inherit=True), module.__dict__)
    return module


def execute(context, *, deadline):
    """Use actual fixed shell/OS audit/peer prerequisites; count only N12."""
    if context["selector"] != "N12":
        raise ValueError("native-family-selector")
    capture_raw = Path(context["capture_path"]).read_bytes()
    if len(capture_raw) > 8 * 1024**2 or hashlib.sha256(capture_raw).hexdigest() != context["capture_sha256"]:
        raise ValueError("native-family-capture-binding")
    return _bridge(capture_raw).execute_case(context, deadline=deadline)


def validate_evidence(context, evidence_raw, *, capture_raw):
    """Check source-bound envelope data; mandatory retained verification checks records.

    This detached callback does not establish a physical process fact. Run and
    aggregate must additionally call verify_retained_evidence and compare rows.
    """
    if type(capture_raw) is not bytes or len(capture_raw) > 8 * 1024**2 or hashlib.sha256(capture_raw).hexdigest() != context["capture_sha256"]:
        raise ValueError("native-family-capture-binding")
    bridge = _bridge(capture_raw)
    bridge.need(type(evidence_raw) is bytes and len(evidence_raw) <= 8 * 1024**2, "envelope-bound")
    packet, capture = bridge.decode(evidence_raw), bridge.decode(capture_raw)
    bridge.need(set(packet) == {"schema", "source", "selector", "input_generation", "source_capture_sha256",
        "build_receipt_sha256", "base_revision", "procedure_sha256", "archive_sha256", "files", "commands"}, "envelope-fields")
    bridge.need(packet["schema"] == "issue779-checkpoint1-native-family-v1" and context["selector"] == "N12"
        and packet["selector"] == "N12" and packet["source"] == context["source"] == capture["source"]
        and packet["input_generation"] == context["generation"]
        and packet["source_capture_sha256"] == context["capture_sha256"]
        and packet["build_receipt_sha256"] == context["build_receipt_sha256"], "envelope-binding")
    procedure = ".github/issue779-checkpoint1/procedures/n12/checkpoint-n12-data-integration.sh"
    bridge.need(packet["procedure_sha256"] == capture["files"][procedure]["sha256"], "envelope-procedure")
    bridge.need(type(packet["files"]) is dict and 1 <= len(packet["files"]) <= 128, "envelope-files")
    total = 0
    for name, row in packet["files"].items():
        bridge.need(type(name) is str and not name.startswith("/") and all(p not in ("", ".", "..") for p in name.split("/"))
            and type(row) is dict and set(row) == {"bytes", "sha256"}
            and type(row["bytes"]) is int and 0 <= row["bytes"] <= 8 * 1024**2
            and type(row["sha256"]) is str and bridge.re.fullmatch("[0-9a-f]{64}", row["sha256"]), "envelope-file")
        total += row["bytes"]
    bridge.need(total <= bridge.MAX_TOTAL and type(packet["archive_sha256"]) is str
        and bridge.re.fullmatch("[0-9a-f]{64}", packet["archive_sha256"])
        and type(packet["base_revision"]) is str and bridge.re.fullmatch("[0-9a-f]{40}", packet["base_revision"]), "envelope-report")
    bridge.validate_commands(packet["commands"])
    return {"case": "N12", "passed": True, "skipped": False,
            "procedure_sha256": packet["procedure_sha256"], "evidence_sha256": hashlib.sha256(evidence_raw).hexdigest()}


def verify_retained_evidence(context, *, deadline):
    """Re-read fixed retained leaves and repeat the original predicates; no process start."""
    capture_raw = Path(context["capture_path"]).read_bytes()
    if len(capture_raw) > 8 * 1024**2 or hashlib.sha256(capture_raw).hexdigest() != context["capture_sha256"]:
        raise ValueError("native-family-capture-binding")
    return _bridge(capture_raw).verify_packet(context, deadline=deadline)
