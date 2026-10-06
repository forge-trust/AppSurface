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


    def test_seal_subject_tree_normalizes_archive_modes_without_changing_bytes(self):
        subject = self.root / "archive-subject"
        directories = [subject, subject / "tests", subject / "calculation",
                       subject / "calculation" / "nested"]
        for directory in directories:
            directory.mkdir(exist_ok=True)
            os.chmod(directory, 0o775)
        contents = {
            subject / "QualificationSubject.csproj": b"inert project metadata\n",
            subject / "tests" / "QualificationSubjectTests.cs": b"inert test source\n",
            subject / "calculation" / "nested" / "input.txt": b"unchanged\x00bytes\n",
        }
        for file, content in contents.items():
            file.write_bytes(content)
            os.chmod(file, 0o664)
        for directory in directories:
            self.assertEqual(0o775, stat.S_IMODE(directory.lstat().st_mode))
        for file in contents:
            self.assertEqual(0o664, stat.S_IMODE(file.lstat().st_mode))
        try:
            prepare.seal_subject_tree(subject)
            for directory in directories:
                self.assertEqual(0o555, stat.S_IMODE(directory.lstat().st_mode))
            for file, content in contents.items():
                self.assertEqual(0o444, stat.S_IMODE(file.lstat().st_mode))
                self.assertEqual(content, file.read_bytes())
        finally:
            # Restore only our known directories/files so temporary cleanup can unlink them.
            for directory in directories:
                os.chmod(directory, 0o700)
            for file in contents:
                os.chmod(file, 0o600)

    def test_seal_subject_tree_rejects_unsafe_entries_before_any_permission_change(self):
        for shape in ("root-link", "file-link", "directory-link", "hardlink", "fifo"):
            with self.subTest(shape=shape):
                case = self.root / shape
                case.mkdir()
                subject = case / "subject"
                subject.mkdir()
                nested = subject / "nested"
                nested.mkdir()
                outside = case / "outside"
                outside.mkdir()
                owned = subject / "a-owned.txt"
                owned.write_bytes(b"owned file must remain unchanged")
                canary = outside / "canary.txt"
                canary.write_bytes(b"outside canary must remain unchanged")
                for directory in (subject, nested, outside):
                    os.chmod(directory, 0o775)
                for file in (owned, canary):
                    os.chmod(file, 0o664)
                candidate = subject
                unsafe = subject / "z-unsafe"
                if shape == "root-link":
                    candidate = case / "subject-link"
                    candidate.symlink_to(subject, target_is_directory=True)
                elif shape == "file-link":
                    unsafe.symlink_to(canary)
                elif shape == "directory-link":
                    unsafe.symlink_to(outside, target_is_directory=True)
                elif shape == "hardlink":
                    os.link(owned, unsafe)
                else:
                    os.mkfifo(unsafe)
                original_chmod = os.chmod
                with patch.object(prepare.os, "chmod", wraps=original_chmod) as chmod:
                    self.assert_rejected(lambda: prepare.seal_subject_tree(candidate))
                chmod.assert_not_called()
                for directory in (subject, nested, outside):
                    self.assertEqual(0o775, stat.S_IMODE(directory.lstat().st_mode))
                for file in (owned, canary):
                    self.assertEqual(0o664, stat.S_IMODE(file.lstat().st_mode))
                self.assertEqual(b"owned file must remain unchanged", owned.read_bytes())
                self.assertEqual(b"outside canary must remain unchanged", canary.read_bytes())


    def test_sdk_preflight_records_observed_metadata_without_commands_or_sealing(self):
        sdk = self.root / "trusted-sdk-metadata"
        sdk.mkdir()
        host = sdk / "dotnet"
        host.write_bytes(b"metadata-only-no-execution")
        workspace = self.root / "sdk-workspace"
        workspace.mkdir()
        with patch.object(prepare, "SDK_ROOT", sdk), patch.object(prepare, "seal_trusted_sdk") as seal:
            prepare.retain_sdk_preflight_binding(workspace, "0" * 40)
        seal.assert_not_called()
        path = workspace / "build-binding.json"
        value = json.loads(path.read_bytes())
        self.assertFalse(value["preparation_complete"])
        self.assertEqual("observed", value["sdk_bootstrap"]["host_metadata_state"])
        self.assertEqual(prepare.sdk_metadata(host.lstat()), value["sdk_bootstrap"]["host_before"])
        self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))

    def test_sdk_preflight_retains_null_unavailable_before_missing_host_failure(self):
        sdk = self.root / "missing-sdk-metadata"
        sdk.mkdir()
        workspace = self.root / "missing-sdk-workspace"
        workspace.mkdir()
        with patch.object(prepare, "SDK_ROOT", sdk), patch.object(prepare, "seal_trusted_sdk") as seal:
            with self.assertRaises(FileNotFoundError):
                prepare.retain_sdk_preflight_binding(workspace, "0" * 40)
        seal.assert_not_called()
        path = workspace / "build-binding.json"
        value = json.loads(path.read_bytes())
        self.assertFalse(value["preparation_complete"])
        self.assertEqual("unavailable", value["sdk_bootstrap"]["host_metadata_state"])
        self.assertIsNone(value["sdk_bootstrap"]["host_before"])
        self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))


class ProductToolDirectoryControls(unittest.TestCase):
    """Real temporary directory metadata only; no root, publish or worker proof."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="product-tool-directory-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.owner_uid = os.getuid()
        self.command_guards = []
        for owner, name in ((prepare.subprocess, "Popen"),
                            (prepare.subprocess, "check_output"),
                            (prepare.Runner, "run")):
            guard = patch.object(owner, name, side_effect=AssertionError("command-path-not-allowed"))
            self.command_guards.append(guard.start())
            self.addCleanup(guard.stop)

    def tearDown(self):
        for guard in self.command_guards:
            guard.assert_not_called()

    def workspace(self, name):
        path = self.root / name
        path.mkdir()
        path.chmod(0o711)
        return path

    def create(self, workspace, entry="cli", deadline=None, owner_uid=None):
        return prepare.prepare_product_tool_directory(
            workspace, entry,
            prepare.time.monotonic()+5 if deadline is None else deadline,
            expected_owner_uid=self.owner_uid if owner_uid is None else owner_uid)

    def assert_closed(self, descriptors):
        self.assertEqual(2, len(descriptors))
        self.assertEqual(2, len(set(descriptors)))
        for fd in descriptors:
            with self.assertRaises(OSError) as error:
                os.fstat(fd)
            self.assertEqual(9, error.exception.errno)

    def test_both_entries_are_private_under_each_actual_umask_without_changing_parent(self):
        for entry in ("cli", "host"):
            for mask in (0o000, 0o077, 0o777):
                with self.subTest(entry=entry, umask=oct(mask)):
                    workspace = self.workspace(f"{entry}-{mask:03o}")
                    before = workspace.lstat()
                    previous = os.umask(mask)
                    try:
                        result = self.create(workspace, entry)
                    finally:
                        os.umask(previous)
                    self.assertEqual(workspace / ("tool-"+entry), result)
                    self.assertFalse(result.is_symlink())
                    info = result.lstat()
                    self.assertTrue(stat.S_ISDIR(info.st_mode))
                    self.assertEqual(self.owner_uid, info.st_uid)
                    self.assertEqual(0o700, stat.S_IMODE(info.st_mode))
                    self.assertEqual([], list(result.iterdir()))
                    after = workspace.lstat()
                    self.assertEqual((before.st_dev, before.st_ino, before.st_uid, before.st_gid, before.st_mode),
                                     (after.st_dev, after.st_ino, after.st_uid, after.st_gid, after.st_mode))
                    self.assertEqual(0o711, stat.S_IMODE(after.st_mode))
                    self.assertEqual([result], list(workspace.iterdir()))

    def test_existing_directory_file_and_links_reject_without_chmod_or_canary_change(self):
        canary = self.root / "outside-canary"
        canary.write_bytes(b"outside bytes must stay unchanged")
        canary.chmod(0o644)
        directory_canary = self.root / "outside-directory"
        directory_canary.mkdir()
        directory_canary.chmod(0o755)
        for shape in ("directory", "file", "file-link", "directory-link", "dangling-link"):
            with self.subTest(shape=shape):
                workspace = self.workspace(shape)
                target = workspace / "tool-cli"
                if shape == "directory":
                    target.mkdir()
                    target.chmod(0o777)
                    (target / "owned-canary").write_bytes(b"existing child bytes")
                elif shape == "file":
                    target.write_bytes(b"existing file bytes")
                    target.chmod(0o777)
                elif shape == "file-link":
                    target.symlink_to(canary)
                elif shape == "directory-link":
                    target.symlink_to(directory_canary, target_is_directory=True)
                else:
                    target.symlink_to(self.root / "missing-canary")
                before = target.lstat()
                original_chmod, original_fchmod = os.chmod, os.fchmod
                with patch.object(prepare.os, "chmod", wraps=original_chmod) as chmod, patch.object(
                        prepare.os, "fchmod", wraps=original_fchmod) as fchmod:
                    with self.assertRaises(FileExistsError):
                        self.create(workspace)
                chmod.assert_not_called()
                fchmod.assert_not_called()
                after = target.lstat()
                self.assertEqual((before.st_dev, before.st_ino, before.st_mode, before.st_uid, before.st_gid),
                                 (after.st_dev, after.st_ino, after.st_mode, after.st_uid, after.st_gid))
                self.assertEqual(b"outside bytes must stay unchanged", canary.read_bytes())
                self.assertEqual(0o644, stat.S_IMODE(canary.lstat().st_mode))
                self.assertEqual(0o755, stat.S_IMODE(directory_canary.lstat().st_mode))
                self.assertEqual([], list(directory_canary.iterdir()))
                if shape == "directory":
                    self.assertEqual(b"existing child bytes", (target / "owned-canary").read_bytes())
                elif shape == "file":
                    self.assertEqual(b"existing file bytes", target.read_bytes())
                else:
                    self.assertTrue(target.is_symlink())
                self.assertFalse((self.root / "missing-canary").exists())
                self.assertEqual(0o711, stat.S_IMODE(workspace.lstat().st_mode))

    def test_malformed_entry_or_path_rejects_before_mkdir(self):
        workspace = self.workspace("valid-input-parent")
        cases = [(workspace, entry) for entry in ("", "CLI", "host/other", "../host", None, 1)]
        cases.extend(((Path("relative-parent"), "cli"),
                      (workspace / ".." / workspace.name, "host")))
        original_mkdir = os.mkdir
        for path, entry in cases:
            with self.subTest(entry=entry, absolute=path.is_absolute()):
                with patch.object(prepare.os, "mkdir", wraps=original_mkdir) as mkdir:
                    with self.assertRaises(prepare.PreparationFailure):
                        self.create(path, entry)
                mkdir.assert_not_called()
        self.assertEqual([], list(workspace.iterdir()))
        self.assertEqual(0o711, stat.S_IMODE(workspace.lstat().st_mode))

    def test_unsafe_workspace_or_wrong_owner_rejects_before_mkdir(self):
        unsafe = self.workspace("unsafe-workspace")
        unsafe.chmod(0o777)
        valid = self.workspace("wrong-owner-workspace")
        link = self.root / "workspace-link"
        link.symlink_to(valid, target_is_directory=True)
        file = self.root / "workspace-file"
        file.write_bytes(b"workspace canary")
        file.chmod(0o600)
        cases = ((unsafe, self.owner_uid), (valid, self.owner_uid+1),
                 (link, self.owner_uid), (file, self.owner_uid))
        original_mkdir = os.mkdir
        for workspace, owner_uid in cases:
            before = workspace.lstat()
            with self.subTest(owner_matches=owner_uid == self.owner_uid,
                              kind=stat.S_IFMT(before.st_mode)):
                with patch.object(prepare.os, "mkdir", wraps=original_mkdir) as mkdir:
                    with self.assertRaises(prepare.PreparationFailure):
                        self.create(workspace, owner_uid=owner_uid)
                mkdir.assert_not_called()
                after = workspace.lstat()
                self.assertEqual((before.st_dev, before.st_ino, before.st_mode, before.st_uid, before.st_gid),
                                 (after.st_dev, after.st_ino, after.st_mode, after.st_uid, after.st_gid))
        self.assertEqual([], list(unsafe.iterdir()))
        self.assertEqual([], list(valid.iterdir()))
        self.assertEqual(b"workspace canary", file.read_bytes())
        self.assertTrue(link.is_symlink())

    def test_expired_deadline_rejects_before_any_filesystem_mutation(self):
        workspace = self.workspace("expired-workspace")
        before = workspace.lstat()
        with patch.object(prepare.os, "mkdir", wraps=os.mkdir) as mkdir, patch.object(
                prepare.os, "fchmod", wraps=os.fchmod) as fchmod:
            with self.assertRaises(prepare.PreparationFailure):
                self.create(workspace, deadline=prepare.time.monotonic()-1)
        mkdir.assert_not_called()
        fchmod.assert_not_called()
        after = workspace.lstat()
        self.assertEqual(before, after)
        self.assertEqual([], list(workspace.iterdir()))

    def test_named_chmod_failure_preserves_error_and_closes_only_retained_parent(self):
        workspace = self.workspace("named-chmod-failure")
        descriptors, closed = [], []
        original_open, original_close = os.open, os.close
        first_error = OSError("controlled-named-chmod-error")

        def tracked_open(*args, **kwargs):
            fd = original_open(*args, **kwargs)
            descriptors.append(fd)
            return fd

        def tracked_close(fd):
            original_close(fd)
            closed.append(fd)

        with patch.object(prepare.os, "open", tracked_open), patch.object(
                prepare.os, "chmod", side_effect=first_error) as chmod, patch.object(
                prepare.os, "fchmod", wraps=os.fchmod) as fchmod, patch.object(
                prepare.os, "close", tracked_close):
            with self.assertRaises(OSError) as error:
                self.create(workspace)
        self.assertIs(first_error, error.exception)
        self.assertEqual(1, len(descriptors))
        self.assertEqual(descriptors, closed)
        chmod.assert_called_once_with("tool-cli", 0o700, dir_fd=descriptors[0], follow_symlinks=False)
        fchmod.assert_not_called()
        with self.assertRaises(OSError) as closed_error:
            os.fstat(descriptors[0])
        self.assertEqual(9, closed_error.exception.errno)
        self.assertEqual(0o711, stat.S_IMODE(workspace.lstat().st_mode))

    def test_chmod_failure_preserves_first_error_and_attempts_every_owned_close(self):
        workspace = self.workspace("chmod-failure")
        descriptors, closed = [], []
        original_open, original_close = os.open, os.close
        first_error = OSError("controlled-fchmod-error")
        close_error = OSError("controlled-later-close-error")

        def tracked_open(*args, **kwargs):
            fd = original_open(*args, **kwargs)
            descriptors.append(fd)
            return fd

        def failing_close(fd):
            original_close(fd)
            closed.append(fd)
            if len(closed) == 1:
                raise close_error

        with patch.object(prepare.os, "open", tracked_open), patch.object(
                prepare.os, "fchmod", side_effect=first_error), patch.object(
                prepare.os, "close", failing_close):
            with self.assertRaises(OSError) as error:
                self.create(workspace)
        self.assertIs(first_error, error.exception)
        self.assertEqual(list(reversed(descriptors)), closed)
        self.assert_closed(descriptors)
        self.assertEqual(0o711, stat.S_IMODE(workspace.lstat().st_mode))

    def test_close_failure_rejects_success_and_attempts_every_owned_close(self):
        workspace = self.workspace("close-failure")
        descriptors, closed = [], []
        original_open, original_close = os.open, os.close
        close_error = OSError("controlled-close-error-after-real-close")

        def tracked_open(*args, **kwargs):
            fd = original_open(*args, **kwargs)
            descriptors.append(fd)
            return fd

        def failing_close(fd):
            original_close(fd)
            closed.append(fd)
            if len(closed) == 1:
                raise close_error

        with patch.object(prepare.os, "open", tracked_open), patch.object(
                prepare.os, "close", failing_close):
            with self.assertRaises(OSError) as error:
                self.create(workspace, "host")
        self.assertIs(close_error, error.exception)
        self.assertEqual(list(reversed(descriptors)), closed)
        self.assert_closed(descriptors)
        self.assertEqual(0o700, stat.S_IMODE((workspace / "tool-host").lstat().st_mode))
        self.assertEqual(0o711, stat.S_IMODE(workspace.lstat().st_mode))


class PrivateBundleLayoutControls(unittest.TestCase):
    """Actual temporary file/FD custody only; UID overrides confer no root lease."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="private-bundle-layout-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.root.chmod(0o700)
        self.uid, self.gid = os.getuid(), os.getgid()
        self.guards = []
        for owner, name in ((prepare.subprocess, "Popen"),
                            (prepare.subprocess, "check_output"), (prepare.Runner, "run")):
            guard = patch.object(owner, name, side_effect=AssertionError("command-path-not-allowed"))
            self.guards.append(guard.start())
            self.addCleanup(guard.stop)
        spec = importlib.util.spec_from_file_location(
            "private_bundle_inventory_data", Path(__file__).with_name("product-coverage.py"))
        self.coverage = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = self.coverage
        spec.loader.exec_module(self.coverage)

    def tearDown(self):
        for guard in self.guards:
            guard.assert_not_called()

    def workspace(self, name):
        workspace = self.root / name
        workspace.mkdir(mode=0o700)
        bundle = workspace / "bundle"
        bundle.mkdir(mode=0o700)
        payloads = {
            "AspireChild.dll": b"metadata-app", "AspireChild.runtimeconfig.json": b"{}",
            "resource/NativeHttpResource.dll": b"metadata-resource",
            "resource/NativeHttpResource.runtimeconfig.json": b"{}",
            "dcp/dcp": b"\x7fELF\x02metadata-only", "proof-input/declared.txt": b"input"}
        for name, data in payloads.items():
            target = bundle / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            target.chmod(0o644)
        tool = workspace / "tool-cli"
        tool.mkdir(mode=0o700)
        canary = tool / "unchanged.dll"
        canary.write_bytes(b"published bytes unchanged")
        canary.chmod(0o444)
        return workspace, payloads, canary

    def relocate(self, workspace, *, deadline=None, uid=None, gid=None):
        return prepare.prepare_application_bundle_input(workspace,
            prepare.time.monotonic()+10 if deadline is None else deadline,
            expected_owner_uid=self.uid if uid is None else uid,
            expected_owner_gid=self.gid if gid is None else gid)

    def measure(self, tool, deadline=None):
        return prepare.measure_product_tool_inventory(tool, self.coverage,
            prepare.time.monotonic()+20 if deadline is None else deadline,
            expected_owner_uid=self.uid, expected_owner_gid=self.gid)

    def test_private_outer_and_sealed_inventory_preserve_published_tool_under_real_umasks(self):
        for mask in (0o000, 0o077, 0o777):
            with self.subTest(umask=oct(mask)):
                workspace, payloads, canary = self.workspace(f"mask-{mask:03o}")
                before = (canary.read_bytes(), canary.lstat())
                (workspace / "bundle").chmod(0o775)
                previous = os.umask(mask)
                try:
                    bundle, provenance = self.relocate(workspace)
                finally:
                    os.umask(previous)
                self.assertEqual(workspace / "application-bundle-input" / prepare.APP_ID / prepare.BUILD_ID, bundle)
                self.assertFalse((workspace / "bundle").exists())
                outer = workspace / "application-bundle-input"
                self.assertEqual(0o700, stat.S_IMODE(outer.lstat().st_mode))
                self.assertEqual(0o555, stat.S_IMODE(bundle.parent.lstat().st_mode))
                self.assertEqual(0o755, stat.S_IMODE(bundle.lstat().st_mode))
                rows = prepare.bundle_inventory(bundle, deadline=prepare.time.monotonic()+10)
                self.assertEqual(sorted(payloads), [row["RelativePath"] for row in rows])
                for row in rows:
                    self.assertEqual(payloads[row["RelativePath"]], (bundle / row["RelativePath"]).read_bytes())
                    self.assertEqual(hashlib.sha256(payloads[row["RelativePath"]]).hexdigest(), row["Sha256"])
                    self.assertEqual(len(payloads[row["RelativePath"]]), row["LengthBytes"])
                    self.assertEqual(row["Mode"], stat.S_IMODE((bundle / row["RelativePath"]).lstat().st_mode))
                self.assertEqual(0o555, stat.S_IMODE(bundle.lstat().st_mode))
                self.assertEqual({"container_path", "path", "container_metadata"}, set(provenance))
                info = outer.lstat()
                self.assertEqual({"uid": info.st_uid, "gid": info.st_gid, "mode": "0700",
                                  "device": info.st_dev, "inode": info.st_ino, "nlink": info.st_nlink},
                                 provenance["container_metadata"])
                self.assertEqual(str(outer), provenance["container_path"])
                self.assertEqual(str(bundle), provenance["path"])
                self.assertEqual(before[0], canary.read_bytes())
                self.assertEqual((before[1].st_dev, before[1].st_ino, before[1].st_mode),
                                 (canary.lstat().st_dev, canary.lstat().st_ino, canary.lstat().st_mode))

    def test_wrong_workspace_owner_group_mode_and_existing_link_reject_without_outside_changes(self):
        for variant in ("owner", "group", "mode", "existing", "symlink"):
            with self.subTest(variant=variant):
                workspace, _, canary = self.workspace(variant)
                before = (canary.read_bytes(), canary.lstat().st_mode)
                kwargs = {}
                if variant == "owner": kwargs["uid"] = self.uid+1
                if variant == "group": kwargs["gid"] = self.gid+1
                if variant == "mode": workspace.chmod(0o777)
                outer = workspace / "application-bundle-input"
                if variant == "existing": outer.mkdir(mode=0o777)
                if variant == "symlink": outer.symlink_to(canary.parent, target_is_directory=True)
                with self.assertRaises((prepare.PreparationFailure, OSError)):
                    self.relocate(workspace, **kwargs)
                self.assertTrue((workspace / "bundle").is_dir())
                self.assertEqual(before, (canary.read_bytes(), canary.lstat().st_mode))
                if variant in ("owner", "group", "mode"):
                    self.assertFalse(outer.exists())

    def test_relocation_named_substitution_is_rejected_and_original_bytes_are_retained(self):
        workspace, payloads, canary = self.workspace("substitution")
        original = os.rename
        displaced = "retained-original"
        def substitute(source, destination, *args, **kwargs):
            result = original(source, destination, *args, **kwargs)
            if source == "bundle" and destination == prepare.BUILD_ID:
                fd = kwargs["dst_dir_fd"]
                original(destination, displaced, src_dir_fd=fd, dst_dir_fd=fd)
                os.mkdir(destination, mode=0o700, dir_fd=fd)
            return result
        with patch.object(prepare.os, "rename", substitute):
            with self.assertRaises(prepare.PreparationFailure): self.relocate(workspace)
        retained = workspace / "application-bundle-input" / prepare.APP_ID / displaced
        self.assertEqual(payloads["AspireChild.dll"], (retained / "AspireChild.dll").read_bytes())
        self.assertEqual(b"published bytes unchanged", canary.read_bytes())

    def test_expired_relocation_deadline_prevents_open_and_creation(self):
        workspace, _, canary = self.workspace("expired")
        with patch.object(prepare.os, "open", side_effect=AssertionError("must-not-open")) as opened:
            with self.assertRaises(prepare.PreparationFailure):
                self.relocate(workspace, deadline=prepare.time.monotonic()-1)
        opened.assert_not_called()
        self.assertEqual({"bundle", "tool-cli"}, {p.name for p in workspace.iterdir()})
        self.assertEqual(b"published bytes unchanged", canary.read_bytes())

    def test_complete_tool_measurement_includes_nested_files_and_rejects_changed_second_snapshot(self):
        tool = self.root / "measured"
        tool.mkdir(mode=0o700)
        child = tool / "application-bundles"
        child.mkdir(mode=0o555)
        # Create before sealing the nested directory; no exclusion may hide it.
        child.chmod(0o700)
        payloads = {"root.dll": b"root", "application-bundles/not-excluded.txt": b"included"}
        for name, data in payloads.items():
            (tool / name).write_bytes(data); (tool / name).chmod(0o444)
        child.chmod(0o555)
        actual, summary = self.measure(tool)
        self.assertEqual({n: hashlib.sha256(b).hexdigest() for n, b in payloads.items()}, actual)
        self.assertEqual((2, 2, 12, 8, 2), tuple(summary[k] for k in
                         ("file_count", "directory_count", "total_bytes", "maximum_file_bytes", "maximum_depth")))
        original = self.coverage.snapshot_tree
        calls = []
        def changed_snapshot(*args, **kwargs):
            result = original(*args, **kwargs); calls.append(result)
            if len(calls) == 2:
                files, directories = result
                files = {name: dict(row) for name, row in files.items()}
                files["root.dll"]["sha256"] = "0"*64
                return files, directories
            return result
        with patch.object(self.coverage, "snapshot_tree", changed_snapshot):
            with self.assertRaises(prepare.PreparationFailure): self.measure(tool)
        self.assertEqual(2, len(calls))
        self.assertEqual(b"included", (child / "not-excluded.txt").read_bytes())

    def test_actual_sparse_files_reach_exact_file_and_tree_byte_bounds_without_limit_changes(self):
        tool = self.root / "byte-bound"
        tool.mkdir(mode=0o700)
        for index in range(8):
            path = tool / f"{index:02d}.dll"
            with path.open("wb") as stream: stream.truncate(32 << 20)
            path.chmod(0o444)
        rows, summary = self.measure(tool)
        self.assertEqual(8, len(rows))
        self.assertEqual((256 << 20, 32 << 20, 32 << 20, 256 << 20, 2048),
                         tuple(summary[k] for k in ("total_bytes", "maximum_file_bytes",
                               "file_limit_bytes", "tree_limit_bytes", "maximum_entries")))
        extra = tool / "99-extra.dll"
        extra.write_bytes(b"x"); extra.chmod(0o444)
        with self.assertRaises(self.coverage.ProductCoverageError): self.measure(tool)
        extra.unlink()
        first = tool / "00.dll"
        first.chmod(0o600)
        with first.open("r+b") as stream: stream.truncate((32 << 20)+1)
        first.chmod(0o444)
        with patch.object(self.coverage.os, "read", side_effect=AssertionError("overlimit-must-not-read")) as hashed:
            with self.assertRaises(self.coverage.ProductCoverageError): self.measure(tool)
        hashed.assert_not_called()

    def test_exact_entry_and_depth_limits_have_one_over_rejecting_neighbors(self):
        tool = self.root / "entry-bound"
        tool.mkdir(mode=0o700)
        for index in range(2047):
            path = tool / f"f{index:04d}.bin"
            path.write_bytes(b"x"); path.chmod(0o444)
        files, summary = self.measure(tool)
        self.assertEqual((2047, 1, 2048), (len(files), summary["directory_count"], summary["maximum_entries"]))
        extra = tool / "extra.bin"
        extra.write_bytes(b"x"); extra.chmod(0o444)
        with self.assertRaises(self.coverage.ProductCoverageError): self.measure(tool)
        deep = self.root / "depth-bound"
        deep.mkdir(mode=0o700)
        current = deep
        for _ in range(8): current = current / "d"; current.mkdir(mode=0o700)
        (current.parent / "leaf.bin").write_bytes(b"x"); (current.parent / "leaf.bin").chmod(0o444)
        _, valid = self.measure(deep)
        self.assertEqual(8, valid["maximum_depth"])
        (current / "ninth").mkdir(mode=0o700)
        with self.assertRaises(self.coverage.ProductCoverageError): self.measure(deep)


class PrivateLinuxPublishMetadataControls(unittest.TestCase):
    """Ordinary owned metadata files only; no restore, build, lease or authority."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="private-linux-publish-data-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.uid = os.geteuid()
        self.deadline = prepare.time.monotonic()+10
        self.command = patch.object(prepare.Runner, "run", side_effect=AssertionError("no-command-dispatch"))
        self.command_mock = self.command.start()
        self.addCleanup(self.command.stop)

    def tearDown(self):
        self.command_mock.assert_not_called()

    def document(self):
        return {"version": 2, "dependencies": {"net10.0": {
            "Package.One": {"type": "Direct", "requested": "[1.2.3, )", "resolved": "1.2.3",
                            "contentHash": "ordinary-data-content-hash", "dependencies": {"Package.Two": "2.0.0"}},
            "Project.One": {"type": "Project", "dependencies": {"Package.One": "[1.2.3, )"}}}}}

    def encode(self, value):
        return json.dumps(value, sort_keys=True).encode()

    def with_rid(self, value):
        result = json.loads(self.encode(value))
        result["dependencies"][prepare.PRIVATE_PUBLISH_TARGET] = json.loads(self.encode(value["dependencies"]["net10.0"]))
        return result

    def write_lock(self, relative="Project/packages.lock.json", value=None):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(self.encode(self.document() if value is None else value))
        path.chmod(0o600)
        return path

    def snapshot(self, paths=None):
        return prepare.snapshot_private_publish_locks(self.root, self.deadline, paths, expected_owner_uid=self.uid)

    def rejected(self, action):
        with self.assertRaises(prepare.PreparationFailure) as failure:
            action()
        self.assertEqual("qualification-preparation-rejected", str(failure.exception))
        self.assertIsNone(failure.exception.__cause__)

    def test_real_lock_snapshot_accepts_only_inherited_rid_rows_and_preserves_input_bytes(self):
        before = self.document()
        path = self.write_lock(value=before)
        saved = self.snapshot(("Project/packages.lock.json",))
        self.assertEqual(path.read_bytes(), saved["Project/packages.lock.json"])
        self.assertFalse(prepare.validate_private_publish_lock(saved["Project/packages.lock.json"], self.encode(before)))
        after = self.with_rid(before)
        del after["dependencies"][prepare.PRIVATE_PUBLISH_TARGET]["Project.One"]
        self.assertTrue(prepare.validate_private_publish_lock(saved["Project/packages.lock.json"], self.encode(after)))
        self.assertEqual(saved["Project/packages.lock.json"], path.read_bytes())
        self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))

    def test_original_version_hash_dependency_and_scalar_type_drift_have_valid_neighbors(self):
        before = self.document()
        good = self.with_rid(before)
        for field, value in (("resolved", "1.2.4"), ("contentHash", "different"),
                             ("requested", "[1.0.0, )"), ("dependencies", {"Package.Two": "3.0.0"}),
                             ("type", True)):
            with self.subTest(field=field):
                self.assertTrue(prepare.validate_private_publish_lock(self.encode(before), self.encode(good)))
                bad = json.loads(self.encode(good))
                bad["dependencies"]["net10.0"]["Package.One"][field] = value
                self.rejected(lambda: prepare.validate_private_publish_lock(self.encode(before), self.encode(bad)))
        for change in ("version", "missing-group", "missing-node"):
            bad = json.loads(self.encode(good))
            if change == "version": bad["version"] = True
            elif change == "missing-group": del bad["dependencies"]["net10.0"]
            else: del bad["dependencies"]["net10.0"]["Project.One"]
            self.rejected(lambda: prepare.validate_private_publish_lock(self.encode(before), self.encode(bad)))

    def test_unknown_sdk_nodes_wrong_rid_and_mismatched_rid_rows_fail_closed(self):
        before = self.document()
        good = self.with_rid(before)
        for change in ("new-sdk-node", "wrong-rid", "wrong-rid-row", "new-target", "scalar-row"):
            with self.subTest(change=change):
                self.assertTrue(prepare.validate_private_publish_lock(self.encode(before), self.encode(good)))
                bad = json.loads(self.encode(good))
                if change == "new-sdk-node": bad["dependencies"][prepare.PRIVATE_PUBLISH_TARGET]["Unreviewed.SDK"] = {"type": "Transitive", "resolved": "10.0.401"}
                elif change == "wrong-rid": bad["dependencies"]["net10.0/osx-arm64"] = bad["dependencies"].pop(prepare.PRIVATE_PUBLISH_TARGET)
                elif change == "wrong-rid-row": bad["dependencies"][prepare.PRIVATE_PUBLISH_TARGET]["Package.One"]["resolved"] = "9.9.9"
                elif change == "new-target": bad["dependencies"]["net9.0"] = {}
                else: bad["dependencies"][prepare.PRIVATE_PUBLISH_TARGET]["Package.One"] = ["canary-not-a-row"]
                self.rejected(lambda: prepare.validate_private_publish_lock(self.encode(before), self.encode(bad)))

    def test_bad_json_duplicate_members_and_non_json_numbers_emit_only_fixed_failure(self):
        before = self.encode(self.document())
        for raw in (b'{"version":2,"version":2,"dependencies":{}}',
                    b'{"version":2,"Version":2,"dependencies":{}}', b'{"canary-secret":',
                    b'{"version":NaN,"dependencies":{}}', b'[]', b'\xff',
                    b'{"version":2,"dependencies":{"net10.0":{"P":{},"P":{}}}}'):
            with self.subTest(shape=raw[:8]):
                self.rejected(lambda: prepare.validate_private_publish_lock(before, raw))
        self.assertTrue(prepare.validate_private_publish_lock(before, self.encode(self.with_rid(self.document()))))

    def test_lock_paths_are_exact_and_links_or_nonregular_files_never_escape_root(self):
        path = self.write_lock()
        expected = ("Project/packages.lock.json",)
        self.assertEqual(set(expected), set(self.snapshot(expected)))
        extra = self.write_lock("Other/packages.lock.json")
        self.rejected(lambda: self.snapshot(expected))
        extra.unlink(); extra.parent.rmdir()
        original = path.read_bytes()
        path.unlink()
        self.rejected(lambda: self.snapshot(expected))
        path.write_bytes(original)
        outside = self.root.parent / (self.root.name+"-outside")
        outside.write_bytes(b"outside-sentinel")
        self.addCleanup(lambda: outside.unlink(missing_ok=True))
        for shape in ("symlink", "hardlink", "fifo"):
            with self.subTest(shape=shape):
                path.unlink()
                if shape == "symlink": path.symlink_to(outside)
                elif shape == "hardlink": os.link(outside, path)
                else: os.mkfifo(path)
                with self.assertRaises((prepare.PreparationFailure, OSError)):
                    self.snapshot(expected)
                self.assertEqual(b"outside-sentinel", outside.read_bytes())
                path.unlink(); path.write_bytes(original)
        os.chmod(self.root, 0o720)
        self.rejected(lambda: self.snapshot(expected))
        self.root.chmod(0o700)

    def test_count_bytes_traversal_and_deadline_limits_reject_before_unbounded_reads(self):
        path = self.write_lock()
        size = len(path.read_bytes())
        with patch.object(prepare, "PRIVATE_LOCK_COUNT", 1), patch.object(prepare, "PRIVATE_LOCK_BYTES", size), patch.object(prepare, "PRIVATE_LOCK_TOTAL", size):
            self.assertEqual(1, len(self.snapshot()))
            with patch.object(prepare, "PRIVATE_LOCK_BYTES", size-1), patch.object(prepare.os, "read", side_effect=AssertionError("must-not-read-oversize")) as read:
                self.rejected(self.snapshot)
            read.assert_not_called()
            self.write_lock("Second/packages.lock.json")
            self.rejected(self.snapshot)
        with patch.object(prepare, "PRIVATE_LOCK_SCAN_ENTRIES", 1):
            self.rejected(self.snapshot)
        with patch.object(prepare.os, "open", side_effect=AssertionError("expired-must-not-open")) as opened:
            self.rejected(lambda: prepare.snapshot_private_publish_locks(self.root, prepare.time.monotonic()-1,
                                                                          expected_owner_uid=self.uid))
        opened.assert_not_called()

    def test_actual_assets_require_exact_project_and_rid_without_loading_an_assembly(self):
        directory = self.root / Path(prepare.CLI_PROJECT).parent / "obj"
        directory.mkdir(parents=True)
        path = directory / "project.assets.json"
        good = {"targets": {prepare.PRIVATE_PUBLISH_TARGET: {}}, "project": {"restore": {
            "projectPath": str(self.root / prepare.CLI_PROJECT)}}}
        for change in (None, "missing-rid", "wrong-rid", "foreign-project", "scalar-target", "duplicate"):
            data = json.loads(self.encode(good))
            if change == "missing-rid": data["targets"] = {"net10.0": {}}
            elif change == "wrong-rid": data["targets"] = {"net10.0/win-x64": {}}
            elif change == "foreign-project": data["project"]["restore"]["projectPath"] += ".foreign"
            elif change == "scalar-target": data["targets"][prepare.PRIVATE_PUBLISH_TARGET] = True
            raw = self.encode(data) if change != "duplicate" else b'{"targets":{},"targets":{}}'
            path.write_bytes(raw)
            if change is None:
                result = prepare.private_publish_assets(self.root, self.deadline, expected_owner_uid=self.uid)
                self.assertEqual((prepare.PRIVATE_PUBLISH_TARGET, len(raw), hashlib.sha256(raw).hexdigest()),
                                 (result["target"], result["bytes"], result["sha256"]))
            else:
                self.rejected(lambda: prepare.private_publish_assets(self.root, self.deadline, expected_owner_uid=self.uid))

    def test_real_metadata_growth_and_named_substitution_close_owned_file_fds(self):
        path = self.write_lock()
        saved = path.read_bytes()
        original_read, original_open = prepare.os.read, prepare.os.open
        for change in ("growth", "substitution"):
            with self.subTest(change=change):
                path.write_bytes(saved)
                wanted_inode = path.stat().st_ino
                changed, owned = [], []
                def record_open(name, flags, *args, **kwargs):
                    fd = original_open(name, flags, *args, **kwargs)
                    if name == path.name: owned.append(fd)
                    return fd
                def mutate(fd, count):
                    if not changed and os.fstat(fd).st_ino == wanted_inode:
                        changed.append(True)
                        if change == "growth":
                            with path.open("ab") as stream: stream.write(b"x")
                        else:
                            path.rename(path.with_name("original.saved"))
                            path.write_bytes(saved)
                    return original_read(fd, count)
                with patch.object(prepare.os, "open", record_open), patch.object(prepare.os, "read", mutate):
                    self.rejected(self.snapshot)
                self.assertTrue(changed)
                self.assertTrue(owned)
                for fd in owned:
                    with self.assertRaises(OSError): os.fstat(fd)
                displaced = path.with_name("original.saved")
                if displaced.exists():
                    self.assertEqual(saved, displaced.read_bytes())
                    displaced.unlink()


class PrivateLinuxPublishFailureDiagnosticControls(unittest.TestCase):
    """Closed metadata and real owned FDs; never SDK, build, root authority or admission."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="linux-publish-failure-data-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.uid = os.geteuid()
        self.deadline = prepare.time.monotonic()+10
        self.source_commit = "a"*40
        self.path = self.root / "build-binding.json"
        self.guard = patch.object(prepare.Runner, "run", side_effect=AssertionError("no-command-dispatch"))
        self.commands = self.guard.start()
        self.addCleanup(self.guard.stop)

    def tearDown(self):
        self.commands.assert_not_called()

    def document(self):
        return {"version": 2, "dependencies": {"net10.0": {"Package.One": {
            "type": "Direct", "resolved": "1.0.0", "requested": "[1.0.0, )",
            "contentHash": "ordinary-file-data", "dependencies": {"Package.Two": "2.0.0"}}}}}

    def diagnostic(self, checkpoint="lock-comparison"):
        value = prepare.PrivatePublishDiagnostic()
        value.value.update(checkpoint=checkpoint, expected_lock_count=1, lock_ordinal=0)
        return value

    def compare(self, before, after, category):
        diagnostic = self.diagnostic()
        with self.assertRaises(prepare.PreparationFailure) as caught:
            prepare.validate_private_publish_lock(json.dumps(before).encode(), json.dumps(after).encode(), diagnostic=diagnostic)
        self.assertEqual("qualification-preparation-rejected", str(caught.exception))
        self.assertIsNone(caught.exception.__cause__)
        self.assertEqual(category, diagnostic.snapshot()["category"])
        return diagnostic.snapshot()

    def binding(self):
        distribution = {"schema": "issue779-pinned-sdk-distribution-v1", "sdk_version": prepare.SDK_VERSION,
            "rid": "linux-x64", "root": str(prepare.SDK_ROOT), "archive_url": prepare.ARCHIVE_URL,
            "archive_sha512": prepare.ARCHIVE_SHA512, "compressed_bytes": 1, "expanded_bytes": 1,
            "node_count": 1, "explicit_member_count": 1, "gnu_longname_headers": 0,
            "tree_sha256": "b"*64, "complete": True, "sdk_audit_completed": False, "qualification_claim": False}
        return {"source_commit": self.source_commit, "preparation_complete": False, "sdk_bootstrap": {
            "schema": "issue779-trusted-sdk-bootstrap-preflight-v1", "root": str(prepare.SDK_ROOT),
            "host_before": {"uid": self.uid}, "host_metadata_state": "observed", "distribution": distribution,
            "preinstallation_ancestors": {"write_bits_cleared": True}}}

    def write_binding(self, binding=None):
        self.path.write_bytes(json.dumps(self.binding() if binding is None else binding).encode())
        self.path.chmod(0o600)
        return self.path.read_bytes()

    def capture(self, diagnostic=None, deadline=None):
        return prepare.retain_private_publish_failure(self.root, self.source_commit,
            self.diagnostic() if diagnostic is None else diagnostic,
            self.deadline if deadline is None else deadline, expected_owner_uid=self.uid)

    def test_json_schema_version_group_and_cli_target_categories_have_valid_neighbors(self):
        before = self.document()
        good = json.loads(json.dumps(before))
        good["dependencies"][prepare.PRIVATE_PUBLISH_TARGET] = json.loads(json.dumps(before["dependencies"]["net10.0"]))
        self.assertTrue(prepare.validate_private_publish_lock(json.dumps(before).encode(), json.dumps(good).encode()))
        for raw in (b'{"secret-canary":', b'{"version":2,"version":2,"dependencies":{}}'):
            diagnostic = self.diagnostic()
            with self.assertRaises(prepare.PreparationFailure) as caught:
                prepare.validate_private_publish_lock(json.dumps(before).encode(), raw, diagnostic=diagnostic)
            self.assertEqual("JSON", diagnostic.snapshot()["category"])
            self.assertNotIn("secret-canary", json.dumps(diagnostic.snapshot()))
            self.assertIsNone(caught.exception.__cause__)
        bad = json.loads(json.dumps(good)); bad["version"] = True
        self.compare(before, bad, "schema")
        bad = json.loads(json.dumps(good)); bad["version"] = 3
        self.compare(before, bad, "version")
        bad = json.loads(json.dumps(good)); del bad["dependencies"]["net10.0"]
        self.compare(before, bad, "original-group")
        bad = json.loads(json.dumps(good)); bad["dependencies"]["net10.0/osx-arm64"] = {}
        self.compare(before, bad, "newRIDgroup")
        diagnostic = self.diagnostic("cli-rid-target")
        self.assertFalse(prepare.validate_private_publish_lock(json.dumps(before).encode(), json.dumps(before).encode(), diagnostic=diagnostic))
        self.assertEqual("schema", diagnostic.snapshot()["category"])

    def test_original_row_fields_and_rid_nodes_emit_hashes_and_closed_equality_facts(self):
        before = self.document()
        good = json.loads(json.dumps(before))
        good["dependencies"][prepare.PRIVATE_PUBLISH_TARGET] = json.loads(json.dumps(before["dependencies"]["net10.0"]))
        for field in ("resolved", "contentHash", "dependencies", "type", "requested"):
            bad = json.loads(json.dumps(good))
            bad["dependencies"]["net10.0"]["Package.One"][field] = {"canary": "value"} if field == "dependencies" else "secret-canary"
            result = self.compare(before, bad, "row")
            self.assertEqual([field], result["fields"])
            self.assertFalse(result["original_group_identical"])
            self.assertEqual(hashlib.sha256(b"Package.One").hexdigest(), result["row_sha256"])
            self.assertNotIn("secret-canary", json.dumps(result))
        bad = json.loads(json.dumps(good))
        bad["dependencies"][prepare.PRIVATE_PUBLISH_TARGET]["unknown-secret-canary"] = {"type": "Transitive", "resolved": "unknown-canary"}
        result = self.compare(before, bad, "unknownRIDnode")
        self.assertEqual("Transitive", result["package_type"])
        self.assertTrue(result["original_group_identical"])
        self.assertIsNone(result["resolved_equal"])
        self.assertNotIn("canary", json.dumps(result))
        bad = json.loads(json.dumps(good)); bad["dependencies"][prepare.PRIVATE_PUBLISH_TARGET]["Package.One"]["contentHash"] = "different"
        result = self.compare(before, bad, "RIDrow")
        self.assertTrue(result["resolved_equal"]); self.assertFalse(result["content_hash_equal"])
        self.assertTrue(result["dependencies_equal"])

    def test_real_snapshot_pathset_metadata_bounds_and_deadline_rejections_remain_failures(self):
        path = self.root / "packages.lock.json"
        path.write_bytes(json.dumps(self.document()).encode()); path.chmod(0o600)
        expected = (path.name,)
        self.assertEqual(set(expected), set(prepare.snapshot_private_publish_locks(self.root, self.deadline, expected, expected_owner_uid=self.uid)))
        for kind in ("pathset", "metadata", "bounds", "deadline"):
            diagnostic = self.diagnostic("post-refresh-snapshot")
            if kind == "pathset": wanted = ("missing.lock.json",)
            else: wanted = expected
            if kind == "metadata": path.chmod(0o600); path.unlink(); path.symlink_to(self.root / "missing-target")
            try:
                with patch.object(prepare, "PRIVATE_LOCK_BYTES", 1 if kind == "bounds" else prepare.PRIVATE_LOCK_BYTES):
                    with self.assertRaises((prepare.PreparationFailure, OSError)):
                        prepare.snapshot_private_publish_locks(self.root, prepare.time.monotonic()-1 if kind == "deadline" else self.deadline,
                            wanted, expected_owner_uid=self.uid, diagnostic=diagnostic)
                self.assertEqual(kind, diagnostic.snapshot()["category"])
            finally:
                if path.is_symlink(): path.unlink(); path.write_bytes(json.dumps(self.document()).encode()); path.chmod(0o600)

    def test_existing_partial_binding_is_atomically_extended_once_and_preserved(self):
        original = self.write_binding()
        diagnostic = self.diagnostic()
        diagnostic.value.update(category="unknownRIDnode", row_sha256="c"*64)
        self.assertTrue(self.capture(diagnostic))
        after = json.loads(self.path.read_bytes())
        observation = after.pop("private_linux_publish_failure")
        self.assertEqual(json.loads(original), after)
        self.assertEqual(diagnostic.snapshot(), observation)
        self.assertLessEqual(len(json.dumps(observation, separators=(",", ":")).encode()), 1024)
        self.assertLessEqual(self.path.stat().st_size, 4096)
        self.assertEqual(0o600, stat.S_IMODE(self.path.stat().st_mode)); self.assertEqual(1, self.path.stat().st_nlink)
        completed = self.path.read_bytes()
        self.assertFalse(self.capture())
        self.assertEqual(completed, self.path.read_bytes())
        self.assertFalse((self.root / ".build-binding-linux-publish-diagnostic.tmp").exists())

    def test_missing_foreign_or_unsafe_binding_and_malformed_observation_never_write(self):
        original = self.write_binding()
        for kind in ("missing", "mode", "hardlink", "symlink", "complete", "foreign", "record", "oversize", "total-bound", "expired"):
            self.path.unlink(missing_ok=True)
            other = self.root / "other"; other.unlink(missing_ok=True)
            self.write_binding()
            diagnostic = self.diagnostic()
            if kind == "missing": self.path.unlink()
            elif kind == "mode": self.path.chmod(0o640)
            elif kind == "hardlink": os.link(self.path, other)
            elif kind == "symlink": self.path.rename(other); self.path.symlink_to(other)
            elif kind == "complete": value = self.binding(); value["preparation_complete"] = True; self.write_binding(value)
            elif kind == "foreign": value = self.binding(); value["source_commit"] = "f"*40; self.write_binding(value)
            elif kind == "record": diagnostic.value["category"] = "secret-canary"
            elif kind == "oversize": self.path.write_bytes(b"x"*4097)
            elif kind == "total-bound":
                value = self.binding(); value["sdk_bootstrap"]["preinstallation_ancestors"]["padding"] = ""
                value["sdk_bootstrap"]["preinstallation_ancestors"]["padding"] = "x"*(4090-len(json.dumps(value).encode()))
                self.write_binding(value)
                self.assertEqual(4090, self.path.stat().st_size)
            with patch.object(prepare.os, "write", side_effect=AssertionError("must-not-write")) as wrote:
                self.assertFalse(self.capture(diagnostic, prepare.time.monotonic()-1 if kind == "expired" else None))
            wrote.assert_not_called()
            if other.exists(): self.assertEqual(original, other.read_bytes())
            self.path.unlink(missing_ok=True); other.unlink(missing_ok=True)
        self.write_binding()
        self.assertTrue(self.capture())

    def test_named_binding_substitution_after_read_closes_fds_without_replacing_new_file(self):
        original = self.write_binding()
        inode = self.path.stat().st_ino
        actual_read, actual_open = prepare.os.read, prepare.os.open
        changed, owned = [], []
        def observe_open(name, flags, *args, **kwargs):
            fd = actual_open(name, flags, *args, **kwargs)
            owned.append(fd)
            return fd
        def substitute(fd, count):
            data = actual_read(fd, count)
            if not changed and os.fstat(fd).st_ino == inode:
                changed.append(True)
                self.path.rename(self.root / "original.saved")
                self.path.write_bytes(b"replacement-canary"); self.path.chmod(0o600)
            return data
        with patch.object(prepare.os, "open", observe_open), patch.object(prepare.os, "read", substitute):
            self.assertFalse(self.capture())
        self.assertTrue(changed)
        self.assertEqual(b"replacement-canary", self.path.read_bytes())
        self.assertEqual(original, (self.root / "original.saved").read_bytes())
        for fd in owned:
            with self.assertRaises(OSError): os.fstat(fd)

    def test_write_failure_keeps_original_exception_and_conservative_temporary_rollback(self):
        original = self.write_binding()
        error = ValueError("original-private-canary")
        with self.assertRaises(ValueError) as caught:
            try:
                raise error
            except ValueError:
                with patch.object(prepare.os, "write", side_effect=OSError("write-private-canary")):
                    self.assertFalse(self.capture())
                raise
        self.assertIs(error, caught.exception)
        self.assertEqual(original, self.path.read_bytes())
        self.assertFalse((self.root / ".build-binding-linux-publish-diagnostic.tmp").exists())
        self.assertTrue(self.capture())

    def test_temporary_name_replacement_is_retained_and_late_deadline_cannot_report_capture(self):
        original = self.write_binding()
        actual_write = prepare.os.write
        def replace_temporary(fd, value):
            name = self.root / ".build-binding-linux-publish-diagnostic.tmp"
            name.rename(self.root / "held.saved")
            name.write_bytes(b"foreign-sentinel"); name.chmod(0o600)
            return actual_write(fd, value)
        with patch.object(prepare.os, "write", replace_temporary):
            self.assertFalse(self.capture())
        self.assertEqual(b"foreign-sentinel", (self.root / ".build-binding-linux-publish-diagnostic.tmp").read_bytes())
        self.assertEqual(original, self.path.read_bytes())
        (self.root / ".build-binding-linux-publish-diagnostic.tmp").unlink()
        (self.root / "held.saved").unlink()
        actual_replace = prepare.os.replace
        def expire_after_replace(*args, **kwargs):
            result = actual_replace(*args, **kwargs)
            self.deadline = 0
            return result
        # Closed bytes may exist after a late deadline; capture still reports false.
        with patch.object(prepare.os, "replace", expire_after_replace), patch.object(prepare.time, "monotonic", side_effect=lambda: 1 if self.deadline == 0 else 0):
            self.assertFalse(self.capture(deadline=0.5))
        self.assertIs(json.loads(self.path.read_bytes())["preparation_complete"], False)


if __name__ == "__main__":
    unittest.main()
