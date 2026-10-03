"""Internal root-owned application lane. Production registration is deliberately empty.

Candidate audits are data controls, never enrollment, admission, or lease issuance.
See tests/evidencehost-consumer/LinuxApplication-README.md for the closed contract.
"""
from __future__ import annotations

import dataclasses
import ctypes
import contextlib
import hashlib
import json
import math
import multiprocessing
import os
import platform
from pathlib import Path
import re
import socket
import stat
import struct
import subprocess
import sys
import threading
import time

MAX_METADATA = 1024 * 1024
MAX_FILE = 128 * 1024 * 1024
MAX_BUNDLE = 512 * 1024 * 1024
MAX_JOB_OUTPUT = 16 * 1024 * 1024
MAX_PROTECTED_TOOL = 64 * 1024 * 1024
HTTP_LIMIT = 4096
APPHOST_KIND = "RestrictedAspireAppHostV1"
RESOURCE_KIND = "NativeHttpResourceV1"
_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,95}\Z")
_HASH = re.compile(r"[0-9a-f]{64}\Z")
_LEASE = re.compile(r"[0-9a-f]{32}\Z")
_ROLES = {"AppHost": "apphost", "AppHostRuntimeConfiguration": "apphost_runtime_configuration",
          "Resource": "resource", "ResourceRuntimeConfiguration": "resource_runtime_configuration",
          "Dcp": "dcp", "DcpExtension": "dcp_extension", "Dependency": "dependency",
          "DeclaredInput": "declared_input", "DependencyManifest": "dependency_manifest"}
_ENV = {"PATH": "/usr/bin:/bin", "HOME": "/nonexistent", "LANG": "C.UTF-8"}
JOIN_PHASES = frozenset(("unknown", "not-started", "receipt-check", "watchdog-live", "active-operations",
                        "no-process", "terminate-main", "process-group-wait", "force-stop", "process-wait",
                        "group-check", "pump-join", "exit-check", "watchdog-disarm", "bundle-recheck",
                        "probe-recheck", "final-check", "complete"))
JOIN_DIAGNOSTIC_BOOLEANS = ("application_state_failed", "application_observed_group", "application_stdout_eof",
                           "application_stderr_eof", "application_pumps_error_free", "application_watchdog_disarmed")
JOIN_DIAGNOSTIC_INTEGERS = {"application_active_operations": (0, 1048576), "application_process_code": (-255, 255)}


def join_diagnostic_snapshot(state=None, pumps=None, watchdog=None):
    """Nonblocking, data-only observations; unknown is None, never an ownership ACK.

    Busy state/pump locks are skipped. No polling, syscall, callback, wait or text
    extraction occurs, so diagnostics cannot extend a cleanup deadline.
    """
    result = {"join_phase": "not-started", **dict.fromkeys(JOIN_DIAGNOSTIC_BOOLEANS),
              **dict.fromkeys(JOIN_DIAGNOSTIC_INTEGERS)}
    try:
        if type(state) is OwnershipState:
            phase = state.join_phase
            result["join_phase"] = phase if type(phase) is str and phase in JOIN_PHASES else "unknown"
            if state.condition.acquire(blocking=False):
                try:
                    values = {"application_state_failed": state.failed,
                              "application_observed_group": state.observed_group,
                              "application_active_operations": state.active,
                              "application_process_code": None if state.process is None else state.process.returncode}
                    for key, value in values.items():
                        bounds = JOIN_DIAGNOSTIC_INTEGERS.get(key)
                        if (bounds and type(value) is int and bounds[0] <= value <= bounds[1]) or (
                                not bounds and type(value) is bool): result[key] = value
                finally: state.condition.release()
        if type(pumps) is OutputPumps and pumps.lock.acquire(blocking=False):
            try:
                if len(pumps.eof) == 2 and all(type(x) is bool for x in pumps.eof):
                    result["application_stdout_eof"], result["application_stderr_eof"] = pumps.eof
                if len(pumps.errors) == 2 and all(type(x) is bool for x in pumps.errors):
                    result["application_pumps_error_free"] = not any(pumps.errors)
            finally: pumps.lock.release()
        if type(watchdog) is WatchdogOwnership and type(watchdog.disarmed) is bool:
            result["application_watchdog_disarmed"] = watchdog.disarmed
    except Exception:
        pass  # A diagnostic observation cannot replace the original failure.
    return result


class ApplicationError(RuntimeError):
    """Closed diagnostics; exception values never enter a broker response."""
    def __init__(self, code="ASEVD410"):
        self.code = code if code in ("ASEVD404", "ASEVD407", "ASEVD410", "ASEVD420") else "ASEVD410"
        super().__init__(self.code)


def _require(value, code="ASEVD404"):
    if not value:
        raise ApplicationError(code)


def _integer(value, low, high):
    _require(type(value) is int and low <= value <= high)
    return value


def _name(value):
    _require(type(value) is str and _ID.fullmatch(value))
    return value


def _relative(value):
    _require(type(value) is str and 0 < len(value) <= 256 and
             re.fullmatch(r"[A-Za-z0-9._/-]+", value) and
             all(part not in ("", ".", "..") for part in value.split("/")))
    return value


def _object(value, required, optional=()):
    _require(type(value) is dict and set(required) <= set(value) <= set(required) | set(optional))
    return value


def _json(raw):
    _require(type(raw) is bytes and 0 < len(raw) <= MAX_METADATA)
    def unique(pairs):
        result = {}; seen = set()
        for key, value in pairs:
            _require(key.casefold() not in seen)
            seen.add(key.casefold()); result[key] = value
        return result
    try:
        value = json.loads(raw, object_pairs_hook=unique, parse_constant=lambda _: _require(False))
        def bounded(item, depth=0):
            _require(depth <= 32)
            if type(item) is dict:
                for key, child in item.items():
                    _require(len(key) <= 128); bounded(child, depth + 1)
            elif type(item) is list:
                _require(len(item) <= 4096)
                for child in item: bounded(child, depth + 1)
            elif type(item) is str:
                _require(len(item) <= 4095 and not any(ord(c) < 32 for c in item))
            else:
                _require(item is None or type(item) in (bool, int, float))
                if type(item) is float: _require(math.isfinite(item))
        bounded(value)
        return value
    except (ValueError, UnicodeError, RecursionError):
        raise ApplicationError("ASEVD404") from None


@dataclasses.dataclass(frozen=True)
class BundleFile:
    """One exact compile-owned regular file; mode is 0444 or native 0555."""
    relative_path: str
    role: str
    length_bytes: int
    sha256: str
    mode: int


@dataclasses.dataclass(frozen=True)
class Capabilities:
    """Closed limits, with a single immutable declared input."""
    read_only_inputs: tuple[str, ...]
    scratch_bytes: int
    memory_bytes: int
    maximum_tasks: int
    maximum_output_bytes: int
    start_seconds: int
    stopping_seconds: int


@dataclasses.dataclass(frozen=True)
class Identities:
    """Three distinct UIDs and five distinct GIDs; supplied by the root account owner."""
    worker_uid: int
    worker_gid: int
    producer_uid: int
    producer_gid: int
    application_uid: int
    application_gid: int
    results_gid: int
    resource_access_gid: int

    def validate(self):
        for value in dataclasses.astuple(self): _integer(value, 1, 2**32 - 1)
        _require(len({self.worker_uid, self.producer_uid, self.application_uid}) == 3)
        _require(len({self.worker_gid, self.producer_gid, self.application_gid,
                      self.results_gid, self.resource_access_gid}) == 5)


@dataclasses.dataclass(frozen=True)
class CandidateAudit:
    """Immutable pure audit result. This type cannot create a RootApplicationLease."""
    canonical_entry: bytes
    descriptor_template: bytes
    entry_digest: str
    catalogue_digest: str
    policy_sha256: str
    profile_id: str
    application_id: str
    files: tuple[BundleFile, ...]
    capabilities: Capabilities
    resource_id: str
    resource_seconds: int

    def descriptor(self, identities: Identities):
        """Return a fresh 14-field metadata object, never authority or registration."""
        identities.validate()
        value = json.loads(self.descriptor_template)
        value.update(application_uid=identities.application_uid, application_gid=identities.application_gid,
                     results_gid=identities.results_gid, resource_access_gid=identities.resource_access_gid)
        return value


def _audit_candidate(canonical_entry: bytes, *, entry_digest: str, catalogue_digest: str,
                    policy_sha256: str) -> CandidateAudit:
    """Inspect C# canonical definition bytes without reserializing its digest or enrolling it."""
    _require(type(canonical_entry) is bytes)
    for digest in (entry_digest, catalogue_digest, policy_sha256):
        _require(type(digest) is str and _HASH.fullmatch(digest))
    _require(hashlib.sha256(canonical_entry).hexdigest() == entry_digest)
    entry = _object(_json(canonical_entry), ("Id", "Version", "BuildId", "AspireSdkVersion", "Policy",
                    "ProfileId", "Resources", "Producers", "BundleFiles", "Capabilities"), ("ChildKind",))
    for key in ("Id", "Version", "BuildId", "ProfileId"): _name(entry[key])
    _require(entry["AspireSdkVersion"] == "13.4.4")
    _require(entry.get("ChildKind", "RestrictedAspireAppHost") == "RestrictedAspireAppHost")
    resources = entry["Resources"]; producers = entry["Producers"]
    _require(type(resources) is list and len(resources) == 1 and type(producers) is list and 0 < len(producers) <= 32)
    resource = _object(resources[0], ("Declaration", "CapabilityClass", "CapabilityVersion", "ResourceName"), ("ChildKind",))
    _require(resource["CapabilityClass"] == "native-http-uds" and resource["CapabilityVersion"] == "1.0.0"
             and resource["ResourceName"] == "native-http" and resource.get("ChildKind", "NativeHttp") == "NativeHttp")
    declaration = _object(resource["Declaration"], ("Id", "Readiness", "DeadlineSeconds", "Requires"))
    _name(declaration["Id"]); _integer(declaration["DeadlineSeconds"], 1, 120)
    _require(declaration["Readiness"] == "aspire_health" and declaration["Requires"] == [])
    producer_declarations = []
    for producer in producers:
        producer = _object(producer, ("Declaration", "ImplementationId", "ImplementationVersion"))
        _require(producer["ImplementationId"] == "coverage" and producer["ImplementationVersion"] == "1.0.0")
        p = _object(producer["Declaration"], ("Id", "Kind", "Version", "RequiredResources", "AssertionIds",
                    "ArtifactSlots", "TimeoutSeconds", "CoverageGate"))
        _name(p["Id"]); _integer(p["TimeoutSeconds"], 1, 600)
        _require(p["Kind"] == "coverage" and p["Version"] == "1.0.0" and p["RequiredResources"] == [declaration["Id"]])
        _require(type(p["AssertionIds"]) is list and 0 < len(p["AssertionIds"]) <= 128
                 and len(set(p["AssertionIds"])) == len(p["AssertionIds"])
                 and all(type(x) is str and 0 < len(x) <= 128 for x in p["AssertionIds"]))
        _require(type(p["ArtifactSlots"]) is list and 0 < len(p["ArtifactSlots"]) <= 128)
        for slot in p["ArtifactSlots"]:
            _object(slot, ("LogicalName", "RelativeRoot", "MediaType", "Required", "MaximumBytes"))
            _name(slot["LogicalName"]); _relative(slot["RelativeRoot"])
            _integer(slot["MaximumBytes"], 0, 256 * 1024 * 1024)
            _require(type(slot["Required"]) is bool and type(slot["MediaType"]) is str and 0 < len(slot["MediaType"]) <= 128)
        gate = _object(p["CoverageGate"], ("MinLinePercent", "MinBranchPercent", "MinPatchLinePercent",
                    "MinPatchBranchPercent", "PatchLineMode", "TolerancePercent"))
        for key, number in gate.items():
            if key == "PatchLineMode": _require(number in ("measurable", "codecov"))
            elif number is None: _require(key in ("MinPatchLinePercent", "MinPatchBranchPercent"))
            else: _require(type(number) in (int, float) and 0 <= number <= 100)
        producer_declarations.append(p)
    _require(len({p["Id"] for p in producer_declarations}) == len(producer_declarations))
    policy = _object(entry["Policy"], ("Id", "Version", "ConservativeProfileId", "Profiles", "Rules"))
    _require(type(policy["Profiles"]) is list and 0 < len(policy["Profiles"]) <= 32
             and type(policy["Rules"]) is list and len(policy["Rules"]) <= 128)
    profiles = [p for p in policy["Profiles"] if type(p) is dict and p.get("Id") == entry["ProfileId"]]
    _require(len(profiles) == 1 and profiles[0].get("Resources") == [declaration]
             and profiles[0].get("Producers") == producer_declarations)
    files = []
    _require(type(entry["BundleFiles"]) is list and 6 <= len(entry["BundleFiles"]) <= 256)
    for file in entry["BundleFiles"]:
        _object(file, ("RelativePath", "Role", "LengthBytes", "Sha256", "Mode"))
        path = _relative(file["RelativePath"]); role = _ROLES.get(file["Role"])
        _require(role is not None); _integer(file["LengthBytes"], 1, MAX_FILE)
        _require(type(file["Sha256"]) is str and _HASH.fullmatch(file["Sha256"]))
        _require(type(file["Mode"]) is int and file["Mode"] == (0o555 if role in ("dcp", "dcp_extension") else 0o444))
        suffixes = {"apphost": ".dll", "resource": ".dll", "apphost_runtime_configuration": ".runtimeconfig.json",
                    "resource_runtime_configuration": ".runtimeconfig.json", "dependency_manifest": ".deps.json"}
        _require(role not in suffixes or path.endswith(suffixes[role]))
        _require(role != "dcp" or path == "dcp/dcp")
        _require(role != "dcp_extension" or path.startswith("dcp/ext/"))
        files.append(BundleFile(path, role, file["LengthBytes"], file["Sha256"], file["Mode"]))
    names = [f.relative_path.lower() for f in files]
    _require(len(set(names)) == len(names) and sum(f.length_bytes for f in files) <= MAX_BUNDLE)
    _require(not any(a != b and b.startswith(a + "/") for a in names for b in names))
    for role in ("apphost", "apphost_runtime_configuration", "resource", "resource_runtime_configuration", "dcp", "declared_input"):
        _require(sum(f.role == role for f in files) == 1)
    _require(next(f.relative_path for f in files if f.role == "resource") == "resource/NativeHttpResource.dll")
    _require("/" not in next(f.relative_path for f in files if f.role == "apphost"))
    caps = _object(entry["Capabilities"], ("ReadOnlyInputs", "ScratchBytes", "MemoryBytes", "MaximumTasks",
                    "MaximumOutputBytes", "StartSeconds", "StoppingSeconds"))
    _require(caps["ReadOnlyInputs"] == [next(f.relative_path for f in files if f.role == "declared_input")])
    limits = (("ScratchBytes", 2**30), ("MemoryBytes", 2**30), ("MaximumTasks", 128),
              ("MaximumOutputBytes", 2**20), ("StartSeconds", 120), ("StoppingSeconds", 30))
    for key, maximum in limits: _integer(caps[key], 1, maximum)
    capabilities = Capabilities(tuple(caps["ReadOnlyInputs"]), *(caps[k] for k, _ in limits))
    def snake(key): return re.sub(r"(?<!^)(?=[A-Z])", "_", key).lower()
    def wire(value):
        if type(value) is dict: return {snake(k): wire(v) for k, v in value.items()}
        if type(value) is list: return [wire(v) for v in value]
        return value
    template = {"application_id": entry["Id"], "application_version": entry["Version"], "build_id": entry["BuildId"],
                "catalogue_digest": catalogue_digest, "entry_digest": entry_digest, "aspire_sdk_version": "13.4.4",
                "resources": [wire(declaration)], "producers": wire(producer_declarations),
                "bundle_files": [dataclasses.asdict(f) for f in files], "capabilities": dataclasses.asdict(capabilities)}
    return CandidateAudit(bytes(canonical_entry), json.dumps(template, separators=(",", ":")).encode(),
                          entry_digest, catalogue_digest, policy_sha256, entry["ProfileId"], entry["Id"],
                          tuple(files), capabilities, declaration["Id"], declaration["DeadlineSeconds"])


def audit_candidate(canonical_entry: bytes, *, entry_digest: str, catalogue_digest: str,
                    policy_sha256: str) -> CandidateAudit:
    """Pure, fixed-error candidate audit; never select, enroll, execute, or create a lease."""
    try:
        return _audit_candidate(canonical_entry, entry_digest=entry_digest,
                                catalogue_digest=catalogue_digest, policy_sha256=policy_sha256)
    except (TypeError, ValueError, KeyError, IndexError, OverflowError, RecursionError):
        raise ApplicationError("ASEVD404") from None


@dataclasses.dataclass(frozen=True)
class _CompiledRegistration:
    canonical_entry: bytes
    entry_digest: str
    catalogue_digest: str
    policy_sha256: str


# Only reviewed source/build registration can change this tuple. No loader or enrollment API exists.
_COMPILED_REGISTRATIONS: tuple[_CompiledRegistration, ...] = ()


@dataclasses.dataclass(frozen=True)
class _SelectedRegistration:
    registration: _CompiledRegistration
    audit: CandidateAudit


def select_registration(application_id: str, entry_digest: str, *, policy_sha256: str,
                        profile_id: str) -> _SelectedRegistration:
    """Select exact compile-owned metadata; empty production always returns ASEVD407."""
    for registration in _COMPILED_REGISTRATIONS:
        audit = audit_candidate(registration.canonical_entry, entry_digest=registration.entry_digest,
                                catalogue_digest=registration.catalogue_digest, policy_sha256=registration.policy_sha256)
        if (audit.application_id, audit.entry_digest, audit.policy_sha256, audit.profile_id) == (
                application_id, entry_digest, policy_sha256, profile_id):
            return _SelectedRegistration(registration, audit)
    raise ApplicationError("ASEVD407")


def _identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid, info.st_nlink,
            info.st_size, info.st_mtime_ns, info.st_ctime_ns)


class _OpenHow(ctypes.Structure):
    _fields_ = [("flags", ctypes.c_uint64), ("mode", ctypes.c_uint64), ("resolve", ctypes.c_uint64)]


def _open_at(fd, name, flags, *, beneath=True):
    if sys.platform != "linux":
        # Portable descriptor-audit procedure only. RootApplicationLease rejects
        # this platform and never substitutes it for mandatory Linux openat2.
        return os.open(name, flags | os.O_NOFOLLOW, dir_fd=fd)
    _require(platform.machine() == "x86_64", "ASEVD410")
    how = _OpenHow(flags | os.O_CLOEXEC, 0, 0x0f if beneath else 0x06)
    libc = ctypes.CDLL(None, use_errno=True)
    result = libc.syscall(ctypes.c_long(437), ctypes.c_int(fd), ctypes.c_char_p(os.fsencode(name)),
                          ctypes.byref(how), ctypes.c_size_t(ctypes.sizeof(how)))
    if result < 0: raise ApplicationError("ASEVD410")
    return result


def _open_directory(path: Path):
    _require(path.is_absolute() and ".." not in path.parts and not any(c.isspace() for c in str(path)))
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        for part in path.parts[1:]:
            next_fd = _open_at(fd, part, os.O_RDONLY | os.O_DIRECTORY, beneath=False)
            os.close(fd); fd = next_fd
        result, fd = fd, -1
        return result
    finally:
        if fd >= 0: os.close(fd)


@dataclasses.dataclass(frozen=True)
class _BundleBinding:
    entry_digest: str
    files: tuple[BundleFile, ...]


class PinnedBundle:
    """Retained descriptors; successful close is idempotent, failed close stays failed."""
    def __init__(self, path, root_fd, root_identity, files, candidate):
        self.path, self.root_fd, self.root_identity, self.files = path, root_fd, root_identity, tuple(files)
        self._binding = _BundleBinding(candidate.entry_digest, tuple(candidate.files))
        self._close_lock = threading.Lock(); self._close_error = None

    def require_binding(self, candidate):
        """Data-only equality guard; a different audited candidate is not interchangeable."""
        _require(self._binding == _BundleBinding(candidate.entry_digest, tuple(candidate.files)), "ASEVD410")

    def verify_candidate(self, candidate, *, deadline=None, expected_owner_uid=0):
        """Rehash the actual named bundle against the exact selected declaration, then recheck held FDs."""
        self.require_binding(candidate)
        with audit_bundle(self.path, candidate, expected_owner_uid=expected_owner_uid, deadline=deadline) as actual:
            _require(actual.root_identity == self.root_identity and
                     tuple(sorted((name, identity) for name, _, identity in actual.files)) ==
                     tuple(sorted((name, identity) for name, _, identity in self.files)), "ASEVD410")
        self.recheck(expected_owner_uid=expected_owner_uid, deadline=deadline)

    def recheck(self, *, expected_owner_uid=0, deadline=None):
        _audit_clock(deadline)
        _require(self.root_fd >= 0 and _identity(os.fstat(self.root_fd)) == self.root_identity)
        _require(_identity(self.path.lstat()) == self.root_identity)
        _require(self.root_identity[3] == expected_owner_uid)
        for name, fd, identity in self.files:
            _audit_clock(deadline)
            _require(_identity(os.fstat(fd)) == identity and identity[3] == expected_owner_uid)
            parent = os.dup(self.root_fd)
            try:
                for part in name.split("/")[:-1]:
                    _audit_clock(deadline)
                    child = _open_at(parent, part, os.O_RDONLY | os.O_DIRECTORY)
                    os.close(parent); parent = child
                    info = os.fstat(parent)
                    _require(info.st_uid == expected_owner_uid and not info.st_mode & 0o222
                             and info.st_dev == self.root_identity[0])
                _require(_identity(os.stat(name.split("/")[-1], dir_fd=parent, follow_symlinks=False)) == identity)
            finally: os.close(parent)
        _audit_clock(deadline)

    def close(self):
        with self._close_lock:
            if self.root_fd >= 0:
                descriptors = [self.root_fd, *(fd for _, fd, _ in self.files)]
                self.root_fd = -1
                for fd in descriptors:
                    try: os.close(fd)
                    except BaseException as error:
                        if self._close_error is None: self._close_error = error
            if self._close_error is not None: raise self._close_error

    def __enter__(self): return self
    def __exit__(self, *unused): self.close()


def _audit_clock(deadline):
    if deadline is not None:
        _require(type(deadline) in (int, float) and math.isfinite(deadline), "ASEVD410")
        _remaining(deadline)


def audit_bundle(path: Path, candidate: CandidateAudit, *, expected_owner_uid=0, deadline=None) -> PinnedBundle:
    """Read actual regular files through no-follow descriptors; owner seam is audit-only."""
    root_fd = -1; retained = []
    try:
        _audit_clock(deadline)
        root_fd = _open_directory(path)
        root_stat = os.fstat(root_fd); device = root_stat.st_dev
        def directory(info):
            _require(stat.S_ISDIR(info.st_mode) and info.st_uid == expected_owner_uid
                     and not info.st_mode & 0o222 and info.st_dev == device)
        directory(root_stat)
        expected = {f.relative_path: f for f in candidate.files}; observed = set(); entries = 0
        def walk(fd, prefix="", depth=0):
            nonlocal entries
            _audit_clock(deadline)
            _require(depth <= 32)
            for name in os.listdir(fd):
                _audit_clock(deadline)
                entries += 1; _require(entries <= 4096)
                relative = prefix + name; _relative(relative)
                before = os.stat(name, dir_fd=fd, follow_symlinks=False)
                if stat.S_ISDIR(before.st_mode):
                    child = _open_at(fd, name, os.O_RDONLY | os.O_DIRECTORY)
                    try:
                        _require(_identity(before) == _identity(os.fstat(child))); directory(before)
                        walk(child, relative + "/", depth + 1)
                        _require(_identity(before) == _identity(os.fstat(child)))
                    finally: os.close(child)
                else:
                    _require(relative in expected and stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                             and before.st_uid == expected_owner_uid and before.st_dev == device)
                    item = expected[relative]
                    _require(stat.S_IMODE(before.st_mode) == item.mode and before.st_size == item.length_bytes)
                    file_fd = _open_at(fd, name, os.O_RDONLY | os.O_NONBLOCK)
                    try:
                        _require(_identity(before) == _identity(os.fstat(file_fd)))
                        digest = hashlib.sha256(); count = 0
                        while True:
                            _audit_clock(deadline)
                            block = os.read(file_fd, min(65536, item.length_bytes + 1 - count))
                            if not block: break
                            count += len(block); _require(count <= item.length_bytes); digest.update(block)
                        _require(count == item.length_bytes and digest.hexdigest() == item.sha256
                                 and _identity(before) == _identity(os.fstat(file_fd))
                                 and _identity(before) == _identity(os.stat(name, dir_fd=fd, follow_symlinks=False)))
                        retained.append((relative, file_fd, _identity(before))); file_fd = -1; observed.add(relative)
                    finally:
                        if file_fd >= 0: os.close(file_fd)
        walk(root_fd)
        _require(observed == set(expected) and _identity(root_stat) == _identity(os.fstat(root_fd)))
        result = PinnedBundle(path, root_fd, _identity(root_stat), retained, candidate)
        result.recheck(expected_owner_uid=expected_owner_uid, deadline=deadline)
        root_fd = -1; retained = []
        return result
    except (OSError, ValueError):
        raise ApplicationError("ASEVD404") from None
    finally:
        if root_fd >= 0: os.close(root_fd)
        for _, fd, _ in retained: os.close(fd)


@dataclasses.dataclass(frozen=True)
class Workspace:
    """Root-created host-visible layout and denial roots, not caller request paths."""
    parent: Path
    scratch: Path
    control: Path
    protected_tools: Path
    protected_output: Path
    broker_control: Path
    producer_scratch: Path
    source_subject: Path


@dataclasses.dataclass(frozen=True)
class _ProtectedProbeSnapshot:
    tools: tuple
    output: tuple
    tool: tuple
    tool_sha256: str


def validate_managed_tool(data: bytes):
    """Bounded PE/CLR structure check, not Assembly.Load or tool execution."""
    _require(type(data) is bytes and 64 <= len(data) <= MAX_PROTECTED_TOOL and data[:2] == b"MZ", "ASEVD410")
    def u16(offset): return struct.unpack_from("<H", data, offset)[0]
    def u32(offset): return struct.unpack_from("<I", data, offset)[0]
    pe = u32(60)
    _require(64 <= pe <= len(data) - 24 and data[pe:pe + 4] == b"PE\0\0", "ASEVD410")
    count, optional_size = u16(pe + 6), u16(pe + 20)
    optional = pe + 24; end = optional + optional_size
    _require(1 <= count <= 96 and 96 <= optional_size <= 4096 and end + count * 40 <= len(data), "ASEVD410")
    magic = u16(optional)
    _require(magic in (0x10b, 0x20b), "ASEVD410")
    directories = optional + (96 if magic == 0x10b else 112)
    _require(directories + 15 * 8 <= end and u32(directories - 4) >= 15, "ASEVD410")
    def mapped(rva, size):
        _require(rva > 0 and size > 0, "ASEVD410")
        for index in range(count):
            section = end + index * 40
            address, raw_size, raw_start = u32(section + 12), u32(section + 16), u32(section + 20)
            delta = rva - address
            if 0 <= delta and delta + size <= raw_size and raw_start + delta + size <= len(data):
                return raw_start + delta
        raise ApplicationError("ASEVD410")
    clr_rva, clr_size = u32(directories + 14 * 8), u32(directories + 14 * 8 + 4)
    _require(clr_size >= 72, "ASEVD410")
    clr = mapped(clr_rva, clr_size)
    _require(72 <= u32(clr) <= clr_size, "ASEVD410")
    _require(u32(clr + 12) >= 16, "ASEVD410")
    metadata = mapped(u32(clr + 8), u32(clr + 12))
    _require(data[metadata:metadata + 4] == b"BSJB", "ASEVD410")


def audit_protected_probes(workspace: Workspace, *, expected_owner_uid=0, deadline=None):
    """Read-only fixed-name FD controls. Owner seam cannot select, enroll or issue a lease."""
    try:
        _audit_clock(deadline)
        with contextlib.ExitStack() as cleanup:
            identities = []
            for path in (workspace.protected_tools, workspace.protected_output):
                fd = _open_directory(path); cleanup.callback(os.close, fd)
                info = os.fstat(fd)
                _require(stat.S_ISDIR(info.st_mode) and info.st_uid == expected_owner_uid
                         and not info.st_mode & 0o022 and _identity(path.lstat()) == _identity(info), "ASEVD410")
                identities.append((fd, _identity(info)))
            tools_fd, tools_identity = identities[0]; output_fd, output_identity = identities[1]
            tool_fd = _open_at(tools_fd, "protected-tool.dll", os.O_RDONLY | os.O_NONBLOCK)
            cleanup.callback(os.close, tool_fd)
            info = os.fstat(tool_fd); tool_identity = _identity(info)
            _require(stat.S_ISREG(info.st_mode) and info.st_uid == expected_owner_uid and info.st_nlink == 1
                     and not info.st_mode & 0o022 and 0 < info.st_size <= MAX_PROTECTED_TOOL, "ASEVD410")
            chunks = []; received = 0
            while True:
                _audit_clock(deadline)
                chunk = os.read(tool_fd, min(65536, MAX_PROTECTED_TOOL + 1 - received))
                if not chunk: break
                received += len(chunk); _require(received <= MAX_PROTECTED_TOOL, "ASEVD410"); chunks.append(chunk)
            _require(received == info.st_size and _identity(os.fstat(tool_fd)) == tool_identity
                     and _identity(os.stat("protected-tool.dll", dir_fd=tools_fd, follow_symlinks=False)) == tool_identity,
                     "ASEVD410")
            data = b"".join(chunks); validate_managed_tool(data)
            try: os.stat("native-resource-output-probe", dir_fd=output_fd, follow_symlinks=False)
            except FileNotFoundError: pass
            else: raise ApplicationError("ASEVD410")
            _require(_identity(os.fstat(tools_fd)) == tools_identity and
                     _identity(workspace.protected_tools.lstat()) == tools_identity and
                     _identity(os.fstat(output_fd)) == output_identity and
                     _identity(workspace.protected_output.lstat()) == output_identity, "ASEVD410")
            _audit_clock(deadline)
            return _ProtectedProbeSnapshot(tools_identity, output_identity, tool_identity, hashlib.sha256(data).hexdigest())
    except Exception:
        raise ApplicationError("ASEVD410") from None


def _overlap(a, b): return a == b or a in b.parents or b in a.parents


def validate_workspace(workspace: Workspace, bundle: PinnedBundle, ids: Identities, lease_id: str,
                       capabilities: Capabilities):
    """Require protected /run ancestors and an independently size-bounded host tmpfs scratch."""
    ids.validate(); _require(_LEASE.fullmatch(lease_id))
    _require(workspace.parent == Path("/run") / ("issue779-app-" + lease_id)
             and workspace.scratch == workspace.parent / "scratch" and workspace.control == workspace.parent / "control"
             and bundle.path == workspace.parent / "bundle")
    for path, owner, gid, mode in ((workspace.parent, 0, 0, 0o711),
                                 (workspace.control, 0, 0, 0o700),
                                 (workspace.scratch, ids.application_uid, ids.application_gid, 0o700)):
        fd = _open_directory(path)
        try:
            info = os.fstat(fd)
            _require((info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode)) == (owner, gid, mode))
        finally: os.close(fd)
    for denied in (workspace.protected_tools, workspace.protected_output, workspace.broker_control,
                   workspace.producer_scratch, workspace.source_subject):
        _require(denied.is_absolute() and not _overlap(workspace.parent, denied))
        fd = _open_directory(denied); os.close(fd)
    mount = str(workspace.scratch)
    lines = Path("/proc/self/mountinfo").read_text().splitlines()
    _require(any(line.split()[4] == mount and line.split(" - ", 1)[1].split()[0] == "tmpfs" for line in lines))
    size = os.statvfs(workspace.scratch)
    _require(0 < size.f_blocks * size.f_frsize <= capabilities.scratch_bytes)
    bundle.recheck()


def closed_command(candidate: CandidateAudit, workspace: Workspace, dotnet: Path, ids: Identities,
                   lease_id: str, deadline: float) -> tuple[str, ...]:
    """Pure argv control for the two fixed kinds. Calling this does not start a process."""
    ids.validate(); _require(_LEASE.fullmatch(lease_id))
    _require(dotnet.is_absolute() and not any(c.isspace() for c in str(dotnet)))
    _require(all(not _overlap(dotnet, p) for p in (workspace.protected_tools, workspace.protected_output,
                  workspace.broker_control, workspace.producer_scratch, workspace.source_subject, workspace.parent)))
    remaining = deadline - time.monotonic(); _require(remaining > 0, "ASEVD410")
    caps = candidate.capabilities; bundle = workspace.parent / "bundle"
    entry = next(f.relative_path for f in candidate.files if f.role == "apphost")
    properties = {"Type": "exec", "User": str(ids.application_uid), "Group": str(ids.application_gid),
                  "SupplementaryGroups": "", "KillMode": "control-group", "SendSIGKILL": "yes",
                  "TimeoutStopSec": str(caps.stopping_seconds), "RuntimeMaxSec": str(remaining),
                  "NoNewPrivileges": "yes", "CapabilityBoundingSet": "", "AmbientCapabilities": "",
                  "ProtectControlGroups": "yes", "ProtectSystem": "strict", "ProtectHome": "yes",
                  "PrivateNetwork": "yes", "PrivateTmp": "yes", "RestrictSUIDSGID": "yes",
                  "LimitCORE": "0", "UMask": "0077", "TasksMax": str(caps.maximum_tasks),
                  "MemoryMax": str(caps.memory_bytes), "WorkingDirectory": str(workspace.scratch),
                  "ReadWritePaths": str(workspace.scratch), "ReadOnlyPaths": f"{bundle} {dotnet.parent}",
                  "InaccessiblePaths": " ".join(str(p) for p in (workspace.protected_tools, workspace.protected_output,
                                               workspace.broker_control, workspace.producer_scratch,
                                               workspace.source_subject)), "Restart": "no"}
    environment = {"HOME": str(workspace.scratch), "TMPDIR": str(workspace.scratch), "LANG": "C.UTF-8",
                   "ASPIRE__STORE__PATH": str(workspace.scratch / ".aspire-store"), "DOTNET_NOLOGO": "1",
                   "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                   "PROOF_PROTECTED_TOOLS": str(workspace.protected_tools),
                   "PROOF_PROTECTED_OUTPUT": str(workspace.protected_output),
                   "PROOF_ALLOWED_INPUT": str(bundle / caps.read_only_inputs[0])}
    return ("/usr/bin/systemd-run", "--quiet", "--wait", "--pipe", "--expand-environment=no",
            "--unit=issue779-app-" + lease_id + ".service", *(f"--property={k}={v}" for k, v in properties.items()),
            "/usr/bin/env", "-i", *(f"{k}={v}" for k, v in environment.items()),
            str(dotnet), str(bundle / entry), "--scratch", str(workspace.scratch), "--case", "normal")


class JobOutputCounter:
    """Data-only shared received-byte counter for application and producer pumps."""
    def __init__(self):
        self._lock = threading.Lock(); self.total = 0; self.exceeded = threading.Event()

    def count(self, size):
        _require(type(size) is int and size >= 0, "ASEVD420")
        with self._lock:
            self.total += size
            if self.total > MAX_JOB_OUTPUT: self.exceeded.set()

    def received_bytes(self):
        with self._lock: return self.total


class OwnershipState:
    """Internal data-only testing seam. It cannot select a registration or launch work."""
    def __init__(self):
        self.condition = threading.Condition(); self.closed = False; self.failed = False
        self.claimed = False; self.active = 0; self.process = None; self.observed_group = False; self.loaded_seen = False
        self.first_join_failure = None
        self.join_phase = "not-started"

    def fail_join(self, error, abort):
        """Latch the first internal failure before abort; exception bytes are not diagnostics."""
        with self.condition:
            if self.first_join_failure is None: self.first_join_failure = error
            self.closed = True; self.failed = True; self.condition.notify_all()
        try: abort.set()
        except BaseException: pass  # Abort failure cannot replace the first join failure.

    def require_final(self, deadline, abort):
        """Final receipt guard after potentially blocking acknowledgement and rechecks."""
        _remaining(deadline)
        with self.condition: _require(not self.failed and not abort.is_set(), "ASEVD410")

    @contextlib.contextmanager
    def joining(self, abort):
        """Data-only join guard; a later cleanup attempt cannot upgrade failed ownership."""
        with self.condition:
            error = self.first_join_failure
        if error is not None: raise error
        try:
            yield
        except BaseException as error:
            self.fail_join(error, abort)
            raise

    def begin_start(self):
        with self.condition:
            _require(not self.closed and not self.claimed, "ASEVD410")
            self.claimed = True; self.active += 1

    def begin_resource(self):
        with self.condition:
            _require(not self.closed and self.process is not None, "ASEVD410"); self.active += 1

    def end_operation(self):
        with self.condition:
            self.active -= 1; self.condition.notify_all()

    def close(self, failed=False):
        with self.condition:
            self.closed = True; self.failed |= failed; self.condition.notify_all()

    def attach_process(self, process):
        # Caller holds this same condition across the closed check and actual Popen.
        with self.condition:
            _require(self.claimed and not self.closed and self.process is None, "ASEVD410")
            self.process = process


class OutputPumps:
    """Two actual binary EOF receipts; no retained child text and no callback substitution."""
    def __init__(self, maximum, job_counter: JobOutputCounter, state: OwnershipState, abort):
        _integer(maximum, 1, 2**20); _require(type(job_counter) is JobOutputCounter)
        self.maximum, self.job_counter, self.state, self.abort = maximum, job_counter, state, abort
        self.lock = threading.Lock(); self.counts = [0, 0]; self.eof = [False, False]; self.errors = [False, False]
        self.threads = []; self.exceeded = False

    def run(self, index, stream):
        try:
            while True:
                chunk = stream.read(65536)
                if type(chunk) is not bytes: raise ApplicationError()
                if not chunk:
                    with self.lock: self.eof[index] = True
                    break
                with self.lock:
                    self.counts[index] += len(chunk)
                    self.job_counter.count(len(chunk))
                    self.exceeded |= sum(self.counts) > self.maximum or self.job_counter.exceeded.is_set()
                    if self.exceeded: self.state.close(True); self.abort.set()
        except Exception:
            with self.lock: self.errors[index] = True
            self.state.close(True); self.abort.set()
        finally:
            try: stream.close()
            except Exception:
                with self.lock: self.errors[index] = True
                self.state.close(True); self.abort.set()

    def start(self, process):
        for index, stream in enumerate((process.stdout, process.stderr)):
            thread = threading.Thread(target=self.run, args=(index, stream), daemon=True)
            self.threads.append(thread); thread.start()

    def join(self, deadline):
        for thread in self.threads: thread.join(timeout=max(0, deadline - time.monotonic()))
        with self.lock:
            _require(len(self.threads) == 2 and not any(t.is_alive() for t in self.threads)
                     and all(self.eof) and not any(self.errors) and not self.exceeded, "ASEVD410")
            return (sum(self.counts), *self.counts)


def _remaining(deadline):
    value = deadline - time.monotonic()
    _require(value > 0, "ASEVD410")
    return value


def _systemctl(unit, arguments, deadline):
    _require(re.fullmatch(r"issue779-app-[0-9a-f]{32}\.service", unit), "ASEVD410")
    return subprocess.run(["/usr/bin/systemctl", *arguments, unit], env=_ENV, stdin=subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=min(2, _remaining(deadline)), check=False)


def _properties(unit, deadline):
    names = ("LoadState", "Type", "ActiveState", "SubState", "MainPID", "ControlGroup", "User", "Group", "KillMode",
             "ExecMainCode", "ExecMainStatus", "Result")
    result = _systemctl(unit, ["show", "--no-pager", *("--property=" + n for n in names)], deadline)
    _require(result.returncode == 0 and len(result.stdout) <= 4096, "ASEVD410")
    values = {}
    for line in result.stdout.decode("ascii").splitlines():
        pair = line.split("=", 1); _require(len(pair) == 2 and pair[0] in names and pair[0] not in values, "ASEVD410")
        values[pair[0]] = pair[1]
    _require(set(values) == set(names), "ASEVD410")
    return values


def classify_startup(properties, ids: Identities, group: str, *, loaded_seen=False):
    """Pure pending/running classifier. Only real kernel identity checks can acknowledge start."""
    p = properties
    if p.get("LoadState") == "not-found":
        _require(not loaded_seen and p.get("MainPID") == "0" and not p.get("ControlGroup")
                 and p.get("ActiveState") == "inactive" and p.get("SubState") == "dead", "ASEVD410")
        return "pending"
    _require(p.get("LoadState") == "loaded" and p.get("Type") == "exec"
             and p.get("User") == str(ids.application_uid) and p.get("Group") == str(ids.application_gid)
             and p.get("KillMode") == "control-group" and p.get("ControlGroup") in ("", group), "ASEVD410")
    _require(type(p.get("MainPID")) is str and p["MainPID"].isascii() and p["MainPID"].isdigit()
             and int(p["MainPID"]) <= 2**31 - 1, "ASEVD410")
    if p["ActiveState"] == "active" and p["SubState"] == "running":
        _require(int(p["MainPID"]) > 0 and p["ControlGroup"] == group, "ASEVD410")
        return "running"
    queued = (p["ActiveState"] == "inactive" and p["SubState"] == "dead" and p["MainPID"] == "0"
              and p.get("ExecMainCode") == "0" and p.get("ExecMainStatus") == "0" and not p["ControlGroup"])
    starting = p["ActiveState"] == "activating" and p["SubState"] in ("condition", "start-pre", "start", "start-post")
    _require(queued or starting, "ASEVD410")
    return "pending"


def _process_identity(pid, ids, group):
    """Actual four UID/GID values and unified kernel membership, never unit text alone."""
    _require(type(pid) is int and 0 < pid <= 2**31 - 1, "ASEVD410")
    status_path = Path(f"/proc/{pid}/status")
    with status_path.open("rb") as stream: status = stream.read(16385)
    _require(len(status) <= 16384, "ASEVD410")
    with Path(f"/proc/{pid}/cgroup").open("rb") as stream: memberships = stream.read(4097)
    validate_process_facts(status, memberships, ids, group)


def validate_process_facts(status: bytes, memberships: bytes, ids: Identities, group: str):
    """Internal pure parser controls; text never establishes a lease or kernel observation."""
    _require(type(status) is bytes and len(status) <= 16384 and type(memberships) is bytes
             and len(memberships) <= 4096, "ASEVD410")
    fields = {line.split(b":", 1)[0]: line.split(b":", 1)[1].split() for line in status.splitlines() if b":" in line}
    _require(fields.get(b"Uid") == [str(ids.application_uid).encode()] * 4
             and fields.get(b"Gid") == [str(ids.application_gid).encode()] * 4
             and fields.get(b"NoNewPrivs") == [b"1"] and fields.get(b"CapEff") == [b"0000000000000000"], "ASEVD410")
    rows = memberships.decode("ascii").splitlines()
    _require(len(rows) == 1 and rows[0].startswith("0::")
             and (rows[0][3:] == group or rows[0][3:].startswith(group + "/")), "ASEVD410")


def validate_http_response(data: bytes):
    """Pure fixed HTTP response check; no peer or readiness authority."""
    try:
        _require(type(data) is bytes and len(data) <= HTTP_LIMIT, "ASEVD410")
        header, body = data.split(b"\r\n\r\n", 1)
        _require(header.split(b"\r\n", 1)[0] == b"HTTP/1.1 200 OK" and body == b"native-http-ready", "ASEVD410")
        return len(data)
    except ValueError:
        raise ApplicationError("ASEVD410") from None


def _group_empty(group):
    _require(re.fullmatch(r"/system.slice/issue779-app-[0-9a-f]{32}\.service", group), "ASEVD410")
    path = Path("/sys/fs/cgroup") / group[1:]
    if not path.exists(): return True
    raw = (path / "cgroup.events").read_bytes(); _require(len(raw) <= 4096, "ASEVD410")
    fields = dict(line.split() for line in raw.decode("ascii").splitlines())
    _require(fields.get("populated") in ("0", "1"), "ASEVD410")
    return fields["populated"] == "0"


def watchdog_control(control, parent_control, deadline, abort, parent_pid):
    """Private fork/Pipe procedure seam, with no lease, unit or root-operation authority.

    Only a matching DISARM request before abort/deadline/parent loss can receive DONE.
    Startup reports the actual process identity; the root lease checks UID zero itself.
    """
    parent_control.close()
    try:
        pid = os.getpid()
        control.send_bytes(f"READY:{pid}:{os.geteuid()}".encode("ascii"))
        while True:
            if abort.is_set() or time.monotonic() >= deadline or os.getppid() != parent_pid:
                return False
            if control.poll(min(0.03, max(0, deadline - time.monotonic()))):
                request = control.recv_bytes(64)
                if request != f"DISARM:{pid}".encode("ascii"): return False
                if abort.is_set() or time.monotonic() >= deadline or os.getppid() != parent_pid:
                    return False
                control.send_bytes(f"DONE:{pid}".encode("ascii"))
                return True
    except (OSError, EOFError):
        return False
    finally: control.close()


def _watchdog(unit, deadline, abort, control, parent_control, parent_pid):
    """Root emergency ownership; only the private DONE handshake permits exit zero."""
    if watchdog_control(control, parent_control, deadline, abort, parent_pid): return
    try:
        cleanup = time.monotonic() + 5
        _systemctl(unit, ["stop", "--no-block"], cleanup)
        _systemctl(unit, ["kill", "--kill-whom=all", "--signal=KILL"], cleanup)
    finally: raise SystemExit(1)


class WatchdogOwnership:
    """Private process/pipe receipt seam; it cannot select or issue an application lease."""
    def __init__(self, process, control, abort=None):
        self.process, self.control = process, control
        self.abort = abort if abort is not None else threading.Event()
        self.ready = False; self.disarmed = False

    def startup(self, deadline, *, expected_uid=0):
        _require(not self.ready and self.control.poll(min(3, _remaining(deadline))), "ASEVD410")
        _require(self.control.recv_bytes(64) == f"READY:{self.process.pid}:{expected_uid}".encode("ascii"), "ASEVD410")
        self.require_live(); self.ready = True

    def require_live(self):
        _require(not self.disarmed and self.process.is_alive() and self.process.exitcode is None, "ASEVD410")

    def disarm(self, deadline):
        _require(self.ready and not self.disarmed, "ASEVD410")
        _remaining(deadline); _require(not self.abort.is_set(), "ASEVD410")
        self.require_live()
        _require(not self.control.poll(0), "ASEVD410")  # No unsolicited/stale DONE.
        self.control.send_bytes(f"DISARM:{self.process.pid}".encode("ascii"))
        _require(self.control.poll(_remaining(deadline)), "ASEVD410")
        _require(self.control.recv_bytes(64) == f"DONE:{self.process.pid}".encode("ascii"), "ASEVD410")
        self.process.join(timeout=_remaining(deadline))
        _require(not self.process.is_alive() and self.process.exitcode == 0, "ASEVD410")
        _remaining(deadline); _require(not self.abort.is_set(), "ASEVD410")
        self.disarmed = True; self.control.close()


class RootApplicationLease:
    """Only a contained compile-owned selection can own actual root operations.

    The launcher owns account creation/deletion, workspace mounts, bundle cleanup,
    worker authentication and aggregate finalization. This class owns the app lane.
    """
    def __init__(self, selected: _SelectedRegistration, identities: Identities, workspace: Workspace,
                 bundle: PinnedBundle, dotnet: Path, job_deadline: float, job_counter: JobOutputCounter):
        _require(type(selected) is _SelectedRegistration and any(selected.registration is x for x in _COMPILED_REGISTRATIONS), "ASEVD407")
        actual = audit_candidate(selected.registration.canonical_entry, entry_digest=selected.registration.entry_digest,
                                 catalogue_digest=selected.registration.catalogue_digest, policy_sha256=selected.registration.policy_sha256)
        _require(actual == selected.audit, "ASEVD407")
        _require(type(bundle) is PinnedBundle, "ASEVD410")
        bundle.require_binding(selected.audit)
        _require(sys.platform == "linux" and os.geteuid() == 0 and type(job_counter) is JobOutputCounter, "ASEVD410")
        self.lease_id = workspace.parent.name.removeprefix("issue779-app-")
        _require(_LEASE.fullmatch(self.lease_id), "ASEVD410")
        self.unit = "issue779-app-" + self.lease_id + ".service"
        self.group = "/system.slice/" + self.unit
        self.selected, self.ids, self.workspace, self.bundle = selected, identities, workspace, bundle
        self.dotnet, self.job_deadline, self.job_counter = dotnet, job_deadline, job_counter
        self.state = OwnershipState(); self.pumps = None; self.pid = 0; self.receipt = None
        self.abort = multiprocessing.Event(); self.monitor = None; self.watchdog = None
        self.probe_snapshot = None
        self.join_lock = threading.Lock()

    @property
    def failed(self):
        with self.state.condition: return self.state.failed

    def descriptor(self): return self.selected.audit.descriptor(self.ids)

    def _check_open(self, deadline):
        _remaining(min(deadline, self.job_deadline))
        with self.state.condition: _require(not self.state.closed and not self.state.failed, "ASEVD410")
        if self.watchdog is not None and self.watchdog.ready: self.watchdog.require_live()

    def start(self, application_id: str, entry_digest: str, deadline: float):
        """Exact seven-field ACK only after real exec/identity and watchdog ownership."""
        audit = self.selected.audit
        _require((application_id, entry_digest) == (audit.application_id, audit.entry_digest), "ASEVD410")
        deadline = min(deadline, self.job_deadline, time.monotonic() + audit.capabilities.start_seconds)
        self.state.begin_start()
        try:
            validate_workspace(self.workspace, self.bundle, self.ids, self.lease_id, audit.capabilities)
            self.bundle.verify_candidate(audit, deadline=deadline)
            self.probe_snapshot = audit_protected_probes(self.workspace, deadline=deadline)
            info = self.dotnet.lstat()
            _require(stat.S_ISREG(info.st_mode) and info.st_uid == 0 and not info.st_mode & 0o022
                     and info.st_mode & 0o111, "ASEVD410")
            parent_control, child_control = multiprocessing.Pipe(duplex=True)
            self.monitor = multiprocessing.Process(target=_watchdog, args=(self.unit, self.job_deadline,
                       self.abort, child_control, parent_control, os.getpid()), daemon=False)
            self.watchdog = WatchdogOwnership(self.monitor, parent_control, self.abort)
            self.monitor.start(); child_control.close()
            self.watchdog.startup(deadline)
            self._check_open(deadline)
            scratch_fd = _open_directory(self.workspace.scratch)
            try:
                fd = os.open("armed", os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o444, dir_fd=scratch_fd)
                try: _require(os.write(fd, b"root-watchdog-armed\n") == 20, "ASEVD410")
                finally: os.close(fd)
            finally: os.close(scratch_fd)
            command = closed_command(audit, self.workspace, self.dotnet, self.ids, self.lease_id, self.job_deadline)
            with self.state.condition:
                self._check_open(deadline)
                # Closed-check, process creation and retained ownership share the stop latch lock.
                process = subprocess.Popen(command, env=_ENV, stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, close_fds=True)
                self.state.attach_process(process)
            self.pumps = OutputPumps(audit.capabilities.maximum_output_bytes, self.job_counter, self.state, self.abort)
            self.pumps.start(process)
            while True:
                self._check_open(deadline)
                props = _properties(self.unit, deadline)
                observation = classify_startup(props, self.ids, self.group, loaded_seen=self.state.loaded_seen)
                self.state.loaded_seen |= props["LoadState"] == "loaded"
                if props["LoadState"] == "loaded":
                    _require(props["Type"] == "exec" and props["User"] == str(self.ids.application_uid)
                             and props["Group"] == str(self.ids.application_gid) and props["KillMode"] == "control-group", "ASEVD410")
                    if props["ControlGroup"]:
                        _require(props["ControlGroup"] == self.group, "ASEVD410"); self.state.observed_group = True
                    if observation == "running":
                        _require(props["ControlGroup"] == self.group, "ASEVD410")
                        _require(props["MainPID"].isdigit(), "ASEVD410"); pid = int(props["MainPID"])
                        _process_identity(pid, self.ids, self.group); self.pid = pid
                        self._check_open(deadline)
                        _require(self.monitor.is_alive(), "ASEVD410")
                        return {"ok": True, "lease_id": self.lease_id, "apphost_pid": pid,
                                "application_uid": self.ids.application_uid, "application_gid": self.ids.application_gid,
                                "cgroup": self.group, "owned": True}
                _require(process.poll() is None, "ASEVD410")
                with self.state.condition: self.state.condition.wait(timeout=min(0.03, _remaining(deadline)))
        except Exception:
            self.state.close(True); self.abort.set()
            raise ApplicationError("ASEVD410") from None
        finally: self.state.end_operation()

    def resource_wait(self, lease_id: str, resource_id: str, deadline: float):
        """Exact nine-field root ACK from a fixed authenticated UDS and <=4096 HTTP bytes."""
        audit = self.selected.audit
        _require((lease_id, resource_id) == (self.lease_id, audit.resource_id) and self.pid > 0, "ASEVD410")
        deadline = min(deadline, self.job_deadline, time.monotonic() + audit.resource_seconds)
        self.state.begin_resource()
        try:
            while True:
                self._check_open(deadline); _require(self.monitor.is_alive(), "ASEVD410")
                _process_identity(self.pid, self.ids, self.group)
                try:
                    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
                        connection.settimeout(min(1, _remaining(deadline)))
                        connection.connect(str(self.workspace.scratch / "http.sock"))
                        credentials = connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12)
                        _require(len(credentials) == 12, "ASEVD410"); pid, uid, gid = struct.unpack("3i", credentials)
                        _require(uid == self.ids.application_uid and gid == self.ids.application_gid, "ASEVD410")
                        _process_identity(pid, self.ids, self.group)
                        expected_resource = self.bundle.path / "resource" / "NativeHttpResource.dll"
                        with Path(f"/proc/{pid}/cmdline").open("rb") as stream: command = stream.read(4097)
                        _require(len(command) <= 4096 and command.split(b"\0") ==
                                 [os.fsencode(self.dotnet), os.fsencode(expected_resource), b"--socket",
                                  os.fsencode(self.workspace.scratch / "http.sock"), b"--case", b"normal", b""], "ASEVD410")
                        _require(os.stat(f"/proc/{pid}/exe").st_ino == self.dotnet.stat().st_ino
                                 and os.stat(f"/proc/{pid}/exe").st_dev == self.dotnet.stat().st_dev, "ASEVD410")
                        connection.settimeout(min(1, _remaining(deadline)))
                        connection.sendall(b"GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
                        data = bytearray()
                        while True:
                            self._check_open(deadline); connection.settimeout(min(1, _remaining(deadline)))
                            part = connection.recv(min(1024, HTTP_LIMIT + 1 - len(data)))
                            if not part: break
                            data.extend(part); _require(len(data) <= HTTP_LIMIT, "ASEVD410")
                        self._check_open(deadline); _process_identity(pid, self.ids, self.group)
                        validate_http_response(bytes(data))
                        _require(audit_protected_probes(self.workspace, deadline=deadline) == self.probe_snapshot, "ASEVD410")
                        self._check_open(deadline)
                        return {"ok": True, "lease_id": lease_id, "resource_id": resource_id,
                                "application_uid": uid, "cgroup": self.group, "kernel_peer_checked": True,
                                "http_status": 200, "healthy": True, "received_bytes": len(data)}
                except (FileNotFoundError, ConnectionRefusedError, TimeoutError):
                    with self.state.condition: self.state.condition.wait(timeout=min(0.03, _remaining(deadline)))
        except Exception:
            self.state.close(True); self.abort.set(); raise ApplicationError("ASEVD410") from None
        finally: self.state.end_operation()

    def stop(self, deadline: float):
        """Latch closure immediately; a fresh shared deadline bounds subsequent teardown."""
        self.state.close()
        if self.state.process is not None:
            _systemctl(self.unit, ["kill", "--kill-whom=main", "--signal=TERM"], min(deadline, self.job_deadline))

    def join(self, deadline: float):
        """Return separate app byte receipt only after physical group/pump/watchdog exit."""
        with self.join_lock, self.state.joining(self.abort):
            deadline = min(deadline, self.job_deadline)
            self.state.join_phase = "receipt-check"
            if self.receipt is not None: return self.receipt
            self.state.close()
            self.state.join_phase = "watchdog-live"
            if self.watchdog is not None: self.watchdog.require_live()
            self.state.join_phase = "active-operations"
            with self.state.condition:
                while self.state.active:
                    if self.watchdog is not None: self.watchdog.require_live()
                    self.state.condition.wait(timeout=min(0.03, _remaining(deadline)))
            process = self.state.process
            if process is None:
                self.state.join_phase = "no-process"
                _require(not self.state.failed, "ASEVD410")
                self.state.join_phase = "watchdog-disarm"
                if self.watchdog is not None: self.watchdog.disarm(deadline)
                _require(self.monitor is None or (self.watchdog is not None and self.watchdog.disarmed), "ASEVD410")
                self.state.join_phase = "final-check"
                self.state.require_final(deadline, self.abort)
                self.receipt = (0, 0, 0); return self.receipt
            grace = min(deadline, time.monotonic() + self.selected.audit.capabilities.stopping_seconds)
            self.state.join_phase = "terminate-main"
            _systemctl(self.unit, ["kill", "--kill-whom=main", "--signal=TERM"], deadline)
            self.state.join_phase = "process-group-wait"
            while time.monotonic() < grace:
                self.watchdog.require_live()
                if process.poll() is not None and self.state.observed_group and _group_empty(self.group): break
                with self.state.condition: self.state.condition.wait(timeout=min(0.03, _remaining(grace)))
            if process.poll() is None or not self.state.observed_group or not _group_empty(self.group):
                self.state.join_phase = "force-stop"
                _systemctl(self.unit, ["stop", "--no-block"], deadline)
                _systemctl(self.unit, ["kill", "--kill-whom=all", "--signal=KILL"], deadline)
            self.state.join_phase = "process-wait"
            try: process.wait(timeout=_remaining(deadline))
            except subprocess.TimeoutExpired as error:
                self.state.fail_join(error, self.abort)
                try: process.kill(); process.wait(timeout=_remaining(deadline))
                except Exception: pass
                raise error
            self.state.join_phase = "group-check"
            _require(self.state.observed_group and _group_empty(self.group), "ASEVD410")
            self.state.join_phase = "pump-join"
            receipt = self.pumps.join(deadline)
            self.state.join_phase = "watchdog-live"
            self.watchdog.require_live()
            self.state.join_phase = "exit-check"
            _require(not self.state.failed and process.returncode == 0, "ASEVD410")
            self.state.join_phase = "watchdog-disarm"
            self.watchdog.disarm(deadline)
            self.state.join_phase = "bundle-recheck"
            self.bundle.recheck(deadline=deadline)
            self.state.join_phase = "probe-recheck"
            _require(audit_protected_probes(self.workspace, deadline=deadline) == self.probe_snapshot, "ASEVD410")
            self.state.join_phase = "final-check"
            self.state.require_final(deadline, self.abort)
            self.receipt = receipt
            self.state.join_phase = "complete"
            return receipt
