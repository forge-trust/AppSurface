"""Source-bound, portable retained-data custody for the fourteen C1 selectors.

``validate`` reconciles a single measured segment with its original native
result. It reads only captured validator source from this checkout; archives,
command records and both streams must come from ``raws``. Historical image
paths are lexical identifiers, never current binary roots. This module neither
runs a procedure nor grants native acceptance, report credit or counter authority.

Original family predicates are selected from hash-verified source ASTs. Adapter
entry points and module-time parser reads are excluded. Each invocation owns its
module namespace, including sibling imports; no process-global import aliases
are installed. Call after the stage's source, plan, build and result checks.
"""
from __future__ import annotations

import ast
import base64
import builtins
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import time
import types


PREFIX = ".github/issue779-checkpoint1/"
SELECTORS = ("None", *(f"N{i:02d}" for i in range(4, 17)))
MAX_LEAF = 8388608
MAX_SOURCE = 131072
MAX_ARCHIVE = 33619968
_ROOT = Path(__file__).resolve().parents[2]

# Only these reviewed adapters contribute executable data callbacks. Native
# entry points, source materializers, root readers and process runners are absent.
_CALLBACKS = {
    "binding.py": {
        "required_helpers", "Rejected", "require", "exact", "integer", "digest",
        "canonical", "_pairs", "decode", "relative", "absolute", "overlaps",
        "validate_capture", "validate_image", "validate_build", "validate_measured_build",
        "validate_helper_commands"},
    "procedures/slot/bridge.py": {
        "need", "sha", "pairs", "decode", "canonical", "unpack",
        "validate_packet", "validate_commands"},
    "procedures/n11/bridge.py": {
        "need", "sha", "pairs", "decode", "canonical", "unpack",
        "validate_packet", "validate_commands"},
    "procedures/n09/adapter.py": {"need", "validate_evidence"},
    "procedures/n10/adapter.py": {"need", "node_artifacts", "validate_evidence"},
    "procedures/owner-death/adapter.py": {
        "need", "digest", "canonical", "decode", "validate_evidence"},
    "procedures/n15/adapter.py": {
        "need", "_inputs", "_command", "verify_retained_evidence"},
    "procedures/n16/adapter.py": {
        "need", "validate_nodes", "artifacts", "validate_evidence"},
}
_DATA = {
    "binding.py",
    *(f"procedures/slot/records-{s.lower()}.py" for s in SELECTORS[:6]),
    *(f"procedures/slot/n04/{s}.py" for s in ("n04-record-data", "n04-archive-data")),
    *(f"procedures/slot/n07/{s}.py" for s in ("n07-record-data", "n07-archive-data")),
    *(f"procedures/slot/n08/{s}.py" for s in (
        "check_kernel_record", "check_cancellation_records", "cancellation_adapter",
        "check_cancellation_archive")),
    *(f"procedures/{s}/records.py" for s in ("n09", "n10", "n11", "n12", "n15", "n16")),
    "procedures/n09/check_n09_records.py",
    *(f"procedures/n11/{s}.py" for s in ("root_stall_frame", "check_stall_record", "stall_capture")),
    *(f"procedures/parsers/{s}.py" for s in (
        "check_owner_death_record", "check_n10_pending_start", "check_n15_records", "check_n16_records")),
}
_STDLIB = frozenset((
    "__future__", "_strptime", "argparse", "base64", "binascii", "datetime", "grp", "hashlib",
    "io", "json", "math", "os", "pathlib", "pwd", "re", "stat", "subprocess",
    "sys", "tarfile", "time", "types", "typing"))


class Rejected(ValueError):
    """A retained segment or its captured validator source is inconsistent."""


def _need(ok, reason):
    if not ok:
        raise Rejected("measurement-custody-" + reason)


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _pairs(rows):
    value = {}
    for key, item in rows:
        _need(key not in value, "duplicate-json-key")
        value[key] = item
    return value


def _decode(raw):
    _need(type(raw) is bytes and len(raw) <= MAX_LEAF, "json-bound")
    return json.loads(raw, object_pairs_hook=_pairs,
                      parse_constant=lambda _: _need(False, "nonfinite-json"))


def _end(deadline):
    _need(type(deadline) in (int, float) and math.isfinite(deadline)
          and time.monotonic() < deadline, "deadline")


class _Sources:
    """Per-call captured code loader and byte-only replacement for retained reads.

    A virtual filename under the receipt's historical source root preserves the
    original bridge's lexical ``relative_to`` calculation across hosts. Neither
    receipt bytes nor path claims are changed. Reads are resolved explicitly to
    supplied retained bytes or the fixed captured source allowlist.
    """

    def __init__(self, capture, source_raw, build_raw, raws, deadline):
        self.capture, self.source_raw, self.build_raw = capture, source_raw, build_raw
        self.raws, self.deadline = raws, deadline
        self.root = Path(_decode(build_raw)["source_root"])
        _need(self.root.is_absolute(), "historical-source-root")
        self.c1 = self.root / PREFIX.rstrip("/")
        self.output = Path("/__issue779_retained_native__")
        self.capture_path = Path("/__issue779_capture__.json")
        self.build_path = Path("/__issue779_build__.json")
        self.modules, self.bytes = {}, {}

    def source(self, rel):
        _end(self.deadline)
        _need(rel in _DATA or rel in _CALLBACKS, "source-allowlist")
        if rel not in self.bytes:
            path = _ROOT / PREFIX / rel
            _need(path.resolve(strict=True) == path, "source-path")
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
            try:
                before = os.fstat(fd)
                _need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                      and 0 < before.st_size <= MAX_SOURCE, "source-file")
                with os.fdopen(fd, "rb", closefd=False) as stream:
                    raw = stream.read(MAX_SOURCE + 1)
                after, named = os.fstat(fd), path.lstat()
                stamp = lambda s: (s.st_dev, s.st_ino, s.st_mode, s.st_nlink,
                                   s.st_size, s.st_mtime_ns, s.st_ctime_ns)
                pin = self.capture["files"][PREFIX + rel]
                _need(stamp(before) == stamp(after) == stamp(named)
                      and len(raw) == before.st_size
                      and _sha(raw) == pin["sha256"]
                      and format(stat.S_IMODE(before.st_mode), "04o") == pin["mode"], "source-binding")
            finally:
                os.close(fd)
            self.bytes[rel] = raw
        return self.bytes[rel]

    def read(self, path, maximum=MAX_LEAF, *, deadline):
        _end(self.deadline)
        _end(deadline)
        path = Path(path)
        if path == self.capture_path:
            raw = self.source_raw
        elif path == self.build_path:
            raw = self.build_raw
        elif path.is_relative_to(self.output):
            name = path.relative_to(self.output).as_posix()
            _need(".." not in Path(name).parts, "retained-path")
            raw = self.raws.get("native/" + name)
        else:
            _need(path.is_relative_to(self.c1), "read-namespace")
            raw = self.source(path.relative_to(self.c1).as_posix())
        _need(type(raw) is bytes and len(raw) <= maximum, "actual-retained-bytes")
        return raw

    def load(self, path, *unused, **keywords):
        _end(self.deadline)
        path = Path(path)
        rel = path.relative_to(self.c1).as_posix() if path.is_absolute() else path.as_posix()
        if rel in self.modules:
            return self.modules[rel]
        raw = self.source(rel)
        module = types.ModuleType("c1_retained_" + rel.replace("/", "_").replace("-", "_"))
        module.__file__ = str(self.c1 / rel)
        self.modules[rel] = module

        def imports(name, globals=None, locals=None, fromlist=(), level=0):
            _need(level == 0, "relative-import")
            if name == "binding":
                return self.load("binding.py")
            sibling = (Path(rel).parent / (name + ".py")).as_posix()
            if sibling in _DATA:
                return self.load(sibling)
            _need(name.split(".")[0] in _STDLIB, "unbound-import")
            return builtins.__import__(name, globals, locals, fromlist, level)

        namespace = module.__dict__
        namespace["__builtins__"] = dict(vars(builtins), __import__=imports)
        namespace.update(HERE=(self.c1 / rel).parent, C1=self.c1, BASE=self.c1)
        tree = ast.parse(raw, filename=module.__file__)
        selected = []
        for node in tree.body:
            if isinstance(node, (ast.Import, ast.ImportFrom)):
                selected.append(node)
            elif isinstance(node, (ast.FunctionDef, ast.ClassDef)):
                if rel not in _CALLBACKS or node.name in _CALLBACKS[rel]:
                    selected.append(node)
            elif isinstance(node, (ast.Assign, ast.AnnAssign)):
                targets = node.targets if isinstance(node, ast.Assign) else [node.target]
                names = {n.id for t in targets for n in ast.walk(t) if isinstance(n, ast.Name)}
                skip = {"HERE", "C1"} if rel in _CALLBACKS else set()
                if rel == "procedures/n16/adapter.py":
                    skip |= {"BASE", "_RECORD_RAW", "_PARSER_RAW", "_RECORDS", "_PARSER"}
                if rel == "procedures/owner-death/adapter.py":
                    skip |= {"_PARSER_RAW", "_PARSER_SHA", "_PARSER"}
                if rel == "procedures/slot/n04/n04-archive-data.py":
                    skip |= {"record_data"}
                if not names & skip:
                    selected.append(node)
            elif (isinstance(node, ast.Expr) and isinstance(node.value, ast.Call)
                  and isinstance(node.value.func, ast.Attribute)
                  and isinstance(node.value.func.value, ast.Name)
                  and node.value.func.value.id == "NEGATIVE_CAPS"
                  and node.value.func.attr == "update"):
                selected.append(node)
        exec(compile(ast.Module(body=selected, type_ignores=[]), module.__file__, "exec",
                     dont_inherit=True), namespace)
        module.load = self.load
        if rel == "binding.py":
            module.read = self.read
        if rel == "procedures/slot/bridge.py":
            module.slot_modules = self.slot_modules
        if rel == "procedures/n11/bridge.py":
            module.record_modules = self.record_modules
        if rel == "procedures/slot/n04/n04-archive-data.py":
            parser = "procedures/slot/n04/n04-record-data.py"
            _need(_sha(self.source(parser)) == module.PARSER_SHA256, "n04-fixed-parser")
            module.record_data = self.load(parser)
        if rel == "procedures/owner-death/adapter.py":
            parser = "procedures/parsers/check_owner_death_record.py"
            module._PARSER_SHA, module._PARSER = _sha(self.source(parser)), self.load(parser)
        if rel == "procedures/n16/adapter.py":
            records, parser = "procedures/n16/records.py", "procedures/parsers/check_n16_records.py"
            module._RECORD_RAW, module._PARSER_RAW = self.source(records), self.source(parser)
            module._RECORDS, module._PARSER = self.load(records), self.load(parser)
        _end(self.deadline)
        return module

    def slot_modules(self, case, *unused, **keywords):
        names = {"N04": ("n04-record-data", "n04-archive-data"),
                 "N07": ("n07-record-data", "n07-archive-data"),
                 "N08": ("check_kernel_record", "check_cancellation_records",
                         "cancellation_adapter", "check_cancellation_archive")}.get(case, ())
        return {name: self.load(f"procedures/slot/{case.lower()}/{name}.py") for name in names}

    def record_modules(self, *unused, **keywords):
        return {name: self.load(f"procedures/n11/{name}.py")
                for name in ("root_stall_frame", "check_stall_record", "stall_capture")}


def _commands(raws, commands, bridge, deadline):
    """Require actual settled records and both original streams, even when empty."""
    bridge.validate_commands(commands)
    for ordinal, command in enumerate(commands):
        _end(deadline)
        name = f"native/commands/command-{ordinal:02d}.json"
        _need(_decode(raws.get(name)) == command, "command-record")
        _need("failure" in command and command["failure"] is None, "command-failure")
        for log in command["logs"]:
            raw = raws.get("native/commands/" + log["name"])
            _need(type(raw) is bytes and len(raw) == log["bytes"]
                  and _sha(raw) == log["sha256"], "command-stream")


def _slot(raws, sources, context, source_raw, build_raw, selector, deadline):
    n11 = selector in ("N11", "N12")
    bridge = sources.load("procedures/n11/bridge.py" if n11 else "procedures/slot/bridge.py")
    records = sources.load(f"procedures/{selector.lower()}/records.py" if n11
                           else f"procedures/slot/records-{selector.lower()}.py")
    cases = ("N01", "N02", "N03") if selector == "None" else (selector,)
    evidence = raws.get("native/" + cases[0] + "-evidence.json")
    packet = _decode(evidence)
    _need(all(raws.get("native/" + case + "-evidence.json") == evidence for case in cases), "none-packet-copies")
    archive = raws.get("native/native-private.tar")
    _need(type(archive) is bytes and 0 < len(archive) <= MAX_ARCHIVE, "archive-bound")
    members = bridge.unpack(archive, records.ARCHIVE_NAMES)
    projection = {n.removeprefix("native/retained/") for n in raws if n.startswith("native/retained/")}
    _need(projection == set(members), "archive-projection")
    for name, raw in members.items():
        _end(deadline)
        _need(raws.get("native/retained/" + name) == raw, "projected-member")
        row = packet["files"].get(name)
        _need(type(row) is dict and set(row) == {"bytes", "sha256"}
              and type(row["bytes"]) is int and row == {"bytes": len(raw), "sha256": _sha(raw)}, "member-pin")
    _commands(raws, packet["commands"], bridge, deadline)
    rows = bridge.validate_packet(context, packet, source_raw, build_raw, members,
                                  archive_raw=archive, deadline=deadline)
    return rows if type(rows) is list else [rows]


def validate(raws, result, source_raw, head, selector, identity, build_raw,
             execution, capture, *, deadline) -> None:
    """Revalidate one original native family's retained bytes without native I/O.

    ``raws`` maps current-segment relative names to actual bytes. ``source_raw``
    and ``build_raw`` are unchanged captured receipts; ``capture`` is their
    already-validated source map. ``execution`` supplies the original generation
    and workflow identity. ``deadline`` is the caller's existing absolute
    monotonic deadline; it is checked, never renewed. All original case rows must
    equal ``result['cases']`` in order. Inconsistency raises ``ValueError`` (or an
    original strict parser's rejection); missing/malformed inputs also fail closed.

    This is data consistency only: it does not inspect downloaded binary roots,
    query NSS, open processes, launch a worker, or validate coverage arithmetic.
    In particular N13's killed owner may produce no tracker hits; that imposes no
    additional acceptance condition here. The caller retains native authority.
    """
    _end(deadline)
    _need(selector in SELECTORS and type(raws) is dict
          and all(type(k) is str and type(v) is bytes for k, v in raws.items()), "input-shape")
    _need(_decode(source_raw) == capture and capture["source"] == head, "capture")
    _need(type(identity) is str and re.fullmatch(r"github-actions-[1-9][0-9]*-[1-9][0-9]*", identity)
          and execution["workflow_identity"] == identity
          and type(execution["generation"]) is str
          and re.fullmatch(r"[0-9a-f]{32}", execution["generation"]), "execution")
    sources = _Sources(capture, source_raw, build_raw, raws, deadline)
    binding = sources.load("binding.py")
    build = binding.decode(build_raw)
    _need(binding.validate_build(build, source_raw, head, selector) == capture, "build-capture")
    context = {"source": head, "selector": selector, "capture_path": str(sources.capture_path),
               "capture_sha256": _sha(source_raw), "build_receipt_path": str(sources.build_path),
               "build_receipt_sha256": _sha(build_raw), "tool_root": build["tool"]["root"],
               "runtime_root": build["runtime"]["root"], "output": str(sources.output),
               "generation": execution["generation"], "workflow_identity": identity,
               "binding_validator": str(sources.c1 / "binding.py"),
               "binding_validator_sha256": capture["files"][PREFIX + "binding.py"]["sha256"]}
    if selector in ("None", "N04", "N05", "N06", "N07", "N08", "N11", "N12"):
        rows = _slot(raws, sources, context, source_raw, build_raw, selector, deadline)
    else:
        evidence = raws.get("native/" + selector + "-evidence.json")
        packet = _decode(evidence)
        if selector in ("N09", "N10"):
            adapter = sources.load(f"procedures/{selector.lower()}/adapter.py")
            bridge = sources.load("procedures/slot/bridge.py")
            _commands(raws, packet["commands"], bridge, deadline)
            archive = raws.get("native/native-private.tar")
            row = adapter.validate_evidence(context, evidence, capture_raw=source_raw,
                                           build_raw=build_raw, archive_raw=archive, deadline=deadline)
        elif selector in ("N13", "N14"):
            adapter = sources.load("procedures/owner-death/adapter.py")
            row = adapter.validate_evidence(context, evidence, capture_raw=source_raw)
            for name, pin in packet["retained"].items():
                _end(deadline)
                raw = raws.get("native/" + name)
                _need(type(raw) is bytes and len(raw) == pin["bytes"]
                      and _sha(raw) == pin["sha256"], "owner-death-leaf")
        elif selector == "N15":
            adapter = sources.load("procedures/n15/adapter.py")
            row = adapter.verify_retained_evidence(context, deadline=deadline)
        else:
            adapter = sources.load("procedures/n16/adapter.py")
            row = adapter.validate_evidence(context, evidence, capture_raw=source_raw)
            _need(base64.b64decode(packet["build_raw"], validate=True) == build_raw, "n16-build-bytes")
            for name, limit in adapter.LEAVES.items():
                _end(deadline)
                pin = packet["files"][name]
                raw = base64.b64decode(pin["base64"], validate=True)
                path = name if name in ("command-00.json", "00-stdout.log", "00-stderr.log") else "N16-" + name
                _need(len(raw) <= limit and raws.get("native/" + path) == raw, "n16-actual-leaf")
            command = _decode(base64.b64decode(packet["files"]["command-00.json"]["base64"], validate=True))
            _need(type(command.get("ordinal")) is int and command["ordinal"] == 0
                  and "failure" in command and command["failure"] is None, "n16-command-types")
        rows = [row]
    _need(rows == result["cases"], "original-case-rows")
    for row in rows:
        _need(_sha(raws["native/" + row["case"] + "-evidence.json"]) == row["evidence_sha256"], "actual-evidence-hash")
    _end(deadline)
