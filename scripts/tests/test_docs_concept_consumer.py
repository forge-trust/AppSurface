"""Unit tests for the packed Docs concept-consumer proof's evidence readers."""

import importlib.util
import json
from pathlib import Path
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
            external = Path(directory).parent / "external.csproj"
            external.write_text("<Project />", encoding="utf-8")
            with self.assertRaisesRegex(VERIFIER.VerificationError, "outside the repository"):
                VERIFIER.inspect_release_project_graph(release, lambda _: [external], repository_root=repository)
            external.unlink()


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
