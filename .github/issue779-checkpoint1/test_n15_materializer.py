"""Direct N15 materializer control using temporary ordinary files only."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import stat
import tempfile
import types
import unittest
from unittest import mock


HERE = Path(__file__).resolve().parent
MATERIALIZER_PATH = HERE / "procedures/n15/materialize.py"
SOURCE = "a" * 40
GENERATION = "b" * 32
WORKFLOW = "github-actions-123-1"


def load_materializer():
    spec = importlib.util.spec_from_file_location("n15_materialize_under_test", MATERIALIZER_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def file_record(path, data, source_mode):
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    path.write_bytes(data)
    path.chmod(source_mode)
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(),
            "mode": format(source_mode, "04o")}


class N15MaterializerTests(unittest.TestCase):
    def test_materialize_serializes_policy_and_reads_back_real_request_and_inputs(self):
        materializer = load_materializer()
        with tempfile.TemporaryDirectory(prefix="n15-materializer-") as temporary:
            base = Path(temporary)
            roots = {role: base / ("input-" + role) for role in ("tool", "runtime", "source")}
            selected = {
                "tool": {
                    "ForgeTrust.AppSurface.Cli.dll": b"tiny-cli-image",
                    "ForgeTrust.AppSurface.Cli.deps.json": b'{"runtimeTarget":"tiny"}',
                    "ForgeTrust.AppSurface.Cli.runtimeconfig.json": b'{"framework":{"name":"tiny"}}',
                    "tool-note.txt": b"ordinary selected tool input\n",
                },
                "runtime": {"dotnet": b"ordinary runtime-host stand-in\n"},
                "source": {"docs/designs/issue-779-csharp-supervision-core.md": b"selected source bytes\n"},
            }
            nodes = {}
            for role, files in selected.items():
                records = {}
                directories = {"."}
                for name, data in files.items():
                    path = roots[role] / name
                    mode = 0o755 if role == "runtime" and name == "dotnet" else 0o644
                    records[name] = file_record(path, data, mode)
                    parent = Path(name).parent
                    while parent != Path("."):
                        directories.add(parent.as_posix())
                        parent = parent.parent
                nodes[role] = {"files": records,
                               "directories": {name: {} for name in directories}}

            fixture_parent, private_parent = base / "fixture-root", base / "private-root"
            fixture_parent.mkdir(mode=0o700)
            private_parent.mkdir(mode=0o700)
            fixture_root, private_root = fixture_parent / "fixture", private_parent / "private"
            staged_roots = set()

            workspace = types.SimpleNamespace(
                FIXTURE_ROOT=fixture_root,
                PRIVATE_ROOT=private_root,
                boottime_ms=lambda: 1000,
                calculate_work_deadline=lambda *_: 200_000,
                parse_deadline=lambda _value: 200.0,
            )

            def safe_ancestors(path):
                self.assertTrue(Path(path).exists())
                self.assertFalse(Path(path).is_symlink())

            def copy_role(source, destination, role_nodes, _deadline):
                source, destination = Path(source), Path(destination)
                destination.mkdir(mode=0o700, parents=True, exist_ok=False)
                for relative in sorted(role_nodes["directories"], key=lambda item: (item.count("/"), item)):
                    if relative != ".":
                        (destination / relative).mkdir(mode=0o700, parents=True, exist_ok=True)
                for relative, record in role_nodes["files"].items():
                    target = destination / relative
                    target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                    data = (source / relative).read_bytes()
                    self.assertEqual(len(data), record["bytes"])
                    self.assertEqual(hashlib.sha256(data).hexdigest(), record["sha256"])
                    target.write_bytes(data)
                    target.chmod(0o555 if int(record["mode"], 8) & 0o111 else 0o444)
                for relative in sorted(role_nodes["directories"], key=lambda item: (-item.count("/"), item)):
                    target = destination if relative == "." else destination / relative
                    target.chmod(0o555)
                staged_roots.add(destination)

            def verify_materialized(root, role_nodes, _deadline, allow_policy=False, policy_path=None):
                root = Path(root)
                expected_files = set(role_nodes["files"])
                if allow_policy:
                    expected_files.add("fixture-policy.json")
                found_files, found_dirs = set(), {"."}
                for directory, subdirs, names in os.walk(root, followlinks=False):
                    directory_path = Path(directory)
                    relative_dir = directory_path.relative_to(root).as_posix()
                    if relative_dir != ".":
                        found_dirs.add(relative_dir)
                    if root in staged_roots:
                        self.assertEqual(stat.S_IMODE(directory_path.stat().st_mode), 0o555)
                    for subdir in subdirs:
                        self.assertFalse((directory_path / subdir).is_symlink())
                    for name in names:
                        path = directory_path / name
                        relative = path.relative_to(root).as_posix()
                        found_files.add(relative)
                        if relative == "fixture-policy.json" and allow_policy:
                            raw = path.read_bytes()
                            self.assertLessEqual(len(raw), 4096)
                            self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o444)
                            self.assertEqual(path, Path(policy_path))
                            continue
                        record = role_nodes["files"][relative]
                        raw = path.read_bytes()
                        self.assertEqual(len(raw), record["bytes"])
                        self.assertEqual(hashlib.sha256(raw).hexdigest(), record["sha256"])
                        source_executable = int(record["mode"], 8) & 0o111
                        expected_mode = (0o555 if source_executable else 0o444) if root in staged_roots else int(record["mode"], 8)
                        self.assertEqual(stat.S_IMODE(path.stat().st_mode), expected_mode)
                self.assertEqual(found_files, expected_files)
                self.assertEqual(found_dirs, set(role_nodes["directories"]))

            def read_pinned(path, expected, cap, _deadline):
                path = Path(path)
                raw = path.read_bytes()
                self.assertLessEqual(len(raw), cap)
                self.assertEqual(hashlib.sha256(raw).hexdigest(), expected)
                return path.stat(), raw

            workspace.safe_ancestors = safe_ancestors
            workspace.copy_role = copy_role
            workspace.verify_materialized = verify_materialized
            workspace.read_pinned = read_pinned

            def canonical(value):
                return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()

            class Owner:
                HERE = HERE / "procedures/n15"
                PREFIX = ".github/issue779-checkpoint1/"

                def _load(self, _path, _sha256, *, deadline):
                    self.assert_outer_deadline(deadline)
                    return workspace

                @staticmethod
                def assert_outer_deadline(_deadline):
                    return None

                @staticmethod
                def remaining(_deadline):
                    return 50.0

                @staticmethod
                def need(ok, code):
                    if not ok:
                        raise AssertionError(code)

                @staticmethod
                def save(path, data):
                    with Path(path).open("xb") as output:
                        output.write(data)

                @staticmethod
                def canonical(value):
                    return canonical(value)

                @staticmethod
                def digest(data):
                    return hashlib.sha256(data).hexdigest()

            capture = {"files": {Owner.PREFIX + "workspace.py": {"sha256": "c" * 64}}}
            prepared = {"capture": capture,
                        "build": {"source_root": str(base / "source-checkout")},
                        "roots": roots, "nodes": nodes}
            context = {"source": SOURCE, "generation": GENERATION,
                       "workflow_identity": WORKFLOW}
            git_parent = types.SimpleNamespace(returncode=0, stdout=b"d" * 40 + b"\n")

            original_lstat = Path.lstat

            def root_owned_lstat(path):
                result = original_lstat(path)
                if Path(path) in (fixture_root, private_root):
                    return types.SimpleNamespace(st_mode=result.st_mode, st_uid=0, st_gid=0)
                return result

            try:
                with mock.patch.object(Path, "lstat", root_owned_lstat), \
                     mock.patch.object(materializer.subprocess, "run", return_value=git_parent) as run_git:
                    result = materializer.materialize(context, prepared, Owner(), deadline=100.0,
                                                      job_utc="2026-10-19T12:00:00Z", boot_end=201_000)
                run_git.assert_called_once()
                policy_raw = (result["stage"] / "tool/fixture-policy.json").read_bytes()
                policy = json.loads(policy_raw)
                self.assertEqual(policy["Id"], "checkpoint-empty")
                self.assertEqual(policy["Rules"], [{"Id": "docs", "Pattern": "docs/**",
                                                     "ProfileId": "empty", "Precedence": 0}])
                self.assertEqual(result["policy_sha256"], hashlib.sha256(policy_raw).hexdigest())

                request_raw = result["request"].read_bytes()
                request = json.loads(request_raw)
                self.assertEqual(request["schema"], "evidence-supervisor-linux-v1")
                self.assertEqual(request["source_revision"] if "source_revision" in request else request["subject_revision"], SOURCE)
                self.assertEqual(request["base_revision"], "d" * 40)
                self.assertEqual(request["workflow_identity"], WORKFLOW)
                self.assertEqual(request["paths"], ["docs/designs/issue-779-csharp-supervision-core.md"])
                self.assertEqual(result["request_sha256"], hashlib.sha256(request_raw).hexdigest())
                self.assertEqual(result["job_deadline_utc"], "2026-10-19T12:00:00Z")
                checksums = (result["private"] / "request-policy.sha256").read_text()
                self.assertIn(hashlib.sha256(request_raw).hexdigest(), checksums)
                self.assertIn(hashlib.sha256(policy_raw).hexdigest(), checksums)
            finally:
                # Sealed output directories are intentionally read-only; make only
                # this temporary test tree removable after assertions complete.
                for directory, _subdirs, _files in os.walk(base):
                    try:
                        os.chmod(directory, 0o700)
                    except FileNotFoundError:
                        pass


if __name__ == "__main__":
    unittest.main()
