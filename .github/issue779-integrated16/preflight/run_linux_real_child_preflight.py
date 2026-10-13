#!/usr/bin/env python3
"""Run only the Linux real-child ownership preflight before the build step."""

from __future__ import annotations

import argparse
import hashlib
import importlib
import json
import os
import pathlib
import shlex
import signal
import stat
import sys
import tempfile
import time
import unittest
from unittest import mock


PREFLIGHT_SECONDS = 45
LOG_LIMIT = 1024 * 1024
INPUT_LIMIT = 4 * 1024 * 1024
CAP_CASE_LIMIT = 1024
EXPECTED_BUILDER_SHA256 = "f9500143e2d554de51e03f9ff9d3899287369394a6689c381599f22541d4b9c7"
EXPECTED_TESTS_SHA256 = "4d37c8944a26ada6e2d4550359310dfbe2d3701a7b867b1bfd64ae6d807a7815"
EXPECTED_CONTRACT_SHA256 = "4e1e774f57d1b878a0985ac218a27490ae4de8da1f21f81cd696dbe6e9c399f8"
INPUT_PINS = {
    "integrated16_build.py": EXPECTED_BUILDER_SHA256,
    "integrated16_build_contract.py": EXPECTED_CONTRACT_SHA256,
    "test_integrated16_build.py": EXPECTED_TESTS_SHA256,
}
TEST_NAMES = (
    "test_real_recipe_signals_while_leader_is_still_owned",
    "test_real_descendant_cleanup_and_combined_pipe_cap",
)
EXPECTED_GROUP_LAUNCHES = 4


class PreflightDeadline(TimeoutError):
    pass


class BoundedLog:
    def __init__(self, path: pathlib.Path, limit: int) -> None:
        self.path = path
        self.limit = limit
        self.written = 0
        self.overflow = False
        self.fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)

    def write(self, text: str) -> int:
        data = text.encode("utf-8", errors="replace")
        available = max(0, self.limit - self.written)
        kept = data[:available]
        offset = 0
        while offset < len(kept):
            offset += os.write(self.fd, kept[offset:])
        self.written += len(kept)
        self.overflow = self.overflow or len(kept) != len(data)
        return len(text)

    def flush(self) -> None:
        os.fsync(self.fd)

    def close(self) -> None:
        self.flush()
        os.close(self.fd)


class CombinedLogCapControl(unittest.TestCase):
    """Keep the second half of the existing combined-cap test independently runnable."""

    def test_combined_log_cap_joins_real_process_group(self) -> None:
        builder_tests = importlib.import_module("test_integrated16_build")
        build = importlib.import_module("integrated16_build")
        writer = "import os; os.write(1,b'x'*700); os.write(2,b'y'*500)"
        script = "#!/bin/bash\nexec " + shlex.join([sys.executable, "-c", writer]) + "\n"
        with tempfile.TemporaryDirectory(prefix="issue779-real-cap-") as temp:
            root = pathlib.Path(temp)
            fixture = builder_tests.IntegratedBuildTests("test_fixed_selector_and_case_closure")
            image, capture = fixture._real_child_image(root, script)
            harness = root / "harness"
            harness.mkdir()
            launched: list[object] = []
            launch_kwargs: list[dict[str, object]] = []

            def launch(*args: object, **kwargs: object) -> object:
                process = builder_tests.REAL_POPEN(*args, **kwargs)
                launched.append(process)
                launch_kwargs.append(dict(kwargs))
                return process

            with mock.patch.object(build, "MAX_OUTER_LOG_BYTES", CAP_CASE_LIMIT), \
                 mock.patch.object(build, "TERM_GRACE_SECONDS", 0.01):
                with self.assertRaisesRegex(build.BuildExecutionError, "outer-recipe-log-bound") as caught:
                    build._run_child(
                        image,
                        harness,
                        capture,
                        time.monotonic() + 5,
                        time.monotonic() + 7,
                        popen=launch,
                    )

            self.assertEqual(len(launched), 1)
            self.assertTrue(launch_kwargs[0].get("start_new_session"))
            joined = getattr(caught.exception, "join", None)
            self.assertIsInstance(joined, dict)
            self.assertIs(joined.get("waited"), True)
            self.assertIs(joined.get("group_absent"), True)
            self.assertIs(joined.get("readers_joined"), True)
            self.assertIsNotNone(launched[0].returncode)
            retained = image.stdout_path.stat().st_size + image.stderr_path.stat().st_size
            self.assertEqual(retained, CAP_CASE_LIMIT)


def _hash(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


class InputInventoryError(ValueError):
    """A pinned preflight input is missing, unsafe, or has changed."""


def validate_input_inventory(
    builder_dir: pathlib.Path,
) -> dict[str, dict[str, int | str]]:
    """Validate the closed, single-link module inventory without importing it."""
    inventory: dict[str, dict[str, int | str]] = {}
    for name in sorted(INPUT_PINS):
        path = builder_dir / name
        try:
            before = path.lstat()
        except OSError as exc:
            raise InputInventoryError("input-missing") from exc
        if not stat.S_ISREG(before.st_mode) or stat.S_ISLNK(before.st_mode):
            raise InputInventoryError("input-not-regular-file")
        if stat.S_IMODE(before.st_mode) != 0o644:
            raise InputInventoryError("input-mode-mismatch")
        if before.st_nlink != 1:
            raise InputInventoryError("input-link-count-mismatch")
        if before.st_size < 0 or before.st_size > INPUT_LIMIT:
            raise InputInventoryError("input-size-limit-exceeded")

        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
        try:
            fd = os.open(path, flags)
        except OSError as exc:
            raise InputInventoryError("input-open-failed") from exc
        try:
            opened = os.fstat(fd)
            stable_fields = ("st_dev", "st_ino", "st_mode", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
            if (
                not stat.S_ISREG(opened.st_mode)
                or stat.S_IMODE(opened.st_mode) != 0o644
                or opened.st_nlink != 1
                or any(getattr(before, field) != getattr(opened, field) for field in stable_fields)
            ):
                raise InputInventoryError("input-changed-during-open")
            digest = hashlib.sha256()
            total = 0
            read_limit = before.st_size + 1
            while True:
                if total >= read_limit:
                    break
                chunk = os.read(fd, min(1024 * 1024, read_limit - total))
                if not chunk:
                    break
                digest.update(chunk)
                total += len(chunk)
                if total > before.st_size:
                    raise InputInventoryError("input-grew-during-read")
            if total != before.st_size:
                raise InputInventoryError("input-size-changed-during-read")
            opened_after = os.fstat(fd)
            try:
                path_after = path.lstat()
            except OSError as exc:
                raise InputInventoryError("input-disappeared-during-read") from exc
            if any(getattr(opened, field) != getattr(opened_after, field) for field in stable_fields):
                raise InputInventoryError("input-changed-during-read")
            if (
                not stat.S_ISREG(path_after.st_mode)
                or any(getattr(path_after, field) != getattr(opened_after, field) for field in stable_fields)
                or stat.S_IMODE(path_after.st_mode) != 0o644
                or path_after.st_nlink != 1
            ):
                raise InputInventoryError("input-path-substituted-during-read")
            sha256 = digest.hexdigest()
        finally:
            os.close(fd)
        if sha256 != INPUT_PINS[name]:
            raise InputInventoryError("input-sha256-mismatch")
        inventory[name] = {
            "sha256": sha256,
            "mode": stat.S_IMODE(path_after.st_mode),
            "nlink": path_after.st_nlink,
            "device": path_after.st_dev,
            "inode": path_after.st_ino,
            "size": path_after.st_size,
        }
    return inventory


def _private_tmp_is_safe() -> bool:
    path = pathlib.Path("/private/tmp")
    try:
        info = path.lstat()
    except OSError:
        return False
    return (
        stat.S_ISDIR(info.st_mode)
        and info.st_uid == 0
        and stat.S_IMODE(info.st_mode) == 0o1777
        and os.access(path, os.W_OK | os.X_OK)
    )


def _write_receipt(path: pathlib.Path, value: dict[str, object]) -> None:
    raw = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode("utf-8")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        offset = 0
        while offset < len(raw):
            offset += os.write(fd, raw[offset:])
        os.fsync(fd)
    finally:
        os.close(fd)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--builder-dir", required=True, type=pathlib.Path)
    parser.add_argument("--receipt", required=True, type=pathlib.Path)
    parser.add_argument("--log", required=True, type=pathlib.Path)
    args = parser.parse_args(argv)

    started = time.monotonic()
    builder_dir = args.builder_dir.resolve(strict=True)
    receipt_path = args.receipt.resolve(strict=False)
    log_path = args.log.resolve(strict=False)
    if receipt_path == log_path or not receipt_path.parent.is_dir() or not log_path.parent.is_dir():
        parser.error("receipt and log must be distinct paths with existing parent directories")
    if os.path.lexists(receipt_path) or os.path.lexists(log_path):
        parser.error("receipt and log paths must be fresh")

    input_hashes: dict[str, str | None] = {name: None for name in sorted(INPUT_PINS)}
    initial_inventory: dict[str, dict[str, int | str]] | None = None
    stream = BoundedLog(log_path, LOG_LIMIT)
    status = "failed"
    detail = None
    test_ids: list[str] = []
    tests_run = failures = errors = skipped = 0
    group_checks = 0
    group_launches: list[dict[str, object]] = []
    elapsed = 0.0
    try:
        if sys.platform != "linux":
            status, detail = "precondition-failed", "linux-required"
        elif not _private_tmp_is_safe():
            status, detail = "precondition-failed", "root-owned-sticky-private-tmp-required"
        else:
            try:
                initial_inventory = validate_input_inventory(builder_dir)
                input_hashes = {name: str(item["sha256"]) for name, item in initial_inventory.items()}
            except InputInventoryError as exc:
                status, detail = "precondition-failed", f"builder-{exc}"

            if initial_inventory is not None:
                sys.path.insert(0, str(builder_dir))
                builder_tests = importlib.import_module("test_integrated16_build")
                build = importlib.import_module("integrated16_build")
                if not builder_tests.HAS_WAITID_WNOWAIT:
                    status, detail = "precondition-failed", "waitid-wnowait-required"
                else:
                    build.MAX_OUTER_LOG_BYTES = LOG_LIMIT
                    suite = unittest.TestSuite([
                        builder_tests.IntegratedBuildTests(TEST_NAMES[0]),
                        builder_tests.IntegratedBuildTests(TEST_NAMES[1]),
                        CombinedLogCapControl("test_combined_log_cap_joins_real_process_group"),
                    ])
                    test_ids = [test.id() for test in suite]
                    expected_ids = [
                        "test_integrated16_build.IntegratedBuildTests." + TEST_NAMES[0],
                        "test_integrated16_build.IntegratedBuildTests." + TEST_NAMES[1],
                        __name__ + ".CombinedLogCapControl.test_combined_log_cap_joins_real_process_group",
                    ]
                    if test_ids != expected_ids:
                        status, detail = "precondition-failed", "selected-test-set-mismatch"
                    else:
                        real_popen = builder_tests.REAL_POPEN

                        def record_real_popen(*popen_args: object, **popen_kwargs: object) -> object:
                            process = real_popen(*popen_args, **popen_kwargs)
                            try:
                                observed_pgid = os.getpgid(process.pid)
                            except OSError:
                                observed_pgid = None
                            group_launches.append({
                                "leader_pid": process.pid,
                                "new_session_requested": popen_kwargs.get("start_new_session") is True,
                                "expected_pgid": process.pid,
                                "observed_pgid": observed_pgid,
                                "pgid_matches_leader": observed_pgid == process.pid,
                            })
                            return process

                        builder_tests.REAL_POPEN = record_real_popen
                        deadline_state: dict[str, object] = {"expired": False, "result": None}
                        inventory_recheck_error: str | None = None

                        class BoundedTextTestRunner(unittest.TextTestRunner):
                            def _makeResult(self) -> unittest.TestResult:
                                result = super()._makeResult()
                                deadline_state["result"] = result
                                return result

                        def expire_preflight(_signum: int, _frame: object) -> None:
                            deadline_state["expired"] = True
                            result = deadline_state["result"]
                            if isinstance(result, unittest.TestResult):
                                result.stop()
                            raise PreflightDeadline("45-second preflight deadline")

                        previous_handler = signal.signal(signal.SIGALRM, expire_preflight)
                        previous_timer = signal.getitimer(signal.ITIMER_REAL)
                        remaining = max(0.001, PREFLIGHT_SECONDS - (time.monotonic() - started))
                        signal.setitimer(signal.ITIMER_REAL, remaining)
                        try:
                            result = BoundedTextTestRunner(stream=stream, verbosity=2).run(suite)
                        except PreflightDeadline:
                            status, detail = "failed", "preflight-deadline"
                            result = deadline_state["result"]
                        finally:
                            signal.setitimer(signal.ITIMER_REAL, *previous_timer)
                            signal.signal(signal.SIGALRM, previous_handler)
                            builder_tests.REAL_POPEN = real_popen
                            try:
                                after_inventory = validate_input_inventory(builder_dir)
                                if after_inventory != initial_inventory:
                                    inventory_recheck_error = "input-inventory-substituted"
                            except InputInventoryError as exc:
                                inventory_recheck_error = str(exc)
                        if result is not None:
                            tests_run = result.testsRun
                            failures = len(result.failures)
                            errors = len(result.errors)
                            skipped = len(result.skipped)
                            unsuccessful_ids = {
                                failed.id() for failed, _ in result.failures + result.errors + result.skipped
                            }
                            successful_test_ids = set(test_ids) - unsuccessful_ids
                            group_checks = len(successful_test_ids)
                            elapsed = time.monotonic() - started
                            pgid_checks = sum(item["pgid_matches_leader"] is True for item in group_launches)
                            session_checks = sum(item["new_session_requested"] is True for item in group_launches)
                            passed = (
                                not deadline_state["expired"]
                                and tests_run == 3
                                and len(successful_test_ids) == 3
                                and failures == errors == skipped == 0
                                and group_checks == 3
                                and len(group_launches) == EXPECTED_GROUP_LAUNCHES
                                and pgid_checks == session_checks == EXPECTED_GROUP_LAUNCHES
                                and not stream.overflow
                                and inventory_recheck_error is None
                                and elapsed < PREFLIGHT_SECONDS
                            )
                            if deadline_state["expired"]:
                                status, detail = "failed", "preflight-deadline"
                            elif inventory_recheck_error is not None:
                                status, detail = "failed", f"builder-input-recheck-{inventory_recheck_error}"
                            else:
                                status = "passed" if passed else "failed"
                            if not passed and detail is None:
                                detail = "exact-count-group-join-log-or-deadline-check-failed"
    except BaseException as exc:
        status = "failed"
        detail = f"runner-exception:{type(exc).__name__}"
        stream.write(f"\npreflight-runner-exception: {type(exc).__name__}\n")
    finally:
        elapsed = time.monotonic() - started
        stream.close()

    log_sha = _hash(log_path)
    terminal = {
        "schema": "issue779-integrated16-linux-real-child-preflight-v1",
        "status": status,
        "detail": detail,
        "platform": sys.platform,
        "python": sys.version.split()[0],
        "builder_dir": str(builder_dir),
        "input_sha256": input_hashes,
        "selected_tests": test_ids,
        "required_existing_tests": list(TEST_NAMES),
        "tests_run": tests_run,
        "failures": failures,
        "errors": errors,
        "skipped": skipped,
        "successful_control_tests": group_checks,
        "required_control_tests": 3,
        "actual_group_leader_launches": group_launches,
        "group_launches_with_matching_pgid": sum(
            item["pgid_matches_leader"] is True for item in group_launches
        ),
        "group_launches_with_new_session": sum(
            item["new_session_requested"] is True for item in group_launches
        ),
        "required_group_leader_launches": EXPECTED_GROUP_LAUNCHES,
        "elapsed_seconds": round(elapsed, 6),
        "deadline_seconds": PREFLIGHT_SECONDS,
        "log": {
            "path": str(log_path),
            "sha256": log_sha,
            "bytes": log_path.stat().st_size,
            "limit_bytes": LOG_LIMIT,
            "overflow": stream.overflow,
        },
        "build_or_compile_started": False,
        "native_or_ci_result_claimed": False,
    }
    _write_receipt(receipt_path, terminal)
    print(json.dumps({"status": status, "tests_run": tests_run, "errors": errors, "skipped": skipped, "receipt": str(receipt_path), "log_sha256": log_sha}, sort_keys=True))
    return 0 if status == "passed" else (2 if status == "precondition-failed" else 1)


if __name__ == "__main__":
    raise SystemExit(main())
