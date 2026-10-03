"""Data-only preparation controls; no formatter, Git, build, native or admission execution.

ELF bytes below are only a format marker in temporary metadata files, never a
native executable or platform proof. Stat-size substitutions test numeric bounds
without allocating large bundles. Real root ownership/openat2 and final compiled
bindings remain outside this suite.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "private_qualification_preparation_data", Path(__file__).with_name("prepare.py"))
prepare = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = prepare
SPEC.loader.exec_module(prepare)


class PreparationDataControls(unittest.TestCase):
    """Exercise actual data helpers while rejecting any accidental command path."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="qualification-data-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.command_guards = []
        for owner, name in (
                (prepare.subprocess, "Popen"), (prepare.subprocess, "check_output"),
                (prepare.Runner, "run"), (prepare, "source_inventory"),
                (prepare, "archive"), (prepare, "prepare")):
            guard = patch.object(owner, name, side_effect=AssertionError("command-path-not-allowed"))
            self.command_guards.append(guard.start())
            self.addCleanup(guard.stop)

    def tearDown(self):
        for guard in self.command_guards:
            guard.assert_not_called()

    def assert_rejected(self, action):
        with self.assertRaises(prepare.PreparationFailure) as failure:
            action()
        self.assertEqual("qualification-preparation-rejected", str(failure.exception))

    def template(self, text):
        path = self.root / "registration.cs.in"
        path.write_text(text, encoding="utf-8")
        return path

    def bundle(self, optional=False):
        root = self.root / "bundle"
        root.mkdir()
        rows = {
            "AspireChild.dll": b"managed-app-metadata",
            "AspireChild.runtimeconfig.json": b"{}",
            "resource/NativeHttpResource.dll": b"managed-resource-metadata",
            "resource/NativeHttpResource.runtimeconfig.json": b"{}",
            "dcp/dcp": b"\x7fELF\x02metadata-only-not-executable",
            "proof-input/declared.txt": b"declared input",
        }
        if optional:
            rows.update({"dcp/ext/extension": b"extension-metadata",
                         "dependency.dll": b"dependency-metadata",
                         "AspireChild.deps.json": b"{}"})
        for name, data in rows.items():
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            path.chmod(0o600)
        return root, rows

    def test_policy_has_complete_closed_resource_producer_and_obligation_edges(self):
        policy = prepare.policy()
        self.assertEqual({"Id", "Version", "ConservativeProfileId", "Profiles", "Rules"}, set(policy))
        self.assertEqual("issue779-private-qualification", policy["Id"])
        self.assertEqual("1.0.0", policy["Version"])
        profile, = policy["Profiles"]
        self.assertEqual(prepare.PROFILE, policy["ConservativeProfileId"])
        self.assertEqual(prepare.PROFILE, profile["Id"])
        self.assertEqual("Targeted", profile["Scope"])
        self.assertEqual([{"Id": "native-http", "Readiness": "aspire_health",
                          "DeadlineSeconds": 45, "Requires": []}], profile["Resources"])
        producer, = profile["Producers"]
        self.assertEqual((prepare.PRODUCER, "coverage", "1.0.0"),
                         (producer["Id"], producer["Kind"], producer["Version"]))
        self.assertEqual(["native-http"], producer["RequiredResources"])
        self.assertEqual([prepare.ASSERTION], producer["AssertionIds"])
        self.assertEqual(180, producer["TimeoutSeconds"])
        self.assertEqual([{"LogicalName": "coverage-report", "RelativeRoot": "merged",
                           "MediaType": "application/xml", "MaximumBytes": 16 * 1024 * 1024,
                           "Required": True}], producer["ArtifactSlots"])
        self.assertEqual({"MinLinePercent": 95, "MinBranchPercent": 85,
                          "MinPatchLinePercent": None, "MinPatchBranchPercent": None,
                          "PatchLineMode": "measurable", "TolerancePercent": 0.5}, producer["CoverageGate"])
        obligation, = profile["Obligations"]
        self.assertEqual([producer["Id"]], obligation["RequiredProducerIds"])
        self.assertEqual(prepare.ASSERTION, obligation["RequiredAssertionId"])
        self.assertEqual([{"Id": "qualification-source", "Pattern": "tests/**",
                           "ProfileId": prepare.PROFILE, "Precedence": 0}], policy["Rules"])

    def test_policy_calls_do_not_share_mutable_declarations(self):
        first = prepare.policy()
        first["Profiles"][0]["Resources"].clear()
        first["Profiles"][0]["Producers"][0]["RequiredResources"].append("unknown")
        first["Rules"][0]["Pattern"] = "**"
        later = prepare.policy()
        self.assertEqual(1, len(later["Profiles"][0]["Resources"]))
        self.assertEqual(["native-http"], later["Profiles"][0]["Producers"][0]["RequiredResources"])
        self.assertEqual("tests/**", later["Rules"][0]["Pattern"])

    def test_unique_json_preserves_valid_nested_data_without_authority(self):
        value = {"policy": prepare.policy(), "bundle_files": []}
        self.assertEqual(value, prepare.unique_json(json.dumps(value).encode()))

    def test_unique_json_rejects_duplicate_and_case_alias_at_any_depth(self):
        for data in (b'{"x":1,"x":2}', b'{"Policy":1,"policy":2}',
                     b'{"outer":[{"Sha256":"canary","sha256":"other"}]}'):
            with self.subTest(data_shape=len(data)):
                self.assert_rejected(lambda: prepare.unique_json(data))

    def test_unique_json_rejects_malformed_envelope_before_build(self):
        for data in (b'{', b'{"x":}', b'{"x":1} trailing', b'{"x":1,}'):
            with self.subTest(length=len(data)), self.assertRaises(json.JSONDecodeError):
                prepare.unique_json(data)

    def test_expand_replaces_each_declared_marker_exactly_once(self):
        template = self.template('entry="__QUALIFICATION_ENTRY__"; build="__QUALIFICATION_BUILD__";')
        result = prepare.expand(template, {"__QUALIFICATION_ENTRY__": "YWJjZA==",
                                           "__QUALIFICATION_BUILD__": "issue779/private-1@build:2"})
        self.assertEqual('entry="YWJjZA=="; build="issue779/private-1@build:2";', result)

    def test_expand_rejects_missing_repeated_or_unexpanded_unknown_marker(self):
        for text, replacements in (
                ('"literal"', {"__QUALIFICATION_ENTRY__": "YWJj"}),
                ('__QUALIFICATION_ENTRY__ __QUALIFICATION_ENTRY__', {"__QUALIFICATION_ENTRY__": "YWJj"}),
                ('__QUALIFICATION_ENTRY__ __QUALIFICATION_UNKNOWN__', {"__QUALIFICATION_ENTRY__": "YWJj"}),
                ('{{unexpanded}}', {})):
            with self.subTest(shape=text):
                self.assert_rejected(lambda: prepare.expand(self.template(text), replacements))

    def test_expand_rejects_literal_injection_and_wrong_value_types(self):
        for value in ('', 'canary"; call();', "line\nnext", "white space", "\\escape", None, 3, ["literal"]):
            with self.subTest(kind=type(value).__name__):
                self.assert_rejected(lambda: prepare.expand(
                    self.template('"__QUALIFICATION_ENTRY__"'), {"__QUALIFICATION_ENTRY__": value}))

    def test_bundle_inventory_binds_actual_bytes_roles_and_readonly_exec_modes(self):
        root, files = self.bundle(optional=True)
        rows = prepare.bundle_inventory(root)
        self.assertEqual(sorted(files), [row["RelativePath"] for row in rows])
        expected_roles = {"AspireChild.dll": "AppHost", "AspireChild.runtimeconfig.json": "AppHostRuntimeConfiguration",
                          "resource/NativeHttpResource.dll": "Resource",
                          "resource/NativeHttpResource.runtimeconfig.json": "ResourceRuntimeConfiguration",
                          "dcp/dcp": "Dcp", "proof-input/declared.txt": "DeclaredInput",
                          "dcp/ext/extension": "DcpExtension", "dependency.dll": "Dependency",
                          "AspireChild.deps.json": "DependencyManifest"}
        for row in rows:
            path = root / row["RelativePath"]
            role = expected_roles[row["RelativePath"]]
            expected_mode = 0o555 if role in ("Dcp", "DcpExtension") else 0o444
            self.assertEqual({"RelativePath", "Role", "LengthBytes", "Sha256", "Mode"}, set(row))
            self.assertEqual(role, row["Role"])
            self.assertEqual(len(files[row["RelativePath"]]), row["LengthBytes"])
            self.assertEqual(hashlib.sha256(files[row["RelativePath"]]).hexdigest(), row["Sha256"])
            self.assertEqual(expected_mode, row["Mode"])
            self.assertEqual(expected_mode, stat.S_IMODE(path.stat().st_mode))
        # The actual bundle audit requires every directory, including the root,
        # to be searchable/readable with no write bit after inventory sealing.
        self.assertEqual(0o555, stat.S_IMODE(root.stat().st_mode))
        self.assertTrue(all(stat.S_IMODE(path.stat().st_mode) == 0o555
                            for path in root.rglob("*") if path.is_dir()))

    def test_bundle_rejects_non_elf64_marker_before_any_command(self):
        root, _ = self.bundle()
        (root / "dcp/dcp").write_bytes(b"not-an-elf-marker")
        self.assert_rejected(lambda: prepare.bundle_inventory(root))

    def test_bundle_rejects_empty_file(self):
        root, _ = self.bundle()
        (root / "empty.dll").touch()
        self.assert_rejected(lambda: prepare.bundle_inventory(root))

    def test_bundle_rejects_file_or_directory_symlinks(self):
        root, _ = self.bundle()
        for target in (root / "AspireChild.dll", root / "resource"):
            with self.subTest(target_kind=target.is_dir()):
                link = root / "linked"
                link.symlink_to(target, target_is_directory=target.is_dir())
                self.assert_rejected(lambda: prepare.bundle_inventory(root))
                link.unlink()

    def test_bundle_rejects_multiply_linked_regular_file(self):
        root, _ = self.bundle()
        os.link(root / "AspireChild.dll", root / "copy.dll")
        self.assert_rejected(lambda: prepare.bundle_inventory(root))

    def test_bundle_rejects_nonregular_file_without_reading_it(self):
        root, _ = self.bundle()
        fifo = root / "!fifo"
        os.mkfifo(fifo)
        with patch.object(Path, "read_bytes", side_effect=AssertionError("must-not-read-fifo")) as read:
            self.assert_rejected(lambda: prepare.bundle_inventory(root))
        read.assert_not_called()

    def test_bundle_rejects_observed_size_over_file_bound_before_read(self):
        root, _ = self.bundle()
        path = root / "!overlimit.dll"
        path.write_bytes(b"small actual file")
        original = Path.lstat
        def changed_size(item, *args, **kwargs):
            info = original(item, *args, **kwargs)
            if item == path:
                fields = list(info)
                fields[6] = 128 * 1024 * 1024 + 1
                return os.stat_result(fields)
            return info
        with patch.object(Path, "lstat", changed_size), patch.object(
                Path, "read_bytes", side_effect=AssertionError("overlimit-must-not-read")) as read:
            self.assert_rejected(lambda: prepare.bundle_inventory(root))
        read.assert_not_called()

    def test_bundle_rejects_aggregate_observed_size_bound_with_small_files(self):
        root, _ = self.bundle()
        original = Path.lstat
        def changed_size(item, *args, **kwargs):
            info = original(item, *args, **kwargs)
            if stat.S_ISREG(info.st_mode):
                fields = list(info)
                fields[6] = 128 * 1024 * 1024
                return os.stat_result(fields)
            return info
        with patch.object(Path, "lstat", changed_size):
            self.assert_rejected(lambda: prepare.bundle_inventory(root))

    def test_bundle_rejects_too_few_or_too_many_rows(self):
        root, _ = self.bundle()
        (root / "proof-input/declared.txt").unlink()
        self.assert_rejected(lambda: prepare.bundle_inventory(root))
        (root / "proof-input/declared.txt").write_bytes(b"declared input")
        for index in range(251):
            (root / f"extra-{index:03d}.dll").write_bytes(b"x")
        self.assert_rejected(lambda: prepare.bundle_inventory(root))


if __name__ == "__main__":
    unittest.main()
