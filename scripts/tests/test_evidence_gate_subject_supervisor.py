"""Safety and forced-parent-loss tests for the OCI subject cleanup supervisor."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import signal
import stat
import subprocess
import sys
import tempfile
import textwrap
import time
from types import SimpleNamespace
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-subject-supervisor.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_subject_supervisor", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
supervisor = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = supervisor
SPEC.loader.exec_module(supervisor)


class FakeExecutor:
    def __init__(self, *, token: str, container_name: str, mountpoint: Path) -> None:
        self.token = token
        self.container_name = container_name
        self.mountpoint = mountpoint
        self.calls: list[list[str]] = []
        self.container_present = True
        self.owned = True
        self.fail_remove = False
        self.unmount_succeeds = True
        self.mount_is_present = True
        self.events: list[str] = []

    def __call__(self, arguments: list[str], **options: object) -> supervisor.CommandResult:
        self.calls.append(list(arguments))
        self.assert_options(options)
        command = arguments[1:]
        if command == ["--remote=false", "ps", "--all", "--no-trunc", "--format=json"]:
            items = [{"Names": [self.container_name]}] if self.container_present else []
            return self.result(items)
        if command == ["--remote=false", "inspect", "--format=json", self.container_name]:
            labels = {supervisor.OWNER_LABEL: self.token} if self.owned else {}
            return self.result(
                [
                    {
                        "Id": "a" * 64,
                        "Name": f"/{self.container_name}",
                        "Config": {"Labels": labels},
                        "Mounts": [
                            {
                                "Type": "bind",
                                "Source": str(self.mountpoint),
                                "Destination": "/scratch",
                            }
                        ],
                    }
                ]
            )
        if command == ["--remote=false", "rm", "--force", "--ignore", "a" * 64]:
            self.events.append("container-remove")
            if self.fail_remove:
                return supervisor.CommandResult(1, b"", b"not removed")
            self.container_present = False
            return supervisor.CommandResult(0, b"", b"")
        if arguments == [supervisor.SUDO_PATH, "-n", "umount", "--", str(self.mountpoint)]:
            self.events.append("umount")
            if self.unmount_succeeds:
                self.mount_is_present = False
                return supervisor.CommandResult(0, b"", b"")
            return supervisor.CommandResult(1, b"", b"still mounted")
        raise AssertionError(f"unexpected command: {arguments!r}")

    @staticmethod
    def assert_options(options: dict[str, object]) -> None:
        assert 0 < options["timeout_seconds"] <= supervisor.MAX_COMMAND_SECONDS
        assert options["maximum_output_bytes"] > 0
        assert options["environment"]["REGISTRY_AUTH_FILE"] == "/dev/null"
        assert set(options["environment"]) == {
            "PATH", "HOME", "XDG_RUNTIME_DIR", "TMPDIR", "REGISTRY_AUTH_FILE"
        }

    @staticmethod
    def result(value: object) -> supervisor.CommandResult:
        return supervisor.CommandResult(
            0, json.dumps(value, separators=(",", ":")).encode("utf-8"), b""
        )


class SubjectSupervisorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="subject-supervisor-tests-")
        self.root = Path(self.temporary.name).resolve()
        self.uid = os.geteuid()
        self.gid = os.getegid()
        self.runner_temp = self.root / "runner-temp"
        self.runner_temp.mkdir(mode=0o700)
        self.home = self.root / "home"
        self.home.mkdir(mode=0o700)
        self.runtime = self.root / "runtime"
        self.runtime.mkdir(mode=0o700)
        self.scratch = self.runner_temp / "subject-scratch"
        self.scratch.mkdir(mode=0o700)
        self.mountpoint = self.scratch / "quota-limited-scratch-0123456789abcdef"
        self.mountpoint.mkdir(mode=0o700)
        os.chown(self.mountpoint, self.uid, self.gid)
        self.token = "b" * 32
        self.container_name = "ase-subject-123456-2-0123456789ab"
        self.state_directory = self.runner_temp / f"appsurface-subject-supervisor-{self.token}"
        self.state_directory.mkdir(mode=0o700)
        self.manifest_path = self.state_directory / supervisor.MANIFEST_NAME
        self.parent_pid = 424_242
        self.parent_start_time = 123_456
        self._write_manifest()
        self.environment = {
            "RUNNER_TEMP": str(self.runner_temp),
            "HOME": str(self.home),
            "XDG_RUNTIME_DIR": str(self.runtime),
        }
        self.executor = FakeExecutor(
            token=self.token, container_name=self.container_name, mountpoint=self.mountpoint
        )
        self.mountinfo_text = self._mountinfo()
        self.fake_statvfs = SimpleNamespace(
            f_blocks=supervisor.TMPFS_BYTES // 4096,
            f_frsize=4096,
            f_files=supervisor.TMPFS_INODES,
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def _write_manifest(self, **overrides: object) -> None:
        document: dict[str, object] = {
            "schemaVersion": 1,
            "ownerToken": self.token,
            "containerName": self.container_name,
            "scratchDirectory": str(self.scratch),
            "mountpoint": str(self.mountpoint),
            "parentPid": self.parent_pid,
            "parentStartTime": self.parent_start_time,
            "scratchDevice": self.scratch.stat().st_dev,
            "scratchInode": self.scratch.stat().st_ino,
            "mountpointDevice": self.mountpoint.lstat().st_dev,
            "mountpointInode": self.mountpoint.lstat().st_ino,
        }
        document.update(overrides)
        payload = (json.dumps(document, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
        if self.manifest_path.exists():
            self.manifest_path.unlink()
        supervisor._write_new_file(self.state_directory, supervisor.MANIFEST_NAME, payload)

    def _mountinfo(self) -> str:
        options = (
            f"rw,nosuid,nodev,size={supervisor.TMPFS_BYTES},"
            f"nr_inodes={supervisor.TMPFS_INODES},mode=700,uid={self.uid},gid={self.gid}"
        )
        return f"21 1 0:42 / {self.mountpoint} rw,relatime - tmpfs tmpfs {options}\n"

    def _run_cleanup(self, *, deadline: float = 10_000) -> dict[str, str]:
        return supervisor._cleanup(
            supervisor.Manifest(
                self.token,
                self.container_name,
                self.scratch,
                self.mountpoint,
                self.parent_pid,
                self.parent_start_time,
                self.scratch.stat().st_dev,
                self.scratch.stat().st_ino,
                self.mountpoint.lstat().st_dev,
                self.mountpoint.lstat().st_ino,
            ),
            home=self.home,
            runtime=self.runtime,
            runner_temp=self.runner_temp,
            uid=self.uid,
            gid=self.gid,
            executor=self.executor,
            mountinfo_reader=lambda: self.mountinfo_text if self.executor.mount_is_present else "",
            statvfs=lambda _path: self.fake_statvfs,
            monotonic=lambda: 0.0,
            sleep=lambda _seconds: None,
            cleanup_deadline=deadline,
        )

    def test_cleanup_removes_owned_container_before_unmounting_exact_tmpfs(self) -> None:
        result = self._run_cleanup()

        self.assertEqual(
            {"containerStatus": "removed", "mountStatus": "unmounted", "scratchStatus": "removed"},
            result,
        )
        self.assertEqual(["container-remove", "umount"], self.executor.events)
        self.assertFalse(self.mountpoint.exists())

    def test_unowned_container_is_never_removed_and_its_mount_is_preserved(self) -> None:
        self.executor.owned = False

        result = self._run_cleanup()

        self.assertEqual("unverified", result["containerStatus"])
        self.assertTrue(self.executor.mount_is_present)
        self.assertFalse(any(" rm " in f" {' '.join(call)} " for call in self.executor.calls))
        self.assertNotIn("umount", self.executor.events)

    def test_container_removal_failure_preserves_mountpoint_and_tmpfs(self) -> None:
        self.executor.fail_remove = True

        result = self._run_cleanup()

        self.assertEqual("failed", result["containerStatus"])
        self.assertTrue(self.executor.mount_is_present)
        self.assertTrue(self.mountpoint.is_dir())
        self.assertNotIn("umount", self.executor.events)

    def test_unmount_failure_preserves_mountpoint(self) -> None:
        self.executor.unmount_succeeds = False

        result = self._run_cleanup()

        self.assertEqual("failed", result["mountStatus"])
        self.assertEqual("preserved", result["scratchStatus"])
        self.assertTrue(self.mountpoint.is_dir())

    def test_foreign_or_wrong_quota_mount_is_never_unmounted(self) -> None:
        self.mountinfo_text = self._mountinfo().replace("size=4294967296", "size=4294967295")

        result = self._run_cleanup()

        self.assertEqual("unverified", result["mountStatus"])
        self.assertTrue(self.executor.mount_is_present)
        self.assertNotIn("umount", self.executor.events)

    def test_subject_export_is_removed_but_unexpected_scratch_is_preserved(self) -> None:
        subject_result = self.scratch / supervisor.SUBJECT_RESULT_NAME
        subject_result.write_bytes(b"{}\n")
        os.chmod(subject_result, 0o600)

        result = self._run_cleanup()

        self.assertEqual("removed", result["scratchStatus"])
        self.assertFalse(subject_result.exists())
        self.assertFalse(self.scratch.exists())

    def test_unexpected_scratch_entry_is_never_deleted(self) -> None:
        unexpected = self.scratch / "subject-created-output.bin"
        unexpected.write_bytes(b"keep")

        result = self._run_cleanup()

        self.assertEqual("preserved", result["scratchStatus"])
        self.assertEqual(b"keep", unexpected.read_bytes())
        self.assertTrue(self.scratch.is_dir())

    def test_symlinked_mountpoint_is_rejected_before_commands(self) -> None:
        moved = self.scratch / "real-mountpoint"
        self.mountpoint.rename(moved)
        self.mountpoint.symlink_to(moved, target_is_directory=True)

        result = self._run_cleanup()

        self.assertEqual("unverified", result["containerStatus"])
        self.assertEqual([], self.executor.calls)
        self.assertTrue(moved.is_dir())

    def test_manifest_rejects_path_traversal_and_symlinked_state(self) -> None:
        for value in (str(self.runner_temp / ".." / "outside"), str(self.root / "elsewhere")):
            with self.subTest(value=value):
                self._write_manifest(mountpoint=value)
                with self.assertRaises(supervisor.SupervisorError):
                    supervisor._read_manifest(
                        self.manifest_path,
                        environment=self.environment,
                        uid=self.uid,
                        gid=self.gid,
                    )
        self._write_manifest()
        moved = self.runner_temp / f"moved-{self.token}"
        self.state_directory.rename(moved)
        self.state_directory.symlink_to(moved, target_is_directory=True)
        with self.assertRaises(supervisor.SupervisorError):
            supervisor._read_manifest(
                self.manifest_path,
                environment=self.environment,
                uid=self.uid,
                gid=self.gid,
            )

    def test_attestation_is_bounded_and_always_non_claiming(self) -> None:
        record = supervisor._attestation(
            trigger="parent-exit",
            statuses={
                "containerStatus": "removed",
                "mountStatus": "unmounted",
                "scratchStatus": "removed",
            },
            failure_code="none",
        )
        encoded = json.dumps(record, separators=(",", ":")).encode("ascii")

        self.assertLessEqual(len(encoded), supervisor.MAX_ATTESTATION_BYTES)
        self.assertFalse(record["claimEligible"])
        self.assertFalse(record["published"])
        self.assertEqual("complete", record["status"])

    def test_request_requires_matching_token_and_is_private(self) -> None:
        with self.assertRaises(supervisor.SupervisorError):
            supervisor._request_cleanup(
                self.manifest_path,
                "c" * 32,
                environment=self.environment,
            )

        supervisor._request_cleanup(
            self.manifest_path,
            self.token,
            environment=self.environment,
        )
        request = self.state_directory / supervisor.REQUEST_NAME
        self.assertEqual(0o600, stat.S_IMODE(request.stat().st_mode))
        self.assertEqual(
            {"ownerToken": self.token, "schemaVersion": 1},
            json.loads(request.read_text(encoding="ascii")),
        )

    def test_supervisor_triggers_when_fake_parent_proc_identity_disappears(self) -> None:
        fake_proc = self.root / "proc"
        parent_directory = fake_proc / str(self.parent_pid)
        parent_directory.mkdir(parents=True)
        stat_fields = ["S"] + ["0"] * 19
        stat_fields[19] = str(self.parent_start_time)
        (parent_directory / "stat").write_text(
            f"{self.parent_pid} (test parent) {' '.join(stat_fields)}\n", encoding="ascii"
        )
        clock = [0.0]

        def sleep(_seconds: float) -> None:
            clock[0] += 0.1
            (parent_directory / "stat").unlink(missing_ok=True)
            try:
                parent_directory.rmdir()
            except FileNotFoundError:
                pass

        exit_code = supervisor.run_supervisor(
            self.manifest_path,
            environment=self.environment,
            effective_uid=self.uid,
            effective_gid=self.gid,
            proc_root=fake_proc,
            mountinfo_reader=lambda: self._mountinfo() if self.executor.mount_is_present else "",
            executor=self.executor,
            statvfs=lambda _path: self.fake_statvfs,
            monotonic=lambda: clock[0],
            sleep=sleep,
        )

        record = json.loads((self.state_directory / supervisor.ATTESTATION_NAME).read_text(encoding="ascii"))
        self.assertEqual(0, exit_code, record)
        self.assertEqual("parent-exit", record["trigger"])
        self.assertEqual("complete", record["status"])
        self.assertFalse(record["claimEligible"])


class ForcedParentKillIntegrationTests(unittest.TestCase):
    def test_detached_supervisor_reaps_fake_resources_after_launcher_sigkill(self) -> None:
        """Exercise real process independence with fake Podman and mount operations."""
        with tempfile.TemporaryDirectory(prefix="subject-supervisor-kill-") as temporary:
            root = Path(temporary).resolve()
            runner_temp = root / "runner-temp"
            runner_temp.mkdir(mode=0o700)
            home = root / "home"
            home.mkdir(mode=0o700)
            runtime = root / "runtime"
            runtime.mkdir(mode=0o700)
            scratch = runner_temp / "subject-scratch"
            scratch.mkdir(mode=0o700)
            mountpoint = scratch / "quota-limited-scratch-0123456789abcdef"
            mountpoint.mkdir(mode=0o700)
            uid = os.geteuid()
            gid = os.getegid()
            os.chown(mountpoint, uid, gid)
            token = "d" * 32
            container_name = "ase-subject-123456-2-fedcba987654"
            state = runner_temp / f"appsurface-subject-supervisor-{token}"
            state.mkdir(mode=0o700)
            manifest_path = state / supervisor.MANIFEST_NAME
            ready_path = root / "launcher-ready"
            fake_proc = root / "fake-proc"
            resources_path = root / "fake-resource-state.json"
            event_path = root / "events.json"
            resources_path.write_text(
                json.dumps({"containerPresent": True, "mountPresent": True}),
                encoding="ascii",
            )

            supervisor_code = textwrap.dedent(
                """
                import importlib.util, json, os, sys, time
                from pathlib import Path
                from types import SimpleNamespace
                spec = importlib.util.spec_from_file_location('egss', sys.argv[1])
                module = importlib.util.module_from_spec(spec); sys.modules[spec.name] = module; spec.loader.exec_module(module)
                manifest_path, state_path, events_path, mountpoint = map(Path, sys.argv[2:6])
                token, container_name = sys.argv[6:8]
                proc_root = Path(sys.argv[8])
                manifest = json.loads(manifest_path.read_text(encoding='ascii'))
                parent_pid = manifest['parentPid']
                parent_start = manifest['parentStartTime']
                state = json.loads(state_path.read_text(encoding='ascii'))
                class Executor:
                    def __call__(self, arguments, **options):
                        command = arguments[1:]
                        events = json.loads(events_path.read_text(encoding='ascii')) if events_path.exists() else []
                        if command[:2] == ['--remote=false', 'ps']:
                            value = [{'Names':[container_name]}] if state['containerPresent'] else []
                            result = module.CommandResult(0, json.dumps(value).encode(), b'')
                        elif command[:2] == ['--remote=false', 'inspect']:
                            value = [{'Id':'a'*64,'Name':'/'+container_name,'Config':{'Labels':{module.OWNER_LABEL:token}},'Mounts':[{'Type':'bind','Source':str(mountpoint),'Destination':'/scratch'}]}]
                            result = module.CommandResult(0, json.dumps(value).encode(), b'')
                        elif command[:2] == ['--remote=false', 'rm']:
                            events.append('container-remove'); state['containerPresent'] = False
                            state_path.write_text(json.dumps(state), encoding='ascii')
                            result = module.CommandResult(0,b'',b'')
                        elif arguments[:3] == [module.SUDO_PATH, '-n', 'umount']:
                            events.append('umount'); state['mountPresent'] = False
                            state_path.write_text(json.dumps(state), encoding='ascii')
                            result = module.CommandResult(0,b'',b'')
                        else: raise AssertionError(arguments)
                        events_path.write_text(json.dumps(events), encoding='ascii')
                        return result
                def mountinfo():
                    if not state['mountPresent']: return ''
                    options = f'rw,nosuid,nodev,size={module.TMPFS_BYTES},nr_inodes={module.TMPFS_INODES},mode=700,uid={os.geteuid()},gid={os.getegid()}'
                    return f'21 1 0:42 / {mountpoint} rw,relatime - tmpfs tmpfs {options}' + chr(10)
                fake_statvfs = SimpleNamespace(f_blocks=module.TMPFS_BYTES//4096, f_frsize=4096, f_files=module.TMPFS_INODES)
                def parent_identity(pid):
                    try: os.kill(pid, 0)
                    except ProcessLookupError: return (parent_start, 'Z')
                    return (parent_start, 'S')
                environment = {'RUNNER_TEMP':str(Path(manifest_path).parent.parent),'HOME':os.environ['HOME'],'XDG_RUNTIME_DIR':os.environ['XDG_RUNTIME_DIR']}
                sys.exit(module.run_supervisor(manifest_path, environment=environment, proc_root=proc_root, proc_identity_reader=parent_identity, executor=Executor(), mountinfo_reader=mountinfo, statvfs=lambda _:fake_statvfs))
                """
            )
            launcher_code = textwrap.dedent(
                """
                import importlib.util, json, os, subprocess, sys, time
                from pathlib import Path
                spec = importlib.util.spec_from_file_location('egss', sys.argv[1])
                module = importlib.util.module_from_spec(spec); sys.modules[spec.name] = module; spec.loader.exec_module(module)
                manifest_path, supervisor_code, ready_path, state_path, events_path, mountpoint = map(str, sys.argv[2:8])
                proc_root = Path(sys.argv[8])
                document = {'schemaVersion':1,'ownerToken':'d'*32,'containerName':'ase-subject-123456-2-fedcba987654','scratchDirectory':str(Path(mountpoint).parent),'mountpoint':mountpoint,'parentPid':os.getpid(),'parentStartTime':987654321}
                scratch_info = Path(mountpoint).parent.stat(); mount_info = Path(mountpoint).lstat()
                document.update({'scratchDevice':scratch_info.st_dev,'scratchInode':scratch_info.st_ino,'mountpointDevice':mount_info.st_dev,'mountpointInode':mount_info.st_ino})
                Path(manifest_path).write_text(json.dumps(document,sort_keys=True,separators=(',',':'))+chr(10),encoding='ascii')
                os.chmod(manifest_path,0o600)
                (proc_root / str(os.getpid())).mkdir(parents=True,mode=0o700)
                command = [sys.executable,'-c',supervisor_code,sys.argv[1],manifest_path,state_path,events_path,mountpoint,'d'*32,'ase-subject-123456-2-fedcba987654',str(proc_root)]
                child_environment = {'PATH':'/usr/bin:/bin','HOME':os.environ['HOME'],'XDG_RUNTIME_DIR':os.environ['XDG_RUNTIME_DIR'],'RUNNER_TEMP':str(Path(manifest_path).parent.parent)}
                subprocess.Popen(command,stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True,close_fds=True,cwd='/',env=child_environment)
                Path(ready_path).write_text('ready',encoding='ascii')
                while True: time.sleep(60)
                """
            )
            launcher_environment = dict(os.environ)
            launcher_environment.update(
                {"RUNNER_TEMP": str(runner_temp), "HOME": str(home), "XDG_RUNTIME_DIR": str(runtime)}
            )
            launcher = subprocess.Popen(
                [
                    sys.executable,
                    "-c",
                    launcher_code,
                    str(SCRIPT),
                    str(manifest_path),
                    supervisor_code,
                    str(ready_path),
                    str(resources_path),
                    str(event_path),
                    str(mountpoint),
                    str(fake_proc),
                ],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                start_new_session=True,
                env=launcher_environment,
            )
            try:
                deadline = time.monotonic() + 10
                while not ready_path.exists() and time.monotonic() < deadline:
                    if launcher.poll() is not None:
                        stderr = launcher.stderr.read().decode("utf-8", errors="replace") if launcher.stderr else ""
                        self.fail(f"launcher exited before starting supervisor: {stderr}")
                    time.sleep(0.02)
                self.assertTrue(ready_path.exists(), "launcher did not start detached supervisor")
                os.kill(launcher.pid, signal.SIGKILL)
                launcher.wait(timeout=5)

                attestation_path = state / supervisor.ATTESTATION_NAME
                deadline = time.monotonic() + 15
                while not attestation_path.exists() and time.monotonic() < deadline:
                    time.sleep(0.05)
                self.assertTrue(attestation_path.exists(), "detached supervisor did not attest cleanup")
                attestation = json.loads(attestation_path.read_text(encoding="ascii"))
                resources = json.loads(resources_path.read_text(encoding="ascii"))
                events = json.loads(event_path.read_text(encoding="ascii"))
                self.assertEqual("complete", attestation["status"])
                self.assertFalse(attestation["claimEligible"])
                self.assertEqual({"containerPresent": False, "mountPresent": False}, resources)
                self.assertEqual(["container-remove", "umount"], events)
                self.assertFalse(mountpoint.exists())
                self.assertFalse(scratch.exists())
            finally:
                if launcher.poll() is None:
                    launcher.kill()
                    launcher.wait(timeout=5)
                if launcher.stderr is not None:
                    launcher.stderr.close()


if __name__ == "__main__":
    unittest.main()
