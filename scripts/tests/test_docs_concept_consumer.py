"""Unit tests for the packed Docs concept-consumer proof's evidence readers."""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest.mock
import unittest
import xml.etree.ElementTree as ET
import zipfile


SCRIPT = Path(__file__).resolve().parents[1] / "verify-docs-concept-consumer.py"
SPEC = importlib.util.spec_from_file_location("docs_concept_consumer_verifier", SCRIPT)
VERIFIER = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(VERIFIER)


class PackageVersionTests(unittest.TestCase):
    def test_accepts_stable_and_prerelease_versions(self):
        for version in (
            "0.0.0", "12.34.56", "1.2.3-0", "1.2.3-preview.183.local",
            "1.2.3-01a", "1.2.3--", "1.2.3-00-",
        ):
            with self.subTest(version=version):
                VERIFIER.validate_package_version(version)

    def test_rejects_malformed_or_noncanonical_versions(self):
        for version in (
            "", "1.2", "1..3", "a.2.3", "1.2.3.4", "01.2.3", "1.02.3", "1.2.03", "1.2.3-",
            "1.2.3-01", "1.2.3-a.00", "1.2.3-a..b", "1.2.3-.a", "1.2.3-a.",
            "1.2.3+build", "1.2.3-a+build", "1.2.3-a_b", "1.2.3-é", "１.2.3",
            "1.2.3-١", "1.2.3\n", " 1.2.3",
        ):
            with self.subTest(version=version):
                with self.assertRaises(VERIFIER.VerificationError):
                    VERIFIER.validate_package_version(version)

    def test_long_prerelease_with_invalid_suffix_is_rejected(self):
        # Bound a regression to the previous backtracking regex in a child process.
        program = """
import importlib.util
import sys
spec = importlib.util.spec_from_file_location("verifier", sys.argv[1])
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)
try:
    verifier.validate_package_version("0.0.0-0." + "--." * 20_000 + "!")
except verifier.VerificationError:
    pass
else:
    raise AssertionError("Malformed prerelease version was accepted")
verifier.validate_package_version("0.0.0-0." + "--." * 20_000 + "valid")
"""
        subprocess.run([sys.executable, "-c", program, str(SCRIPT)], check=True, timeout=5)


class DependencyPinTests(unittest.TestCase):
    def test_reads_semver_lower_bound_and_closed_exact_range(self):
        self.assertEqual("0.0.0-ci.local", VERIFIER.dependency_minimum_version("0.0.0-ci.local"))
        self.assertEqual("0.0.0-ci.local", VERIFIER.dependency_minimum_version("[0.0.0-ci.local]"))
        self.assertEqual("0.0.0-ci.local", VERIFIER.dependency_minimum_version("[0.0.0-ci.local, )"))

    def test_rejects_floating_or_exclusive_dependencies_as_version_pins(self):
        for value in ("(1.0.0, )", "1.*", "", "[1.0.0"):
            with self.subTest(value=value):
                self.assertEqual("", VERIFIER.dependency_minimum_version(value))
        self.assertEqual("1.0.0", VERIFIER.dependency_minimum_version("[1.0.0, 2.0.0)"))

    def test_nuspec_dependency_floor_must_match_candidate_version(self):
        with tempfile.TemporaryDirectory(prefix="docs-concept-package-") as directory:
            package = Path(directory) / "Docs.nupkg"
            nuspec = """<package><metadata><dependencies><group targetFramework="net10.0">
              <dependency id="ForgeTrust.AppSurface.ReleaseContracts" version="[0.0.0-ci.local]" />
            </group></dependencies></metadata></package>"""
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("Docs.nuspec", nuspec)
            VERIFIER.require_dependency_pin(
                package, "ForgeTrust.AppSurface.ReleaseContracts", "0.0.0-ci.local"
            )
            with self.assertRaises(VERIFIER.VerificationError):
                VERIFIER.require_dependency_pin(
                    package, "ForgeTrust.AppSurface.ReleaseContracts", "0.1.0"
                )


class RestoreEvidenceTests(unittest.TestCase):
    def test_assets_require_exact_package_identity_and_package_kind(self):
        with tempfile.TemporaryDirectory(prefix="docs-concept-assets-") as directory:
            assets = Path(directory) / "project.assets.json"
            assets.write_text(
                '{"libraries":{"ForgeTrust.AppSurface.ReleaseContracts/0.0.0-ci.local":{"type":"package"}}}',
                encoding="utf-8",
            )
            VERIFIER.require_assets_package(
                assets, "ForgeTrust.AppSurface.ReleaseContracts", "0.0.0-ci.local"
            )
            assets.write_text(
                '{"libraries":{"ForgeTrust.AppSurface.ReleaseContracts/0.0.0-ci.local":{"type":"project"}}}',
                encoding="utf-8",
            )
            with self.assertRaises(VERIFIER.VerificationError):
                VERIFIER.require_assets_package(
                    assets, "ForgeTrust.AppSurface.ReleaseContracts", "0.0.0-ci.local"
                )

    def test_locked_restore_rejects_mixed_version_or_duplicate_entries(self):
        with tempfile.TemporaryDirectory(prefix="docs-concept-lock-") as directory:
            lock = Path(directory) / "packages.lock.json"
            lock.write_text(
                '{"dependencies":{"net10.0":{"ForgeTrust.AppSurface.Docs":{"resolved":"0.0.0-ci.local"}}}}',
                encoding="utf-8",
            )
            VERIFIER.require_locked_package(lock, "ForgeTrust.AppSurface.Docs", "0.0.0-ci.local")
            lock.write_text(
                '{"dependencies":{"net10.0":{"ForgeTrust.AppSurface.Docs":{"resolved":"0.1.0"}}}}',
                encoding="utf-8",
            )
            with self.assertRaises(VERIFIER.VerificationError):
                VERIFIER.require_locked_package(lock, "ForgeTrust.AppSurface.Docs", "0.0.0-ci.local")

    def test_restored_package_must_be_the_exact_local_feed_archive(self):
        with tempfile.TemporaryDirectory(prefix="docs-concept-local-feed-") as directory:
            root = Path(directory)
            artifacts = root / "artifacts"
            cache = root / "cache"
            artifacts.mkdir()
            package_id = "ForgeTrust.AppSurface.ReleaseContracts"
            version = "0.0.0-ci.local"
            packed = artifacts / f"{package_id}.{version}.nupkg"
            packed.write_bytes(b"packed package bytes")
            cache_dir = cache / package_id.casefold() / version.casefold()
            cache_dir.mkdir(parents=True)
            restored = cache_dir / f"{package_id.casefold()}.{version.casefold()}.nupkg"
            restored.write_bytes(packed.read_bytes())
            (cache_dir / ".nupkg.metadata").write_text(
                json.dumps({"source": str(artifacts)}), encoding="utf-8"
            )

            VERIFIER.require_restored_local_artifact(artifacts, cache, package_id, version)

            restored.write_bytes(b"a different archive")
            with self.assertRaisesRegex(VERIFIER.VerificationError, "bytes differ"):
                VERIFIER.require_restored_local_artifact(artifacts, cache, package_id, version)

            restored.write_bytes(packed.read_bytes())
            (cache_dir / ".nupkg.metadata").write_text(
                json.dumps({"source": str(root / "other-feed")}), encoding="utf-8"
            )
            with self.assertRaisesRegex(VERIFIER.VerificationError, "fresh local feed"):
                VERIFIER.require_restored_local_artifact(artifacts, cache, package_id, version)


class ReleaseProjectGraphTests(unittest.TestCase):
    def test_traverses_transitive_references_and_rejects_web_project(self):
        with tempfile.TemporaryDirectory(prefix="docs-concept-project-graph-") as directory:
            root = Path(directory)
            release = root / "tools/ForgeTrust.AppSurface.Release/Release.csproj"
            console = root / "Console/ForgeTrust.AppSurface.Console/Console.csproj"
            web = root / "Web/ForgeTrust.AppSurface.Docs/Docs.csproj"
            for project in (release, console, web):
                project.parent.mkdir(parents=True, exist_ok=True)
                project.write_text("<Project />", encoding="utf-8")
            graph = {
                release.resolve(): [console.resolve()],
                console.resolve(): [],
                web.resolve(): [],
            }

            def read_references(project):
                return graph[project]

            self.assertEqual(
                [release.resolve(), console.resolve()],
                VERIFIER.inspect_release_project_graph(release, read_references, repository_root=root),
            )
            graph[console.resolve()] = [web.resolve()]
            with self.assertRaisesRegex(VERIFIER.VerificationError, "Web project"):
                VERIFIER.inspect_release_project_graph(release, read_references, repository_root=root)

    def test_rejects_reference_outside_the_repository(self):
        with tempfile.TemporaryDirectory(prefix="docs-concept-project-graph-") as directory:
            repository = Path(directory) / "repo"
            repository.mkdir()
            release = repository / "Release.csproj"
            release.write_text("<Project />", encoding="utf-8")
            external = Path(directory) / "external.csproj"
            external.write_text("<Project />", encoding="utf-8")
            with self.assertRaisesRegex(VERIFIER.VerificationError, "outside the repository"):
                VERIFIER.inspect_release_project_graph(release, lambda _: [external], repository_root=repository)


class BclOnlyCoreTests(unittest.TestCase):
    def test_compilation_project_has_no_package_references_and_includes_core_sources(self):
        with tempfile.TemporaryDirectory(prefix="docs-concept-bcl-core-") as directory:
            work_dir = Path(directory)
            with unittest.mock.patch.object(VERIFIER, "run_checked") as run_checked:
                VERIFIER.build_bcl_only_concept_core(work_dir, {})

            project = work_dir / "bcl-only-core/ConceptCore.BclOnly.csproj"
            xml = ET.parse(project).getroot()
            self.assertEqual([], list(xml.iter("PackageReference")))
            includes = {
                Path(item.attrib["Include"]).name
                for item in xml.iter("Compile")
            }
            core_sources = {
                source.name
                for source in VERIFIER.RELEASE_CONTRACTS_DIR.glob("DocConcept*.cs")
            }
            self.assertTrue(core_sources)
            self.assertTrue(core_sources.issubset(includes))
            self.assertIn("PackageReleaseLink.cs", includes)
            run_checked.assert_called_once()


class PackageArtifactRootTests(unittest.TestCase):
    def test_package_proof_evidence_is_confined_to_the_artifact_output(self):
        with tempfile.TemporaryDirectory(prefix="docs-concept-artifacts-") as directory:
            with unittest.mock.patch.object(VERIFIER, "run_checked") as run_checked:
                artifacts = VERIFIER.build_package_artifacts(Path(directory), "0.0.0-ci.local", {})

            command = run_checked.call_args.args[0]
            self.assertEqual(artifacts, Path(command[command.index("--artifacts-output") + 1]))
            for option in ("--coverage-proof-work-dir", "--coverage-proof-report",
                           "--docs-proof-work-dir", "--docs-proof-report"):
                with self.subTest(option=option):
                    self.assertTrue(Path(command[command.index(option) + 1]).is_relative_to(artifacts))


class ConsumerFixtureTests(unittest.TestCase):
    def test_fixture_consumes_exact_docs_package_without_source_project_references(self):
        project = ET.parse(VERIFIER.CONSUMER_FIXTURE / "DocsConceptConsumer.csproj").getroot()
        self.assertEqual([], list(project.iter("ProjectReference")))
        package_references = list(project.iter("PackageReference"))
        self.assertEqual(1, len(package_references))
        self.assertEqual("ForgeTrust.AppSurface.Docs", package_references[0].attrib["Include"])
        self.assertEqual("[$(AppSurfacePackageVersion)]", package_references[0].attrib["Version"])


if __name__ == "__main__":
    unittest.main()
