"""Detached N11 terminal-record binding; no native state or owner is created."""
import copy
import importlib.util
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("n11_closed_observation", ROOT / "procedures/n11/records.py")
records = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(records)
GENERATION = "0123456789abcdef0123456789abcdef"
IDS = {"worker_uid": 1501, "subject_uid": 1502, "worker_gid": 1601,
       "subject_gid": 1602, "results_gid": 1603}


def observation(settled=True):
    prefix = GENERATION[:28]
    value = {"schema": "issue779-n11-filesystem-nss-v2" if settled else "issue779-n11-filesystem-nss-v1",
             "generation": GENERATION, "output_slot_present": False, "raw_results_empty": True,
             "account_disposition": "removed-under-root-custody" if settled else "preserved-quarantined",
             "users": [{"name": name + prefix, "uid": IDS[role + "_uid"],
                        "gid": IDS[role + "_gid"], "retained": not settled}
                       for name, role in (("evw", "worker"), ("evs", "subject"))],
             "groups": [{"name": name + prefix, "gid": IDS[key], "retained": not settled}
                        for name, key in (("evw", "worker_gid"), ("evs", "subject_gid"), ("evr", "results_gid"))],
             "original_worker_pid_absent": True, "original_broker_pid_absent": True,
             "fresh_generated_worker_group": {"exists": False, "populated": None, "frozen": None, "inode": None},
             "fresh_generated_owner_group": {"exists": True, "populated": False, "frozen": False, "inode": 123},
             "native_acceptance": False, "native_authority": False}
    if settled:
        value.update(output_parent_descriptor_object_match=True, root_owned_layout_observed=True)
    else:
        value["output_parent_descriptor_identity_match"] = True
    return value


class N11ObservationBindingTests(unittest.TestCase):
    def check(self, value, *, settled=True, ids=None, disposition=None, schema=None):
        return records.validate_filesystem_observation(value, generation=GENERATION,
            descriptor={k: v for k, v in IDS.items() if k != "results_gid"},
            consistency_schema=schema or ("issue779-n11-settled-callback-failure-data-v1" if settled
                                         else "issue779-n11-detached-data-consistency-v1"),
            account_disposition=disposition or ("removed-under-root-custody" if settled else "preserved-quarantined"),
            original_record={"account_ids": IDS if ids is None else ids})

    def test_complete_sealed_and_original_quarantine_are_separate_closed_contracts(self):
        self.assertIs(True, self.check(observation()))
        self.assertIs(True, self.check(observation(False), settled=False))
        with self.assertRaises(records.Rejected):
            self.check(observation(False))
        with self.assertRaises(records.Rejected):
            self.check(observation(), settled=False)
        with self.assertRaises(records.Rejected):
            self.check(observation(), schema="unknown-canary")

    def test_sealed_branch_requires_original_five_typed_ids_not_later_nss_inference(self):
        for ids in ({}, {**IDS, "extra": 1}, {**IDS, "results_gid": 0},
                    {**IDS, "results_gid": True}, {**IDS, "results_gid": 2**32 - 1},
                    {**IDS, "results_gid": IDS["worker_gid"]}, {**IDS, "worker_uid": 9999},
                    {**IDS, "subject_gid": "1602"}):
            with self.subTest(ids=ids), self.assertRaises(records.Rejected):
                self.check(observation(), ids=ids)

    def test_every_positive_layout_exit_empty_and_failure_flag_is_required(self):
        fields = ("output_parent_descriptor_object_match", "root_owned_layout_observed",
                  "original_worker_pid_absent", "original_broker_pid_absent")
        for key in (*fields, "output_slot_present", "raw_results_empty", "native_acceptance", "native_authority"):
            value = observation()
            value[key] = not value[key]
            with self.subTest(key=key), self.assertRaises(records.Rejected):
                self.check(value)
        value = observation(); value["generation"] = "f" * 32
        with self.assertRaises(records.Rejected):
            self.check(value)
        value = observation(); value["raw-canary"] = "untrusted"
        with self.assertRaises(records.Rejected):
            self.check(value)

    def test_partial_user_or_group_removal_and_changed_numeric_or_name_binding_reject(self):
        for collection in ("users", "groups"):
            for index in range(len(observation()[collection])):
                for field, changed in (("retained", True), ("name", "foreign"), ("gid", 9999)):
                    value = observation(); value[collection][index][field] = changed
                    with self.subTest(collection=collection, index=index, field=field), self.assertRaises(records.Rejected):
                        self.check(value)
                value = observation(); value[collection].pop(index)
                with self.assertRaises(records.Rejected):
                    self.check(value)
        value = observation(); value["users"][0]["uid"] = 9999
        with self.assertRaises(records.Rejected):
            self.check(value)
        with self.assertRaises(records.Rejected):
            self.check(observation(), disposition="preserved-quarantined")

    def test_group_existence_and_empty_state_cannot_be_projected_from_unknown_or_populated(self):
        for key in ("fresh_generated_worker_group", "fresh_generated_owner_group"):
            for changed in ({"exists": False, "populated": False, "frozen": None, "inode": None},
                            {"exists": True, "populated": True, "frozen": False, "inode": 1},
                            {"exists": True, "populated": False, "frozen": True, "inode": 1},
                            {"exists": True, "populated": False, "frozen": False, "inode": 0},
                            {"exists": True, "populated": False, "frozen": False, "inode": True}):
                value = observation(); value[key] = copy.deepcopy(changed)
                with self.subTest(key=key, changed=changed), self.assertRaises(records.Rejected):
                    self.check(value)


if __name__ == "__main__":
    unittest.main()
