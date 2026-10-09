"""Detached cancellation archive binding; this grants no runtime capability.

The runner must separately verify the exact built image, reviewed root execution,
original command terminal, retained archive digest and canonical USTAR framing.
This module checks only the bytes passed to it and performs no filesystem I/O.
"""
import re

from cancellation_adapter import decode, digest, parse_original, require, uint


CAPS = {
    'signal.json': 1024,
    'kernel.json': 4097,
    'joined-streams.json': 96 * 1024,
    'root-failure.json': 1024,
    'worker.stdout': 0,
    'worker.stderr': 65536,
    'worker-control.json': 65536,
    'filesystem-nss-observation.json': 8192,
    'cancellation-capture.json': 8192,
    'logs/n01.stdout': 0,
    'logs/n01.stderr': 112 * 1024,
}


def exact(value, fields):
    require(type(value) is dict and set(value) == set(fields))


def check_archive(raw, *, case, source, base, entry_sha):
    """Return bounded detached consistency facts, preserving the failed run.

    ``raw`` contains already inspected canonical members. These data checks are
    insufficient to authenticate a root capture, signal or process identity.
    Account disposition must remain quarantined; no positive custody is claimed.
    """
    require(type(raw) is dict and case in ('N08', 'N09'))
    required = set(CAPS) | {'fixture-result.json'}
    require(required <= set(raw))
    for name, cap in CAPS.items():
        require(type(raw[name]) is bytes and len(raw[name]) <= cap)
    result = decode(raw['fixture-result.json'], 4096)
    exact(result, ('schema', 'generation', 'case', 'source_revision', 'base_revision',
                  'capture_sha256', 'root_launch_exit', 'fixture_processes_joined',
                  'fresh_generated_groups_empty', 'account_disposition',
                  'native_acceptance', 'trusted'))
    g = result['generation']
    require(type(g) is str and re.fullmatch('[0-9a-f]{32}', g) and g != '0' * 32)
    require(result['schema'] == 'issue779-cancellation-fixture-v1'
            and result['case'] == case and result['source_revision'] == source
            and result['base_revision'] == base and type(result['root_launch_exit']) is int
            and result['root_launch_exit'] == 1 and result['fixture_processes_joined'] is True
            and result['fresh_generated_groups_empty'] is True
            and result['account_disposition'] == 'preserved-quarantined'
            and result['native_acceptance'] is False and result['trusted'] is False
            and result['capture_sha256'] == digest(raw['cancellation-capture.json']))
    capture = decode(raw['cancellation-capture.json'], 8192)
    exact(capture, ('schema', 'case', 'generation', 'source', 'entry_sha256',
                    'policy_sha256', 'record_files', 'observation_only',
                    'native_authority', 'native_acceptance'))
    require(capture['schema'] == 'issue779-cancellation-private-capture-v1'
            and capture['case'] == case and capture['generation'] == g
            and capture['source'] == source and capture['entry_sha256'] == entry_sha
            and capture['observation_only'] is True and capture['native_authority'] is False
            and capture['native_acceptance'] is False)
    policy = capture['policy_sha256']
    require(type(policy) is str and re.fullmatch('[0-9a-f]{64}', policy))
    descriptor, frames = parse_original(raw['logs/n01.stderr'], raw['worker-control.json'],
        case=case, generation=g, source=source, base=base, entry_sha=entry_sha, policy_sha=policy)
    require(raw['logs/n01.stdout'] == b'')
    for name, content in frames.items():
        require(raw[name] == content)
    records = capture['record_files']
    exact(records, set(frames) | {'filesystem-nss-observation.json'})
    for name, row in records.items():
        exact(row, ('bytes', 'sha256'))
        uint(row['bytes'], 0, CAPS[name])
        require(row['bytes'] == len(raw[name]) and row['sha256'] == digest(raw[name]))
    observation = decode(raw['filesystem-nss-observation.json'], 8192)
    exact(observation, ('schema', 'generation', 'case', 'output_parent_descriptor_identity_match',
        'output_slot', 'manifest_present', 'artifact_files_present', 'account_disposition',
        'users', 'groups', 'original_worker_pid_absent', 'original_broker_pid_absent',
        'fresh_generated_worker_group', 'worker_fd_disposition', 'native_acceptance', 'trusted'))
    require(observation['schema'] == 'issue779-cancellation-filesystem-nss-v1'
            and observation['generation'] == g and observation['case'] == case
            and observation['output_parent_descriptor_identity_match'] is True
            and observation['manifest_present'] is False and observation['artifact_files_present'] is False
            and observation['account_disposition'] == 'preserved-quarantined'
            and observation['original_worker_pid_absent'] is True
            and observation['original_broker_pid_absent'] is True
            and observation['worker_fd_disposition'] == 'kernel-closed-on-original-monitored-normal-exit'
            and observation['native_acceptance'] is False and observation['trusted'] is False)
    slot = observation['output_slot']
    if case == 'N08':
        require(slot is None)
    else:
        exact(slot, ('device', 'inode', 'mode', 'empty'))
        uint(slot['device'], 0, (1 << 64) - 1)
        uint(slot['inode'], 1, (1 << 64) - 1)
        require(slot['mode'] == '0700' and slot['empty'] is True)
    group = observation['fresh_generated_worker_group']
    exact(group, ('exists', 'populated', 'frozen', 'inode'))
    require(type(group['exists']) is bool)
    if group['exists']:
        require(group['populated'] is False and group['frozen'] is False)
        uint(group['inode'], 1, (1 << 64) - 1)
    else:
        require(all(group[x] is None for x in ('populated', 'frozen', 'inode')))
    users, groups = observation['users'], observation['groups']
    require(type(users) is list and len(users) == 2 and type(groups) is list and len(groups) == 3)
    prefix = g[:28]
    for row, tag in zip(users, ('worker', 'subject')):
        exact(row, ('name', 'uid', 'gid', 'retained'))
        expected = 'evw' if tag == 'worker' else 'evs'
        require(row['name'] == expected + prefix and type(row['uid']) is int
                and type(row['gid']) is int and row['uid'] == descriptor[tag + '_uid']
                and row['gid'] == descriptor[tag + '_gid'] and row['retained'] is True)
    for row, prefix_name in zip(groups, ('evw', 'evs', 'evr')):
        exact(row, ('name', 'gid', 'retained'))
        uint(row['gid'], 1)
        require(row['name'] == prefix_name + prefix and row['retained'] is True)
    require(groups[0]['gid'] == descriptor['worker_gid']
            and groups[1]['gid'] == descriptor['subject_gid']
            and len({row['gid'] for row in groups}) == 3)
    return {'case': case, 'generation': g, 'source': source,
            'record_hashes_match': True, 'original_failed_exit': 1,
            'account_disposition': 'preserved-quarantined', 'native_authority': False,
            'native_acceptance': False}
