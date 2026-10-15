"""Data-only controls for the N15 finalizer; no NSS, root, systemd, or subprocess calls."""
import hashlib
import json
from pathlib import Path
import types
import unittest

import finalize_n15_observation as f
import check_n15_records as parser

GEN="0123456789abcdef0123456789abcdef"


def account_fixture():
    suffix=GEN[:28]
    return {"names":{"worker":"evw"+suffix,"subject":"evs"+suffix,"results":"evr"+suffix},
            "ids":{"worker_uid":1001,"worker_gid":1002,"subject_uid":1003,"subject_gid":1004,"results_gid":1005}}


class FinalizerDataControls(unittest.TestCase):
    def test_generated_account_binding_and_nss_reuse_are_closed(self):
        rows=f.account_rows(account_fixture(),GEN,{"uid":1001,"gid":1002})
        self.assertEqual(rows[0],("user","evw"+GEN[:28],1001))
        right_name=types.SimpleNamespace(pw_name=rows[0][1],pw_uid=rows[0][2])
        right_number=types.SimpleNamespace(pw_name=rows[0][1],pw_uid=rows[0][2])
        self.assertTrue(f.nss_identity_matches("user",rows[0][1],rows[0][2],right_name,right_number))
        reused=types.SimpleNamespace(pw_name="foreign",pw_uid=rows[0][2])
        self.assertFalse(f.nss_identity_matches("user",rows[0][1],rows[0][2],right_name,reused))
        bad=account_fixture(); bad["ids"]["subject_uid"]=bad["ids"]["worker_uid"]
        with self.assertRaises(f.Reject):
            f.account_rows(bad,GEN,{"uid":1001,"gid":1002})

    def test_fixed_utility_exit_and_owner_join_fail_closed(self):
        self.assertTrue(f.utility_succeeded(0))
        self.assertFalse(f.utility_succeeded(1))
        self.assertFalse(f.utility_succeeded(True))
        f.validate_join_observation(321,False)
        with self.assertRaises(f.Reject):
            f.validate_join_observation(321,True)

    def test_cleanup_utility_rows_require_every_generated_name_and_record_failures(self):
        names=account_fixture()["names"]
        rows=[{"utility":"userdel","name":names["worker"],"exit":0},
              {"utility":"userdel","name":names["subject"],"exit":0},
              {"utility":"groupdel","name":names["worker"],"exit":0},
              {"utility":"groupdel","name":names["subject"],"exit":0},
              {"utility":"groupdel","name":names["results"],"exit":0}]
        self.assertTrue(f.validate_utility_rows(rows,names))
        rows[-1]={"utility":"groupdel","name":names["results"],"exit":None,"already_absent":True}
        self.assertTrue(f.validate_utility_rows(rows,names))
        rows[-1]={"utility":"groupdel","name":names["results"],"exit":7}
        self.assertFalse(f.validate_utility_rows(rows,names))
        with self.assertRaises(f.Reject):
            f.validate_utility_rows(rows[:-1],names)

    def test_acquisition_uses_verified_capture_digest_operand(self):
        text=Path(__file__).with_name("acquire-inbound.sh").read_text()
        self.assertIn('--arg capture "${v[source-capture-sha256]}"',text)
        self.assertIn('.capture_input_sha256==$capture',text)
        self.assertNotIn('.capture_input_sha256=="8f5822ab0f5da555ef142a28321631f1960e61463fe14a83b33e83d521934046"',text)
        transport=Path(__file__).with_name("prepare-root-inputs-v2.sh").read_text()
        self.assertIn('--arg capture "${values[source-capture-sha256]}"',transport)
        self.assertIn('.capture_input_sha256==$capture',transport)
        build=Path(__file__).parents[1]/"issue779-csharp-build"/"build-fdd.sh"
        capture=Path(__file__).parents[1]/"issue779-csharp-build"/"capture-receipt.json"
        build_text=build.read_text()
        raw=capture.read_bytes()
        document=json.loads(raw)
        projection={key:document[key] for key in ("validation_commit","previous_validation_commit","source_count","source_tree","source")}
        actual_projection=hashlib.sha256(json.dumps(projection,sort_keys=True,separators=(",",":")).encode()).hexdigest()
        self.assertEqual(hashlib.sha256(raw).hexdigest(),"8f5822ab0f5da555ef142a28321631f1960e61463fe14a83b33e83d521934046")
        self.assertEqual(actual_projection,"5aa7188c4dbe5632d7602271eff23837fc49f098a3ae46b9e06d1157a2cc476a")
        self.assertIn("RESULT['capture_input_sha256'] = capture_fact['sha256']",build_text)
        self.assertIn("RESULT['capture_projection_sha256'] = projection_sha",build_text)

    def test_partial_read_or_wrong_digest_cannot_bind_retry_evidence(self):
        raw=b"fixed bounded record\n"
        digest=hashlib.sha256(raw).hexdigest()
        self.assertTrue(f.digest_matches(raw,digest))
        self.assertFalse(f.digest_matches(raw[:-1],digest))
        self.assertFalse(f.digest_matches(raw,"0"*64))

    def test_original_replay_observations_and_mutations(self):
        m={"owner_unit":"appsurface-evidence-owner-"+GEN+".service",
           "worker_unit":"appsurface-evidence-worker-"+GEN+".service","retry_exit":1}
        p={"owner":{"unit":m["owner_unit"]},"worker":{"unit":m["worker_unit"]}}
        owner={"LoadState":"loaded","ActiveState":"failed","SubState":"failed",
               "ExecMainCode":"1","ExecMainStatus":"1"}
        evidence=f.validate_replay_observation(m,p,b"",b"ASEVD402\n",b"before",b"before",owner)
        self.assertEqual(set(evidence),parser.REACTIVATION_KEYS)
        with self.assertRaises(f.Reject):
            f.validate_replay_observation(m,p,b"",b'{"phase":"start-transient-unit-reply"}',
                                          b"before",b"before",owner)
        with self.assertRaises(f.Reject):
            f.validate_replay_observation(m,p,b"",b"ASEVD402",b"before",b"after",owner)
        with self.assertRaises(f.Reject):
            f.validate_replay_observation({**m,"retry_exit":124},p,b"",b"ASEVD402",
                                          b"before",b"before",owner)

    def test_original_admission_window_five_to_thirty_seconds(self):
        trigger=100000
        self.assertEqual(parser.admission_window_remaining(trigger+5000,trigger),5000)
        self.assertEqual(parser.admission_window_remaining(trigger+30000,trigger),30000)
        for remaining in (4999,30001):
            with self.subTest(remaining=remaining), self.assertRaises(parser.Reject):
                parser.admission_window_remaining(trigger+remaining,trigger)


if __name__=="__main__":
    unittest.main(verbosity=2)
