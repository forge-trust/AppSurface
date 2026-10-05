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


if __name__ == "__main__":
    unittest.main()
