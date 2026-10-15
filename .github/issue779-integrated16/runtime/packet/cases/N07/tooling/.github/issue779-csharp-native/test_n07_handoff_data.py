"""Detached helper handoff data and synthetic output-bound controls.

The tests AST-extract actual candidate validators and helper run functions without
importing or executing the publisher, runner, or helper entrypoints. No SDK, .NET
unit, native execution, authority, or ownership evidence is involved.
"""
import ast
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import resource
import shlex
import signal
import subprocess
import sys
import tempfile
import time
import unittest


class HandoffData(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (Path(__file__).parent / 'run-native.py').read_text()
        tree = ast.parse(source)
        selected = [n for n in tree.body if isinstance(n, ast.FunctionDef)
                    and n.name in ('require', 'validate_n07_helper_handoff')]
        if len(selected) != 2:
            raise ValueError('actual-validator-definition-count')
        class Rejected(ValueError):
            pass
        namespace = {'re': re, 'Rejected': Rejected}
        exec(compile(ast.Module(body=selected, type_ignores=[]), '<actual-n07-data-validator>', 'exec'), namespace)
        cls.validate = staticmethod(namespace['validate_n07_helper_handoff'])
        assertion = next(n for n in ast.walk(selected[-1]) if isinstance(n, ast.Compare)
                         and isinstance(n.left, ast.Subscript)
                         and isinstance(n.left.slice, ast.Constant) and n.left.slice.value == 'source_pins')
        cls.pins = ast.literal_eval(assertion.comparators[0])
        cls.recipe = 'c' * 64

    def value(self):
        return dict(schema='issue779-n07-helper-build-handoff-v1', exit=0, authority=False,
                    native_execution=False, recipe_sha256=self.recipe, sdk_required='10.0.401',
                    runtime_required='10.0.12', sdk_sha256='a' * 64, source_pins=copy.deepcopy(self.pins),
                    helper_files=4, helper_directories=1, helper_bytes=100,
                    helper_tsv_sha256='b' * 64, helper_nodes_sha256='d' * 64,
                    helper_entry_sha256='e' * 64, helper_root='/detached/data-only/helper',
                    commands=[dict(error=False, exit=0, forced_cleanup=False, group_absent=True,
                                   log='build-%02d.log' % i, log_bytes=0, waited=True) for i in range(3)])

    def rejects(self, value):
        with self.assertRaises(ValueError):
            self.validate(value, self.recipe)

    def test_closed_valid_data_neighbor(self):
        self.validate(self.value(), self.recipe)

    def test_missing_source_input(self):
        value = self.value(); value['source_pins'].pop('N07CoordinatorData.cs'); self.rejects(value)

    def test_wrong_recipe(self):
        value = self.value(); value['recipe_sha256'] = 'f' * 64; self.rejects(value)

    def test_bool_not_exit_integer(self):
        value = self.value(); value['exit'] = False; self.rejects(value)

    def test_pending_or_wrong_schema(self):
        value = self.value(); value['schema'] = 'issue779-n04-helper-build-handoff-v1'; self.rejects(value)

    def test_nonzero_child(self):
        value = self.value(); value['commands'][1]['exit'] = 1; self.rejects(value)

    def test_unjoined_group(self):
        value = self.value(); value['commands'][1]['group_absent'] = False; self.rejects(value)

    def test_forced_cleanup(self):
        value = self.value(); value['commands'][1]['forced_cleanup'] = True; self.rejects(value)

    def test_missing_reap(self):
        value = self.value(); value['commands'][1]['waited'] = False; self.rejects(value)

    def test_log_bound(self):
        value = self.value(); value['commands'][1]['log_bytes'] = 8388609; self.rejects(value)

    def test_file_count_bool_and_node_bound(self):
        value = self.value(); value['helper_files'] = True; self.rejects(value)
        value = self.value(); value['helper_files'] = 8192; self.rejects(value)

    def test_no_authority_or_unknown_key(self):
        value = self.value(); value['authority'] = True; self.rejects(value)
        value = self.value(); value['native_acceptance'] = True; self.rejects(value)


HELPERS = {
    "N03": Path(__file__).with_name("build-helper.sh"),
    "N07": Path(__file__).with_name("build-n07-helper.sh"),
}
TEST_LIMIT = 4096

def embedded_helper(path):
    source = path.read_text(encoding="utf-8")
    return source.split("<<'PY'\n", 1)[1].rsplit("\nPY", 1)[0]

def actual_runner_functions(source):
    tree = ast.parse(source)
    selected = [node for node in tree.body if isinstance(node, ast.FunctionDef)
                and node.name in ("limit_build_log", "absent", "run")]
    if {node.name for node in selected} != {"limit_build_log", "absent", "run"}:
        raise ValueError("candidate-helper-functions-missing")
    runner = next(node for node in selected if node.name == "run")
    popen = [node for node in ast.walk(runner) if isinstance(node, ast.Call)
             and isinstance(node.func, ast.Attribute) and node.func.attr == "Popen"]
    if len(popen) != 1:
        raise ValueError("candidate-popen-count")
    keywords = {item.arg: item.value for item in popen[0].keywords}
    if not (isinstance(keywords.get("preexec_fn"), ast.Name)
            and keywords["preexec_fn"].id == "limit_build_log"
            and isinstance(keywords.get("stderr"), ast.Attribute)
            and keywords["stderr"].attr == "STDOUT"):
        raise ValueError("candidate-hard-limit-or-combined-log-not-wired")
    if "log.stat().st_size<MAX_LOG_BYTES" not in source:
        raise ValueError("candidate-full-log-not-rejected-during-poll")
    namespace = {"resource": resource, "MAX_LOG_BYTES": TEST_LIMIT}
    exec(compile(ast.Module(body=selected, type_ignores=[]), str(HELPERS["N03"]), "exec"), namespace)
    return namespace

class BoundedNoisyChildren(unittest.TestCase):
    def exercise(self, path):
        functions = actual_runner_functions(embedded_helper(path))
        old_limit = resource.getrlimit(resource.RLIMIT_FSIZE)
        hard = old_limit[1]
        self.assertTrue(hard == resource.RLIM_INFINITY or hard >= TEST_LIMIT)
        code = ("import os; os.write(1,b'O'*1024); os.write(2,b'E'*1024); "
                "data=b'N'*200000\nwhile data:\n n=os.write(1,data)\n data=data[n:]")
        child_command = shlex.join([sys.executable, "-c", code])
        descendant_script = child_command + "\nstatus=$?; exit \"$status\""
        with tempfile.TemporaryDirectory(prefix="n07-log-control-", dir=Path(__file__).parent) as temp:
            out = Path(temp)
            (out / "project").mkdir()
            records = []
            functions.update({
                "OUT": out, "RECORDS": records, "FINAL": 10_000_000,
                "now": lambda: 0, "check": lambda final=False: None,
                "read": lambda item, cap: item.read_bytes(),
                "os": os, "pathlib": __import__("pathlib"),
                "subprocess": subprocess, "signal": signal, "time": time, "re": re,
            })
            start = time.monotonic()
            with self.assertRaisesRegex(ValueError, "build-command-rejected"):
                functions["run"](["/bin/bash", "-c", descendant_script])
            elapsed = time.monotonic() - start
            self.assertLess(elapsed, 5.0)
            self.assertEqual(1, len(records))
            record = records[0]
            self.assertNotEqual(0, record["exit"])
            self.assertTrue(record["waited"])
            self.assertIs(record["group_absent"], True)
            self.assertTrue(record["error"])
            log = out / record["log"]
            payload = log.read_bytes()
            self.assertEqual(TEST_LIMIT, record["log_bytes"])
            self.assertEqual(TEST_LIMIT, len(payload))
            self.assertLessEqual(len(payload), TEST_LIMIT)
            self.assertEqual(b"O" * 1024 + b"E" * 1024, payload[:2048])
            self.assertEqual(b"N" * (TEST_LIMIT - 2048), payload[2048:])
            self.assertTrue(record["forced_cleanup"])
            self.assertEqual(old_limit, resource.getrlimit(resource.RLIMIT_FSIZE))
            print("CONTROL " + json.dumps({"helper": path.name, "child_exit": record["exit"],
                "waited": record["waited"], "group_absent": record["group_absent"],
                "forced_cleanup": record["forced_cleanup"], "log_bytes": record["log_bytes"],
                "test_limit_bytes": TEST_LIMIT, "elapsed_seconds": round(elapsed, 4)}), flush=True)

    def test_n03_noisy_descendant_cannot_overshoot_log_file(self):
        self.exercise(HELPERS["N03"])

    def test_n07_noisy_descendant_cannot_overshoot_log_file(self):
        self.exercise(HELPERS["N07"])


class N03ArchiveValidationControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (Path(__file__).parent / 'run-native.py').read_text()
        tree = ast.parse(source)
        definitions = [node for node in tree.body if isinstance(node, ast.FunctionDef)
                       and node.name in ('require', 'require_n03_result', 'validate_n03_archive',
                                         'validate_successful_archive')]
        if {node.name for node in definitions} != {
                'require', 'require_n03_result', 'validate_n03_archive', 'validate_successful_archive'}:
            raise ValueError('actual-n03-validator-definition-set')
        assignments = {node.targets[0].id: node.value for node in tree.body
                       if isinstance(node, ast.Assign) and len(node.targets) == 1
                       and isinstance(node.targets[0], ast.Name)}
        class Rejected(ValueError):
            pass
        namespace = {'json': json, 're': re, 'sha': lambda data: hashlib.sha256(data).hexdigest(),
                     'Rejected': Rejected, 'KEYS': ast.literal_eval(assignments['KEYS']),
                     'SOURCE': ast.literal_eval(assignments['SOURCE']),
                     'N03_REQUIRED_ARCHIVE_NAMES': frozenset(
                         ast.literal_eval(assignments['N03_REQUIRED_ARCHIVE_NAMES'].args[0]))}
        exec(compile(ast.Module(body=definitions, type_ignores=[]), '<actual-n03-archive-validator>', 'exec'),
             namespace)
        cls.validate_success = staticmethod(namespace['validate_successful_archive'])
        cls.Rejected = Rejected


    @classmethod
    def actual_fixture_writer_lines(cls):
        shell = (Path(__file__).parent / 'checkpoint-n01-n02-v5.sh').read_text()
        lines = shell.splitlines()
        assignments = [line for line in lines
                       if line.startswith('n03_result_sha256=$(bounded sha256sum "$log/n03/result.json")')]
        cleanup_starts = [i for i, line in enumerate(lines) if line == 'cleanup() {']
        if len(cleanup_starts) != 1:
            raise ValueError('actual-fixture-cleanup-count')
        cleanup_start = cleanup_starts[0]
        trap_lines = [i for i, line in enumerate(lines)
                      if line == "trap cleanup EXIT; trap 'exit 1' INT TERM"]
        if len(trap_lines) != 1 or trap_lines[0] <= cleanup_start + 1 or lines[trap_lines[0] - 1] != '}':
            raise ValueError('actual-fixture-cleanup-end')
        cleanup_end = trap_lines[0] - 1
        cleanup_lines = lines[cleanup_start:trap_lines[0]]
        writers = [line for line in cleanup_lines
                   if 'try_bounded jq -jcS -n --arg g "$G"' in line
                   and 'n03_result_sha256 "$n03_result_sha256"' in line
                   and 'fixture-result.pending' in line]
        if len(assignments) != 1 or len(writers) != 1:
            raise ValueError('actual-fixture-result-writer-count')
        if writers[0].count('n03_result_sha256 "$n03_result_sha256"') != 1:
            raise ValueError('actual-fixture-result-writer-argument-count')
        run_lines = [i for i, line in enumerate(lines)
                     if line.startswith('n03_run "$n03_helper_root"')]
        assignment_index = lines.index(assignments[0])
        if len(trap_lines) != 1 or len(run_lines) != 1 or not (
                cleanup_end < trap_lines[0] < run_lines[0] < assignment_index):
            raise ValueError('actual-fixture-digest-cleanup-order')
        return assignments[0], writers[0]

    @classmethod
    def actual_fixture_result(cls, result_raw, sha256sum_digest_override=None):
        assignment, writer = cls.actual_fixture_writer_lines()
        with tempfile.TemporaryDirectory(prefix='n03-fixture-runtime-') as temp:
            base = Path(temp)
            log = base / 'log'
            (log / 'n03').mkdir(parents=True)
            (log / 'n03/result.json').write_bytes(result_raw)
            private = base / 'private'
            private.mkdir()
            override_enabled = '1' if sha256sum_digest_override is not None else '0'
            override_digest = '' if sha256sum_digest_override is None else sha256sum_digest_override
            script = ('set -euo pipefail\n'
                      'sha256sum_override_enabled=' + override_enabled + '\n'
                      'sha256sum_override_digest=' + shlex.quote(override_digest) + '\n'
                      'bounded() {\n'
                      '  if [[ "$1" == sha256sum && "$2" == "$log/n03/result.json" && "$sha256sum_override_enabled" == 1 ]]; then\n'
                      '    if [[ -n "$sha256sum_override_digest" ]]; then\n'
                      '      printf "%s  %s\\n" "$sha256sum_override_digest" "$2"\n'
                      '    fi\n'
                      '    return 0\n'
                      '  fi\n'
                      '  command "$@"\n'
                      '}\n'
                      'try_bounded() {\n'
                      '  [[ "$1" == jq ]] || return 127\n'
                      '  shift\n'
                      '  /usr/bin/jq "$@"\n'
                      '}\n'
                      'G=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n'
                      'source_revision=5b4ed381badb91de47d793715d05b6ae7737e39e\n'
                      'base_revision=4dd992ec1bc2df8220c73149115c5b478edb0085\n'
                      'NEGATIVE_CASE=N07\nnegative_launch=1\n'
                      'policy_sha=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n'
                      'log=' + shlex.quote(str(log)) + '\n'
                      'private=' + shlex.quote(str(private)) + '\n'
                      'n03_result_sha256=\n' + assignment + '\n')
            script += ('status=0\n' + writer + '\n' +
                       'test "$status" -eq 0\n')
            subprocess.run(['/bin/bash', '-c', script], check=True,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           timeout=2, env={'PATH': '/sbin:/usr/bin:/bin', 'LC_ALL': 'C'})
            return json.loads((private / 'fixture-result.pending').read_bytes())

    @staticmethod
    def data():
        source = '03ff51b361d686e6e2590a27e9a4f43529cdbaf2'
        worker_pid, broker_pid = 4121, 4122
        worker_uid, broker_uid, gid = 65532, 65533, 65534
        cgroup = '0::/system.slice/issue779-native-tool-' + 'a' * 32 + '-1.service'
        def sample(pid, uid, ticks):
            return dict(schema='issue779-n03-owned-image-sample-v1', pid=pid, start_ticks=str(ticks),
                        uid4=':'.join([str(uid)] * 4), gid4=':'.join([str(gid)] * 4), cgroup=cgroup,
                        exact_managed_argv=True, image_pinned=True, empty_groups=True, no_new_privs=1,
                        effective_capabilities_zero=True)
        result = dict(schema='issue779-n03-peer-observation-v1', control='N03', status='observed',
                      source_commit=source, worker_exit=1, broker_exit=0, worker_pid=worker_pid,
                      broker_pid=broker_pid, worker_stdout_bytes=0, asevd402_observed=True,
                      kernel_peer_bound_to_owned_worker=True, request_bytes=0, sent_bytes=0,
                      ready_request_observed=False, descriptor_supplied=False,
                      owned_process_groups_joined=True, native_acceptance=False,
                      systemd_worker_authority=False,
                      stream_custody='private-files-after-original-process-reap')
        broker = sample(broker_pid, broker_uid, 202)
        worker = sample(worker_pid, worker_uid, 201)
        events = [
            dict(schema='issue779-n03-managed-broker-event-v1', stage='ready', broker_pid=broker_pid,
                 broker_uid=broker_uid, broker_gid=gid, broker_start_ticks=202),
            dict(schema='issue779-n03-managed-broker-event-v1', stage='peer',
                 identity_kind='kernel-so-peercred-sample', peer_pid=worker_pid,
                 peer_uid=worker_uid, peer_gid=gid),
            dict(schema='issue779-n03-managed-broker-event-v1', stage='eof', peer_pid=worker_pid,
                 request_bytes=0, sent_bytes=0),
            dict(schema='issue779-n03-managed-broker-result-v1', status='observed', peer_pid=worker_pid,
                 request_bytes=0, sent_bytes=0, native_acceptance=False),
        ]
        event_raw = b''.join(json.dumps(event, separators=(',', ':')).encode('ascii') + b'\n'
                             for event in events)
        result_raw = json.dumps(result, separators=(',', ':')).encode('ascii') + b'\n'
        raw = {'logs/n03/result.json': result_raw, 'logs/n03/broker.events.jsonl': event_raw,
               'logs/n03/worker-peer.trace': (
                   'getsockopt(3, SOL_SOCKET, SO_PEERCRED, {pid=%d, uid=%d, gid=%d}, [12]) = 0\n'
                   % (broker_pid, broker_uid, gid)).encode('ascii'),
               'logs/n03/worker-live.json': json.dumps(worker).encode('ascii'),
               'logs/n03/broker-live.json': json.dumps(broker).encode('ascii'),
               'logs/n03/broker-live-after.json': json.dumps(broker).encode('ascii'),
               'logs/n03/worker.stdout': b'', 'logs/n03/worker.stderr': b'ASEVD402: observed\n',
               'logs/n03/broker.stderr': b''}
        fixture = N03ArchiveValidationControls.actual_fixture_result(result_raw)
        return {'fixture-result.json': fixture,
                'logs/n03/result.json': result,
                'logs/n03/worker-live.json': worker, 'logs/n03/broker-live.json': broker,
                'logs/n03/broker-live-after.json': copy.deepcopy(broker),
                'n03-result-sha256': hashlib.sha256(result_raw).hexdigest(), '_retained_raw': raw}

    class ArchiveValidator:
        def __init__(self, reject=False):
            self.calls = 0
            self.reject = reject

        def validate_n07_archive(self, values, **kwargs):
            self.calls += 1
            if self.reject:
                raise ValueError('n07-archive-rejected')
            return {'checked': True}

    def run_success(self, values, validator=None):
        validator = validator or self.ArchiveValidator()
        result = self.validate_success(values, 0, validator, object(), 'source', 'base')
        return result, validator

    def test_actual_writer_digest_reaches_actual_n03_consumer(self):
        values = self.data()
        result_raw = values['_retained_raw']['logs/n03/result.json']
        digest = hashlib.sha256(result_raw).hexdigest()
        self.assertEqual(digest, values['fixture-result.json']['n03_result_sha256'])
        result, validator = self.run_success(values)
        self.assertEqual(result, {'checked': True})
        self.assertEqual(validator.calls, 1)

    def test_actual_shell_assignment_wrong_digest_blocks_success(self):
        values = self.data()
        result_raw = values['_retained_raw']['logs/n03/result.json']
        values['fixture-result.json'] = self.actual_fixture_result(result_raw, '0' * 64)
        self.assertEqual('0' * 64, values['fixture-result.json']['n03_result_sha256'])
        validator = self.ArchiveValidator()
        with self.assertRaises(self.Rejected):
            self.validate_success(values, 0, validator, object(), 'source', 'base')
        self.assertEqual(validator.calls, 0)

    def test_actual_shell_writer_rejects_empty_digest_assignment(self):
        values = self.data()
        result_raw = values['_retained_raw']['logs/n03/result.json']
        values['fixture-result.json'] = self.actual_fixture_result(result_raw, '')
        self.assertEqual('', values['fixture-result.json']['n03_result_sha256'])
        validator = self.ArchiveValidator()
        with self.assertRaises(self.Rejected):
            self.validate_success(values, 0, validator, object(), 'source', 'base')
        self.assertEqual(validator.calls, 0)

    def test_valid_archive_calls_existing_n07_validator(self):
        result, validator = self.run_success(self.data())
        self.assertEqual(result, {'checked': True})
        self.assertEqual(validator.calls, 1)

    def test_missing_peer_trace_blocks_success(self):
        values = self.data(); del values['_retained_raw']['logs/n03/worker-peer.trace']
        validator = self.ArchiveValidator()
        with self.assertRaises(self.Rejected):
            self.validate_success(values, 0, validator, object(), 'source', 'base')
        self.assertEqual(validator.calls, 0)

    def test_missing_broker_events_blocks_success(self):
        values = self.data(); del values['_retained_raw']['logs/n03/broker.events.jsonl']
        validator = self.ArchiveValidator()
        with self.assertRaises(self.Rejected):
            self.validate_success(values, 0, validator, object(), 'source', 'base')
        self.assertEqual(validator.calls, 0)

    def test_n03_event_rejection_blocks_success_before_n07_validator(self):
        values = self.data()
        values['_retained_raw']['logs/n03/broker.events.jsonl'] = values['_retained_raw'][
            'logs/n03/broker.events.jsonl'].replace(b'"sent_bytes":0', b'"sent_bytes":1', 1)
        validator = self.ArchiveValidator()
        with self.assertRaises(self.Rejected):
            self.validate_success(values, 0, validator, object(), 'source', 'base')
        self.assertEqual(validator.calls, 0)

    def test_n07_validator_rejection_blocks_success(self):
        validator = self.ArchiveValidator(reject=True)
        with self.assertRaisesRegex(ValueError, 'n07-archive-rejected'):
            self.validate_success(self.data(), 0, validator, object(), 'source', 'base')
        self.assertEqual(validator.calls, 1)


if __name__ == '__main__':
    unittest.main()


if __name__ == '__main__':
    unittest.main()
