"""Security-boundary tests for the trusted pull-request capture helper."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-controller.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_controller", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
controller = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = controller
SPEC.loader.exec_module(controller)


BASE_SHA = "a" * 40
HEAD_SHA = "b" * 40
TOKEN = "test-read-only-token-must-not-be-written"
DIFF = b"diff --git a/src/example.cs b/src/example.cs\n+trusted capture bytes\n"


def event_document(
    *,
    base_sha: str = BASE_SHA,
    head_sha: str = HEAD_SHA,
    head_repository_id: int = 123,
    head_repository: str = "forge-trust/AppSurface",
    action: str = "opened",
) -> dict[str, object]:
    return {
        "action": action,
        "number": 777,
        "repository": {"id": 123, "full_name": "forge-trust/AppSurface"},
        "pull_request": {
            "number": 777,
            "base": {
                "ref": "main",
                "sha": base_sha,
                "repo": {"id": 123, "full_name": "forge-trust/AppSurface"},
            },
            "head": {
                "sha": head_sha,
                "repo": {"id": head_repository_id, "full_name": head_repository},
            },
        },
    }


def api_document(
    *,
    base_sha: str = BASE_SHA,
    head_sha: str = HEAD_SHA,
    head_repository_id: int = 123,
    head_repository: str = "forge-trust/AppSurface",
) -> dict[str, object]:
    return {
        "number": 777,
        "state": "open",
        "base": {
            "ref": "main",
            "sha": base_sha,
            "repo": {"id": 123, "full_name": "forge-trust/AppSurface"},
        },
        "head": {
            "sha": head_sha,
            "repo": {"id": head_repository_id, "full_name": head_repository},
        },
    }


class FakeApi:
    def __init__(self, *responses: dict[str, object]) -> None:
        self.responses = list(responses)
        self.calls: list[tuple[str, int, str]] = []

    def __call__(self, repository: str, number: int, token: str) -> dict[str, object]:
        self.calls.append((repository, number, token))
        if not self.responses:
            raise AssertionError("unexpected extra API call")
        return self.responses.pop(0)


class FakeGit:
    def __init__(self, source_diff: bytes = DIFF, base_sha: str = BASE_SHA, head_sha: str = HEAD_SHA) -> None:
        self.source_diff = source_diff
        self.base_sha = base_sha
        self.head_sha = head_sha
        self.calls: list[list[str]] = []
        self.object_store_paths: list[Path] = []

    def __call__(
        self,
        arguments: list[str],
        *,
        deadline: float,
        maximum_output_bytes: int,
    ) -> bytes:
        self.calls.append(list(arguments))
        self.assertions(deadline, maximum_output_bytes)
        if "init" in arguments:
            object_store = Path(arguments[-1])
            object_store.mkdir(parents=True, exist_ok=True)
            (object_store / ".fixture-store").write_bytes(b"retained")
            self.object_store_paths.append(object_store)
            return b""
        if "fetch" in arguments:
            return b""
        if "rev-parse" in arguments:
            ref = arguments[-1]
            if ref == "refs/evidence/base^{commit}":
                return (self.base_sha + "\n").encode("ascii")
            if ref == "refs/evidence/head^{commit}":
                return (self.head_sha + "\n").encode("ascii")
            raise AssertionError(f"unexpected ref: {ref}")
        if "diff" in arguments:
            return self.source_diff
        raise AssertionError(f"unexpected Git command: {arguments}")

    @staticmethod
    def assertions(deadline: float, maximum_output_bytes: int) -> None:
        if deadline <= time.monotonic():
            raise AssertionError("Git was invoked after its deadline")
        if maximum_output_bytes <= 0:
            raise AssertionError("Git output limit must be positive")


class EvidenceGateControllerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory(prefix="evidence-gate-tests-")
        self.root = Path(self.temporary_directory.name)
        self.event_path = self.root / "event.json"
        self.output_path = self.root / "capture"
        self.write_event(event_document())

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def write_event(self, event: object) -> None:
        if isinstance(event, bytes):
            self.event_path.write_bytes(event)
        else:
            self.event_path.write_text(json.dumps(event), encoding="utf-8")

    def capture(
        self,
        api: FakeApi | None = None,
        git: FakeGit | None = None,
        *,
        repository: str = "forge-trust/AppSurface",
        repository_id: int = 123,
        target_branch: str = "main",
        event_path: Path | None = None,
        output_path: Path | None = None,
        object_store_path: Path | None = None,
    ) -> tuple[str, FakeApi, FakeGit]:
        api = api or FakeApi(api_document(), api_document())
        git = git or FakeGit()
        result = controller._capture_diff(
            repository=repository,
            repository_id=repository_id,
            target_branch=target_branch,
            run_id=456,
            run_attempt=2,
            token=TOKEN,
            event_path=event_path or self.event_path,
            output_path=output_path or self.output_path,
            object_store_path=object_store_path,
            api_client=api,
            git_runner=git,
        )
        return result, api, git

    def test_same_repository_capture_matches_fixed_git_diff_and_canonical_identity(self) -> None:
        result, api, git = self.capture()

        self.assertEqual("captured", result)
        self.assertEqual(2, len(api.calls))
        self.assertEqual([("forge-trust/AppSurface", 777, TOKEN)] * 2, api.calls)
        self.assertEqual(DIFF, (self.output_path / "source.diff").read_bytes())
        self.assertEqual(
            b'{"BaseRevision":"' + BASE_SHA.encode("ascii") + b'","HeadRepositoryId":123,"HeadRevision":"'
            + HEAD_SHA.encode("ascii") + b'","PullRequestNumber":777,"RepositoryId":123,'
            b'"TargetBranch":"main","WorkflowRunAttempt":2,"WorkflowRunId":456}',
            (self.output_path / "pull-request-run-identity.json").read_bytes(),
        )
        self.assertEqual(
            {"pull-request-run-identity.json", "repository.git", "source.diff"},
            {path.name for path in self.output_path.iterdir()},
        )
        retained_store = self.output_path / "repository.git"
        self.assertTrue(retained_store.is_dir())
        self.assertEqual(b"retained", (retained_store / ".fixture-store").read_bytes())
        self.assertEqual(
            Path("repository.git"),
            git.object_store_paths[0].relative_to(git.object_store_paths[0].parents[0]),
        )
        output_bytes = b"".join(
            path.read_bytes() for path in self.output_path.rglob("*") if path.is_file()
        )
        self.assertNotIn(TOKEN.encode(), output_bytes)

        fetch = next(call for call in git.calls if "fetch" in call)
        self.assertEqual(
            [
                "fetch",
                "--depth=1",
                "--no-tags",
                "--no-recurse-submodules",
                "--no-write-fetch-head",
                "https://github.com/forge-trust/AppSurface.git",
                "+refs/heads/main:refs/evidence/base",
                "+refs/pull/777/head:refs/evidence/head",
            ],
            fetch[3:],
        )
        diff = next(call for call in git.calls if "diff" in call)
        self.assertEqual(
            [
                "-c",
                "diff.external=",
                "-c",
                "diff.noprefix=false",
                "-c",
                "diff.mnemonicprefix=false",
                "-c",
                "diff.algorithm=myers",
                "diff",
                "--no-ext-diff",
                "--no-textconv",
                "--no-color",
                "--binary",
                "--full-index",
                "--unified=3",
                "--find-renames=50%",
                "--find-copies=50%",
                "--submodule=short",
                "--ignore-submodules=none",
                "--no-relative",
                "--src-prefix=a/",
                "--dst-prefix=b/",
                BASE_SHA,
                HEAD_SHA,
                "--",
            ],
            diff[3:],
        )
        self.assertTrue(all(isinstance(call, list) for call in git.calls))

    def test_api_capture_deadline_rejects_a_delayed_response(self) -> None:
        def slow_api(_repository: str, _number: int, _token: str) -> dict[str, object]:
            time.sleep(0.02)
            return api_document()

        with mock.patch.object(controller, "MAX_CAPTURE_SECONDS", 0.001):
            with self.assertRaisesRegex(controller.ControllerError, "deadline"):
                controller._capture_diff(
                    repository="forge-trust/AppSurface",
                    repository_id=123,
                    target_branch="main",
                    run_id=456,
                    run_attempt=2,
                    token=TOKEN,
                    event_path=self.event_path,
                    output_path=self.output_path,
                    api_client=slow_api,
                    git_runner=FakeGit(),
                )
        self.assertFalse(self.output_path.exists())

    def test_production_api_alarm_interrupts_a_slow_body_read(self) -> None:
        if not hasattr(controller.signal, "setitimer"):
            self.skipTest("The production controller requires POSIX deadline alarms.")

        def slow_read(_repository: str, _number: int, _token: str) -> dict[str, object]:
            time.sleep(0.3)
            return api_document()

        previous_handler = controller.signal.getsignal(controller.signal.SIGALRM)
        with self.assertRaisesRegex(controller.ControllerError, "deadline"):
            controller._invoke_api(
                slow_read, "forge-trust/AppSurface", 777, TOKEN,
                time.monotonic() + 0.03, enforce_alarm=True,
            )
        self.assertIs(previous_handler, controller.signal.getsignal(controller.signal.SIGALRM))

    def test_stale_base_or_head_is_rejected_before_git_fetch(self) -> None:
        changed = "c" * 40
        cases = (
            api_document(base_sha=changed),
            api_document(head_sha=changed),
        )
        for current in cases:
            with self.subTest(current_base=current["base"]["sha"], current_head=current["head"]["sha"]):
                api = FakeApi(current)
                git = FakeGit()
                with self.assertRaisesRegex(controller.ControllerError, "stale"):
                    self.capture(api=api, git=git)
                self.assertEqual([], git.calls)
                self.assertFalse(self.output_path.exists())

    def test_fork_emits_observation_only_metadata_without_git_or_gate_identity(self) -> None:
        fork_event = event_document(
            head_repository_id=987,
            head_repository="contributor/AppSurface",
        )
        self.write_event(fork_event)
        fork_api = api_document(
            head_repository_id=987,
            head_repository="contributor/AppSurface",
        )
        result, api, git = self.capture(api=FakeApi(fork_api))

        self.assertEqual("observation", result)
        self.assertEqual(1, len(api.calls))
        self.assertEqual([], git.calls)
        self.assertEqual({"observation.json"}, {path.name for path in self.output_path.iterdir()})
        observation = json.loads((self.output_path / "observation.json").read_bytes())
        self.assertEqual("ObservationOnly", observation["Mode"])
        self.assertEqual(BASE_SHA, observation["BaseRevision"])
        self.assertEqual(HEAD_SHA, observation["HeadRevision"])
        self.assertNotIn("Plan", observation)
        self.assertNotIn("PullRequestRunIdentity", observation)
        self.assertNotIn(TOKEN, (self.output_path / "observation.json").read_text(encoding="utf-8"))

    def test_external_object_store_is_retained_for_cli_and_removed_after_failed_capture(self) -> None:
        external_store = self.root / "trusted-objects.git"
        result, _, git = self.capture(object_store_path=external_store)

        self.assertEqual("captured", result)
        self.assertTrue(external_store.is_dir())
        self.assertEqual(b"retained", (external_store / ".fixture-store").read_bytes())
        self.assertEqual(external_store, git.object_store_paths[0])
        self.assertFalse((self.output_path / "repository.git").exists())

        self.output_path = self.root / "failed-capture"
        failing_store = self.root / "failed-objects.git"
        api = FakeApi(api_document(), api_document(head_sha="c" * 40))
        with self.assertRaisesRegex(controller.ControllerError, "stale"):
            self.capture(api=api, git=FakeGit(), output_path=self.output_path, object_store_path=failing_store)
        self.assertFalse(failing_store.exists())
        self.assertFalse(self.output_path.exists())

    def test_internal_object_store_path_is_preserved_inside_output(self) -> None:
        nested_store = self.output_path / "git" / "objects.git"
        result, _, git = self.capture(object_store_path=nested_store)

        self.assertEqual("captured", result)
        self.assertTrue(nested_store.is_dir())
        self.assertEqual(b"retained", (nested_store / ".fixture-store").read_bytes())
        self.assertEqual(
            Path("git/objects.git"),
            git.object_store_paths[0].relative_to(git.object_store_paths[0].parents[1]),
        )
        self.assertFalse((self.output_path / "repository.git").exists())

    def test_existing_external_object_store_is_never_taken_or_removed(self) -> None:
        existing_store = self.root / "already-owned.git"
        existing_store.mkdir()
        sentinel = existing_store / "keep.txt"
        sentinel.write_text("caller-owned", encoding="utf-8")
        api = FakeApi(api_document())
        git = FakeGit()

        with self.assertRaisesRegex(controller.ControllerError, "must not already exist"):
            self.capture(api=api, git=git, object_store_path=existing_store)

        self.assertEqual("caller-owned", sentinel.read_text(encoding="utf-8"))
        self.assertEqual([], api.calls)
        self.assertEqual([], git.calls)

    def test_external_object_store_is_removed_when_atomic_output_publication_fails(self) -> None:
        external_store = self.root / "publication-failure.git"
        api = FakeApi(api_document(), api_document())

        with mock.patch.object(
            controller,
            "_publish_staged_output",
            side_effect=controller.ControllerError("injected publication failure"),
        ):
            with self.assertRaisesRegex(controller.ControllerError, "injected publication failure"):
                self.capture(
                    api=api,
                    git=FakeGit(),
                    object_store_path=external_store,
                    output_path=self.output_path,
                )

        self.assertFalse(external_store.exists())
        self.assertFalse(self.output_path.exists())

    def test_fork_does_not_create_requested_object_store(self) -> None:
        self.write_event(
            event_document(head_repository_id=987, head_repository="contributor/AppSurface")
        )
        external_store = self.root / "fork-objects.git"
        fork_api = api_document(
            head_repository_id=987,
            head_repository="contributor/AppSurface",
        )

        result, _, git = self.capture(
            api=FakeApi(fork_api),
            git=FakeGit(),
            object_store_path=external_store,
        )

        self.assertEqual("observation", result)
        self.assertFalse(external_store.exists())
        self.assertEqual([], git.calls)

    def test_retained_store_contains_both_exact_commits_and_reproduces_source_diff(self) -> None:
        source = self.root / "source"
        source.mkdir()
        self.run_local_git(["init", "--quiet", str(source)])
        self.run_local_git(["-C", str(source), "config", "user.name", "Controller Fixture"])
        self.run_local_git(["-C", str(source), "config", "user.email", "fixture@example.invalid"])
        subject = source / "src" / "example.cs"
        subject.parent.mkdir()
        subject.write_text("base content\n", encoding="utf-8")
        self.run_local_git(["-C", str(source), "add", "src/example.cs"])
        self.run_local_git(["-C", str(source), "commit", "--quiet", "-m", "base"])
        base_sha = self.run_local_git(["-C", str(source), "rev-parse", "HEAD"]).strip()
        subject.write_text("head content\n", encoding="utf-8")
        self.run_local_git(["-C", str(source), "commit", "--quiet", "-am", "head"])
        head_sha = self.run_local_git(["-C", str(source), "rev-parse", "HEAD"]).strip()
        self.run_local_git(["-C", str(source), "update-ref", "refs/heads/main", base_sha])
        self.run_local_git(["-C", str(source), "update-ref", "refs/pull/777/head", head_sha])

        remote = self.root / "remote.git"
        self.run_local_git(["init", "--bare", "--quiet", str(remote)])
        self.run_local_git(
            [
                "--git-dir",
                str(remote),
                "fetch",
                "--quiet",
                str(source),
                "+refs/heads/main:refs/heads/main",
                "+refs/pull/777/head:refs/pull/777/head",
            ]
        )
        self.write_event(event_document(base_sha=base_sha, head_sha=head_sha))
        api = FakeApi(
            api_document(base_sha=base_sha, head_sha=head_sha),
            api_document(base_sha=base_sha, head_sha=head_sha),
        )
        real_git_calls: list[list[str]] = []

        def local_remote_git(
            arguments: list[str],
            *,
            deadline: float,
            maximum_output_bytes: int,
        ) -> bytes:
            real_git_calls.append(list(arguments))
            actual_arguments = list(arguments)
            if "fetch" in actual_arguments:
                remote_position = actual_arguments.index("https://github.com/forge-trust/AppSurface.git")
                actual_arguments[remote_position] = str(remote)
            return controller.run_git_command(
                actual_arguments,
                deadline=deadline,
                maximum_output_bytes=maximum_output_bytes,
            )

        result = controller._capture_diff(
            repository="forge-trust/AppSurface",
            repository_id=123,
            target_branch="main",
            run_id=456,
            run_attempt=2,
            token=TOKEN,
            event_path=self.event_path,
            output_path=self.output_path,
            api_client=api,
            git_runner=local_remote_git,
        )

        self.assertEqual("captured", result)
        retained_store = self.output_path / "repository.git"
        self.assertTrue(retained_store.is_dir())
        self.assertEqual(
            base_sha,
            self.run_local_git(["--git-dir", str(retained_store), "rev-parse", "refs/evidence/base"]).strip(),
        )
        self.assertEqual(
            head_sha,
            self.run_local_git(["--git-dir", str(retained_store), "rev-parse", "refs/evidence/head"]).strip(),
        )
        for revision in (base_sha, head_sha):
            self.assertEqual(
                "commit",
                self.run_local_git(["--git-dir", str(retained_store), "cat-file", "-t", revision]).strip(),
            )
        regenerated = self.run_local_git_bytes(
            [
                "--no-pager",
                f"--git-dir={retained_store}",
                *controller.GIT_DIFF_OPTIONS,
                base_sha,
                head_sha,
                "--",
            ]
        )
        self.assertEqual((self.output_path / "source.diff").read_bytes(), regenerated)
        self.assertTrue(any("fetch" in call for call in real_git_calls))

    def test_malformed_event_json_and_duplicate_keys_fail_closed(self) -> None:
        for payload in (b"{broken", b'{"action":"opened","action":"synchronize"}'):
            with self.subTest(payload=payload):
                self.write_event(payload)
                api = FakeApi(api_document())
                git = FakeGit()
                with self.assertRaises(controller.ControllerError):
                    self.capture(api=api, git=git)
                self.assertEqual([], api.calls)
                self.assertEqual([], git.calls)

    def test_malformed_or_symlink_event_path_fails_closed(self) -> None:
        with self.assertRaisesRegex(controller.ControllerError, "event path"):
            self.capture(event_path=self.root / ".." / "event.json")

        link = self.root / "event-link.json"
        try:
            link.symlink_to(self.event_path)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks are unavailable on this host")
        with self.assertRaisesRegex(controller.ControllerError, "unsafe"):
            self.capture(event_path=link)

    def test_malformed_sha_action_numeric_id_and_target_branch_are_rejected(self) -> None:
        cases = (
            event_document(base_sha="A" * 40),
            event_document(head_sha="b" * 39),
            event_document(action="labeled"),
            event_document(),
        )
        for malformed in cases:
            if malformed["action"] == "opened" and malformed["pull_request"]["base"]["sha"] == BASE_SHA:
                malformed["repository"]["id"] = "123"
            self.write_event(malformed)
            with self.assertRaises(controller.ControllerError):
                self.capture(api=FakeApi(api_document()), git=FakeGit())
            self.assertFalse(self.output_path.exists())

        self.write_event(event_document())
        with self.assertRaisesRegex(controller.ControllerError, "unexpected branch"):
            self.capture(target_branch="release")

    def test_output_path_traversal_and_existing_output_fail_closed(self) -> None:
        with self.assertRaisesRegex(controller.ControllerError, "output path"):
            self.capture(output_path=self.root / ".." / "outside")
        self.output_path.mkdir()
        with self.assertRaisesRegex(controller.ControllerError, "must not already exist"):
            self.capture()

    def test_oversized_git_diff_is_rejected_before_any_output_is_published(self) -> None:
        api = FakeApi(api_document())
        git = FakeGit(b"x" * (controller.MAX_SOURCE_DIFF_BYTES + 1))
        with self.assertRaisesRegex(controller.ControllerError, "byte limit"):
            self.capture(api=api, git=git)
        self.assertFalse(self.output_path.exists())
        self.assertEqual(1, len(api.calls))

    def test_fetched_revision_mismatch_and_mid_capture_staleness_are_rejected(self) -> None:
        api = FakeApi(api_document(), api_document())
        git = FakeGit(base_sha="c" * 40)
        with self.assertRaisesRegex(controller.ControllerError, "fetched Git refs"):
            self.capture(api=api, git=git)
        self.assertFalse(self.output_path.exists())

        changed_head = api_document(head_sha="c" * 40)
        api = FakeApi(api_document(), changed_head)
        git = FakeGit()
        with self.assertRaisesRegex(controller.ControllerError, "stale"):
            self.capture(api=api, git=git)
        self.assertFalse(self.output_path.exists())

    def test_untrusted_repository_text_cannot_become_a_git_argument(self) -> None:
        api = FakeApi(api_document())
        git = FakeGit()
        with self.assertRaisesRegex(controller.ControllerError, "owner/repository"):
            self.capture(
                api=api,
                git=git,
                repository="forge-trust/AppSurface;touch-pwned",
            )
        self.assertEqual([], api.calls)
        self.assertEqual([], git.calls)

    def test_process_runner_uses_an_argument_array_shell_false_and_drops_token(self) -> None:
        class PipeProcess:
            def __init__(self) -> None:
                stdout_read, stdout_write = os.pipe()
                stderr_read, stderr_write = os.pipe()
                os.write(stdout_write, b"bounded")
                os.close(stdout_write)
                os.close(stderr_write)
                self.stdout = os.fdopen(stdout_read, "rb", buffering=0)
                self.stderr = os.fdopen(stderr_read, "rb", buffering=0)
                self.returncode = 0

            def poll(self) -> int:
                return self.returncode

            def wait(self, timeout: float | None = None) -> int:
                return self.returncode

            def kill(self) -> None:
                self.returncode = -9

        command = ["git", "--no-pager", "--git-dir=/tmp/fixed.git", "cat-file", "-t", HEAD_SHA]
        with mock.patch.dict(os.environ, {"GITHUB_TOKEN": TOKEN}, clear=False):
            with mock.patch.object(controller.subprocess, "Popen", return_value=PipeProcess()) as popen:
                result = controller.run_git_command(
                    command,
                    deadline=time.monotonic() + 5,
                    maximum_output_bytes=64,
                )

        self.assertEqual(b"bounded", result)
        args, kwargs = popen.call_args
        self.assertEqual(command, args[0])
        self.assertIsInstance(args[0], list)
        self.assertIs(kwargs["shell"], False)
        self.assertNotIn("GITHUB_TOKEN", kwargs["env"])
        self.assertNotIn(TOKEN, repr(kwargs))

    def test_nonpositive_and_string_ids_are_not_accepted(self) -> None:
        for invalid in (0, -1, True, "777"):
            with self.subTest(invalid=invalid), self.assertRaises(controller.ControllerError):
                controller._positive_integer(invalid, "test ID")

    @staticmethod
    def run_local_git(arguments: list[str]) -> str:
        return EvidenceGateControllerTests.run_local_git_bytes(arguments).decode("utf-8")

    @staticmethod
    def run_local_git_bytes(arguments: list[str]) -> bytes:
        result = subprocess.run(
            ["git", *arguments],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
            check=False,
        )
        if result.returncode != 0:
            raise AssertionError("The local Git fixture command failed.")
        return result.stdout


if __name__ == "__main__":
    unittest.main()
