#!/usr/bin/env python3
"""Portable metadata/state/procedure controls. No root, systemd, enrollment or native admission."""
import dataclasses
import hashlib
import importlib.util
import io
import json
import multiprocessing
import os
from pathlib import Path
import stat
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location("linux_application", Path(__file__).resolve().parents[2] / "scripts/evidencehost_linux_application.py")
app = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = app
SPEC.loader.exec_module(app)
IDS = app.Identities(1001, 2001, 1002, 2002, 1003, 2003, 2004, 2005)
LEASE = "a" * 32
GROUP = "/system.slice/issue779-app-" + LEASE + ".service"


def definition():
    resource = {"Id": "http", "Readiness": "aspire_health", "DeadlineSeconds": 120, "Requires": []}
    producer = {"Id": "coverage", "Kind": "coverage", "Version": "1.0.0", "RequiredResources": ["http"],
                "AssertionIds": ["coverage/assertion@1"], "ArtifactSlots": [{"LogicalName": "report", "RelativeRoot": "coverage",
                "MediaType": "application/xml", "Required": True, "MaximumBytes": 1024}], "TimeoutSeconds": 600,
                "CoverageGate": {"MinLinePercent": 100, "MinBranchPercent": 100, "MinPatchLinePercent": None,
                "MinPatchBranchPercent": None, "PatchLineMode": "measurable", "TolerancePercent": 0}}
    files = [("AspireChild.dll", "AppHost"), ("AspireChild.runtimeconfig.json", "AppHostRuntimeConfiguration"),
             ("resource/NativeHttpResource.dll", "Resource"), ("resource/NativeHttpResource.runtimeconfig.json", "ResourceRuntimeConfiguration"),
             ("dcp/dcp", "Dcp"), ("proof-input/declared.txt", "DeclaredInput"), ("dcp/ext/extension", "DcpExtension"),
             ("dependency.dll", "Dependency"), ("AspireChild.deps.json", "DependencyManifest")]
    return {"Id": "app", "Version": "1.0.0", "BuildId": "build", "AspireSdkVersion": "13.4.4", "ProfileId": "profile",
            "Policy": {"Id": "policy", "Version": "1.0.0", "ConservativeProfileId": "profile", "Rules": [],
            "Profiles": [{"Id": "profile", "Scope": "Targeted", "Resources": [resource], "Producers": [producer], "Obligations": []}]},
            "Resources": [{"Declaration": resource, "CapabilityClass": "native-http-uds", "CapabilityVersion": "1.0.0", "ResourceName": "native-http"}],
            "Producers": [{"Declaration": producer, "ImplementationId": "coverage", "ImplementationVersion": "1.0.0"}],
            "BundleFiles": [{"RelativePath": path, "Role": role, "LengthBytes": 3,
                             "Sha256": hashlib.sha256(b"abc").hexdigest(), "Mode": 0o555 if role in ("Dcp", "DcpExtension") else 0o444}
                            for path, role in files],
            "Capabilities": {"ReadOnlyInputs": ["proof-input/declared.txt"], "ScratchBytes": 2**30, "MemoryBytes": 2**30,
                             "MaximumTasks": 64, "MaximumOutputBytes": 2**20, "StartSeconds": 120, "StoppingSeconds": 30}}


def candidate(entry=None):
    raw = json.dumps(entry or definition(), sort_keys=True, separators=(",", ":")).encode()
    return app.audit_candidate(raw, entry_digest=hashlib.sha256(raw).hexdigest(), catalogue_digest="b" * 64, policy_sha256="c" * 64)


def workspace():
    parent = Path("/run/issue779-app-" + LEASE)
    return app.Workspace(parent, parent / "scratch", parent / "control", Path("/protected/tools"),
                         Path("/protected/output"), Path("/protected/control"), Path("/producer"), Path("/source"))


class MetadataControls(unittest.TestCase):
    def test_task_cap_128_is_selected_literal_in_metadata_and_argv_129_rejected(self):
        for maximum in (64, 128):
            with self.subTest(maximum=maximum):
                entry = definition(); entry["Capabilities"]["MaximumTasks"] = maximum
                audit = candidate(entry)
                self.assertEqual(maximum, audit.capabilities.maximum_tasks)
                self.assertEqual(maximum, audit.descriptor(IDS)["capabilities"]["maximum_tasks"])
                command = app.closed_command(audit, workspace(), Path("/usr/share/dotnet/dotnet"), IDS, LEASE, time.monotonic() + 60)
                self.assertEqual((f"--property=TasksMax={maximum}",), tuple(arg for arg in command if arg.startswith("--property=TasksMax=")))
                self.assertEqual((), app._COMPILED_REGISTRATIONS)
                with self.assertRaisesRegex(app.ApplicationError, "^ASEVD407$"):
                    app.select_registration("app", audit.entry_digest, policy_sha256=audit.policy_sha256, profile_id="profile")
        entry = definition(); entry["Capabilities"]["MaximumTasks"] = 129
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD404$"): candidate(entry)

    def test_candidate_has_complete_fresh_wire_metadata_and_is_not_enrolled(self):
        audit = candidate(); descriptor = audit.descriptor(IDS)
        self.assertEqual(14, len(descriptor)); self.assertEqual(9, len(descriptor["bundle_files"]))
        self.assertEqual("native-http", definition()["Resources"][0]["ResourceName"])
        descriptor["capabilities"]["read_only_inputs"].clear()
        self.assertEqual(["proof-input/declared.txt"], audit.descriptor(IDS)["capabilities"]["read_only_inputs"])
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD407$"):
            app.select_registration("app", audit.entry_digest, policy_sha256=audit.policy_sha256, profile_id="profile")

    def test_audit_or_fabricated_selection_cannot_issue_lease(self):
        audit = candidate()
        registration = app._CompiledRegistration(audit.canonical_entry, audit.entry_digest, audit.catalogue_digest, audit.policy_sha256)
        selected = app._SelectedRegistration(registration, audit)
        for value in (audit, selected, None):
            with self.subTest(value=type(value).__name__), self.assertRaisesRegex(app.ApplicationError, "^ASEVD407$"):
                app.RootApplicationLease(value, IDS, workspace(), None, Path("/usr/share/dotnet/dotnet"), 1, app.JobOutputCounter())
        self.assertEqual((), app._COMPILED_REGISTRATIONS)

    def test_metadata_mutations_have_fixed_value_free_failure(self):
        rows = [("role", lambda x: x["BundleFiles"][0].update(Role=["canary"])),
                ("prefix", lambda x: x["BundleFiles"][0].update(RelativePath="resource")),
                ("mode", lambda x: x["BundleFiles"][0].update(Mode=0o644)),
                ("linkname", lambda x: x["BundleFiles"][0].update(RelativePath="../canary")),
                ("count", lambda x: x["BundleFiles"].extend(x["BundleFiles"] * 30)),
                ("size", lambda x: x["BundleFiles"][0].update(LengthBytes=app.MAX_FILE + 1)),
                ("tasks", lambda x: x["Capabilities"].update(MaximumTasks=True)),
                ("sdk", lambda x: x.update(AspireSdkVersion="canary")),
                ("nested-apphost", lambda x: x["BundleFiles"][0].update(RelativePath="apphost/App.dll"))]
        for name, change in rows:
            with self.subTest(name=name):
                entry = definition(); change(entry)
                with self.assertRaisesRegex(app.ApplicationError, "^ASEVD404$"): candidate(entry)

    def test_duplicate_json_and_digest_drift_are_rejected(self):
        for raw in (b'{"Id":"canary","id":"canary"}', candidate().canonical_entry):
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD404$"):
                app.audit_candidate(raw, entry_digest="0" * 64, catalogue_digest="b" * 64, policy_sha256="c" * 64)

    def test_identity_map_rejects_root_shared_and_boolean_values(self):
        for change in ({"application_uid": 0}, {"application_uid": IDS.worker_uid},
                       {"results_gid": IDS.application_gid}, {"worker_gid": True}):
            with self.subTest(change=change), self.assertRaisesRegex(app.ApplicationError, "^ASEVD404$"):
                dataclasses.replace(IDS, **change).validate()

    def test_closed_argv_contains_exact_kind_contract_and_all_denials(self):
        command = app.closed_command(candidate(), workspace(), Path("/usr/share/dotnet/dotnet"), IDS, LEASE, time.monotonic() + 60)
        self.assertEqual(("--scratch", str(workspace().scratch), "--case", "normal"), command[-4:])
        self.assertIn("--property=SupplementaryGroups=", command)
        self.assertIn("--property=RestrictSUIDSGID=yes", command)
        self.assertIn("--property=PrivateNetwork=yes", command)
        self.assertIn("--property=PrivateTmp=yes", command)
        self.assertIn("--property=CapabilityBoundingSet=", command)
        self.assertIn("--property=MemoryMax=1073741824", command)
        self.assertIn("--property=TasksMax=64", command)
        self.assertIn("--property=InaccessiblePaths=/protected/tools /protected/output /protected/control /producer /source", command)
        self.assertIn("ASPIRE__STORE__PATH=" + str(workspace().scratch / ".aspire-store"), command)
        self.assertNotIn("--property=RemainAfterExit=yes", command)


class BundleDescriptorControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="app-audit-", dir="/tmp")
        self.root = Path(self.temporary.name).resolve() / "bundle"; self.root.mkdir()
        self.audit = candidate()
        for item in self.audit.files:
            path = self.root / item.relative_path; path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"abc"); path.chmod(item.mode)
        for directory in [self.root, *(p for p in self.root.rglob("*") if p.is_dir())]: directory.chmod(0o555)

    def tearDown(self):
        for directory in [self.root, *(p for p in self.root.rglob("*") if p.is_dir() and not p.is_symlink())]: directory.chmod(0o755)
        self.temporary.cleanup()

    def test_actual_descriptors_recheck_and_idempotent_close(self):
        bundle = app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid())
        descriptors = [bundle.root_fd, *(fd for _, fd, _ in bundle.files)]
        bundle.recheck(expected_owner_uid=os.getuid()); bundle.close(); bundle.close()
        for fd in descriptors:
            with self.assertRaises(OSError): os.fstat(fd)

    def test_exact_candidate_binding_and_fresh_rehash_reject_cross_candidate_or_changed_bytes(self):
        with app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid()) as bundle:
            bundle.require_binding(self.audit)
            other = definition(); other["Id"] = "another-application"
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): bundle.require_binding(candidate(other))
            files = (dataclasses.replace(self.audit.files[0], length_bytes=4), *self.audit.files[1:])
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
                bundle.require_binding(dataclasses.replace(self.audit, files=files))
            bundle.verify_candidate(self.audit, expected_owner_uid=os.getuid(), deadline=time.monotonic() + 2)
            path = self.root / "AspireChild.dll"; path.chmod(0o644); path.write_bytes(b"xyz"); path.chmod(0o444)
            with self.assertRaises(app.ApplicationError):
                bundle.verify_candidate(self.audit, expected_owner_uid=os.getuid(), deadline=time.monotonic() + 2)

    def test_audit_deadline_rejects_before_open_during_real_read_and_on_recheck(self):
        with patch.object(app, "_open_directory") as opening:
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
                app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid(), deadline=time.monotonic() - 1)
            opening.assert_not_called()
        real_read = os.read; clock = [0]
        def expiring_read(fd, count):
            block = real_read(fd, count); clock[0] = 3; return block
        with patch.object(app.time, "monotonic", side_effect=lambda: clock[0]), patch.object(app.os, "read", side_effect=expiring_read):
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
                app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid(), deadline=2)
        with app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid()) as bundle:
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
                bundle.recheck(expected_owner_uid=os.getuid(), deadline=time.monotonic() - 1)

    def test_close_failure_attempts_every_real_fd_once_and_remains_failed(self):
        bundle = app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid())
        descriptors = [bundle.root_fd, *(fd for _, fd, _ in bundle.files)]
        first = OSError("private-close-canary"); attempted = []; real_close = os.close
        def failing_close(fd):
            attempted.append(fd)
            if fd == descriptors[0]: raise first
            real_close(fd)
        try:
            with patch.object(app.os, "close", side_effect=failing_close):
                with self.assertRaises(OSError) as raised: bundle.close()
                self.assertIs(first, raised.exception)
                self.assertEqual(descriptors, attempted)
                for fd in descriptors[1:]:
                    with self.assertRaises(OSError): os.fstat(fd)
                with self.assertRaises(OSError) as repeated: bundle.close()
                self.assertIs(first, repeated.exception); self.assertEqual(descriptors, attempted)
            self.assertEqual(-1, bundle.root_fd)
        finally: real_close(descriptors[0])  # Only the deliberately unclosed test FD.

    def test_actual_symlink_and_hardlink_are_rejected(self):
        path = self.root / "AspireChild.dll"; self.root.chmod(0o755); path.unlink()
        path.symlink_to(self.root / "dependency.dll")
        with self.assertRaises(app.ApplicationError): app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid())
        path.unlink(); os.link(self.root / "dependency.dll", path); self.root.chmod(0o555)
        with self.assertRaises(app.ApplicationError): app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid())

    def test_actual_writable_wrong_hash_and_unlisted_file_are_rejected(self):
        path = self.root / "AspireChild.dll"
        path.chmod(0o644)
        with self.assertRaises(app.ApplicationError): app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid())
        path.write_bytes(b"xyz"); path.chmod(0o444)
        with self.assertRaises(app.ApplicationError): app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid())
        path.chmod(0o644); path.write_bytes(b"abc"); path.chmod(0o444)
        self.root.chmod(0o755); (self.root / "extra").write_bytes(b"canary"); self.root.chmod(0o555)
        with self.assertRaises(app.ApplicationError): app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid())

    def test_retained_identity_rejects_replacement_and_wrong_owner(self):
        with app.audit_bundle(self.root, self.audit, expected_owner_uid=os.getuid()) as bundle:
            with self.assertRaises(app.ApplicationError): bundle.recheck(expected_owner_uid=os.getuid() + 1)
            self.root.chmod(0o755)
            path = self.root / "AspireChild.dll"; path.unlink(); path.write_bytes(b"abc"); path.chmod(0o444)
            self.root.chmod(0o555)
            with self.assertRaises(app.ApplicationError): bundle.recheck(expected_owner_uid=os.getuid())


class OwnershipAndPumpControls(unittest.TestCase):
    def test_final_receipt_deadline_abort_or_failed_state_latches_without_retry_upgrade(self):
        for reason in ("expired", "abort", "failed"):
            with self.subTest(reason=reason):
                state = app.OwnershipState(); abort = threading.Event()
                if reason == "abort": abort.set()
                if reason == "failed": state.close(True)
                deadline = time.monotonic() + 2 if reason != "expired" else time.monotonic() - 1
                with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
                    with state.joining(abort): state.require_final(deadline, abort)
                first = state.first_join_failure
                with self.assertRaises(app.ApplicationError) as retry:
                    with state.joining(abort): pass
                self.assertIs(first, retry.exception); self.assertTrue(state.failed)

    def test_join_timeout_latches_first_failure_and_denies_later_success(self):
        state = app.OwnershipState(); abort = threading.Event()
        first = subprocess.TimeoutExpired("private-canary", 1)
        with self.assertRaises(subprocess.TimeoutExpired) as raised:
            with state.joining(abort): raise first
        self.assertIs(first, raised.exception)
        self.assertTrue(state.closed); self.assertTrue(state.failed); self.assertTrue(abort.is_set())
        completed = []
        with self.assertRaises(subprocess.TimeoutExpired) as retried:
            with state.joining(abort): completed.append("would-have-succeeded")
        self.assertIs(first, retried.exception); self.assertEqual([], completed)
        state.fail_join(OSError("later-canary"), abort)
        self.assertIs(first, state.first_join_failure)

    def test_stop_before_spawn_and_duplicate_attempt_cannot_reopen(self):
        state = app.OwnershipState(); state.begin_start(); state.close()
        with self.assertRaises(app.ApplicationError): state.attach_process(Mock())
        state.end_operation(); self.assertEqual(0, state.active)
        with self.assertRaises(app.ApplicationError): state.begin_start()

    def test_stop_cannot_cross_retained_process_critical_section(self):
        state = app.OwnershipState(); state.begin_start(); entered = threading.Event(); process = Mock()
        def closing(): entered.set(); state.close()
        with state.condition:
            thread = threading.Thread(target=closing); thread.start(); self.assertTrue(entered.wait(1))
            self.assertFalse(state.closed); state.attach_process(process)
        thread.join(timeout=1); self.assertFalse(thread.is_alive()); self.assertTrue(state.closed)
        self.assertIs(process, state.process); state.end_operation()

    def test_real_short_read_eof_and_exact_shared_bytes(self):
        state = app.OwnershipState(); job = app.JobOutputCounter(); job.count(7)
        pumps = app.OutputPumps(100, job, state, threading.Event())
        process = Mock(stdout=io.BytesIO(b"abc"), stderr=io.BytesIO(b"de"))
        pumps.start(process); self.assertEqual((5, 3, 2), pumps.join(time.monotonic() + 2))
        self.assertEqual(12, job.received_bytes()); self.assertFalse(state.failed)

    def test_real_pump_read_failure_is_not_eof_or_success(self):
        class Failed(io.BytesIO):
            def read(self, size=-1): raise OSError("private-canary")
        state = app.OwnershipState(); abort = threading.Event()
        pumps = app.OutputPumps(100, app.JobOutputCounter(), state, abort)
        pumps.start(Mock(stdout=Failed(), stderr=io.BytesIO()))
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): pumps.join(time.monotonic() + 2)
        self.assertTrue(state.failed); self.assertTrue(abort.is_set())

    def test_teardown_burst_counts_discarded_bytes_and_latches_quota(self):
        state = app.OwnershipState(); job = app.JobOutputCounter(); abort = threading.Event()
        pumps = app.OutputPumps(3, job, state, abort)
        pumps.start(Mock(stdout=io.BytesIO(b"abcdef"), stderr=io.BytesIO(b"ghi")))
        with self.assertRaises(app.ApplicationError): pumps.join(time.monotonic() + 2)
        self.assertEqual(9, job.received_bytes()); self.assertEqual([6, 3], pumps.counts)
        self.assertTrue(state.failed); self.assertTrue(abort.is_set())

    def test_job_quota_includes_existing_producer_bytes(self):
        state = app.OwnershipState(); job = app.JobOutputCounter(); job.count(app.MAX_JOB_OUTPUT)
        pumps = app.OutputPumps(100, job, state, threading.Event())
        pumps.start(Mock(stdout=io.BytesIO(b"x"), stderr=io.BytesIO()))
        with self.assertRaises(app.ApplicationError): pumps.join(time.monotonic() + 2)
        self.assertTrue(job.exceeded.is_set())


def managed_pe_control():
    """Synthetic structural bytes only; this is never loaded or presented as native denial proof."""
    data = bytearray(1536); data[:2] = b"MZ"; struct.pack_into("<I", data, 60, 128)
    data[128:132] = b"PE\0\0"; struct.pack_into("<H", data, 134, 1); struct.pack_into("<H", data, 148, 224)
    optional = 152; struct.pack_into("<H", data, optional, 0x10b); struct.pack_into("<I", data, optional + 92, 16)
    struct.pack_into("<II", data, optional + 96 + 14 * 8, 0x2000, 72)
    section = optional + 224; struct.pack_into("<III", data, section + 12, 0x2000, 1024, 512)
    struct.pack_into("<I", data, 512, 72); struct.pack_into("<II", data, 520, 0x2100, 64)
    data[768:772] = b"BSJB"
    return bytes(data)


class ProtectedProbeControls(unittest.TestCase):
    """Actual local files/FDs with an owner seam; no lease, real-root or CLR load claim."""
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="app-probe-", dir="/tmp")
        self.base = Path(self.temporary.name).resolve(); self.tools = self.base / "tools"; self.output = self.base / "output"
        self.tools.mkdir(mode=0o750); self.output.mkdir(mode=0o700)
        self.tool = self.tools / "protected-tool.dll"; self.tool.write_bytes(managed_pe_control()); self.tool.chmod(0o444)
        self.workspace = dataclasses.replace(workspace(), protected_tools=self.tools, protected_output=self.output)

    def tearDown(self): self.temporary.cleanup()

    def audit(self):
        return app.audit_protected_probes(self.workspace, expected_owner_uid=os.getuid(), deadline=time.monotonic() + 2)

    def test_actual_structural_managed_tool_and_absence_are_rechecked(self):
        snapshot = self.audit(); self.assertEqual(snapshot, self.audit())
        self.assertEqual(hashlib.sha256(managed_pe_control()).hexdigest(), snapshot.tool_sha256)
        self.assertEqual((), app._COMPILED_REGISTRATIONS)
        probe = self.output / "native-resource-output-probe"; probe.write_bytes(b"private-canary")
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()

    def test_missing_and_malformed_managed_tool_cannot_count_as_denial(self):
        self.tool.unlink()
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()
        for content in (b"private-canary", b"MZ" + b"BSJB" + bytes(100),
                        managed_pe_control().replace(b"BSJB", b"NOPE")):
            self.tool.write_bytes(content); self.tool.chmod(0o444)
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()
            self.tool.unlink()

    def test_symlink_hardlink_or_unprotected_file_and_parent_are_rejected(self):
        target = self.base / "target"; target.write_bytes(managed_pe_control()); target.chmod(0o444)
        self.tool.unlink(); self.tool.symlink_to(target)
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()
        self.tool.unlink(); os.link(target, self.tool)
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()
        self.tool.unlink(); self.tool.write_bytes(managed_pe_control()); self.tool.chmod(0o666)
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()
        self.tool.chmod(0o444); self.tools.chmod(0o770)
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()
        self.tools.chmod(0o750)
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
            app.audit_protected_probes(self.workspace, expected_owner_uid=os.getuid() + 1)

    def test_overbound_regular_tool_is_rejected_before_read(self):
        self.tool.chmod(0o644)
        with self.tool.open("wb") as stream: stream.truncate(app.MAX_PROTECTED_TOOL + 1)
        self.tool.chmod(0o444)
        with patch.object(app.os, "read") as reading:
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()
            reading.assert_not_called()

    def test_existing_or_dangling_probe_and_linked_output_root_are_rejected(self):
        probe = self.output / "native-resource-output-probe"
        probe.symlink_to(self.base / "absent")
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()
        probe.unlink(); probe.mkdir()
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): self.audit()
        probe.rmdir(); alias = self.base / "alias"; alias.symlink_to(self.output)
        with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
            app.audit_protected_probes(dataclasses.replace(self.workspace, protected_output=alias), expected_owner_uid=os.getuid())

    def test_read_failure_closes_actual_fds_and_emits_only_closed_code(self):
        real_open = app._open_at; opened = []
        def recording_open(*args, **kwargs):
            fd = real_open(*args, **kwargs); opened.append(fd); return fd
        with patch.object(app, "_open_at", side_effect=recording_open), patch.object(app.os, "read", side_effect=OSError("private-read-canary")):
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$") as failure: self.audit()
        self.assertNotIn("canary", str(failure.exception))
        for fd in opened:
            with self.assertRaises(OSError): os.fstat(fd)


def portable_watchdog(control, parent_control, deadline, abort, parent_pid):
    # The actual protocol has no systemd/root operation; failure is a nonzero fork exit.
    clean = app.watchdog_control(control, parent_control, deadline, abort, parent_pid)
    raise SystemExit(0 if clean else 1)


def exited_watchdog(control, parent_control, release, exitcode):
    parent_control.close()
    control.send_bytes(f"READY:{os.getpid()}:{os.geteuid()}".encode("ascii"))
    release.wait(2); control.close(); raise SystemExit(exitcode)


def incomplete_watchdog(control, parent_control, response):
    parent_control.close()
    control.send_bytes(f"READY:{os.getpid()}:{os.geteuid()}".encode("ascii"))
    if control.poll(2):
        control.recv_bytes(64)
        if response is not None: control.send_bytes(response)
    control.close()


class WatchdogHandshakeControls(unittest.TestCase):
    """Actual private forks/Pipes only; no unit, payload, compiled table or root actions."""
    def setUp(self): self.context = multiprocessing.get_context("fork")

    def launch(self, target, *arguments):
        parent, child = self.context.Pipe(duplex=True)
        process = self.context.Process(target=target, args=(child, parent, *arguments))
        process.start(); child.close()
        def cleanup():
            if process.is_alive(): process.terminate()
            process.join(2); parent.close()
        self.addCleanup(cleanup)
        return process, parent, app.WatchdogOwnership(process, parent)

    def test_actual_live_startup_matching_disarm_ack_and_joined_zero(self):
        abort = self.context.Event()
        process, control, ownership = self.launch(portable_watchdog, time.monotonic() + 5, abort, os.getpid())
        ownership.startup(time.monotonic() + 2, expected_uid=os.geteuid())
        ownership.require_live(); self.assertFalse(ownership.disarmed)
        ownership.disarm(time.monotonic() + 2)
        self.assertTrue(ownership.disarmed); self.assertEqual(0, process.exitcode)

    def test_abort_after_matching_done_and_actual_join_cannot_mark_disarmed(self):
        abort = self.context.Event()
        process, control, unused = self.launch(portable_watchdog, time.monotonic() + 5, abort, os.getpid())
        ownership = app.WatchdogOwnership(process, control, abort)
        ownership.startup(time.monotonic() + 2, expected_uid=os.geteuid())
        real_join = process.join
        def joining_then_aborting(timeout): real_join(timeout); abort.set()
        state = app.OwnershipState()
        with patch.object(process, "join", side_effect=joining_then_aborting):
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
                with state.joining(abort): ownership.disarm(time.monotonic() + 2)
        self.assertEqual(0, process.exitcode); self.assertFalse(ownership.disarmed); self.assertTrue(state.failed)

    def test_deadline_after_matching_done_and_actual_join_cannot_mark_disarmed(self):
        abort = self.context.Event()
        process, control, ownership = self.launch(portable_watchdog, time.monotonic() + 5, abort, os.getpid())
        ownership.startup(time.monotonic() + 2, expected_uid=os.geteuid())
        remaining = app._remaining; calls = []
        def expiring_final_remaining(deadline):
            calls.append(deadline)
            return remaining(time.monotonic() - 1 if len(calls) == 4 else deadline)
        state = app.OwnershipState()
        with patch.object(app, "_remaining", side_effect=expiring_final_remaining):
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
                with state.joining(abort): ownership.disarm(time.monotonic() + 2)
        self.assertEqual(4, len(calls)); self.assertEqual(0, process.exitcode)
        self.assertFalse(ownership.disarmed); self.assertTrue(state.failed)

    def test_prior_zero_or_nonzero_exit_cannot_be_upgraded_and_latches_failure(self):
        for code in (0, 1):
            with self.subTest(exitcode=code):
                release = self.context.Event()
                process, control, ownership = self.launch(exited_watchdog, release, code)
                ownership.startup(time.monotonic() + 2, expected_uid=os.geteuid())
                release.set(); process.join(2); self.assertEqual(code, process.exitcode)
                state = app.OwnershipState(); abort = threading.Event()
                with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"):
                    with state.joining(abort): ownership.disarm(time.monotonic() + 2)
                first = state.first_join_failure
                with self.assertRaises(app.ApplicationError) as retried:
                    with state.joining(abort): pass
                self.assertIs(first, retried.exception)
                self.assertTrue(state.failed); self.assertTrue(abort.is_set()); self.assertFalse(ownership.disarmed)

    def test_abort_expiry_or_parent_loss_never_ack_clean_disarm(self):
        for failure in ("abort", "expiry", "parent-loss"):
            with self.subTest(failure=failure):
                abort = self.context.Event()
                if failure == "abort": abort.set()
                deadline = time.monotonic() + 5 if failure != "expiry" else time.monotonic() - 1
                parent_pid = os.getpid() if failure != "parent-loss" else os.getpid() + 1
                process, control, ownership = self.launch(portable_watchdog, deadline, abort, parent_pid)
                self.assertTrue(control.poll(2))
                self.assertEqual(f"READY:{process.pid}:{os.geteuid()}".encode("ascii"), control.recv_bytes(64))
                process.join(2); self.assertEqual(1, process.exitcode)
                with self.assertRaises(EOFError): control.recv_bytes(64)
                self.assertFalse(ownership.disarmed)

    def test_wrong_disarm_identity_or_oversize_request_never_ack(self):
        for oversized in (False, True):
            with self.subTest(oversized=oversized):
                process, control, ownership = self.launch(portable_watchdog, time.monotonic() + 5,
                                                          self.context.Event(), os.getpid())
                ownership.startup(time.monotonic() + 2, expected_uid=os.geteuid())
                control.send_bytes(b"x" * 65 if oversized else f"DISARM:{process.pid + 1}".encode("ascii"))
                process.join(2); self.assertEqual(1, process.exitcode)
                # Linux may reset the pipe when malformed request bytes remain unread.
                with self.assertRaises((EOFError, ConnectionResetError)): control.recv_bytes(64)
                self.assertFalse(ownership.disarmed)

    def test_zero_exit_without_matching_done_ack_is_not_clean_disarm(self):
        for response in (None, b"DONE:0"):
            with self.subTest(response=response):
                process, control, ownership = self.launch(incomplete_watchdog, response)
                ownership.startup(time.monotonic() + 2, expected_uid=os.geteuid())
                with self.assertRaises((app.ApplicationError, EOFError)):
                    ownership.disarm(time.monotonic() + 2)
                process.join(2); self.assertEqual(0, process.exitcode); self.assertFalse(ownership.disarmed)


class KernelAndReadinessParserControls(unittest.TestCase):
    def test_four_uid_gid_values_and_exact_unified_group(self):
        status = b"Uid:\t1003 1003 1003 1003\nGid:\t2003 2003 2003 2003\nNoNewPrivs:\t1\nCapEff:\t0000000000000000\n"
        app.validate_process_facts(status, ("0::" + GROUP + "\n").encode(), IDS, GROUP)
        for bad_status, bad_group in ((status.replace(b"1003", b"0", 1), GROUP),
                                     (status.replace(b"2003", b"2004", 1), GROUP),
                                     (status.replace(b"NoNewPrivs:\t1", b"NoNewPrivs:\t0"), GROUP),
                                     (status, "/system.slice/foreign.service")):
            with self.assertRaises(app.ApplicationError): app.validate_process_facts(bad_status, ("0::" + bad_group).encode(), IDS, GROUP)

    def test_http_fixed_body_and_total_received_byte_cap(self):
        response = b"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nnative-http-ready"
        self.assertEqual(len(response), app.validate_http_response(response))
        for data in (response.replace(b"200 OK", b"503 Failed"), response + b"canary", b"canary", b"x" * 4097):
            with self.assertRaisesRegex(app.ApplicationError, "^ASEVD410$"): app.validate_http_response(data)

    def test_actual_v6_queued_shape_is_pending_only_and_not_running(self):
        properties = {"LoadState": "loaded", "Type": "exec", "ActiveState": "inactive", "SubState": "dead", "MainPID": "0",
                      "User": "1003", "Group": "2003", "ControlGroup": "", "KillMode": "control-group", "ExecMainCode": "0", "ExecMainStatus": "0"}
        self.assertEqual("pending", app.classify_startup(properties, IDS, GROUP))
        running = {**properties, "ActiveState": "active", "SubState": "running", "MainPID": "123", "ControlGroup": GROUP}
        self.assertEqual("running", app.classify_startup(running, IDS, GROUP))
        for change in ({"User": "0"}, {"ControlGroup": "/foreign"}, {"MainPID": "canary"}, {"ExecMainCode": "1"},
                       {"ActiveState": "active", "SubState": "running"}, {"SubState": "unknown"}):
            with self.subTest(change=change), self.assertRaises(app.ApplicationError): app.classify_startup({**properties, **change}, IDS, GROUP)

    def test_disappearance_after_loaded_cannot_reset_startup_grace(self):
        absent = {"LoadState": "not-found", "ActiveState": "inactive", "SubState": "dead", "MainPID": "0", "ControlGroup": ""}
        self.assertEqual("pending", app.classify_startup(absent, IDS, GROUP))
        with self.assertRaises(app.ApplicationError): app.classify_startup(absent, IDS, GROUP, loaded_seen=True)


if __name__ == "__main__": unittest.main()
