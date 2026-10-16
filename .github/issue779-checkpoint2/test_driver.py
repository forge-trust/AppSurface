"""Pure fixture projections only; no root process, admission, artifact or coverage authority."""
from pathlib import Path
import importlib.util,json,sys,unittest
sys.path.insert(0,str(Path(__file__).parent))
spec=importlib.util.spec_from_file_location('checkpoint2_native',Path(__file__).with_name('run-native.py'))
native=importlib.util.module_from_spec(spec);spec.loader.exec_module(native)
class DriverControls(unittest.TestCase):
 def observation(self):
  return {'schema':'evidence-coverage-output-v1','registered_pairs':2,'received_bytes':17,'received_byte_limit':16,'failure':'QuotaExceeded','quota_exceeded':True,'stop_signal_failed':False,'pairs':[{'received_bytes':9},{'received_bytes':8}]}
 def test_actual_ledger_projection_requires_exact_pair_set_and_byte_reconciliation(self):
  row=self.observation();self.assertEqual(row,native.read_output_observation(json.dumps(row).encode()))
  for changed in [dict(row,registered_pairs=1),dict(row,received_bytes=18),dict(row,pairs=[]),dict(row,received_bytes=True)]:
   with self.subTest(changed=changed):
    with self.assertRaises(RuntimeError):native.read_output_observation(json.dumps(changed).encode())
 def test_missing_duplicate_or_unrelated_raw_lines_cannot_substitute_for_quota(self):
  row=json.dumps(self.observation()).encode()
  for raw in [b'private-canary',b'{}',row+b'\n'+row]:
   with self.subTest(raw=raw):
    with self.assertRaises(RuntimeError):native.read_output_observation(raw)
  self.assertEqual(17,native.read_output_observation(b'private-canary\n'+row)['received_bytes'])
 def test_policy_selects_actual_coverage_and_retains_original_thresholds(self):
  p=native.policy();producer=p['Profiles'][0]['Producers'][0]
  self.assertEqual('coverage',producer['Kind']);self.assertEqual('1.0.0',producer['Version'])
  self.assertEqual((95,85,.5),tuple(producer['CoverageGate'][k] for k in ['MinLinePercent','MinBranchPercent','TolerancePercent']))
  self.assertEqual('calculation/**',p['Rules'][0]['Pattern']);self.assertEqual([],p['Profiles'][0]['Resources'])
  self.assertEqual(['appsurface/coverage/behavioral-patch@1'],producer['AssertionIds'])
 def test_fixture_never_stamps_trusted_or_native_authority(self):
  self.assertNotIn('Trusted',json.dumps(native.policy()));self.assertEqual(5,len(native.FIXED))
  self.assertEqual('HOME=/nonexistent',native.FIXED[1])
if __name__=='__main__':unittest.main()
