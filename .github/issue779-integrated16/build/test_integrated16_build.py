from __future__ import annotations

import hashlib
import json
import os
import pathlib
import shlex
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from types import SimpleNamespace
from unittest import mock

import integrated16_build as build

REAL_POPEN = subprocess.Popen
HAS_WAITID_WNOWAIT = (
    getattr(os, "waitid", None) is not None
    and all(hasattr(os, name) for name in ("P_PID", "WEXITED", "WNOHANG", "WNOWAIT"))
)


def compact_json(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")


def make_fixture(root: pathlib.Path) -> tuple[build.BuildConfig, pathlib.Path]:
    source_root = root / "source"
    source_root.mkdir()
    source_file = source_root / "app.txt"
    source_bytes = b"fixed source fixture\n"
    source_file.write_bytes(source_bytes)
    os.chmod(source_file, 0o644)
    source_map = {
        "sha256": {"app.txt": hashlib.sha256(source_bytes).hexdigest()},
        "modes": {"app.txt": "0644"},
    }
    commit = "1" * 40
    parent = "2" * 40
    tree = "3" * 40
    capture_value = {
        "schema": "issue779-csharp-common-source-capture-v4",
        "validation_commit": commit,
        "previous_validation_commit": parent,
        "source_count": 1,
        "source_tree": tree,
        "source": source_map,
    }
    projection = {
        "validation_commit": commit,
        "previous_validation_commit": parent,
        "source_count": 1,
        "tree": tree,
        "source": source_map,
    }
    capture_bytes = compact_json(capture_value)
    capture_path = root / "source-capture.json"
    capture_path.write_bytes(capture_bytes)
    os.chmod(capture_path, 0o600)

    raw_recipe = build.DEFAULT_CONFIG.recipe_path.read_bytes()
    replacements = {
        build.SOURCE_COMMIT.encode(): commit.encode(),
        build.SOURCE_PARENT.encode(): parent.encode(),
        build.SOURCE_TREE.encode(): tree.encode(),
        build.CAPTURE_SHA256.encode(): hashlib.sha256(capture_bytes).hexdigest().encode(),
        build.CAPTURE_PROJECTION_SHA256.encode(): hashlib.sha256(compact_json(projection)).hexdigest().encode(),
        str(build.SOURCE_COUNT).encode(): b"1",
    }
    for old, new in replacements.items():
        if raw_recipe.count(old) < 1:
            raise AssertionError(f"fixture recipe pin occurrence missing: {old!r}")
        raw_recipe = raw_recipe.replace(old, new)
    recipe_path = root / "build-fdd.sh"
    recipe_path.write_bytes(raw_recipe)
    os.chmod(recipe_path, 0o644)
    harness_root = root / "harness"
    harness_root.mkdir()
    config = build.BuildConfig(
        source=build.SourceTarget(
            commit=commit,
            parent=parent,
            tree=tree,
            count=1,
            capture_path=capture_path,
            capture_sha256=hashlib.sha256(capture_bytes).hexdigest(),
            capture_bytes=len(capture_bytes),
            capture_mode=0o600,
            capture_projection_sha256=hashlib.sha256(compact_json(projection)).hexdigest(),
            source_map_sha256=hashlib.sha256(compact_json(source_map)).hexdigest(),
            source_root=source_root,
        ),
        recipe_path=recipe_path,
        recipe_sha256=hashlib.sha256(raw_recipe).hexdigest(),
        recipe_bytes=len(raw_recipe),
        recipe_mode=0o644,
    )
    return config, harness_root


class FakeFailureProcess:
    pid = 424242

    def __init__(self) -> None:
        self.wait_calls = 0
        self.returncode = None
        self.stdout = None
        self.stderr = None

    def poll(self) -> int:
        raise AssertionError("Popen.poll() must not reap the owned group leader")

    def wait(self, timeout: float | None = None) -> int:
        self.wait_calls += 1
        self.returncode = 17
        return 17


class IntegratedBuildTests(unittest.TestCase):
    def setUp(self) -> None:
        self.no_popen = mock.patch.object(
            build.subprocess, "Popen", side_effect=AssertionError("real subprocess forbidden in data suite")
        )
        self.no_run = mock.patch.object(
            build.subprocess, "run", side_effect=AssertionError("real command forbidden in data suite")
        )
        self.no_popen.start()
        self.no_run.start()

    def tearDown(self) -> None:
        self.no_run.stop()
        self.no_popen.stop()

    def test_fixed_selector_and_case_closure(self) -> None:
        expected_selectors = ("None", *(f"N{i:02d}" for i in range(4, 17)))
        self.assertEqual(
            build.SELECTORS,
            expected_selectors,
        )
        flattened_cases = [case for cases in build.CASES_BY_SELECTOR.values() for case in cases]
        self.assertEqual(len(flattened_cases), 16)
        self.assertEqual(set(flattened_cases), {f"N{i:02d}" for i in range(1, 17)})
        self.assertEqual(len(flattened_cases), len(set(flattened_cases)))
        self.assertEqual(build.CASES_BY_SELECTOR["None"], ("N01", "N02", "N03"))
        for case_id in ("N04", "N05", "N06", *(f"N{i:02d}" for i in range(7, 17))):
            self.assertEqual(build.CASES_BY_SELECTOR[case_id], (case_id,))
        self.assertEqual(build.TOTAL_DEADLINE_SECONDS, 14 * 1200)
        self.assertEqual(build.PER_IMAGE_DEADLINE_SECONDS, 1200)
        self.assertIsNone(build.EXECUTION_CLEARED_RECIPE_SHA256)
        self.assertEqual(build.SOURCE_BINDING_STATUS, "current_capture_pending_final_harness_review")

    def test_current_capture_source_bytes_modes_and_recipe_match_exact_pins(self) -> None:
        capture = build._validate_capture(build.DEFAULT_CONFIG)
        tree_sha = build._validate_source_tree(build.TARGET_SOURCE_ROOT, capture)
        recipe = build._read_recipe(build.DEFAULT_CONFIG)
        self.assertEqual(len(capture["source"]["sha256"]), 2872)
        self.assertEqual(tree_sha, build.SOURCE_MAP_SHA256)
        self.assertEqual(hashlib.sha256(recipe).hexdigest(), build.BASE_RECIPE_SHA256)

    def test_current_recipe_specializes_all_selectors_without_source_edits(self) -> None:
        recipe, mode = build._read_regular(build.BASE_RECIPE, 1024 * 1024)
        before_sha = hashlib.sha256(recipe).hexdigest()
        self.assertEqual(mode, 0o644)
        self.assertIn(
            b"UBUNTU_PREREQUISITE_SHA = '2962b893b617ff6d2e3e6846df4ea8b170da74ad9b3cd4848b4a0405c4b6daff'",
            recipe,
        )
        self.assertIn(
            b"NATIVE_RUNNER_SHA = 'ad1ffed5685c4a9af8de2d9923cad6d4b3476bf7fed5d82607b05460276fdac2'",
            recipe,
        )
        self.assertIsNone(build.EXECUTION_CLEARED_RECIPE_SHA256)
        hashes: dict[str, str] = {}
        for selector in build.SELECTORS:
            derived = build._specialize_recipe(recipe, selector)
            build.ast.parse(build._embedded_python(derived), filename=f"{selector}-build-fdd.py")
            self.assertIn(("SELECTED_VARIANT = '" + selector + "'").encode(), derived)
            self.assertNotIn(b"-p:EvidencePrivateQualification=None", derived)
            self.assertEqual(derived.replace(("SELECTED_VARIANT = '" + selector + "'").encode(),
                                              b"SELECTED_VARIANT = 'N11'"), recipe)
            hashes[selector] = hashlib.sha256(derived).hexdigest()
        self.assertEqual(len(set(hashes.values())), len(build.SELECTORS))
        self.assertEqual(hashlib.sha256(recipe).hexdigest(), before_sha)

    def test_default_api_is_plan_only_with_separate_image_and_receipt_paths(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            config, harness = make_fixture(root)
            output = root / "matrix-output"
            with mock.patch.object(build.subprocess, "Popen") as popen:
                plan = build.orchestrate(output, harness, config=config)
            popen.assert_not_called()
            self.assertFalse(output.exists())
            self.assertFalse(plan["builds_started"])
            self.assertFalse(plan["native_execution"])
            self.assertEqual(
                plan["source"]["binding_status"], "current_capture_pending_final_harness_review"
            )
            self.assertEqual([row["selector"] for row in plan["images"]], list(build.SELECTORS))
            self.assertEqual(len({row["output_dir"] for row in plan["images"]}), 14)
            self.assertEqual(len({row["receipt_path"] for row in plan["images"]}), 14)
            self.assertEqual(plan["cases"]["None"], ["N01", "N02", "N03"])
            self.assertEqual(plan["cases"]["N05"], ["N05"])
            self.assertEqual(plan["cases"]["N06"], ["N06"])
            self.assertEqual(plan["deadline"]["cumulative_seconds"], 14 * 1200)
            self.assertEqual(plan["deadline"]["per_image_cap_seconds"], 1200)
            self.assertTrue(all(row["image_sha256"] is None for row in plan["images"]))

    def test_actual_current_source_plan_preflight_does_not_execute_recipe(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            harness = root / "harness"
            harness.mkdir()
            output = root / "actual-matrix-output"
            with mock.patch.object(build.subprocess, "Popen") as popen:
                plan = build.orchestrate(output, harness)
            popen.assert_not_called()
            self.assertFalse(output.exists())
            self.assertFalse(plan["builds_started"])
            self.assertFalse(plan["native_execution"])
            self.assertFalse(plan["native_coverage_claim"])
            self.assertEqual(plan["source"]["commit"], "fc59bb1254294d624f6779f928020edfe988dd5e")
            self.assertEqual(plan["source"]["parent"], "1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38")
            self.assertEqual(plan["source"]["tree"], "c9ed8adcc08d050dc70af6885d31171038a36f86")
            self.assertEqual(plan["source"]["count"], 2872)
            self.assertEqual(
                plan["source"]["binding_status"], "current_capture_pending_final_harness_review"
            )
            self.assertEqual([row["selector"] for row in plan["images"]], list(build.SELECTORS))
            self.assertEqual(plan["cases"]["None"], ["N01", "N02", "N03"])
            self.assertEqual(plan["cases"]["N05"], ["N05"])
            self.assertEqual(plan["cases"]["N06"], ["N06"])
            self.assertEqual(plan["deadline"]["cumulative_seconds"], 14 * 1200)
            self.assertEqual(plan["deadline"]["per_image_cap_seconds"], 1200)
            self.assertTrue(all(row["image_sha256"] is None for row in plan["images"]))

    def test_explicit_execution_stays_blocked_on_unreviewed_base_recipe(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            config, harness = make_fixture(root)
            output = root / "matrix-output"
            with self.assertRaisesRegex(build.BuildExecutionError, "pinned-base-recipe-review-pending"):
                build.orchestrate(output, harness, execute=True, config=config)
            self.assertFalse(output.exists())

    def test_unreviewed_current_source_binding_blocks_execution_after_recipe_gate(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            config, harness = make_fixture(root)
            output = root / "matrix-output"
            with mock.patch.object(build, "EXECUTION_CLEARED_RECIPE_SHA256", config.recipe_sha256):
                with self.assertRaisesRegex(build.BuildExecutionError, "pinned-source-capture-review-pending"):
                    build.orchestrate(output, harness, execute=True, config=config)
            self.assertFalse(output.exists())

    def test_capture_mutation_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            config, _ = make_fixture(root)
            with config.source.capture_path.open("ab") as stream:
                stream.write(b"x")
            with self.assertRaisesRegex(build.BuildPreparationError, "capture-size-or-mode-mismatch|capture-sha256-mismatch"):
                build._validate_capture(config)

    def test_source_byte_mutation_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            config, _ = make_fixture(root)
            (config.source.source_root / "app.txt").write_bytes(b"changed source\n")
            with self.assertRaisesRegex(build.BuildPreparationError, "source-bytes-mismatch"):
                capture = build._validate_capture(config)
                build._validate_source_tree(config.source.source_root, capture)

    def test_recipe_byte_mutation_blocks_before_output_or_process(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            config, harness = make_fixture(root)
            with config.recipe_path.open("ab") as stream:
                stream.write(b"# changed\n")
            output = root / "matrix-output"
            with mock.patch.object(build.subprocess, "Popen") as popen:
                with self.assertRaisesRegex(build.BuildPreparationError, "recipe-size-or-mode-mismatch|recipe-sha256-mismatch"):
                    build.orchestrate(output, harness, execute=True, config=config)
            popen.assert_not_called()
            self.assertFalse(output.exists())

    def test_selector_is_part_of_final_image_binding(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            config, _ = make_fixture(pathlib.Path(temp))
            receipt_sha = "a" * 64
            artifacts = [{"path": "tool/app.dll", "bytes": 1, "mode": "0644", "sha256": "b" * 64}]
            n04 = build._image_binding("N04", config, "c" * 64, receipt_sha, artifacts)
            n07 = build._image_binding("N07", config, "c" * 64, receipt_sha, artifacts)
            self.assertNotEqual(n04, n07)

    def test_failed_mocked_child_is_terminated_joined_and_never_credited(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            config, harness = make_fixture(root)
            output = root / "matrix-output"
            process = FakeFailureProcess()
            clock_now = [0.0]
            group_alive = [True]
            signal_calls: list[int] = []

            def clock() -> float:
                return clock_now[0]

            def sleep(seconds: float) -> None:
                clock_now[0] += max(seconds, 0.001)

            def killpg(_pgid: int, sig: int) -> None:
                signal_calls.append(sig)
                if sig != 0:
                    self.assertIsNone(process.returncode)
                if sig == 0:
                    if group_alive[0]:
                        return
                    raise ProcessLookupError
                if sig == signal.SIGKILL:
                    group_alive[0] = False

            popen = mock.Mock(return_value=process)
            for stream_name in ("stdout", "stderr"):
                read_fd, write_fd = os.pipe()
                os.close(write_fd)
                setattr(process, stream_name, os.fdopen(read_fd, "rb", buffering=0))
            with (
                mock.patch.object(build.os, "killpg", side_effect=killpg),
                mock.patch.object(build, "TERM_GRACE_SECONDS", 0.02),
                mock.patch.object(build, "POLL_SECONDS", 0.01),
                mock.patch.object(build, "_leader_exit_observed_unreaped", return_value=True),
            ):
                plan = build._prepare(config, harness, output)
                result = build._execute(plan, popen=popen, clock=clock, sleeper=sleep)
            self.assertEqual(result["status"], "failed")
            self.assertEqual(result["failed_selector"], "None")
            self.assertEqual(popen.call_count, 1)
            self.assertTrue(popen.call_args.kwargs["start_new_session"])
            self.assertEqual(process.wait_calls, 1)
            self.assertEqual(signal_calls[:2], [0, signal.SIGTERM])
            self.assertIn(signal.SIGKILL, signal_calls)
            self.assertEqual(signal_calls[-1], 0)
            self.assertTrue(result["images"]["None"]["process_group_join"]["group_absent"])
            self.assertEqual(result["images"]["None"]["image_sha256"], None)
            self.assertEqual(result["images"]["None"]["native_execution"], False)
            self.assertTrue(all(result["images"][s]["status"] == "not_started" for s in build.SELECTORS[1:]))
            self.assertTrue((output / "receipts" / "ordinary.json").is_file())
            self.assertTrue((output / "receipts" / "n04.json").is_file())
            self.assertFalse((output / "images" / "ordinary").exists())

    def test_waitid_wnowait_contract_is_simulated_without_child_execution(self) -> None:
        process = FakeFailureProcess()
        process.pid = 7319
        calls = []

        def fake_waitid(idtype: int, pid: int, options: int) -> object:
            calls.append((idtype, pid, options))
            return SimpleNamespace(si_pid=pid)

        with (
            mock.patch.object(build.os, "waitid", side_effect=fake_waitid, create=True),
            mock.patch.object(build.os, "P_PID", 1, create=True),
            mock.patch.object(build.os, "WEXITED", 2, create=True),
            mock.patch.object(build.os, "WNOHANG", 4, create=True),
            mock.patch.object(build.os, "WNOWAIT", 8, create=True),
        ):
            self.assertTrue(build._leader_exit_observed_unreaped(process))

        self.assertEqual(calls, [(1, 7319, 14)])
        self.assertIsNone(process.returncode)
        self.assertEqual(process.wait_calls, 0)

    def test_missing_waitid_wnowait_fails_closed_without_execution_claim(self) -> None:
        process = FakeFailureProcess()
        with mock.patch.object(build.os, "waitid", None, create=True):
            with self.assertRaisesRegex(
                build.BuildExecutionError, "unreaped-leader-observation-unavailable"
            ):
                build._leader_exit_observed_unreaped(process)
        self.assertIsNone(process.returncode)
        self.assertEqual(process.wait_calls, 0)

    def test_group_join_signals_precede_reap_and_never_follow_it(self) -> None:
        process = FakeFailureProcess()
        group_states = iter((True, False, True, False, False))
        signal_states = []

        def record_signal(pgid: int, sig: int) -> None:
            signal_states.append((pgid, sig, process.returncode))

        with (
            mock.patch.object(build, "_group_alive", side_effect=lambda _pgid: next(group_states)),
            mock.patch.object(build.os, "killpg", side_effect=record_signal),
        ):
            joined = build._join_group(
                process, 10.0, clock=lambda: 0.0, sleeper=lambda _seconds: None
            )

        self.assertTrue(joined["waited"])
        self.assertTrue(joined["group_absent"])
        self.assertEqual([row[1] for row in signal_states], [signal.SIGTERM, signal.SIGKILL])
        self.assertTrue(all(row[0] == process.pid and row[2] is None for row in signal_states))
        self.assertEqual(process.wait_calls, 1)
        self.assertEqual(process.returncode, 17)

    def test_signal_after_leader_reap_is_rejected_before_numeric_pgid_signal(self) -> None:
        process = FakeFailureProcess()
        process.returncode = 0
        with mock.patch.object(build.os, "killpg") as killpg:
            with self.assertRaisesRegex(
                build.BuildExecutionError, "process-group-signal-after-leader-reap"
            ):
                build._signal_group(process, signal.SIGKILL)
        killpg.assert_not_called()

    def test_completed_join_is_reused_after_late_eof_failure(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            image, capture = self._real_child_image(root, "#!/bin/bash\nexit 0\n")
            harness = root / "harness"
            harness.mkdir()
            read_out, write_out = os.pipe()
            read_err, write_err = os.pipe()
            os.close(write_out)
            os.close(write_err)

            class FakeProcess:
                pid = 9137

                def __init__(self) -> None:
                    self.returncode = None
                    self.stdout = os.fdopen(read_out, "rb", buffering=0)
                    self.stderr = os.fdopen(read_err, "rb", buffering=0)

            process = FakeProcess()
            readers = []

            class ControlledReader:
                def __init__(self, *, target, args, daemon) -> None:
                    self.target = target
                    self.args = args
                    self.index = len(readers)
                    self.join_calls = 0
                    readers.append(self)

                def start(self) -> None:
                    self.target(*self.args)

                def join(self, timeout=None) -> None:
                    self.join_calls += 1
                    if self.index == 0 and self.join_calls == 1:
                        raise build.BuildExecutionError("synthetic-late-eof-receipt-failure")

                def is_alive(self) -> bool:
                    return False

            join_calls = []

            def completed_join(child, *_args, **_kwargs):
                join_calls.append(child.pid)
                child.returncode = 0
                return {"waited": True, "group_absent": True, "signals": ["TERM"]}

            popen = mock.Mock(return_value=process)
            with (
                mock.patch.object(build, "_leader_exit_observed_unreaped", return_value=True),
                mock.patch.object(build, "_join_group", side_effect=completed_join) as join_group,
                mock.patch.object(build.threading, "Thread", ControlledReader),
            ):
                with self.assertRaisesRegex(
                    build.BuildExecutionError, "synthetic-late-eof-receipt-failure"
                ) as caught:
                    build._run_child(
                        image,
                        harness,
                        capture,
                        10.0,
                        20.0,
                        popen=popen,
                        clock=lambda: 0.0,
                        sleeper=lambda _seconds: None,
                    )

            join_group.assert_called_once()
            self.assertEqual(join_calls, [process.pid])
            self.assertEqual(process.returncode, 0)
            self.assertTrue(caught.exception.join["waited"])
            self.assertTrue(caught.exception.join["group_absent"])
            self.assertTrue(caught.exception.join["readers_joined"])
            self.assertEqual([reader.join_calls for reader in readers], [2, 1])

    def _real_child_image(self, root: pathlib.Path, script: str) -> tuple[build.ImagePlan, pathlib.Path]:
        logs = root / "logs"
        recipes = root / "recipes"
        image_dir = root / "images" / "ordinary"
        logs.mkdir()
        recipes.mkdir()
        image_dir.mkdir(parents=True)
        recipe_path = recipes / "ordinary.build-fdd.sh"
        recipe_path.write_text(script, encoding="utf-8")
        os.chmod(recipe_path, 0o700)
        raw = recipe_path.read_bytes()
        image = build.ImagePlan(
            selector="None",
            cases=("N01",),
            output_dir=image_dir,
            receipt_path=root / "receipts" / "ordinary.json",
            stdout_path=logs / "ordinary.stdout.log",
            stderr_path=logs / "ordinary.stderr.log",
            recipe_path=recipe_path,
            recipe_bytes=raw,
            recipe_sha256=hashlib.sha256(raw).hexdigest(),
        )
        return image, root / "capture.json"

    @unittest.skipUnless(
        HAS_WAITID_WNOWAIT,
        "host lacks waitid/WNOWAIT; Linux behavior is covered by the data-only simulation",
    )
    def test_real_recipe_signals_while_leader_is_still_owned(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            script = "#!/bin/bash\nexec " + shlex.join([
                sys.executable, "-c", "import os; os.write(1,b'out\\n'); os.write(2,b'err\\n')"
            ]) + "\n"
            image, capture = self._real_child_image(root, script)
            harness = root / "harness"
            harness.mkdir()
            owned = []
            signal_states = []
            real_killpg = os.killpg

            def launch(*args, **kwargs):
                process = REAL_POPEN(*args, **kwargs)
                owned.append(process)
                return process

            def tracked_killpg(pgid, sig):
                if sig != 0:
                    signal_states.append(owned[0].returncode)
                    self.assertIsNone(owned[0].returncode)
                return real_killpg(pgid, sig)

            with mock.patch.object(build.os, "killpg", side_effect=tracked_killpg), \
                 mock.patch.object(build, "TERM_GRACE_SECONDS", 0.01):
                result = build._run_child(
                    image, harness, capture, time.monotonic() + 5, time.monotonic() + 7,
                    popen=launch,
                )
            self.assertEqual(result["exit"], 0)
            self.assertTrue(result["group_absent"])
            self.assertIsNotNone(owned[0].returncode)
            self.assertTrue(signal_states)
            self.assertTrue(all(state is None for state in signal_states))
            self.assertEqual(image.stdout_path.read_bytes(), b"out\n")
            self.assertEqual(image.stderr_path.read_bytes(), b"err\n")

    @unittest.skipUnless(
        HAS_WAITID_WNOWAIT,
        "host lacks waitid/WNOWAIT; Linux behavior is covered by the data-only simulation",
    )
    def test_real_descendant_cleanup_and_combined_pipe_cap(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            marker = root / "term-seen"
            child_code = (
                "import os,signal,time; "
                f"marker={str(marker)!r}; "
                "pid=os.fork(); "
                "exec('if pid == 0:\\n signal.signal(signal.SIGTERM, lambda *_: (open(marker, \\\"w\\\").write(\\\"term\\\"), os._exit(0)))\\n time.sleep(30)\\n os._exit(0)'); "
                "os._exit(0)"
            )
            script = "#!/bin/bash\nexec " + shlex.join([sys.executable, "-c", child_code]) + "\n"
            image, capture = self._real_child_image(root, script)
            harness = root / "harness"
            harness.mkdir()
            owned = []
            real_killpg = os.killpg

            def launch(*args, **kwargs):
                process = REAL_POPEN(*args, **kwargs)
                owned.append(process)
                return process

            def tracked_killpg(pgid, sig):
                if sig != 0:
                    self.assertIsNone(owned[0].returncode)
                return real_killpg(pgid, sig)

            with mock.patch.object(build.os, "killpg", side_effect=tracked_killpg), \
                 mock.patch.object(build, "TERM_GRACE_SECONDS", 0.02):
                result = build._run_child(
                    image, harness, capture, time.monotonic() + 5, time.monotonic() + 7,
                    popen=launch,
                )
            self.assertEqual(result["exit"], 0)
            self.assertTrue(result["group_absent"])
            self.assertTrue(marker.exists())

        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            writer = "import os; os.write(1,b'x'*700); os.write(2,b'y'*500)"
            script = "#!/bin/bash\nexec " + shlex.join([sys.executable, "-c", writer]) + "\n"
            image, capture = self._real_child_image(root, script)
            harness = root / "harness"
            harness.mkdir()
            owned = []

            def launch(*args, **kwargs):
                process = REAL_POPEN(*args, **kwargs)
                owned.append(process)
                return process

            with mock.patch.object(build, "MAX_OUTER_LOG_BYTES", 1024), \
                 mock.patch.object(build, "TERM_GRACE_SECONDS", 0.01):
                with self.assertRaisesRegex(build.BuildExecutionError, "outer-recipe-log-bound") as caught:
                    build._run_child(
                        image, harness, capture, time.monotonic() + 5, time.monotonic() + 7,
                        popen=launch,
                    )
            self.assertTrue(caught.exception.join["group_absent"])
            self.assertLessEqual(image.stdout_path.stat().st_size + image.stderr_path.stat().st_size, 1024)
            self.assertIsNotNone(owned[0].returncode)

    def test_artifact_and_receipt_publication_obey_deadlines(self) -> None:
        with tempfile.TemporaryDirectory(dir="/private/tmp") as temp:
            root = pathlib.Path(temp)
            handoff = root / "handoff"
            (handoff / "tool").mkdir(parents=True)
            (handoff / "runtime").mkdir()
            (handoff / "tool" / "app.dll").write_bytes(b"tool")
            (handoff / "runtime" / "dotnet").write_bytes(b"runtime")
            with self.assertRaisesRegex(build.BuildExecutionError, "postbuild-artifact-deadline"):
                build._artifact_manifest(root, deadline=0.0, clock=lambda: 1.0)
            receipt = root / "deadline.json"
            with self.assertRaisesRegex(build.BuildExecutionError, "postbuild-receipt-deadline"):
                build._atomic_json(receipt, {"status": "complete"}, deadline=0.0, clock=lambda: 1.0)
            self.assertFalse(receipt.exists())


if __name__ == "__main__":
    unittest.main()
