"""Detached N04 consistency adapter after the runner's canonical USTAR inspection.

No function authenticates an actor, reads proc/NSS, grants authority or accepts a native case.
The fixed sibling parser is loaded from captured SHA-matching bytes for DATA use only.
Its checksum is continuity data, not pre-execution OS/root trust. Privileged consumers
must establish the reviewed tool/image/custody prerequisites separately before interpretation.
"""
import hashlib
import os
from pathlib import Path
import re
import stat
import types

SOURCE = '5b5a2af696741203b1cb5ce200182452b5f1fcb7'
BASE = '4dd992ec1bc2df8220c73149115c5b478edb0085'
PARSER_SHA256 = 'eba48c40d6f35001ba30fa7cf733d87dea0a12e23e9c55f11675a6e514f88784'
REJECTION = 'N04 canonical archive data rejected.'

def _require(value):
    if not value:
        raise ValueError(REJECTION)

def _load_fixed_parser():
    path = Path(__file__).with_name('n04-record-data.py')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        _require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_size <= 65536)
        raw = b''
        while len(raw) <= 65536:
            block = os.read(fd, min(65536, 65537 - len(raw)))
            if not block:
                break
            raw += block
        after = os.fstat(fd)
        named = os.lstat(path)
        identity = lambda s: (s.st_dev, s.st_ino, s.st_mode, s.st_nlink, s.st_size, s.st_mtime_ns, s.st_ctime_ns)
        _require(len(raw) == before.st_size and len(raw) <= 65536
                 and identity(before) == identity(after) == identity(named)
                 and hashlib.sha256(raw).hexdigest() == PARSER_SHA256)
    finally:
        os.close(fd)
    module = types.ModuleType('n04_fixed_record_data')
    module.__file__ = str(path)
    exec(compile(raw, str(path), 'exec'), module.__dict__)
    return module

record_data = _load_fixed_parser()

# Exact 13 selected names and caps from the frozen root retainer/fixture contract.
CAPS = {
    'fixture-result.json': 4096,
    'logs/n01.stderr': 6144,
    'logs/n01.stdout': 0,
    'logs/n04-helper.stderr': 0,
    'logs/n04-helper.stdout': 4096,
    'n04-account-ids.tsv': 128,
    'n04-consistency.json': 2048,
    'n04-descriptor.json': 65536,
    'n04-helper-live.json': 2048,
    'n04-helper-result.json': 4096,
    'n04-kernel-observation.json': 4097,
    'n04-root-failure.json': 1024,
    'n04-root-terminal.txt': 1024,
}
PARSED_NAMES = (
    'fixture-result.json', 'n04-consistency.json', 'n04-descriptor.json',
    'n04-helper-live.json', 'n04-helper-result.json', 'n04-kernel-observation.json',
    'n04-root-failure.json',
)
FIXTURE_KEYS = frozenset((
    'schema', 'generation', 'source_revision', 'base_revision', 'control',
    'root_launch_join_status', 'helper_launch_join_status', 'root_stdout_bytes',
    'account_disposition', 'native_acceptance', 'observation_only', 'policy_sha256',
    'independent_raw_worker_capture', 'other_controls',
))

def _digest(value):
    return type(value) is str and re.fullmatch('[0-9a-f]{64}', value) is not None

def _same(left, right):
    """Strict detached equality: JSON booleans cannot masquerade as integer identities."""
    if type(left) is not type(right):
        return False
    if type(left) is dict:
        return set(left) == set(right) and all(_same(left[k], right[k]) for k in left)
    if type(left) is list:
        return len(left) == len(right) and all(_same(a, b) for a, b in zip(left, right))
    return left == right

def _validate(values, expected_source, expected_base):
    _require(type(expected_source) is str and re.fullmatch('[0-9a-f]{40}', expected_source) is not None)
    _require(type(expected_base) is str and re.fullmatch('[0-9a-f]{40}', expected_base) is not None)
    _require(type(values) is dict and '_retained_raw' in values
             and all(type(k) is str for k in values))
    _require(not any(k.startswith(('negative-', 'n05-', 'n06-')) for k in values))
    raw = values['_retained_raw']
    _require(type(raw) is dict and len(raw) <= 4096)
    _require(all(type(k) is str and type(v) is bytes for k, v in raw.items()))
    _require(sum(len(v) for v in raw.values()) <= 33554432)
    _require(set(CAPS) <= set(raw))
    _require(not any(k.startswith(('negative-', 'n05-', 'n06-')) for k in raw))
    # Cap every required raw member BEFORE any decoding. Other canonical members belong to
    # the runner's existing retainer whitelist; this function does not issue canonical custody.
    _require(all(len(raw[name]) <= cap for name, cap in CAPS.items()))
    decoded = {name: record_data.decode_data(raw[name]) for name in PARSED_NAMES}
    _require(all(name in values and _same(values[name], decoded[name]) for name in PARSED_NAMES))
    result = decoded['fixture-result.json']
    _require(type(result) is dict and set(result) == FIXTURE_KEYS)
    _require(result['schema'] == 'issue779-n04-peer-fixture-v1'
             and result['source_revision'] == expected_source and result['base_revision'] == expected_base
             and result['control'] == 'N04' and result['account_disposition'] == 'preserved-quarantined'
             and result['native_acceptance'] is False and result['observation_only'] is True
             and result['independent_raw_worker_capture'] is False
             and result['other_controls'] == 'not-qualified-by-this-fixture')
    _require(type(result['generation']) is str
             and re.fullmatch('[0-9a-f]{32}', result['generation']) is not None
             and result['generation'] != '0' * 32 and _digest(result['policy_sha256']))
    for key, expected in (('root_launch_join_status', 1), ('helper_launch_join_status', 0),
                          ('root_stdout_bytes', 0)):
        _require(type(result[key]) is int and result[key] == expected)
    _require(raw['logs/n01.stdout'] == raw['logs/n04-helper.stderr'] == b'')
    _require(raw['logs/n01.stderr'] == b''.join(raw[n] for n in
        ('n04-kernel-observation.json', 'n04-root-failure.json', 'n04-root-terminal.txt')))
    _require(raw['n04-helper-result.json'] == raw['logs/n04-helper.stdout'])
    descriptor = decoded['n04-descriptor.json']
    _require(type(descriptor) is dict and _digest(descriptor.get('policy_sha256'))
             and descriptor['policy_sha256'] == result['policy_sha256'])
    consistency = decoded['n04-consistency.json']
    _require(type(consistency) is dict and _digest(consistency.get('request_sha256')))
    # Request bytes are NOT retained. This digest is detached consistency plus authenticated
    # fixture provenance. request-policy.sha256 is deliberately not treated as request bytes/SHA.
    replay = record_data.check_n04_records(
        raw['logs/n01.stderr'], raw['logs/n04-helper.stdout'], raw['logs/n04-helper.stderr'],
        raw['n04-helper-live.json'], raw['n04-descriptor.json'],
        generation=result['generation'], request_sha256=consistency['request_sha256'],
        expected_helper_unit='appsurface-evidence-n04-coordinator-' + result['generation'] + '.service')
    _require(_same(consistency, replay))
    row = raw['n04-account-ids.tsv']
    _require(re.fullmatch(rb'[1-9][0-9]*\t[1-9][0-9]*\t[1-9][0-9]*\t[1-9][0-9]*\t[1-9][0-9]*\n', row) is not None)
    ids = [int(part) for part in row[:-1].split(b'\t')]
    _require(all(0 < value < 4294967295 for value in ids)
             and ids[:4] == [descriptor[k] for k in ('worker_uid', 'worker_gid', 'subject_uid', 'subject_gid')]
             and ids[0] != ids[2] and len(set((ids[1], ids[3], ids[4]))) == 3)
    # Strict descriptor integer types are checked by the fixed parser before this equality.
    return {
        'schema': 'issue779-n04-canonical-adapter-summary-v1', 'control': 'N04',
        'source_revision': expected_source, 'base_revision': expected_base,
        'generation': result['generation'], 'root_launch_join_status': 1,
        'helper_launch_join_status': 0, 'selected_members': len(CAPS),
        'descriptor_sha256': replay['descriptor_sha256'], 'policy_sha256': result['policy_sha256'],
        'account_disposition': 'preserved-quarantined',
        'request_digest_basis': 'detached-consistency-and-external-fixture-provenance',
        'independent_raw_worker_capture': False, 'observation_only': True,
        'native_authority': False, 'native_acceptance': False,
    }

def validate_n04_archive(values, expected_source=SOURCE, expected_base=BASE):
    """Validate source-bound detached consistency; canonical inspection/provenance remains external.

    All malformed/missing/type/decoder failures become one fixed rejection without input echo.
    The returned summary is DATA, not actual NSS, request-byte, syscall, peer or custody proof.
    """
    try:
        return _validate(values, expected_source, expected_base)
    except Exception:
        raise ValueError(REJECTION) from None
