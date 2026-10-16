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

class ProducerSamplingControls(unittest.TestCase):
 def sample(self,*,uid=1001,gid=1002,proc_uids=None,proc_gids=None,cgroup=None,
            starts=(7654,7654),manager_pids=('4321','4321'),pending=None):
  from unittest.mock import patch
  unit='appsurface-evidence-producer-test.service';pid='4321'
  descriptor={'subject_uid':uid,'subject_gid':gid}
  expected_cgroup='/system.slice/'+unit
  facts={'MainPID':manager_pids[0],'ExecMainPID':pid,'User':str(uid),'Group':str(gid),
         'ControlGroup':expected_cgroup}
  final=dict(facts,MainPID=manager_pids[1])
  status='Name:\tdotnet\nUid:\t'+' '.join(map(str,proc_uids or [uid]*4))+'\nGid:\t'+' '.join(map(str,proc_gids or [gid]*4))+'\n'
  group_text='0::'+(cgroup if cgroup is not None else expected_cgroup)+'\n'
  stat_reads=iter(starts)
  def read_text(path,*args,**kwargs):
   value=str(path)
   if value.endswith('/stat'):
    start=next(stat_reads)
    return pid+' (producer process) S '+' '.join(['0']*18+[str(start)])
   if value.endswith('/status'):return status
   if value.endswith('/cgroup'):return group_text
   raise AssertionError('unexpected proc read')
  with patch.object(native,'show',side_effect=[facts,final]) as mocked_show:
   with patch.object(Path,'read_text',autospec=True,side_effect=read_text):
    result=native.sample_producer(unit,descriptor,1,pending)
  return result,mocked_show

 def test_returns_only_stable_exact_producer_identity_after_manager_resample(self):
  result,show=self.sample()
  self.assertEqual({'pid':4321,'uid':1001,'gid':1002,'starttime_ticks':7654,
                    'cgroup':'/system.slice/appsurface-evidence-producer-test.service'},result)
  self.assertEqual(2,show.call_count)

 def test_wrong_process_tuple_stays_pending_with_closed_observation_facts(self):
  pending={}
  result,show=self.sample(proc_uids=[1001,1001,1001,2001],pending=pending)
  self.assertIsNone(result)
  self.assertEqual(1,show.call_count)
  self.assertEqual({'pid':4321,'uids':[1001,1001,1001,2001],'gids':[1002]*4,
                    'cgroup_matches':True,'same_starttime':True},pending)

 def test_manager_pid_transition_is_not_returned_as_producer_acceptance(self):
  result,show=self.sample(manager_pids=('4321','9876'))
  self.assertIsNone(result)
  self.assertEqual(2,show.call_count)

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


class KernelCounterControls(unittest.TestCase):
 def test_closed_counter_projection_retains_peaks_and_limit_events_without_native_authority(self):
  from unittest.mock import patch
  rows={'pids.current':b'64', 'pids.max':b'64', 'pids.events':b'max 7\n',
        'memory.current':b'1024', 'memory.max':b'1073741824', 'memory.events':b'oom 0\noom_kill 0\n'}
  samples={}
  def read(path):return rows[path.name]
  with patch.object(Path,'read_bytes',autospec=True,side_effect=read):
   native.sample_cgroup_counters('fixed.service',samples)
   rows['pids.current']=b'4';native.sample_cgroup_counters('fixed.service',samples)
  self.assertEqual(64,samples['pids.current.peak']);self.assertEqual(7,samples['pids.events.max'])
  self.assertEqual(0,samples['memory.events.oom_kill']);self.assertEqual(2,samples['count'])
  with patch.object(Path,'read_bytes',side_effect=FileNotFoundError):
   native.sample_cgroup_counters('fixed.service',samples)
  self.assertEqual(2,samples['count'])
  with patch.object(Path,'read_bytes',return_value=b'private-canary'):
   with self.assertRaises(RuntimeError):native.sample_cgroup_counters('fixed.service',samples)
  self.assertNotIn('private-canary',json.dumps(samples));self.assertEqual(2,samples['count'])

class ProcessThreadSnapshotControls(unittest.TestCase):
 def test_cgroup_pid_parser_is_bounded_unique_and_order_independent(self):
  self.assertEqual((12,34),native.parse_cgroup_pids(b'12\n34\n'))
  self.assertIsNone(native.parse_cgroup_pids(b'12\n12\n'))
  self.assertIsNone(native.parse_cgroup_pids(b'0\n'))
  self.assertIsNone(native.parse_cgroup_pids(b'12\nprivate-canary\n'))
  self.assertIsNone(native.parse_cgroup_pids(b'1'* (native.MAX_CGROUP_PROCS_BYTES+1)))
  self.assertIsNone(native.parse_cgroup_pids(b'\n'.join(str(i).encode() for i in range(1,34))))

 def test_proc_starttime_uses_final_comm_delimiter_and_rejects_bad_identity(self):
  self.assertEqual(987654,native.parse_proc_starttime(b'4321 (dotnet (MSBuild)) S '+b' '.join([b'0']*18+[b'987654']),4321))
  self.assertIsNone(native.parse_proc_starttime(b'4322 (dotnet) S '+b' '.join([b'0']*18+[b'987654']),4321))
  self.assertIsNone(native.parse_proc_starttime(b'4321 (dotnet S '+b' '.join([b'0']*20),4321))

 def test_status_accepts_only_exact_four_uid_gid_fields_and_bounded_threads(self):
  def status(uid=1001,gid=1002,threads=7):
   return (f'Name:\tdotnet\nUid:\t{uid} {uid} {uid} {uid}\nGid:\t{gid} {gid} {gid} {gid}\nThreads:\t{threads}\n').encode()
  self.assertEqual(7,native.parse_proc_status(status(),1001,1002))
  self.assertIsNone(native.parse_proc_status(status(uid=2001),1001,1002))
  self.assertIsNone(native.parse_proc_status(status(threads=native.MAX_PROCESS_THREADS+1),1001,1002))
  self.assertIsNone(native.parse_proc_status(b'Uid: 1001 1001 1001 1001\nGid: 1002 1002 1002 1002\n',1001,1002))

 def test_role_projection_returns_only_closed_executable_basenames(self):
  self.assertEqual('MSBuild.dll',native.process_role(b'/sdk/dotnet\0/sdk/MSBuild.dll\0'))
  self.assertEqual('testhost.dll',native.process_role(b'/private/secret/path/testhost.dll\0'))
  self.assertEqual('other',native.process_role(b'/private/secret/canary\0'))
  for raw in (b'/private/secret/canary',b'/private/secret/canary\0'):
   self.assertNotIn('private/secret',native.process_role(raw))

 def test_process_tree_requires_stable_exact_cgroup_membership_and_never_returns_raw_arguments(self):
  from unittest.mock import patch
  import time
  unit='appsurface-evidence-producer-test.service';identity={'pid':41,'uid':1001,'gid':1002,'cgroup':'/system.slice/'+unit}
  proc={
   '/sys/fs/cgroup/system.slice/'+unit+'/cgroup.procs':[b'41\n7\n',b'7\n41\n'],
   '/proc/41/stat':[b'41 (dotnet (host)) S '+b' '.join([b'0']*18+[b'888']),b'41 (dotnet (host)) S '+b' '.join([b'0']*18+[b'888'])],
   '/proc/7/stat':[b'7 (testhost) S '+b' '.join([b'0']*18+[b'777']),b'7 (testhost) S '+b' '.join([b'0']*18+[b'777'])],
   '/proc/41/status':[b'Uid: 1001 1001 1001 1001\nGid: 1002 1002 1002 1002\nThreads: 3\n'],
   '/proc/7/status':[b'Uid: 1001 1001 1001 1001\nGid: 1002 1002 1002 1002\nThreads: 5\n'],
   '/proc/41/cgroup':[b'0::/system.slice/'+unit.encode()+b'\n'],
   '/proc/7/cgroup':[b'0::/system.slice/'+unit.encode()+b'\n'],
   '/proc/41/cmdline':[b'/usr/share/dotnet/dotnet\0/sdk/dotnet.dll\0/private/secret/canary\0'],
   '/proc/7/cmdline':[b'/tmp/testhost.dll\0/private/secret/canary\0']}
  def bounded(path,limit,deadline):
   values=proc.get(str(path))
   if not values:return None
   return values.pop(0)
  with patch.object(native,'_bounded_proc_read',side_effect=bounded):
   facts=native.sample_process_tree(unit,identity,time.monotonic()+1)
  self.assertEqual(8,facts['total_threads'])
  self.assertEqual({'dotnet.dll':3,'testhost.dll':5},facts['by_role_threads'])
  self.assertEqual({41,7},{row['pid'] for row in facts['processes']})
  self.assertNotIn('private/secret',json.dumps(facts))

 def test_missing_unstable_or_foreign_process_facts_are_unavailable_not_acceptance(self):
  from unittest.mock import patch
  import time
  unit='appsurface-evidence-producer-test.service';identity={'pid':41,'uid':1001,'gid':1002,'cgroup':'/system.slice/'+unit}
  for label in ('missing','membership-churn','foreign-cgroup','unstable-stat','foreign-uid'):
   with self.subTest(label=label):
    reads={}
    def bounded(path,limit,deadline):
     name=str(path);reads[name]=reads.get(name,0)+1
     if name.endswith('cgroup.procs'):return b'41\n' if reads[name]==1 or label!='membership-churn' else b'42\n'
     if str(path).endswith('/stat'):
      start=b'889' if label=='unstable-stat' and reads[name]==2 else b'888'
      return b'41 (dotnet (worker)) S '+b' '.join([b'0']*18+[start])
     if name.endswith('/status'):
      if label=='missing':return None
      uid=b'2001' if label=='foreign-uid' else b'1001'
      return b'Uid: '+b' '.join([uid]*4)+b'\nGid: 1002 1002 1002 1002\nThreads: 2\n'
     if name.endswith('/cgroup'):
      group=b'0::/system.slice/other.service\n' if label=='foreign-cgroup' else b'0::/system.slice/'+unit.encode()+b'\n'
      return group
     if str(path).endswith('/cmdline'):return b'/sdk/dotnet.dll\0'
     return None
    with patch.object(native,'_bounded_proc_read',side_effect=bounded):
     self.assertIsNone(native.sample_process_tree(unit,identity,time.monotonic()+1))

 def test_recording_keeps_bounded_peak_snapshot_and_treats_sampling_failure_as_missing(self):
  from unittest.mock import patch
  samples={};unit='fixed.service';identity={'pid':1,'uid':1001,'gid':1002,'cgroup':'/system.slice/fixed.service'}
  with patch.object(native,'sample_process_tree',side_effect=[{'total_threads':5,'by_role_threads':{'dotnet.dll':3,'testhost.dll':2},'processes':[{'pid':1,'starttime_ticks':2,'threads':3,'role':'dotnet.dll'}]},None,{'total_threads':4,'by_role_threads':{'dotnet.dll':4},'processes':[]} ]):
   native._record_process_facts(unit,samples,identity,None)
   first=samples['process_facts']['peak_snapshot']
   native._record_process_facts(unit,samples,identity,None)
   self.assertFalse(samples['process_facts']['last_sample_available'])
   self.assertIsNone(samples['process_facts']['last_total_threads'])
   native._record_process_facts(unit,samples,identity,None)
  facts=samples['process_facts']
  self.assertEqual(5,facts['peak_total_threads']);self.assertEqual(first,facts['peak_snapshot'])
  self.assertEqual({'dotnet.dll':4,'testhost.dll':2},facts['peak_by_role_threads'])

class IgnoringDescendantSamplingControls(unittest.TestCase):
 def setUp(self):
  import os,tempfile
  self.os=os;self.tmp=tempfile.TemporaryDirectory();self.scratch=Path(self.tmp.name)
  self.uid=os.geteuid();self.gid=os.getegid();self.unit='appsurface-evidence-producer-test.service'
  self.producer={'pid':41001,'uid':self.uid,'gid':self.gid,'cgroup':'/system.slice/'+self.unit}

 def tearDown(self):self.tmp.cleanup()

 def marker(self,name,contents):
  path=self.scratch/name;path.write_bytes(contents);path.chmod(0o600);return path

 def complete_markers(self,pid=b'41002'):
  self.marker('checkpoint2-child-ready',b'SIGTERM handler installed')
  self.marker('checkpoint2-case-entered',b'ignored-cancel')
  self.marker('checkpoint2-ignored-child-pid',pid)

 def proc_facts(self,*,uid=None,gid=None,cgroup=None,starts=(7654,7654)):
  from unittest.mock import patch
  reads={}
  uid=self.uid if uid is None else uid;gid=self.gid if gid is None else gid
  status=(f'Uid:\t{uid} {uid} {uid} {uid}\nGid:\t{gid} {gid} {gid} {gid}\nThreads:\t3\n').encode()
  group=('0::/system.slice/'+self.unit+'\n' if cgroup is None else cgroup).encode()
  def bounded(path,limit,deadline):
   name=str(path);reads[name]=reads.get(name,0)+1
   if name.endswith('/stat'):
    start=starts[min(reads[name]-1,len(starts)-1)]
    return b'41002 (testhost (child)) S '+b' '.join([b'0']*18+[str(start).encode()])
   if name.endswith('/status'):return status
   if name.endswith('/cgroup'):return group
   return None
  return patch, bounded

 def test_valid_private_markers_and_stable_exact_proc_facts_are_only_observations(self):
  self.complete_markers()
  from unittest.mock import patch
  _,bounded=self.proc_facts()
  with patch.object(native,'_bounded_proc_read',side_effect=bounded):
   observation=native.sample_ignoring_descendant(self.unit,self.producer,self.scratch)
  self.assertEqual({'pid':41002,'uid':self.uid,'gid':self.gid,'starttime_ticks':7654,
   'cgroup':'/system.slice/'+self.unit},observation)
  self.assertIsNone(observation.get('accepted'))
  self.assertIsNone(observation.get('authority'))

 def test_missing_or_malformed_markers_and_zero_or_self_pid_never_sample_proc(self):
  from unittest.mock import patch
  self.marker('checkpoint2-child-ready',b'SIGTERM handler installed')
  self.marker('checkpoint2-case-entered',b'ignored-cancel')
  with patch.object(native,'_bounded_proc_read') as proc:
   self.assertIsNone(native.sample_ignoring_descendant(self.unit,self.producer,self.scratch))
   proc.assert_not_called()
  for value in (b'zero',b'0',str(self.producer['pid']).encode()):
   with self.subTest(pid_marker=value):
    self.marker('checkpoint2-ignored-child-pid',value)
    with patch.object(native,'_bounded_proc_read') as proc:
     self.assertIsNone(native.sample_ignoring_descendant(self.unit,self.producer,self.scratch))
     proc.assert_not_called()

 def test_subject_marker_reader_requires_single_link_private_regular_file_and_exact_owner(self):
  import os
  valid=self.marker('valid',b'ignored-cancel')
  self.assertEqual(b'ignored-cancel',native.read_subject_marker(valid,self.uid,self.gid,32))
  self.assertIsNone(native.read_subject_marker(valid,self.uid+1,self.gid,32))
  self.assertIsNone(native.read_subject_marker(valid,self.uid,self.gid+1,32))
  self.assertIsNone(native.read_subject_marker(valid,self.uid,self.gid,4))
  wrong_mode=self.marker('wrong-mode',b'ignored-cancel');wrong_mode.chmod(0o644)
  self.assertIsNone(native.read_subject_marker(wrong_mode,self.uid,self.gid,32))
  linked=self.marker('linked',b'ignored-cancel');os.link(linked,self.scratch/'linked-again')
  self.assertIsNone(native.read_subject_marker(linked,self.uid,self.gid,32))
  symlink=self.scratch/'symlink';symlink.symlink_to(valid)
  self.assertIsNone(native.read_subject_marker(symlink,self.uid,self.gid,32))

 def test_wrong_proc_uid_gid_cgroup_or_unstable_starttime_is_unavailable(self):
  from unittest.mock import patch
  cases=[('uid',self.uid+1,self.gid,None,(7654,7654)),
   ('gid',self.uid,self.gid+1,None,(7654,7654)),
   ('cgroup',self.uid,self.gid,'0::/system.slice/other.service\n',(7654,7654)),
   ('starttime',self.uid,self.gid,None,(7654,7655))]
  for label,uid,gid,cgroup,starts in cases:
   with self.subTest(label=label):
    self.complete_markers()
    _,bounded=self.proc_facts(uid=uid,gid=gid,cgroup=cgroup,starts=starts)
    with patch.object(native,'_bounded_proc_read',side_effect=bounded):
     self.assertIsNone(native.sample_ignoring_descendant(self.unit,self.producer,self.scratch))

class RetainedMutationObservationControls(unittest.TestCase):
 def events(self):
  return [{'schema':'evidence-c2-artifact-mutation-v1','phase':'mutated','original_sha256':'a'*64,'changed_sha256':'b'*64,'length':123,'device_major':1,'device_minor':2,'inode':3},
   {'schema':'evidence-c2-artifact-mutation-v1','phase':'read-rejected','returned_bytes':0}]
 def test_fixed_metadata_requires_actual_change_then_zero_byte_rejection(self):
  rows=self.events();self.assertEqual(rows,native.read_mutation_observation(b'non-json-private-canary\n'+b'\n'.join(json.dumps(row).encode() for row in rows)))
 def test_unknown_duplicate_incomplete_or_unchanged_events_never_substitute_for_the_control(self):
  import copy
  original=self.events();variants=[[],original[:1],original[::-1],original+original]
  for update in [{'length':0},{'inode':0},{'changed_sha256':'a'*64},{'raw_path':'private-canary'}]:
   rows=copy.deepcopy(original);rows[0].update(update);variants.append(rows)
  for returned in [1,False,None]:
   rows=copy.deepcopy(original);rows[1]['returned_bytes']=returned;variants.append(rows)
  for rows in variants:
   with self.subTest(rows=rows):
    with self.assertRaises(RuntimeError):native.read_mutation_observation(b'\n'.join(json.dumps(row).encode() for row in rows))

if __name__=='__main__':unittest.main()
