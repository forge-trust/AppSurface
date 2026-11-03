"""N09 detached identity/projection data controls; no native execution or owner."""
import copy
import importlib.util
from pathlib import Path
import unittest

import binding

spec = importlib.util.spec_from_file_location("c1_n09_records", Path(__file__).parent / "procedures/n09/records.py")
records = importlib.util.module_from_spec(spec)
spec.loader.exec_module(records)
SOURCE, BASE, G = "a" * 40, "b" * 40, "c" * 32


def model():
    return {"schema": "issue779-n09-retained-slot-fixture-v1", "generation": G, "case": "N09",
        "source_revision": SOURCE, "base_revision": BASE, "observation_sha256": "d" * 64,
        "root_launch_exit": 1, "fixture_processes_joined": True, "fresh_generated_groups_empty": True,
        "account_disposition": "closed-and-nss-absent", "publication": False, "native_acceptance": False, "trusted": False}


class N09DataControls(unittest.TestCase):
    def setUp(self):
        records.SOURCE, records.N09_BASE_REVISION = SOURCE, BASE

    def test_current_source_and_explicit_base_bind_the_original_closed_result(self):
        records.validate_native_result(model(), expected_base_revision=BASE)
        for field, bad in (("source_revision", "f" * 40), ("base_revision", "f" * 40),
                           ("root_launch_exit", 0), ("fixture_processes_joined", False),
                           ("fresh_generated_groups_empty", False), ("publication", True),
                           ("native_acceptance", True), ("trusted", True)):
            changed = model()
            changed[field] = bad
            with self.subTest(field=field), self.assertRaises(ValueError):
                records.validate_native_result(changed, expected_base_revision=BASE)

    def test_actual_observation_bytes_and_retained_slot_facts_are_required(self):
        observation = {"schema": "issue779-n09-retained-slot-observation-v1", "case": "N09",
            "generation": G, "source_revision": SOURCE, "base_revision": BASE,
            "allocation_record_sha256": "d" * 64, "cleanup_record_sha256": "e" * 64, "results_gid": 1234,
            "account_ids_absent": True, "worker_cgroup_settled": True, "original_projection_verified": True,
            "path_comparison": {"schema": "issue779-n09-post-settlement-path-comparison-v1",
                "parent_identity_matches": True, "slot_identity_matches": True, "slot_empty": True,
                "native_authority": False, "native_acceptance": False},
            "observation_only": True, "native_authority": False, "native_acceptance": False}
        def check(value, *, stale_hash=False):
            raw = binding.canonical(value)
            result = model()
            result["observation_sha256"] = "0" * 64 if stale_hash else binding.digest(raw)
            records.validate_n09_observation({"n09-observation.json": value,
                "_retained_raw": {"n09-observation.json": raw}}, result)
        check(observation)
        with self.assertRaises(ValueError):
            check(observation, stale_hash=True)
        for field in ("parent_identity_matches", "slot_identity_matches", "slot_empty"):
            changed = copy.deepcopy(observation)
            changed["path_comparison"][field] = False
            with self.subTest(field=field), self.assertRaises(ValueError):
                check(changed)


if __name__ == "__main__":
    unittest.main()
