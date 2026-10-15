"""Pure data-only controls for the closed workspace materializer CLI."""
import importlib.machinery
import importlib.util
import hashlib
import json
from pathlib import Path
import unittest

SCRIPT = Path(__file__).with_name("prepare-case-workspace.py")
loader = importlib.machinery.SourceFileLoader("workspace_materializer", str(SCRIPT))
spec = importlib.util.spec_from_loader(loader.name, loader)
workspace = importlib.util.module_from_spec(spec)
loader.exec_module(workspace)

RUNNER_PATH = Path(__file__).with_name('run-native.py')
runner_loader = importlib.machinery.SourceFileLoader('owner_death_runner_markers', str(RUNNER_PATH))
runner_spec = importlib.util.spec_from_loader(runner_loader.name, runner_loader)
runner_module = importlib.util.module_from_spec(runner_spec)
runner_loader.exec_module(runner_module)

BASE = [
    "--generation", "0123456789abcdef0123456789abcdef", "--case", "N13",
    "--source-root", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/source",
    "--source-map", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/control/source.tsv",
    "--source-map-sha256", "0" * 64,
    "--source-nodes", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/control/source.json",
    "--source-nodes-sha256", "0" * 64,
    "--tool-root", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/tool",
    "--tool-map", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/control/tool.tsv",
    "--tool-map-sha256", "0" * 64,
    "--tool-nodes", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/control/tool.json",
    "--tool-nodes-sha256", "0" * 64,
    "--runtime-root", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/runtime",
    "--runtime-map", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/control/runtime.tsv",
    "--runtime-map-sha256", "0" * 64,
    "--runtime-nodes", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/control/runtime.json",
    "--runtime-nodes-sha256", "0" * 64,
    "--build-receipt", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/control/build.json",
    "--build-receipt-sha256", "0" * 64,
    "--source-capture", "/var/lib/appsurface-evidence-input-0123456789abcdef0123456789abcdef/control/capture.json",
    "--source-capture-sha256", "0" * 64,
    "--job-deadline-utc", "2030-01-01T00:04:00Z",
    "--job-deadline-boottime-ms", "400000", "--workflow-identity", "github-actions-123456-1"
]

class CommonCaptureEncoderTests(unittest.TestCase):
    def test_helper_projection_matches_current_common_capture_encoder(self):
        capture_path = Path(__file__).parents[1] / 'issue779-csharp-build' / 'capture-receipt.json'
        raw = capture_path.read_bytes()
        capture = json.loads(raw)
        source = capture['source']
        compact = hashlib.sha256(json.dumps(source, sort_keys=True, separators=(',', ':')).encode()).hexdigest()
        self.assertEqual(compact, runner_module.CANONICAL_SOURCE_MAP)
        self.assertEqual(workspace.source_projection_sha(capture, 'tree'), '57f62b362fd89e1935dabd7bf1cd11f62647855c303d4f98ca26dcebfd5e2dc5')
        self.assertEqual(hashlib.sha256(raw).hexdigest(), runner_module.CAPTURE_SHA)


class WorkspaceMarkerTests(unittest.TestCase):
    def test_exact_shared_materializer_markers(self):
        generation = '0123456789abcdef0123456789abcdef'
        good = ('CASE_WORKSPACE_STAGED:N13:%s:REQUEST_AND_POLICY_PINNED:NO_OWNER_LAUNCH:NO_NATIVE_CREDIT\n' % generation).encode()
        self.assertTrue(runner_module.parse_workspace_stage_marker(good, 'N13', generation))
        self.assertEqual(runner_module.parse_workspace_hash_marker(b'CASE_WORKSPACE_HASHES:' + b'a'*64 + b':' + b'b'*64 + b'\n'), ('a'*64, 'b'*64))
        for bad in (good.replace(b'N13', b'N14'), good.replace(b':REQUEST_AND_POLICY_PINNED', b':a'*64), good+b'EXTRA\n'):
            with self.subTest(bad=bad), self.assertRaises(runner_module.Rejected):
                runner_module.parse_workspace_stage_marker(bad, 'N13', generation)
        with self.assertRaises(runner_module.Rejected):
            runner_module.parse_workspace_hash_marker(b'CASE_WORKSPACE_HASHES:' + b'a'*64 + b':' + b'b'*64)
        helper_text = SCRIPT.read_text()
        self.assertIn('CASE_WORKSPACE_STAGED:%s:%s:REQUEST_AND_POLICY_PINNED:NO_OWNER_LAUNCH:NO_NATIVE_CREDIT', helper_text)
        compile(runner_module.WORKSPACE_HASH_READ, '<workspace-hash-reader>', 'exec')


class WorkspaceCallerCompositionTests(unittest.TestCase):
    def test_materialization_is_pinned_and_precedes_owner_dispatch(self):
        runner = (Path(__file__).with_name("run-native.py")).read_text()
        fixture = (Path(__file__).with_name("checkpoint-owner-death-common.sh")).read_text()
        call = runner.index("runner.phase='case-workspace-materialization'")
        dispatch = runner.index("result['native_dispatched']=True; runner.phase='actual-owner-death-'", call)
        self.assertLess(call, dispatch)
        self.assertIn("helper_path=bootstrap/'prepare-case-workspace.py'", runner)
        self.assertIn("'--case',args.case", runner)
        self.assertIn("REQUEST_AND_POLICY_PINNED:NO_OWNER_LAUNCH:NO_NATIVE_CREDIT", runner)
        self.assertIn("CASE_WORKSPACE_HASHES:", runner)
        self.assertIn("case-workspace-hash-binding", runner)
        self.assertLess(runner.index("case-workspace-hash-binding"), dispatch)
        for operand in ("--source-root", "--source-map", "--source-nodes", "--tool-root",
                        "--tool-map", "--tool-nodes", "--runtime-root", "--runtime-map",
                        "--runtime-nodes", "--build-receipt", "--source-capture",
                        "--job-deadline-utc", "--job-deadline-boottime-ms", "--workflow-identity"):
            self.assertIn("'" + operand + "'", runner)
        self.assertIn("'--build-receipt-sha256',build_sha", runner)
        self.assertIn("'--source-capture-sha256',CAPTURE_SHA", runner)
        self.assertIn("NO_OWNER_LAUNCH:NO_NATIVE_CREDIT", runner)
        self.assertIn("workspace-materialization-binding", fixture)
        self.assertIn("%u:%g:%a", fixture)
        self.assertIn("0:0:555", fixture)
        self.assertIn("0:0:700", fixture)
        self.assertNotIn("install -d -o 0 -g 0 -m 700 \"$stage\"", fixture)

class WorkspaceArgumentTests(unittest.TestCase):
    def test_closed_variants(self):
        for case in ("N13", "N14", "N15"):
            self.assertEqual(workspace.validate_case(case), case)
        with self.assertRaises(workspace.Reject):
            workspace.validate_case("N16")
        self.assertEqual(workspace.validate_workflow_identity("github-actions-123456-1"),
                         "github-actions-123456-1")
        with self.assertRaises(workspace.Reject):
            workspace.validate_workflow_identity("arbitrary workflow")

    def test_duplicate_is_rejected_even_with_equals_form(self):
        args = BASE + ["--case=N14"]
        with self.assertRaises(workspace.Reject):
            workspace.parse_args(args)

    def test_abbreviation_and_unknown_options_are_rejected(self):
        args = ["--gener", "0123456789abcdef0123456789abcdef"] + BASE[2:]
        with self.assertRaises(workspace.Reject):
            workspace.parse_args(args)
        args = BASE + ["--caller-command", "echo"]
        with self.assertRaises(workspace.Reject):
            workspace.parse_args(args)

    def test_original_deadline_is_capped_without_reset(self):
        end = workspace.calculate_work_deadline(1000, 100000, 1240, 340000)
        self.assertEqual(end, 280000)
        self.assertLessEqual(end, 340000 - 60000)
        with self.assertRaises(workspace.Reject):
            workspace.calculate_work_deadline(1000, 100000, 1240, 400001)
        with self.assertRaises(workspace.Reject):
            workspace.calculate_work_deadline(1000, 100000, 1060, 160000)

if __name__ == "__main__":
    unittest.main()
