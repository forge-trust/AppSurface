"""Run the proof harness's actual lifecycle functions against isolated local files.

No package build, network access, or application host is needed; process cleanup uses
only a temporary harmless sleep process.
"""

from __future__ import annotations

import os
import pathlib
import shlex
import subprocess
import tempfile
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
HARNESS = REPO_ROOT / "scripts/verify-nested-collection-consumer.sh"


class NestedCollectionLifecycleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        self.proofs = self.root / ".gstack/issue-868/packproof"
        self.proofs.mkdir(parents=True)
        self.run = self.proofs / "candidate"
        self.run.mkdir()
        self.latest = self.proofs / "LATEST"
        self.latest.write_text("previous-success\n", encoding="utf-8")
        self.commands = self.root / "commands"
        self.commands.mkdir()
        self.host_calls = self.root / "host-calls.txt"
        launch_stub = self.commands / "nohup"
        launch_stub.write_text(
            '#!/bin/sh\nprintf "unexpected launch\\n" >> "$HARNESS_TEST_HOST_CALLS"\nexit 91\n',
            encoding="utf-8",
        )
        launch_stub.chmod(0o755)
        source = HARNESS.read_text(encoding="utf-8")
        self.functions = source[source.index("fail() {"):source.index('\ncase "$COMMAND" in')]

    def invoke(self, command: str) -> subprocess.CompletedProcess[str]:
        environment = os.environ.copy()
        environment.update({
            "REPO_ROOT": str(self.root),
            "PROOFS_ROOT": str(self.proofs),
            "RUN_DIR": str(self.run),
            "STAGE": "lifecycle regression",
            "START_LOCK_DIR": "",
            "START_LOCK_ACQUIRED": "false",
            "START_OWNED_PID": "",
            "START_OWNED_DLL": "",
            "START_OWNED_URL": "",
            "HARNESS_TEST_OWNED_DLL": getattr(self, "environment_owned_dll", ""),
            "PATH": str(self.commands) + os.pathsep + environment["PATH"],
            "HARNESS_TEST_HOST_CALLS": str(self.host_calls),
        })
        return subprocess.run(
            ["bash", "-Eeuo", "pipefail", "-c", self.functions + "\n" + command],
            capture_output=True, text=True, check=False, env=environment, timeout=10,
        )

    @staticmethod
    def stop_process(process: subprocess.Popen[bytes]) -> None:
        if process.poll() is None:
            process.kill()
        process.wait()

    def test_success_publishes_marker_and_replaces_default_after_receipt(self) -> None:
        (self.run / "receipt.md").write_text("completed proof\n", encoding="utf-8")
        result = self.invoke('publish_prepared_run candidate')

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue((self.run / "PREPARED").is_file())
        self.assertEqual(self.latest.read_text(), "candidate\n")
        self.assertFalse((self.proofs / "LATEST.candidate").exists())

    def test_missing_or_empty_receipt_cannot_publish_or_replace_default(self) -> None:
        for empty in (False, True):
            with self.subTest(empty=empty):
                if empty:
                    (self.run / "receipt.md").touch()
                result = self.invoke('publish_prepared_run candidate')

                self.assertNotEqual(result.returncode, 0)
                self.assertIn("receipt is missing or empty", result.stderr)
                self.assertFalse((self.run / "PREPARED").exists())
                self.assertEqual(self.latest.read_text(), "previous-success\n")

    def test_prepare_failure_retains_successful_default_and_records_failure(self) -> None:
        # Stop after the real source snapshot, before any network or build action.
        result = self.invoke('git() { return 19; }; prepare')

        self.assertEqual(result.returncode, 19, result.stderr)
        self.assertEqual(self.latest.read_text(), "previous-success\n")
        failures = list(self.proofs.glob("*/FAILURE.txt"))
        self.assertEqual(len(failures), 1)
        self.assertIn("recording source identity (exit 19)", failures[0].read_text())
        self.assertFalse((failures[0].parent / "PREPARED").exists())

    def test_start_rejects_incomplete_runs_even_with_a_leftover_dll(self) -> None:
        dll = self.run / "consumer-host/bin/Release/net10.0/RazorWireWebExample.dll"
        dll.parent.mkdir(parents=True)
        dll.touch()
        for marker, receipt in ((False, False), (False, True), (True, False)):
            with self.subTest(marker=marker, receipt=receipt):
                (self.run / "PREPARED").unlink(missing_ok=True)
                (self.run / "receipt.md").unlink(missing_ok=True)
                if marker:
                    (self.run / "PREPARED").touch()
                if receipt:
                    (self.run / "receipt.md").write_text("receipt\n", encoding="utf-8")
                result = self.invoke('start_host')

                self.assertNotEqual(result.returncode, 0)
                self.assertIn("not completely prepared", result.stderr)
                self.assertFalse((self.run / "host.pid").exists())
                self.assertFalse(self.host_calls.exists())
                self.assertIn("not completely prepared", (self.run / "FAILURE.txt").read_text())

    def test_default_and_explicit_selection_both_enforce_completion(self) -> None:
        self.latest.write_text("candidate\n", encoding="utf-8")
        for arguments in ('start', 'start candidate'):
            with self.subTest(arguments=arguments):
                result = self.invoke(f'resolve_run_dir {arguments}; start_host')
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("not completely prepared", result.stderr)
                self.assertFalse((self.run / "host.pid").exists())
                self.assertFalse(self.host_calls.exists())

    def test_start_lock_contention_fails_fast_without_removing_other_lock(self) -> None:
        lock = self.run / "start.lock"
        lock.mkdir()
        result = self.invoke('start_host')

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Another start operation is already active", result.stderr)
        self.assertTrue(lock.is_dir())

    def test_start_lock_is_released_after_success_and_failure(self) -> None:
        for command, expected in (("exit 0", 0), ("exit 23", 23)):
            with self.subTest(command=command):
                result = self.invoke(
                    'START_LOCK_DIR="$RUN_DIR/start.lock"; '
                    'mkdir "$START_LOCK_DIR"; START_LOCK_ACQUIRED=true; ' + command
                )
                self.assertEqual(result.returncode, expected, result.stderr)
                self.assertFalse((self.run / "start.lock").exists())

    def test_failed_start_cleanup_stops_only_the_owned_matching_process(self) -> None:
        dll = self.run / "consumer-host/bin/Release/net10.0/RazorWireWebExample.dll"
        dll.parent.mkdir(parents=True)
        dll.touch()
        owned = subprocess.Popen(
            ["bash", "-c", 'exec -a "$1" sleep 30', "_", str(dll)],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        self.addCleanup(self.stop_process, owned)
        ps_stub = self.commands / "ps"
        ps_stub.write_text(
            '#!/bin/sh\nprintf "%s\\n" "$HARNESS_TEST_OWNED_DLL"\n',
            encoding="utf-8",
        )
        ps_stub.chmod(0o755)
        self.environment_owned_dll = str(dll)
        (self.run / "host.pid").write_text(f"{owned.pid}\n")
        (self.run / "host.url").write_text("http://127.0.0.1:12345\n")
        command = (
            f'kill() {{ if [[ "$1" == "-0" ]]; then return 1; else builtin kill "$@"; fi; }}; '
            f'START_OWNED_PID={owned.pid}; START_OWNED_DLL={str(dll)!r}; '
            'START_OWNED_URL=http://127.0.0.1:12345; exit 29'
        )
        result = self.invoke(command)

        self.assertEqual(result.returncode, 29, result.stderr)
        owned.wait(timeout=3)
        self.assertFalse((self.run / "host.pid").exists())
        self.assertFalse((self.run / "host.url").exists())

    def test_failed_start_cleanup_preserves_reused_host(self) -> None:
        dll = self.run / "consumer-host/bin/Release/net10.0/RazorWireWebExample.dll"
        dll.parent.mkdir(parents=True)
        dll.touch()
        reused = subprocess.Popen(["sleep", "30"], stdout=subprocess.DEVNULL)
        self.addCleanup(self.stop_process, reused)
        pid_file = self.run / "host.pid"
        url_file = self.run / "host.url"
        pid_file.write_text(f"{reused.pid}\n")
        url_file.write_text("http://127.0.0.1:54321\n")
        result = self.invoke("exit 31")

        self.assertEqual(result.returncode, 31, result.stderr)
        self.assertIsNone(reused.poll())
        self.assertEqual(pid_file.read_text(), f"{reused.pid}\n")
        self.assertEqual(url_file.read_text(), "http://127.0.0.1:54321\n")

    def test_failed_start_retains_metadata_when_owned_host_resists_termination(self) -> None:
        dll = self.run / "consumer-host/bin/Release/net10.0/RazorWireWebExample.dll"
        dll.parent.mkdir(parents=True)
        dll.touch()
        pid_file = self.run / "host.pid"
        url_file = self.run / "host.url"
        pid_file.write_text("4242\n")
        url_file.write_text("http://127.0.0.1:4242\n")
        ps_stub = self.commands / "ps"
        ps_stub.write_text(
            '#!/bin/sh\nprintf "%s\\n" "$HARNESS_TEST_OWNED_DLL"\n',
            encoding="utf-8",
        )
        ps_stub.chmod(0o755)
        sleep_stub = self.commands / "sleep"
        sleep_stub.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
        sleep_stub.chmod(0o755)
        self.environment_owned_dll = str(dll)
        result = self.invoke(
            f'kill() {{ return 0; }}; START_OWNED_PID=4242; '
            f'START_OWNED_DLL={shlex.quote(str(dll))}; '
            'START_OWNED_URL=http://127.0.0.1:4242; exit 37'
        )

        self.assertEqual(result.returncode, 37, result.stderr)
        self.assertIn("still running after 20 seconds", result.stderr)
        self.assertEqual(pid_file.read_text(), "4242\n")
        self.assertEqual(url_file.read_text(), "http://127.0.0.1:4242\n")

    def test_nuget_config_xml_escapes_special_local_feed_path(self) -> None:
        local_feed = self.root / 'feed & <"quoted>'
        local_feed.mkdir()
        destination = self.run / "NuGet.config"
        result = self.invoke(f'write_nuget_config {str(destination)!r} {str(local_feed)!r}')

        self.assertEqual(result.returncode, 0, result.stderr)
        import xml.etree.ElementTree as ET

        root = ET.parse(destination).getroot()
        local = root.find("./packageSources/add[@key='issue-868-local']")
        self.assertIsNotNone(local)
        self.assertEqual(local.attrib["value"], str(local_feed))


if __name__ == "__main__":
    unittest.main()
