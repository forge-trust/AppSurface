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

class PackageRowsControls(unittest.TestCase):
 def setUp(self):
  import base64,copy,tempfile,time
  from unittest.mock import patch
  self.base64=base64;self.copy=copy;self.time=time;self.patch=patch
  self._temporary=tempfile.TemporaryDirectory()
  self.root=Path(self._temporary.name)
  self.source_lock=Path(__file__).with_name('subject')/'packages.lock.json'
  self.lock=json.loads(self.source_lock.read_text())

 def tearDown(self):
  self._temporary.cleanup()

 def write_lock(self,lock,name):
  path=self.root/name
  path.write_text(json.dumps(lock))
  return path

 def test_accepts_the_actual_fourteen_locked_package_rows(self):
  lock_path=self.write_lock(self.lock,'valid14.json')
  rows=native.package_rows(lock_path)
  self.assertEqual(14,len(rows))
  self.assertEqual(14,len({(name,version) for name,version,_ in rows}))
  self.assertTrue(all(name==name.lower() and version==version.lower() for name,version,_ in rows))
  self.assertTrue(all(len(self.base64.b64decode(digest,validate=True))==64 for _,_,digest in rows))

 def test_bad_lock_rows_reject_before_network_or_cache_creation(self):
  variants=[]
  absolute_name=self.copy.deepcopy(self.lock);nodes=absolute_name['dependencies']['net10.0'];row=nodes.pop(next(iter(nodes)));nodes['/absolute-package']=row
  variants.append(('absolute-name',absolute_name))
  traversal_name=self.copy.deepcopy(self.lock);nodes=traversal_name['dependencies']['net10.0'];row=nodes.pop(next(iter(nodes)));nodes['../outside']=row
  variants.append(('traversal-name',traversal_name))
  absolute_version=self.copy.deepcopy(self.lock);nodes=absolute_version['dependencies']['net10.0'];nodes[next(iter(nodes))]['resolved']='/1.0.0'
  variants.append(('absolute-version',absolute_version))
  traversal_version=self.copy.deepcopy(self.lock);nodes=traversal_version['dependencies']['net10.0'];nodes[next(iter(nodes))]['resolved']='../1.0.0'
  variants.append(('traversal-version',traversal_version))
  bad_type=self.copy.deepcopy(self.lock);nodes=bad_type['dependencies']['net10.0'];nodes[next(iter(nodes))]['type']='Unknown'
  variants.append(('bad-type',bad_type))
  bad_hash=self.copy.deepcopy(self.lock);nodes=bad_hash['dependencies']['net10.0'];nodes[next(iter(nodes))]['contentHash']='not-base64'
  variants.append(('bad-hash',bad_hash))
  duplicate_case=self.copy.deepcopy(self.lock);nodes=duplicate_case['dependencies']['net10.0'];names=list(nodes);row=nodes.pop(names[1]);nodes[names[0].swapcase()]=row
  variants.append(('duplicate-case',duplicate_case))
  too_few=self.copy.deepcopy(self.lock);nodes=too_few['dependencies']['net10.0'];nodes.pop(next(iter(nodes)))
  variants.append(('thirteen-packages',too_few))
  too_many=self.copy.deepcopy(self.lock);nodes=too_many['dependencies']['net10.0'];nodes['ExtraControlPackage']=self.copy.deepcopy(next(iter(nodes.values())))
  variants.append(('fifteen-packages',too_many))

  for label,lock in variants:
   with self.subTest(label=label):
    lock_path=self.write_lock(lock,label+'.json')
    destination=self.root/(label+'-cache')
    with self.patch.object(native.urllib.request,'urlopen') as urlopen:
     with self.assertRaises(RuntimeError):
      native.acquire_packages(lock_path,destination,self.time.monotonic()+10,'dotnet','hasher.dll')
     urlopen.assert_not_called()
    self.assertFalse(destination.exists())

class TraversalRootControls(unittest.TestCase):
 def test_fresh_root_has_required_traversal_under_private_umask_and_rejects_existing_path(self):
  import os,stat,tempfile
  with tempfile.TemporaryDirectory() as tmp:
   path=Path(tmp)/'generation';old=os.umask(0o077)
   try:native.create_traversable_root(path)
   finally:os.umask(old)
   observed=path.lstat();self.assertEqual(0o755,stat.S_IMODE(observed.st_mode));self.assertEqual(os.geteuid(),observed.st_uid)
   with self.assertRaises(FileExistsError):native.create_traversable_root(path)
   self.assertEqual((observed.st_dev,observed.st_ino), (path.lstat().st_dev,path.lstat().st_ino))

if __name__=='__main__':unittest.main()
