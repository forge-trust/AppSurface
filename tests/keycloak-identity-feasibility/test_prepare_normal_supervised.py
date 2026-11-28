"""Check the isolated replay inputs, worker payload and preparation guards."""

import hashlib
import importlib.util
import json
from pathlib import Path
from subprocess import CompletedProcess
import sys
import tempfile
import unittest
from unittest.mock import patch
import warnings
from xml.etree import ElementTree
from zipfile import ZipFile

from replay_environment import (SDK_VERSION, docker_endpoint, dotnet_host, prepare_offline_nuget,
                                prepared_file_manifest, run_offline_build, verify_offline_restore,
                                verify_prepared_closure)
from worker_payload import PURE_LIBRARY, WORKER_PREFIX, read_worker_payload, worker_payload_target


class CompositionArtifactStagingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).with_name("prepare-normal-supervised.py")
        spec = importlib.util.spec_from_file_location("prepare_normal_supervised", path)
        cls.preparer = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.preparer)

    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name).resolve()
        self.composition = self.root / "composition"
        self.code = self.composition / "code"
        self.browser = self.composition / "browser"
        self.code.mkdir(parents=True)
        self.browser.mkdir()
        self.dll = self.code / "ForgeTrust.AppSurface.Auth.Keycloak.dll"
        self.dll.write_bytes(b"composition candidate library")
        self.browser_script = self.browser / "supervised-browser.mjs"
        self.browser_script.write_bytes(b"browser driver")
        self.receipt = {
            "root": str(self.composition),
            "codeManifest": prepared_file_manifest(self.code),
            "browserScriptSha256": hashlib.sha256(self.browser_script.read_bytes()).hexdigest(),
        }
        self.fresh = self.root / "fresh"
        self.fresh.mkdir()

    def test_matching_composition_artifacts_are_staged_exactly(self):
        self.preparer.stage_verified_composition(self.receipt, self.fresh)
        payload = self.fresh / "payload"
        browser = self.fresh / "browser"
        self.assertEqual(self.receipt["codeManifest"], prepared_file_manifest(payload))
        self.assertEqual(self.dll.read_bytes(), (payload / self.dll.name).read_bytes())
        self.assertEqual(self.receipt["browserScriptSha256"],
                         hashlib.sha256((browser / self.browser_script.name).read_bytes()).hexdigest())

    def test_changed_composition_dll_fails_before_candidate_overlay(self):
        self.dll.write_bytes(b"changed composition library")
        with self.assertRaisesRegex(ValueError, "prepared closure changed"):
            self.preparer.stage_verified_composition(self.receipt, self.fresh)

    def test_changed_browser_script_fails_on_copied_driver_hash(self):
        self.browser_script.write_bytes(b"changed browser driver")
        with self.assertRaisesRegex(ValueError, "prepared browser driver changed"):
            self.preparer.stage_verified_composition(self.receipt, self.fresh)


class ReplayCaseExpectationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = Path(__file__).with_name("run-normal-supervised.py")
        spec = importlib.util.spec_from_file_location("run_normal_supervised", path)
        cls.driver = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.driver)

    def test_every_selected_case_has_one_expectation(self):
        driver = self.driver
        selected = set(driver.DEFAULT_CASES) | set(driver.POST_SEED_CASES)
        for names in driver.ACCEPTANCE_CASES.values():
            with self.subTest(names=names):
                self.assertEqual(len(names), len(set(names)))
            selected.update(names)
        self.assertEqual(selected, set(driver.CASE_EXPECTATIONS))
        self.assertIn("success", driver.DEFAULT_CASES)

    def test_startup_evidence_and_host_exit_are_independent(self):
        driver = self.driver
        expected = {
            "neutral-fresh": (driver.StartupOutcome.WEB_READY, 0, None, False),
            "uncertain-stop": (driver.StartupOutcome.WEB_READY, 1, None, True),
            "public-secret": (driver.StartupOutcome.PUBLIC_INPUT_REJECTED, 1, None, False),
            "skoolit-seed-failure": (driver.StartupOutcome.SEED_FAILED, 1, None, False),
            "skoolit-google-id-only": (driver.StartupOutcome.PREFLIGHT_REJECTED, 44, "ASKEYC027", False),
            "skoolit-google-secret-only": (driver.StartupOutcome.PREFLIGHT_REJECTED, 44, "ASKEYC027", False),
            "skoolit-unapproved-issuer": (driver.StartupOutcome.PREFLIGHT_REJECTED, 44, "ASKEYC001", False),
            "legacy-missing": (driver.StartupOutcome.MISSING_VOLUME, 1, "ASKEYC028", False),
            "legacy-moved": (driver.StartupOutcome.MISSING_VOLUME, 1, "ASKEYC028", False),
            "drift-client": (driver.StartupOutcome.DRIFT_REJECTED, 1, "ASKEYC031", False),
            "drift-mapper": (driver.StartupOutcome.DRIFT_REJECTED, 1, "ASKEYC031", False),
            "drift-broker": (driver.StartupOutcome.DRIFT_REJECTED, 1, "ASKEYC031", False),
        }
        for name, (startup, exit_code, diagnostic, retains_owner) in expected.items():
            with self.subTest(name=name):
                self.assertEqual(
                    driver.CaseExpectation(startup, exit_code, diagnostic, retains_owner),
                    driver.CASE_EXPECTATIONS[name],
                )


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
        restored = {"libraries": {}, "project": {"restore": {}}}
        assets.write_text(json.dumps(restored))
        verify_offline_restore(assets, packages)
        (packages / "unexpected").touch()
        with self.assertRaisesRegex(ValueError, "cache is not empty"):
            verify_offline_restore(assets, packages)
        restored["libraries"]["unexpected/1.0"] = {}
        assets.write_text(json.dumps(restored))
        with self.assertRaisesRegex(ValueError, "resolved package libraries"):
            verify_offline_restore(assets, packages)

    def test_offline_nuget_config_isolates_the_feed_and_cache_folders(self):
        packages = self.root / "cache/nuget"
        feed = self.root / "local & offline feed"
        feed.mkdir()
        overrides = {
            "rEsToReSoUrCeS": str(self.root / "unexpected"),
            "RestoreAdditionalProjectSources": str(self.root / "extra"),
            "RestoreFallbackFolders": str(self.root / "fallback"),
            "RestoreAdditionalProjectFallbackFolders": str(self.root / "extra-fallback"),
            "RestoreConfigFile": str(self.root / "other.config"),
            "RestorePackagesPath": str(self.root / "other-packages"),
        }
        with patch.dict("os.environ", overrides):
            config, env = prepare_offline_nuget(self.root / "dotnet", self.root, packages, feed=feed)

        sources = ElementTree.parse(config).find("packageSources")
        self.assertEqual(["clear", "add"], [source.tag for source in sources])
        self.assertEqual(str(feed), sources.find("add").get("value"))
        self.assertIsNotNone(ElementTree.parse(config).find("fallbackPackageFolders/clear"))
        self.assertTrue(overrides.keys().isdisjoint(env))
        for key, path in (("NUGET_PACKAGES", packages), ("NUGET_HTTP_CACHE_PATH", packages.parent / "http-cache"),
                          ("NUGET_SCRATCH", packages.parent / "scratch")):
            self.assertEqual(str(path), env[key])
            self.assertEqual([], list(path.iterdir()))

    def test_offline_restore_requires_effective_source_metadata(self):
        packages = self.root / "packages"
        packages.mkdir()
        assets = self.root / "project.assets.json"
        assets.write_text('{"libraries":{}}')
        with self.assertRaisesRegex(ValueError, "metadata is missing"):
            verify_offline_restore(assets, packages)
        assets.write_text(json.dumps({"libraries": {}, "project": {"restore": {"sources": {str(self.root / "extra"): {}}}}}))
        with self.assertRaisesRegex(ValueError, "unexpected package sources"):
            verify_offline_restore(assets, packages)

    def test_offline_restore_allows_only_its_declared_local_feed(self):
        packages = self.root / "packages"
        packages.mkdir()
        (packages / "fixture").mkdir()
        feed = self.root / "feed"
        feed.mkdir()
        assets = self.root / "project.assets.json"
        sources = {str(feed): {}}
        restored = {"libraries": {"fixture/1.0.0": {}}, "packageFolders": {str(packages): {}},
                    "project": {"restore": {"sources": sources}}}
        assets.write_text(json.dumps(restored))
        verify_offline_restore(assets, packages, exact_package_folder=True, feed=feed)
        sources[str(self.root / "extra")] = {}
        assets.write_text(json.dumps(restored))
        with self.assertRaisesRegex(ValueError, "unexpected package sources"):
            verify_offline_restore(assets, packages, exact_package_folder=True, feed=feed)

    def test_worker_anchor_requires_the_exact_private_package_folder(self):
        packages = self.root / "packages"
        packages.mkdir()
        assets = self.root / "project.assets.json"
        data = {"libraries": {}, "packageFolders": {str(packages): {}}, "project": {"restore": {}}}
        assets.write_text(json.dumps(data))
        verify_offline_restore(assets, packages, exact_package_folder=True)
        data["packageFolders"][str(self.root / "other")] = {}
        assets.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, "unexpected package folders"):
            verify_offline_restore(assets, packages, exact_package_folder=True)

    def test_offline_build_requires_both_log_paths_before_restore(self):
        cache = self.root / "offline"
        project = self.root / "Fixture.csproj"
        restore_log = self.root / "restore.log"
        output_log = self.root / "build.log"

        for logs in ((restore_log,), (restore_log, None), (restore_log, output_log, self.root / "extra.log")):
            with self.subTest(logs=logs):
                with patch("replay_environment.subprocess.run") as run:
                    with self.assertRaisesRegex(ValueError, "provide both the restore log path"):
                        run_offline_build(self.root / "dotnet", project, cache_root=cache, logs=logs)
                run.assert_not_called()
                self.assertFalse(cache.exists())
                self.assertFalse(project.with_name("NuGet.Config").exists())
                self.assertFalse(restore_log.exists())
                self.assertFalse(output_log.exists())

    def test_offline_build_stops_after_a_failed_restore(self):
        cache = self.root / "offline"
        project = self.root / "Fixture.csproj"
        restore_log = self.root / "0.log"
        publish_log = self.root / "1.log"
        with patch("replay_environment.subprocess.run", return_value=CompletedProcess([], 1)) as run:
            result = run_offline_build(self.root / "dotnet", project, cache_root=cache,
                                       logs=(restore_log, publish_log), publish_to=self.root / "publish")
        run.assert_called_once()
        self.assertEqual([("restore", 1)], [(step.stage, step.exit_code) for step in result.commands])
        self.assertEqual("restore", result.failed_stage)
        self.assertIsNone(result.verification_failure)
        self.assertEqual(str(cache / "nuget"), run.call_args.kwargs["env"]["NUGET_PACKAGES"])
        self.assertEqual(120, run.call_args.kwargs["timeout"])
        self.assertTrue(restore_log.is_file())
        self.assertFalse(publish_log.exists())

    def test_offline_build_rejects_unverified_assets_before_build_or_publish(self):
        for stage in ("build", "publish"):
            with self.subTest(stage=stage):
                directory = self.root / stage
                assets = directory / "obj/project.assets.json"
                assets.parent.mkdir(parents=True)
                assets.write_text(json.dumps({
                    "libraries": {},
                    "project": {"restore": {"sources": {str(directory / "unexpected-feed"): {}}}},
                }))
                restore_log, output_log = directory / "restore.log", directory / f"{stage}.log"
                with patch("replay_environment.subprocess.run", return_value=CompletedProcess([], 0)) as run:
                    result = run_offline_build(
                        self.root / "dotnet", directory / "Fixture.csproj", cache_root=directory / "cache",
                        logs=(restore_log, output_log),
                        publish_to=directory / "published" if stage == "publish" else None,
                    )

                run.assert_called_once()
                self.assertEqual("restore", run.call_args.args[0][1])
                self.assertEqual([("restore", 0)], [(step.stage, step.exit_code) for step in result.commands])
                self.assertEqual("verify-restore", result.verification_failure)
                self.assertEqual("verify-restore", result.failed_stage)
                self.assertIn("unexpected package sources", restore_log.read_text())
                self.assertFalse(output_log.exists())

    def test_offline_build_checks_the_exact_package_folder_before_publishing(self):
        assets = self.root / "obj/project.assets.json"
        assets.parent.mkdir()
        cache = self.root / "offline"
        assets.write_text(json.dumps({
            "libraries": {}, "project": {"restore": {}},
            "packageFolders": {str(cache / "nuget"): {}, str(self.root / "extra-cache"): {}},
        }))
        restore_log, publish_log = self.root / "restore.log", self.root / "publish.log"
        with patch("replay_environment.subprocess.run", return_value=CompletedProcess([], 0)) as run:
            result = run_offline_build(
                self.root / "dotnet", self.root / "Fixture.csproj", cache_root=cache,
                logs=(restore_log, publish_log), publish_to=self.root / "published", exact_package_folder=True,
            )
        run.assert_called_once()
        self.assertEqual([0], [step.exit_code for step in result.commands])
        self.assertEqual("verify-restore", result.verification_failure)
        self.assertIn("unexpected package folders", restore_log.read_text())
        self.assertFalse(publish_log.exists())

    def test_offline_build_rechecks_the_empty_cache_after_a_successful_publish(self):
        assets = self.root / "obj/project.assets.json"
        assets.parent.mkdir()
        assets.write_text(json.dumps({"libraries": {}, "project": {"restore": {}}}))
        cache = self.root / "offline"
        restore_log, publish_log = self.root / "restore.log", self.root / "publish.log"

        def run(command, **kwargs):
            if command[1] == "publish":
                (cache / "nuget" / "unexpected-package").mkdir()
            return CompletedProcess(command, 0)

        with patch("replay_environment.subprocess.run", side_effect=run):
            result = run_offline_build(
                self.root / "dotnet", self.root / "Fixture.csproj", cache_root=cache,
                logs=(restore_log, publish_log), publish_to=self.root / "published",
            )
        self.assertEqual([("restore", 0), ("publish", 0)], [(step.stage, step.exit_code) for step in result.commands])
        self.assertEqual("verify-publish", result.verification_failure)
        self.assertEqual("verify-publish", result.failed_stage)
        self.assertIn("private NuGet cache is not empty", publish_log.read_text())

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


class WorkerAnchorReceiptTests(unittest.TestCase):
    def test_restore_rejection_keeps_the_sdk_exit_and_skips_copying_outputs(self):
        script = Path(__file__).with_name("prepare-worker-anchor.py")
        spec = importlib.util.spec_from_file_location("prepare_worker_anchor", script)
        anchor = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(anchor)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            staged, evidence = root / "staged", root / "evidence"
            staged.mkdir()
            pure, adapter = root / "pure.nupkg", root / "adapter.nupkg"
            with ZipFile(pure, "w") as package:
                package.writestr("lib/net10.0/" + PURE_LIBRARY, b"fixture assembly")
            with ZipFile(adapter, "w") as package:
                package.writestr(WORKER_PREFIX + PURE_LIBRARY, b"fixture assembly")

            def restore(command, *, cwd, env, **kwargs):
                self.assertEqual("restore", command[1])
                assets = cwd / "obj/project.assets.json"
                assets.parent.mkdir()
                assets.write_text(json.dumps({
                    "libraries": {}, "project": {"restore": {}},
                    "packageFolders": {env["NUGET_PACKAGES"]: {}, str(root / "extra-cache"): {}},
                }))
                (staged / "fixture-publish/stale-output.dll").write_bytes(b"stale output")
                return CompletedProcess(command, 0)

            argv = [str(script), "--package", str(pure), "--adapter-package", str(adapter),
                    "--evidence", str(evidence), "--sdk", str(root / "sdk")]
            with patch.object(sys, "argv", argv), patch.object(anchor.tempfile, "mkdtemp", return_value=str(staged)):
                with patch.object(anchor.os, "umask"), patch("replay_environment.subprocess.run", side_effect=restore) as sdk:
                    self.assertEqual(1, anchor.main())

            sdk.assert_called_once()
            receipt = json.loads((evidence / "receipt.json").read_text())
            self.assertFalse(receipt["passed"])
            self.assertEqual("verify-restore", receipt["verificationFailure"])
            self.assertEqual(1, len(receipt["commands"]))
            self.assertEqual(sdk.call_args.args[0], receipt["commands"][0]["command"])
            self.assertEqual(0, receipt["commands"][0]["exitCode"])
            self.assertEqual({PURE_LIBRARY}, set(receipt["runtimePayload"]))
            self.assertFalse((staged / "code/stale-output.dll").exists())
            self.assertIn("unexpected package folders", (evidence / "0.log").read_text())
            self.assertFalse((evidence / "1.log").exists())
