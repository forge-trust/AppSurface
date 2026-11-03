"""Detached N15 data/clock controls only; no root, NSS, worker or subprocess calls."""
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import types
import unittest
from unittest import mock

HERE = Path(__file__).resolve().parent

def module(name, path):
    raw = path.read_bytes()
    value = types.ModuleType(name)
    value.__file__ = str(path)
    exec(compile(raw, str(path), "exec", dont_inherit=True), value.__dict__)
    return value

f = module("n15_finalizer_detached", HERE / "finalize_n15_observation.py")
r = module("n15_archive_detached", HERE / "records.py")
p = module("n15_parser_detached", HERE.parent / "parsers/check_n15_records.py")
GEN = "0123456789abcdef0123456789abcdef"


def account_model():
    suffix = GEN[:28]
    return {"names": {"worker": "evw" + suffix, "subject": "evs" + suffix, "results": "evr" + suffix},
            "ids": {"worker_uid": 1001, "worker_gid": 1002, "subject_uid": 1003, "subject_gid": 1004, "results_gid": 1005}}


class N15DataControls(unittest.TestCase):
    def test_actual_generated_name_and_number_metadata_then_one_field_reuse(self):
        value = account_model()
        rows = f.account_rows(value, GEN, {"uid": 1001, "gid": 1002})
        self.assertEqual(("user", "evw" + GEN[:28], 1001), rows[0])
        for field, bad in (("worker_uid", 0), ("subject_uid", 1001), ("results_gid", 1002)):
            changed = copy.deepcopy(value)
            changed["ids"][field] = bad
            with self.subTest(field=field), self.assertRaises(f.Reject):
                f.account_rows(changed, GEN, {"uid": 1001, "gid": 1002})
        name = rows[0][1]
        exact = types.SimpleNamespace(pw_name=name, pw_uid=1001)
        self.assertTrue(f.nss_identity_matches("user", name, 1001, exact, exact))
        reused = types.SimpleNamespace(pw_name="foreign", pw_uid=1001)
        self.assertFalse(f.nss_identity_matches("user", name, 1001, exact, reused))

    def test_cleanup_complete_utilities_then_nonzero_bool_and_missing(self):
        names = account_model()["names"]
        rows = [{"utility": utility, "name": names[name], "exit": 0}
                for utility, name in (("userdel", "worker"), ("userdel", "subject"),
                                      ("groupdel", "worker"), ("groupdel", "subject"), ("groupdel", "results"))]
        self.assertTrue(f.validate_utility_rows(rows, names))
        for code in (1, 124):
            changed = copy.deepcopy(rows)
            changed[-1]["exit"] = code
            self.assertFalse(f.validate_utility_rows(changed, names))
        changed = copy.deepcopy(rows)
        changed[-1]["exit"] = True
        with self.assertRaises(f.Reject): f.validate_utility_rows(changed, names)
        with self.assertRaises(f.Reject): f.validate_utility_rows(rows[:-1], names)
        f.validate_join_observation(321, False)
        with self.assertRaises(f.Reject): f.validate_join_observation(321, True)

    def test_replay_requires_same_unit_denial_and_no_new_phase(self):
        measurement = {"owner_unit": "appsurface-evidence-owner-" + GEN + ".service",
                       "worker_unit": "appsurface-evidence-worker-" + GEN + ".service", "retry_exit": 1}
        partial = {"owner": {"unit": measurement["owner_unit"]}, "worker": {"unit": measurement["worker_unit"]}}
        owner = {"LoadState": "loaded", "ActiveState": "failed", "SubState": "failed", "ExecMainCode": "1", "ExecMainStatus": "1"}
        value = f.validate_replay_observation(measurement, partial, b"", b"ASEVD402\n", b"before", b"before", owner, "unchanged")
        self.assertEqual(p.REACTIVATION_KEYS, set(value))
        for stdout, stderr in ((b"unexpected", b"ASEVD402"), (b"", b"other"),
                               (b"", b'ASEVD402 {"phase":"start-transient-unit-reply"}')):
            with self.subTest(stdout=stdout, stderr=stderr), self.assertRaises(f.Reject):
                f.validate_replay_observation(measurement, partial, stdout, stderr, b"before", b"before", owner, "unchanged")

    def test_original_five_to_thirty_second_window_has_exact_neighbors(self):
        for left in (5000, 30000): self.assertEqual(left, p.admission_window_remaining(100000 + left, 100000))
        for left in (4999, 30001):
            with self.assertRaises(p.Reject): p.admission_window_remaining(100000 + left, 100000)

    def test_fixture_uses_existing_job_and_one_shared_start_window(self):
        boot_clock = object()
        clock = types.SimpleNamespace(CLOCK_BOOTTIME=boot_clock,
                                      clock_gettime_ns=mock.Mock(return_value=100000 * 1000000))
        with mock.patch.object(r, "time", clock):
            args = r.fixture_arguments(Path("/unused-data-only"), GEN, 340000)
            values = dict(zip(args[1::2], args[2::2]))
            self.assertEqual("340000", values["--job-deadline-boot-ms"])
            self.assertEqual("280000", values["--work-deadline-boot-ms"])
            self.assertEqual("130000", values["--startup-deadline-boot-ms"])
            self.assertEqual(values["--startup-deadline-boot-ms"], values["--admission-deadline-boot-ms"])
            with self.assertRaises(r.Rejected): r.fixture_arguments(Path("/unused-data-only"), GEN, 160000)
            self.assertEqual([mock.call(boot_clock), mock.call(boot_clock)], clock.clock_gettime_ns.call_args_list)

    def test_partial_diagnostic_archive_grammar_cannot_establish_native_case(self):
        selection = {"schema": "issue779-native-retention-selection-v1", "selection_status": "parent-empty",
                     "data_file_count": 0, "data_bytes": 0, "missing_fixed_file_count": len(r.ARCHIVE_NAMES) - 1}
        body = json.dumps(selection, sort_keys=True, separators=(",", ":")).encode()
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w:", format=tarfile.USTAR_FORMAT) as archive:
            entry = tarfile.TarInfo("retention-selection.json")
            entry.size = len(body)
            entry.mode = 0o600
            entry.uid = entry.gid = entry.mtime = 0
            archive.addfile(entry, io.BytesIO(body))
        raw = buffer.getvalue()
        values = r.inspect_archive(raw)
        self.assertEqual({"retention-selection.json"}, set(values))
        with self.assertRaises(r.Rejected):
            r.validate_n15_archive(values, b"not-a-parser", GEN, "1" * 64, "github-actions-1-1", 1.0,
                                   "2026-10-19T08:00:00Z", "2" * 40, "3" * 64, "4" * 40)
        with self.assertRaises(r.Rejected): r.inspect_archive(raw + b"unexpected")


if __name__ == "__main__":
    unittest.main()
