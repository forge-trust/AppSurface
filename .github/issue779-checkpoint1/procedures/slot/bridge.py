"""Fixed slot/peer root bridge; all native predicates remain in the literal shells.

Current source/build/helper receipts are verified without translating the old build
schema. Only raw actual private records can pass the readable record validators.
"""
from pathlib import Path
import hashlib
import io
import json
import os
import pwd
import re
import stat
import subprocess
import sys
import tarfile
import time
import types

HERE = Path(__file__).resolve().parent
C1 = HERE.parents[1]
CASES = {"None": "checkpoint-none.sh", **{f"N{i:02d}": f"checkpoint-n{i:02d}.sh" for i in range(4, 9)}}
INPUTS = {"plan.json", "source.json", "build-receipt.json", "procedure-inventory.json", "execution.json"}
MAX_JSON = 8 * 1024**2
MAX_ARCHIVE = 33619968
MAX_TOTAL = 33554432 + 4096


def need(ok, code):
    if not ok:
        raise ValueError("c1-slot-peer-" + code)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def pairs(items):
    """Reject exact duplicate JSON names; Linux inventory paths are case-sensitive.

    Closed receipt/envelope field sets are validated separately. Case folding
    here would reject legitimate distinct published paths such as Assets/assets.
    """
    value = {}
    for key, item in items:
        need(type(key) is str and key not in value, "json-member")
        value[key] = item
    return value


def decode(raw):
    return json.loads(raw, object_pairs_hook=pairs,
                      parse_constant=lambda _: need(False, "json-number"))


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode() + b"\n"


def remaining(deadline):
    value = deadline - time.monotonic()
    need(value > 0, "original-deadline")
    return value


def read(path, cap, *, deadline):
    path = Path(path)
    need(path.is_absolute() and path.resolve(strict=True) == path, "path")
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    signature = lambda s: (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink,
                           s.st_size, s.st_mtime_ns, s.st_ctime_ns)
    try:
        before = os.fstat(fd)
        need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and 0 <= before.st_size <= cap, "file")
        raw = bytearray()
        while len(raw) <= before.st_size:
            remaining(deadline)
            chunk = os.read(fd, min(65536, before.st_size + 1 - len(raw)))
            if not chunk:
                break
            raw.extend(chunk)
        need(len(raw) == before.st_size and signature(before) == signature(os.fstat(fd))
             == signature(path.lstat()), "changed-file")
        return bytes(raw)
    finally:
        os.close(fd)


def load(path, capture, source_root, *, deadline):
    rel = Path(path).relative_to(source_root).as_posix()
    raw = read(path, 131072, deadline=deadline)
    need(sha(raw) == capture["files"][rel]["sha256"], "source-module")
    module = types.ModuleType("c1_fixed_" + Path(path).stem)
    module.__file__ = str(path)
    exec(compile(raw, str(path), "exec", dont_inherit=True), module.__dict__)
    return module


def save_archive(path, raw, *, deadline):
    """Retain the original USTAR bytes under their separate archive bound.

    The generic per-log/leaf writer keeps its 8 MiB cap. This fixed archive
    writer is exclusive, retains its FD through fsync and named-file identity
    checks, and never overwrites or truncates a prior generation's evidence.
    It copies data only; no successful write establishes a native outcome.
    """
    path = Path(path)
    need(path.name == "native-private.tar" and path.parent.resolve(strict=True) == path.parent,
         "archive-output-path")
    need(type(raw) is bytes and 0 < len(raw) <= MAX_ARCHIVE, "archive-output-bound")
    remaining(deadline)
    parent = path.parent.lstat()
    need(stat.S_ISDIR(parent.st_mode) and parent.st_uid == os.geteuid()
         and stat.S_IMODE(parent.st_mode) == 0o700, "archive-output-parent")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        os.fchmod(fd, 0o600)
        view = memoryview(raw)
        while view:
            remaining(deadline)
            written = os.write(fd, view)
            need(written > 0, "archive-output-write")
            view = view[written:]
        os.fsync(fd)
        opened, named = os.fstat(fd), path.lstat()
        facts = lambda s: (s.st_dev, s.st_ino, s.st_uid, s.st_gid, s.st_mode, s.st_nlink, s.st_size)
        need(facts(opened) == facts(named) and stat.S_ISREG(opened.st_mode)
             and opened.st_uid == os.geteuid() and opened.st_nlink == 1
             and stat.S_IMODE(opened.st_mode) == 0o600 and opened.st_size == len(raw), "archive-output-custody")
        remaining(deadline)
    finally:
        os.close(fd)
    remaining(deadline)


def prerequisites(context, *, deadline):
    """Verify original current inputs and adopt only the dispatcher's five root600 files."""
    need(os.geteuid() == 0 and sys.platform == "linux" and context["selector"] in CASES, "root-selector")
    need(0 < remaining(deadline) <= 900 and re.fullmatch("[0-9a-f]{32}", context["generation"]), "deadline-generation")
    cap_raw = read(context["capture_path"], MAX_JSON, deadline=deadline)
    build_raw = read(context["build_receipt_path"], MAX_JSON, deadline=deadline)
    need(sha(cap_raw) == context["capture_sha256"] and sha(build_raw) == context["build_receipt_sha256"], "input-sha")
    capture, build = decode(cap_raw), decode(build_raw)
    source_root = Path(build["source_root"])
    need(Path(context["binding_validator"]) == source_root / ".github/issue779-checkpoint1/binding.py"
         and capture["source"] == context["source"], "source-validator")
    binding = load(Path(context["binding_validator"]), capture, source_root, deadline=deadline)
    need(sha(read(context["binding_validator"], 131072, deadline=deadline)) == context["binding_validator_sha256"], "binding-sha")
    binding.verify_build(build, cap_raw, context["source"], context["selector"], deadline=deadline)
    need(build["tool"]["root"] == context["tool_root"] and build["runtime"]["root"] == context["runtime_root"], "root-selection")
    output = binding.absolute(context["output"])
    first = output.lstat()
    need(output.resolve(strict=True) == output and stat.S_ISDIR(first.st_mode)
         and first.st_uid == first.st_gid == 0 and stat.S_IMODE(first.st_mode) == 0o700
         and first.st_nlink >= 2 and set(os.listdir(output)) == INPUTS, "dispatcher-output")
    inputs = {}
    for leaf in INPUTS:
        s = (output / leaf).lstat()
        need(stat.S_ISREG(s.st_mode) and s.st_nlink == 1 and s.st_uid == s.st_gid == 0
             and stat.S_IMODE(s.st_mode) == 0o600, "dispatcher-input-custody")
        inputs[leaf] = read(output / leaf, MAX_JSON, deadline=deadline)
    need(inputs["source.json"] == cap_raw and inputs["build-receipt.json"] == build_raw, "dispatcher-input-copy")
    plan, execution = decode(inputs["plan.json"]), decode(inputs["execution.json"])
    need(plan["source"] == context["source"] and plan["selector"] == context["selector"]
         and plan["source_capture_sha256"] == context["capture_sha256"]
         and plan["build_receipt_sha256"] == context["build_receipt_sha256"]
         and plan["procedure_inventory_sha256"] == sha(inputs["procedure-inventory.json"])
         and execution == {"generation": context["generation"], "workflow_identity": context["workflow_identity"]}, "dispatcher-plan")
    owner = load(C1 / "procedures/owner-death/adapter.py", capture, source_root, deadline=deadline)
    prepared = owner.prepare_verified_inputs(context, binding, capture, build, cap_raw, build_raw,
                                              deadline=deadline, source_sealed=False)
    helpers = load(C1 / "procedures/slot/helpers.py", capture, source_root, deadline=deadline)
    peer = helpers.build_peer_inputs(context, prepared, binding, deadline=deadline)
    base = subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false",
        "-c", "safe.directory=" + str(source_root), "rev-parse", context["source"] + "^"], cwd=source_root,
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        timeout=min(15, remaining(deadline)), check=False)
    need(base.returncode == 0 and re.fullmatch(b"[0-9a-f]{40}\\n", base.stdout), "current-parent")
    return binding, owner, capture, build, prepared, peer, base.stdout.decode().strip(), output


def unpack(raw, allowed):
    """Validate and read the original bounded USTAR packet without filesystem extraction."""
    need(type(raw) is bytes and 0 < len(raw) <= MAX_ARCHIVE, "archive-bound")
    values, names, total = {}, [], 0
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:") as archive:
        for member in archive:
            need(member.name in allowed and member.name not in values and member.isfile()
                 and member.mode == 0o600 and member.uid == member.gid == member.mtime == 0
                 and not member.linkname and not member.pax_headers and not member.uname and not member.gname
                 and 0 <= member.size <= MAX_JSON, "archive-node")
            total += member.size
            need(total <= MAX_TOTAL and len(values) < len(allowed), "archive-total")
            with archive.extractfile(member) as f:
                data = f.read(member.size + 1)
            need(len(data) == member.size, "archive-short")
            values[member.name] = data
            names.append(member.name)
    need(names == sorted(names) and "retention-selection.json" in values, "archive-order")
    return values


def slot_modules(case, capture, source_root, *, deadline):
    """Load only the selected, source-bound detached byte validators.

    Sibling imports are scoped to this load and restored afterward. These
    parsers can reject inconsistent bytes; none can create a native owner.
    """
    selected = {"N04": ("n04-record-data", "n04-archive-data"),
                "N07": ("n07-record-data", "n07-archive-data"),
                "N08": ("check_kernel_record", "check_cancellation_records", "cancellation_adapter",
                        "check_cancellation_archive")}.get(case, ())
    previous = {name: sys.modules.get(name) for name in selected}
    modules = {}
    try:
        for name in selected:
            path = HERE / case.lower() / (name + ".py")
            module = load(path, capture, source_root, deadline=deadline)
            sys.modules[name] = module
            modules[name] = module
    finally:
        for name, original in previous.items():
            if original is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = original
    return modules


def additional_arguments(case, context, prepared, binding, capture, source_root, reviewed, *, deadline):
    """Install and select fixed helper/parser prerequisites without changing a case."""
    flags = []
    if case in ("N04", "N07"):
        helpers = load(HERE / "helpers.py", capture, source_root, deadline=deadline)
        helper = helpers.build_helper_inputs(context, prepared, binding, helper_id=case.lower(), deadline=deadline)
        prefix = "--" + case.lower() + "-helper-"
        flags += [prefix + "root", str(helper["root"]), prefix + "map", str(helper["map"]),
                  prefix + "map-sha256", helper["map_sha256"], prefix + "nodes", str(helper["nodes"]),
                  prefix + "nodes-sha256", helper["nodes_sha256"], prefix + "entry-sha256", helper["entry_sha256"]]
    names = {"N07": ("n07-record-data.py", "n07-helper-live.py"),
             "N08": ("check_kernel_record.py", "check_cancellation_records.py", "cancellation_adapter.py",
                     "capture_cancellation.py")}.get(case, ())
    if names:
        pins = []
        for name in names:
            path = HERE / case.lower() / name
            raw = read(path, 131072, deadline=deadline)
            need(sha(raw) == capture["files"][path.relative_to(source_root).as_posix()]["sha256"], "fixed-parser-binding")
            target = reviewed / name
            with target.open("xb") as stream:
                os.chmod(target, 0o444)
                stream.write(raw)
            pins.append(name + "\t" + sha(raw) + "\n")
        if case == "N07":
            flags += ["--n07-parser", str(reviewed / names[0]), "--n07-live-parser", str(reviewed / names[1])]
        else:
            pin_file = reviewed / "parser-pins.tsv"
            with pin_file.open("xb") as stream:
                os.chmod(pin_file, 0o444)
                stream.write("".join(pins).encode())
            flags += ["--cancellation-parser-root", str(reviewed), "--cancellation-parser-pins", str(pin_file)]
    return flags


def validate_packet(context, packet, capture_raw, build_raw, raw_members, *, archive_raw, deadline):
    """Re-run original readable data predicates against all actual retained raw bytes."""
    capture, build = decode(capture_raw), decode(build_raw)
    need(sha(capture_raw) == context["capture_sha256"] and sha(build_raw) == context["build_receipt_sha256"], "packet-inputs")
    case = context["selector"]
    need(set(packet) == {"schema", "source", "selector", "input_generation", "source_capture_sha256",
         "build_receipt_sha256", "base_revision", "procedure_sha256", "archive_sha256", "files", "commands"}, "packet-fields")
    need(packet["schema"] == "issue779-checkpoint1-native-family-v1" and packet["source"] == context["source"]
         and packet["selector"] == case and packet["input_generation"] == context["generation"]
         and packet["source_capture_sha256"] == context["capture_sha256"]
         and packet["build_receipt_sha256"] == context["build_receipt_sha256"], "packet-source")
    validate_commands(packet["commands"])
    source_root = Path(build["source_root"])
    case_root = HERE
    rel = (case_root / CASES[case]).relative_to(source_root).as_posix()
    need(packet["procedure_sha256"] == capture["files"][rel]["sha256"], "packet-procedure")
    need(type(packet["files"]) is dict and set(packet["files"]) == set(raw_members), "packet-files")
    for name, raw in raw_members.items():
        need(packet["files"][name] == {"bytes": len(raw), "sha256": sha(raw)}, "packet-retained-bytes")
    need(type(archive_raw) is bytes and packet["archive_sha256"] == sha(archive_raw), "packet-archive")
    records = load(HERE / ("records-" + case.lower() + ".py"), capture, source_root, deadline=deadline)
    need(unpack(archive_raw, records.ARCHIVE_NAMES) == raw_members, "packet-raw-archive-members")
    need(re.fullmatch("[0-9a-f]{40}", packet["base_revision"]), "packet-base")
    modules = slot_modules(case, capture, source_root, deadline=deadline)
    records.validate(archive_raw, source=context["source"], base=packet["base_revision"],
        entry_sha=build["tool"]["files"]["ForgeTrust.AppSurface.Cli.dll"]["sha256"], modules=modules)
    cases = ("N01", "N02", "N03") if case == "None" else (case,)
    rows = [{"case": name, "passed": True, "skipped": False,
             "procedure_sha256": packet["procedure_sha256"], "evidence_sha256": sha(canonical(packet))} for name in cases]
    return rows if case == "None" else rows[0]


def validate_commands(commands):
    """Require both bounded stream records for each settled native utility.

    This checks retained data shape only. The aggregate must additionally reread
    each command record and both actual stream files, including empty files.
    """
    need(type(commands) is list and len(commands) == 2, "packet-command-count")
    for ordinal, row in enumerate(commands):
        need(type(row) is dict and type(row.get("ordinal")) is int
             and row["ordinal"] == ordinal and type(row.get("exit")) is int
             and row["exit"] == 0 and row.get("waited") is True
             and row.get("group_absent") is True and row.get("forced_cleanup") is False
             and row.get("timed_out") is False and "failure" in row
             and row["failure"] is None and row.get("root_utility_cgroup_empty") is True,
             "packet-command-terminal")
        logs = row.get("logs")
        need(type(logs) is list and len(logs) == 2, "packet-command-stream-count")
        expected = {f"{ordinal:02d}-stdout.log", f"{ordinal:02d}-stderr.log"}
        seen = set()
        for log in logs:
            need(type(log) is dict and set(log) == {"name", "bytes", "sha256"}
                 and type(log["name"]) is str and log["name"] in expected
                 and log["name"] not in seen and type(log["bytes"]) is int
                 and 0 <= log["bytes"] <= MAX_JSON and type(log["sha256"]) is str
                 and re.fullmatch("[0-9a-f]{64}", log["sha256"]) is not None,
                 "packet-command-stream")
            seen.add(log["name"])
        need(seen == expected, "packet-command-stream-set")


def verify_command_logs(output, commands, *, deadline):
    """Reread each ordinal's fixed stdout/stderr after complete shape checks."""
    validate_commands(commands)
    for row in commands:
        ordinal = row["ordinal"]
        saved = decode(read(output / "commands" / f"command-{ordinal:02d}.json", MAX_JSON, deadline=deadline))
        need(saved == row, "aggregate-command-record")
        for log in row["logs"]:
            raw = read(output / "commands" / log["name"], MAX_JSON, deadline=deadline)
            need(log["sha256"] == sha(raw) and log["bytes"] == len(raw), "aggregate-command-log")


def execute_case(context, *, deadline):
    binding, owner, capture, build, prepared, peer, base, output = prerequisites(context, deadline=deadline)
    source_root, control = Path(build["source_root"]), prepared["control"]
    case = context["selector"]
    case_root = HERE
    reviewed = prepared["retained"] / "reviewed"
    reviewed.mkdir(mode=0o700)
    names = [CASES[case], "retain-" + case.lower() + ".sh", "dispatch-" + case.lower() + ".sh"]
    for name in names:
        raw = read(case_root / name, 131072, deadline=deadline)
        rel = (case_root / name).relative_to(source_root).as_posix()
        need(sha(raw) == capture["files"][rel]["sha256"], "installed-procedure")
        installed = "checkpoint-n01-n02-v5.sh" if name == CASES[case] else "retain-native.sh" if name.startswith("retain-") else "dispatch.sh"
        owner.save(reviewed / installed, raw)
        os.chmod(reviewed / installed, 0o500 if installed == "retain-native.sh" else 0o444)
    audit_module = load(C1 / "procedures/os_audit.py", capture, source_root, deadline=deadline)
    extra_flags = additional_arguments(case, context, prepared, binding, capture, source_root, reviewed, deadline=deadline)
    extra_roots = [extra_flags[extra_flags.index("--" + case.lower() + "-helper-root") + 1]] if case in ("N04", "N07") else []
    audit_owner = audit_module.Audit(min(deadline - 5, time.monotonic() + 60))
    audit_owner.initialize_tools()
    audit = audit_owner.build(
        str(prepared["roots"]["runtime"]), str(prepared["roots"]["tool"]), False, str(peer["root"]),
        additional_helpers=tuple(extra_roots))
    owner.save(control / "os-audit.json", audit)
    native = load(C1 / "procedures/native_process.py", capture, source_root, deadline=deadline)
    commands_output = output / "commands"
    commands_output.mkdir(mode=0o700)
    runner = native.Runner(commands_output, deadline)
    original_actor = source_root.lstat()
    uid, gid = original_actor.st_uid, original_actor.st_gid
    need(pwd.getpwuid(uid).pw_gid == gid, "n02-actual-source-actor")
    need(0 < uid < 2**32-1 and 0 < gid < 2**32-1, "n02-real-credentials")
    fixture = ["--execute", "--reviewed-script-sha256", capture["files"][(case_root / CASES[case]).relative_to(source_root).as_posix()]["sha256"]]
    for role, flag in (("source", "source"), ("tool", "payload"), ("runtime", "runtime")):
        map_path, node_path = control / (role + ".tsv"), control / (role + "-nodes.json")
        fixture += ["--" + flag + "-root", str(prepared["roots"][role]),
            "--" + flag + "-manifest", str(map_path), "--" + flag + "-manifest-sha256", sha(read(map_path, MAX_JSON, deadline=deadline)),
            "--" + flag + "-nodes", str(node_path), "--" + flag + "-nodes-sha256", sha(read(node_path, MAX_JSON, deadline=deadline))]
    fixture += ["--source-review", str(control / "source.json"), "--source-review-sha256", context["capture_sha256"],
        "--source-capture", str(control / "source.json"), "--source-capture-sha256", context["capture_sha256"],
        "--build-receipt", str(control / "build-receipt.json"), "--build-receipt-sha256", context["build_receipt_sha256"],
        "--binding-validator", str(control / "binding.py"), "--binding-validator-sha256", context["binding_validator_sha256"],
        "--binding-deadline-monotonic", repr(deadline), "--os-audit", str(control / "os-audit.json"), "--os-audit-sha256", sha(audit),
        "--source-revision", context["source"], "--base-revision", base, "--workflow-identity", context["workflow_identity"],
        "--entry", "ForgeTrust.AppSurface.Cli.dll", "--entry-sha256", build["tool"]["files"]["ForgeTrust.AppSurface.Cli.dll"]["sha256"],
        "--runtime-host", "dotnet", "--n02-uid", str(uid), "--n02-gid", str(gid),
        "--n03-helper-root", str(peer["root"]), "--n03-helper-map", str(peer["map"]), "--n03-helper-map-sha256", peer["map_sha256"],
        "--n03-helper-nodes", str(peer["nodes"]), "--n03-helper-nodes-sha256", peer["nodes_sha256"],
        "--n03-helper-entry-sha256", peer["entry_sha256"], "--n03-helper-generation", peer["generation"]]
    fixture += extra_flags
    need(remaining(deadline) > 665, "original-fixture-retention-reserve")
    fixture_error, archive = None, None
    try:
        runner.phase = "actual-" + case
        dispatch = read(reviewed / "dispatch.sh", 131072, deadline=deadline).decode()
        code, _ = runner.run(native.ROOT_PREFIX + ["-c", dispatch, "--", context["generation"],
            fixture[2]] + fixture, min(deadline - 35, time.monotonic() + 630), allow_failure=True)
        need(code == 0, "actual-fixture-exit")
    except BaseException as caught:
        fixture_error = caught
    finally:
        if deadline - time.monotonic() > 35:
            try:
                runner.phase = "private-retention"
                retain_end = min(deadline - 5, time.monotonic() + 30)
                _, retention_marker = runner.run(native.ROOT_PREFIX + [str(reviewed / "retain-native.sh"), "--execute",
                    "--generation", context["generation"], "--deadline-monotonic-ms", native.root_deadline(retain_end),
                    "--reviewed-script-sha256", sha(read(reviewed / "retain-native.sh", 131072, deadline=retain_end))], retain_end)
                need(retention_marker.startswith(b"NATIVE_RETENTION_COMPLETED:DATA_ONLY:FILES="), "retention-terminal-marker")
                archive = read(control / "native.tar", MAX_ARCHIVE, deadline=retain_end)
            except BaseException:
                if fixture_error is None:
                    raise
        if fixture_error is not None:
            raise fixture_error
    need(archive is not None, "retention-missing")
    records = load(HERE / ("records-" + case.lower() + ".py"), capture, source_root, deadline=deadline)
    values = unpack(archive, records.ARCHIVE_NAMES)
    save_archive(output / "native-private.tar", archive, deadline=deadline)
    retained = output / "retained"
    retained.mkdir(mode=0o700)
    for name, raw in values.items():
        path = retained / name
        path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        owner.save(path, raw)
    packet = {"schema": "issue779-checkpoint1-native-family-v1", "source": context["source"], "selector": case,
        "input_generation": context["generation"], "source_capture_sha256": context["capture_sha256"],
        "build_receipt_sha256": context["build_receipt_sha256"], "base_revision": base,
        "procedure_sha256": fixture[2], "archive_sha256": sha(archive),
        "files": {n: {"bytes": len(v), "sha256": sha(v)} for n, v in values.items()}, "commands": runner.records}
    result = validate_packet(context, packet, prepared["capture_raw"], prepared["build_raw"], values,
                             archive_raw=archive, deadline=deadline)
    for name in (("N01", "N02", "N03") if case == "None" else (case,)):
        owner.save(output / (name + "-evidence.json"), canonical(packet))
    return result


def verify_packet(context, *, deadline):
    """No process/repair: bind retained actual leaves and repeat original data predicates."""
    output = Path(context["output"])
    packet = decode(read(output / (("N01" if context["selector"] == "None" else context["selector"]) + "-evidence.json"), MAX_JSON, deadline=deadline))
    capture_raw = read(context["capture_path"], MAX_JSON, deadline=deadline)
    build_raw = read(context["build_receipt_path"], MAX_JSON, deadline=deadline)
    capture, build = decode(capture_raw), decode(build_raw)
    records = load(HERE / ("records-" + context["selector"].lower() + ".py"), capture,
                   Path(build["source_root"]), deadline=deadline)
    need(set(packet["files"]) <= records.ARCHIVE_NAMES, "aggregate-fixed-leaves")
    values = {name: read(output / "retained" / name, MAX_JSON, deadline=deadline) for name in packet["files"]}
    verify_command_logs(output, packet["commands"], deadline=deadline)
    archive = read(output / "native-private.tar", MAX_ARCHIVE, deadline=deadline)
    return validate_packet(context, packet, capture_raw, build_raw, values, archive_raw=archive, deadline=deadline)
