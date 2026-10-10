"""Detached N07 archive consistency only; authenticated custody remains external.

Load only after the reviewed caller verifies this module and n07-record-data.py.
No decoded record creates a lease, admission, account owner, or native acceptance.
"""
import re

RECORD_SHA256 = '96d01d088300aeffb5fa7eab7387c3b4be319d249e796edcb189aceb441548e7'
CAPS = {
    'n07-worker-unit.txt': 65536, 'n07-helper-unit.txt': 65536,
    'n07-worker-raw.json': 98304, 'n07-root-failure.json': 6145,
    'n07-ordinary-failure.json': 1025, 'n07-root-terminal.txt': 1024,
    'n07-descriptor.json': 65536, 'n07-account-ids.tsv': 128,
    'n07-worker.stdout': 0, 'n07-worker.stderr': 65536,
    'n07-precleanup-frame.json': 1025, 'n07-helper-result.json': 4096,
    'n07-helper-live.json': 2048, 'n07-parent-postjoin.json': 4096,
    'n07-consistency.json': 4096, 'logs/n07-helper.stdout': 4096,
    'logs/n07-helper.stderr': 8388608, 'logs/n01.stderr': 114688,
    'logs/n01.stdout': 0,
}


def reject():
    raise ValueError('n07-detached-archive-rejected')


def require(value):
    if not value:
        reject()


def validate_n07_archive(values, *, records, expected_source, expected_base):
    """Check complete fixed raw transfers against the actual original-holder packet.

    ``records`` is the externally SHA-pinned original N07 data module, supplied
    by the source-bound runner. All expected values are detached data, never
    authority. Missing records, earlier guards, and incomplete settlement fail.
    Worker terminal code/status and TaskStatus are preserved by check_records;
    no normal exit is manufactured from a signal or a faulted monitor.
    """
    raw = values['_retained_raw']
    require(type(raw) is dict and all(name in raw for name in CAPS))
    require(all(type(raw[n]) is bytes and len(raw[n]) <= cap for n, cap in CAPS.items()))
    fixture = values['fixture-result.json']
    keys = {'schema', 'generation', 'source_revision', 'base_revision', 'control',
            'root_launch_join_status', 'root_stdout_bytes', 'worker_terminal_from_original_monitor',
            'original_csharp_joins_required', 'account_disposition', 'native_acceptance',
            'observation_only', 'policy_sha256', 'other_controls'}
    require(type(fixture) is dict and set(fixture) == keys)
    require(fixture['schema'] == 'issue779-n07-parent-substitution-fixture-v1'
            and fixture['control'] == 'N07' and fixture['source_revision'] == expected_source
            and fixture['base_revision'] == expected_base
            and type(fixture['root_launch_join_status']) is int
            and fixture['root_launch_join_status'] == 1
            and type(fixture['root_stdout_bytes']) is int and fixture['root_stdout_bytes'] == 0
            and fixture['worker_terminal_from_original_monitor'] is True
            and fixture['original_csharp_joins_required'] is True
            and fixture['account_disposition'] == 'preserved-quarantined'
            and fixture['native_acceptance'] is False and fixture['observation_only'] is True
            and fixture['other_controls'] == 'not-qualified-by-this-fixture')
    generation = fixture['generation']
    require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation) is not None
            and generation != '0' * 32)
    policy = fixture['policy_sha256']
    require(type(policy) is str and re.fullmatch('[0-9a-f]{64}', policy) is not None)
    hashes = raw['request-policy.sha256'].splitlines()
    require(len(hashes) == 2 and all(re.fullmatch(rb'[0-9a-f]{64}  /[^\r\n\x00]+', row) for row in hashes))
    request_hash = hashes[0][:64].decode('ascii')
    require(hashes[1][:64].decode('ascii') == policy)
    helper_unit = 'appsurface-evidence-n07-coordinator-' + generation + '.service'
    result, lines, stdout, stderr, frame, helper, descriptor = records.check_records(
        raw['logs/n01.stderr'], raw['n07-helper-result.json'], raw['n07-helper-live.json'],
        raw['n07-descriptor.json'], generation=generation, request_sha256=request_hash,
        helper_unit=helper_unit, expected_policy_sha256=policy, expected_source=expected_source)
    require(raw['logs/n01.stdout'] == raw['n07-worker.stdout'] == stdout == b'')
    require(raw['logs/n07-helper.stderr'] == b''
            and raw['logs/n07-helper.stdout'] == raw['n07-helper-result.json'])
    require(all(raw[name] == value for name, value in zip(
        ('n07-worker-raw.json', 'n07-root-failure.json', 'n07-ordinary-failure.json', 'n07-root-terminal.txt'), lines)))
    require(raw['n07-worker.stderr'] == stderr and raw['n07-precleanup-frame.json'] == frame)
    require(records.decode(raw['n07-consistency.json'], 4096) == result)
    post = records.decode(raw['n07-parent-postjoin.json'], 4096)
    require(type(post) is dict and set(post) == {'original_parent', 'replacement_parent', 'both_empty', 'native_authority'}
            and post['original_parent'] == helper['original_parent']
            and post['replacement_parent'] == helper['replacement_parent']
            and post['both_empty'] is True and post['native_authority'] is False)
    ids = raw['n07-account-ids.tsv']
    require(re.fullmatch(rb'[1-9][0-9]*\t[1-9][0-9]*\t[1-9][0-9]*\t[1-9][0-9]*\t[1-9][0-9]*\n', ids) is not None)
    numbers = [int(x) for x in ids[:-1].split(b'\t')]
    require(all(0 < x < 4294967295 for x in numbers)
            and numbers[:4] == [descriptor[k] for k in ('worker_uid', 'worker_gid', 'subject_uid', 'subject_gid')])
    settlement = records.decode(lines[1], 6145)['failure_settlement']
    terminal = settlement['terminal']
    records.selected_unit(raw['n07-worker-unit.txt'], descriptor['unit'], terminal)
    live = records.decode(raw['n07-helper-live.json'], 2048)
    records.selected_unit(raw['n07-helper-unit.txt'], helper_unit,
                          {'exec_main_pid': live['pid'], 'exec_main_code': 1, 'exec_main_status': 0})
    records.validate_failure(records.decode(lines[2], 1025))
    return result
