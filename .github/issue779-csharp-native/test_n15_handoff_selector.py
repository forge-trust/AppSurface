"""Data-only regressions for the frozen N15 dispatch-marker selector."""
import importlib.util
from pathlib import Path
import unittest

RUNNER_PATH = Path(__file__).with_name("run-native.py")
RUNNER_SPEC = importlib.util.spec_from_file_location("issue779_n15_runner_data", RUNNER_PATH)
runner = importlib.util.module_from_spec(RUNNER_SPEC)
RUNNER_SPEC.loader.exec_module(runner)

MARKER = b"N15_CSHARP_OWNER_DISPATCHED\n"
FIXTURE_LOG = MARKER + b"N15_OWNER_LAUNCH_JOIN_MEASURED:launch=0:observer=0:stdout=0:phase_stderr=1\n"
DISPATCH = ["root-fixture-dispatch"]


class DispatchFailure(RuntimeError):
    pass


class RetentionFailure(RuntimeError):
    pass


class FakeRunner:
    def __init__(self, fail_dispatch=False, failure=None, dispatch_exit=0):
        self.records = []
        self.fail_dispatch = fail_dispatch
        self.failure = failure
        self.dispatch_exit = dispatch_exit

    def run(self, argv, deadline, allow_failure=False):
        row = {"ordinal": len(self.records), "phase": "actual-csharp-n15-owner-launch-observe-and-replay",
               "argv": argv, "exit": None}
        self.records.append(row)
        if argv == DISPATCH and self.fail_dispatch:
            raise self.failure
        row["exit"] = self.dispatch_exit if argv == DISPATCH else 0
        return row["exit"], b""


def collect_with_saved_stdout(test_runner, fixture_stdout):
    def collect():
        for name in ("retention", "archive-sha-before", "archive-read", "archive-sha-after"):
            test_runner.records.append({"ordinal": len(test_runner.records), "phase": "bounded-private-retention",
                                        "argv": [name], "exit": 0})
        return {"fixture-stdout.log": fixture_stdout}, {"retained": True}
    return collect


class N15HandoffSelectorControls(unittest.TestCase):
    def test_success_uses_saved_fixture_stdout_after_unrelated_last_query(self):
        test_runner = FakeRunner()
        result = {}
        index = len(test_runner.records)
        values = runner.run_fixture_with_retention(
            test_runner, DISPATCH, 10.0, collect_with_saved_stdout(test_runner, FIXTURE_LOG), result, index)
        self.assertEqual(test_runner.records[-1]["argv"], ["archive-sha-after"])
        self.assertIs(values["fixture-stdout.log"], FIXTURE_LOG)
        self.assertIs(result["native_dispatched"], True)
        self.assertEqual(result["fixture_exit"], 0)

    def test_nonzero_dispatch_retains_marker_and_original_failure(self):
        test_runner = FakeRunner(dispatch_exit=7)
        result = {}
        index = len(test_runner.records)
        with self.assertRaises(runner.Rejected) as caught:
            runner.run_fixture_with_retention(
                test_runner, DISPATCH, 10.0, collect_with_saved_stdout(test_runner, MARKER), result, index)
        self.assertEqual(caught.exception.category, "native-fixture-wrapper-nonzero")
        self.assertEqual(result["fixture_exit"], 7)
        self.assertIs(result["native_dispatched"], True)

    def test_no_marker_leaves_dispatch_false(self):
        test_runner = FakeRunner()
        result = {}
        index = len(test_runner.records)
        runner.run_fixture_with_retention(
            test_runner, DISPATCH, 10.0,
            collect_with_saved_stdout(test_runner, b"setup completed; no owner launch\n"), result, index)
        self.assertIs(result["native_dispatched"], False)

    def test_missing_malformed_or_oversized_saved_log_stays_false(self):
        bad_logs = (None, b"", MARKER[:-1], b"N15_CSHARP_OWNER_DISPATCHED\r\n",
                    b"prefix N15_CSHARP_OWNER_DISPATCHED\n",
                    "N15_CSHARP_OWNER_DISPATCHED\n", b"x" * (runner.CAPS["fixture-stdout.log"] + 1))
        for saved in bad_logs:
            with self.subTest(saved_type=type(saved).__name__, size=len(saved) if hasattr(saved, "__len__") else None):
                test_runner = FakeRunner()
                result = {}
                index = len(test_runner.records)
                runner.run_fixture_with_retention(
                    test_runner, DISPATCH, 10.0, collect_with_saved_stdout(test_runner, saved), result, index)
                self.assertIs(result["native_dispatched"], False)

    def test_bad_dispatch_index_does_not_fall_back_to_last_record(self):
        test_runner = FakeRunner()
        result = {}
        runner.run_fixture_with_retention(
            test_runner, DISPATCH, 10.0, collect_with_saved_stdout(test_runner, MARKER), result, 9)
        self.assertIs(result["native_dispatched"], False)

    def test_original_dispatch_failure_wins_if_retention_also_fails(self):
        original = DispatchFailure("original fixture failure")
        retention = RetentionFailure("archive query failed")
        test_runner = FakeRunner(fail_dispatch=True, failure=original)

        def collect():
            test_runner.records.append({"ordinal": len(test_runner.records), "phase": "bounded-private-retention",
                                        "argv": ["failed-archive-query"], "exit": 1})
            raise retention

        result = {}
        index = len(test_runner.records)
        with self.assertRaises(DispatchFailure) as caught:
            runner.run_fixture_with_retention(test_runner, DISPATCH, 10.0, collect, result, index)
        self.assertIs(caught.exception, original)
        self.assertIs(result["native_dispatched"], False)
        self.assertEqual(result["retention_failure"]["error_class"], "RetentionFailure")


CAPTURED_BUILD_COMMANDS = '[{"argv":["git","rev-parse","HEAD"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"00-stdout.log","sha256":"2648fe668f080e9271e147768ede43e2a31f5cf6865389bc3f83a36ff7659649"},{"bytes":0,"name":"00-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":0,"phase":"source","pid":3238,"timed_out":false,"waited":true},{"argv":["git","show","-s","--format=%P","HEAD"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"01-stdout.log","sha256":"4dab81d018e34d509093109a4b7b66d1fc24bd10f26d14b4ea408ae5406fbbfe"},{"bytes":0,"name":"01-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":1,"phase":"source","pid":3239,"timed_out":false,"waited":true},{"argv":["git","show","-s","--format=%P","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"02-stdout.log","sha256":"adb2a93d8e84bde69b0a3b9a50567653111ca472316ed3ca269a2ed0b23294b0"},{"bytes":0,"name":"02-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":2,"phase":"source","pid":3240,"timed_out":false,"waited":true},{"argv":["git","diff","--name-status","a6933fa3dc2c7a88832abf337447f88527477703","HEAD"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":1183,"name":"03-stdout.log","sha256":"3fd29817221563e20f4085d7d50c4a915135223fb8b5efff4a510cfc793bbeaa"},{"bytes":0,"name":"03-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":3,"phase":"source","pid":3241,"timed_out":false,"waited":true},{"argv":["git","ls-files","--stage","-z"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":339128,"name":"04-stdout.log","sha256":"3791eceab2bbbec789504d7bae72e2af192e871f4ceac5f3800364466dc15f01"},{"bytes":0,"name":"04-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":4,"phase":"source","pid":3242,"timed_out":false,"waited":true},{"argv":["git","ls-tree","-rz","HEAD"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":347798,"name":"05-stdout.log","sha256":"bc855c5db0d726863ecf407e230c2fe9396ac4716e9081e9c60694e30d6b8206"},{"bytes":0,"name":"05-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":5,"phase":"source","pid":3243,"timed_out":false,"waited":true},{"argv":["git","merge-base","--is-ancestor","a6933fa3dc2c7a88832abf337447f88527477703","HEAD"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":0,"name":"06-stdout.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},{"bytes":0,"name":"06-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":6,"phase":"source","pid":3244,"timed_out":false,"waited":true},{"argv":["git","clone","--no-hardlinks","--no-checkout","/home/runner/work/AppSurface/AppSurface","/home/runner/work/_temp/issue779-csharp-n15-build/compile"],"child_umask":18,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":0,"name":"07-stdout.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},{"bytes":82,"name":"07-stderr.log","sha256":"8b498215183b4d339b96035cc279005ec74b2b6ee4df577b0c6b9e3ffed2aeea"}],"ordinal":7,"phase":"source","pid":3245,"timed_out":false,"waited":true},{"argv":["git","checkout","--detach","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":18,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":0,"name":"08-stdout.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},{"bytes":87,"name":"08-stderr.log","sha256":"ecba646f73c56f412ee7f54c24274812a1ac45b513cb20179c85611f49888732"}],"ordinal":8,"phase":"source","pid":3248,"timed_out":false,"waited":true},{"argv":["git","ls-files","--stage","-z"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":336937,"name":"09-stdout.log","sha256":"89674e117f16e405c78420c09e440e2420749f6fc2d10aaa2bcdb9ef6e2dacdc"},{"bytes":0,"name":"09-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":9,"phase":"source","pid":3249,"timed_out":false,"waited":true},{"argv":["git","ls-tree","-rz","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":345544,"name":"10-stdout.log","sha256":"18cdeb2f99318945258280d2a2920acc9e90e063915abacd3b3fff8283893bf8"},{"bytes":0,"name":"10-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":10,"phase":"source","pid":3250,"timed_out":false,"waited":true},{"argv":["git","rev-parse","HEAD"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"11-stdout.log","sha256":"4dab81d018e34d509093109a4b7b66d1fc24bd10f26d14b4ea408ae5406fbbfe"},{"bytes":0,"name":"11-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":11,"phase":"source","pid":3251,"timed_out":false,"waited":true},{"argv":["git","rev-parse","HEAD^{tree}"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"12-stdout.log","sha256":"707f20829b1729c93b46b6ec3b8c7435d3259ff242e9a878ee58d010dba2aae9"},{"bytes":0,"name":"12-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":12,"phase":"source","pid":3252,"timed_out":false,"waited":true},{"argv":["git","show","-s","--format=%P","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"13-stdout.log","sha256":"adb2a93d8e84bde69b0a3b9a50567653111ca472316ed3ca269a2ed0b23294b0"},{"bytes":0,"name":"13-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":13,"phase":"source","pid":3253,"timed_out":false,"waited":true},{"argv":["git","status","--porcelain=v1","--untracked-files=all"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":0,"name":"14-stdout.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},{"bytes":0,"name":"14-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":14,"phase":"source","pid":3254,"timed_out":false,"waited":true},{"argv":["git","archive","--format=tar","--output=/home/runner/work/_temp/issue779-csharp-n15-build/source.tar","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":0,"name":"15-stdout.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},{"bytes":0,"name":"15-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":15,"phase":"source","pid":3260,"timed_out":false,"waited":true},{"argv":["/usr/share/dotnet/dotnet","--version"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":9,"name":"16-stdout.log","sha256":"93c11b5221b72e0032ab2d9f93d28c2c9b7a0bb2557effffcdfef45ab739a0da"},{"bytes":0,"name":"16-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":16,"phase":"sdk-assets","pid":3262,"timed_out":false,"waited":true},{"argv":["/usr/share/dotnet/dotnet","--info"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":2844,"name":"17-stdout.log","sha256":"e4f468a5c6b7a77da528730bf18adcab2c22b0bfabf579bdbae6cfb3f5c9e562"},{"bytes":0,"name":"17-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":17,"phase":"sdk-assets","pid":3270,"timed_out":false,"waited":true},{"argv":["/usr/share/dotnet/dotnet","--list-runtimes"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":1296,"name":"18-stdout.log","sha256":"559c58b293a9896d17475fdc396b9a9632b0126bffcca39415fa27efd8c5e904"},{"bytes":0,"name":"18-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":18,"phase":"sdk-assets","pid":3278,"timed_out":false,"waited":true},{"argv":["pnpm","--version"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":7,"name":"19-stdout.log","sha256":"3948a9c905c8968e4141cd3316099acbc6e598b1b97440c46d85647983ffbb41"},{"bytes":0,"name":"19-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":19,"phase":"sdk-assets","pid":3279,"timed_out":false,"waited":true},{"argv":["node","--version"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":9,"name":"20-stdout.log","sha256":"f2ec568164f99546161909b0189b57bb5be3e80f8e2ccd8a51a4aed8c4055a04"},{"bytes":0,"name":"20-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":20,"phase":"sdk-assets","pid":3296,"timed_out":false,"waited":true},{"argv":["pnpm","--dir","Web","install","--frozen-lockfile"],"child_umask":18,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":595,"name":"21-stdout.log","sha256":"8ed659904fb20b21ab650ee03d30aac11ffbc2ae5aea50fc574bd47ca59aa1fa"},{"bytes":0,"name":"21-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":21,"phase":"sdk-assets","pid":3297,"timed_out":false,"waited":true},{"argv":["pnpm","--dir","Web","run","assets:build"],"child_umask":18,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":0,"name":"22-stdout.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},{"bytes":176,"name":"22-stderr.log","sha256":"7a85218570426006eea5e94eb79f30968dbfe43b2d5798a7f42528dfbd106732"}],"ordinal":22,"phase":"sdk-assets","pid":3331,"timed_out":false,"waited":true},{"argv":["git","ls-files","--stage","-z"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":336937,"name":"23-stdout.log","sha256":"89674e117f16e405c78420c09e440e2420749f6fc2d10aaa2bcdb9ef6e2dacdc"},{"bytes":0,"name":"23-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":23,"phase":"sdk-assets","pid":3422,"timed_out":false,"waited":true},{"argv":["git","ls-tree","-rz","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":345544,"name":"24-stdout.log","sha256":"18cdeb2f99318945258280d2a2920acc9e90e063915abacd3b3fff8283893bf8"},{"bytes":0,"name":"24-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":24,"phase":"sdk-assets","pid":3423,"timed_out":false,"waited":true},{"argv":["git","rev-parse","HEAD"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"25-stdout.log","sha256":"4dab81d018e34d509093109a4b7b66d1fc24bd10f26d14b4ea408ae5406fbbfe"},{"bytes":0,"name":"25-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":25,"phase":"sdk-assets","pid":3424,"timed_out":false,"waited":true},{"argv":["git","rev-parse","HEAD^{tree}"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"26-stdout.log","sha256":"707f20829b1729c93b46b6ec3b8c7435d3259ff242e9a878ee58d010dba2aae9"},{"bytes":0,"name":"26-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":26,"phase":"sdk-assets","pid":3425,"timed_out":false,"waited":true},{"argv":["git","show","-s","--format=%P","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"27-stdout.log","sha256":"adb2a93d8e84bde69b0a3b9a50567653111ca472316ed3ca269a2ed0b23294b0"},{"bytes":0,"name":"27-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":27,"phase":"sdk-assets","pid":3426,"timed_out":false,"waited":true},{"argv":["git","status","--porcelain=v1","--untracked-files=all"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":0,"name":"28-stdout.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},{"bytes":0,"name":"28-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":28,"phase":"sdk-assets","pid":3427,"timed_out":false,"waited":true},{"argv":["/usr/share/dotnet/dotnet","restore","/home/runner/work/_temp/issue779-csharp-n15-build/compile/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj","--locked-mode","-p:EvidencePrivateQualification=N15","-p:ContinuousIntegrationBuild=true","-p:SelfContained=false","-p:UseAppHost=false"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":5093,"name":"29-stdout.log","sha256":"07e068660a06572dc78762d86505672a9804716b5c8d9f213a028038daa9196a"},{"bytes":0,"name":"29-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":29,"phase":"restore-publish","pid":3433,"timed_out":false,"waited":true},{"argv":["/usr/share/dotnet/dotnet","publish","/home/runner/work/_temp/issue779-csharp-n15-build/compile/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj","--no-restore","-c","Release","-p:EvidencePrivateQualification=N15","-p:ContinuousIntegrationBuild=true","-p:SelfContained=false","-p:UseAppHost=false","-p:UseSharedCompilation=false","-nr:false","-o","/home/runner/work/_temp/issue779-csharp-n15-build/published"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":6322,"name":"30-stdout.log","sha256":"7d666cb74a9dd5686fa5fd4adb7fe6ee26cb0aa461b2467a1b265f84eab038ca"},{"bytes":0,"name":"30-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":30,"phase":"restore-publish","pid":3542,"timed_out":false,"waited":true},{"argv":["/usr/share/dotnet/dotnet","restore","/home/runner/work/_temp/issue779-csharp-n15-build/compile/Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj","--locked-mode","-p:UseSharedCompilation=false"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":281,"name":"31-stdout.log","sha256":"21c15d1834c7dbf68648690c222e6df2e46c0b8aad6a9c08c5411d0b0d60d39e"},{"bytes":0,"name":"31-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":31,"phase":"restore-publish","pid":4329,"timed_out":false,"waited":true},{"argv":["/usr/share/dotnet/dotnet","test","/home/runner/work/_temp/issue779-csharp-n15-build/compile/Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/ForgeTrust.AppSurface.Evidence.Supervision.Tests.csproj","--no-restore","-p:UseSharedCompilation=false","--filter","FullyQualifiedName~OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs","--logger","trx;LogFileName=pipe-handle.trx","--results-directory","/home/runner/work/_temp/issue779-csharp-n15-build/receipts/pipe-tests"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":1428,"name":"32-stdout.log","sha256":"0296af7ce64e04990bbbd3610cfeb3f0b911aaee1ed7f679ce3aa296ce968319"},{"bytes":0,"name":"32-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":32,"phase":"restore-publish","pid":4375,"timed_out":false,"waited":true},{"argv":["git","ls-files","--stage","-z"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":336937,"name":"33-stdout.log","sha256":"89674e117f16e405c78420c09e440e2420749f6fc2d10aaa2bcdb9ef6e2dacdc"},{"bytes":0,"name":"33-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":33,"phase":"restore-publish","pid":4588,"timed_out":false,"waited":true},{"argv":["git","ls-tree","-rz","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":345544,"name":"34-stdout.log","sha256":"18cdeb2f99318945258280d2a2920acc9e90e063915abacd3b3fff8283893bf8"},{"bytes":0,"name":"34-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":34,"phase":"restore-publish","pid":4589,"timed_out":false,"waited":true},{"argv":["git","rev-parse","HEAD"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"35-stdout.log","sha256":"4dab81d018e34d509093109a4b7b66d1fc24bd10f26d14b4ea408ae5406fbbfe"},{"bytes":0,"name":"35-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":35,"phase":"restore-publish","pid":4590,"timed_out":false,"waited":true},{"argv":["git","rev-parse","HEAD^{tree}"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"36-stdout.log","sha256":"707f20829b1729c93b46b6ec3b8c7435d3259ff242e9a878ee58d010dba2aae9"},{"bytes":0,"name":"36-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":36,"phase":"restore-publish","pid":4591,"timed_out":false,"waited":true},{"argv":["git","show","-s","--format=%P","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"37-stdout.log","sha256":"adb2a93d8e84bde69b0a3b9a50567653111ca472316ed3ca269a2ed0b23294b0"},{"bytes":0,"name":"37-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":37,"phase":"restore-publish","pid":4592,"timed_out":false,"waited":true},{"argv":["git","status","--porcelain=v1","--untracked-files=all"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":0,"name":"38-stdout.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},{"bytes":0,"name":"38-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":38,"phase":"restore-publish","pid":4593,"timed_out":false,"waited":true},{"argv":["git","ls-files","--stage","-z"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":336937,"name":"39-stdout.log","sha256":"89674e117f16e405c78420c09e440e2420749f6fc2d10aaa2bcdb9ef6e2dacdc"},{"bytes":0,"name":"39-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":39,"phase":"handoff","pid":4600,"timed_out":false,"waited":true},{"argv":["git","ls-tree","-rz","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":345544,"name":"40-stdout.log","sha256":"18cdeb2f99318945258280d2a2920acc9e90e063915abacd3b3fff8283893bf8"},{"bytes":0,"name":"40-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":40,"phase":"handoff","pid":4601,"timed_out":false,"waited":true},{"argv":["git","rev-parse","HEAD"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"41-stdout.log","sha256":"4dab81d018e34d509093109a4b7b66d1fc24bd10f26d14b4ea408ae5406fbbfe"},{"bytes":0,"name":"41-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":41,"phase":"handoff","pid":4602,"timed_out":false,"waited":true},{"argv":["git","rev-parse","HEAD^{tree}"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"42-stdout.log","sha256":"707f20829b1729c93b46b6ec3b8c7435d3259ff242e9a878ee58d010dba2aae9"},{"bytes":0,"name":"42-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":42,"phase":"handoff","pid":4603,"timed_out":false,"waited":true},{"argv":["git","show","-s","--format=%P","a6933fa3dc2c7a88832abf337447f88527477703"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":41,"name":"43-stdout.log","sha256":"adb2a93d8e84bde69b0a3b9a50567653111ca472316ed3ca269a2ed0b23294b0"},{"bytes":0,"name":"43-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":43,"phase":"handoff","pid":4604,"timed_out":false,"waited":true},{"argv":["git","status","--porcelain=v1","--untracked-files=all"],"child_umask":-1,"exit":0,"failure":null,"forced_cleanup":false,"group_absent":true,"logs":[{"bytes":0,"name":"44-stdout.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},{"bytes":0,"name":"44-stderr.log","sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}],"ordinal":44,"phase":"handoff","pid":4605,"timed_out":false,"waited":true}]'


class BuildCommandTerminalControls(unittest.TestCase):
    """Captured build data only: these controls never issue a subprocess."""
    def rows(self):
        import json
        return json.loads(CAPTURED_BUILD_COMMANDS)

    def test_actual_45_command_recipe_including_pipe_pair_is_accepted(self):
        runner.validate_build_command_records(self.rows())

    def test_stale_count_missing_extra_duplicate_or_bool_ordinal_is_rejected(self):
        for mutation in ('old43', 'missing', 'extra', 'duplicate', 'bool'):
            rows = self.rows()
            if mutation == 'old43':
                del rows[31:33]
                for ordinal, row in enumerate(rows):
                    row['ordinal'] = ordinal
            elif mutation == 'missing':
                rows.pop()
            elif mutation == 'extra':
                rows.append(dict(rows[-1], ordinal=45))
            elif mutation == 'duplicate':
                rows[32]['ordinal'] = 31
            else:
                rows[0]['ordinal'] = False
            with self.subTest(mutation=mutation), self.assertRaises(runner.Rejected):
                runner.validate_build_command_records(rows)

    def test_any_failed_unjoined_timedout_or_forced_command_is_rejected(self):
        for field, value in (('exit', 1), ('exit', False), ('failure', {}),
                             ('waited', False), ('group_absent', False),
                             ('timed_out', True), ('forced_cleanup', True)):
            for ordinal in (0, 31, 32, 44):
                rows = self.rows()
                rows[ordinal][field] = value
                with self.subTest(field=field, ordinal=ordinal), self.assertRaises(runner.Rejected):
                    runner.validate_build_command_records(rows)

    def test_changed_phase_or_pipe_procedure_is_rejected(self):
        for mutation in ('phase', 'restore', 'project', 'method', 'result-path'):
            rows = self.rows()
            if mutation == 'phase':
                rows[32]['phase'] = 'handoff'
            elif mutation == 'restore':
                rows[31]['argv'][3] = '--force'
            elif mutation == 'project':
                rows[32]['argv'][2] += '.other'
            elif mutation == 'method':
                rows[32]['argv'][6] = 'FullyQualifiedName~OtherTest'
            else:
                rows[32]['argv'][10] = '/tmp/other'
            with self.subTest(mutation=mutation), self.assertRaises(runner.Rejected):
                runner.validate_build_command_records(rows)


class WorkspaceHandoffControls(unittest.TestCase):
    """Use the actual root workspace validator, never materialize a root workspace."""
    def workspace(self):
        spec = importlib.util.spec_from_file_location("n15_workspace_v24", Path(__file__).with_name("prepare-case-workspace.py"))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def receipt(self):
        import json
        return json.loads(Path(__file__).parents[3].joinpath("observed-build/build-receipt.json").read_bytes())

    def test_full_actual_receipt_passes_workspace_before_materialization(self):
        workspace = self.workspace()
        receipt = self.receipt()
        self.assertIs(workspace.validate_build_receipt_data(receipt, "N15"), receipt)

    def test_old_inventory_and_wrong_pipe_procedure_fail_workspace(self):
        workspace = self.workspace()
        for mutation in ("old43", "phase", "pipe-method", "missing-pipe"):
            receipt = self.receipt()
            if mutation == "old43":
                del receipt["commands"][31:33]
                for i, row in enumerate(receipt["commands"]):
                    row["ordinal"] = i
            elif mutation == "phase":
                receipt["commands"][31]["phase"] = "handoff"
            elif mutation == "pipe-method":
                receipt["commands"][32]["argv"][6] = "FullyQualifiedName~Other"
            else:
                del receipt["linux_pipe_regression"]
            with self.subTest(mutation=mutation), self.assertRaises(workspace.Reject):
                workspace.validate_build_receipt_data(receipt, "N15")

    def test_failed_or_unjoined_pipe_pair_rejects_workspace(self):
        workspace = self.workspace()
        for key, value in (("exit", 1), ("waited", False), ("group_absent", False), ("timed_out", True)):
            receipt = self.receipt()
            receipt["commands"][32][key] = value
            with self.subTest(field=key), self.assertRaises(workspace.Reject):
                workspace.validate_build_receipt_data(receipt, "N15")

    def test_invalid_private_selector_still_rejects(self):
        workspace = self.workspace()
        receipt = self.receipt()
        receipt["commands"][30]["argv"].append("-p:EvidencePrivateQualification=N14")
        with self.assertRaises(workspace.Reject):
            workspace.validate_build_receipt_data(receipt, "N15")


if __name__ == "__main__":
    unittest.main(verbosity=2)
