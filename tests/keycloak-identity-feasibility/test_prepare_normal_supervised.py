"""Check the isolated replay inputs, worker payload and preparation guards."""

import hashlib
import json
from pathlib import Path
from subprocess import CompletedProcess
import tempfile
import unittest
from unittest.mock import patch
import warnings
from zipfile import ZipFile

from replay_environment import (SDK_VERSION, docker_endpoint, dotnet_host, run_offline_build,
                                verify_offline_restore, verify_prepared_closure)
from worker_payload import WORKER_PREFIX, read_worker_payload, worker_payload_target


class WorkerPayloadTargetTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name).resolve()
        self.payload = self.root / "payload"
        self.payload.mkdir()

    def test_accepts_a_nested_payload_file(self):
        target = worker_payload_target(self.payload, "runtimes/worker.dll")
        self.assertEqual(self.payload / "runtimes" / "worker.dll", target)

    def test_rejects_invalid_payload_paths(self):
        for name in ("", str(self.root / "worker.dll"), "../worker.dll"):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "unsafe package entry"):
                worker_payload_target(self.payload, name)

    def test_rejects_a_destination_outside_the_payload(self):
        link = self.payload / "linked"
        link.symlink_to(self.root, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "unsafe package entry"):
            worker_payload_target(self.payload, "linked/worker.dll")

    def test_reads_only_nested_worker_files(self):
        archive = self.root / "adapter.nupkg"
        with ZipFile(archive, "w") as package:
            package.writestr("lib/net10.0/unrelated.dll", b"unrelated")
            package.writestr(WORKER_PREFIX + "runtimes/", b"")
            package.writestr(WORKER_PREFIX + "runtimes/worker.dll", b"worker")
        self.assertEqual({"runtimes/worker.dll": b"worker"}, read_worker_payload(archive))

    def test_duplicate_archive_entries_fail(self):
        archive = self.root / "adapter.nupkg"
        member = WORKER_PREFIX + "worker.dll"
        with ZipFile(archive, "w") as package, warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            package.writestr(member, b"first")
            package.writestr(member, b"second")
        with self.assertRaisesRegex(ValueError, "duplicate worker payload entry"):
            read_worker_payload(archive)


class ReplayEnvironmentTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name).resolve()

    def test_sdk_is_portable_and_receipt_binding_is_checked(self):
        sdk = self.root / "alternate tools"
        (sdk / "sdk" / SDK_VERSION).mkdir(parents=True)
        dotnet = sdk / "dotnet"
        dotnet.touch(mode=0o700)
        self.assertEqual(dotnet, dotnet_host(sdk))
        self.assertEqual(dotnet, dotnet_host(None, str(sdk)))
        with self.assertRaisesRegex(ValueError, "differs"):
            dotnet_host(self.root, str(sdk))

    def test_cleanup_keeps_the_recorded_socket(self):
        system_socket = "unix:///var/run/docker.sock"
        self.assertEqual(system_socket, docker_endpoint(recorded=system_socket))
        with self.assertRaisesRegex(ValueError, "differs"):
            docker_endpoint(docker_endpoint(), system_socket)

    def test_offline_restore_checks_inputs_and_the_cache(self):
        packages = self.root / "packages"
        packages.mkdir()
        assets = self.root / "project.assets.json"
        assets.write_text('{"libraries":{}}')
        verify_offline_restore(assets, packages)
        (packages / "unexpected").touch()
        with self.assertRaisesRegex(ValueError, "cache is not empty"):
            verify_offline_restore(assets, packages)
        assets.write_text('{"libraries":{"unexpected/1.0":{}}}')
        with self.assertRaisesRegex(ValueError, "resolved package libraries"):
            verify_offline_restore(assets, packages)

    def test_worker_anchor_requires_the_exact_private_package_folder(self):
        packages = self.root / "packages"
        packages.mkdir()
        assets = self.root / "project.assets.json"
        data = {"libraries": {}, "packageFolders": {str(packages): {}}}
        assets.write_text(json.dumps(data))
        verify_offline_restore(assets, packages, exact_package_folder=True)
        data["packageFolders"][str(self.root / "other")] = {}
        assets.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, "unexpected package folders"):
            verify_offline_restore(assets, packages, exact_package_folder=True)

    def test_offline_build_stops_after_a_failed_restore(self):
        cache = self.root / "offline"
        project = self.root / "Fixture.csproj"
        restore_log = self.root / "0.log"
        publish_log = self.root / "1.log"
        with patch("replay_environment.subprocess.run", return_value=CompletedProcess([], 1)) as run:
            steps = run_offline_build(self.root / "dotnet", project, cache_root=cache,
                                      logs=(restore_log, publish_log), publish_to=self.root / "publish")
        run.assert_called_once()
        self.assertEqual([("restore", 1)], [(step.stage, step.exit_code) for step in steps])
        self.assertEqual(str(cache / "nuget"), run.call_args.kwargs["env"]["NUGET_PACKAGES"])
        self.assertEqual(120, run.call_args.kwargs["timeout"])
        self.assertTrue(restore_log.is_file())
        self.assertFalse(publish_log.exists())

    def test_prepared_closure_checks_the_recorded_bytes(self):
        member = self.root / "worker.dll"
        member.write_bytes(b"worker")
        manifest = {member.name: hashlib.sha256(b"worker").hexdigest()}
        verify_prepared_closure(self.root, manifest)
        member.write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "prepared closure changed"):
            verify_prepared_closure(self.root, manifest)

    def test_prepared_closure_rejects_an_extra_file(self):
        member = self.root / "worker.dll"
        member.write_bytes(b"worker")
        manifest = {member.name: hashlib.sha256(b"worker").hexdigest()}
        (self.root / "extra.txt").touch()
        with self.assertRaisesRegex(ValueError, "prepared closure changed"):
            verify_prepared_closure(self.root, manifest)
