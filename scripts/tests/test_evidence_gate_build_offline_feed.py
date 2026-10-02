"""Synthetic lock/cache tests for the trusted offline NuGet feed builder."""

from __future__ import annotations

import base64
from dataclasses import replace
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-build-offline-feed.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_build_offline_feed", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
offline_feed = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = offline_feed
SPEC.loader.exec_module(offline_feed)


class EvidenceGateBuildOfflineFeedTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory(prefix="evidence-gate-offline-feed-tests-")
        self.root = Path(self.temporary_directory.name).resolve()
        self.checkout = self.root / "trusted-checkout"
        self.checkout.mkdir()
        self.cache = self.root / "global-packages"
        self.cache.mkdir()
        self.output = self.root / "feed"
        self.solution_relative_path = "Synthetic.slnx"
        self.solution_projects: set[str] = set()
        self.verifier_calls: list[str] = []

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    @staticmethod
    def content_hash(package_bytes: bytes) -> str:
        digest = hashlib.sha512(b"synthetic NuGet content hash\x00" + package_bytes).digest()
        return base64.b64encode(digest).decode("ascii")

    def package_entry(
        self,
        package_id: str = "Example.Package",
        version: str = "1.2.3",
        package_bytes: bytes = b"synthetic nupkg bytes",
    ) -> tuple[str, dict[str, str], bytes]:
        return package_id, {
            "type": "Direct",
            "requested": f"[{version}, )",
            "resolved": version,
            "contentHash": self.content_hash(package_bytes),
        }, package_bytes

    def write_lock(
        self,
        relative_path: str,
        entries: dict[str, dict[str, object]],
        *,
        select_project: bool = True,
        lock_property: str | None = None,
    ) -> Path:
        path = self.checkout / relative_path
        path.parent.mkdir(parents=True, exist_ok=True)
        project_path = path.parent / "Synthetic.csproj"
        project_xml = (
            f"<Project><PropertyGroup><NuGetLockFilePath>{lock_property}</NuGetLockFilePath>"
            "</PropertyGroup></Project>"
            if lock_property is not None
            else "<Project />"
        )
        project_path.write_text(project_xml, encoding="utf-8")
        if select_project:
            self.solution_projects.add(project_path.relative_to(self.checkout).as_posix())
        path.write_text(
            json.dumps({"version": 2, "dependencies": {"net10.0": entries}}),
            encoding="utf-8",
        )
        return path

    def cache_package(
        self,
        package_id: str,
        version: str,
        package_bytes: bytes,
    ) -> Path:
        package_id = package_id.lower()
        version = version.lower()
        directory = self.cache / package_id / version
        directory.mkdir(parents=True, exist_ok=True)
        path = directory / f"{package_id}.{version}.nupkg"
        path.write_bytes(package_bytes)
        return path

    def write_solution(self, project_paths: set[str] | None = None) -> Path:
        selected_projects = self.solution_projects if project_paths is None else project_paths
        project_elements = "".join(
            f'<Project Path="{path.replace("&", "&amp;").replace(chr(34), "&quot;")}" />'
            for path in sorted(selected_projects)
        )
        solution_path = self.checkout / self.solution_relative_path
        solution_path.write_text(f"<Solution>{project_elements}</Solution>", encoding="utf-8")
        return solution_path

    def fake_content_hash_verifier(self, archive_path: Path) -> str:
        self.assertTrue(archive_path.name.startswith(".offline-feed-"))
        self.verifier_calls.append(archive_path.name)
        return self.content_hash(archive_path.read_bytes())

    def build(
        self,
        *,
        limits: object | None = None,
        write_solution: bool = True,
    ) -> offline_feed.FeedBuildResult:
        if write_solution:
            self.write_solution()
        options: dict[str, object] = {}
        if limits is not None:
            options["limits"] = limits
        options["_content_hash_verifier"] = self.fake_content_hash_verifier
        return offline_feed.build_offline_feed(
            self.checkout,
            self.solution_relative_path,
            self.cache,
            self.output,
            **options,
        )

    def test_success_selects_only_linux_project_locks_and_creates_flat_verified_feed(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock("DefaultProject/packages.lock.json", {package_id: entry})
        self.write_lock(
            "RidProject/packages.osx-arm64.lock.json",
            {
                "Uncached.Mac.Package": {
                    "type": "Transitive",
                    "resolved": "7.8.9",
                    "contentHash": self.content_hash(b"not in Linux cache"),
                }
            },
            select_project=False,
        )
        self.write_lock(
            "RidProject/packages.linux-x64.lock.json",
            {
                "Example.Rid.Package": {
                    "type": "Transitive",
                    "resolved": "4.5.6-beta.1",
                    "contentHash": self.content_hash(b"rid archive"),
                }
            },
            lock_property="packages.$(NETCoreSdkRuntimeIdentifier).lock.json",
        )
        rid_bytes = b"rid archive"
        self.cache_package(package_id, "1.2.3", package_bytes)
        self.cache_package("Example.Rid.Package", "4.5.6-beta.1", rid_bytes)

        result = self.build()

        self.assertEqual(2, result.lock_file_count)
        self.assertEqual(2, result.package_count)
        self.assertEqual(len(package_bytes) + len(rid_bytes), result.package_bytes)
        self.assertEqual(
            {"example.package.1.2.3.nupkg", "example.rid.package.4.5.6-beta.1.nupkg"},
            {path.name for path in self.output.iterdir()},
        )
        self.assertEqual(package_bytes, (self.output / "example.package.1.2.3.nupkg").read_bytes())
        self.assertEqual(2, len(self.verifier_calls))
        self.assertNotEqual(
            base64.b64encode(hashlib.sha512(package_bytes).digest()).decode("ascii"),
            entry["contentHash"],
        )

    def test_duplicate_identity_across_frameworks_and_locks_is_emitted_once(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock("A/packages.lock.json", {package_id: entry})
        self.write_lock("B/packages.lock.json", {package_id.lower(): entry})
        self.cache_package(package_id, "1.2.3", package_bytes)

        result = self.build()

        self.assertEqual(1, result.package_count)
        self.assertEqual(1, len(list(self.output.iterdir())))

    def test_project_references_are_not_treated_as_nuget_archives(self) -> None:
        self.write_lock(
            "Project/packages.lock.json",
            {
                "sample.project": {
                    "type": "Project",
                    "dependencies": {"Example.Package": "[1.2.3, )"},
                },
                "Example.Package": {
                    "type": "CentralTransitive",
                    "resolved": "1.2.3",
                    "contentHash": self.content_hash(b"archive"),
                },
            },
        )
        self.cache_package("Example.Package", "1.2.3", b"archive")

        result = self.build()

        self.assertEqual(1, result.package_count)

    def test_conflicting_content_hashes_fail_before_output_creation(self) -> None:
        package_id, first, first_bytes = self.package_entry(package_bytes=b"first")
        _package_id, conflicting, _conflicting_bytes = self.package_entry(package_bytes=b"second")
        self.write_lock("A/packages.lock.json", {package_id: first})
        self.write_lock(
            "B/packages.linux-x64.lock.json",
            {package_id.lower(): conflicting},
            lock_property="packages.$(NETCoreSdkRuntimeIdentifier).lock.json",
        )
        self.cache_package(package_id, "1.2.3", first_bytes)

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "disagree"):
            self.build()

        self.assertFalse(self.output.exists())

    def test_missing_archive_fails_before_output_creation(self) -> None:
        package_id, entry, _package_bytes = self.package_entry()
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        (self.cache / package_id.lower() / "1.2.3").mkdir(parents=True)

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "missing"):
            self.build()

        self.assertFalse(self.output.exists())

    def test_missing_selected_linux_lock_fails_even_when_macos_lock_exists(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock(
            "Project/packages.osx-arm64.lock.json",
            {package_id: entry},
            lock_property="packages.$(NETCoreSdkRuntimeIdentifier).lock.json",
        )
        self.cache_package(package_id, "1.2.3", package_bytes)

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "Linux dependency lock"):
            self.build()

        self.assertFalse(self.output.exists())

    def test_ambiguous_or_unbounded_project_xml_fails_before_feed_creation(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        self.cache_package(package_id, "1.2.3", package_bytes)
        project = self.checkout / "Project/Synthetic.csproj"
        project.write_text(
            "<Project><PropertyGroup><NuGetLockFilePath>packages.lock.json</NuGetLockFilePath>"
            "<NuGetLockFilePath>packages.other.lock.json</NuGetLockFilePath></PropertyGroup></Project>",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "ambiguous"):
            self.build()

        project.write_text("<Project />", encoding="utf-8")
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "byte limit"):
            self.build(limits=replace(offline_feed.DEFAULT_LIMITS, max_project_file_bytes=1))

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "total byte limit"):
            self.build(limits=replace(offline_feed.DEFAULT_LIMITS, max_total_project_bytes=1))
        self.assertFalse(self.output.exists())

    def test_archive_with_wrong_nuget_content_hash_fails_and_removes_partial_output(self) -> None:
        package_id, entry, _package_bytes = self.package_entry(package_bytes=b"locked bytes")
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        self.cache_package(package_id, "1.2.3", b"different bytes")

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "NuGet.*contentHash"):
            self.build()

        self.assertFalse(self.output.exists())

    def test_later_hash_failure_removes_earlier_verified_packages(self) -> None:
        first_id, first_entry, first_bytes = self.package_entry("First.Package", package_bytes=b"first archive")
        second_id, second_entry, second_bytes = self.package_entry("Second.Package", package_bytes=b"second archive")
        self.write_lock("Project/packages.lock.json", {first_id: first_entry, second_id: second_entry})
        self.cache_package(first_id, "1.2.3", first_bytes)
        self.cache_package(second_id, "1.2.3", second_bytes)
        self.write_solution()
        verified: list[Path] = []

        def fail_second(archive_path: Path) -> str:
            verified.append(archive_path)
            if len(verified) == 2:
                self.assertTrue((self.output / "first.package.1.2.3.nupkg").is_file())
                return self.content_hash(b"wrong archive")
            return self.content_hash(archive_path.read_bytes())

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "NuGet.*contentHash"):
            offline_feed.build_offline_feed(
                self.checkout,
                self.solution_relative_path,
                self.cache,
                self.output,
                _content_hash_verifier=fail_second,
            )

        self.assertEqual(2, len(verified))
        self.assertFalse(self.output.exists())

    def test_symlinked_lock_file_and_symlinked_lock_directory_fail_closed(self) -> None:
        package_id, entry, _package_bytes = self.package_entry()
        lock = self.write_lock("Project/packages.lock.json", {package_id: entry})
        outside = self.root / "outside.lock.json"
        outside.write_text(json.dumps({"version": 2, "dependencies": {}}), encoding="utf-8")
        lock.unlink()
        lock.symlink_to(outside)

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "symbolic link"):
            self.build()

        lock.unlink()
        outside_project = self.root / "outside-project"
        outside_project.mkdir()
        (outside_project / "Synthetic.csproj").write_text("<Project />", encoding="utf-8")
        (outside_project / "packages.lock.json").write_text(
            json.dumps({"version": 2, "dependencies": {"net10.0": {}}}),
            encoding="utf-8",
        )
        linked_directory = self.checkout / "linked-project"
        linked_directory.symlink_to(outside_project, target_is_directory=True)
        self.solution_projects = {"linked-project/Synthetic.csproj"}
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "symbolic link"):
            self.build()

    def test_symlinked_checkout_argument_is_rejected(self) -> None:
        package_id, entry, _package_bytes = self.package_entry()
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        self.write_solution()
        link = self.root / "checkout-link"
        link.symlink_to(self.checkout, target_is_directory=True)

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "symbolic link"):
            offline_feed.build_offline_feed(link, self.solution_relative_path, self.cache, self.output)

    def test_symlinked_cache_id_version_or_archive_is_rejected(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        outside = self.root / "package-outside"
        outside.mkdir()
        (outside / "1.2.3").mkdir()
        (outside / "1.2.3" / "example.package.1.2.3.nupkg").write_bytes(package_bytes)
        (self.cache / "example.package").symlink_to(outside, target_is_directory=True)

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "symbolic link"):
            self.build()

    def test_symlinked_cache_version_and_archive_are_rejected(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        outside = self.root / "outside-version"
        outside.mkdir()
        (outside / "example.package.1.2.3.nupkg").write_bytes(package_bytes)
        (self.cache / "example.package").mkdir()
        (self.cache / "example.package" / "1.2.3").symlink_to(outside, target_is_directory=True)

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "symbolic link"):
            self.build()

        (self.cache / "example.package" / "1.2.3").unlink()
        version_directory = self.cache / "example.package" / "1.2.3"
        version_directory.mkdir()
        (version_directory / "example.package.1.2.3.nupkg").symlink_to(outside / "example.package.1.2.3.nupkg")
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "symbolic link"):
            self.build()

    def test_existing_output_and_output_under_input_roots_are_rejected(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        self.cache_package(package_id, "1.2.3", package_bytes)
        self.output.mkdir()

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "must not already exist"):
            self.build()
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "separate"):
            offline_feed.build_offline_feed(
                self.checkout,
                self.solution_relative_path,
                self.cache,
                self.checkout / "feed",
                _content_hash_verifier=self.fake_content_hash_verifier,
            )

    def test_malformed_duplicate_json_properties_and_unsafe_package_paths_fail(self) -> None:
        lock = self.write_lock("Project/packages.lock.json", {})
        lock.write_text('{"version":2,"version":2,"dependencies":{}}', encoding="utf-8")
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "malformed"):
            self.build()

        lock.unlink()
        self.write_lock(
            "Project/packages.lock.json",
            {"../escape": {"type": "Direct", "resolved": "1.2.3", "contentHash": "A" * 88}},
        )
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "package ID"):
            self.build()

    def test_missing_hash_or_invalid_hash_fails_closed(self) -> None:
        self.write_lock(
            "Project/packages.lock.json",
            {"Example.Package": {"type": "Direct", "resolved": "1.2.3", "contentHash": "not base64"}},
        )
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "base64"):
            self.build()

    def test_lock_file_count_total_bytes_and_package_budgets_are_enforced(self) -> None:
        package_id, entry, package_bytes = self.package_entry(package_bytes=b"four")
        self.write_lock("A/packages.lock.json", {package_id: entry})
        self.write_lock(
            "B/packages.linux-x64.lock.json",
            {"Second.Package": {
                "type": "Transitive",
                "resolved": "2.3.4",
                "contentHash": self.content_hash(b"other"),
            }},
            lock_property="packages.$(NETCoreSdkRuntimeIdentifier).lock.json",
        )
        self.cache_package(package_id, "1.2.3", package_bytes)
        self.cache_package("Second.Package", "2.3.4", b"other")

        count_limits = replace(offline_feed.DEFAULT_LIMITS, max_lock_files=1)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "lock-file-count"):
            self.build(limits=count_limits)

        byte_limits = replace(offline_feed.DEFAULT_LIMITS, max_total_lock_bytes=1)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "lock-byte"):
            self.build(limits=byte_limits)

        package_limits = replace(offline_feed.DEFAULT_LIMITS, max_package_bytes=3)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "per-file byte"):
            self.build(limits=package_limits)

        count_package_limits = replace(offline_feed.DEFAULT_LIMITS, max_packages=1)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "distinct-package-count"):
            self.build(limits=count_package_limits)

        single_lock_limits = replace(offline_feed.DEFAULT_LIMITS, max_lock_file_bytes=1)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "lock file"):
            self.build(limits=single_lock_limits)

        total_package_limits = replace(offline_feed.DEFAULT_LIMITS, max_total_package_bytes=3)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "total byte"):
            self.build(limits=total_package_limits)

        selected_entry_limits = replace(offline_feed.DEFAULT_LIMITS, max_selected_directory_entries=1)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "Selected project directories"):
            self.build(limits=selected_entry_limits)

        cache_entry_limits = replace(offline_feed.DEFAULT_LIMITS, max_cache_entries=1)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "inspected-entry"):
            self.build(limits=cache_entry_limits)
        self.assertFalse(self.output.exists())

    def test_unlisted_fixture_locks_do_not_contribute_missing_packages(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock("SelectedProject/packages.lock.json", {package_id: entry})
        self.write_lock(
            "tests/config-key-compatibility/previous-provider-plugin/packages.lock.json",
            {
                "Absent.Historical.Package": {
                    "type": "Direct",
                    "resolved": "9.8.7",
                    "contentHash": self.content_hash(b"not cached"),
                }
            },
            select_project=False,
        )
        self.cache_package(package_id, "1.2.3", package_bytes)

        result = self.build()

        self.assertEqual(1, result.lock_file_count)
        self.assertEqual(1, result.package_count)
        self.assertEqual({"example.package.1.2.3.nupkg"}, {path.name for path in self.output.iterdir()})

    def test_solution_and_selected_project_paths_reject_links_and_traversal(self) -> None:
        package_id, entry, _package_bytes = self.package_entry()
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        solution_path = self.write_solution()
        external_solution = self.root / "external.slnx"
        external_solution.write_text(solution_path.read_text(encoding="utf-8"), encoding="utf-8")
        solution_path.unlink()
        solution_path.symlink_to(external_solution)

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "symbolic link"):
            self.build(write_solution=False)

        solution_path.unlink()
        solution_path.write_text('<Solution><Project Path="../outside.csproj" /></Solution>', encoding="utf-8")
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "relative path"):
            self.build(write_solution=False)

    def test_solution_rejects_missing_and_duplicate_project_selections(self) -> None:
        self.write_lock("Project/packages.lock.json", {})
        solution_path = self.checkout / self.solution_relative_path
        solution_path.write_text("<Solution />", encoding="utf-8")
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "no projects"):
            self.build(write_solution=False)

        solution_path.write_text(
            '<Solution><Project Path="Project/Synthetic.csproj" />'
            '<Project Path="Project/Synthetic.csproj" /></Solution>',
            encoding="utf-8",
        )
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "more than once"):
            self.build(write_solution=False)
        self.assertFalse(self.output.exists())

    def test_output_parent_must_be_a_physical_directory(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        self.cache_package(package_id, "1.2.3", package_bytes)
        self.write_solution()
        linked_parent = self.root / "linked-output-parent"
        linked_parent.symlink_to(self.root, target_is_directory=True)

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "symbolic link"):
            offline_feed.build_offline_feed(
                self.checkout,
                self.solution_relative_path,
                self.cache,
                linked_parent / "feed",
                _content_hash_verifier=self.fake_content_hash_verifier,
            )
        self.assertFalse(self.output.exists())

    def test_nuget_content_hash_parser_requires_one_valid_fixed_line(self) -> None:
        expected = self.content_hash(b"package")
        output = f"Package signature verification result: OK\nContent hash: {expected}\n".encode("utf-8")
        self.assertEqual(expected, offline_feed._parse_nuget_content_hash_output(output))

        for invalid in (
            b"No content hash\n",
            f"Content hash: {expected}\nContent hash: {expected}\n".encode("ascii"),
            b"Content hash: not-base64\n",
        ):
            with self.subTest(invalid=invalid), self.assertRaises(offline_feed.OfflineFeedError):
                offline_feed._parse_nuget_content_hash_output(invalid)

    def test_nuget_verifier_requires_a_successful_bounded_process(self) -> None:
        verifier = self.root / "dotnet"
        archive = self.root / "package.nupkg"
        archive.write_bytes(b"synthetic archive")
        expected = self.content_hash(b"synthetic archive")

        def write_verifier(body: str) -> None:
            verifier.write_text(f"#!/bin/sh\n{body}\n", encoding="utf-8")
            verifier.chmod(0o755)

        with patch.object(offline_feed, "DOTNET_EXECUTABLES", (verifier,)):
            write_verifier(f"printf '%s\\n' 'Content hash: {expected}'")
            self.assertEqual(expected, offline_feed._nuget_content_hash_verifier(archive, 2, 4096))

            write_verifier("exit 3")
            with self.assertRaisesRegex(offline_feed.OfflineFeedError, "verification failed"):
                offline_feed._nuget_content_hash_verifier(archive, 2, 4096)

            write_verifier("printf '%0256d\\n' 1")
            with self.assertRaisesRegex(offline_feed.OfflineFeedError, "output-byte limit"):
                offline_feed._nuget_content_hash_verifier(archive, 2, 128)

            write_verifier("while :; do :; done")
            with self.assertRaisesRegex(offline_feed.OfflineFeedError, "time limit"):
                offline_feed._nuget_content_hash_verifier(archive, 0.05, 4096)

    def test_limits_cannot_be_raised_or_set_to_boolean(self) -> None:
        package_limits = replace(offline_feed.DEFAULT_LIMITS, max_packages=offline_feed.DEFAULT_LIMITS.max_packages + 1)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "hard ceiling"):
            self.build(limits=package_limits)

        invalid_limits = replace(offline_feed.DEFAULT_LIMITS, max_packages=True)
        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "hard ceiling"):
            self.build(limits=invalid_limits)

    def test_successful_feed_uses_exclusive_new_output(self) -> None:
        package_id, entry, package_bytes = self.package_entry()
        self.write_lock("Project/packages.lock.json", {package_id: entry})
        self.cache_package(package_id, "1.2.3", package_bytes)
        self.output.mkdir()

        with self.assertRaisesRegex(offline_feed.OfflineFeedError, "must not already exist"):
            self.build()


if __name__ == "__main__":
    unittest.main()
