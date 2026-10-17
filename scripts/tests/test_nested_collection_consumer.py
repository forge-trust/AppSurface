"""Regression tests for the package-consumer checks in the proof harness.

These tests execute the inline Python blocks from the shell script itself. The
fixtures stay synthetic, so the tests need neither a .NET build nor network
access.
"""

from __future__ import annotations

import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
HARNESS = REPO_ROOT / "scripts" / "verify-nested-collection-consumer.sh"
PACKAGE_VERSION = "0.1.0-issue868"

REWRITE_MARKER = (
    'python3 - "$host_dir/RazorWireWebExample.csproj" '
    '"$PACKAGE_VERSION" <<\'PY\''
)
ASSETS_MARKER = (
    'python3 - "$host_dir/obj/project.assets.json" '
    '"$PACKAGE_VERSION" "$RUN_DIR/nuget-packages" <<\'PY\''
)


def extract_inline_python(command_marker: str) -> str:
    """Return the Python body following an exact shell command marker."""
    lines = HARNESS.read_text(encoding="utf-8").splitlines()
    matches = [index for index, line in enumerate(lines) if command_marker in line]
    if len(matches) != 1:
        raise AssertionError(
            f"Expected one command marker {command_marker!r}; found {len(matches)}."
        )

    command_line = lines[matches[0]]
    delimiter_match = re.search(r"<<'([^']+)'\s*$", command_line)
    if delimiter_match is None:
        raise AssertionError(f"Command marker has no quoted heredoc: {command_line!r}")
    delimiter = delimiter_match.group(1)

    body = []
    for line in lines[matches[0] + 1 :]:
        if line == delimiter:
            return "\n".join(body) + "\n"
        body.append(line)
    raise AssertionError(f"Heredoc delimiter {delimiter!r} was not found.")


class NestedCollectionConsumerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.rewrite_script = extract_inline_python(REWRITE_MARKER)
        cls.assets_script = extract_inline_python(ASSETS_MARKER)

    def run_inline(self, script: str, *arguments: str) -> subprocess.CompletedProcess[str]:
        # Deliberately expose no system commands (including rg) to these blocks.
        with tempfile.TemporaryDirectory() as empty_path:
            environment = os.environ.copy()
            environment["PATH"] = empty_path
            return subprocess.run(
                [sys.executable, "-c", script, *arguments],
                check=False,
                capture_output=True,
                text=True,
                env=environment,
            )

    @staticmethod
    def write_project(directory: pathlib.Path, *, extra_reference: bool = False,
                      include_razorwire: bool = True, include_import: bool = True,
                      malformed_xml: bool = False) -> pathlib.Path:
        project_path = directory / "RazorWireWebExample.csproj"
        references = []
        if include_razorwire:
            references.append(
                '    <ProjectReference Include="../../Web/ForgeTrust.RazorWire/ForgeTrust.RazorWire.csproj" />'
            )
        references.append(
            '    <ProjectReference Include="../../Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj" />'
        )
        if extra_reference:
            references.append(
                '    <ProjectReference Include="../../Other/Additional.Source.csproj" />'
            )
        if include_import:
            references.append(
                '  <Import Project="..\\..\\Web\\ForgeTrust.AppSurface.Web.Tailwind\\build\\ForgeTrust.AppSurface.Web.Tailwind.targets" />'
            )
        project_text = "\n".join(
            [
                '<Project Sdk="Microsoft.NET.Sdk.Web">',
                "  <PropertyGroup>",
                "    <ImplicitUsings>enable</ImplicitUsings>",
                "  </PropertyGroup>",
                "  <ItemGroup>",
                *references,
                "  </ItemGroup>",
                "</Project>",
                "",
            ]
        )
        if malformed_xml:
            project_text = project_text.replace("</Project>", "</ProjectBroken>")
        project_path.write_text(project_text, encoding="utf-8")
        return project_path

    def run_rewrite(self, project_path: pathlib.Path) -> subprocess.CompletedProcess[str]:
        return self.run_inline(self.rewrite_script, str(project_path), PACKAGE_VERSION)

    def test_rewrite_pins_both_packages_and_removes_source_imports(self) -> None:
        with tempfile.TemporaryDirectory() as directory_name:
            project_path = self.write_project(pathlib.Path(directory_name))
            result = self.run_rewrite(project_path)

            self.assertEqual(result.returncode, 0, result.stderr)
            rewritten = project_path.read_text(encoding="utf-8")
            root = __import__("xml.etree.ElementTree", fromlist=["ElementTree"]).parse(
                project_path
            ).getroot()
            local_name = lambda element: element.tag.rsplit("}", 1)[-1]
            project_references = [
                element for element in root.iter() if local_name(element) == "ProjectReference"
            ]
            package_references = {
                element.get("Include"): element.get("Version")
                for element in root.iter()
                if local_name(element) == "PackageReference"
            }
            imports = [element for element in root.iter() if local_name(element) == "Import"]

            self.assertEqual(project_references, [])
            self.assertEqual(
                package_references,
                {
                    "ForgeTrust.RazorWire": f"[{PACKAGE_VERSION}]",
                    "ForgeTrust.AppSurface.Web.Tailwind": f"[{PACKAGE_VERSION}]",
                },
            )
            self.assertEqual(imports, [])
            self.assertIn("<IsExampleProject>true</IsExampleProject>", rewritten)

    def test_rewrite_rejects_malformed_project_xml_without_writing(self) -> None:
        with tempfile.TemporaryDirectory() as directory_name:
            project_path = self.write_project(pathlib.Path(directory_name), malformed_xml=True)
            original = project_path.read_text(encoding="utf-8")
            result = self.run_rewrite(project_path)

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("ParseError", result.stderr)
            self.assertEqual(project_path.read_text(encoding="utf-8"), original)

    def test_rewrite_rejects_an_additional_source_project_reference(self) -> None:
        with tempfile.TemporaryDirectory() as directory_name:
            project_path = self.write_project(pathlib.Path(directory_name), extra_reference=True)
            original = project_path.read_text(encoding="utf-8")
            result = self.run_rewrite(project_path)

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("still contains a source ProjectReference", result.stderr)
            self.assertEqual(project_path.read_text(encoding="utf-8"), original)

    def test_rewrite_rejects_a_missing_required_package_source_reference(self) -> None:
        with tempfile.TemporaryDirectory() as directory_name:
            project_path = self.write_project(
                pathlib.Path(directory_name), include_razorwire=False
            )
            result = self.run_rewrite(project_path)

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("exactly one RazorWire ProjectReference", result.stderr)

    def test_rewrite_rejects_a_missing_tailwind_targets_import(self) -> None:
        with tempfile.TemporaryDirectory() as directory_name:
            project_path = self.write_project(
                pathlib.Path(directory_name), include_import=False
            )
            result = self.run_rewrite(project_path)

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("exactly one Tailwind targets import", result.stderr)

    @staticmethod
    def valid_assets() -> dict[str, object]:
        return {
            "libraries": {
                f"ForgeTrust.RazorWire/{PACKAGE_VERSION}": {
                    "type": "package",
                    "path": f"forgetrust.razorwire/{PACKAGE_VERSION.lower()}",
                },
                f"ForgeTrust.AppSurface.Web.Tailwind/{PACKAGE_VERSION}": {
                    "type": "package",
                    "path": f"forgetrust.appsurface.web.tailwind/{PACKAGE_VERSION.lower()}",
                },
            },
            "project": {
                "frameworks": {
                    "net10.0": {
                        "dependencies": {
                            "ForgeTrust.RazorWire": {"version": f"[{PACKAGE_VERSION}]"},
                            "ForgeTrust.AppSurface.Web.Tailwind": {"version": f"[{PACKAGE_VERSION}]"},
                        }
                    }
                }
            },
        }

    def run_assets_guard(
        self,
        assets: dict[str, object],
        *,
        cache_entry: bool = True,
        metadata: bool = True,
        missing_cache_packages: tuple[str, ...] = (),
        missing_metadata_packages: tuple[str, ...] = (),
    ) -> subprocess.CompletedProcess[str]:
        with tempfile.TemporaryDirectory() as directory_name:
            root = pathlib.Path(directory_name)
            assets_path = root / "project.assets.json"
            assets_path.write_text(json.dumps(assets), encoding="utf-8")
            cache_path = root / "nuget-packages"
            package_ids = (
                "ForgeTrust.RazorWire",
                "ForgeTrust.AppSurface.Web.Tailwind",
            )
            for package_id in package_ids:
                normalized_id = package_id.lower()
                if not cache_entry or package_id in missing_cache_packages:
                    continue
                package_cache = cache_path / normalized_id / PACKAGE_VERSION.lower()
                package_cache.mkdir(parents=True)
                if metadata and package_id not in missing_metadata_packages:
                    (package_cache / ".nupkg.metadata").write_text("{}", encoding="utf-8")
            return self.run_inline(
                self.assets_script,
                str(assets_path),
                PACKAGE_VERSION,
                str(cache_path),
            )

    def test_asset_guard_accepts_exact_package_from_private_cache(self) -> None:
        result = self.run_assets_guard(self.valid_assets())
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_asset_guard_rejects_a_forgetrust_project_library(self) -> None:
        assets = self.valid_assets()
        assets["libraries"]["ForgeTrust.AppSurface.Core/1.2.3"] = {  # type: ignore[index]
            "type": "project",
            "path": "../../ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj",
        }
        result = self.run_assets_guard(assets)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Source-project substitution", result.stderr)

    def test_asset_guard_rejects_an_additional_razorwire_version(self) -> None:
        assets = self.valid_assets()
        assets["libraries"]["ForgeTrust.RazorWire/9.9.9"] = {  # type: ignore[index]
            "type": "package",
            "path": "forgetrust.razorwire/9.9.9",
        }
        result = self.run_assets_guard(assets)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("unexpected RazorWire package version", result.stderr)

    def test_asset_guard_rejects_a_missing_exact_package(self) -> None:
        assets = self.valid_assets()
        del assets["libraries"][f"ForgeTrust.RazorWire/{PACKAGE_VERSION}"]  # type: ignore[index]
        result = self.run_assets_guard(assets)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("do not contain the exact package", result.stderr)

    def test_asset_guard_rejects_a_wrong_package_path(self) -> None:
        assets = self.valid_assets()
        assets["libraries"][f"ForgeTrust.RazorWire/{PACKAGE_VERSION}"]["path"] = (  # type: ignore[index]
            "forgetrust.razorwire/another-version"
        )
        result = self.run_assets_guard(assets)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Expected package path", result.stderr)

    def test_asset_guard_rejects_non_exact_dependency_ranges(self) -> None:
        for package_id in (
            "ForgeTrust.RazorWire",
            "ForgeTrust.AppSurface.Web.Tailwind",
        ):
            for dependency_range in (
                PACKAGE_VERSION,
                f"[{PACKAGE_VERSION},)",
                f"(,{PACKAGE_VERSION}]",
            ):
                with self.subTest(package_id=package_id, dependency_range=dependency_range):
                    assets = self.valid_assets()
                    assets["project"]["frameworks"]["net10.0"]["dependencies"][  # type: ignore[index]
                        package_id
                    ]["version"] = dependency_range  # type: ignore[index]
                    result = self.run_assets_guard(assets)

                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn(f"does not pin {package_id} exactly", result.stderr)

    def test_asset_guard_rejects_a_missing_exact_tailwind_package(self) -> None:
        assets = self.valid_assets()
        del assets["libraries"][f"ForgeTrust.AppSurface.Web.Tailwind/{PACKAGE_VERSION}"]  # type: ignore[index]
        result = self.run_assets_guard(assets)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("do not contain the exact package ForgeTrust.AppSurface.Web.Tailwind", result.stderr)

    def test_asset_guard_rejects_an_additional_tailwind_package_version(self) -> None:
        assets = self.valid_assets()
        assets["libraries"]["ForgeTrust.AppSurface.Web.Tailwind/9.9.9"] = {  # type: ignore[index]
            "type": "package",
            "path": "forgetrust.appsurface.web.tailwind/9.9.9",
        }
        result = self.run_assets_guard(assets)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("unexpected ForgeTrust.AppSurface.Web.Tailwind package version", result.stderr)

    def test_asset_guard_rejects_a_tailwind_project_library(self) -> None:
        assets = self.valid_assets()
        assets["libraries"][f"ForgeTrust.AppSurface.Web.Tailwind/{PACKAGE_VERSION}"] = {  # type: ignore[index]
            "type": "project",
            "path": "../../Web/ForgeTrust.AppSurface.Web.Tailwind/ForgeTrust.AppSurface.Web.Tailwind.csproj",
        }
        result = self.run_assets_guard(assets)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Source-project substitution", result.stderr)

    def test_asset_guard_rejects_a_wrong_tailwind_package_path(self) -> None:
        assets = self.valid_assets()
        assets["libraries"][f"ForgeTrust.AppSurface.Web.Tailwind/{PACKAGE_VERSION}"]["path"] = (  # type: ignore[index]
            "forgetrust.appsurface.web.tailwind/another-version"
        )
        result = self.run_assets_guard(assets)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Expected package path forgetrust.appsurface.web.tailwind", result.stderr)

    def test_asset_guard_rejects_missing_exact_dependency_for_each_package(self) -> None:
        for package_id in (
            "ForgeTrust.RazorWire",
            "ForgeTrust.AppSurface.Web.Tailwind",
        ):
            with self.subTest(package_id=package_id):
                assets = self.valid_assets()
                del assets["project"]["frameworks"]["net10.0"]["dependencies"][package_id]  # type: ignore[index]
                result = self.run_assets_guard(assets)

                self.assertNotEqual(result.returncode, 0)
                self.assertIn(f"does not pin {package_id} exactly", result.stderr)

    def test_asset_guard_rejects_missing_tailwind_private_cache_entry_or_metadata(self) -> None:
        cases = (
            ({"ForgeTrust.AppSurface.Web.Tailwind"}, set()),
            (set(), {"ForgeTrust.AppSurface.Web.Tailwind"}),
        )
        for missing_cache, missing_metadata in cases:
            with self.subTest(missing_cache=missing_cache, missing_metadata=missing_metadata):
                result = self.run_assets_guard(
                    self.valid_assets(),
                    missing_cache_packages=tuple(missing_cache),
                    missing_metadata_packages=tuple(missing_metadata),
                )

                self.assertNotEqual(result.returncode, 0)
                self.assertIn(
                    f"forgetrust.appsurface.web.tailwind/{PACKAGE_VERSION.lower()}",
                    result.stderr,
                )

    def test_asset_guard_rejects_missing_private_cache_entry_or_metadata(self) -> None:
        cases = (
            (False, False, "Exact package was not restored into this run's private NuGet cache"),
            (True, False, "Exact package was not restored into this run's private NuGet cache"),
        )
        for cache_entry, metadata, error in cases:
            with self.subTest(cache_entry=cache_entry, metadata=metadata):
                result = self.run_assets_guard(
                    self.valid_assets(), cache_entry=cache_entry, metadata=metadata
                )

                self.assertNotEqual(result.returncode, 0)
                self.assertIn(error, result.stderr)


if __name__ == "__main__":
    unittest.main()
