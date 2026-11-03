#!/usr/bin/env python3
"""Run EvidenceWorkerExecution's real fatal path in isolated worker processes."""

from __future__ import annotations

import os
import hashlib
import json
import signal
import subprocess
import tempfile
import unittest
from pathlib import Path


FIXTURE = Path(__file__).resolve().parent
WORKER_DLL = FIXTURE / "LifecycleWorker" / "bin" / "Debug" / "net10.0" / "EvidenceHost.LifecycleWorker.dll"
OUTER_TIMEOUT_SECONDS = 12
TEARDOWN_TIMEOUT_SECONDS = 3
FAILURE_MARKERS = ("disposed", "artifact-hashed", "failure-manifest", "worker-finally")


class EvidenceWorkerExecutionLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if os.name != "posix":
            raise RuntimeError("This subprocess verifier requires POSIX process groups for owned-worker teardown.")
        if not WORKER_DLL.is_file():
            raise RuntimeError(
                "Lifecycle worker is not built. Run: dotnet build "
                "tests/evidencehost-consumer/LifecycleWorker/LifecycleWorker.csproj"
            )

    def test_synchronous_stall_before_task_return_uses_failfast(self) -> None:
        self.assert_fatal(
            "synchronous-stall",
            required=("worker-entered", "callback-entered", "admission-closed", "stop-requested"),
            forbidden=("joined", "disposer-entered", *FAILURE_MARKERS),
            failfast_message="Owned Evidence callback or write remained active",
        )

    def test_callback_ignoring_cancellation_uses_failfast(self) -> None:
        self.assert_fatal(
            "ignore-cancellation",
            required=("worker-entered", "callback-entered", "admission-closed", "stop-requested"),
            forbidden=("joined", "disposer-entered", *FAILURE_MARKERS),
            failfast_message="Owned Evidence callback or write remained active",
        )

    def test_blocked_tracked_write_and_pump_use_failfast(self) -> None:
        self.assert_fatal(
            "blocked-write-pump",
            required=(
                "worker-entered",
                "callback-entered",
                "write-entered",
                "pump-entered",
                "stage-callback-completed",
                "admission-closed",
                "stop-requested",
            ),
            forbidden=("joined", "disposer-entered", *FAILURE_MARKERS),
            failfast_message="Owned Evidence callback or write remained active",
        )

    def test_nonsettling_disposer_uses_failfast(self) -> None:
        self.assert_fatal(
            "nonsettling-disposer",
            required=(
                "worker-entered",
                "callback-entered",
                "admission-closed",
                "stage-returned",
                "stop-requested",
                "supervisor-exit-acknowledged",
                "disposer-entered",
            ),
            forbidden=FAILURE_MARKERS,
            failfast_message="Evidence cleanup did not settle within its budget",
        )

    def test_cooperative_timeout_joins_disposes_hashes_and_writes_failure_manifest_in_order(self) -> None:
        result = self.run_worker("cooperative")
        self.assertEqual(2, result.returncode, result.diagnostics)

        events = result.events
        required = (
            "callback-entered",
            "callback-settled",
            "joined",
            "disposer-entered",
            "disposed",
            "artifact-hashed",
            "failure-manifest",
            "worker-finally",
        )
        for name in required:
            self.assertIn(name, events)
        self.assert_in_order(events, required)
        self.assertLess(events.index("admission-closed"), events.index("stop-requested"), events)
        self.assertLess(events.index("stop-requested"), events.index("joined"), events)
        self.assertLess(events.index("supervisor-exit-acknowledged"), events.index("joined"), events)

        manifest = result.directory / "failure-manifest.json"
        self.assertTrue(manifest.is_file())
        manifest_data = json.loads(manifest.read_text(encoding="utf-8"))
        recorded_hash = (result.directory / "artifact.sha256").read_text(encoding="utf-8").strip()
        actual_hash = hashlib.sha256((result.directory / "artifact.bin").read_bytes()).hexdigest().upper()
        self.assertEqual("failed", manifest_data["outcome"])
        self.assertEqual("DeadlineExceeded", manifest_data["terminalCode"])
        self.assertEqual(actual_hash, recorded_hash)
        self.assertEqual(actual_hash, manifest_data["artifactSha256"])

    def assert_fatal(
        self,
        mode: str,
        *,
        required: tuple[str, ...],
        forbidden: tuple[str, ...],
        failfast_message: str,
    ) -> None:
        result = self.run_worker(mode)
        self.assertNotEqual(0, result.returncode, result.diagnostics)
        self.assertIn("Process terminated.", result.diagnostics)
        self.assertIn(failfast_message, result.diagnostics)

        for name in required:
            self.assertIn(name, result.events, f"missing {name!r} in {result.events!r}; {result.diagnostics}")
        for name in forbidden:
            self.assertNotIn(name, result.events, f"unexpected {name!r} in {result.events!r}")
        if "admission-closed" in required:
            self.assertLess(
                result.events.index("admission-closed"),
                result.events.index("stop-requested"),
                result.events,
            )

    def run_worker(self, mode: str) -> WorkerResult:
        temporary = tempfile.TemporaryDirectory(prefix=f"evidence-lifecycle-{mode}-")
        output_directory = Path(temporary.name)
        stdout_path = output_directory / "stdout.log"
        stderr_path = output_directory / "stderr.log"

        with stdout_path.open("wb") as stdout, stderr_path.open("wb") as stderr:
            process = subprocess.Popen(
                ["dotnet", str(WORKER_DLL), mode, str(output_directory)],
                cwd=output_directory,
                stdin=subprocess.DEVNULL,
                stdout=stdout,
                stderr=stderr,
                close_fds=True,
                start_new_session=True,
            )
            try:
                returncode = process.wait(timeout=OUTER_TIMEOUT_SECONDS)
            except subprocess.TimeoutExpired:
                try:
                    self.terminate_owned_group(process)
                finally:
                    temporary.cleanup()
                self.fail(f"{mode} exceeded the independent {OUTER_TIMEOUT_SECONDS}s process deadline")

        diagnostics = self.read_tail(stdout_path) + self.read_tail(stderr_path)
        events_path = output_directory / "events.log"
        events = tuple(events_path.read_text(encoding="utf-8").splitlines()) if events_path.is_file() else ()
        result = WorkerResult(returncode, diagnostics, events, output_directory, temporary)
        self.addCleanup(result.cleanup)
        return result

    def terminate_owned_group(self, process: subprocess.Popen[bytes]) -> None:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass

        try:
            process.wait(timeout=TEARDOWN_TIMEOUT_SECONDS)
        except subprocess.TimeoutExpired:
            self.fail(f"owned worker process group {process.pid} survived SIGKILL teardown")

    @staticmethod
    def read_tail(path: Path, limit: int = 32 * 1024) -> str:
        if not path.is_file():
            return ""
        with path.open("rb") as stream:
            stream.seek(0, os.SEEK_END)
            length = stream.tell()
            stream.seek(max(0, length - limit), os.SEEK_SET)
            return stream.read(limit).decode("utf-8", errors="replace")

    def assert_in_order(self, events: tuple[str, ...], expected: tuple[str, ...]) -> None:
        positions = [events.index(name) for name in expected]
        self.assertEqual(sorted(positions), positions, f"unexpected lifecycle ordering: {events!r}")


class WorkerResult:
    def __init__(
        self,
        returncode: int,
        diagnostics: str,
        events: tuple[str, ...],
        directory: Path,
        temporary: tempfile.TemporaryDirectory[str],
    ) -> None:
        self.returncode = returncode
        self.diagnostics = diagnostics
        self.events = events
        self.directory = directory
        self.temporary = temporary

    def cleanup(self) -> None:
        self.temporary.cleanup()


if __name__ == "__main__":
    unittest.main(verbosity=2)
