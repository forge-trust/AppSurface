"""Filesystem-only regression controls for the checkpoint 4 cutover verifier."""

from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location(
    "issue779_checkpoint4_verify_cutover", HERE / "verify-cutover.py"
)
assert SPEC is not None and SPEC.loader is not None
VERIFY_CUTOVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY_CUTOVER)


def make_valid_tree(root: Path) -> None:
    """Create only the source tree shape consumed by verify(root)."""
    (root / "Cli/ForgeTrust.AppSurface.Cli").mkdir(parents=True)
    (root / "Evidence").mkdir()
    (root / ".github/workflows").mkdir(parents=True)
    (root / ".github/issue779-checkpoint2").mkdir(parents=True)
    (root / ".github/issue779-checkpoint3").mkdir(parents=True)
    (root / "Cli/ForgeTrust.AppSurface.Cli/EvidenceProcessEntryPoint.cs").write_text(
        "LinuxEmptyObservationExecution.RunAsync EvidenceWorkerCommand\n"
    )
    (root / ".github/issue779-checkpoint2/run-native.py").write_text(
        "# native runner fixture\n"
    )
    (root / ".github/issue779-checkpoint3/run-native.py").write_text(
        "# native runner fixture\n"
    )


class CutoverVerifierTests(unittest.TestCase):
    def test_minimal_valid_tree_passes_without_touching_repository_root(self) -> None:
        with tempfile.TemporaryDirectory(prefix="issue779-cutover-") as temporary:
            root = Path(temporary) / "fixture"
            make_valid_tree(root)
            VERIFY_CUTOVER.verify(root)

    def test_every_retired_path_rejects_files_and_both_symlink_kinds(self) -> None:
        for name in VERIFY_CUTOVER.RETIRED:
            for kind in ("file", "symlink", "dangling-symlink"):
                with self.subTest(retired_path=name, kind=kind):
                    with tempfile.TemporaryDirectory(prefix="issue779-cutover-") as temporary:
                        parent = Path(temporary)
                        root = parent / "fixture"
                        make_valid_tree(root)
                        retired = root / name
                        retired.parent.mkdir(parents=True, exist_ok=True)
                        if kind == "file":
                            retired.write_text("retired\n")
                        else:
                            target = parent / "symlink-target"
                            if kind == "symlink":
                                target.write_text("outside the fixture tree\n")
                            retired.symlink_to(target)
                        with self.assertRaisesRegex(ValueError, "legacy EvidenceHost"):
                            VERIFY_CUTOVER.verify(root)

    def test_each_retired_source_reference_in_an_active_consumer_rejects(self) -> None:
        for name in VERIFY_CUTOVER.RETIRED[:5]:
            with self.subTest(reference=name):
                with tempfile.TemporaryDirectory(prefix="issue779-cutover-") as temporary:
                    root = Path(temporary) / "fixture"
                    make_valid_tree(root)
                    consumer = root / "Evidence/ActiveConsumer.cs"
                    consumer.write_text(f"// references {name}\n")
                    with self.assertRaisesRegex(ValueError, "active consumer"):
                        VERIFY_CUTOVER.verify(root)

    def test_each_required_csharp_role_entry_marker_is_enforced(self) -> None:
        for source in (
            "EvidenceWorkerCommand\n",
            "LinuxEmptyObservationExecution.RunAsync\n",
        ):
            with self.subTest(source=source):
                with tempfile.TemporaryDirectory(prefix="issue779-cutover-") as temporary:
                    root = Path(temporary) / "fixture"
                    make_valid_tree(root)
                    entry = root / "Cli/ForgeTrust.AppSurface.Cli/EvidenceProcessEntryPoint.cs"
                    entry.write_text(source)
                    with self.assertRaisesRegex(ValueError, "CSharp role dispatch"):
                        VERIFY_CUTOVER.verify(root)

    def test_malformed_native_harness_ast_in_either_checkpoint_rejects(self) -> None:
        for name in (
            ".github/issue779-checkpoint2/run-native.py",
            ".github/issue779-checkpoint3/run-native.py",
        ):
            with self.subTest(harness=name):
                with tempfile.TemporaryDirectory(prefix="issue779-cutover-") as temporary:
                    root = Path(temporary) / "fixture"
                    make_valid_tree(root)
                    (root / name).write_text("def invalid(:\n    pass\n")
                    with self.assertRaises(SyntaxError):
                        VERIFY_CUTOVER.verify(root)


if __name__ == "__main__":
    unittest.main()
