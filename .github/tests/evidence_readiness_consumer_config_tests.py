#!/usr/bin/env python3
"""Verify generated NuGet source attributes preserve caller-selected local paths.

Run: python3 .github/tests/evidence_readiness_consumer_config_tests.py
Only the actual configuration-writing shell block runs; no restore or network.
"""

from pathlib import Path
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().parents[2] / "scripts/verify-evidence-readiness-consumers.sh"


class ConsumerConfigTests(unittest.TestCase):
    # Value: protects=NuGet source paths round-trip through generated XML;
    # fails_when=raw XML-special characters enter attributes; why_new=packed
    # consumer proof used ordinary paths; seam=actual configuration shell block;
    # no_claim=does not execute restore, pack, or consumer programs.
    def test_sources_round_trip_work_directory_paths(self) -> None:
        source = SCRIPT.read_text()
        start = source.index("# NuGet source attributes") if "# NuGet source attributes" in source else source.index("producer_config=")
        block = source[start:source.index("export NUGET_PACKAGES=")]
        for name in ("ordinary", "space & proof", 'xml < > " apostrophe \' café', "attribute\twhitespace\r\npath"):
            with self.subTest(path=name), tempfile.TemporaryDirectory() as temporary:
                run_root = Path(temporary) / name
                run_root.mkdir()
                feed = run_root / "feed"
                feed.mkdir()
                subprocess.run(
                    ["bash", "-c", 'run_root=$1; feed=$2\n' + block, "_", str(run_root), str(feed)],
                    check=True, capture_output=True, text=True,
                )
                for kind in ("producer", "neutral", "typed"):
                    with self.subTest(config=kind):
                        root = ET.parse(run_root / f"nuget-{kind}.config").getroot()
                        sources = root.find("packageSources")
                        self.assertIsNotNone(sources)
                        self.assertIsNotNone(sources.find("clear"))
                        entries = {item.attrib["key"]: item.attrib["value"] for item in sources.findall("add")}
                        expected = {} if kind == "producer" else {"issue867-local-feed": str(feed)}
                        if kind != "neutral":
                            expected["nuget.org"] = "https://api.nuget.org/v3/index.json"
                        self.assertEqual(expected, entries)


if __name__ == "__main__":
    unittest.main()
